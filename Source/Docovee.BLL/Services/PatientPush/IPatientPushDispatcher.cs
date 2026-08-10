using Docovee.DS.Models;

namespace Docovee.BLL.Services.PatientPush;

/// <summary>Fans out a message to every registered <see cref="IPatientPushChannel"/>.</summary>
public interface IPatientPushDispatcher
{
    Task DispatchAsync(PatientPushMessage message, CancellationToken cancellationToken = default);
}
