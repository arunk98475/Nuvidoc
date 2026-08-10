using Docovee.DS.Models;

namespace Docovee.BLL.Services.PatientPush;

/// <summary>One delivery transport (SignalR, FCM, APNs, …).</summary>
public interface IPatientPushChannel
{
    string Name { get; }
    Task SendAsync(PatientPushMessage message, CancellationToken cancellationToken = default);
}
