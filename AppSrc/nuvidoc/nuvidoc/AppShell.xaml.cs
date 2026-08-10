using nuvidoc.Services;

namespace nuvidoc;

public partial class AppShell : Shell
{
    private readonly FlyoutItem _loginFlyout;
    private readonly FlyoutItem _logoutFlyout;

    public AppShell(MainPage homePage, NotificationsPage notificationsPage, LoginPage loginPage, LogoutPage logoutPage)
    {
        InitializeComponent();

        Items.Add(new FlyoutItem
        {
            Title = "Home",
            Route = "MainPage",
            FlyoutIcon = "home.png",
            Items =
            {
                new ShellContent
                {
                    Title = "Home",
                    Content = homePage,
                    Route = "home",
                    Icon = "home.png",
                    FlyoutIcon = "home.png"
                }
            }
        });

        Items.Add(new FlyoutItem
        {
            Title = "Notifications",
            Route = "Notifications",
            FlyoutIcon = "home.png",
            Items =
            {
                new ShellContent
                {
                    Title = "Notifications",
                    Content = notificationsPage,
                    Route = "notifications",
                    Icon = "home.png",
                    FlyoutIcon = "home.png"
                }
            }
        });

        // All flyout entries are FlyoutItems so Home ↔ Login/Logout navigation works the same way.
        _loginFlyout = new FlyoutItem
        {
            Title = "Login",
            Route = "Login",
            FlyoutIcon = "login.png",
            Items =
            {
                new ShellContent
                {
                    Title = "Login",
                    Content = loginPage,
                    Route = "login",
                    Icon = "login.png",
                    FlyoutIcon = "login.png"
                }
            }
        };

        // Do NOT reuse homePage here — one page cannot live in two FlyoutItems.
        _logoutFlyout = new FlyoutItem
        {
            Title = "Logout",
            Route = "Logout",
            FlyoutIcon = "logout.png",
            Items =
            {
                new ShellContent
                {
                    Title = "Logout",
                    Content = logoutPage,
                    Route = "logout",
                    Icon = "logout.png",
                    FlyoutIcon = "logout.png"
                }
            }
        };

        Items.Add(_loginFlyout);
        Items.Add(_logoutFlyout);

        RefreshAuthMenu();

        // Pushed pages (not in the flyout).
        Routing.RegisterRoute(nameof(RegistrationPage), typeof(RegistrationPage));
        Routing.RegisterRoute(nameof(SearchResultPage), typeof(SearchResultPage));
        Routing.RegisterRoute(nameof(DoctorProfilePage), typeof(DoctorProfilePage));

        Navigated += (_, _) => RefreshAuthMenu();
        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(FlyoutIsPresented) && FlyoutIsPresented)
                RefreshAuthMenu();
        };
    }

    /// <summary>Show Login when signed out, Logout when signed in.</summary>
    public void RefreshAuthMenu()
    {
        var signedIn = AuthSession.IsSignedIn;

        _loginFlyout.FlyoutItemIsVisible = !signedIn;
        _logoutFlyout.FlyoutItemIsVisible = signedIn;

        if (signedIn)
        {
            var name = Preferences.Default.Get("patient_full_name", string.Empty);
            var email = Preferences.Default.Get("patient_email", string.Empty);
            FlyoutUserLabel.Text = string.IsNullOrWhiteSpace(name) ? email : name;
            FlyoutUserStatusLabel.Text = string.IsNullOrWhiteSpace(email) ? "Signed in" : email;
        }
        else
        {
            FlyoutUserLabel.Text = "Welcome";
            FlyoutUserStatusLabel.Text = "Sign in to save your progress";
        }
    }
}
