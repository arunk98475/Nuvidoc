using Docovee.DS.Models;
using Microsoft.Maui.Controls.Shapes;
using nuvidoc.Services;

namespace nuvidoc;

/// <summary>
/// Home-page Nuvi chat — same server flow as the web app (/api/chat/message).
/// </summary>
public partial class MainPage : ContentPage
{
    private static readonly string MatchSearchLoadingMessage =
        "Please wait for a while — I'm searching for the best matches for you.";

    /// <summary>Minimum time (ms) to show the typing dots before the first welcome message.</summary>
    private const int WelcomeTypingMinMs = 5000;

    private readonly NuvidocApiClient _api;
    private readonly MatchNavState _matchNav;
    private Guid? _sessionKey;
    private string _currentStage = "Greeting";
    private bool _usePasswordInput;
    private bool _optionsOnly;
    private bool _awaitingWildcardConcern;
    private string? _pollingQuestionKind;
    private bool _pendingSkipToMatches;
    private bool _pendingCompleteMatchSearch;
    private bool _busy;
    private bool _started;
    private View? _typingView;
    private CancellationTokenSource? _typingAnimCts;
    private readonly HashSet<int> _recommendedDoctorIds = new();
    private MobileBootstrapDto? _bootstrap;

    public MainPage(NuvidocApiClient api, MatchNavState matchNav)
    {
        InitializeComponent();
        _api = api;
        _matchNav = matchNav;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        RefreshStatus();

        if (_started)
            return;

        _started = true;
        await PlayWelcomeIntroAsync();
    }

    private void RefreshStatus()
    {
        var signedIn = Preferences.Default.Get("patient_signed_in", false);
        StatusLabel.Text = signedIn
            ? $"Signed in · {Preferences.Default.Get("patient_email", "patient")}"
            : "Here to help you find the right dentist";
    }

    private async Task PlayWelcomeIntroAsync()
    {
        ChatEntry.IsEnabled = false;
        SendBtn.IsEnabled = false;
        ShowTyping();

        var startedAt = DateTime.UtcNow;

        string welcome;
        string botName = "Nuvi";
        IReadOnlyList<string> chips =
        [
            "I need a dentist", "Tooth pain", "Dental implants",
            "Teeth cleaning", "Invisalign", "Emergency dental"
        ];

        try
        {
            _bootstrap = await _api.GetBootstrapAsync();
            if (_bootstrap != null)
            {
                botName = _bootstrap.ChatBotName;
                BrandLabel.Text = botName;
                welcome = Preferences.Default.Get("patient_signed_in", false)
                    ? $"Hi {Preferences.Default.Get("patient_full_name", Preferences.Default.Get("patient_email", "there"))}! 👋 I'm {botName} — your personal dentist-matching concierge. Welcome back! What's going on with your teeth or smile?"
                    : _bootstrap.WelcomeMessage;
                if (_bootstrap.QuickConcerns?.Count > 0)
                    chips = _bootstrap.QuickConcerns;
            }
            else
            {
                welcome =
                    $"Hi! I'm {botName} 👋 I'm here to match you with the right dentist — not just any dentist, the right one for YOU. Tooth pain, cleaning, implants, or a new dental home — tell me what's going on.";
            }
        }
        catch
        {
            welcome =
                $"Hi! I'm {botName} 👋 I'm here to match you with the right dentist. (API offline — start the web server to continue the full flow.)";
            StatusLabel.Text = "Offline — start NuviDoc API";
        }

        // Keep typing indicator visible at least WelcomeTypingMinMs before the first message.
        var elapsed = (int)(DateTime.UtcNow - startedAt).TotalMilliseconds;
        var remaining = WelcomeTypingMinMs - elapsed;
        if (remaining > 0)
            await Task.Delay(remaining);

        RemoveTyping();
        AddAi(welcome);
        SetChips(chips);
        ChatEntry.Placeholder = $"Tell {botName} what's going on…";
        ChatEntry.IsEnabled = true;
        SendBtn.IsEnabled = true;
        RefreshStatus();
    }

    private async void OnChatSend(object? sender, EventArgs e)
    {
        var text = ChatEntry.Text?.Trim() ?? "";
        if (string.IsNullOrEmpty(text) || _busy) return;
        ChatEntry.Text = "";
        await SendMessageAsync(text);
    }

    private async Task SendChipAsync(string option)
    {
        if (_busy) return;

        if (option.Contains("no thanks", StringComparison.OrdinalIgnoreCase) ||
            option.Contains("show my match", StringComparison.OrdinalIgnoreCase))
            _pendingSkipToMatches = true;

        if (string.Equals(_pollingQuestionKind, "wildcard", StringComparison.OrdinalIgnoreCase) &&
            option.Equals("No", StringComparison.OrdinalIgnoreCase))
            _pendingCompleteMatchSearch = true;

        await SendMessageAsync(option);
    }

    private async Task SendMessageAsync(string text, string? action = null, int? selectedDoctorId = null)
    {
        if (_busy) return;
        _busy = true;
        SendBtn.IsEnabled = false;

        var wasPassword = _usePasswordInput;
        var completeMatchSearch = _pendingCompleteMatchSearch || (_awaitingWildcardConcern && !string.IsNullOrWhiteSpace(text));
        _pendingCompleteMatchSearch = false;

        var skipToMatches = _pendingSkipToMatches ||
            (_currentStage == "DeepDivePermission" && IsSkipToMatchesMessage(text));
        _pendingSkipToMatches = false;

        var pendingMatchSearch = skipToMatches || completeMatchSearch;

        try
        {
            if (!string.IsNullOrWhiteSpace(text))
                AddUser(wasPassword ? "••••••••" : text);

            if (pendingMatchSearch)
                AddAi(MatchSearchLoadingMessage, loading: true);
            else if (selectedDoctorId == null)
                ShowTyping();

            var (ok, status, data) = await _api.SendChatMessageAsync(new ChatMessageRequest
            {
                SessionKey = _sessionKey,
                Message = string.IsNullOrWhiteSpace(text)
                    ? (selectedDoctorId != null ? "" : (action ?? "continue"))
                    : text,
                Action = action,
                SelectedDoctorId = selectedDoctorId
            });

            RemoveTyping();

            if (!ok)
            {
                AddAi($"Sorry — something went wrong ({status}). Please try again.");
                return;
            }

            if (data.AwaitingMatchSearch)
            {
                if (!pendingMatchSearch)
                    AddAi(string.IsNullOrWhiteSpace(data.Text) ? MatchSearchLoadingMessage : data.Text, loading: true);

                ApplyChatResponseState(data);
                _sessionKey = data.SessionKey;
                _matchNav.BeginSearch(data.SessionKey);
                await Shell.Current.GoToAsync(nameof(SearchResultPage));
                return;
            }

            if (!string.IsNullOrWhiteSpace(data.FollowUpText) && (data.ShowLoading || pendingMatchSearch))
            {
                if (!pendingMatchSearch)
                {
                    AddAi(string.IsNullOrWhiteSpace(data.Text) ? MatchSearchLoadingMessage : data.Text, loading: true);
                    await Task.Delay(2000);
                }

                AddAi(data.FollowUpText);
                if (data.DoctorCards?.Count > 0)
                    AddDoctorCards(data.DoctorCards);
            }
            else
            {
                if (data.ShowLoading)
                    await Task.Delay(1500);

                var hasText = !string.IsNullOrWhiteSpace(data.Text);
                if (hasText || data.DoctorCards?.Count > 0)
                {
                    if (hasText)
                        AddAi(data.Text);
                    if (data.DoctorCards?.Count > 0)
                        AddDoctorCards(data.DoctorCards);
                }
            }

            if (selectedDoctorId is int id)
                _recommendedDoctorIds.Add(id);
            else if (data.SelectedDoctor?.Id is int sid)
                _recommendedDoctorIds.Add(sid);

            ApplyChatResponseState(data);
        }
        catch (Exception ex)
        {
            RemoveTyping();
            AddAi($"I'm having trouble connecting right now. ({ex.Message})");
        }
        finally
        {
            _busy = false;
            if (!_optionsOnly)
                SendBtn.IsEnabled = ChatEntry.IsEnabled;
            await ChatScroll.ScrollToAsync(MessagesLayout, ScrollToPosition.End, true);
        }
    }

    private void ApplyChatResponseState(ChatMessageResponse data)
    {
        _sessionKey = data.SessionKey;
        if (!string.IsNullOrWhiteSpace(data.Stage))
            _currentStage = data.Stage!;
        _awaitingWildcardConcern = data.AwaitingWildcardConcern;
        _pollingQuestionKind = data.PollingQuestionKind;
        _usePasswordInput = data.UsePasswordInput;
        _optionsOnly = data.OptionsOnly;

        StageLabel.Text = data.Stage ?? "";
        ChatEntry.IsPassword = _usePasswordInput;
        ChatEntry.Keyboard = Keyboard.Default;

        if (!string.IsNullOrWhiteSpace(data.InputPlaceholder))
            ChatEntry.Placeholder = data.InputPlaceholder;
        else if (_usePasswordInput)
            ChatEntry.Placeholder = "Enter your password…";
        else
            ChatEntry.Placeholder = $"Tell {BrandLabel.Text} what's going on…";

        if (data.LanguageOptions?.Count > 0)
            SetChips(data.LanguageOptions);
        else
            SetChips(data.Options);

        if (_optionsOnly)
        {
            ChatEntry.Text = "";
            ChatEntry.IsEnabled = false;
            ChatEntry.Placeholder = "Tap an option above to continue";
            SendBtn.IsEnabled = false;
        }
        else
        {
            ChatEntry.IsEnabled = !data.FlowComplete;
            SendBtn.IsEnabled = !data.FlowComplete;
        }

        if (data.SignedIn)
        {
            Preferences.Default.Set("patient_signed_in", true);
            RefreshStatus();
        }

        if (data.FlowComplete)
        {
            SetChips(null);
            ChatEntry.Placeholder = "Conversation complete — reopen app to start over";
            ChatEntry.IsEnabled = false;
            SendBtn.IsEnabled = false;
        }
    }

    private static bool IsSkipToMatchesMessage(string text)
    {
        var lower = (text ?? "").ToLowerInvariant();
        return lower.Contains("no thanks") || lower.Contains("show my match");
    }

    private void SetChips(IReadOnlyList<string>? options)
    {
        ChipsLayout.Children.Clear();
        if (options == null || options.Count == 0)
            return;

        foreach (var opt in options)
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
            chip.Clicked += async (_, _) => await SendChipAsync(captured);
            ChipsLayout.Children.Add(chip);
        }
    }

    private void AddDoctorCards(IReadOnlyList<DoctorDto> cards)
    {
        foreach (var d in cards)
        {
            var card = new Border
            {
                StrokeThickness = 1,
                Stroke = Color.FromArgb("#D4DDD9"),
                BackgroundColor = Colors.White,
                Padding = new Thickness(12),
                StrokeShape = new RoundRectangle { CornerRadius = 12 },
                Margin = new Thickness(0, 4)
            };

            var title = d.IsSponsored ? $"{d.Name} · Sponsored" : d.Name;
            var body = $"{d.Specialty}\n{d.Location}";
            if (d.GoogleRating > 0)
                body += $"\n★ {d.GoogleRating:0.0} ({d.GoogleReviewCount})";

            var stack = new VerticalStackLayout { Spacing = 6 };
            stack.Children.Add(new Label
            {
                Text = title,
                FontFamily = "OpenSansSemibold",
                FontSize = 15,
                TextColor = Color.FromArgb("#1A2B22")
            });
            stack.Children.Add(new Label
            {
                Text = body,
                FontSize = 13,
                TextColor = Color.FromArgb("#6B8078"),
                LineBreakMode = LineBreakMode.WordWrap
            });

            var tap = new Button
            {
                Text = "Select / view",
                FontSize = 13,
                BackgroundColor = Color.FromArgb("#3D6B5A"),
                TextColor = Colors.White,
                CornerRadius = 18,
                HeightRequest = 40
            };
            var doctorId = d.Id;
            tap.Clicked += async (_, _) =>
            {
                if (_recommendedDoctorIds.Contains(doctorId))
                {
                    await DisplayAlert(d.Name, body, "OK");
                    return;
                }

                await SendMessageAsync("", selectedDoctorId: doctorId);
            };
            stack.Children.Add(tap);
            card.Content = stack;
            MessagesLayout.Children.Add(card);
        }
    }

    private void ShowTyping()
    {
        RemoveTyping();

        var row = new HorizontalStackLayout { Spacing = 10 };
        row.Children.Add(new Image
        {
            Source = "favicon_large.png",
            WidthRequest = 28,
            HeightRequest = 28,
            Aspect = Aspect.AspectFit,
            VerticalOptions = LayoutOptions.Start
        });

        var dot1 = CreateTypingDot();
        var dot2 = CreateTypingDot();
        var dot3 = CreateTypingDot();

        var dots = new HorizontalStackLayout
        {
            Spacing = 5,
            VerticalOptions = LayoutOptions.Center,
            Children = { dot1, dot2, dot3 }
        };

        var bubble = new Border
        {
            StrokeThickness = 0,
            Padding = new Thickness(16, 12),
            BackgroundColor = Color.FromArgb("#E8EDEB"),
            StrokeShape = new RoundRectangle { CornerRadius = 14 },
            Content = dots
        };
        row.Children.Add(bubble);
        _typingView = row;
        MessagesLayout.Children.Add(row);
        _ = ChatScroll.ScrollToAsync(MessagesLayout, ScrollToPosition.End, false);

        _typingAnimCts = new CancellationTokenSource();
        _ = AnimateTypingDotsAsync(new[] { dot1, dot2, dot3 }, _typingAnimCts.Token);
    }

    private static BoxView CreateTypingDot() => new()
    {
        WidthRequest = 8,
        HeightRequest = 8,
        CornerRadius = 4,
        Color = Color.FromArgb("#6B8078"),
        Opacity = 0.35,
        VerticalOptions = LayoutOptions.Center
    };

    private static async Task AnimateTypingDotsAsync(BoxView[] dots, CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                for (var i = 0; i < dots.Length; i++)
                {
                    if (token.IsCancellationRequested) return;

                    for (var j = 0; j < dots.Length; j++)
                        dots[j].Opacity = j == i ? 1.0 : 0.35;

                    await Task.Delay(280, token);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Typing indicator removed.
        }
    }

    private void RemoveTyping()
    {
        _typingAnimCts?.Cancel();
        _typingAnimCts?.Dispose();
        _typingAnimCts = null;

        if (_typingView != null)
        {
            MessagesLayout.Children.Remove(_typingView);
            _typingView = null;
        }
    }

    private void AddAi(string text, bool loading = false)
    {
        var display = loading ? $"⏳ {text}" : text;
        AddBubble(display, isAi: true);
    }

    private void AddUser(string text) => AddBubble(text, isAi: false);

    private void AddBubble(string text, bool isAi)
    {
        var row = new Grid
        {
            ColumnDefinitions = isAi
                ? new ColumnDefinitionCollection
                {
                    new(GridLength.Auto),
                    new(GridLength.Star)
                }
                : new ColumnDefinitionCollection
                {
                    new(GridLength.Star),
                    new(GridLength.Auto)
                },
            ColumnSpacing = 8
        };

        var bubble = new Border
        {
            StrokeThickness = 0,
            Padding = new Thickness(14, 10),
            StrokeShape = new RoundRectangle { CornerRadius = 14 },
            BackgroundColor = Color.FromArgb(isAi ? "#E8EDEB" : "#3D6B5A"),
            MaximumWidthRequest = 300,
            HorizontalOptions = isAi ? LayoutOptions.Start : LayoutOptions.End
        };
        bubble.Content = new Label
        {
            Text = text,
            FontSize = 14,
            TextColor = Color.FromArgb(isAi ? "#1A2B22" : "#FFFFFF"),
            LineBreakMode = LineBreakMode.WordWrap
        };

        if (isAi)
        {
            var avatar = new Image
            {
                Source = "favicon_large.png",
                WidthRequest = 28,
                HeightRequest = 28,
                Aspect = Aspect.AspectFit,
                VerticalOptions = LayoutOptions.Start
            };
            row.Add(avatar, 0);
            row.Add(bubble, 1);
        }
        else
        {
            row.Add(bubble, 1);
        }

        MessagesLayout.Children.Add(row);
        _ = ChatScroll.ScrollToAsync(MessagesLayout, ScrollToPosition.End, false);
    }
}
