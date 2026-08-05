namespace nuvidoc;

public partial class AppShell : Shell
{
    public AppShell(MainPage homePage)
    {
        InitializeComponent();

        Items.Add(new ShellContent
        {
            Title = "Home",
            Route = "MainPage",
            Content = homePage
        });

        Routing.RegisterRoute(nameof(RegistrationPage), typeof(RegistrationPage));
    }
}
