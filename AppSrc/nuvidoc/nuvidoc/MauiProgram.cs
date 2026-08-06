using System.Net;
using Microsoft.Extensions.Logging;
using nuvidoc.Services;

namespace nuvidoc;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder
            .UseMauiApp<App>()
            .ConfigureFonts(fonts =>
            {
                fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
                fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
            });

        builder.Services.AddSingleton<ApiCookieContainer>();

        builder.Services.AddHttpClient<NuvidocApiClient>((sp, client) =>
        {
            client.BaseAddress = new Uri(ApiConfig.BaseUrl.TrimEnd('/') + "/");
            client.Timeout = TimeSpan.FromSeconds(120);
        })
        .ConfigurePrimaryHttpMessageHandler(sp =>
        {
            var cookies = sp.GetRequiredService<ApiCookieContainer>();
            return new HttpClientHandler
            {
                CookieContainer = cookies.Cookies,
                UseCookies = true,
                AutomaticDecompression = DecompressionMethods.All
            };
        });

        builder.Services.AddSingleton<MainPage>();
        builder.Services.AddTransient<RegistrationPage>();
        // Singletons: held by FlyoutItems for the app lifetime.
        builder.Services.AddSingleton<LoginPage>();
        builder.Services.AddSingleton<LogoutPage>();
        builder.Services.AddSingleton<AppShell>();

#if DEBUG
        builder.Logging.AddDebug();
#endif

        return builder.Build();
    }
}
