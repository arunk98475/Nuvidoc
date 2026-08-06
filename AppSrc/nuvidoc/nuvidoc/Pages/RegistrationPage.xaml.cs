using System.Globalization;
using Docovee.DS.Models;
using Microsoft.Maui.Controls.Shapes;
using nuvidoc.Services;

namespace nuvidoc;

public partial class RegistrationPage : ContentPage, IQueryAttributable
{
    private enum Step
    {
        Name,
        Email,
        Phone,
        DateOfBirth,
        Password,
        ConfirmPassword,
        Done
    }

    private readonly NuvidocApiClient _api;
    private Step _step = Step.Name;
    private string _fullName = "";
    private string _email = "";
    private string _phone = "";
    private DateOnly _dob;
    private string _password = "";
    private string? _concern;
    private bool _busy;

    public RegistrationPage(NuvidocApiClient api)
    {
        InitializeComponent();
        _api = api;
    }

    private bool _returnToChat;

    public void ApplyQueryAttributes(IDictionary<string, object> query)
    {
        if (query.TryGetValue("concern", out var value) && value is string concern)
            _concern = Uri.UnescapeDataString(concern);
        if (query.TryGetValue("returnToChat", out var ret) && ret is string s)
            _returnToChat = s == "1" || s.Equals("true", StringComparison.OrdinalIgnoreCase);
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        if (MessagesLayout.Children.Count > 0)
            return;

        var first = string.IsNullOrWhiteSpace(_concern)
            ? "Let's set up your free NuviDoc account so I can help match you with the right dentist."
            : $"Got it — \"{_concern}\". First, let's set up your free account so I can help you get booked.";

        AddAi(first);
        AddAi("What's your name?");
        HintLabel.Text = "Step 1 of 6 · Name";
    }

    private async void OnSendClicked(object? sender, EventArgs e)
    {
        if (_busy || _step == Step.Done)
            return;

        var text = AnswerEntry.Text?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(text))
            return;

        AnswerEntry.Text = "";
        var display = _step is Step.Password or Step.ConfirmPassword ? "••••••••" : text;
        AddUser(display);

        _busy = true;
        SendBtn.IsEnabled = false;
        try
        {
            await AdvanceAsync(text);
        }
        finally
        {
            _busy = false;
            SendBtn.IsEnabled = _step != Step.Done;
            AnswerEntry.IsPassword = _step is Step.Password or Step.ConfirmPassword;
            await ChatScroll.ScrollToAsync(MessagesLayout, ScrollToPosition.End, true);
        }
    }

    private async Task AdvanceAsync(string answer)
    {
        switch (_step)
        {
            case Step.Name:
                if (answer.Length < 2)
                {
                    AddAi("Please enter your full name.");
                    return;
                }

                _fullName = answer;
                _step = Step.Email;
                AddAi($"Nice to meet you, {_fullName.Split(' ')[0]}! What's the best email address for you?");
                HintLabel.Text = "Step 2 of 6 · Email (your login)";
                AnswerEntry.Keyboard = Keyboard.Email;
                break;

            case Step.Email:
                if (!answer.Contains('@'))
                {
                    AddAi("That doesn't look like an email — could you try again?");
                    return;
                }

                try
                {
                    var check = await _api.CheckEmailAvailableAsync(answer);
                    if (!check.Available)
                    {
                        AddAi(check.Message ?? "That email is already registered.");
                        return;
                    }
                }
                catch (Exception ex)
                {
                    AddAi($"I couldn't reach the server ({ex.Message}). Is the NuviDoc API running?");
                    return;
                }

                _email = answer.Trim();
                _step = Step.Phone;
                AddAi("And a phone number? (We'll only use this if your matched dentist needs to confirm your appointment.)");
                HintLabel.Text = "Step 3 of 6 · Phone";
                AnswerEntry.Keyboard = Keyboard.Telephone;
                break;

            case Step.Phone:
                if (answer.Length < 7)
                {
                    AddAi("Please enter a valid phone number.");
                    return;
                }

                _phone = answer;
                _step = Step.DateOfBirth;
                AddAi("What's your date of birth? (MM/DD/YYYY — dental offices need this to keep your chart accurate.)");
                HintLabel.Text = "Step 4 of 6 · Date of birth";
                AnswerEntry.Keyboard = Keyboard.Default;
                AnswerEntry.IsPassword = false;
                break;

            case Step.DateOfBirth:
                if (!TryParseDob(answer, out _dob))
                {
                    AddAi("Please enter your date of birth as MM/DD/YYYY (for example, 04/09/1980).");
                    return;
                }

                _step = Step.Password;
                AddAi("Last step for your profile — create a password so you can come back anytime. (Your login will be your email + this password.)");
                HintLabel.Text = "Step 5 of 6 · Password (min 6 characters)";
                AnswerEntry.Keyboard = Keyboard.Default;
                AnswerEntry.IsPassword = true;
                break;

            case Step.Password:
                if (answer.Length < 6)
                {
                    AddAi("Password must be at least 6 characters.");
                    return;
                }

                _password = answer;
                _step = Step.ConfirmPassword;
                AddAi("Please confirm your password.");
                HintLabel.Text = "Step 6 of 6 · Confirm password";
                AnswerEntry.IsPassword = true;
                break;

            case Step.ConfirmPassword:
                if (answer != _password)
                {
                    AddAi("Those passwords don't match — please confirm again.");
                    return;
                }

                await SubmitRegistrationAsync();
                break;
        }
    }

    private async Task SubmitRegistrationAsync()
    {
        AddAi("Creating your free account…");
        HintLabel.Text = "Registering…";

        try
        {
            var result = await _api.RegisterPatientAsync(new MobilePatientRegisterRequest
            {
                FullName = _fullName,
                Email = _email,
                Phone = _phone,
                DateOfBirth = _dob,
                Password = _password,
                ConfirmPassword = _password
            });

            if (!result.Success)
            {
                AddAi(result.Message ?? "Registration failed. Please try again.");
                _step = Step.ConfirmPassword;
                HintLabel.Text = "Step 6 of 6 · Confirm password";
                return;
            }

            _step = Step.Done;
            AnswerEntry.IsEnabled = false;
            SendBtn.IsEnabled = false;
            AnswerEntry.IsPassword = false;
            Preferences.Default.Set("patient_signed_in", true);
            Preferences.Default.Set("patient_account_created", true);
            AddAi(result.Message ?? "Registration successful.");
            HintLabel.Text = "Account created";

            if (_returnToChat)
            {
                AddAi("Taking you back to Nuvi to finish booking…");
                await Task.Delay(600);
                await Shell.Current.GoToAsync("//MainPage");
            }
            else
            {
                AddAi("You can continue chatting with Nuvi from Home.");
                await Task.Delay(400);
                await Shell.Current.GoToAsync("//MainPage");
            }
        }
        catch (Exception ex)
        {
            AddAi($"Something went wrong: {ex.Message}");
            _step = Step.ConfirmPassword;
            HintLabel.Text = "Step 6 of 6 · Confirm password";
        }
    }

    private static bool TryParseDob(string input, out DateOnly dob)
    {
        dob = default;
        var formats = new[] { "M/d/yyyy", "MM/dd/yyyy", "M/d/yy", "yyyy-MM-dd" };
        if (DateOnly.TryParseExact(input.Trim(), formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out dob))
        {
            var today = DateOnly.FromDateTime(DateTime.Today);
            return dob <= today && dob >= today.AddYears(-120);
        }

        return false;
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
            MaximumWidthRequest = 320
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
