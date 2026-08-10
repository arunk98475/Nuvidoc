using System.Net;
using Microsoft.Extensions.Logging;
using nuvidoc.Services;
using Plugin.LocalNotification;
using Plugin.LocalNotification.AndroidOption;

namespace nuvidoc;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder
            .UseMauiApp<App>()
            .UseLocalNotification(config =>
            {
                config.AddAndroid(android =>
                {
                    android.AddChannel(new NotificationChannelRequest
                    {
                        Id = BookingLocalNotifier.BookingChannelId,
                        Name = "Booking updates",
                        Description = "Appointment and call status updates",
                        Importance = AndroidImportance.High,
                        EnableVibration = true,
                        ShowBadge = true
                    });
                });
            })
            .ConfigureFonts(fonts =>
            {
                fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
                fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
            });

        builder.Services.AddSingleton<ApiCookieContainer>();
        builder.Services.AddSingleton<MatchNavState>();
        builder.Services.AddSingleton<BookingLocalNotifier>();
        builder.Services.AddSingleton<BookingAlertHub>();
        builder.Services.AddSingleton<IBookingAlertHandler>(sp => sp.GetRequiredService<BookingAlertHub>());
        builder.Services.AddSingleton<SignalRBookingPushClient>();

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
        builder.Services.AddTransient<SearchResultPage>();
        builder.Services.AddTransient<DoctorProfilePage>();
        builder.Services.AddSingleton<NotificationsPage>();
        builder.Services.AddSingleton<LoginPage>();
        builder.Services.AddSingleton<LogoutPage>();
        builder.Services.AddSingleton<AppShell>();

#if DEBUG
        builder.Logging.AddDebug();
#endif

        return builder.Build();
    }
}
