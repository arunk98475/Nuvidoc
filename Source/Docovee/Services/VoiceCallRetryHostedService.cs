using Docovee.BLL.Services;
using Docovee.DS;
using Docovee.DS.Entities;
using Microsoft.EntityFrameworkCore;

namespace Docovee.Services;

/// <summary>
/// Runs delayed book/cancel/reschedule redials outside the HTTP request.
/// IIS cancels webhook request tokens (and can abort request-captured Task.Run),
/// which previously skipped the 120s retry.
/// </summary>
public sealed class VoiceCallRetryHostedService : BackgroundService
{
    private readonly IVoiceCallRetryQueue _queue;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<VoiceCallRetryHostedService> _logger;

    public VoiceCallRetryHostedService(
        IVoiceCallRetryQueue queue,
        IServiceScopeFactory scopeFactory,
        ILogger<VoiceCallRetryHostedService> logger)
    {
        _queue = queue;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Voice call retry worker started.");
        while (!stoppingToken.IsCancellationRequested)
        {
            VoiceCallRetryJob job;
            try
            {
                job = await _queue.DequeueAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            _ = ProcessJobAsync(job, stoppingToken);
        }
    }

    private async Task ProcessJobAsync(VoiceCallRetryJob job, CancellationToken stoppingToken)
    {
        try
        {
            _logger.LogInformation(
                "Voice retry waiting {Delay}s kind={Kind} call={CallId} session={SessionId} doctor={DoctorId}",
                (int)job.Delay.TotalSeconds, job.Kind, job.CompletedCallId, job.SearchSessionId, job.DoctorId);

            if (job.Delay > TimeSpan.Zero)
                await Task.Delay(job.Delay, stoppingToken);

            using var scope = _scopeFactory.CreateScope();
            if (job.Kind == VoiceCallRetryKind.Intent)
            {
                var bookings = scope.ServiceProvider.GetRequiredService<IVoiceCallBookingService>();
                await bookings.ExecuteScheduledIntentRetryAsync(job.CompletedCallId, CancellationToken.None);
                _logger.LogInformation("Intent retry executed for call {CallId}", job.CompletedCallId);
                return;
            }

            var db = scope.ServiceProvider.GetRequiredService<DocoveeDbContext>();
            var cascade = scope.ServiceProvider.GetRequiredService<IVoiceCallCascadeService>();
            var prior = await db.VoiceOutboundCalls.AsNoTracking()
                .Where(c =>
                    c.SearchSessionId == job.SearchSessionId
                    && c.CallIntent == VoiceOutboundCallIntents.Book
                    && c.DoctorId == job.DoctorId)
                .OrderByDescending(c => c.Id)
                .FirstOrDefaultAsync(CancellationToken.None);

            if (prior == null)
            {
                _logger.LogInformation(
                    "Book retry skipped — no prior call for session {SessionId} doctor {DoctorId}",
                    job.SearchSessionId, job.DoctorId);
                return;
            }

            var result = await cascade.TryCallNextDoctorAsync(
                prior, CancellationToken.None, skipRetryDelay: true);
            _logger.LogInformation(
                "Book retry finished session {SessionId} doctor {DoctorId} started={Started} exhausted={Exhausted}",
                job.SearchSessionId, job.DoctorId, result.NextCallStarted, result.AllDoctorsExhausted);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            _logger.LogInformation("Voice retry worker stopping; dropped kind={Kind} call={CallId}", job.Kind, job.CompletedCallId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Voice retry failed kind={Kind} call={CallId} session={SessionId}",
                job.Kind, job.CompletedCallId, job.SearchSessionId);
        }
    }
}
