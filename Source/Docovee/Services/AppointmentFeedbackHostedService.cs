using Docovee.BLL.Services;

namespace Docovee.Services;

/// <summary>Sends post-booking WhatsApp/SMS feedback requests after the configured delay.</summary>
public sealed class AppointmentFeedbackHostedService : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(10);
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<AppointmentFeedbackHostedService> _logger;

    public AppointmentFeedbackHostedService(
        IServiceScopeFactory scopeFactory,
        ILogger<AppointmentFeedbackHostedService> logger)
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

        _logger.LogInformation("Appointment feedback worker started.");
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var feedback = scope.ServiceProvider.GetRequiredService<IAppointmentFeedbackService>();
                var sent = await feedback.ProcessDueFeedbackRequestsAsync(stoppingToken);
                if (sent > 0)
                    _logger.LogInformation("Sent {Count} appointment feedback request(s).", sent);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Appointment feedback cycle failed.");
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
