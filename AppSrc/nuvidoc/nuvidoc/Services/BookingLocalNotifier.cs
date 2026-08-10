using Docovee.DS.Models;
using Plugin.LocalNotification;
using Plugin.LocalNotification.AndroidOption;
using Plugin.LocalNotification.iOSOption;

namespace nuvidoc.Services;

/// <summary>
/// Requests OS notification permission and shows a local notification when a booking update arrives
/// (SignalR while app is alive; FCM can call the same path later).
/// </summary>
public sealed class BookingLocalNotifier
{
    public const string BookingChannelId = "nuvidoc_booking";

    private static int _nextId = 9000;

    public async Task EnsurePermissionAsync()
    {
        try
        {
            if (!await LocalNotificationCenter.Current.AreNotificationsEnabled())
                await LocalNotificationCenter.Current.RequestNotificationPermission();
        }
        catch
        {
            // Permission UI may fail on unsupported platforms; ignore.
        }
    }

    public async Task ShowAsync(PatientPushMessage message)
    {
        await EnsurePermissionAsync();

        var title = string.IsNullOrWhiteSpace(message.Title) ? "NuviDoc" : message.Title.Trim();
        var body = string.IsNullOrWhiteSpace(message.Body)
            ? (string.IsNullOrWhiteSpace(message.Status) ? "Booking update" : message.Status)
            : message.Body.Trim();

        var id = message.NotificationId is > 0
            ? message.NotificationId.Value
            : Interlocked.Increment(ref _nextId);

        var request = new NotificationRequest
        {
            NotificationId = id,
            Title = title,
            Description = body,
            Subtitle = message.Status,
            CategoryType = NotificationCategoryType.Status,
            Android =
            {
                Priority = AndroidPriority.High,
                VisibilityType = AndroidVisibilityType.Public,
                ChannelId = BookingChannelId
            },
            iOS =
            {
                HideForegroundAlert = false,
                PresentAsBanner = true,
                ShowInNotificationCenter = true,
                PlayForegroundSound = true,
                Priority = iOSPriority.TimeSensitive
            }
        };

        try
        {
            await LocalNotificationCenter.Current.Show(request);
        }
        catch
        {
            // Never break SignalR handling if the OS rejects the notification.
        }
    }
}
