using nuvidoc.Services;

namespace nuvidoc;

/// <summary>
/// Placeholder page for the Logout FlyoutItem.
/// Signs the patient out as soon as it appears, then returns to Home.
/// </summary>
public class LogoutPage : ContentPage
{
    private readonly SignalRBookingPushClient _push;
    private bool _busy;

    public LogoutPage(SignalRBookingPushClient push)
    {
        _push = push;
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
            await _push.DisconnectAsync();
            AuthSession.Clear();

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
