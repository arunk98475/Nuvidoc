using Docovee.BLL.Services;

namespace Docovee.Services;

/// <summary>Auto-closes inactive patient accounts and permanently deletes long-closed accounts per admin settings.</summary>
public sealed class PatientAccountLifecycleHostedService : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(24);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<PatientAccountLifecycleHostedService> _logger;

    public PatientAccountLifecycleHostedService(
        IServiceScopeFactory scopeFactory,
        ILogger<PatientAccountLifecycleHostedService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(60), stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        _logger.LogInformation("Patient account lifecycle worker started.");
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var lifecycle = scope.ServiceProvider.GetRequiredService<IPatientAccountLifecycleService>();
                var result = await lifecycle.ProcessDueAccountsAsync(stoppingToken);
                if (result.ClosedCount > 0 || result.DeletedCount > 0)
                {
                    _logger.LogInformation(
                        "Patient account lifecycle: closed {Closed}, permanently deleted {Deleted}.",
                        result.ClosedCount,
                        result.DeletedCount);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Patient account lifecycle cycle failed.");
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
