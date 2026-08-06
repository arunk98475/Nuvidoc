using Docovee.DS.Models;
using Microsoft.Maui.Controls.Shapes;
using nuvidoc.Flow;
using nuvidoc.Services;

namespace nuvidoc;

public partial class ChatPage : ContentPage, IQueryAttributable
{
    private readonly NuvidocApiClient _api;
    private PatientFlowEngine? _engine;
    private string? _initialConcern;
    private bool _busy;
    private bool _started;

    public ChatPage(NuvidocApiClient api)
    {
        InitializeComponent();
        _api = api;
    }

    public void ApplyQueryAttributes(IDictionary<string, object> query)
    {
        if (query.TryGetValue("concern", out var value) && value is string concern)
            _initialConcern = Uri.UnescapeDataString(concern);
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();

        if (_started)
        {
            if (_engine?.State.Stage == PatientFlowStage.AccountPermission &&
                Preferences.Default.Get("patient_account_created", false))
            {
                Preferences.Default.Remove("patient_account_created");
                _engine.MarkAccountCreated();
                await PresentAsync(_engine.CurrentPrompt());
            }
            return;
        }

        _started = true;
        var signedIn = Preferences.Default.Get("patient_signed_in", false);
        _engine = new PatientFlowEngine(new PatientFlowState
        {
            IsSignedIn = signedIn,
            HasPriorTriage = Preferences.Default.Get("patient_has_prior_triage", false),
            NeedsAccount = !signedIn,
            AccountCreated = signedIn
        });
        await PresentAsync(_engine.Start(_initialConcern));
    }

    private async void OnSendClicked(object? sender, EventArgs e)
    {
        var text = AnswerEntry.Text?.Trim() ?? "";
        if (string.IsNullOrEmpty(text)) return;
        AnswerEntry.Text = "";
        await HandleUserInputAsync(text);
    }

    private async Task HandleUserInputAsync(string text)
    {
        if (_engine is null || _busy) return;

        AddUser(text);
        _busy = true;
        SendBtn.IsEnabled = false;
        try
        {
            // Create account → RegistrationPage, then return here
            if (_engine.State.Stage == PatientFlowStage.AccountPermission &&
                (text.Contains("create", StringComparison.OrdinalIgnoreCase) ||
                 text.StartsWith("yes", StringComparison.OrdinalIgnoreCase)))
            {
                var concern = Uri.EscapeDataString(_engine.State.Concern ?? "");
                await Shell.Current.GoToAsync($"{nameof(RegistrationPage)}?concern={concern}&returnToChat=1");
                return;
            }

            var before = _engine.State.Stage;
            var prompt = _engine.Submit(text);

            // Just entered matching — load doctors, then show selection
            if (_engine.State.Stage == PatientFlowStage.DoctorMatching)
            {
                await PresentAsync(prompt);
                await LoadMatchesAsync();
                _engine.GoToDoctorSelection();
                await PresentAsync(_engine.CurrentPrompt());
                return;
            }

            await PresentAsync(prompt);

            if (prompt.StartCallingSimulation)
            {
                await SimulateCallingAsync();
                await PresentAsync(_engine.Submit("OK"));
            }

            // Persist triage completion hint for next visit
            if (_engine.State.Stage is PatientFlowStage.DeepDivePermission or PatientFlowStage.DoctorMatching)
                Preferences.Default.Set("patient_has_prior_triage", true);

            _ = before;
        }
        finally
        {
            _busy = false;
            var done = _engine?.State.Stage == PatientFlowStage.Complete;
            SendBtn.IsEnabled = !done;
            await ChatScroll.ScrollToAsync(MessagesLayout, ScrollToPosition.End, true);
        }
    }

    private async Task LoadMatchesAsync()
    {
        try
        {
            var doctors = await _api.GetFeaturedDoctorsAsync(6);
            var ordered = doctors
                .OrderByDescending(d => d.IsSponsored)
                .ThenByDescending(d => d.GoogleRating)
                .ToList();
            _engine!.SetDoctors(ordered);
            if (ordered.Count > 0)
                AddAi($"John — I've got a few docs for you! Check them out. First are sponsored; the rest are ranked.");
        }
        catch
        {
            _engine!.SetDoctors(Array.Empty<DoctorDto>());
            AddAi("Live match search isn't reachable right now — you can still pick a calling strategy with sample options.");
        }
    }

    private async Task SimulateCallingAsync()
    {
        var selected = _engine!.State.RankedDoctors
            .Where(d => _engine.State.SelectedDoctorIds.Contains(d.Id))
            .ToList();
        if (selected.Count == 0)
            selected = _engine.State.RankedDoctors.Take(2).ToList();

        foreach (var d in selected.DefaultIfEmpty())
        {
            var name = d?.Name ?? "next office";
            AddAi($"Calling {name}…");
            await Task.Delay(800);
            AddAi($"{name}: received your appointment preferences.");
            await Task.Delay(500);
        }
    }

    private async Task PresentAsync(PatientFlowPrompt prompt)
    {
        if (!string.IsNullOrWhiteSpace(prompt.Text))
            AddAi(prompt.Text);

        ProgressLabel.Text = prompt.ProgressLabel;
        ChipsLayout.Children.Clear();

        if (prompt.Options != null)
        {
            foreach (var opt in prompt.Options)
            {
                var chip = new Button
                {
                    Text = opt,
                    FontSize = 13,
                    Padding = new Thickness(14, 8),
                    Margin = new Thickness(0, 0, 8, 8),
                    CornerRadius = 20,
                    BackgroundColor = Color.FromArgb("#EAF2EE"),
                    TextColor = Color.FromArgb("#1A2B22"),
                    BorderColor = Color.FromArgb("#D4DDD9"),
                    BorderWidth = 1
                };
                var captured = opt;
                chip.Clicked += async (_, _) =>
                {
                    if (_busy) return;
                    await HandleUserInputAsync(captured);
                };
                ChipsLayout.Children.Add(chip);
            }
        }

        AnswerEntry.IsEnabled = prompt.FreeTextAllowed && !prompt.FlowComplete;
        SendBtn.IsEnabled = !prompt.FlowComplete;
        await ChatScroll.ScrollToAsync(MessagesLayout, ScrollToPosition.End, animated: false);
    }

    private void AddAi(string text) => AddBubble(text, isAi: true);
    private void AddUser(string text) => AddBubble(text, isAi: false);

    private void AddBubble(string text, bool isAi)
    {
        var bubble = new Border
        {
            StrokeThickness = 0,
            Padding = new Thickness(14, 10),
            StrokeShape = new RoundRectangle { CornerRadius = 14 },
            BackgroundColor = Color.FromArgb(isAi ? "#E8EDEB" : "#3D6B5A"),
            HorizontalOptions = isAi ? LayoutOptions.Start : LayoutOptions.End,
            MaximumWidthRequest = 340
        };
        bubble.Content = new Label
        {
            Text = text,
            FontSize = 14,
            TextColor = Color.FromArgb(isAi ? "#1A2B22" : "#FFFFFF"),
            LineBreakMode = LineBreakMode.WordWrap
        };
        MessagesLayout.Children.Add(bubble);
    }
}
