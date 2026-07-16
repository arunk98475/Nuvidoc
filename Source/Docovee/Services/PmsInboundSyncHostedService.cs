using Docovee.BLL.Services;

namespace Docovee.Services;

/// <summary>
/// Polls connected PMS calendars periodically and reconciles into NuviDoc appointments.
/// </summary>
public sealed class PmsInboundSyncHostedService : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<PmsInboundSyncHostedService> _logger;

    public PmsInboundSyncHostedService(
        IServiceScopeFactory scopeFactory,
        ILogger<PmsInboundSyncHostedService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(45), stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var pms = scope.ServiceProvider.GetRequiredService<IPmsCalendarService>();
                var changed = await pms.SyncInboundAsync(stoppingToken);
                if (changed > 0)
                    _logger.LogInformation("PMS inbound sync updated {Count} appointment(s).", changed);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "PMS inbound sync cycle failed.");
            }

            try
            {
                await Task.Delay(Interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}
