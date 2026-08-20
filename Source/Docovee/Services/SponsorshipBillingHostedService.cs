using Docovee.BLL.Services;

namespace Docovee.Services;

/// <summary>Charges sponsored doctors on a daily / weekly / monthly / custom schedule.</summary>
public sealed class SponsorshipBillingHostedService : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(24);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<SponsorshipBillingHostedService> _logger;

    public SponsorshipBillingHostedService(
        IServiceScopeFactory scopeFactory,
        ILogger<SponsorshipBillingHostedService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        _logger.LogInformation("Sponsorship billing worker started.");
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var billing = scope.ServiceProvider.GetRequiredService<ISponsorshipBillingService>();
                var count = await billing.ProcessDueRecurringChargesAsync(stoppingToken);
                if (count > 0)
                    _logger.LogInformation("Processed sponsorship billing for {Count} doctor(s).", count);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Sponsorship billing cycle failed.");
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
