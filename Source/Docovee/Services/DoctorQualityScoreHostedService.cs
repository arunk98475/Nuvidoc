using Docovee.BLL.Services;

namespace Docovee.Services;

/// <summary>Recomputes stale doctor quality scores and auto-pauses sponsorship below the minimum.</summary>
public sealed class DoctorQualityScoreHostedService : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(24);
    private static readonly TimeSpan MaxAge = TimeSpan.FromHours(24);
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<DoctorQualityScoreHostedService> _logger;

    public DoctorQualityScoreHostedService(
        IServiceScopeFactory scopeFactory,
        ILogger<DoctorQualityScoreHostedService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        _logger.LogInformation("Doctor quality score worker started.");
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var quality = scope.ServiceProvider.GetRequiredService<IDoctorQualityScoreService>();
                var count = await quality.RecomputeStaleAsync(MaxAge, stoppingToken);
                if (count > 0)
                    _logger.LogInformation("Recomputed quality scores for {Count} doctor(s).", count);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Doctor quality score cycle failed.");
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
