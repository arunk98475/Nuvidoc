namespace nuvidoc;

public partial class AppShell : Shell
{
    private readonly MenuItem _authMenuItem;

    public AppShell(MainPage homePage)
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

        _authMenuItem = new MenuItem();
        _authMenuItem.Clicked += OnAuthMenuClicked;
        Items.Add(_authMenuItem);
        RefreshAuthMenu();

        Routing.RegisterRoute(nameof(RegistrationPage), typeof(RegistrationPage));
        Routing.RegisterRoute(nameof(LoginPage), typeof(LoginPage));

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
        _authMenuItem.Text = signedIn ? "Sign out" : "Sign in";

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

    private async void OnAuthMenuClicked(object? sender, EventArgs e)
    {
        // MenuItems don't auto-close the flyout (unlike FlyoutItems).
        FlyoutIsPresented = false;

        if (Preferences.Default.Get("patient_signed_in", false))
        {
            Preferences.Default.Remove("patient_signed_in");
            Preferences.Default.Remove("patient_email");
            Preferences.Default.Remove("patient_full_name");
            Preferences.Default.Remove("patient_account_created");
            RefreshAuthMenu();
            await DisplayAlert("Signed out", "You are signed out on this device.", "OK");
            await GoToAsync("//MainPage");
            return;
        }

        await GoToAsync(nameof(LoginPage));
    }
}
