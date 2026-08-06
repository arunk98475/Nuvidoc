using Docovee.DS.Models;
using Microsoft.Maui.Controls.Shapes;
using nuvidoc.Services;

namespace nuvidoc;

public partial class DoctorProfilePage : ContentPage
{
    private readonly NuvidocApiClient _api;
    private readonly MatchNavState _nav;
    private bool _started;

    public DoctorProfilePage(NuvidocApiClient api, MatchNavState nav)
    {
        InitializeComponent();
        _api = api;
        _nav = nav;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        if (_started) return;
        _started = true;
        await LoadAsync();
    }

    private async void OnBackClicked(object? sender, EventArgs e)
    {
        await Shell.Current.GoToAsync("..");
    }

    private async Task LoadAsync()
    {
        ErrorLabel.IsVisible = false;

        if (_nav.SelectedDoctorId is not int doctorId)
        {
            ShowError("No doctor selected.");
            return;
        }

        if (!string.IsNullOrWhiteSpace(_nav.SelectedDoctorName))
            HeaderTitleLabel.Text = _nav.SelectedDoctorName;

        // Show whatever card details we already have while the full profile loads.
        if (_nav.SelectedDoctorCard is DoctorDto card)
            ShowCardSnapshot(card);

        SetNuviLoading(true);
        ProfileLoadingPanel.IsVisible = true;

        var nuviTask = LoadNuviCommentAsync(doctorId);
        var profileTask = LoadFullProfileAsync(doctorId);

        await Task.WhenAll(nuviTask, profileTask);
    }

    private async Task LoadNuviCommentAsync(int doctorId)
    {
        try
        {
            var (ok, _, data) = await _api.SendChatMessageAsync(new ChatMessageRequest
            {
                SessionKey = _nav.SessionKey,
                SelectedDoctorId = doctorId,
                Message = ""
            });

            if (ok)
                _nav.SessionKey = data.SessionKey;

            var comment = ok && !string.IsNullOrWhiteSpace(data.Text)
                ? data.Text
                : _nav.MatchReason;

            if (!string.IsNullOrWhiteSpace(comment))
            {
                _nav.NuviComment = comment;
                NuviCommentLabel.Text = comment;
                NuviCommentLabel.IsVisible = true;
                SetNuviLoading(false);
            }
            else
            {
                NuviCommentLabel.Text = "Nuvi’s notes for this match aren’t available right now.";
                NuviCommentLabel.IsVisible = true;
                SetNuviLoading(false);
            }
        }
        catch
        {
            if (!string.IsNullOrWhiteSpace(_nav.MatchReason))
            {
                NuviCommentLabel.Text = _nav.MatchReason;
                NuviCommentLabel.IsVisible = true;
            }
            else
            {
                NuviCommentLabel.Text = "Could not load Nuvi’s comments.";
                NuviCommentLabel.IsVisible = true;
            }

            SetNuviLoading(false);
        }
    }

    private async Task LoadFullProfileAsync(int doctorId)
    {
        try
        {
            var profile = await _api.GetDoctorProfileAsync(doctorId);
            ProfileLoadingPanel.IsVisible = false;

            if (profile is null)
            {
                // Keep card snapshot if we already showed it.
                if (_nav.SelectedDoctorCard is null)
                    ShowError("Doctor profile was not found.");
                return;
            }

            HeaderTitleLabel.Text = profile.Name;
            BuildFullProfile(profile);
        }
        catch (Exception ex)
        {
            ProfileLoadingPanel.IsVisible = false;
            if (_nav.SelectedDoctorCard is null)
                ShowError($"Could not load profile. ({ex.Message})");
            else
                ErrorLabel.IsVisible = false;
        }
    }

    private void SetNuviLoading(bool loading)
    {
        NuviLoadingRow.IsVisible = loading;
        if (loading)
            NuviCommentLabel.IsVisible = false;
    }

    private void ShowError(string message)
    {
        ProfileLoadingPanel.IsVisible = false;
        SetNuviLoading(false);
        ErrorLabel.IsVisible = true;
        ErrorLabel.Text = message;
    }

    private void ShowCardSnapshot(DoctorDto d)
    {
        ProfileLayout.Children.Clear();

        var body = d.Specialty;
        if (!string.IsNullOrWhiteSpace(d.PracticeName))
            body += $"\n{d.PracticeName}";
        if (!string.IsNullOrWhiteSpace(d.Location))
            body += $"\n{d.Location}";
        if (d.GoogleRating > 0)
            body += $"\n★ {d.GoogleRating:0.0} ({d.GoogleReviewCount})";

        ProfileLayout.Children.Add(CreateCard(new VerticalStackLayout
        {
            Spacing = 6,
            Children =
            {
                CreateTitle(d.Name),
                CreateMuted(body)
            }
        }));

        if (!string.IsNullOrWhiteSpace(d.MatchReason))
            ProfileLayout.Children.Add(CreateSection("Match note", d.MatchReason!));

        if (!string.IsNullOrWhiteSpace(d.Niche))
            ProfileLayout.Children.Add(CreateSection("Focus", d.Niche!));

        if (!string.IsNullOrWhiteSpace(d.Top3Procedures))
            ProfileLayout.Children.Add(CreateSection("Top procedures", d.Top3Procedures!));

        if (!string.IsNullOrWhiteSpace(d.OfficePhoneNumber))
            ProfileLayout.Children.Add(CreateSection("Phone", d.OfficePhoneNumber!));
    }

    private void BuildFullProfile(PublicDoctorProfileDto p)
    {
        ProfileLayout.Children.Clear();

        var location = string.Join(", ",
            new[] { p.Address, p.City, p.State, p.ZipCode }
                .Where(s => !string.IsNullOrWhiteSpace(s)));

        ProfileLayout.Children.Add(CreateCard(new VerticalStackLayout
        {
            Spacing = 6,
            Children =
            {
                CreateTitle(p.Name),
                CreateMuted(p.Specialty),
                CreateMuted(string.IsNullOrWhiteSpace(p.PracticeName) ? location : $"{p.PracticeName}\n{location}")
            }
        }));

        if (p.GoogleRating > 0 || !string.IsNullOrWhiteSpace(p.SummaryOfReviews))
        {
            var ratingText = p.GoogleRating > 0
                ? $"★ {p.GoogleRating:0.0} ({p.GoogleReviewCount} Google reviews)"
                : "";
            if (!string.IsNullOrWhiteSpace(p.SummaryOfReviews))
                ratingText = string.IsNullOrEmpty(ratingText)
                    ? p.SummaryOfReviews!
                    : $"{ratingText}\n\n{p.SummaryOfReviews}";

            ProfileLayout.Children.Add(CreateSection("Reviews", ratingText!));
        }

        if (!string.IsNullOrWhiteSpace(p.Niche))
            ProfileLayout.Children.Add(CreateSection("Focus", p.Niche!));

        if (!string.IsNullOrWhiteSpace(p.Top3Procedures))
            ProfileLayout.Children.Add(CreateSection("Top procedures", p.Top3Procedures!));

        if (p.YearsOfPractice is int years)
            ProfileLayout.Children.Add(CreateSection("Experience", $"{years} years of practice"));

        if (!string.IsNullOrWhiteSpace(p.OfficePhoneNumber))
            ProfileLayout.Children.Add(CreateSection("Phone", p.OfficePhoneNumber!));

        if (p.Languages.Count > 0)
            ProfileLayout.Children.Add(CreateSection("Languages", string.Join(", ", p.Languages)));

        if (p.InsuranceCarriers.Count > 0)
            ProfileLayout.Children.Add(CreateSection("Insurance", string.Join(", ", p.InsuranceCarriers)));

        var offers = new List<string>();
        if (p.OffersDentalImplants) offers.Add("Dental implants");
        if (p.OffersTmj) offers.Add("TMJ");
        if (p.OffersBotox) offers.Add("Botox");
        if (offers.Count > 0)
            ProfileLayout.Children.Add(CreateSection("Offers", string.Join(" · ", offers)));

        if (!string.IsNullOrWhiteSpace(p.Website))
            ProfileLayout.Children.Add(CreateSection("Website", p.Website!));
    }

    private static Border CreateCard(View content) => new()
    {
        StrokeThickness = 1,
        Stroke = Color.FromArgb("#D4DDD9"),
        BackgroundColor = Colors.White,
        Padding = new Thickness(14),
        StrokeShape = new RoundRectangle { CornerRadius = 14 },
        Content = content
    };

    private static Border CreateSection(string heading, string body) =>
        CreateCard(new VerticalStackLayout
        {
            Spacing = 4,
            Children =
            {
                new Label
                {
                    Text = heading,
                    FontFamily = "OpenSansSemibold",
                    FontSize = 13,
                    TextColor = Color.FromArgb("#3D6B5A")
                },
                new Label
                {
                    Text = body,
                    FontSize = 14,
                    TextColor = Color.FromArgb("#1A2B22"),
                    LineBreakMode = LineBreakMode.WordWrap
                }
            }
        });

    private static Label CreateTitle(string text) => new()
    {
        Text = text,
        FontFamily = "OpenSansSemibold",
        FontSize = 20,
        TextColor = Color.FromArgb("#1A2B22")
    };

    private static Label CreateMuted(string text) => new()
    {
        Text = text,
        FontSize = 14,
        TextColor = Color.FromArgb("#6B8078"),
        LineBreakMode = LineBreakMode.WordWrap
    };
}
