using Docovee.DS.Models;
using Docovee.logging;

namespace Docovee.BLL.Services.PatientPush;

public sealed class PatientPushDispatcher : IPatientPushDispatcher
{
    private readonly IEnumerable<IPatientPushChannel> _channels;
    private readonly IDocoveeLogger _logger;

    public PatientPushDispatcher(IEnumerable<IPatientPushChannel> channels, IDocoveeLogger logger)
    {
        _channels = channels;
        _logger = logger;
    }

    public async Task DispatchAsync(PatientPushMessage message, CancellationToken cancellationToken = default)
    {
        foreach (var channel in _channels)
        {
            try
            {
                await channel.SendAsync(message, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    "Patient push channel {Channel} failed for status {Status} session={SessionKey}: {Error}",
                    channel.Name,
                    message.Status,
                    message.SessionKey,
                    ex.Message);
            }
        }
    }
}
