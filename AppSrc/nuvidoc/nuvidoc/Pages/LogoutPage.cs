namespace nuvidoc;

/// <summary>
/// Placeholder page for the Logout FlyoutItem.
/// Signs the patient out as soon as it appears, then returns to Home.
/// </summary>
public class LogoutPage : ContentPage
{
    private bool _busy;

    public LogoutPage()
    {
        BackgroundColor = Color.FromArgb("#F5F7F6");
        Content = new VerticalStackLayout
        {
            VerticalOptions = LayoutOptions.Center,
            HorizontalOptions = LayoutOptions.Center,
            Spacing = 12,
            Children =
            {
                new ActivityIndicator { IsRunning = true, Color = Color.FromArgb("#3D6B5A") },
                new Label
                {
                    Text = "Signing out…",
                    FontSize = 16,
                    TextColor = Color.FromArgb("#1A2B22"),
                    HorizontalTextAlignment = TextAlignment.Center
                }
            }
        };
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        if (_busy) return;
        _busy = true;

        try
        {
            Preferences.Default.Remove("patient_signed_in");
            Preferences.Default.Remove("patient_email");
            Preferences.Default.Remove("patient_full_name");
            Preferences.Default.Remove("patient_account_created");

            if (Shell.Current is AppShell shell)
                shell.RefreshAuthMenu();

            await DisplayAlert("Signed out", "You are signed out on this device.", "OK");
            await Shell.Current.GoToAsync("//MainPage");
        }
        finally
        {
            _busy = false;
        }
    }
}
