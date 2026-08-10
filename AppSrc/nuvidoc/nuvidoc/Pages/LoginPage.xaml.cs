using Docovee.DS.Models;
using nuvidoc.Services;

namespace nuvidoc;

public partial class LoginPage : ContentPage
{
    private readonly NuvidocApiClient _api;
    private readonly SignalRBookingPushClient _push;
    private readonly BookingLocalNotifier _notifier;

    public LoginPage(NuvidocApiClient api, SignalRBookingPushClient push, BookingLocalNotifier notifier)
    {
        InitializeComponent();
        _api = api;
        _push = push;
        _notifier = notifier;
    }

    private async void OnLoginClicked(object? sender, EventArgs e)
    {
        var email = EmailEntry.Text?.Trim() ?? "";
        var password = PasswordEntry.Text ?? "";
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
        {
            StatusLabel.Text = "Enter email and password.";
            return;
        }

        StatusLabel.Text = "Signing in…";
        try
        {
            var result = await _api.LoginPatientAsync(new MobilePatientLoginRequest
            {
                Email = email,
                Password = password
            });

            if (!result.Success)
            {
                StatusLabel.Text = result.Message ?? "Sign-in failed.";
                return;
            }

            try
            {
                await _notifier.EnsurePermissionAsync();
                await _push.EnsureConnectedAsync();
            }
            catch { /* hub / permission optional at login */ }

            if (Shell.Current is AppShell shell)
                shell.RefreshAuthMenu();

            StatusLabel.Text = "Signed in. Opening Nuvi…";
            await Shell.Current.GoToAsync("//MainPage");
        }
        catch (Exception ex)
        {
            StatusLabel.Text = $"Could not reach server: {ex.Message}";
        }
    }

    private async void OnRegisterClicked(object? sender, EventArgs e)
    {
        await Shell.Current.GoToAsync(nameof(RegistrationPage));
    }
}
