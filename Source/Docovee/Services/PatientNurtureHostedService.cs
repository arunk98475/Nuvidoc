using Docovee.BLL.Services;

namespace Docovee.Services;

/// <summary>Sends periodic booking reminders to registered patients who have never booked.</summary>
public sealed class PatientNurtureHostedService : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(20);
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<PatientNurtureHostedService> _logger;

    public PatientNurtureHostedService(
        IServiceScopeFactory scopeFactory,
        ILogger<PatientNurtureHostedService> logger)
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

        _logger.LogInformation("Patient booking nurture worker started.");
        while (!stoppingToken.IsCancellationRequested)
        {
            var sent = 0;
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var nurture = scope.ServiceProvider.GetRequiredService<IPatientNurtureService>();
                var appSettings = scope.ServiceProvider.GetRequiredService<IAppSettingsService>();
                try
                {
                    sent = await nurture.ProcessDueNurtureRemindersAsync(stoppingToken);
                    if (sent > 0)
                        _logger.LogInformation("Sent {Count} booking nurture reminder(s).", sent);
                }
                finally
                {
                    try
                    {
                        await appSettings.RecordPatientBookingReminderRunAsync(
                            DateTime.UtcNow,
                            sent,
                            stoppingToken);
                    }
                    catch (Exception recordEx) when (recordEx is not OperationCanceledException)
                    {
                        _logger.LogWarning(recordEx, "Could not record booking reminder last-run status.");
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Patient booking nurture cycle failed.");
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
