using nuvidoc.Services;

namespace nuvidoc;

public partial class App : Application
{
    public App()
    {
        // Resources must load before any page XAML that uses StaticResource.
        InitializeComponent();
    }

    protected override Window CreateWindow(IActivationState? activationState)
    {
        var services = Handler?.MauiContext?.Services
            ?? IPlatformApplication.Current?.Services
            ?? throw new InvalidOperationException("MAUI services are not available yet.");

        var shell = services.GetRequiredService<AppShell>();
        var window = new Window(shell);

        // After UI is up: ask notification permission + connect SignalR if already signed in.
        window.Created += (_, _) =>
        {
            _ = BootstrapRealtimeAsync(services);
        };

        return window;
    }

    private static async Task BootstrapRealtimeAsync(IServiceProvider services)
    {
        try
        {
            // Let the first frame paint before showing the permission dialog.
            await Task.Delay(600);

            var notifier = services.GetRequiredService<BookingLocalNotifier>();
            await notifier.EnsurePermissionAsync();

            if (!AuthSession.IsSignedIn)
                return;

            var push = services.GetRequiredService<SignalRBookingPushClient>();
            await push.EnsureConnectedAsync();
        }
        catch
        {
            // Realtime is best-effort; REST notifications page still works.
        }
    }
}
