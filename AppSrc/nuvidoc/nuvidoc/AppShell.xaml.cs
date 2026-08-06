namespace nuvidoc;

public partial class AppShell : Shell
{
    private readonly FlyoutItem _loginFlyout;
    private readonly MenuItem _signOutMenuItem;

    public AppShell(MainPage homePage, LoginPage loginPage)
    {
        InitializeComponent();

        Items.Add(new FlyoutItem
        {
            Title = "Home",
            Route = "MainPage",
            Items =
            {
                new ShellContent
                {
                    Title = "Home",
                    Content = homePage,
                    Route = "home"
                }
            }
        });

        // Login as FlyoutItem (same type as Home) so the drawer closes and navigates correctly.
        // Do NOT reuse homePage, and do NOT RegisterRoute("LoginPage") — that conflicts with this item.
        _loginFlyout = new FlyoutItem
        {
            Title = "Login",
            Route = "Login",
            Items =
            {
                new ShellContent
                {
                    Title = "Login",
                    Content = loginPage,
                    Route = "login"
                }
            }
        };
        Items.Add(_loginFlyout);

        _signOutMenuItem = new MenuItem { Text = "Sign out" };
        _signOutMenuItem.Clicked += OnSignOutClicked;

        RefreshAuthMenu();

        // Registration stays a pushed page (not in the flyout).
        Routing.RegisterRoute(nameof(RegistrationPage), typeof(RegistrationPage));

        Navigated += (_, _) => RefreshAuthMenu();
        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(FlyoutIsPresented) && FlyoutIsPresented)
                RefreshAuthMenu();
        };
    }

    private void RefreshAuthMenu()
    {
        var signedIn = Preferences.Default.Get("patient_signed_in", false);

        _loginFlyout.FlyoutItemIsVisible = !signedIn;

        if (signedIn)
        {
            if (!Items.Contains(_signOutMenuItem))
                Items.Add(_signOutMenuItem);
        }
        else if (Items.Contains(_signOutMenuItem))
        {
            Items.Remove(_signOutMenuItem);
        }

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

    private async void OnSignOutClicked(object? sender, EventArgs e)
    {
        FlyoutIsPresented = false;

        Preferences.Default.Remove("patient_signed_in");
        Preferences.Default.Remove("patient_email");
        Preferences.Default.Remove("patient_full_name");
        Preferences.Default.Remove("patient_account_created");
        RefreshAuthMenu();
        await DisplayAlert("Signed out", "You are signed out on this device.", "OK");
        await GoToAsync("//MainPage");
    }
}
