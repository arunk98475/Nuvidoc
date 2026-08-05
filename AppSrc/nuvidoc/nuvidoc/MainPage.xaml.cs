using Docovee.DS.Models;
using nuvidoc.Services;

namespace nuvidoc;

public partial class MainPage : ContentPage
{
    private readonly NuvidocApiClient _api;
    private MobileBootstrapDto? _bootstrap;
    private string? _selectedConcern;

    public MainPage(NuvidocApiClient api)
    {
        InitializeComponent();
        _api = api;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await LoadBootstrapAsync();
    }

    private async Task LoadBootstrapAsync()
    {
        StatusLabel.Text = "Connecting to NuviDoc…";
        TalkToNuviBtn.IsEnabled = false;

        try
        {
            _bootstrap = await _api.GetBootstrapAsync();
            if (_bootstrap is null)
            {
                StatusLabel.Text = "Could not load home content.";
                return;
            }

            BrandLabel.Text = _bootstrap.SiteName;
            BotNameLabel.Text = _bootstrap.ChatBotName;
            TaglineLabel.Text = _bootstrap.Tagline;
            WelcomeLabel.Text = _bootstrap.WelcomeMessage;
            TalkToNuviBtn.Text = "Create free account";

            ConcernsLayout.Children.Clear();
            foreach (var concern in _bootstrap.QuickConcerns)
                ConcernsLayout.Children.Add(CreateConcernChip(concern));

            StatusLabel.Text = "Connected · ready";
            TalkToNuviBtn.IsEnabled = true;
        }
        catch (Exception ex)
        {
            WelcomeLabel.Text =
                "Hi! I'm Nuvi. Start the NuviDoc web API (http://localhost:5274), then pull to refresh this screen.";
            StatusLabel.Text = $"Offline — {ex.Message}";
            TalkToNuviBtn.IsEnabled = true;

            if (ConcernsLayout.Children.Count == 0)
            {
                foreach (var concern in new[]
                         {
                             "I need a dentist", "Tooth pain", "Dental implants",
                             "Teeth cleaning", "Invisalign", "Emergency dental"
                         })
                    ConcernsLayout.Children.Add(CreateConcernChip(concern));
            }
        }
    }

    private Button CreateConcernChip(string text)
    {
        var chip = new Button
        {
            Text = text,
            FontSize = 13,
            Padding = new Thickness(14, 8),
            Margin = new Thickness(0, 0, 8, 8),
            CornerRadius = 20,
            BackgroundColor = Color.FromArgb("#EAF2EE"),
            TextColor = Color.FromArgb("#1A2B22"),
            BorderColor = Color.FromArgb("#D4DDD9"),
            BorderWidth = 1
        };
        chip.Clicked += (_, _) => SelectConcern(chip, text);
        return chip;
    }

    private void SelectConcern(Button selected, string text)
    {
        _selectedConcern = text;
        foreach (var child in ConcernsLayout.Children.OfType<Button>())
        {
            var isSelected = ReferenceEquals(child, selected);
            child.BackgroundColor = Color.FromArgb(isSelected ? "#3D6B5A" : "#EAF2EE");
            child.TextColor = Color.FromArgb(isSelected ? "#FFFFFF" : "#1A2B22");
        }
    }

    private async void OnTalkToNuviClicked(object? sender, EventArgs e)
    {
        var concern = string.IsNullOrWhiteSpace(_selectedConcern)
            ? ""
            : Uri.EscapeDataString(_selectedConcern);

        await Shell.Current.GoToAsync(
            string.IsNullOrEmpty(concern)
                ? nameof(RegistrationPage)
                : $"{nameof(RegistrationPage)}?concern={concern}");
    }
}
