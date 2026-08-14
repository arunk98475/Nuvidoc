using Docovee.BLL.Services;

namespace Docovee.Services;

/// <summary>Sends 7-day / 3-day / 1-day / same-day appointment reminders in Pacific time.</summary>
public sealed class AppointmentReminderHostedService : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<AppointmentReminderHostedService> _logger;

    public AppointmentReminderHostedService(
        IServiceScopeFactory scopeFactory,
        ILogger<AppointmentReminderHostedService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        _logger.LogInformation("Appointment reminder worker started.");
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var reminders = scope.ServiceProvider.GetRequiredService<IPatientReminderService>();
                var sent = await reminders.ProcessDueRemindersAsync(stoppingToken);
                if (sent > 0)
                    _logger.LogInformation("Sent {Count} appointment reminder(s).", sent);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Appointment reminder cycle failed.");
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
