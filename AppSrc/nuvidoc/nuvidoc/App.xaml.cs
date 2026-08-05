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
        return new Window(shell);
    }
}
