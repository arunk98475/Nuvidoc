namespace Docovee.BLL.Data;



/// <summary>

/// Conversation copy and deep-dive questions from Docs/Nuvidoc_Onboarding_Questionnaire_29_06_2026.xlsx

/// (💬 User Intake Flow, 🔍 User Deep-Dive Questions, 🤖 Nuvi AI Conversation Flow).

/// </summary>

public static class NuviFlowContent

{

    public const string GreetingMessage =

        "Hi! I'm Nuvi 👋 I'm here to personally match you with the right doctor — not just any doctor, the right one for YOU. What's going on? Tell me what's been on your mind health-wise, or what kind of doctor you're looking for.";



    public const string FirstVisitQuestionTemplate =

        "Is this your first time visiting {0}?";



    public static readonly string[] FirstVisitOptions =

        ["Yes", "No"];



    public static string FormatFirstVisitQuestion(string siteName) =>

        string.Format(FirstVisitQuestionTemplate, siteName.Trim());



    public const string ReturningUsernameQuestion =

        "Welcome back! Please enter your username or email address.";



    public const string ReturningPasswordQuestion =

        "Thanks — now enter your password.";



    public const string LogisticsVisitQuestion =

        "And are you looking for someone local you can visit in person, or would a virtual/telehealth option work for you — or both?";



    public static readonly string[] LogisticsVisitOptions =

        ["In-person only", "Telehealth only", "Either works"];

    public const string ImplantQualificationQuestion1 =

        "Are you looking for dental implants — missing teeth, failing teeth, or dentures you want replaced?";

    public static string FormatReturningPatientImplantWelcome(string displayName, string chatBotName) =>
        $"Hi {displayName}! 👋 I'm {chatBotName} — your personal dentist-matching concierge. Welcome back! {ImplantQualificationQuestion1}";

    public static string FormatGuestImplantWelcome(string chatBotName) =>
        $"Hi! I'm {chatBotName} 👋 I'm here to match you with the right dentist for dental implants. {ImplantQualificationQuestion1}";

    public static readonly string[] GuestImplantWelcomeOptions =
        ["Yes", "No"];

    public const string GuestImplantWelcomeDeclinedMessage =
        "No problem — I'm here whenever you're ready to explore dental implants.";

    public static readonly string[] ImplantQualificationQuestion1Options =

        ["Implants / missing teeth / denture replacement", "Cleaning", "Filling", "Invisalign", "Just browsing"];

    public const string ImplantQualificationQuestion2 =

        "Do you want to start treatment within the next 60 days, or are you looking further out?";

    public static readonly string[] ImplantQualificationQuestion2Options =

        ["ASAP / this month / within 60 days", "Maybe in 6 months", "Just getting prices"];

    public const string ImplantQualificationQuestion3 =

        "How will you cover this?";

    public static readonly string[] ImplantQualificationQuestion3Options =

        ["Private dental insurance", "Cash/card", "Monthly financing", "Medicaid", "Medicare"];

    public const string ImplantQualificationQuestion4 =

        "Most offices offer financing around $150–300 a month for a case like yours — is that something you'd want to apply for, or are you paying cash or card?";

    public static readonly string[] ImplantQualificationQuestion4Options =

        ["Apply for financing", "Cash/card"];

    public const string ImplantQualificationQuestion5 =

        "Financing companies typically approve around 640 and up. Would you like to continue?";

    public static readonly string[] ImplantQualificationQuestion5Options =

        ["Yes Continue", "I dont want to continue"];

    public const string ImplantQualificationDisqualifiedMessage =

        "Thanks for sharing that. Right now we only book implant patients who can start soon and have a way to pay privately or with financing.";



    public const string LogisticsLocationQuestion =

        "May I know your ZIP code in Houston — or just skip it?";



    public const string LogisticsLocationSkipOption = "Skip for now";



    public static readonly string[] LogisticsLocationOptions =

        [LogisticsLocationSkipOption];



    /// <summary>
    /// Chip label for a saved ZIP/location — show the value only (no "last used" wording).
    /// </summary>
    public static string FormatUseLastZipOption(string lastLocation) =>
        NormalizeSavedLocationChip(lastLocation);

    /// <summary>
    /// Unwraps legacy chip text like "Use last used (77006)" and strips helper phrases
    /// such as "(use last saved ZIP)" so the chip stays a clean location value.
    /// </summary>
    public static string NormalizeSavedLocationChip(string lastLocation)
    {
        if (string.IsNullOrWhiteSpace(lastLocation))
            return string.Empty;

        var s = lastLocation.Trim();

        const string legacyPrefix = "Use last used (";
        if (s.StartsWith(legacyPrefix, StringComparison.OrdinalIgnoreCase) && s.EndsWith(')'))
        {
            var inner = s[legacyPrefix.Length..^1].Trim();
            if (!string.IsNullOrWhiteSpace(inner))
                s = inner;
        }

        s = System.Text.RegularExpressions.Regex.Replace(
            s,
            @"\s*\(\s*use last saved zip(?:\s*code)?\s*\)\s*$",
            string.Empty,
            System.Text.RegularExpressions.RegexOptions.IgnoreCase).Trim();

        return s;
    }

    public static string[] FormatLogisticsLocationOptionsWithSaved(string lastLocation) =>
        [FormatUseLastZipOption(lastLocation), LogisticsLocationSkipOption];



    /// <summary>Default market when the patient skips ZIP (Houston-first launch).</summary>

    public const string DefaultLocationWhenSkipped = "Houston, TX";



    public const string LogisticsInsuranceTypeQuestion =

        "Do you have health insurance, or are you looking for self-pay / cash-pay options?";



    public static readonly string[] LogisticsInsuranceTypeOptions =

        ["I have insurance", "Self-pay", "Not sure yet"];



    public const string LogisticsInsurancePlanQuestion =

        "What insurance plan are you on? (Don't worry if you're not sure — you can skip this for now)";



    public static readonly string[] LogisticsInsurancePlanOptions =

        ["Aetna PPO", "Blue Cross Blue Shield", "Cigna", "United Healthcare", "Medicare", "Humana", "Skip for now"];



    public const string LogisticsUrgencyQuestion =

        "One more quick thing — roughly how soon are you hoping to be seen?";



    public static readonly string[] LogisticsUrgencyOptions =

        ["ASAP (this week)", "Within a month", "No rush", "Just exploring"];



    public const string MomentumBridgeMessage =

        "I've identified some doctors who could be a great fit for you. To help with booking and save your matches, I just need a quick second to set up your profile.";



    public const string DeepDivePermissionQuestionTemplate =

        "Nice to meet you, {0}! May I ask a few follow-up questions just to make sure they're the best fit for you? Totally optional — I can show your matches now if you prefer.";



    public static readonly string[] DeepDivePermissionOptions =

        ["Yes, ask away", "No thanks, show my matches"];



    public const string MatchSearchLoadingMessage =

        "Please wait for a while — I'm searching for the best matches for you.";



    public const string AccountNameQuestion =

        "What's your name?";



    public const string AccountEmailRequiredMessage =

        "I'll need an email to create your profile — a Gmail or any personal email works. What's your email address?";



    public const string AccountPhoneQuestion =

        "And a 10-digit US phone number? (We'll only use this if your matched dentist needs to confirm your appointment.)";



    public const string AccountPhoneRequiredMessage =

        "I need a 10-digit US phone number so the office can reach you — for example, 713-555-1212.";



    public const string AccountMissingPhoneQuestion =

        "I don't have a phone number on file yet — the office needs it to confirm your appointment. What's a 10-digit US number we can use?";



    public const string AccountDateOfBirthQuestion =

        "What's your date of birth? (MM/DD/YYYY — dental offices need this to keep your chart accurate.)";



    public const string AccountMissingDateOfBirthQuestion =

        "I don't have your date of birth on file yet — dental offices need this to keep your chart accurate. What's your date of birth? (MM/DD/YYYY)";



    public const string AccountPasswordQuestion =

        "Last step for your profile — create a password so you can come back anytime and manage your matches. (Your login will be your email + this password.)";



    public const string DeepDiveWelcomeSuffix =

        "now that I know what you're dealing with — let me get to know what matters most to YOU in a doctor.";



    public const string BookingInitiationPrompt =

        "Give {0}'s office a call when you're ready — they're the best next step to get you taken care of.";



    public const string CallOfficesPermissionQuestion =

        "Would you like me to call their offices to book an appointment?";



    public const string MatchRevealAfterListMessage =

        "Above is the list of doctors I found that match your requirements. I think they could be a great fit. Here's who I found—and why I think each one could be the right choice.\n\nWould you like me to call their offices to book an appointment?";



    public static readonly string[] CallOfficesPermissionOptions =

        ["Yes", "No"];



    public const string CallOfficesDeclineEndMessage =

        "No problem — I've saved your matches above. You can tap any doctor card anytime, or refresh to start a new search. I'm here whenever you need me.";



    public const string CallOfficesAskQuestionsPrompt =

        "So I can reach out to these docs, can I ask you a few questions?";



    public static readonly string[] CallOfficesAskQuestionsOptions =

        ["Yes", "No"];



    public const string CallOfficesAllOrTopQuestion =

        "Do I need to call all doctors or just top one?";



    public static readonly string[] CallOfficesAllOrTopOptions =

        ["ALL", "Top one"];



    public const string CallOfficesPreferenceQuestion =

        "Which one you give preference?";



    public static readonly string[] CallOfficesPreferenceOptions =

        ["Dentist", "Date and Time"];



    public const string DeepDiveWildcardQuestion =

        "Is there anything else that matters to you when finding your perfect doctor that we haven't asked yet?";



    public const string DeepDiveLanguageFollowUpQuestion =

        "Which language would you prefer your doctor to speak?";



    public const string DeepDiveWildcardFollowUpQuestion =

        "Please tell me what else matters to you when choosing a doctor.";



    public static string FormatDeepDivePermissionQuestion(string displayName) =>

        string.Format(DeepDivePermissionQuestionTemplate, displayName);



    public static bool IsWildcardDeepDiveQuestion(string question) =>

        question.Contains("anything else that matters", StringComparison.OrdinalIgnoreCase);



    public static bool IsLanguageDeepDiveQuestion(string question) =>

        question.Contains("speaks a language other than English", StringComparison.OrdinalIgnoreCase);



    public static IReadOnlyList<(string Question, string ValidationHint, int MatchWeight, string MatchWeightLabel)> DeepDiveQuestions { get; } =

    [

        ("How important is it that your doctor is close to home or work?", "Very important, somewhat, or doesn't matter", 8, "High"),

        ("Would you travel 30+ minutes for the right doctor?", "Yes or no", 5, "Medium"),

        ("Does experience level matter to you — do you prefer a doctor who's been practicing for many years?", "Yes, no, or neutral", 8, "High"),

        ("Is training at a top-ranked medical school or residency program important to you?", "Yes, no, or neutral", 5, "Medium"),

        ("Shall I only show doctors who accept your insurance plan?", "Yes or no", 10, "Critical"),

        ("Do online reviews (Google, Healthgrades) matter to you when choosing a doctor?", "Yes or no", 5, "Medium"),

        ("Would you consider a newer doctor with fewer reviews if everything else felt right?", "Yes or no", 3, "Low-Medium"),

        ("Is it important that your doctor speaks a language other than English?", "Yes or no", 8, "High"),

        ("On a scale of 1–5, how much does the doctor's personality and bedside manner matter to you vs. just their credentials?", "A number 1 through 5", 8, "High"),

        ("Do you value a doctor who takes a holistic or integrative approach, or do you prefer strictly conventional medicine?", "Holistic, conventional, or doesn't matter", 5, "Medium"),

        ("Would you feel more comfortable with a doctor who shares some of your personal interests or lifestyle?", "Yes or no", 3, "Low-Medium"),

        ("Do you have a preference for your doctor's approximate age group?", "30s, 40s–50s, 60s+, or no preference", 1, "Low"),

        (DeepDiveWildcardQuestion, "Yes or no", 5, "Variable"),

    ];

    public const string CancelBookingChip = "Cancel Booking";

    public const string RescheduleBookingChip = "Reschedule Booking";

    public static readonly string[] ImplantQualificationQuestion1ReturningOptions =
        ["Implants / missing teeth / denture replacement", CancelBookingChip, RescheduleBookingChip];

    public const string CancelBookingNeverMindOption = "Never mind";

    public const string CancelBookingPrompt =
        "Which appointment would you like me to cancel? Pick one below — I'll call the office to confirm.";

    public const string CancelBookingNoneMessage =
        "You don't have any upcoming appointments to cancel right now. I can help you find a dentist whenever you're ready.";

    public const string RescheduleComingSoonMessage =
        "Rescheduling from chat is coming soon. For now, you can cancel a booking and book a new one, or contact the office directly.";

    public const string RescheduleSelectPrompt =
        "Please select the appointment you need to reschedule";

    public const string RescheduleNoneMessage =
        "You don't have any upcoming appointments to reschedule right now. I can help you find a dentist whenever you're ready.";

    public const string RescheduleWindowPrompt =
        "When would you like the new appointment?";

    public const string RescheduleCallPermissionPrompt =
        "Can I call the same practice now to reschedule this appointment for you?";

    public static readonly string[] RescheduleCallPermissionOptions =
        ["Yes", "No"];

    public static readonly string[] YesNoOptions =
        ["Yes", "No"];

    public const string CancelSuccessNewBookingPrompt =
        "I successfully cancelled the booking. Do you want to start a new booking?";

    public const string PostCancelStartBookingPrompt =
        "Great — let's find you a new appointment. What's going on with your teeth or smile?";

    public const string PostCancelDeclineNewBooking =
        "Okay — no problem. I'm here if you need anything else.";

}


