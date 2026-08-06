using Docovee.DS.Models;

namespace nuvidoc.Flow;

/// <summary>
/// Client-side patient journey matching Flow_Instructions.md.md
/// (same OpenAI / Figma flow). Calling / SMS / email backends are simulated until APIs exist.
/// </summary>
public sealed class PatientFlowEngine
{
    private readonly PatientFlowState _state;

    public PatientFlowEngine(PatientFlowState state) => _state = state;

    public PatientFlowState State => _state;

    public PatientFlowPrompt Start(string? initialConcern = null)
    {
        if (!string.IsNullOrWhiteSpace(initialConcern))
            _state.Concern = initialConcern.Trim();

        _state.Stage = _state.IsSignedIn
            ? PatientFlowStage.ExistingWelcome
            : PatientFlowStage.GuestWelcome;

        return BuildPromptForCurrentStage(isEntry: true);
    }

    public PatientFlowPrompt Submit(string answer)
    {
        var text = (answer ?? "").Trim();
        if (string.IsNullOrEmpty(text) && _state.Stage != PatientFlowStage.DoctorMatching)
            return BuildPromptForCurrentStage(reprompt: "Please choose an option or type a short answer.");

        ApplyAnswer(text);
        return BuildPromptForCurrentStage();
    }

    public void SetDoctors(IReadOnlyList<DoctorDto> doctors)
    {
        _state.RankedDoctors = doctors.ToList();
    }

    public void MarkAccountCreated()
    {
        _state.AccountCreated = true;
        _state.NeedsAccount = false;
        _state.IsSignedIn = true;
        if (_state.Stage == PatientFlowStage.AccountPermission)
            _state.Stage = PatientFlowStage.AppointmentDays;
    }

    public void GoToDoctorSelection()
    {
        _state.Stage = PatientFlowStage.DoctorSelection;
    }

    public PatientFlowPrompt CurrentPrompt() => BuildPromptForCurrentStage();

    private void ApplyAnswer(string text)
    {
        switch (_state.Stage)
        {
            case PatientFlowStage.GuestWelcome:
            case PatientFlowStage.ExistingWelcome:
                _state.Stage = PatientFlowStage.Concern;
                if (!string.IsNullOrWhiteSpace(text) &&
                    !text.Equals("Continue", StringComparison.OrdinalIgnoreCase) &&
                    !text.Equals("Let's go", StringComparison.OrdinalIgnoreCase))
                    _state.Concern = text;
                break;

            case PatientFlowStage.Concern:
                _state.Concern = text;
                _state.Stage = _state.IsSignedIn && _state.HasPriorTriage
                    ? PatientFlowStage.ReusePriorTriage
                    : _state.IsSignedIn
                        ? PatientFlowStage.Urgency
                        : PatientFlowStage.FirstVisit;
                break;

            case PatientFlowStage.FirstVisit:
                _state.IsFirstVisit = IsYes(text) || text.Contains("first", StringComparison.OrdinalIgnoreCase);
                _state.Stage = PatientFlowStage.Urgency;
                break;

            case PatientFlowStage.ReusePriorTriage:
                if (IsYes(text) || text.Contains("reuse", StringComparison.OrdinalIgnoreCase))
                    _state.Stage = PatientFlowStage.DeepDivePermission;
                else
                    _state.Stage = PatientFlowStage.Urgency;
                break;

            case PatientFlowStage.Urgency:
                _state.Urgency = text;
                _state.Stage = PatientFlowStage.InsuranceStatus;
                break;

            case PatientFlowStage.InsuranceStatus:
                _state.HasInsurance = text.Contains("have insurance", StringComparison.OrdinalIgnoreCase)
                    || (IsYes(text) && !text.Contains("self", StringComparison.OrdinalIgnoreCase));
                if (text.Contains("self", StringComparison.OrdinalIgnoreCase) ||
                    text.Contains("not sure", StringComparison.OrdinalIgnoreCase) ||
                    IsNo(text))
                {
                    _state.HasInsurance = false;
                    _state.Stage = PatientFlowStage.TravelPreference;
                }
                else
                    _state.Stage = PatientFlowStage.InsurancePlan;
                break;

            case PatientFlowStage.InsurancePlan:
                _state.InsurancePlan = text;
                _state.Stage = PatientFlowStage.TravelPreference;
                break;

            case PatientFlowStage.TravelPreference:
                _state.TravelPreference = text;
                _state.Stage = PatientFlowStage.DistancePreference;
                break;

            case PatientFlowStage.DistancePreference:
                _state.DistancePreference = text;
                _state.Stage = PatientFlowStage.DoctorExperience;
                break;

            case PatientFlowStage.DoctorExperience:
                _state.DoctorExperience = text;
                _state.Stage = PatientFlowStage.TopSchool;
                break;

            case PatientFlowStage.TopSchool:
                _state.TopSchoolPreference = text;
                _state.Stage = PatientFlowStage.ReviewsImportance;
                break;

            case PatientFlowStage.ReviewsImportance:
                _state.ReviewsImportance = text;
                _state.Stage = PatientFlowStage.PreferredLanguage;
                break;

            case PatientFlowStage.PreferredLanguage:
                _state.PreferredLanguage = text;
                _state.Stage = PatientFlowStage.BedsideManner;
                break;

            case PatientFlowStage.BedsideManner:
                _state.BedsideManner = text;
                _state.Stage = PatientFlowStage.HolisticVsConventional;
                break;

            case PatientFlowStage.HolisticVsConventional:
                _state.HolisticPreference = text;
                _state.Stage = PatientFlowStage.DeepDivePermission;
                break;

            case PatientFlowStage.DeepDivePermission:
                _state.WantsDeepDive = IsYes(text) || text.Contains("ask", StringComparison.OrdinalIgnoreCase);
                _state.Stage = _state.WantsDeepDive
                    ? PatientFlowStage.DeepDiveFreeText
                    : PatientFlowStage.DoctorMatching;
                break;

            case PatientFlowStage.DeepDiveFreeText:
                _state.DeepDiveNotes = text;
                _state.Stage = PatientFlowStage.DoctorMatching;
                break;

            case PatientFlowStage.DoctorMatching:
                // Selection happens via chips / multi-select UI
                break;

            case PatientFlowStage.DoctorSelection:
                ParseDoctorSelection(text);
                _state.Stage = PatientFlowStage.BookingPreference;
                break;

            case PatientFlowStage.BookingPreference:
                _state.BookingPreference = text.Contains("time", StringComparison.OrdinalIgnoreCase)
                    ? "Prefer appointment time"
                    : "Prefer dentist ranking";
                _state.Stage = (!_state.IsSignedIn && _state.NeedsAccount && !_state.AccountCreated)
                    ? PatientFlowStage.AccountPermission
                    : PatientFlowStage.AppointmentDays;
                break;

            case PatientFlowStage.AccountPermission:
                if (IsYes(text) || text.Contains("account", StringComparison.OrdinalIgnoreCase) ||
                    text.Contains("create", StringComparison.OrdinalIgnoreCase))
                {
                    // ChatPage will navigate to RegistrationPage
                }
                else
                {
                    _state.NeedsAccount = false;
                    _state.Stage = PatientFlowStage.AppointmentDays;
                }
                break;

            case PatientFlowStage.AppointmentDays:
                _state.PreferredDays = text;
                _state.Stage = PatientFlowStage.AppointmentTimes;
                break;

            case PatientFlowStage.AppointmentTimes:
                _state.PreferredTimes = text;
                _state.Stage = PatientFlowStage.CallingOffices;
                break;

            case PatientFlowStage.CallingOffices:
                _state.Stage = PatientFlowStage.BookingResult;
                break;

            case PatientFlowStage.BookingResult:
                _state.Stage = PatientFlowStage.Complete;
                break;
        }
    }

    private void ParseDoctorSelection(string text)
    {
        _state.SelectedDoctorIds.Clear();
        if (_state.RankedDoctors.Count == 0)
            return;

        if (text.Contains("all", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("keep all", StringComparison.OrdinalIgnoreCase))
        {
            _state.SelectedDoctorIds = _state.RankedDoctors.Select(d => d.Id).ToList();
            return;
        }

        if (text.Contains("#1", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("first only", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("only first", StringComparison.OrdinalIgnoreCase))
        {
            _state.SelectedDoctorIds.Add(_state.RankedDoctors[0].Id);
            return;
        }

        // "1,2,3" or doctor names
        foreach (var part in text.Split(new[] { ',', ';', '|' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var p = part.Trim();
            if (int.TryParse(p, out var oneBased) && oneBased >= 1 && oneBased <= _state.RankedDoctors.Count)
                _state.SelectedDoctorIds.Add(_state.RankedDoctors[oneBased - 1].Id);
            else
            {
                var match = _state.RankedDoctors.FirstOrDefault(d =>
                    d.Name.Contains(p, StringComparison.OrdinalIgnoreCase));
                if (match != null)
                    _state.SelectedDoctorIds.Add(match.Id);
            }
        }

        if (_state.SelectedDoctorIds.Count == 0 && _state.RankedDoctors.Count > 0)
            _state.SelectedDoctorIds.Add(_state.RankedDoctors[0].Id);
    }

    private PatientFlowPrompt BuildPromptForCurrentStage(bool isEntry = false, string? reprompt = null)
    {
        if (!string.IsNullOrEmpty(reprompt))
            return new PatientFlowPrompt { Text = reprompt, FreeTextAllowed = true, ProgressLabel = Progress() };

        return _state.Stage switch
        {
            PatientFlowStage.GuestWelcome => new PatientFlowPrompt
            {
                Text = "Hi! I'm Nuvi 👋 I'm here to match you with the right dentist — not just any dentist, the right one for YOU.\n\nTooth pain, cleaning, implants, or a new dental home — tell me what's going on, or tap Continue.",
                Options = new[] { "Continue", "I need a dentist", "Tooth pain", "Emergency dental" },
                ProgressLabel = Progress()
            },
            PatientFlowStage.ExistingWelcome => new PatientFlowPrompt
            {
                Text = "Welcome back! 👋 What's going on with your teeth or smile today?",
                Options = new[] { "Continue", "Tooth pain", "Cleaning", "Follow-up" },
                ProgressLabel = Progress()
            },
            PatientFlowStage.Concern => new PatientFlowPrompt
            {
                Text = string.IsNullOrWhiteSpace(_state.Concern)
                    ? "In a sentence or two, what's your dental concern?"
                    : $"Got it — \"{_state.Concern}\". Anything to add, or tap Continue.",
                Options = new[] { "Continue" },
                ProgressLabel = Progress()
            },
            PatientFlowStage.FirstVisit => new PatientFlowPrompt
            {
                Text = "Is this your first time visiting NuviDoc?",
                Options = new[] { "Yes", "No" },
                FreeTextAllowed = false,
                ProgressLabel = Progress()
            },
            PatientFlowStage.ReusePriorTriage => new PatientFlowPrompt
            {
                Text = "I still have your previous preferences on file. Would you like to reuse them, or update your answers?",
                Options = new[] { "Reuse previous answers", "Update my answers" },
                FreeTextAllowed = false,
                ProgressLabel = Progress()
            },
            PatientFlowStage.Urgency => new PatientFlowPrompt
            {
                Text = "How soon do you need to be seen?",
                Options = new[] { "ASAP (this week)", "Within a month", "No rush", "Just exploring" },
                FreeTextAllowed = false,
                ProgressLabel = Progress()
            },
            PatientFlowStage.InsuranceStatus => new PatientFlowPrompt
            {
                Text = "What's your insurance situation?",
                Options = new[] { "I have insurance", "Self-pay", "Not sure yet" },
                FreeTextAllowed = false,
                ProgressLabel = Progress()
            },
            PatientFlowStage.InsurancePlan => new PatientFlowPrompt
            {
                Text = "Which insurance plan should I match against?",
                Options = new[] { "Aetna PPO", "Blue Cross Blue Shield", "Cigna", "United Healthcare", "Medicare", "Humana", "Skip for now" },
                ProgressLabel = Progress()
            },
            PatientFlowStage.TravelPreference => new PatientFlowPrompt
            {
                Text = "Would you travel 30+ minutes for the right dentist?",
                Options = new[] { "Yes", "No" },
                FreeTextAllowed = false,
                ProgressLabel = Progress()
            },
            PatientFlowStage.DistancePreference => new PatientFlowPrompt
            {
                Text = "How important is it that your dentist is close to home or work?",
                Options = new[] { "Very important", "Somewhat important", "Doesn't matter" },
                FreeTextAllowed = false,
                ProgressLabel = Progress()
            },
            PatientFlowStage.DoctorExperience => new PatientFlowPrompt
            {
                Text = "Does experience level matter — do you prefer a dentist who's been practicing for many years?",
                Options = new[] { "Yes", "No", "Neutral" },
                FreeTextAllowed = false,
                ProgressLabel = Progress()
            },
            PatientFlowStage.TopSchool => new PatientFlowPrompt
            {
                Text = "Is training at a top-ranked school or residency important to you?",
                Options = new[] { "Yes", "No", "Neutral" },
                FreeTextAllowed = false,
                ProgressLabel = Progress()
            },
            PatientFlowStage.ReviewsImportance => new PatientFlowPrompt
            {
                Text = "Do online reviews (Google, etc.) matter when choosing a dentist?",
                Options = new[] { "Yes", "No" },
                FreeTextAllowed = false,
                ProgressLabel = Progress()
            },
            PatientFlowStage.PreferredLanguage => new PatientFlowPrompt
            {
                Text = "Is it important that your dentist speaks a language other than English?",
                Options = new[] { "Yes", "No" },
                FreeTextAllowed = false,
                ProgressLabel = Progress()
            },
            PatientFlowStage.BedsideManner => new PatientFlowPrompt
            {
                Text = "On a scale of 1–5, how much does bedside manner matter vs. credentials?",
                Options = new[] { "1", "2", "3", "4", "5" },
                FreeTextAllowed = false,
                ProgressLabel = Progress()
            },
            PatientFlowStage.HolisticVsConventional => new PatientFlowPrompt
            {
                Text = "Do you prefer holistic/integrative care, conventional dentistry, or either?",
                Options = new[] { "Holistic", "Conventional", "Doesn't matter" },
                FreeTextAllowed = false,
                ProgressLabel = Progress()
            },
            PatientFlowStage.DeepDivePermission => new PatientFlowPrompt
            {
                Text = "May I ask a few follow-up questions to make sure they're the best fit? Totally optional — I can show matches now if you prefer.",
                Options = new[] { "Yes, ask away", "No thanks, show my matches" },
                FreeTextAllowed = false,
                ProgressLabel = Progress()
            },
            PatientFlowStage.DeepDiveFreeText => new PatientFlowPrompt
            {
                Text = "Tell me anything else that matters when finding your perfect dentist — symptoms, past experiences, or must-haves.",
                Options = new[] { "Nothing else" },
                ProgressLabel = Progress()
            },
            PatientFlowStage.DoctorMatching => new PatientFlowPrompt
            {
                Text = "Please wait — I'm searching for the best matches for you…",
                ShowDoctorCards = true,
                FreeTextAllowed = false,
                ProgressLabel = Progress()
            },
            PatientFlowStage.DoctorSelection => BuildDoctorSelectionPrompt(),
            PatientFlowStage.BookingPreference => new PatientFlowPrompt
            {
                Text = "What matters more for booking?",
                Options = new[]
                {
                    "Prefer my ranked dentists (call in order)",
                    "Prefer the soonest appointment time"
                },
                FreeTextAllowed = false,
                ProgressLabel = Progress()
            },
            PatientFlowStage.AccountPermission => new PatientFlowPrompt
            {
                Text = "So I can reach out to these offices, can I ask a few questions to set up your free account?",
                Options = new[] { "Yes, create my free account", "Skip for now" },
                NavigateToRegistration = true,
                FreeTextAllowed = false,
                ProgressLabel = Progress()
            },
            PatientFlowStage.AppointmentDays => new PatientFlowPrompt
            {
                Text = "Which days work best for you?",
                Options = new[] { "Weekdays", "Weekends", "Any day", "Mon–Wed", "Thu–Fri" },
                ProgressLabel = Progress()
            },
            PatientFlowStage.AppointmentTimes => new PatientFlowPrompt
            {
                Text = "What times of day work best?",
                Options = new[] { "Mornings", "Afternoons", "Evenings", "Anytime" },
                ProgressLabel = Progress()
            },
            PatientFlowStage.CallingOffices => new PatientFlowPrompt
            {
                Text = "Great — I'll start contacting offices using your preferences (in-person visits only). This may take a moment…",
                Options = new[] { "OK" },
                StartCallingSimulation = true,
                FreeTextAllowed = false,
                ProgressLabel = Progress()
            },
            PatientFlowStage.BookingResult => BuildBookingResultPrompt(),
            PatientFlowStage.Complete => new PatientFlowPrompt
            {
                Text = "You're all set. I'll send reminders 7 days, 3 days, 1 day, and the morning of your visit (in-app + email + SMS when those channels are live).\n\nNeed to cancel or reschedule later? Just open Nuvi and tell me.",
                FlowComplete = true,
                FreeTextAllowed = false,
                ProgressLabel = "Done"
            },
            _ => new PatientFlowPrompt { Text = "Let's continue.", ProgressLabel = Progress() }
        };
    }

    private PatientFlowPrompt BuildDoctorSelectionPrompt()
    {
        if (_state.RankedDoctors.Count == 0)
        {
            return new PatientFlowPrompt
            {
                Text = "I couldn't load live matches right now. You can still continue — I'll use sample ranking when calling is connected.\n\nWhich offices should I contact?",
                Options = new[] { "Keep all (sample)", "Only first" },
                ProgressLabel = Progress()
            };
        }

        var lines = new List<string>
        {
            "I've got a few docs for you! Check them out — first are sponsored, then ranked to your preferences.",
            "",
            "Which offices would you like me to reach out to?"
        };
        for (var i = 0; i < _state.RankedDoctors.Count; i++)
        {
            var d = _state.RankedDoctors[i];
            var tag = d.IsSponsored ? " · Sponsored" : "";
            lines.Add($"{i + 1}. {d.Name} — {d.Location}{tag}");
        }

        return new PatientFlowPrompt
        {
            Text = string.Join("\n", lines),
            Options = new[] { "Keep all", "Only #1", "1,2,3" },
            ShowDoctorCards = true,
            ProgressLabel = Progress()
        };
    }

    private PatientFlowPrompt BuildBookingResultPrompt()
    {
        // Simulated outcome: succeed if any doctors selected
        _state.BookingSucceeded = _state.SelectedDoctorIds.Count > 0 || _state.RankedDoctors.Count > 0;
        if (_state.BookingSucceeded)
        {
            var doc = _state.RankedDoctors.FirstOrDefault(d => _state.SelectedDoctorIds.Contains(d.Id))
                      ?? _state.RankedDoctors.FirstOrDefault();
            var name = doc?.Name ?? "your matched dentist";
            var when = DateTime.Now.AddDays(3).Date.AddHours(10);
            _state.BookedSummary = $"{name} on {when:dddd, MMM d} at {when:h:mm tt}";
            return new PatientFlowPrompt
            {
                Text = $"Booking secured (demo)!\n\n{_state.BookedSummary}\n\nThe office will confirm and send new-patient paperwork. You'll also get in-app, email, and SMS confirmation when those services are connected.",
                Options = new[] { "Continue" },
                FreeTextAllowed = false,
                ProgressLabel = Progress()
            };
        }

        return new PatientFlowPrompt
        {
            Text = "I called every dentist on your list and couldn't lock a time yet. I'll continue contacting offices starting at 8:00 AM next working day.",
            Options = new[] { "OK" },
            FreeTextAllowed = false,
            ProgressLabel = Progress()
        };
    }

    private string Progress()
    {
        var order = new[]
        {
            PatientFlowStage.GuestWelcome, PatientFlowStage.ExistingWelcome, PatientFlowStage.Concern,
            PatientFlowStage.FirstVisit, PatientFlowStage.ReusePriorTriage, PatientFlowStage.Urgency,
            PatientFlowStage.InsuranceStatus, PatientFlowStage.InsurancePlan, PatientFlowStage.TravelPreference,
            PatientFlowStage.DistancePreference, PatientFlowStage.DoctorExperience, PatientFlowStage.TopSchool,
            PatientFlowStage.ReviewsImportance, PatientFlowStage.PreferredLanguage, PatientFlowStage.BedsideManner,
            PatientFlowStage.HolisticVsConventional, PatientFlowStage.DeepDivePermission, PatientFlowStage.DeepDiveFreeText,
            PatientFlowStage.DoctorMatching, PatientFlowStage.DoctorSelection, PatientFlowStage.BookingPreference,
            PatientFlowStage.AccountPermission, PatientFlowStage.AppointmentDays, PatientFlowStage.AppointmentTimes,
            PatientFlowStage.CallingOffices, PatientFlowStage.BookingResult, PatientFlowStage.Complete
        };
        var idx = Array.IndexOf(order, _state.Stage);
        if (idx < 0) return "";
        return $"Step {idx + 1} of {order.Length}";
    }

    private static bool IsYes(string text) =>
        text.Equals("yes", StringComparison.OrdinalIgnoreCase) ||
        text.StartsWith("yes", StringComparison.OrdinalIgnoreCase);

    private static bool IsNo(string text) =>
        text.Equals("no", StringComparison.OrdinalIgnoreCase) ||
        text.StartsWith("no ", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("no thanks", StringComparison.OrdinalIgnoreCase);
}
