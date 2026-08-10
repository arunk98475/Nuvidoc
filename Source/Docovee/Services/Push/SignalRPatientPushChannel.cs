using Docovee.BLL.Services.PatientPush;
using Docovee.DS.Models;
using Docovee.Hubs;
using Microsoft.AspNetCore.SignalR;

namespace Docovee.Services.Push;

/// <summary>
/// SignalR delivery channel. Swap/add FcmApnsPatientPushChannel later without changing booking code.
/// </summary>
public sealed class SignalRPatientPushChannel : IPatientPushChannel
{
    private readonly IHubContext<PatientNotificationsHub> _hub;

    public SignalRPatientPushChannel(IHubContext<PatientNotificationsHub> hub) => _hub = hub;

    public string Name => "SignalR";

    public async Task SendAsync(PatientPushMessage message, CancellationToken cancellationToken = default)
    {
        if (message.SessionKey is Guid sessionKey && sessionKey != Guid.Empty)
        {
            await _hub.Clients
                .Group(PatientPushGroupNames.Session(sessionKey))
                .SendAsync(PatientPushClientMethods.BookingUpdated, message, cancellationToken);
        }

        if (message.PatientId is > 0)
        {
            await _hub.Clients
                .Group(PatientPushGroupNames.Patient(message.PatientId.Value))
                .SendAsync(PatientPushClientMethods.BookingUpdated, message, cancellationToken);
        }
    }
}
