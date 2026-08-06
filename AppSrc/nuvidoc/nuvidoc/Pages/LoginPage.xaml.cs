using Docovee.DS.Models;
using nuvidoc.Services;

namespace nuvidoc;

public partial class LoginPage : ContentPage
{
    private readonly NuvidocApiClient _api;

    public LoginPage(NuvidocApiClient api)
    {
        InitializeComponent();
        _api = api;
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

            Preferences.Default.Set("patient_signed_in", true);
            Preferences.Default.Set("patient_email", email);
            if (!string.IsNullOrWhiteSpace(result.FullName))
                Preferences.Default.Set("patient_full_name", result.FullName);

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
