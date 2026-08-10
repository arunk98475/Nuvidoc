using Docovee.DS.Models;

namespace nuvidoc.Services;

/// <summary>UI reacts to booking / call terminal events (SignalR now; FCM later).</summary>
public interface IBookingAlertHandler
{
    Task HandleAsync(PatientPushMessage message);
}

/// <summary>
/// Shows an OS local notification and raises an in-process event for pages to subscribe.
/// </summary>
public sealed class BookingAlertHub : IBookingAlertHandler
{
    private readonly BookingLocalNotifier _localNotifier;

    public BookingAlertHub(BookingLocalNotifier localNotifier) => _localNotifier = localNotifier;

    public event Func<PatientPushMessage, Task>? Received;

    public async Task HandleAsync(PatientPushMessage message)
    {
        try
        {
            await _localNotifier.ShowAsync(message);
        }
        catch
        {
            // Local notification must not block UI subscribers.
        }

        var handlers = Received;
        if (handlers == null)
            return;

        foreach (var d in handlers.GetInvocationList())
        {
            if (d is not Func<PatientPushMessage, Task> handler)
                continue;
            try
            {
                await handler(message);
            }
            catch
            {
                // Keep remaining subscribers alive.
            }
        }
    }
}
