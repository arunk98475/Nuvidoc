using Docovee.DS.Models;
using Microsoft.Maui.Controls.Shapes;
using nuvidoc.Services;

namespace nuvidoc;

public partial class SearchResultPage : ContentPage
{
    private readonly NuvidocApiClient _api;
    private readonly MatchNavState _nav;
    private bool _busy;
    private bool _loaded;

    public SearchResultPage(NuvidocApiClient api, MatchNavState nav)
    {
        InitializeComponent();
        _api = api;
        _nav = nav;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();

        if (_loaded && _nav.DoctorCards is { Count: > 0 })
        {
            ShowResults(_nav.DoctorCards, _nav.RevealText);
            return;
        }

        await RunSearchAsync();
    }

    private async void OnRetryClicked(object? sender, EventArgs e) => await RunSearchAsync();

    private async Task RunSearchAsync()
    {
        if (_busy) return;
        _busy = true;

        LoadingPanel.IsVisible = true;
        ResultsScroll.IsVisible = false;
        ErrorPanel.IsVisible = false;
        SubtitleLabel.Text = "Nuvi is finding the right dentists for you";

        try
        {
            if (!_nav.NeedsSearch && _nav.DoctorCards is { Count: > 0 })
            {
                ShowResults(_nav.DoctorCards, _nav.RevealText);
                return;
            }

            if (_nav.SessionKey is null)
            {
                ShowError("No active chat session. Go back and talk to Nuvi first.");
                return;
            }

            var (ok, status, data) = await _api.SendChatMessageAsync(new ChatMessageRequest
            {
                SessionKey = _nav.SessionKey,
                Action = "match_search",
                Message = ""
            });

            if (!ok)
            {
                ShowError($"Match search failed ({status}). Please try again.");
                return;
            }

            _nav.SessionKey = data.SessionKey;
            _nav.SetResults(data.DoctorCards, data.Text);

            if (data.DoctorCards is not { Count: > 0 })
            {
                ShowError(string.IsNullOrWhiteSpace(data.Text)
                    ? "No matching dentists found. Try refining your chat with Nuvi."
                    : data.Text);
                return;
            }

            await Task.Delay(400);
            ShowResults(data.DoctorCards, data.Text);
            _loaded = true;
        }
        catch (Exception ex)
        {
            ShowError($"Could not reach the server. ({ex.Message})");
        }
        finally
        {
            _busy = false;
        }
    }

    private void ShowError(string message)
    {
        LoadingPanel.IsVisible = false;
        ResultsScroll.IsVisible = false;
        ErrorPanel.IsVisible = true;
        ErrorLabel.Text = message;
    }

    private void ShowResults(IReadOnlyList<DoctorDto> cards, string? revealText)
    {
        LoadingPanel.IsVisible = false;
        ErrorPanel.IsVisible = false;
        ResultsScroll.IsVisible = true;

        SubtitleLabel.Text = cards.Count == 1
            ? "1 dentist matched for you"
            : $"{cards.Count} dentists matched for you";

        CardsLayout.Children.Clear();

        if (!string.IsNullOrWhiteSpace(revealText))
        {
            CardsLayout.Children.Add(new Border
            {
                StrokeThickness = 0,
                BackgroundColor = Color.FromArgb("#EAF2EE"),
                Padding = new Thickness(14, 12),
                StrokeShape = new RoundRectangle { CornerRadius = 12 },
                Content = new Label
                {
                    Text = revealText,
                    FontSize = 14,
                    TextColor = Color.FromArgb("#1A2B22"),
                    LineBreakMode = LineBreakMode.WordWrap
                }
            });
        }

        foreach (var doctor in cards)
            CardsLayout.Children.Add(CreateDoctorCard(doctor));
    }

    private View CreateDoctorCard(DoctorDto d)
    {
        var title = d.IsSponsored ? $"{d.Name} · Sponsored" : d.Name;
        if (d.Recommended)
            title = $"★ {title}";

        var body = d.Specialty;
        if (!string.IsNullOrWhiteSpace(d.PracticeName))
            body += $"\n{d.PracticeName}";
        if (!string.IsNullOrWhiteSpace(d.Location))
            body += $"\n{d.Location}";
        if (d.GoogleRating > 0)
            body += $"\n★ {d.GoogleRating:0.0} ({d.GoogleReviewCount})";
        if (d.MatchScore > 0)
            body += $"\nMatch score: {d.MatchScore}";

        var stack = new VerticalStackLayout { Spacing = 8 };
        stack.Children.Add(new Label
        {
            Text = title,
            FontFamily = "OpenSansSemibold",
            FontSize = 16,
            TextColor = Color.FromArgb("#1A2B22")
        });
        stack.Children.Add(new Label
        {
            Text = body,
            FontSize = 13,
            TextColor = Color.FromArgb("#6B8078"),
            LineBreakMode = LineBreakMode.WordWrap
        });

        if (!string.IsNullOrWhiteSpace(d.MatchReason))
        {
            stack.Children.Add(new Label
            {
                Text = d.MatchReason,
                FontSize = 13,
                TextColor = Color.FromArgb("#3D6B5A"),
                LineBreakMode = LineBreakMode.WordWrap
            });
        }

        stack.Children.Add(new Button
        {
            Text = "View profile",
            FontSize = 13,
            BackgroundColor = Color.FromArgb("#3D6B5A"),
            TextColor = Colors.White,
            CornerRadius = 18,
            HeightRequest = 42,
            CommandParameter = d
        });

        var button = (Button)stack.Children[^1];
        var captured = d;
        button.Clicked += async (_, _) => await OnDoctorSelectedAsync(captured);

        return new Border
        {
            StrokeThickness = 1,
            Stroke = Color.FromArgb("#D4DDD9"),
            BackgroundColor = Colors.White,
            Padding = new Thickness(14),
            StrokeShape = new RoundRectangle { CornerRadius = 14 },
            Content = stack
        };
    }

    private async Task OnDoctorSelectedAsync(DoctorDto doctor)
    {
        if (_busy) return;
        _busy = true;

        try
        {
            // Navigate immediately — Nuvi comments + full profile load on DoctorProfilePage.
            _nav.SelectDoctor(doctor);
            await Shell.Current.GoToAsync(nameof(DoctorProfilePage));
        }
        catch (Exception ex)
        {
            await DisplayAlert("Error", $"Could not open profile. ({ex.Message})", "OK");
        }
        finally
        {
            _busy = false;
        }
    }
}
