using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Docovee.BLL.Audit;
using Docovee.BLL.Configuration;
using Docovee.BLL.Data;
using Docovee.BLL.Services.Billing;
using Docovee.BLL.Services.PatientPush;
using Docovee.DS;
using Docovee.DS.Entities;
using Docovee.DS.Models;
using Docovee.logging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Docovee.BLL.Services;

public interface IVoiceCallBookingService
{
    Task RecordInitiatedCallAsync(VoiceOutboundCallRecordRequest request, CancellationToken cancellationToken = default);

    Task<bool> ProcessPostCallWebhookAsync(
        string rawBody,
        string? signatureHeader,
        CancellationToken cancellationToken = default);

    Task<bool> ProcessConversationAsync(string conversationId, CancellationToken cancellationToken = default);

    void ScheduleConversationPolling(string conversationId);

    Task FinalizeStaleConversationAsync(string conversationId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Places a delayed cancel/reschedule redial after CallRetryDelaySeconds.
    /// Invoked from a background task so the ElevenLabs webhook is not held open.
    /// </summary>
    Task ExecuteScheduledIntentRetryAsync(int completedCallId, CancellationToken cancellationToken = default);
}

public sealed class VoiceOutboundCallRecordRequest
{
    public required string ConversationId { get; init; }
    public string? CallSid { get; init; }
    public Guid SessionKey { get; init; }
    public int? SearchSessionId { get; init; }
    public int? PatientId { get; init; }
    public int DoctorId { get; init; }
    public string PatientName { get; init; } = string.Empty;
    public string? PatientPhone { get; init; }
    public string? PatientEmail { get; init; }
    public string? VisitReason { get; init; }
    public string? ToNumber { get; init; }
    public string CallIntent { get; init; } = VoiceOutboundCallIntents.Book;
    public int? AppointmentId { get; init; }
}

public interface IPatientNotificationService
{
    Task<IReadOnlyList<PatientNotificationDto>> GetForPatientAsync(int patientId, CancellationToken cancellationToken = default);
    Task<int> CountUnreadAsync(int patientId, CancellationToken cancellationToken = default);
    Task MarkAllReadAsync(int patientId, CancellationToken cancellationToken = default);
}

public sealed class PatientNotificationDto
{
    public int Id { get; init; }
    public string Type { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string Body { get; init; } = string.Empty;
    public int? AppointmentId { get; init; }
    public int? DoctorId { get; init; }
    public bool IsRead { get; init; }
    public DateTime CreatedAt { get; init; }
    /// <summary>Linked appointment start (Pacific wall-clock).</summary>
    public DateTime? AppointmentStartsAt { get; init; }
    /// <summary>Display end of slot (start + 1 hour when not captured).</summary>
    public DateTime? AppointmentEndsAt { get; set; }
}

public sealed class PatientNotificationService : IPatientNotificationService
{
    private readonly DocoveeDbContext _db;

    public PatientNotificationService(DocoveeDbContext db) => _db = db;

    public async Task<IReadOnlyList<PatientNotificationDto>> GetForPatientAsync(
        int patientId, CancellationToken cancellationToken = default)
    {
        var rows = await _db.PatientNotifications.AsNoTracking()
            .Where(n => n.PatientId == patientId)
            .OrderByDescending(n => n.CreatedAt)
            .ThenByDescending(n => n.Id)
            .Take(50)
            .Select(n => new PatientNotificationDto
            {
                Id = n.Id,
                Type = n.Type,
                Title = n.Title,
                Body = n.Body,
                AppointmentId = n.AppointmentId,
                DoctorId = n.DoctorId,
                IsRead = n.IsRead,
                CreatedAt = n.CreatedAt,
                AppointmentStartsAt = n.AppointmentId == null
                    ? null
                    : _db.Appointments.Where(a => a.Id == n.AppointmentId).Select(a => (DateTime?)a.StartsAt).FirstOrDefault()
            })
            .ToListAsync(cancellationToken);

        foreach (var n in rows)
        {
            if (n.AppointmentStartsAt is DateTime start)
                n.AppointmentEndsAt = start.AddHours(1);
        }

        return rows;
    }

    public Task<int> CountUnreadAsync(int patientId, CancellationToken cancellationToken = default) =>
        _db.PatientNotifications.AsNoTracking()
            .CountAsync(n => n.PatientId == patientId && !n.IsRead, cancellationToken);

    public async Task MarkAllReadAsync(int patientId, CancellationToken cancellationToken = default)
    {
        var unread = await _db.PatientNotifications
            .Where(n => n.PatientId == patientId && !n.IsRead)
            .ToListAsync(cancellationToken);
        if (unread.Count == 0)
            return;

        foreach (var n in unread)
            n.IsRead = true;
        await _db.SaveChangesAsync(cancellationToken);
    }
}

public sealed class VoiceCallBookingService : IVoiceCallBookingService
{
    private readonly DocoveeDbContext _db;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IAppointmentService _appointments;
    private readonly ISponsorshipBillingService _sponsorshipBilling;
    private readonly IVisitBillingService _visitBilling;
    private readonly ElevenLabsOptions _elevenLabs;
    private readonly AnthropicOptions _anthropic;
    private readonly IDocoveeLogger _logger;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IPatientPushDispatcher _push;
    private readonly IVoiceCallCascadeService _cascade;
    private readonly INuviVoiceCallingService _voiceCalling;
    private readonly TwilioOptions _twilio;
    private readonly IVoiceCallRetryQueue _retryQueue;
    private readonly IAuditTrailService _audit;

    public VoiceCallBookingService(
        DocoveeDbContext db,
        IHttpClientFactory httpClientFactory,
        IAppointmentService appointments,
        ISponsorshipBillingService sponsorshipBilling,
        IVisitBillingService visitBilling,
        IOptions<ElevenLabsOptions> elevenLabs,
        IOptions<AnthropicOptions> anthropic,
        IDocoveeLogger logger,
        IServiceScopeFactory scopeFactory,
        IPatientPushDispatcher push,
        IVoiceCallCascadeService cascade,
        INuviVoiceCallingService voiceCalling,
        IOptions<TwilioOptions> twilio,
        IVoiceCallRetryQueue retryQueue,
        IAuditTrailService audit)
    {
        _db = db;
        _httpClientFactory = httpClientFactory;
        _appointments = appointments;
        _sponsorshipBilling = sponsorshipBilling;
        _visitBilling = visitBilling;
        _elevenLabs = elevenLabs.Value;
        _anthropic = anthropic.Value;
        _logger = logger;
        _scopeFactory = scopeFactory;
        _push = push;
        _cascade = cascade;
        _voiceCalling = voiceCalling;
        _twilio = twilio.Value;
        _retryQueue = retryQueue;
        _audit = audit;
    }

    private static bool IsTerminalStatus(string status) =>
        status is VoiceOutboundCallStatuses.Booked
            or VoiceOutboundCallStatuses.Canceled
            or VoiceOutboundCallStatuses.Failed
            or VoiceOutboundCallStatuses.NoSlot
            or VoiceOutboundCallStatuses.Declined
            or VoiceOutboundCallStatuses.NoAnswer
            or VoiceOutboundCallStatuses.Completed;

    public async Task RecordInitiatedCallAsync(
        VoiceOutboundCallRecordRequest request,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.ConversationId))
            return;

        var existing = await _db.VoiceOutboundCalls
            .FirstOrDefaultAsync(c => c.ConversationId == request.ConversationId, cancellationToken);
        if (existing != null)
            return;

        var now = DateTime.UtcNow;
        _db.VoiceOutboundCalls.Add(new VoiceOutboundCall
        {
            ConversationId = request.ConversationId.Trim(),
            CallSid = request.CallSid,
            SessionKey = request.SessionKey,
            SearchSessionId = request.SearchSessionId,
            PatientId = request.PatientId,
            DoctorId = request.DoctorId,
            PatientName = string.IsNullOrWhiteSpace(request.PatientName) ? "Patient" : request.PatientName.Trim(),
            PatientPhone = request.PatientPhone,
            PatientEmail = request.PatientEmail,
            VisitReason = request.VisitReason,
            ToNumber = request.ToNumber,
            CallIntent = string.IsNullOrWhiteSpace(request.CallIntent)
                ? VoiceOutboundCallIntents.Book
                : request.CallIntent.Trim(),
            AppointmentId = request.AppointmentId,
            Status = VoiceOutboundCallStatuses.Initiated,
            CreatedAt = now,
            UpdatedAt = now
        });
        await _db.SaveChangesAsync(cancellationToken);
    }

    public void ScheduleConversationPolling(string conversationId)
    {
        if (string.IsNullOrWhiteSpace(conversationId))
            return;

        if (!string.IsNullOrWhiteSpace(_elevenLabs.WebhookSecret))
        {
            _logger.LogInformation(
                "Skipping conversation polling for {ConversationId} — ElevenLabs webhook secret is configured.",
                conversationId);
            return;
        }

        var initialDelaySeconds = Math.Max(0, _elevenLabs.ConversationPollingDelaySeconds);

        _ = Task.Run(async () =>
        {
            try
            {
                // Wait for the live call + ElevenLabs analysis to finish.
                if (initialDelaySeconds > 0)
                    await Task.Delay(TimeSpan.FromSeconds(initialDelaySeconds));
                for (var i = 0; i < 20; i++)
                {
                    using var scope = _scopeFactory.CreateScope();
                    var service = scope.ServiceProvider.GetRequiredService<IVoiceCallBookingService>();
                    var done = await service.ProcessConversationAsync(conversationId);
                    if (done)
                        return;
                    await Task.Delay(TimeSpan.FromSeconds(30));
                }

                using (var scope = _scopeFactory.CreateScope())
                {
                    var service = scope.ServiceProvider.GetRequiredService<IVoiceCallBookingService>();
                    await service.FinalizeStaleConversationAsync(conversationId);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Voice call polling failed for {ConversationId}", conversationId);
            }
        });
    }

    public async Task<bool> ProcessPostCallWebhookAsync(
        string rawBody,
        string? signatureHeader,
        CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(_elevenLabs.WebhookSecret)
            && !ValidateWebhookSignature(rawBody, signatureHeader, _elevenLabs.WebhookSecret))
        {
            _logger.LogInformation("ElevenLabs webhook signature validation failed.");
            return false;
        }

        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(rawBody) ? "{}" : rawBody);
        var root = doc.RootElement;
        var type = root.TryGetProperty("type", out var typeEl) ? typeEl.GetString() : null;

        if (string.Equals(type, "call_initiation_failure", StringComparison.OrdinalIgnoreCase))
        {
            var failureData = root.TryGetProperty("data", out var failureDataEl) ? failureDataEl : root;
            var failureConversationId = failureData.TryGetProperty("conversation_id", out var failureIdEl)
                ? failureIdEl.GetString()
                : root.TryGetProperty("conversation_id", out var failureIdEl2) ? failureIdEl2.GetString() : null;

            if (string.IsNullOrWhiteSpace(failureConversationId))
            {
                _logger.LogInformation("ElevenLabs call_initiation_failure webhook missing conversation_id.");
                return true;
            }

            return await ProcessCallInitiationFailureAsync(failureConversationId, failureData, cancellationToken);
        }

        if (!string.IsNullOrWhiteSpace(type)
            && !type.Contains("transcription", StringComparison.OrdinalIgnoreCase)
            && !type.Contains("post_call", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogInformation("ElevenLabs webhook ignored (type={Type}).", type);
            return true; // ignore audio-only webhooks without analysis
        }

        var data = root.TryGetProperty("data", out var dataEl) ? dataEl : root;
        var conversationId = data.TryGetProperty("conversation_id", out var idEl)
            ? idEl.GetString()
            : root.TryGetProperty("conversation_id", out var idEl2) ? idEl2.GetString() : null;

        if (string.IsNullOrWhiteSpace(conversationId))
        {
            _logger.LogInformation("ElevenLabs webhook missing conversation_id.");
            return true;
        }

        await _audit.LogDiscloseAsync(
            _db,
            AuditEntityTypes.VoiceOutboundCall,
            conversationId,
            "ElevenLabs post-call webhook processed",
            cancellationToken: cancellationToken);

        return await ProcessConversationPayloadAsync(
            conversationId, data, cancellationToken, allowClaudeSlotExtraction: false);
    }

    private async Task<bool> ProcessCallInitiationFailureAsync(
        string conversationId,
        JsonElement data,
        CancellationToken cancellationToken)
    {
        var call = await _db.VoiceOutboundCalls
            .FirstOrDefaultAsync(c => c.ConversationId == conversationId, cancellationToken);
        if (call == null)
        {
            call = await TryCreateCallFromPayloadAsync(conversationId, data, cancellationToken);
            if (call == null)
            {
                _logger.LogInformation(
                    "call_initiation_failure for unknown conversation {ConversationId}", conversationId);
                return false;
            }
        }

        if (IsCancelIntent(call.CallIntent))
            return await ProcessCancelInitiationFailureAsync(call, data, cancellationToken);

        if (IsRescheduleIntent(call.CallIntent))
            return await ProcessRescheduleInitiationFailureAsync(call, data, cancellationToken);

        if (call.AppointmentId.HasValue && call.Status == VoiceOutboundCallStatuses.Booked)
            return true;

        if (call.Status is VoiceOutboundCallStatuses.Booked
                or VoiceOutboundCallStatuses.NoSlot
                or VoiceOutboundCallStatuses.Declined
                or VoiceOutboundCallStatuses.NoAnswer
                or VoiceOutboundCallStatuses.Failed)
            return true;

        var failureReason = data.TryGetProperty("failure_reason", out var frEl)
            ? frEl.GetString()
            : null;
        var status = MapTelephonyFailureReason(failureReason);
        var detail = BuildTelephonyFailureDetail(data, failureReason);

        call.Status = status;
        call.OutcomeNotes = Truncate(detail, 2000);
        call.UpdatedAt = DateTime.UtcNow;
        call.CompletedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "Call initiation failure. Conversation={ConversationId} Status={Status} Reason={Reason}",
            conversationId, status, failureReason ?? "(none)");

        await NotifyBookFailureAsync(
            call,
            "Nuvi call update",
            DescribeCallOutcome(status, detail),
            cancellationToken);
        return true;
    }

    private Task<bool> ProcessCancelInitiationFailureAsync(
        VoiceOutboundCall call, JsonElement data, CancellationToken cancellationToken) =>
        ProcessIntentInitiationFailureAsync(
            call,
            data,
            "Cancel update",
            "Nuvi couldn't reach the office to cancel. Please try again or call the office directly.",
            cancellationToken);

    private Task<bool> ProcessRescheduleInitiationFailureAsync(
        VoiceOutboundCall call, JsonElement data, CancellationToken cancellationToken) =>
        ProcessIntentInitiationFailureAsync(
            call,
            data,
            "Reschedule update",
            "Nuvi couldn't reach the office to reschedule. Please try again or call the office directly.",
            cancellationToken);

    private async Task<bool> ProcessIntentInitiationFailureAsync(
        VoiceOutboundCall call,
        JsonElement data,
        string updateTitle,
        string finalFailureMessage,
        CancellationToken cancellationToken)
    {
        if (call.Status is VoiceOutboundCallStatuses.Canceled
            or VoiceOutboundCallStatuses.Booked
            or VoiceOutboundCallStatuses.Declined
            or VoiceOutboundCallStatuses.NoAnswer
            or VoiceOutboundCallStatuses.Failed
            or VoiceOutboundCallStatuses.Completed)
            return true;

        var failureReason = data.TryGetProperty("failure_reason", out var frEl)
            ? frEl.GetString()
            : null;
        var status = MapTelephonyFailureReason(failureReason);
        var detail = BuildTelephonyFailureDetail(data, failureReason);

        call.Status = status;
        call.OutcomeNotes = Truncate(detail, 2000);
        call.UpdatedAt = DateTime.UtcNow;
        call.CompletedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);

        await NotifyIntentCallFailureAsync(
            call, updateTitle, finalFailureMessage, cancellationToken);
        return true;
    }

    public async Task<bool> ProcessConversationAsync(string conversationId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_elevenLabs.ApiKey))
            return false;

        var client = _httpClientFactory.CreateClient();
        var baseUrl = string.IsNullOrWhiteSpace(_elevenLabs.BaseUrl)
            ? "https://api.elevenlabs.io"
            : _elevenLabs.BaseUrl.TrimEnd('/');
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/v1/convai/conversations/{conversationId}");
        request.Headers.TryAddWithoutValidation("xi-api-key", _elevenLabs.ApiKey.Trim());
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var response = await client.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogInformation(
                "Failed to fetch ElevenLabs conversation {ConversationId}: {Status}",
                conversationId, (int)response.StatusCode);
            return false;
        }

        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
        var root = doc.RootElement;
        // Some GET responses wrap the conversation under "data".
        var data = root.TryGetProperty("data", out var dataEl)
                   && dataEl.ValueKind == JsonValueKind.Object
                   && (dataEl.TryGetProperty("conversation_id", out _)
                       || dataEl.TryGetProperty("analysis", out _)
                       || dataEl.TryGetProperty("transcript", out _))
            ? dataEl
            : root;
        return await ProcessConversationPayloadAsync(
            conversationId, data, cancellationToken, allowClaudeSlotExtraction: true);
    }

    public async Task FinalizeStaleConversationAsync(
        string conversationId,
        CancellationToken cancellationToken = default)
    {
        var call = await _db.VoiceOutboundCalls
            .FirstOrDefaultAsync(c => c.ConversationId == conversationId, cancellationToken);
        if (call == null || call.Status != VoiceOutboundCallStatuses.Initiated)
            return;

        _logger.LogInformation(
            "Finalizing stale initiated conversation {ConversationId} after polling exhausted",
            conversationId);

        call.Status = VoiceOutboundCallStatuses.NoAnswer;
        call.OutcomeNotes = Truncate(
            "Timed out waiting for ElevenLabs to finalize the call (status stayed initiated).",
            2000);
        call.UpdatedAt = DateTime.UtcNow;
        call.CompletedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);

        if (IsCancelIntent(call.CallIntent))
        {
            await NotifyIntentCallFailureAsync(
                call,
                "Cancel update",
                "Nuvi couldn't reach the office to cancel. Please try again or call the office directly.",
                cancellationToken);
            return;
        }

        if (IsRescheduleIntent(call.CallIntent))
        {
            await NotifyIntentCallFailureAsync(
                call,
                "Reschedule update",
                "Nuvi couldn't reach the office to reschedule. Please try again or call the office directly.",
                cancellationToken);
            return;
        }

        await NotifyBookFailureAsync(
            call,
            "Nuvi call update",
            DescribeCallOutcome(VoiceOutboundCallStatuses.NoAnswer, call.OutcomeNotes),
            cancellationToken);
    }

    public async Task ExecuteScheduledIntentRetryAsync(
        int completedCallId,
        CancellationToken cancellationToken = default)
    {
        var call = await _db.VoiceOutboundCalls
            .FirstOrDefaultAsync(c => c.Id == completedCallId, cancellationToken);
        if (call == null)
        {
            _logger.LogInformation("Scheduled intent retry skipped — call {CallId} not found", completedCallId);
            return;
        }

        var result = await TryRetryIntentCallAsync(call, cancellationToken, skipRetryDelay: true);
        if (result.Started && !string.IsNullOrWhiteSpace(result.ChatMessage))
            await PushCallChatUpdateAsync(call, result.ChatMessage, cancellationToken);

        _logger.LogInformation(
            "Scheduled intent retry for call {CallId} started={Started} conversation={ConversationId}",
            completedCallId, result.Started, result.ConversationId);
    }

    private async Task<bool> ProcessConversationPayloadAsync(
        string conversationId,
        JsonElement data,
        CancellationToken cancellationToken,
        bool allowClaudeSlotExtraction)
    {
        var call = await _db.VoiceOutboundCalls
            .FirstOrDefaultAsync(c => c.ConversationId == conversationId, cancellationToken);

        if (call == null)
        {
            // Recover from dynamic variables when webhook arrives before/without local record.
            call = await TryCreateCallFromPayloadAsync(conversationId, data, cancellationToken);
            if (call == null)
            {
                _logger.LogInformation("No voice outbound call row for conversation {ConversationId}", conversationId);
                return false;
            }
        }

        if (IsCancelIntent(call.CallIntent))
            return await ProcessCancelConversationPayloadAsync(call, conversationId, data, cancellationToken);

        if (IsRescheduleIntent(call.CallIntent))
            return await ProcessRescheduleConversationPayloadAsync(
                call, conversationId, data, cancellationToken, allowClaudeSlotExtraction);

        if (call.AppointmentId.HasValue && call.Status == VoiceOutboundCallStatuses.Booked)
            return true;

        if (call.Status is VoiceOutboundCallStatuses.Booked
                or VoiceOutboundCallStatuses.NoSlot
                or VoiceOutboundCallStatuses.Declined
                or VoiceOutboundCallStatuses.NoAnswer)
            return true;

        var status = data.TryGetProperty("status", out var statusEl) ? statusEl.GetString() : null;
        if (ShouldContinuePollingConversation(data, call, status, out var staleOutcome))
            return false;

        var outcome = staleOutcome ?? ExtractBookingOutcome(data);

        // Polling path: if ElevenLabs marked booked but date/time didn't parse, ask Claude.
        if (allowClaudeSlotExtraction && outcome.IsBooked && outcome.StartsAt == null)
        {
            if (!ConversationContentReadyForExtraction(data))
            {
                _logger.LogInformation(
                    "Waiting for ElevenLabs transcript/analysis before Claude extract for {ConversationId}",
                    conversationId);
                return false;
            }

            outcome = await EnrichBookingOutcomeWithClaudeAsync(data, outcome, cancellationToken);
        }

        // Reject past appointment times even if the agent marked the call as booked.
        // Compare in US Pacific (clinic) time — server may run in another timezone (e.g. IST).
        var clinicNow = ElevenLabsTwilioCallingService.GetClinicNow();
        if (outcome.IsBooked && outcome.StartsAt is DateTime proposed
            && NormalizeToClinicLocal(proposed) < clinicNow.AddMinutes(-1))
        {
            var pastNote =
                $"Office gave a past appointment time ({proposed:ddd, MMM d yyyy h:mm tt}). Booking was not saved — need a future date/time.";
            outcome = new BookingOutcome(
                false,
                proposed,
                null,
                VoiceOutboundCallStatuses.NoSlot,
                string.IsNullOrWhiteSpace(outcome.Notes)
                    ? pastNote
                    : $"{outcome.Notes} | {pastNote}");
            _logger.LogInformation(
                "Rejected past booking datetime {StartsAt} for conversation {ConversationId} (clinicNow={ClinicNow})",
                proposed, conversationId, clinicNow);
        }

        call.UpdatedAt = DateTime.UtcNow;
        call.CompletedAt = DateTime.UtcNow;
        call.OutcomeNotes = Truncate(outcome.Notes, 2000);

        if (!outcome.IsBooked)
        {
            call.Status = outcome.StatusHint ?? VoiceOutboundCallStatuses.Completed;
            await _db.SaveChangesAsync(cancellationToken);
            await NotifyBookFailureAsync(
                call,
                "Nuvi call update",
                string.IsNullOrWhiteSpace(outcome.Notes)
                    ? DescribeCallOutcome(call.Status)
                    : outcome.Notes!,
                cancellationToken);
            return true;
        }

        var startsAt = outcome.StartsAt;
        if (outcome.IsBooked && startsAt == null)
        {
            // Do not invent "today" / "tomorrow" — missing datetime means we cannot save a real booking.
            call.Status = VoiceOutboundCallStatuses.Completed;
            call.OutcomeNotes = Truncate(
                string.IsNullOrWhiteSpace(outcome.Notes)
                    ? "Booked on the call, but no appointment_datetime was returned."
                    : $"{outcome.Notes} | Missing appointment_datetime — appointment not saved.",
                2000);
            await _db.SaveChangesAsync(cancellationToken);
            const string missingTitle = "Nuvi call update";
            const string missingBody =
                "The office confirmed a booking, but Nuvi could not capture the appointment date/time. Please confirm the slot with the office.";
            var missingPracticeLabel = await ResolvePracticeLabelAsync(call.DoctorId, cancellationToken);
            var missingChat =
                $"I spoke with {missingPracticeLabel} and they confirmed a booking, but I couldn't capture the appointment date and time. Please confirm the slot with their office directly.";
            await AppendCallChatMessageAsync(call, missingChat, cancellationToken);
            await PushCallChatUpdateAsync(call, missingChat, cancellationToken);
            int? notificationId = null;
            if (call.PatientId is > 0)
            {
                notificationId = await AddNotificationAsync(
                    call.PatientId.Value,
                    PatientNotificationTypes.VoiceCallUpdate,
                    missingTitle,
                    missingBody,
                    doctorId: call.DoctorId,
                    cancellationToken: cancellationToken);
            }

            await DispatchTerminalPushAsync(call, call.Status, missingTitle, missingBody, notificationId, cancellationToken: cancellationToken);
            _logger.LogInformation(
                "Booked without parseable datetime for conversation {ConversationId}",
                conversationId);
            return true;
        }

        if (startsAt == null)
        {
            call.Status = outcome.StatusHint ?? VoiceOutboundCallStatuses.Completed;
            await _db.SaveChangesAsync(cancellationToken);
            await DispatchTerminalPushAsync(
                call,
                call.Status,
                "Nuvi call update",
                $"Call finished with status {call.Status}.",
                cancellationToken: cancellationToken);
            return true;
        }

        startsAt = NormalizeToClinicLocal(startsAt.Value);
        if (startsAt < clinicNow.AddMinutes(-1))
        {
            call.Status = VoiceOutboundCallStatuses.NoSlot;
            call.OutcomeNotes = Truncate(
                $"Rejected past appointment time ({startsAt:g}).", 2000);
            await _db.SaveChangesAsync(cancellationToken);
            const string pastTitle = "Nuvi call update";
            var pastBody =
                $"The office offered a past time ({startsAt:ddd, MMM d yyyy h:mm tt}). No appointment was saved.";
            await NotifyBookFailureAsync(call, pastTitle, pastBody, cancellationToken);
            return true;
        }

        var doctor = await _db.Doctors.AsNoTracking().FirstOrDefaultAsync(d => d.Id == call.DoctorId, cancellationToken);
        DateOnly? dob = null;
        if (call.PatientId is > 0)
        {
            dob = await _db.Patients.AsNoTracking()
                .Where(p => p.Id == call.PatientId.Value)
                .Select(p => p.DateOfBirth)
                .FirstOrDefaultAsync(cancellationToken);
        }

        var appointmentStartsAt = startsAt.Value;
        var appointmentEndsAt = outcome.EndsAt is DateTime end
            ? NormalizeToClinicLocal(end)
            : appointmentStartsAt.AddHours(1);
        if (appointmentEndsAt <= appointmentStartsAt)
            appointmentEndsAt = appointmentStartsAt.AddHours(1);

        var appointment = new Appointment
        {
            DoctorId = call.DoctorId,
            PatientId = call.PatientId,
            PatientName = call.PatientName,
            PatientPhone = call.PatientPhone,
            PatientEmail = call.PatientEmail,
            PatientDateOfBirth = dob is DateOnly d && d.Year > 1900 ? d : new DateOnly(1990, 1, 1),
            VisitReason = string.IsNullOrWhiteSpace(call.VisitReason) ? "Dental appointment (Nuvi booked)" : call.VisitReason!,
            StartsAt = appointmentStartsAt,
            Status = AppointmentStatuses.Confirmed,
            Source = AppointmentSources.NuviChat,
            SearchSessionId = call.SearchSessionId,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        _db.Appointments.Add(appointment);
        await _db.SaveChangesAsync(cancellationToken);

        call.AppointmentId = appointment.Id;
        call.Status = VoiceOutboundCallStatuses.Booked;
        await _db.SaveChangesAsync(cancellationToken);

        var doctorName = doctor?.Name ?? "your dentist";
        var practiceLabel = FormatPracticeLabel(doctor?.PracticeName, doctorName);
        var when = FormatPstSlot(appointmentStartsAt, appointmentEndsAt);
        var bookedTitle = "Appointment booked";
        var bookedBody = $"Nuvi booked your visit with {doctorName} on {when}.";
        var bookedChat = FormatBookedCallChat(practiceLabel, when);
        await AppendCallChatMessageAsync(call, bookedChat, cancellationToken);
        await PushCallChatUpdateAsync(call, bookedChat, cancellationToken);
        int? bookedNotificationId = null;
        if (call.PatientId is > 0)
        {
            bookedNotificationId = await AddNotificationAsync(
                call.PatientId.Value,
                PatientNotificationTypes.AppointmentBooked,
                bookedTitle,
                bookedBody,
                appointment.Id,
                call.DoctorId,
                cancellationToken);
        }

        await DispatchTerminalPushAsync(
            call,
            VoiceOutboundCallStatuses.Booked,
            bookedTitle,
            bookedBody,
            bookedNotificationId,
            appointmentStartsAt,
            appointmentEndsAt,
            doctorName,
            chatMessage: null,
            cancellationToken: cancellationToken);

        _logger.LogInformation(
            "Voice booking saved. Conversation={ConversationId} Appointment={AppointmentId}",
            conversationId, appointment.Id);

        var sponsorshipCharge = await _sponsorshipBilling.TryChargeAsync(
            appointment.DoctorId,
            SponsorshipBillingChargeTrigger.Booking,
            appointment.Id,
            cancellationToken);
        if (!sponsorshipCharge.Success && sponsorshipCharge.ChargeStatus != BillingChargeStatuses.Skipped)
        {
            _logger.LogWarning(
                "Sponsorship billing failed for voice appointment {AppointmentId}: {Message}",
                appointment.Id, sponsorshipCharge.Message);
        }
        else
        {
            _logger.LogInformation(
                "Sponsorship billing for voice appointment {AppointmentId}: {Status} — {Message}",
                appointment.Id, sponsorshipCharge.ChargeStatus, sponsorshipCharge.Message);
        }

        var visitCharge = await _visitBilling.TryChargeAsync(
            appointment.DoctorId,
            appointment.Id,
            VisitBillingChargeTrigger.Booking,
            cancellationToken);
        if (!visitCharge.Success && visitCharge.ChargeStatus != BillingChargeStatuses.Skipped)
        {
            _logger.LogWarning(
                "Visit billing failed for voice appointment {AppointmentId}: {Message}",
                appointment.Id, visitCharge.Message);
        }

        return true;
    }

    private static bool IsCancelIntent(string? intent) =>
        string.Equals(intent, VoiceOutboundCallIntents.Cancel, StringComparison.OrdinalIgnoreCase);

    private static bool IsRescheduleIntent(string? intent) =>
        string.Equals(intent, VoiceOutboundCallIntents.Reschedule, StringComparison.OrdinalIgnoreCase);

    private async Task<bool> ProcessRescheduleConversationPayloadAsync(
        VoiceOutboundCall call,
        string conversationId,
        JsonElement data,
        CancellationToken cancellationToken,
        bool allowClaudeSlotExtraction)
    {
        if (call.Status is VoiceOutboundCallStatuses.Booked
            or VoiceOutboundCallStatuses.Canceled
            or VoiceOutboundCallStatuses.Declined
            or VoiceOutboundCallStatuses.Failed
            or VoiceOutboundCallStatuses.NoAnswer
            or VoiceOutboundCallStatuses.NoSlot
            or VoiceOutboundCallStatuses.Completed)
            return true;

        var convStatus = data.TryGetProperty("status", out var statusEl) ? statusEl.GetString() : null;
        if (ShouldContinuePollingConversation(data, call, convStatus, out var staleOutcome))
            return false;

        var outcome = staleOutcome ?? ExtractBookingOutcome(data);
        if (allowClaudeSlotExtraction && outcome.IsBooked && outcome.StartsAt == null)
        {
            if (!ConversationContentReadyForExtraction(data))
            {
                _logger.LogInformation(
                    "Waiting for ElevenLabs transcript/analysis before Claude extract (reschedule) for {ConversationId}",
                    conversationId);
                return false;
            }

            outcome = await EnrichBookingOutcomeWithClaudeAsync(data, outcome, cancellationToken);
        }

        var clinicNow = ElevenLabsTwilioCallingService.GetClinicNow();
        if (outcome.IsBooked && outcome.StartsAt is DateTime proposed
            && NormalizeToClinicLocal(proposed) < clinicNow.AddMinutes(-1))
        {
            outcome = new BookingOutcome(
                false,
                proposed,
                null,
                VoiceOutboundCallStatuses.NoSlot,
                string.IsNullOrWhiteSpace(outcome.Notes)
                    ? $"Office gave a past appointment time ({proposed:ddd, MMM d yyyy h:mm tt}). Reschedule was not saved."
                    : $"{outcome.Notes} | Past time rejected.");
        }

        if ((!outcome.IsBooked || outcome.StartsAt == null)
            && TryExtractCollectedAppointmentSlot(outcome.Notes, out var collectedSlot, out var collectedEnd))
        {
            outcome = new BookingOutcome(
                true,
                collectedSlot,
                collectedEnd,
                VoiceOutboundCallStatuses.Booked,
                outcome.Notes);
        }

        call.UpdatedAt = DateTime.UtcNow;
        call.CompletedAt = DateTime.UtcNow;
        call.OutcomeNotes = Truncate(outcome.Notes, 2000);

        var doctorName = await _db.Doctors.AsNoTracking()
            .Where(d => d.Id == call.DoctorId)
            .Select(d => d.Name)
            .FirstOrDefaultAsync(cancellationToken) ?? "your dentist";

        DateTime? oldStartsAt = null;
        if (call.AppointmentId is int existingId)
        {
            oldStartsAt = await _db.Appointments.AsNoTracking()
                .Where(a => a.Id == existingId)
                .Select(a => (DateTime?)a.StartsAt)
                .FirstOrDefaultAsync(cancellationToken);
        }

        if (!outcome.IsBooked || outcome.StartsAt == null)
        {
            call.Status = outcome.StatusHint ?? VoiceOutboundCallStatuses.Completed;
            await _db.SaveChangesAsync(cancellationToken);

            const string failTitle = "Reschedule update";
            var failBody = outcome.StatusHint switch
            {
                VoiceOutboundCallStatuses.Declined =>
                    "The office could not reschedule the appointment on the call. Please contact them directly.",
                VoiceOutboundCallStatuses.NoAnswer =>
                    "Nuvi couldn't reach the office to reschedule. Please try again or call the office directly.",
                VoiceOutboundCallStatuses.NoSlot =>
                    "The office didn't have a new time available in your window. Please try again or call them directly.",
                _ => "Nuvi couldn't confirm a new appointment time. Please contact the office directly."
            };

            if (ShouldAttemptIntentRetry(call.Status))
            {
                await NotifyIntentCallFailureAsync(
                    call, failTitle, failBody, cancellationToken,
                    oldStartsAt, doctorName);
            }
            else
            {
                await DispatchIntentOutcomeAsync(
                    call,
                    call.Status,
                    failTitle,
                    failBody,
                    oldStartsAt,
                    oldStartsAt?.AddHours(1),
                    doctorName,
                    cancellationToken);
            }

            return true;
        }

        var newStartsAt = NormalizeToClinicLocal(outcome.StartsAt.Value);
        if (call.PatientId is not > 0 || call.AppointmentId is not > 0)
        {
            call.Status = VoiceOutboundCallStatuses.Failed;
            call.OutcomeNotes = Truncate(
                "Reschedule call succeeded but appointment/patient link was missing.", 2000);
            await _db.SaveChangesAsync(cancellationToken);
            return true;
        }

        var update = await _appointments.RescheduleAsPatientAsync(
            call.PatientId.Value,
            call.AppointmentId.Value,
            newStartsAt,
            cancellationToken);
        if (!update.Success)
        {
            call.Status = VoiceOutboundCallStatuses.Failed;
            call.OutcomeNotes = Truncate(
                $"Reschedule confirmed on call but DB update failed: {update.Error}", 2000);
            await _db.SaveChangesAsync(cancellationToken);
            _logger.LogInformation(
                "Reschedule call succeeded but status update failed for appointment {AppointmentId}: {Error}",
                call.AppointmentId, update.Error);
        }
        else
        {
            call.Status = VoiceOutboundCallStatuses.Booked;
            await _db.SaveChangesAsync(cancellationToken);
        }

        const string title = "Appointment rescheduled";
        var body = FormatRescheduleSuccessChat(newStartsAt);

        await DispatchIntentOutcomeAsync(
            call,
            VoiceOutboundCallStatuses.Booked,
            title,
            body,
            newStartsAt,
            newStartsAt.AddHours(1),
            doctorName,
            cancellationToken,
            notificationType: PatientNotificationTypes.AppointmentUpdate);

        _logger.LogInformation(
            "Voice reschedule saved. Conversation={ConversationId} Appointment={AppointmentId} NewStartsAt={StartsAt}",
            conversationId, call.AppointmentId, newStartsAt);
        return true;
    }

    private async Task<bool> ProcessCancelConversationPayloadAsync(
        VoiceOutboundCall call,
        string conversationId,
        JsonElement data,
        CancellationToken cancellationToken)
    {
        if (call.Status is VoiceOutboundCallStatuses.Canceled
            or VoiceOutboundCallStatuses.Declined
            or VoiceOutboundCallStatuses.Failed
            or VoiceOutboundCallStatuses.NoAnswer
            or VoiceOutboundCallStatuses.Completed)
            return true;

        var convStatus = data.TryGetProperty("status", out var statusEl) ? statusEl.GetString() : null;
        if (ShouldContinuePollingConversation(data, call, convStatus, out var staleBooking))
            return false;

        if (staleBooking != null)
        {
            call.Status = VoiceOutboundCallStatuses.NoAnswer;
            call.OutcomeNotes = Truncate(staleBooking.Notes ?? "The office did not answer the cancellation call.", 2000);
            call.UpdatedAt = DateTime.UtcNow;
            call.CompletedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(cancellationToken);

            await NotifyIntentCallFailureAsync(
                call,
                "Cancel update",
                "Nuvi couldn't reach the office to cancel. Please try again or call the office directly.",
                cancellationToken);
            return true;
        }

        var outcome = ExtractCancelOutcome(data);
        call.UpdatedAt = DateTime.UtcNow;
        call.CompletedAt = DateTime.UtcNow;
        call.OutcomeNotes = Truncate(outcome.Notes, 2000);

        var doctorName = await _db.Doctors.AsNoTracking()
            .Where(d => d.Id == call.DoctorId)
            .Select(d => d.Name)
            .FirstOrDefaultAsync(cancellationToken) ?? "your dentist";

        DateTime? appointmentStartsAt = null;
        if (call.AppointmentId is int apptId)
        {
            appointmentStartsAt = await _db.Appointments.AsNoTracking()
                .Where(a => a.Id == apptId)
                .Select(a => (DateTime?)a.StartsAt)
                .FirstOrDefaultAsync(cancellationToken);
        }

        if (outcome.IsCanceled)
        {
            if (call.PatientId is > 0 && call.AppointmentId is > 0)
            {
                var update = await _appointments.UpdateStatusAsPatientAsync(
                    call.PatientId.Value,
                    call.AppointmentId.Value,
                    AppointmentStatuses.PatientCanceled,
                    cancellationToken);
                if (!update.Success)
                {
                    _logger.LogInformation(
                        "Cancel call succeeded but status update failed for appointment {AppointmentId}: {Error}",
                        call.AppointmentId, update.Error);
                }
            }

            call.Status = VoiceOutboundCallStatuses.Canceled;
            await _db.SaveChangesAsync(cancellationToken);

            const string title = "Appointment canceled";
            var body = FormatCancelSuccessNewBookingChat();
            await MarkPostCancelNewBookingOfferAsync(call, cancellationToken);

            await DispatchIntentOutcomeAsync(
                call,
                VoiceOutboundCallStatuses.Canceled,
                title,
                body,
                appointmentStartsAt,
                appointmentStartsAt?.AddHours(1),
                doctorName,
                cancellationToken,
                notificationType: PatientNotificationTypes.AppointmentCanceled,
                chatOptions: NuviFlowContent.YesNoOptions,
                optionsOnly: true);

            _logger.LogInformation(
                "Voice cancel confirmed. Conversation={ConversationId} Appointment={AppointmentId}",
                conversationId, call.AppointmentId);
            return true;
        }

        call.Status = outcome.StatusHint ?? VoiceOutboundCallStatuses.Completed;
        await _db.SaveChangesAsync(cancellationToken);

        const string failTitle = "Cancellation update";
        var failBody = outcome.StatusHint switch
        {
            VoiceOutboundCallStatuses.Declined =>
                "The office could not cancel the appointment on the call. Please contact the office directly.",
            VoiceOutboundCallStatuses.NoAnswer =>
                "Nuvi couldn't reach the office to cancel. Please try again or call the office directly.",
            _ => string.IsNullOrWhiteSpace(outcome.Notes)
                ? "Nuvi couldn't confirm the cancellation with the office. Please contact them directly."
                : outcome.Notes!
        };

        if (ShouldAttemptIntentRetry(call.Status))
        {
            await NotifyIntentCallFailureAsync(
                call, failTitle, failBody, cancellationToken,
                appointmentStartsAt, doctorName);
        }
        else
        {
            await DispatchIntentOutcomeAsync(
                call,
                call.Status,
                failTitle,
                failBody,
                appointmentStartsAt,
                appointmentStartsAt?.AddHours(1),
                doctorName,
                cancellationToken);
        }

        return true;
    }

    private static CancelOutcome ExtractCancelOutcome(JsonElement data)
    {
        var notes = new List<string>();
        var searchable = new List<string>();
        string? statusHint = null;
        var canceled = false;

        if (data.TryGetProperty("analysis", out var analysis))
        {
            if (analysis.TryGetProperty("transcript_summary", out var summary)
                && summary.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(summary.GetString()))
            {
                notes.Add(summary.GetString()!);
                searchable.Add(summary.GetString()!);
            }
        }

        if (data.TryGetProperty("analysis", out var analysis2)
            && analysis2.TryGetProperty("data_collection_results", out var dcr))
        {
            foreach (var (id, value) in EnumerateDataCollectionEntries(dcr))
            {
                if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(value) || value == "null")
                    continue;

                var idLower = id.ToLowerInvariant();
                var valueClean = value.Trim().Trim('"');
                var valueLower = valueClean.ToLowerInvariant();
                searchable.Add($"{id}={valueClean}");
                notes.Add($"{id}={valueClean}");

                if (idLower is "status" or "outcome" or "booking_status" or "cancel_status")
                {
                    statusHint = MapCancelStatusHint(valueLower);
                    if (valueLower is "canceled" or "cancelled" or "cancel" or "success")
                        canceled = true;
                }
            }
        }

        if (data.TryGetProperty("transcript", out var transcript) && transcript.ValueKind == JsonValueKind.Array)
        {
            foreach (var turn in transcript.EnumerateArray())
            {
                if (turn.TryGetProperty("message", out var msg)
                    && msg.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(msg.GetString()))
                    searchable.Add(msg.GetString()!);
            }
        }

        var joinedNotes = notes.Count > 0 ? string.Join(" | ", notes) : null;
        if (!canceled && !string.IsNullOrWhiteSpace(joinedNotes))
        {
            var n = joinedNotes.ToLowerInvariant();
            if (n.Contains("cancel") && (n.Contains("confirm") || n.Contains("all set") || n.Contains("done")))
                canceled = true;
        }

        if (!canceled && searchable.Count > 0)
        {
            foreach (var blob in searchable)
            {
                var b = blob.ToLowerInvariant();
                if (b.Contains("appointment canceled") || b.Contains("appointment cancelled")
                    || b.Contains("cancellation confirmed") || b.Contains("has been canceled")
                    || b.Contains("has been cancelled"))
                {
                    canceled = true;
                    statusHint ??= VoiceOutboundCallStatuses.Canceled;
                    break;
                }
            }
        }

        return new CancelOutcome(canceled, statusHint, joinedNotes);
    }

    private static string? MapCancelStatusHint(string valueLower) => valueLower switch
    {
        "canceled" or "cancelled" or "cancel" or "success" => VoiceOutboundCallStatuses.Canceled,
        "declined" or "dnc" => VoiceOutboundCallStatuses.Declined,
        "no_answer" or "voicemail" or "noanswer" => VoiceOutboundCallStatuses.NoAnswer,
        "failed" => VoiceOutboundCallStatuses.Failed,
        _ => VoiceOutboundCallStatuses.Completed
    };

    private sealed record CancelOutcome(bool IsCanceled, string? StatusHint, string? Notes);

    private async Task<VoiceOutboundCall?> TryCreateCallFromPayloadAsync(
        string conversationId,
        JsonElement data,
        CancellationToken cancellationToken)
    {
        var vars = ExtractDynamicVariables(data);
        if (!vars.TryGetValue("session_key", out var sessionKeyRaw)
            && !vars.TryGetValue("external_call_id", out sessionKeyRaw))
            return null;

        if (!Guid.TryParse(sessionKeyRaw, out var sessionKey))
            return null;

        var session = await _db.SearchSessions.AsNoTracking()
            .FirstOrDefaultAsync(s => s.SessionKey == sessionKey, cancellationToken);
        if (session == null)
            return null;

        // Prefer selected doctor from search context if present.
        var doctorId = 0;
        if (!string.IsNullOrWhiteSpace(session.SearchContextJson))
        {
            try
            {
                using var ctxDoc = JsonDocument.Parse(session.SearchContextJson);
                if (ctxDoc.RootElement.TryGetProperty("selectedDoctorId", out var sel)
                    && sel.TryGetInt32(out var sid))
                    doctorId = sid;
                else if (ctxDoc.RootElement.TryGetProperty("matchedDoctorIds", out var list)
                         && list.ValueKind == JsonValueKind.Array
                         && list.GetArrayLength() > 0
                         && list[0].TryGetInt32(out var first))
                    doctorId = first;
            }
            catch
            {
                // ignore
            }
        }

        if (doctorId <= 0)
            return null;

        vars.TryGetValue("patient_name", out var patientName);
        vars.TryGetValue("patient_phone", out var patientPhone);
        vars.TryGetValue("patient_email", out var patientEmail);
        vars.TryGetValue("appointment_type", out var visitReason);
        vars.TryGetValue("chief_complaint", out visitReason);

        var now = DateTime.UtcNow;
        var call = new VoiceOutboundCall
        {
            ConversationId = conversationId,
            SessionKey = sessionKey,
            SearchSessionId = session.Id,
            PatientId = session.PatientId,
            DoctorId = doctorId,
            PatientName = string.IsNullOrWhiteSpace(patientName) ? "Patient" : patientName,
            PatientPhone = patientPhone,
            PatientEmail = patientEmail,
            VisitReason = visitReason,
            Status = VoiceOutboundCallStatuses.Initiated,
            CreatedAt = now,
            UpdatedAt = now
        };
        _db.VoiceOutboundCalls.Add(call);
        await _db.SaveChangesAsync(cancellationToken);
        return call;
    }

    private static bool ShouldAttemptBookCascade(string status) =>
        status is VoiceOutboundCallStatuses.NoAnswer
            or VoiceOutboundCallStatuses.Failed
            or VoiceOutboundCallStatuses.NoSlot
            or VoiceOutboundCallStatuses.Declined
            or VoiceOutboundCallStatuses.Completed;

    private async Task NotifyBookFailureAsync(
        VoiceOutboundCall call,
        string defaultTitle,
        string defaultBody,
        CancellationToken cancellationToken)
    {
        var practiceLabel = await ResolvePracticeLabelAsync(call.DoctorId, cancellationToken);
        var willRetrySameDoctor = await WillRetrySameDoctorAsync(call, cancellationToken);
        var retryDelayMinutes = FormatRetryDelayMinutes(_elevenLabs.CallRetryDelaySeconds);
        var failureChat = FormatBookCallOutcomeChat(
            practiceLabel,
            call.Status,
            willRetrySameDoctor,
            retryDelayMinutes);
        await AppendCallChatMessageAsync(call, failureChat, cancellationToken);
        await PushCallChatUpdateAsync(call, failureChat, cancellationToken);

        if (ShouldAttemptBookCascade(call.Status))
        {
            var cascade = await _cascade.TryCallNextDoctorAsync(call, cancellationToken);
            if (cascade.NextCallStarted || cascade.RetryScheduled || cascade.AllDoctorsExhausted)
            {
                var title = cascade.NotificationTitle ?? defaultTitle;
                var body = cascade.NotificationBody ?? defaultBody;
                int? notificationId = null;
                if (call.PatientId is > 0)
                {
                    notificationId = await AddNotificationAsync(
                        call.PatientId.Value,
                        PatientNotificationTypes.VoiceCallUpdate,
                        title,
                        body,
                        doctorId: cascade.NextDoctorId ?? call.DoctorId,
                        cancellationToken: cancellationToken);
                }

                await DispatchTerminalPushAsync(
                    call,
                    call.Status,
                    title,
                    body,
                    notificationId,
                    chatMessage: null,
                    cancellationToken: cancellationToken);
                return;
            }
        }

        int? fallbackNotificationId = null;
        if (call.PatientId is > 0)
        {
            fallbackNotificationId = await AddNotificationAsync(
                call.PatientId.Value,
                PatientNotificationTypes.VoiceCallUpdate,
                defaultTitle,
                defaultBody,
                doctorId: call.DoctorId,
                cancellationToken: cancellationToken);
        }

        await DispatchTerminalPushAsync(
            call,
            call.Status,
            defaultTitle,
            defaultBody,
            fallbackNotificationId,
            chatMessage: null,
            cancellationToken: cancellationToken);
    }

    private async Task<int> AddNotificationAsync(
        int patientId,
        string type,
        string title,
        string body,
        int? appointmentId = null,
        int? doctorId = null,
        CancellationToken cancellationToken = default)
    {
        var row = new PatientNotification
        {
            PatientId = patientId,
            Type = type,
            Title = title,
            Body = body,
            AppointmentId = appointmentId,
            DoctorId = doctorId,
            IsRead = false,
            CreatedAt = DateTime.UtcNow
        };
        _db.PatientNotifications.Add(row);
        await _db.SaveChangesAsync(cancellationToken);
        return row.Id;
    }

    private async Task DispatchTerminalPushAsync(
        VoiceOutboundCall call,
        string status,
        string title,
        string body,
        int? notificationId = null,
        DateTime? startsAt = null,
        DateTime? endsAt = null,
        string? doctorName = null,
        string? chatMessage = null,
        string? notificationType = null,
        IReadOnlyList<string>? chatOptions = null,
        bool optionsOnly = false,
        CancellationToken cancellationToken = default)
    {
        string? resolvedDoctorName = doctorName;
        if (string.IsNullOrWhiteSpace(resolvedDoctorName) && call.DoctorId > 0)
        {
            resolvedDoctorName = await _db.Doctors.AsNoTracking()
                .Where(d => d.Id == call.DoctorId)
                .Select(d => d.Name)
                .FirstOrDefaultAsync(cancellationToken);
        }

        string? slotLabel = null;
        if (startsAt is DateTime start)
        {
            var end = endsAt ?? start.AddHours(1);
            slotLabel = FormatPstSlot(start, end);
        }

        var pushType = notificationType ?? status switch
        {
            VoiceOutboundCallStatuses.Booked => PatientNotificationTypes.AppointmentBooked,
            VoiceOutboundCallStatuses.Canceled => PatientNotificationTypes.AppointmentCanceled,
            _ => PatientNotificationTypes.VoiceCallUpdate
        };

        var liveSession = IsCancelIntent(call.CallIntent) || IsRescheduleIntent(call.CallIntent)
            ? await ResolveLiveChatSessionAsync(call, cancellationToken)
            : null;

        await _push.DispatchAsync(new PatientPushMessage
        {
            Type = pushType,
            PatientId = call.PatientId,
            SessionKey = liveSession?.SessionKey ?? call.SessionKey,
            ConversationId = call.ConversationId,
            Status = status,
            Title = title,
            Body = body,
            ChatMessage = chatMessage,
            DoctorId = call.DoctorId,
            DoctorName = resolvedDoctorName,
            AppointmentId = call.AppointmentId,
            StartsAt = startsAt,
            EndsAt = endsAt ?? (startsAt?.AddHours(1)),
            SlotLabel = slotLabel,
            NotificationId = notificationId,
            ChatOptions = chatOptions,
            OptionsOnly = optionsOnly
        }, cancellationToken);
    }

    private static BookingOutcome ExtractBookingOutcome(JsonElement data)
    {
        var notes = new List<string>();
        var searchable = new List<string>();
        string? dateOnlyRaw = null;
        string? timeOnlyRaw = null;

        if (data.TryGetProperty("analysis", out var analysis))
        {
            if (analysis.TryGetProperty("transcript_summary", out var summary)
                && summary.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(summary.GetString()))
            {
                notes.Add(summary.GetString()!);
                searchable.Add(summary.GetString()!);
            }

            if (analysis.TryGetProperty("call_successful", out var ok)
                && ok.ValueKind == JsonValueKind.String)
                notes.Add($"call_successful={ok.GetString()}");
        }

        string? statusHint = null;
        DateTime? startsAt = null;
        DateTime? endsAt = null;
        var booked = false;
        string? endTimeRaw = null;

        // ElevenLabs format examples (preferred):
        //   status + appointment_date + appointment_time
        // Legacy still accepted: appointment_start_time / appointment_end_time / appointment_datetime
        // All Nuvi times are Pacific (PST/PDT) wall-clock.
        if (data.TryGetProperty("analysis", out var analysis2)
            && analysis2.TryGetProperty("data_collection_results", out var dcr))
        {
            foreach (var (id, value) in EnumerateDataCollectionEntries(dcr))
            {
                if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(value) || value == "null")
                    continue;

                var idLower = id.ToLowerInvariant();
                var valueClean = value.Trim().Trim('"');
                var valueLower = valueClean.ToLowerInvariant();
                searchable.Add($"{id}={valueClean}");
                notes.Add($"{id}={valueClean}");

                if (idLower is "status" or "outcome" or "booking_status")
                {
                    statusHint = MapStatusHint(valueLower);
                    if (valueLower is "booked" or "confirmed" or "success" or "scheduled" or "rescheduled")
                        booked = true;
                }

                // Preferred split fields: appointment_date + appointment_time.
                if (idLower is "appointment_date" or "date" or "day" or "appointment_day")
                {
                    dateOnlyRaw ??= valueClean;
                    if (LooksLikeDateWithoutTime(valueClean) || TryParseAppointmentSlot(valueClean, out _, out _))
                        booked = true;
                    continue;
                }

                if (idLower is "appointment_time" or "appointment_start_time" or "start_time"
                    or "slot_time" or "appointment_start" or "time")
                {
                    // Clock time only (e.g. "10:00 AM"). Combined with appointment_date below.
                    if (IsTimeOnly(valueClean) || !TryParseAppointmentSlot(valueClean, out _, out _))
                    {
                        timeOnlyRaw ??= valueClean;
                        booked = true;
                        continue;
                    }

                    // Rare: field contains a full date+time — accept it.
                    if (TryParseAppointmentSlot(valueClean, out var fullFromTime, out var fullEnd))
                    {
                        startsAt ??= fullFromTime;
                        endsAt ??= fullEnd;
                        booked = true;
                    }
                    continue;
                }

                if (idLower is "appointment_end_time" or "end_time" or "appointment_end")
                {
                    endTimeRaw ??= valueClean;
                    continue;
                }

                // Combined datetime (legacy / fallback) — not appointment_time (that is clock-only).
                if (idLower is "appointment_datetime" or "booked_datetime"
                    or "starts_at" or "scheduled_at" or "booking_time"
                    or "time_slot" or "appointment_slot" or "confirmed_datetime" or "slot")
                {
                    if (TryParseAppointmentSlot(valueClean, out var dt, out var slotEnd))
                    {
                        startsAt = dt;
                        endsAt = slotEnd ?? endsAt;
                        booked = true;
                    }
                    else if (IsTimeOnly(valueClean))
                    {
                        timeOnlyRaw ??= valueClean;
                        booked = true;
                    }
                    else if (LooksLikeDateWithoutTime(valueClean))
                    {
                        dateOnlyRaw ??= valueClean;
                        booked = true;
                    }
                }
            }
        }

        // Tool results / tool_calls — end_call usually only has reason/message (custom fields may be inside those strings).
        if (data.TryGetProperty("transcript", out var transcript) && transcript.ValueKind == JsonValueKind.Array)
        {
            foreach (var turn in transcript.EnumerateArray())
            {
                if (turn.TryGetProperty("message", out var msg)
                    && msg.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(msg.GetString()))
                    searchable.Add(msg.GetString()!);

                if (turn.TryGetProperty("tool_calls", out var calls) && calls.ValueKind == JsonValueKind.Array)
                {
                    foreach (var call in calls.EnumerateArray())
                    {
                        var toolName = call.TryGetProperty("tool_name", out var tn) ? tn.GetString()
                            : call.TryGetProperty("name", out var n) ? n.GetString() : null;
                        var paramsJson = call.TryGetProperty("params_as_json", out var p)
                            ? (p.ValueKind == JsonValueKind.String ? p.GetString() : p.ToString())
                            : call.TryGetProperty("arguments", out var a)
                                ? (a.ValueKind == JsonValueKind.String ? a.GetString() : a.ToString())
                                : null;
                        ApplyEndCallPayload(toolName, paramsJson, ref booked, ref statusHint, ref startsAt, ref endsAt, searchable);
                    }
                }

                if (turn.TryGetProperty("tool_results", out var tools) && tools.ValueKind == JsonValueKind.Array)
                {
                    foreach (var tool in tools.EnumerateArray())
                    {
                        var toolName = tool.TryGetProperty("tool_name", out var tn) ? tn.GetString() : null;
                        var resultValue = tool.TryGetProperty("result_value", out var rv) ? rv.GetString()
                            : tool.TryGetProperty("result", out var r)
                                ? (r.ValueKind == JsonValueKind.String ? r.GetString() : r.ToString())
                                : null;
                        ApplyEndCallPayload(toolName, resultValue, ref booked, ref statusHint, ref startsAt, ref endsAt, searchable);
                    }
                }
            }
        }

        ApplyTelephonySignals(data, notes, ref statusHint);

        var joinedNotes = notes.Count > 0 ? string.Join(" | ", notes) : null;
        if (!booked && !string.IsNullOrWhiteSpace(joinedNotes))
        {
            var n = joinedNotes.ToLowerInvariant();
            if (n.Contains("booked") || n.Contains("scheduled") || n.Contains("rescheduled")
                || n.Contains("confirmed an appointment")
                || n.Contains("appointment is confirmed") || n.Contains("you're all set"))
                booked = true;
        }

        // Combine separate date + start (+ optional end) collection fields.
        // Prefer these over spoken transcript when both are present.
        var fromDataCollection = false;
        if (startsAt == null && dateOnlyRaw != null && timeOnlyRaw != null
            && TryParseAppointmentSlot($"{dateOnlyRaw} {timeOnlyRaw}", out var combined, out var combinedEnd))
        {
            startsAt = combined;
            endsAt = combinedEnd ?? endsAt;
            fromDataCollection = true;
        }
        else if (startsAt == null && dateOnlyRaw != null && timeOnlyRaw != null
                 && TryParseAppointmentDateTimeCore(
                     NormalizeSpokenDateTimeText($"{dateOnlyRaw} {NormalizeAmPm(timeOnlyRaw)}"),
                     out var looseCombined))
        {
            startsAt = looseCombined;
            fromDataCollection = true;
        }

        if (startsAt != null && endTimeRaw != null
            && TryParseAppointmentDateTimeCore($"{startsAt:yyyy-MM-dd} {NormalizeAmPm(endTimeRaw)}", out var parsedEnd))
        {
            endsAt = parsedEnd;
        }
        else if (startsAt != null && endTimeRaw != null
                 && TryParseAppointmentSlot($"{dateOnlyRaw ?? startsAt.Value.ToString("yyyy-MM-dd")} {endTimeRaw}", out var endSlot, out _))
        {
            endsAt = endSlot;
        }

        // Scan spoken transcript / end_call reason for confirmations.
        // Prefer the best slot (real clock time), not the first weak/midnight match.
        // Skip when ElevenLabs data-collection already provided a concrete slot.
        if (!fromDataCollection && (startsAt == null || IsLikelyMidnightPlaceholder(startsAt.Value)))
        {
            DateTime? bestStart = null;
            DateTime? bestEnd = null;
            var bestScore = -1;
            for (var i = 0; i < searchable.Count; i++)
            {
                var blob = searchable[i];
                if (!(TryParseAppointmentSlot(blob, out var fromText, out var fromTextEnd)
                      || TryExtractSpokenSlot(blob, out fromText, out fromTextEnd)))
                    continue;

                var score = ScoreSlotCandidate(blob, fromText, i);
                if (score <= bestScore)
                    continue;
                bestScore = score;
                bestStart = fromText;
                bestEnd = fromTextEnd;
                if (blob.Contains("book", StringComparison.OrdinalIgnoreCase)
                    || blob.Contains("confirm", StringComparison.OrdinalIgnoreCase)
                    || blob.Contains("schedul", StringComparison.OrdinalIgnoreCase))
                    booked = true;
            }

            if (bestStart != null && !IsLikelyMidnightPlaceholder(bestStart.Value))
            {
                startsAt = bestStart;
                endsAt = bestEnd ?? endsAt;
            }
            else if (startsAt == null && bestStart != null)
            {
                startsAt = bestStart;
                endsAt = bestEnd ?? endsAt;
            }
        }

        if (booked && (startsAt == null || IsLikelyMidnightPlaceholder(startsAt.Value))
            && !string.IsNullOrWhiteSpace(joinedNotes)
            && (TryParseAppointmentSlot(joinedNotes, out var fromNotes, out var fromNotesEnd)
                || TryExtractSpokenSlot(joinedNotes, out fromNotes, out fromNotesEnd)
                || TryExtractCollectedAppointmentSlot(joinedNotes, out fromNotes, out fromNotesEnd))
            && !IsLikelyMidnightPlaceholder(fromNotes))
        {
            startsAt = fromNotes;
            endsAt = fromNotesEnd ?? endsAt;
        }

        if ((startsAt == null || IsLikelyMidnightPlaceholder(startsAt.Value))
            && TryExtractCollectedAppointmentSlot(joinedNotes, out var collectedFromNotes, out var collectedFromNotesEnd)
            && !IsLikelyMidnightPlaceholder(collectedFromNotes))
        {
            startsAt = collectedFromNotes;
            endsAt = collectedFromNotesEnd ?? endsAt;
            booked = true;
            statusHint = VoiceOutboundCallStatuses.Booked;
        }

        // Never save a midnight "placeholder" as a real dental booking time.
        if (startsAt != null && IsLikelyMidnightPlaceholder(startsAt.Value))
            startsAt = null;

        if (!booked && statusHint != null && string.IsNullOrWhiteSpace(joinedNotes))
            joinedNotes = DescribeCallOutcome(statusHint);

        return new BookingOutcome(booked, startsAt, endsAt, statusHint, joinedNotes);
    }

    /// <summary>
    /// When ElevenLabs sends no post-call analysis (declined / no-answer / very short calls),
    /// infer outcome from telephony metadata and failure_reason fields.
    /// </summary>
    private static void ApplyTelephonySignals(JsonElement data, List<string> notes, ref string? statusHint)
    {
        if (data.TryGetProperty("failure_reason", out var failureReasonEl)
            && failureReasonEl.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(failureReasonEl.GetString()))
        {
            var failureReason = failureReasonEl.GetString()!;
            notes.Add($"failure_reason={failureReason}");
            statusHint ??= MapTelephonyFailureReason(failureReason);
        }

        if (data.TryGetProperty("metadata", out var metadata) && metadata.ValueKind == JsonValueKind.Object)
        {
            if (metadata.TryGetProperty("call_duration_secs", out var durationEl)
                && durationEl.TryGetInt32(out var durationSecs))
            {
                notes.Add($"call_duration_secs={durationSecs}");
                if (statusHint == null && durationSecs <= 8 && HasMinimalConversation(data))
                    statusHint = VoiceOutboundCallStatuses.NoAnswer;
            }

            if (metadata.TryGetProperty("termination_reason", out var termEl)
                && termEl.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(termEl.GetString()))
            {
                var term = termEl.GetString()!;
                notes.Add($"termination_reason={term}");
                statusHint ??= InferStatusFromTerminationReason(term);
            }

            if (metadata.TryGetProperty("error", out var errorEl)
                && errorEl.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(errorEl.GetString()))
            {
                notes.Add($"error={errorEl.GetString()}");
                statusHint ??= VoiceOutboundCallStatuses.Failed;
            }

            if (metadata.TryGetProperty("body", out var twilioBody)
                && twilioBody.ValueKind == JsonValueKind.Object
                && twilioBody.TryGetProperty("CallStatus", out var callStatusEl)
                && callStatusEl.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(callStatusEl.GetString()))
            {
                var callStatus = callStatusEl.GetString()!;
                notes.Add($"twilio_call_status={callStatus}");
                statusHint ??= MapTelephonyFailureReason(callStatus);
            }
            else if (metadata.TryGetProperty("phone_call", out var phoneCall)
                     && phoneCall.ValueKind == JsonValueKind.Object
                     && phoneCall.TryGetProperty("status", out var phoneStatusEl)
                     && phoneStatusEl.ValueKind == JsonValueKind.String
                     && !string.IsNullOrWhiteSpace(phoneStatusEl.GetString()))
            {
                var phoneStatus = phoneStatusEl.GetString()!;
                notes.Add($"phone_call_status={phoneStatus}");
                statusHint ??= MapTelephonyFailureReason(phoneStatus);
            }
        }

        if (data.TryGetProperty("analysis", out var analysis)
            && analysis.TryGetProperty("call_successful", out var callOk)
            && callOk.ValueKind == JsonValueKind.String)
        {
            var ok = callOk.GetString()?.Trim().ToLowerInvariant();
            notes.Add($"call_successful={ok}");
            if (statusHint == null && ok is "failure" or "false" or "no" or "unsuccessful")
                statusHint = VoiceOutboundCallStatuses.Failed;
        }
    }

    private static string? InferStatusFromTelephonySignals(JsonElement data)
    {
        string? hint = null;
        var notes = new List<string>();
        ApplyTelephonySignals(data, notes, ref hint);
        return hint;
    }

    /// <summary>
    /// ElevenLabs sometimes leaves declined/no-answer calls stuck at status=initiated with no analysis.
    /// Returns true when polling should continue waiting.
    /// </summary>
    private bool ShouldContinuePollingConversation(
        JsonElement data,
        VoiceOutboundCall call,
        string? conversationStatus,
        out BookingOutcome? staleOutcome)
    {
        staleOutcome = null;
        var inProgress = string.Equals(conversationStatus, "processing", StringComparison.OrdinalIgnoreCase)
            || string.Equals(conversationStatus, "initiated", StringComparison.OrdinalIgnoreCase)
            || string.Equals(conversationStatus, "in-progress", StringComparison.OrdinalIgnoreCase);
        if (!inProgress)
            return false;

        if (TryBuildStaleUnconnectedOutcome(data, call, out var stale))
        {
            staleOutcome = stale;
            return false;
        }

        return true;
    }

    private bool TryBuildStaleUnconnectedOutcome(
        JsonElement data,
        VoiceOutboundCall call,
        out BookingOutcome outcome)
    {
        outcome = default!;

        var status = data.TryGetProperty("status", out var statusEl) ? statusEl.GetString() : null;
        if (!string.Equals(status, "initiated", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(status, "in-progress", StringComparison.OrdinalIgnoreCase))
            return false;

        var minAgeSeconds = Math.Max(0, _elevenLabs.ConversationPollingDelaySeconds);
        if (DateTime.UtcNow - call.CreatedAt < TimeSpan.FromSeconds(minAgeSeconds))
            return false;

        if (!HasMinimalConversation(data))
            return false;

        if (data.TryGetProperty("has_user_audio", out var hasUserAudio)
            && hasUserAudio.ValueKind == JsonValueKind.True)
            return false;

        var durationSecs = 0;
        var neverAccepted = true;
        if (data.TryGetProperty("metadata", out var metadata) && metadata.ValueKind == JsonValueKind.Object)
        {
            if (metadata.TryGetProperty("call_duration_secs", out var durEl) && durEl.TryGetInt32(out var d))
                durationSecs = d;

            if (metadata.TryGetProperty("accepted_time_unix_secs", out var acceptedEl)
                && acceptedEl.ValueKind == JsonValueKind.Number
                && acceptedEl.TryGetInt64(out var accepted)
                && accepted > 0)
                neverAccepted = false;
        }

        if (!neverAccepted && durationSecs > 8)
            return false;

        outcome = new BookingOutcome(
            false,
            null,
            null,
            VoiceOutboundCallStatuses.NoAnswer,
            neverAccepted
                ? "The call was not answered (never connected)."
                : "The call ended without a booking.");

        return true;
    }

    private static bool HasMinimalConversation(JsonElement data)
    {
        if (!data.TryGetProperty("transcript", out var transcript) || transcript.ValueKind != JsonValueKind.Array)
            return true;

        var spokenTurns = 0;
        foreach (var turn in transcript.EnumerateArray())
        {
            if (turn.TryGetProperty("message", out var msg)
                && msg.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(msg.GetString()))
                spokenTurns++;
        }

        return spokenTurns <= 1;
    }

    private static string InferStatusFromTerminationReason(string terminationReason)
    {
        var lower = terminationReason.ToLowerInvariant();
        if (lower.Contains("no answer", StringComparison.Ordinal)
            || lower.Contains("no_answer", StringComparison.Ordinal)
            || lower.Contains("not answer", StringComparison.Ordinal)
            || lower.Contains("voicemail", StringComparison.Ordinal))
            return VoiceOutboundCallStatuses.NoAnswer;

        if (lower.Contains("declin", StringComparison.Ordinal)
            || lower.Contains("busy", StringComparison.Ordinal)
            || lower.Contains("reject", StringComparison.Ordinal)
            || lower.Contains("dnc", StringComparison.Ordinal))
            return VoiceOutboundCallStatuses.Declined;

        if (lower.Contains("fail", StringComparison.Ordinal) || lower.Contains("error", StringComparison.Ordinal))
            return VoiceOutboundCallStatuses.Failed;

        return VoiceOutboundCallStatuses.Completed;
    }

    private static string MapTelephonyFailureReason(string? reason)
    {
        var r = (reason ?? string.Empty).Trim().ToLowerInvariant();
        return r switch
        {
            "busy" => VoiceOutboundCallStatuses.Declined,
            "no-answer" or "no_answer" or "noanswer" => VoiceOutboundCallStatuses.NoAnswer,
            "declined" or "reject" or "rejected" => VoiceOutboundCallStatuses.Declined,
            "failed" or "error" or "canceled" or "cancelled" => VoiceOutboundCallStatuses.Failed,
            _ => VoiceOutboundCallStatuses.NoAnswer
        };
    }

    private static string BuildTelephonyFailureDetail(JsonElement data, string? failureReason)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(failureReason))
            parts.Add($"failure_reason={failureReason.Trim()}");

        if (data.TryGetProperty("metadata", out var metadata) && metadata.ValueKind == JsonValueKind.Object)
        {
            if (metadata.TryGetProperty("body", out var body) && body.ValueKind == JsonValueKind.Object)
            {
                if (body.TryGetProperty("CallStatus", out var callStatus)
                    && callStatus.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(callStatus.GetString()))
                    parts.Add($"twilio_call_status={callStatus.GetString()}");

                if (body.TryGetProperty("SipResponseCode", out var sip)
                    && sip.ValueKind is JsonValueKind.String or JsonValueKind.Number)
                    parts.Add($"sip_response={sip}");
            }

            if (metadata.TryGetProperty("error", out var error)
                && error.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(error.GetString()))
                parts.Add($"error={error.GetString()}");
        }

        return parts.Count > 0 ? string.Join(" | ", parts) : "Call could not be connected.";
    }

    private static string DescribeCallOutcome(string status, string? detail = null)
    {
        var summary = status switch
        {
            VoiceOutboundCallStatuses.NoAnswer =>
                "The office did not answer the call.",
            VoiceOutboundCallStatuses.Declined =>
                "The call was declined or the line was busy.",
            VoiceOutboundCallStatuses.Failed =>
                "The call could not be completed.",
            VoiceOutboundCallStatuses.NoSlot =>
                "The office did not have an available appointment in your window.",
            VoiceOutboundCallStatuses.Completed =>
                "The call ended without a booking.",
            _ => $"Call finished with status {status}."
        };

        if (string.IsNullOrWhiteSpace(detail) || detail == summary)
            return summary;

        return $"{summary} ({detail})";
    }

    public static string FormatPracticeLabel(string? practiceName, string? doctorName)
    {
        if (!string.IsNullOrWhiteSpace(practiceName))
            return practiceName.Trim();

        if (!string.IsNullOrWhiteSpace(doctorName))
            return $"{doctorName.Trim()}'s office";

        return "the office";
    }

    public static string FormatAttemptingCallChat(string practiceLabel) =>
        $"I'm attempting to call {practiceLabel} now to book your appointment. I'll update you here as soon as I hear back.";

    public static string FormatCallingPracticeChat(string practiceLabel) =>
        $"I am calling to {practiceLabel}.";

    public static string FormatIntentNoAnswerExhaustedChat(string? callIntent, int attempts)
    {
        var n = Math.Max(1, attempts);
        var attemptWord = n == 1 ? "attempt" : "attempts";
        var action = string.Equals(callIntent, VoiceOutboundCallIntents.Cancel, StringComparison.OrdinalIgnoreCase)
            ? "cancellation"
            : "reschedule";
        return $"I tried {n} {attemptWord} but no answer. Please try later for {action}.";
    }

    public static string FormatCancelSuccessNewBookingChat() =>
        NuviFlowContent.CancelSuccessNewBookingPrompt;

    public static string FormatRescheduleSuccessChat(DateTime startsAt)
    {
        var date = startsAt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var time = startsAt.ToString("h:mm tt", CultureInfo.InvariantCulture);
        return $"Appointment successfully rescheduled to {date} at {time}.";
    }

    public static string FormatBookedCallChat(string practiceLabel, string when) =>
        $"Great news — I successfully booked your appointment with {practiceLabel} on {when}.";

    public static string FormatBookCallOutcomeChat(
        string practiceLabel,
        string status,
        bool willRetrySameDoctor = false,
        int retryDelayMinutes = 0) =>
        status switch
        {
            VoiceOutboundCallStatuses.NoAnswer =>
                willRetrySameDoctor && retryDelayMinutes > 0
                    ? $"I wasn't able to reach {practiceLabel} — the office did not answer. I'll retry after {FormatRetryDelayPhrase(retryDelayMinutes)}."
                    : $"I wasn't able to reach {practiceLabel} — the office did not answer.",
            VoiceOutboundCallStatuses.Declined =>
                $"I couldn't get through to {practiceLabel} — the call was declined or the line was busy.",
            VoiceOutboundCallStatuses.Failed =>
                willRetrySameDoctor && retryDelayMinutes > 0
                    ? $"I couldn't complete the call to {practiceLabel}. I'll retry after {FormatRetryDelayPhrase(retryDelayMinutes)}."
                    : $"I couldn't complete the call to {practiceLabel}.",
            VoiceOutboundCallStatuses.NoSlot =>
                $"{practiceLabel} answered, but they don't have an available appointment in your requested window.",
            VoiceOutboundCallStatuses.Completed =>
                $"The call with {practiceLabel} ended without booking an appointment.",
            _ => $"The call to {practiceLabel} finished without a booking."
        };

    public static int FormatRetryDelayMinutes(int delaySeconds) =>
        Math.Max(1, (int)Math.Ceiling(Math.Max(0, delaySeconds) / 60.0));

    public static string FormatRetryDelayPhrase(int delayMinutes) =>
        delayMinutes == 1 ? "1 minute" : $"{delayMinutes} minutes";

    public static string FormatIntentCallOutcomeChat(
        string practiceLabel,
        string callIntent,
        string status,
        bool willRetry = false,
        int retryDelayMinutes = 0)
    {
        var isCancel = string.Equals(callIntent, VoiceOutboundCallIntents.Cancel, StringComparison.OrdinalIgnoreCase);
        var actionVerb = isCancel ? "cancel" : "reschedule";
        return status switch
        {
            VoiceOutboundCallStatuses.NoAnswer =>
                willRetry && retryDelayMinutes > 0
                    ? $"I wasn't able to reach {practiceLabel} — the office did not answer. I'll retry calling to {actionVerb} after {FormatRetryDelayPhrase(retryDelayMinutes)}."
                    : $"I wasn't able to reach {practiceLabel} — the office did not answer.",
            VoiceOutboundCallStatuses.Failed =>
                willRetry && retryDelayMinutes > 0
                    ? $"I couldn't complete the call to {practiceLabel}. I'll retry calling to {actionVerb} after {FormatRetryDelayPhrase(retryDelayMinutes)}."
                    : $"I couldn't complete the call to {practiceLabel}.",
            VoiceOutboundCallStatuses.Declined =>
                isCancel
                    ? "The office could not cancel the appointment on the call. Please contact the office directly."
                    : "The office could not reschedule the appointment on the call. Please contact them directly.",
            VoiceOutboundCallStatuses.NoSlot =>
                "The office didn't have a new time available in your window. Please try again or call them directly.",
            VoiceOutboundCallStatuses.Completed =>
                isCancel
                    ? "Nuvi couldn't confirm the cancellation with the office. Please contact them directly."
                    : "Nuvi couldn't confirm a new appointment time. Please contact the office directly.",
            _ => isCancel
                ? "Nuvi couldn't confirm the cancellation with the office. Please contact them directly."
                : "Nuvi couldn't confirm a new appointment time. Please contact the office directly."
        };
    }

    private static bool ShouldAttemptIntentRetry(string status) =>
        status is VoiceOutboundCallStatuses.NoAnswer or VoiceOutboundCallStatuses.Failed;

    private sealed class IntentCallRetryResult
    {
        public bool Started { get; init; }
        public bool Scheduled { get; init; }
        public string? ChatMessage { get; init; }
        public string? NotificationBody { get; init; }
        public string? ConversationId { get; init; }
    }

    private async Task DispatchIntentOutcomeAsync(
        VoiceOutboundCall call,
        string status,
        string title,
        string body,
        DateTime? startsAt = null,
        DateTime? endsAt = null,
        string? doctorName = null,
        CancellationToken cancellationToken = default,
        string? notificationType = null,
        IReadOnlyList<string>? chatOptions = null,
        bool optionsOnly = false)
    {
        await AppendCallChatMessageAsync(call, body, cancellationToken);

        int? notificationId = null;
        if (call.PatientId is > 0)
        {
            notificationId = await AddNotificationAsync(
                call.PatientId.Value,
                notificationType ?? PatientNotificationTypes.VoiceCallUpdate,
                title,
                body,
                call.AppointmentId,
                call.DoctorId,
                cancellationToken);
        }

        await DispatchTerminalPushAsync(
            call,
            status,
            title,
            body,
            notificationId,
            startsAt,
            endsAt,
            doctorName,
            chatMessage: body,
            notificationType: notificationType,
            chatOptions: chatOptions,
            optionsOnly: optionsOnly,
            cancellationToken: cancellationToken);
    }

    private async Task NotifyIntentCallFailureAsync(
        VoiceOutboundCall call,
        string updateTitle,
        string finalFailureMessage,
        CancellationToken cancellationToken,
        DateTime? startsAt = null,
        string? doctorName = null)
    {
        var practiceLabel = await ResolvePracticeLabelAsync(call.DoctorId, cancellationToken);
        var willRetry = await WillRetryIntentCallAsync(call, cancellationToken);
        var retryDelayMinutes = FormatRetryDelayMinutes(_elevenLabs.CallRetryDelaySeconds);
        var failureChat = FormatIntentCallOutcomeChat(
            practiceLabel,
            call.CallIntent,
            call.Status,
            willRetry,
            retryDelayMinutes);

        if (willRetry && ShouldAttemptIntentRetry(call.Status))
        {
            await AppendCallChatMessageAsync(call, failureChat, cancellationToken);
            await PushCallChatUpdateAsync(call, failureChat, cancellationToken);

            var retry = await TryRetryIntentCallAsync(call, cancellationToken);
            if (retry.Started || retry.Scheduled)
            {
                var body = retry.NotificationBody ?? failureChat;
                int? notificationId = null;
                if (call.PatientId is > 0)
                {
                    notificationId = await AddNotificationAsync(
                        call.PatientId.Value,
                        PatientNotificationTypes.VoiceCallUpdate,
                        updateTitle,
                        body,
                        call.AppointmentId,
                        call.DoctorId,
                        cancellationToken);
                }

                await DispatchTerminalPushAsync(
                    call,
                    call.Status,
                    updateTitle,
                    body,
                    notificationId,
                    startsAt,
                    startsAt?.AddHours(1),
                    doctorName,
                    chatMessage: retry.ChatMessage,
                    cancellationToken: cancellationToken);
                return;
            }

            await DispatchIntentOutcomeAsync(
                call,
                call.Status,
                updateTitle,
                await ResolveIntentExhaustedChatAsync(call, finalFailureMessage, cancellationToken),
                startsAt,
                startsAt?.AddHours(1),
                doctorName,
                cancellationToken);
            return;
        }

        await DispatchIntentOutcomeAsync(
            call,
            call.Status,
            updateTitle,
            await ResolveIntentExhaustedChatAsync(call, failureChat, cancellationToken),
            startsAt,
            startsAt?.AddHours(1),
            doctorName,
            cancellationToken);
    }

    private async Task<string> ResolveIntentExhaustedChatAsync(
        VoiceOutboundCall call,
        string fallback,
        CancellationToken cancellationToken)
    {
        if (call.Status is not (VoiceOutboundCallStatuses.NoAnswer or VoiceOutboundCallStatuses.Failed))
            return fallback;

        var attempts = await CountIntentDialsInRetryWindowAsync(call, cancellationToken);
        return FormatIntentNoAnswerExhaustedChat(call.CallIntent, attempts);
    }

    private async Task<bool> WillRetryIntentCallAsync(
        VoiceOutboundCall call,
        CancellationToken cancellationToken)
    {
        if (call.AppointmentId is not > 0 || call.DoctorId <= 0)
            return false;

        if (call.Status is not (VoiceOutboundCallStatuses.NoAnswer or VoiceOutboundCallStatuses.Failed))
            return false;

        if (!IsCancelIntent(call.CallIntent) && !IsRescheduleIntent(call.CallIntent))
            return false;

        var maxRetries = Math.Max(0, _elevenLabs.MaxCallRetriesPerDoctor);
        if (maxRetries == 0)
            return false;

        var dialCount = await CountIntentDialsInRetryWindowAsync(call, cancellationToken);
        var willRetry = dialCount <= maxRetries;
        if (!willRetry)
        {
            _logger.LogInformation(
                "No auto-retry for {Intent} appointment {AppointmentId} — {Count} dial(s) in current window, limit {Max}",
                call.CallIntent, call.AppointmentId, dialCount, maxRetries);
        }

        return willRetry;
    }

    private Task<int> CountIntentDialsInRetryWindowAsync(
        VoiceOutboundCall call,
        CancellationToken cancellationToken)
    {
        var windowStart = IntentRetryWindowStart(call.CreatedAt);
        return _db.VoiceOutboundCalls.AsNoTracking()
            .CountAsync(c =>
                c.AppointmentId == call.AppointmentId
                && c.CallIntent == call.CallIntent
                && c.DoctorId == call.DoctorId
                && c.CreatedAt >= windowStart,
                cancellationToken);
    }

    private DateTime IntentRetryWindowStart(DateTime callCreatedAt)
    {
        var maxRetries = Math.Max(0, _elevenLabs.MaxCallRetriesPerDoctor);
        var delaySeconds = Math.Max(0, _elevenLabs.CallRetryDelaySeconds);
        var windowSeconds = Math.Max(60, (maxRetries + 1) * delaySeconds + 60);
        return callCreatedAt.AddSeconds(-windowSeconds);
    }

    private void ScheduleDelayedIntentRetry(int completedCallId, TimeSpan delay)
    {
        _logger.LogInformation(
            "Queueing intent retry for call {CallId} in {Delay}s",
            completedCallId, (int)delay.TotalSeconds);
        _retryQueue.Enqueue(new VoiceCallRetryJob
        {
            Kind = VoiceCallRetryKind.Intent,
            CompletedCallId = completedCallId,
            Delay = delay
        });
    }

    private async Task<IntentCallRetryResult> TryRetryIntentCallAsync(
        VoiceOutboundCall call,
        CancellationToken cancellationToken,
        bool skipRetryDelay = false)
    {
        if (call.AppointmentId is not > 0 || call.DoctorId <= 0)
            return new IntentCallRetryResult();

        if (!IsCancelIntent(call.CallIntent) && !IsRescheduleIntent(call.CallIntent))
            return new IntentCallRetryResult();

        var maxRetries = Math.Max(0, _elevenLabs.MaxCallRetriesPerDoctor);
        var dialCount = await CountIntentDialsInRetryWindowAsync(call, cancellationToken);

        if (dialCount > maxRetries)
            return new IntentCallRetryResult();

        var pending = await _db.VoiceOutboundCalls.AsNoTracking()
            .AnyAsync(c =>
                c.AppointmentId == call.AppointmentId
                && c.CallIntent == call.CallIntent
                && c.Status == VoiceOutboundCallStatuses.Initiated,
                cancellationToken);
        if (pending)
            return new IntentCallRetryResult();

        if (!_voiceCalling.IsConfigured)
            return new IntentCallRetryResult();

        var appointment = await _db.Appointments.AsNoTracking()
            .FirstOrDefaultAsync(a => a.Id == call.AppointmentId, cancellationToken);
        var doctor = await _db.Doctors.AsNoTracking()
            .FirstOrDefaultAsync(d => d.Id == call.DoctorId, cancellationToken);
        if (appointment == null || doctor == null)
            return new IntentCallRetryResult();

        var overrideTo = ElevenLabsTwilioCallingService.ToE164(_twilio.OutboundOverrideToNumber);
        var officePhone = ElevenLabsTwilioCallingService.ToE164(doctor.OfficePhoneNumber);
        if (string.IsNullOrWhiteSpace(officePhone))
            officePhone = await ResolveDoctorPhoneE164Async(doctor.Id, cancellationToken);
        var dialNumber = !string.IsNullOrWhiteSpace(overrideTo) ? overrideTo! : officePhone;
        if (string.IsNullOrWhiteSpace(dialNumber))
            return new IntentCallRetryResult();

        var retryDelay = TimeSpan.FromSeconds(Math.Max(0, _elevenLabs.CallRetryDelaySeconds));
        if (!skipRetryDelay && retryDelay > TimeSpan.Zero)
        {
            _logger.LogInformation(
                "Retry {Attempt}/{Max} for {Intent} appointment {AppointmentId} doctor {DoctorId} — scheduling redial in {Delay}s",
                dialCount + 1, maxRetries + 1, call.CallIntent, call.AppointmentId, call.DoctorId,
                _elevenLabs.CallRetryDelaySeconds);
            ScheduleDelayedIntentRetry(call.Id, retryDelay);
            var retryMinutes = FormatRetryDelayMinutes(_elevenLabs.CallRetryDelaySeconds);
            return new IntentCallRetryResult
            {
                Scheduled = true,
                NotificationBody =
                    $"I'll retry calling to {(IsCancelIntent(call.CallIntent) ? "cancel" : "reschedule")} after {FormatRetryDelayPhrase(retryMinutes)}."
            };
        }

        var slotStart = appointment.StartsAt;
        var appointmentDate = slotStart.ToString("yyyy-MM-dd");
        var appointmentTime = slotStart.ToString("h:mm tt");
        var appointmentDateTime =
            $"{slotStart:dddd, MMMM d, yyyy} at {appointmentTime} Pacific";
        var patientName = string.IsNullOrWhiteSpace(call.PatientName) ? "Patient" : call.PatientName;
        var isCancel = IsCancelIntent(call.CallIntent);
        DateOnly? patientDob = ElevenLabsTwilioCallingService.PreferPatientDateOfBirth(appointment.PatientDateOfBirth);
        if (patientDob is null && call.PatientId is > 0)
        {
            var dob = await _db.Patients.AsNoTracking()
                .Where(p => p.Id == call.PatientId.Value)
                .Select(p => (DateOnly?)p.DateOfBirth)
                .FirstOrDefaultAsync(cancellationToken);
            patientDob = ElevenLabsTwilioCallingService.PreferPatientDateOfBirth(dob);
        }

        NuviOutboundCallRequest request;
        if (isCancel)
        {
            request = new NuviOutboundCallRequest
            {
                Intent = VoiceOutboundCallIntents.Cancel,
                ToNumber = dialNumber,
                DoctorName = doctor.Name,
                PracticeName = doctor.PracticeName,
                PracticePhone = officePhone,
                PatientName = patientName,
                PatientPhone = call.PatientPhone,
                PatientEmail = call.PatientEmail,
                PatientDateOfBirth = patientDob,
                AppointmentId = appointment.Id,
                AppointmentDate = appointmentDate,
                AppointmentTime = appointmentTime,
                AppointmentDateTime = appointmentDateTime,
                AppointmentType = appointment.VisitReason,
                ChiefComplaint = appointment.VisitReason,
                CallContext = $"Cancel appointment #{appointment.Id} for {patientName}.",
                SessionKey = call.SessionKey.ToString()
            };
        }
        else
        {
            var urgency = await ResolveRescheduleUrgencyPreferenceAsync(call, cancellationToken);
            var window = AppointmentRescheduleService.BuildPacificBookingWindow(urgency);
            request = new NuviOutboundCallRequest
            {
                Intent = VoiceOutboundCallIntents.Reschedule,
                ToNumber = dialNumber,
                DoctorName = doctor.Name,
                PracticeName = doctor.PracticeName,
                PracticePhone = officePhone,
                PatientName = patientName,
                PatientPhone = call.PatientPhone,
                PatientEmail = call.PatientEmail,
                PatientDateOfBirth = patientDob,
                AppointmentId = appointment.Id,
                AppointmentDate = appointmentDate,
                AppointmentTime = appointmentTime,
                AppointmentDateTime = appointmentDateTime,
                AppointmentType = appointment.VisitReason,
                ChiefComplaint = appointment.VisitReason,
                PreferredDate = window.Phrase,
                AvailabilityWindow = window.Phrase,
                BookingWindowStart = window.StartDate,
                BookingWindowEnd = window.EndDate,
                PreferredTimeWindow = "any available time during office hours (Pacific Time)",
                CallContext =
                    $"Reschedule appointment #{appointment.Id} for {patientName}. Current slot {appointmentDateTime}. New window: {window.Phrase}.",
                SessionKey = call.SessionKey.ToString()
            };
        }

        var callResult = await _voiceCalling.PlaceOfficeCallAsync(request, cancellationToken);
        if (!callResult.Success || string.IsNullOrWhiteSpace(callResult.ConversationId))
        {
            _logger.LogInformation(
                "Intent call retry failed for appointment {AppointmentId} intent {Intent}: {Message}",
                call.AppointmentId, call.CallIntent, callResult.Message);
            return new IntentCallRetryResult();
        }

        await RecordInitiatedCallAsync(new VoiceOutboundCallRecordRequest
        {
            ConversationId = callResult.ConversationId!,
            CallSid = callResult.CallSid,
            SessionKey = call.SessionKey,
            SearchSessionId = call.SearchSessionId,
            PatientId = call.PatientId,
            DoctorId = doctor.Id,
            PatientName = patientName,
            PatientPhone = call.PatientPhone,
            PatientEmail = call.PatientEmail,
            VisitReason = appointment.VisitReason,
            ToNumber = dialNumber,
            CallIntent = call.CallIntent,
            AppointmentId = appointment.Id
        }, cancellationToken);
        ScheduleConversationPolling(callResult.ConversationId!);

        var practiceLabel = FormatPracticeLabel(doctor.PracticeName, doctor.Name);
        var chatText = FormatCallingPracticeChat(practiceLabel);
        if (!string.IsNullOrWhiteSpace(overrideTo))
            chatText += $"\n\n(Dev override: dialing {overrideTo} instead of the office number.)";

        await AppendCallChatMessageAsync(call, chatText, cancellationToken);

        _logger.LogInformation(
            "Intent call retry started for appointment {AppointmentId} intent {Intent} attempt {Attempt}/{Max} conversation {ConversationId}",
            call.AppointmentId, call.CallIntent, dialCount + 1, maxRetries + 1, callResult.ConversationId);

        return new IntentCallRetryResult
        {
            Started = true,
            ChatMessage = chatText,
            NotificationBody = chatText,
            ConversationId = callResult.ConversationId
        };
    }

    private async Task<string> ResolveRescheduleUrgencyPreferenceAsync(
        VoiceOutboundCall call,
        CancellationToken cancellationToken)
    {
        if (call.SearchSessionId is int sessionId)
        {
            var session = await _db.SearchSessions.AsNoTracking()
                .FirstOrDefaultAsync(s => s.Id == sessionId, cancellationToken);
            if (session != null)
            {
                var context = SearchContextHelper.Load(session);
                if (!string.IsNullOrWhiteSpace(context.RescheduleUrgencyPreference))
                    return context.RescheduleUrgencyPreference;
            }
        }

        return NuviFlowContent.LogisticsUrgencyOptions[1];
    }

    private async Task<string?> ResolveDoctorPhoneE164Async(int doctorId, CancellationToken cancellationToken)
    {
        var locationPhone = await _db.DoctorLocations.AsNoTracking()
            .Where(l => l.DoctorId == doctorId && l.PhoneNumber != null && l.PhoneNumber != "")
            .Select(l => l.PhoneNumber)
            .FirstOrDefaultAsync(cancellationToken);
        return ElevenLabsTwilioCallingService.ToE164(locationPhone);
    }

    private async Task<bool> WillRetrySameDoctorAsync(
        VoiceOutboundCall call,
        CancellationToken cancellationToken)
    {
        if (call.SearchSessionId is not > 0 || call.DoctorId <= 0)
            return false;

        if (call.Status is not (VoiceOutboundCallStatuses.NoAnswer or VoiceOutboundCallStatuses.Failed))
            return false;

        var maxRetries = Math.Max(0, _elevenLabs.MaxCallRetriesPerDoctor);
        if (maxRetries == 0)
            return false;

        var dialCount = await _db.VoiceOutboundCalls.AsNoTracking()
            .CountAsync(c =>
                c.SearchSessionId == call.SearchSessionId
                && c.CallIntent == VoiceOutboundCallIntents.Book
                && c.DoctorId == call.DoctorId,
                cancellationToken);

        return dialCount <= maxRetries;
    }

    private async Task<string> ResolvePracticeLabelAsync(int doctorId, CancellationToken cancellationToken)
    {
        if (doctorId <= 0)
            return "the office";

        var doctor = await _db.Doctors.AsNoTracking()
            .Where(d => d.Id == doctorId)
            .Select(d => new { d.PracticeName, d.Name })
            .FirstOrDefaultAsync(cancellationToken);

        return doctor == null
            ? "the office"
            : FormatPracticeLabel(doctor.PracticeName, doctor.Name);
    }

    private async Task MarkPostCancelNewBookingOfferAsync(
        VoiceOutboundCall call,
        CancellationToken cancellationToken)
    {
        var session = await ResolveLiveChatSessionAsync(call, cancellationToken);
        if (session == null)
            return;

        var context = SearchContextHelper.Load(session);
        context.Stage = NuviConversationStage.PostCancelNewBooking;
        context.CancelAppointmentChoices = null;
        SearchContextHelper.Save(session, context);
        session.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);
    }

    private async Task<SearchSession?> ResolveLiveChatSessionAsync(
        VoiceOutboundCall call,
        CancellationToken cancellationToken)
    {
        SearchSession? latest = null;
        if (call.PatientId is > 0)
        {
            latest = await _db.SearchSessions
                .Where(s => s.PatientId == call.PatientId)
                .OrderByDescending(s => s.UpdatedAt)
                .ThenByDescending(s => s.Id)
                .FirstOrDefaultAsync(cancellationToken);
        }

        var preferLatest = IsCancelIntent(call.CallIntent) || IsRescheduleIntent(call.CallIntent);
        if (preferLatest && latest != null)
            return latest;

        if (call.SearchSessionId is > 0)
        {
            var byId = await _db.SearchSessions
                .FirstOrDefaultAsync(s => s.Id == call.SearchSessionId.Value, cancellationToken);
            if (byId != null)
                return byId;
        }

        return latest;
    }

    private async Task AppendCallChatMessageAsync(
        VoiceOutboundCall call,
        string message,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(message))
            return;

        var session = await ResolveLiveChatSessionAsync(call, cancellationToken);
        if (session == null)
            return;

        _db.ChatMessages.Add(new ChatMessage
        {
            SearchSessionId = session.Id,
            Role = "assistant",
            Content = message,
            CreatedAt = DateTime.UtcNow
        });
        session.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);
    }

    private async Task PushCallChatUpdateAsync(VoiceOutboundCall call, string message, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(message))
            return;

        var liveSession = await ResolveLiveChatSessionAsync(call, cancellationToken);
        await _push.DispatchAsync(new PatientPushMessage
        {
            Type = PatientNotificationTypes.VoiceCallUpdate,
            PatientId = call.PatientId,
            SessionKey = liveSession?.SessionKey ?? call.SessionKey,
            ConversationId = call.ConversationId,
            Status = call.Status,
            Title = "Nuvi call update",
            Body = message,
            ChatMessage = message,
            DoctorId = call.DoctorId
        }, cancellationToken);
    }

    private static int ScoreSlotCandidate(string blob, DateTime startsAt, int index)
    {
        var score = index; // later transcript lines win ties
        if (!IsLikelyMidnightPlaceholder(startsAt))
            score += 100;
        if (startsAt.Hour is >= 7 and <= 20)
            score += 50;
        var b = blob.ToLowerInvariant();
        if (b.Contains("confirm") || b.Contains("booked") || b.Contains("perfect") || b.Contains("all set"))
            score += 40;
        if (b.Contains("p.m") || b.Contains("pm") || b.Contains("a.m") || b.Contains("am")
            || b.Contains("o'clock") || System.Text.RegularExpressions.Regex.IsMatch(b, @"\d{1,2}:\d{2}"))
            score += 30;
        return score;
    }

    private static bool IsLikelyMidnightPlaceholder(DateTime value) =>
        value.TimeOfDay == TimeSpan.Zero;

    private static void ApplyEndCallPayload(
        string? toolName,
        string? payload,
        ref bool booked,
        ref string? statusHint,
        ref DateTime? startsAt,
        ref DateTime? endsAt,
        List<string>? searchable = null)
    {
        if (string.IsNullOrWhiteSpace(payload))
            return;

        searchable?.Add(payload);

        var looksLikeEndCall = toolName?.Contains("end_call", StringComparison.OrdinalIgnoreCase) == true
            || payload.Contains("appointment_datetime", StringComparison.OrdinalIgnoreCase)
            || payload.Contains("\"status\"", StringComparison.OrdinalIgnoreCase)
            || payload.Contains("\"reason\"", StringComparison.OrdinalIgnoreCase);

        if (!looksLikeEndCall && !payload.Contains("booked", StringComparison.OrdinalIgnoreCase))
            return;

        if (payload.Contains("\"status\":\"booked\"", StringComparison.OrdinalIgnoreCase)
            || payload.Contains("\"status\": \"booked\"", StringComparison.OrdinalIgnoreCase)
            || payload.Contains("booked", StringComparison.OrdinalIgnoreCase)
            || payload.Contains("appointment confirmed", StringComparison.OrdinalIgnoreCase)
            || payload.Contains("successfully booked", StringComparison.OrdinalIgnoreCase))
        {
            booked = true;
            statusHint ??= VoiceOutboundCallStatuses.Booked;
        }

        // Pull free-text reason/message from standard end_call JSON.
        try
        {
            using var doc = JsonDocument.Parse(payload);
            foreach (var name in new[] { "reason", "message", "rationale", "summary" })
            {
                if (doc.RootElement.TryGetProperty(name, out var el)
                    && el.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(el.GetString()))
                    searchable?.Add(el.GetString()!);
            }
        }
        catch
        {
            // not JSON
        }

        if (TryExtractJsonSlot(payload, out var dt, out var slotEnd))
        {
            startsAt = dt;
            endsAt = slotEnd ?? endsAt;
        }
        else if (startsAt == null && TryParseAppointmentSlot(payload, out var loose, out var looseEnd))
        {
            startsAt = loose;
            endsAt = looseEnd ?? endsAt;
        }
        else if (startsAt == null && TryExtractSpokenSlot(payload, out var spoken, out var spokenEnd))
        {
            startsAt = spoken;
            endsAt = spokenEnd ?? endsAt;
        }
    }

    /// <summary>
    /// Pull slots from spoken confirmations, e.g. "August 10th at 9 AM" / "10th August between 1:00 p.m. to 2:00 p.m.".
    /// </summary>
    private static bool TryExtractSpokenSlot(string text, out DateTime startsAt, out DateTime? endsAt)
    {
        startsAt = default;
        endsAt = null;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var normalized = NormalizeSpokenDateTimeText(text);

        // "August 10, 2026 at 1:00 PM" / "Aug 10th at 1 PM" / "10 August 2026 between 1:00 PM to 2:00 PM"
        var m = System.Text.RegularExpressions.Regex.Match(
            normalized,
            @"(?:on\s+|for\s+|confirmed\s+(?:for\s+)?)?(?:(?:Mon|Tue|Wed|Thu|Fri|Sat|Sun)[a-z]*,?\s+)?((?:Jan(?:uary)?|Feb(?:ruary)?|Mar(?:ch)?|Apr(?:il)?|May|Jun(?:e)?|Jul(?:y)?|Aug(?:ust)?|Sep(?:t(?:ember)?)?|Oct(?:ober)?|Nov(?:ember)?|Dec(?:ember)?)\s+\d{1,2}(?:st|nd|rd|th)?(?:,?\s*\d{4})?|\d{1,2}(?:st|nd|rd|th)?\s+(?:Jan(?:uary)?|Feb(?:ruary)?|Mar(?:ch)?|Apr(?:il)?|May|Jun(?:e)?|Jul(?:y)?|Aug(?:ust)?|Sep(?:t(?:ember)?)?|Oct(?:ober)?|Nov(?:ember)?|Dec(?:ember)?)(?:,?\s*\d{4})?|\d{4}-\d{1,2}-\d{1,2}|\d{1,2}/\d{1,2}/\d{4})\s*(?:at|@|from|between)?\s*(\d{1,2}(?::\d{2})?\s*(?:[AaPp]\.?[Mm]\.?)?)(?:\s*(?:[-–—]|to|and|until)\s*(\d{1,2}(?::\d{2})?\s*(?:[AaPp]\.?[Mm]\.?)?))?",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (!m.Success)
            return false;

        var datePart = System.Text.RegularExpressions.Regex.Replace(
            m.Groups[1].Value, @"(st|nd|rd|th)", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (!System.Text.RegularExpressions.Regex.IsMatch(datePart, @"\b20\d{2}\b"))
            datePart = $"{datePart}, {ElevenLabsTwilioCallingService.GetClinicNow().Year}";

        // Day-first "10 August 2026" → "August 10, 2026"
        var dayFirst = System.Text.RegularExpressions.Regex.Match(
            datePart,
            @"^(\d{1,2})\s+(Jan(?:uary)?|Feb(?:ruary)?|Mar(?:ch)?|Apr(?:il)?|May|Jun(?:e)?|Jul(?:y)?|Aug(?:ust)?|Sep(?:t(?:ember)?)?|Oct(?:ober)?|Nov(?:ember)?|Dec(?:ember)?)(?:,?\s*(\d{4}))?",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (dayFirst.Success)
        {
            var year = dayFirst.Groups[3].Success
                ? dayFirst.Groups[3].Value
                : ElevenLabsTwilioCallingService.GetClinicNow().Year.ToString();
            datePart = $"{dayFirst.Groups[2].Value} {dayFirst.Groups[1].Value}, {year}";
        }

        var startTok = NormalizeAmPm(m.Groups[2].Value);
        var endTok = m.Groups[3].Success ? NormalizeAmPm(m.Groups[3].Value) : null;

        // If end has AM/PM but start doesn't, copy meridiem (e.g. "1 to 2 PM").
        if (endTok != null
            && !System.Text.RegularExpressions.Regex.IsMatch(startTok, @"[AaPp]\.?[Mm]")
            && System.Text.RegularExpressions.Regex.IsMatch(endTok, @"[AaPp]\.?[Mm]"))
        {
            var mer = System.Text.RegularExpressions.Regex.Match(endTok, @"[AaPp]\.?[Mm]\.?", System.Text.RegularExpressions.RegexOptions.IgnoreCase).Value;
            startTok = $"{startTok} {mer}";
        }

        if (!TryParseAppointmentDateTimeCore($"{datePart} {startTok}", out startsAt))
            return false;
        if (IsLikelyMidnightPlaceholder(startsAt))
            return false;

        if (endTok != null && TryParseAppointmentDateTimeCore($"{datePart} {endTok}", out var endDt))
            endsAt = endDt;
        else
            endsAt = startsAt.AddHours(1);

        return true;
    }

    private static string NormalizeSpokenDateTimeText(string text)
    {
        var s = text;
        // a.m. / p.m. → AM / PM
        s = System.Text.RegularExpressions.Regex.Replace(s, @"\b([AaPp])\s*\.\s*[Mm]\s*\.?", "$1M", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        s = System.Text.RegularExpressions.Regex.Replace(s, @"\b([AaPp])[Mm]\b", "$1M", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        // Ordinal day words → numbers
        s = ReplaceWord(s, "first", "1st");
        s = ReplaceWord(s, "second", "2nd");
        s = ReplaceWord(s, "third", "3rd");
        s = ReplaceWord(s, "fourth", "4th");
        s = ReplaceWord(s, "fifth", "5th");
        s = ReplaceWord(s, "sixth", "6th");
        s = ReplaceWord(s, "seventh", "7th");
        s = ReplaceWord(s, "eighth", "8th");
        s = ReplaceWord(s, "ninth", "9th");
        s = ReplaceWord(s, "tenth", "10th");
        s = ReplaceWord(s, "eleventh", "11th");
        s = ReplaceWord(s, "twelfth", "12th");
        s = ReplaceWord(s, "thirteenth", "13th");
        s = ReplaceWord(s, "fourteenth", "14th");
        s = ReplaceWord(s, "fifteenth", "15th");
        s = ReplaceWord(s, "sixteenth", "16th");
        s = ReplaceWord(s, "seventeenth", "17th");
        s = ReplaceWord(s, "eighteenth", "18th");
        s = ReplaceWord(s, "nineteenth", "19th");
        s = ReplaceWord(s, "twentieth", "20th");
        s = ReplaceWord(s, "twenty first", "21st");
        s = ReplaceWord(s, "twenty-first", "21st");
        s = ReplaceWord(s, "twenty second", "22nd");
        s = ReplaceWord(s, "twenty-second", "22nd");
        s = ReplaceWord(s, "twenty third", "23rd");
        s = ReplaceWord(s, "twenty-third", "23rd");
        s = ReplaceWord(s, "twenty fourth", "24th");
        s = ReplaceWord(s, "twenty-fourth", "24th");
        s = ReplaceWord(s, "twenty fifth", "25th");
        s = ReplaceWord(s, "twenty-fifth", "25th");
        s = ReplaceWord(s, "twenty sixth", "26th");
        s = ReplaceWord(s, "twenty-sixth", "26th");
        s = ReplaceWord(s, "twenty seventh", "27th");
        s = ReplaceWord(s, "twenty-seventh", "27th");
        s = ReplaceWord(s, "twenty eighth", "28th");
        s = ReplaceWord(s, "twenty-eighth", "28th");
        s = ReplaceWord(s, "twenty ninth", "29th");
        s = ReplaceWord(s, "twenty-ninth", "29th");
        s = ReplaceWord(s, "thirtieth", "30th");
        s = ReplaceWord(s, "thirty first", "31st");
        s = ReplaceWord(s, "thirty-first", "31st");

        // Clock words → numeric
        s = System.Text.RegularExpressions.Regex.Replace(
            s,
            @"\b(one|two|three|four|five|six|seven|eight|nine|ten|eleven|twelve|noon|midnight)\b(?:\s*o'?clock)?",
            m => m.Groups[1].Value.ToLowerInvariant() switch
            {
                "one" => "1",
                "two" => "2",
                "three" => "3",
                "four" => "4",
                "five" => "5",
                "six" => "6",
                "seven" => "7",
                "eight" => "8",
                "nine" => "9",
                "ten" => "10",
                "eleven" => "11",
                "twelve" => "12",
                "noon" => "12:00 PM",
                "midnight" => "12:00 AM",
                _ => m.Value
            },
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        // Spoken minutes after an hour: "11 thirty" → "11:30", "11 forty five" → "11:45"
        s = System.Text.RegularExpressions.Regex.Replace(
            s,
            @"\b(\d{1,2})\s+(o'?\s*clock|oh\s*clock)\b",
            "$1:00",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        s = System.Text.RegularExpressions.Regex.Replace(
            s,
            @"\b(\d{1,2})\s+(zero|oh)\s+(zero|oh)?\b",
            "$1:00",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        s = System.Text.RegularExpressions.Regex.Replace(
            s,
            @"\b(\d{1,2})\s+fifteen\b",
            "$1:15",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        s = System.Text.RegularExpressions.Regex.Replace(
            s,
            @"\b(\d{1,2})\s+thirty\b",
            "$1:30",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        s = System.Text.RegularExpressions.Regex.Replace(
            s,
            @"\b(\d{1,2})\s+forty[- ]?five\b",
            "$1:45",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        return s;
    }

    private static string ReplaceWord(string input, string word, string replacement) =>
        System.Text.RegularExpressions.Regex.Replace(
            input, $@"\b{word}\b", replacement, System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private static string NormalizeAmPm(string token) =>
        System.Text.RegularExpressions.Regex.Replace(
            token.Trim(),
            @"([AaPp])\s*\.?\s*[Mm]\.?",
            "$1M",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private static bool LooksLikeDateWithoutTime(string text) =>
        System.Text.RegularExpressions.Regex.IsMatch(
            text.Trim(),
            @"^(?:\d{4}-\d{1,2}-\d{1,2}|(?:Jan|Feb|Mar|Apr|May|Jun|Jul|Aug|Sep|Oct|Nov|Dec)[a-z]*\s+\d{1,2}(?:st|nd|rd|th)?(?:,?\s*\d{4})?|\d{1,2}(?:st|nd|rd|th)?\s+(?:Jan|Feb|Mar|Apr|May|Jun|Jul|Aug|Sep|Oct|Nov|Dec)[a-z]*(?:,?\s*\d{4})?|\d{1,2}/\d{1,2}/\d{4}|\d{1,2}/[A-Za-z]{3,9}/\d{4})$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>
    /// Yields (fieldId, value) pairs. Object-map keys are the field ids (ElevenLabs format).
    /// </summary>
    private static IEnumerable<(string Id, string Value)> EnumerateDataCollectionEntries(JsonElement dcr)
    {
        if (dcr.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in dcr.EnumerateArray())
            {
                var id = item.TryGetProperty("data_collection_id", out var idEl) ? idEl.GetString()
                    : item.TryGetProperty("id", out var idEl2) ? idEl2.GetString() : null;
                var value = ExtractDataCollectionValue(item);
                if (!string.IsNullOrWhiteSpace(id) && value != null)
                    yield return (id, value);
            }

            yield break;
        }

        if (dcr.ValueKind != JsonValueKind.Object)
            yield break;

        foreach (var prop in dcr.EnumerateObject())
        {
            var value = prop.Value.ValueKind == JsonValueKind.Object
                ? ExtractDataCollectionValue(prop.Value)
                : prop.Value.ValueKind == JsonValueKind.String
                    ? prop.Value.GetString()
                    : prop.Value.ToString();
            if (value != null)
                yield return (prop.Name, value);
        }
    }

    private static string? ExtractDataCollectionValue(JsonElement item)
    {
        if (item.TryGetProperty("value", out var valEl))
        {
            if (valEl.ValueKind == JsonValueKind.String)
            {
                var s = valEl.GetString();
                if (!string.IsNullOrWhiteSpace(s) && !string.Equals(s, "null", StringComparison.OrdinalIgnoreCase))
                    return s;
            }
            else if (valEl.ValueKind is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False)
                return valEl.ToString();
        }

        // Sometimes the model puts the useful text in rationale instead of value.
        // Only accept short rationales — long prose is analysis text, not the field value.
        if (item.TryGetProperty("rationale", out var rationale)
            && rationale.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(rationale.GetString()))
        {
            var r = rationale.GetString()!.Trim();
            if (r.Length <= 64)
                return r;
        }

        return null;
    }

    private static string? MapStatusHint(string valueLower) => valueLower switch
    {
        "booked" or "confirmed" or "scheduled" or "rescheduled" or "success" => VoiceOutboundCallStatuses.Booked,
        "canceled" or "cancelled" or "cancel" => VoiceOutboundCallStatuses.Canceled,
        "no_slot" or "no slot" or "unavailable" => VoiceOutboundCallStatuses.NoSlot,
        "declined" or "dnc" => VoiceOutboundCallStatuses.Declined,
        "no_answer" or "voicemail" or "noanswer" => VoiceOutboundCallStatuses.NoAnswer,
        "failed" => VoiceOutboundCallStatuses.Failed,
        _ => VoiceOutboundCallStatuses.Completed
    };

    private static bool TryExtractCollectedAppointmentSlot(
        string? notes,
        out DateTime startsAt,
        out DateTime? endsAt)
    {
        startsAt = default;
        endsAt = null;
        if (string.IsNullOrWhiteSpace(notes))
            return false;

        var dateMatch = System.Text.RegularExpressions.Regex.Match(
            notes,
            @"appointment_date\s*=\s*(\d{4}-\d{2}-\d{2})",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        var timeMatch = System.Text.RegularExpressions.Regex.Match(
            notes,
            @"appointment_time\s*=\s*([^|]+)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (!dateMatch.Success || !timeMatch.Success)
            return false;

        var date = dateMatch.Groups[1].Value.Trim();
        var time = timeMatch.Groups[1].Value.Trim();
        return TryParseAppointmentSlot($"{date} {time}", out startsAt, out endsAt);
    }

    private const string TimeRangePattern =
        @"(\d{1,2}:\d{2}\s*(?:[AaPp][Mm])?|\d{1,2}\s*[AaPp][Mm])\s*(?:[-–—]|to)\s*(\d{1,2}:\d{2}\s*(?:[AaPp][Mm])?|\d{1,2}\s*[AaPp][Mm])";

    private static bool TryParseAppointmentSlot(string raw, out DateTime startsAt, out DateTime? endsAt)
    {
        startsAt = default;
        endsAt = null;
        if (string.IsNullOrWhiteSpace(raw))
            return false;

        var original = NormalizeSpokenDateTimeText(raw.Trim().Trim('"'));
        // Nuvi speaks PST only — keep range end when present ("9AM-10AM").
        TryExtractTimeRange(original, out var rangeStartToken, out var rangeEndToken);
        var text = rangeStartToken != null
            ? System.Text.RegularExpressions.Regex.Replace(
                original,
                TimeRangePattern,
                rangeStartToken,
                System.Text.RegularExpressions.RegexOptions.IgnoreCase)
            : original;

        if (IsTimeOnly(text))
            return false;

        if (!TryParseAppointmentDateTimeCore(text, out startsAt))
        {
            if (TryParseDateAndTimeRange(original, out startsAt, out endsAt))
                return true;
            return false;
        }

        // Date-only / midnight ISO strings are not a real booking slot.
        if (startsAt.TimeOfDay == TimeSpan.Zero && !HasExplicitClockTime(original))
            return false;

        if (rangeEndToken != null)
        {
            var endText = System.Text.RegularExpressions.Regex.Replace(
                original,
                TimeRangePattern,
                rangeEndToken,
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (TryParseAppointmentDateTimeCore(endText, out var endDt) ||
                TryParseAppointmentDateTimeCore($"{startsAt:yyyy-MM-dd} {rangeEndToken}", out endDt))
            {
                endsAt = endDt;
            }
        }

        endsAt ??= startsAt.AddHours(1);
        return true;
    }

    private static bool HasTimeToken(string text) =>
        System.Text.RegularExpressions.Regex.IsMatch(
            NormalizeSpokenDateTimeText(text),
            @"\d{1,2}:\d{2}\s*(?:[AaPp]M)?|\d{1,2}\s*[AaPp]M",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>
    /// True only for explicit clock times — not ISO "T00:00:00" midnight placeholders.
    /// </summary>
    private static bool HasExplicitClockTime(string text)
    {
        var n = NormalizeSpokenDateTimeText(text);
        if (System.Text.RegularExpressions.Regex.IsMatch(n, @"\b([1-9]|1[0-2])\s*[AaPp]M\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            return true;
        if (System.Text.RegularExpressions.Regex.IsMatch(n, @"\b([01]?\d|2[0-3]):[0-5]\d\s*[AaPp]M\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            return true;
        // 24h time that isn't midnight
        if (System.Text.RegularExpressions.Regex.IsMatch(n, @"\b([01]?\d|2[0-3]):[0-5]\d\b")
            && !System.Text.RegularExpressions.Regex.IsMatch(n, @"\b00:00\b|\b0:00\b"))
            return true;
        return false;
    }

    private static bool TryParseAppointmentDateTime(string raw, out DateTime startsAt) =>
        TryParseAppointmentSlot(raw, out startsAt, out _);

    private static bool TryParseAppointmentDateTimeCore(string text, out DateTime startsAt)
    {
        startsAt = default;
        text = text.Trim();
        if (string.IsNullOrWhiteSpace(text) || IsTimeOnly(text))
            return false;

        // Offset/Z → convert instant to Pacific wall-clock. No-offset → treat as PST already.
        foreach (var culture in new[] { CultureInfo.InvariantCulture, new CultureInfo("en-US") })
        {
            if (DateTimeOffset.TryParse(
                    text, culture,
                    DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.RoundtripKind,
                    out var dto)
                && HasExplicitCalendarDate(dto.DateTime, text)
                && HasExplicitOffset(text))
            {
                startsAt = DateTime.SpecifyKind(
                    TimeZoneInfo.ConvertTime(dto, GetClinicTimeZone()).DateTime,
                    DateTimeKind.Unspecified);
                return true;
            }
        }

        var formats = new[]
        {
            "yyyy-MM-dd HH:mm",
            "yyyy-MM-dd H:mm",
            "yyyy-MM-dd'T'HH:mm",
            "yyyy-MM-dd'T'HH:mm:ss",
            "yyyy-MM-dd h:mm tt",
            "yyyy-MM-dd hh:mm tt",
            "yyyy-MM-dd H:mm",
            "yyyy-MM-dd",
            "yyyy/MM/dd HH:mm",
            "yyyy/MM/dd",
            "MM/dd/yyyy HH:mm",
            "MM/dd/yyyy h:mm tt",
            "MM/dd/yyyy",
            "M/d/yyyy h:mm tt",
            "M/d/yyyy h:mm:ss tt",
            "M/d/yyyy",
            "dd/MM/yyyy HH:mm",
            "dd/MM/yyyy h:mm tt",
            "dd/MM/yyyy",
            "d/M/yyyy h:mm tt",
            "d/M/yyyy",
            "d/MMM/yyyy HH:mm",
            "d/MMM/yyyy h:mm tt",
            "d/MMM/yyyy",
            "dd/MMM/yyyy HH:mm",
            "dd/MMM/yyyy h:mm tt",
            "dd/MMM/yyyy",
            "MMMM d, yyyy h:mm tt",
            "MMMM d, yyyy H:mm",
            "MMMM d, yyyy",
            "MMM d, yyyy h:mm tt",
            "MMM d, yyyy",
            "d MMMM yyyy h:mm tt",
            "d MMMM yyyy",
            "d MMM yyyy h:mm tt",
            "d MMM yyyy"
        };

        foreach (var culture in new[] { new CultureInfo("en-US"), CultureInfo.InvariantCulture })
        {
            if (DateTime.TryParseExact(
                    text, formats, culture,
                    DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.NoCurrentDateDefault,
                    out var parsed)
                && parsed.Year >= 2000
                && HasExplicitCalendarDate(parsed, text))
            {
                startsAt = DateTime.SpecifyKind(parsed, DateTimeKind.Unspecified);
                return true;
            }
        }

        // Last resort for "2026-08-15 10:00 AM" style strings (culture-aware).
        if (DateTime.TryParse(text, new CultureInfo("en-US"), DateTimeStyles.AllowWhiteSpaces, out var soft)
            && soft.Year >= 2000
            && HasExplicitCalendarDate(soft, text)
            && HasExplicitClockTime(text))
        {
            startsAt = DateTime.SpecifyKind(soft, DateTimeKind.Unspecified);
            return true;
        }

        var match = System.Text.RegularExpressions.Regex.Match(
            text,
            @"(\d{4}-\d{1,2}-\d{1,2}(?:[ T]\d{1,2}:\d{2}(?::\d{2})?(?:\.\d+)?(?:Z|[+-]\d{2}:?\d{2})?)?)|(\d{1,2}/\d{1,2}/\d{4}(?:\s+\d{1,2}:\d{2}(?:\s*[AaPp][Mm])?)?)|(\d{1,2}/[A-Za-z]{3,9}/\d{4}(?:\s+\d{1,2}:\d{2}(?:\s*[AaPp][Mm])?)?)|((?:Jan|Feb|Mar|Apr|May|Jun|Jul|Aug|Sep|Oct|Nov|Dec)[a-z]*\s+\d{1,2},?\s+\d{4}(?:\s+\d{1,2}:\d{2}(?:\s*[AaPp][Mm])?)?)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (match.Success && !string.Equals(match.Value, text, StringComparison.OrdinalIgnoreCase))
            return TryParseAppointmentDateTimeCore(match.Value, out startsAt);

        return false;
    }

    private static bool HasExplicitOffset(string text) =>
        System.Text.RegularExpressions.Regex.IsMatch(
            text,
            @"Z\s*$|[+-]\d{2}:?\d{2}\s*$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private static void TryExtractTimeRange(string text, out string? startToken, out string? endToken)
    {
        startToken = null;
        endToken = null;
        // Require real clock tokens (":mm" and/or AM/PM) so ISO dates like 2026-08-15 are not treated as ranges.
        var m = System.Text.RegularExpressions.Regex.Match(
            text,
            TimeRangePattern,
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (!m.Success)
            return;
        startToken = m.Groups[1].Value.Trim();
        endToken = m.Groups[2].Value.Trim();
    }

    private static bool TryParseDateAndTimeRange(string text, out DateTime startsAt, out DateTime? endsAt)
    {
        startsAt = default;
        endsAt = null;
        var dateMatch = System.Text.RegularExpressions.Regex.Match(
            text,
            @"(\d{4}-\d{1,2}-\d{1,2})|((?:Jan|Feb|Mar|Apr|May|Jun|Jul|Aug|Sep|Oct|Nov|Dec)[a-z]*\s+\d{1,2},?\s+\d{4})|(\d{1,2}/\d{1,2}/\d{4})|(\d{1,2}/[A-Za-z]{3,9}/\d{4})",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        TryExtractTimeRange(text, out var startTok, out var endTok);
        if (!dateMatch.Success)
            return false;

        if (startTok != null)
        {
            if (!TryParseAppointmentDateTimeCore($"{dateMatch.Value} {startTok}", out startsAt))
                return false;
            if (endTok != null
                && TryParseAppointmentDateTimeCore($"{dateMatch.Value} {endTok}", out var endDt))
                endsAt = endDt;
            else
                endsAt = startsAt.AddHours(1);
            return true;
        }

        var timeMatch = System.Text.RegularExpressions.Regex.Match(
            text,
            @"\b(\d{1,2}(?::\d{2})?\s*[AaPp][Mm])\b",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (!timeMatch.Success)
            return false;
        if (!TryParseAppointmentDateTimeCore($"{dateMatch.Value} {timeMatch.Value}", out startsAt))
            return false;
        endsAt = startsAt.AddHours(1);
        return true;
    }

    private static bool IsTimeOnly(string text)
    {
        return System.Text.RegularExpressions.Regex.IsMatch(
            text.Trim(),
            @"^(?:[01]?\d|2[0-3]):[0-5]\d(?:\s*[AaPp][Mm])?$|^(?:[1-9]|1[0-2])\s*[AaPp][Mm]$");
    }

    /// <summary>
    /// Heuristic: require year/month/day tokens in the raw string so we don't accept "today + time".
    /// </summary>
    private static bool HasExplicitCalendarDate(DateTime parsed, string raw)
    {
        if (parsed.Year < 2000)
            return false;

        if (System.Text.RegularExpressions.Regex.IsMatch(raw, @"\b20\d{2}\b"))
            return true;
        if (System.Text.RegularExpressions.Regex.IsMatch(
                raw,
                @"\b(?:Jan|Feb|Mar|Apr|May|Jun|Jul|Aug|Sep|Oct|Nov|Dec)[a-z]*\b",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            return true;
        if (System.Text.RegularExpressions.Regex.IsMatch(raw, @"\b\d{1,2}[/-]\d{1,2}[/-]\d{2,4}\b"))
            return true;

        return false;
    }

    /// <summary>
    /// Store appointments as Pacific wall-clock (Unspecified). Nuvi replies are PST-only.
    /// </summary>
    private static DateTime NormalizeToClinicLocal(DateTime value)
    {
        var tz = GetClinicTimeZone();
        if (value.Kind == DateTimeKind.Utc)
        {
            return DateTime.SpecifyKind(
                TimeZoneInfo.ConvertTimeFromUtc(value, tz),
                DateTimeKind.Unspecified);
        }

        if (value.Kind == DateTimeKind.Local)
        {
            return DateTime.SpecifyKind(
                TimeZoneInfo.ConvertTime(value, tz),
                DateTimeKind.Unspecified);
        }

        return DateTime.SpecifyKind(value, DateTimeKind.Unspecified);
    }

    private static TimeZoneInfo GetClinicTimeZone()
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(
                OperatingSystem.IsWindows() ? "Pacific Standard Time" : "America/Los_Angeles");
        }
        catch
        {
            return TimeZoneInfo.Utc;
        }
    }

    private static bool TryExtractJsonSlot(string json, out DateTime startsAt, out DateTime? endsAt)
    {
        startsAt = default;
        endsAt = null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            string? ReadString(string name)
            {
                if (!root.TryGetProperty(name, out var el))
                    return null;
                var raw = el.ValueKind == JsonValueKind.String ? el.GetString() : el.ToString();
                return string.IsNullOrWhiteSpace(raw) ? null : raw.Trim().Trim('"');
            }

            var datePart = ReadString("appointment_date");
            var timePart = ReadString("appointment_time")
                           ?? ReadString("appointment_start_time")
                           ?? ReadString("start_time");
            if (datePart != null && timePart != null
                && TryParseAppointmentSlot($"{datePart} {timePart}", out startsAt, out endsAt))
                return true;

            foreach (var name in new[]
                     {
                         "appointment_datetime", "booked_datetime",
                         "starts_at", "datetime", "scheduled_at",
                         "time_slot", "appointment_slot"
                     })
            {
                var raw = ReadString(name);
                if (!string.IsNullOrWhiteSpace(raw) && TryParseAppointmentSlot(raw, out startsAt, out endsAt))
                    return true;
            }

            var endRaw = ReadString("appointment_end")
                         ?? ReadString("ends_at")
                         ?? ReadString("end_time")
                         ?? ReadString("appointment_end_time");
            if (!string.IsNullOrWhiteSpace(endRaw) && TryParseAppointmentDateTimeCore(endRaw, out var endDt))
                endsAt = endDt;
        }
        catch
        {
            // ignore non-JSON payloads
        }

        return false;
    }

    /// <summary>e.g. "Mon, Aug 10 · 9:00 AM (PST)" — start time only (no end).</summary>
    public static string FormatPstSlot(DateTime startsAt, DateTime endsAt)
    {
        _ = endsAt;
        var date = startsAt.ToString("ddd, MMM d", CultureInfo.InvariantCulture);
        var start = startsAt.ToString("h:mm tt", CultureInfo.InvariantCulture);
        return $"{date} · {start} (PST)";
    }

    private static Dictionary<string, string> ExtractDynamicVariables(JsonElement data)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        JsonElement vars = default;
        var found = false;

        if (data.TryGetProperty("conversation_initiation_client_data", out var init)
            && init.TryGetProperty("dynamic_variables", out vars))
            found = true;
        else if (data.TryGetProperty("dynamic_variables", out vars))
            found = true;

        if (!found || vars.ValueKind != JsonValueKind.Object)
            return result;

        foreach (var prop in vars.EnumerateObject())
        {
            if (prop.Value.ValueKind == JsonValueKind.String)
                result[prop.Name] = prop.Value.GetString() ?? "";
            else
                result[prop.Name] = prop.Value.ToString();
        }

        return result;
    }

    private static bool ValidateWebhookSignature(string rawBody, string? signatureHeader, string secret)
    {
        // ElevenLabs-Signature: t=timestamp,v0=hash
        if (string.IsNullOrWhiteSpace(signatureHeader))
            return false;

        string? timestamp = null;
        string? hash = null;
        foreach (var part in signatureHeader.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var kv = part.Split('=', 2);
            if (kv.Length != 2)
                continue;
            if (kv[0] == "t") timestamp = kv[1];
            if (kv[0] is "v0" or "v1") hash = kv[1];
        }

        if (string.IsNullOrWhiteSpace(timestamp) || string.IsNullOrWhiteSpace(hash))
            return false;

        var payload = $"{timestamp}.{rawBody}";
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var computed = Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(computed),
            Encoding.UTF8.GetBytes(hash.ToLowerInvariant()));
    }

    private static string Truncate(string? value, int max)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;
        return value.Length <= max ? value : value[..max] + "…";
    }

    /// <summary>
    /// True when the GET conversation payload has enough transcript/analysis text for Claude.
    /// </summary>
    private static bool ConversationContentReadyForExtraction(JsonElement data)
    {
        if (data.TryGetProperty("transcript", out var transcript) && transcript.ValueKind == JsonValueKind.Array)
        {
            foreach (var turn in transcript.EnumerateArray())
            {
                if (turn.TryGetProperty("message", out var msg)
                    && msg.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(msg.GetString()))
                    return true;
            }
        }

        if (data.TryGetProperty("analysis", out var analysis) && analysis.ValueKind == JsonValueKind.Object)
        {
            if (analysis.TryGetProperty("transcript_summary", out var summary)
                && summary.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(summary.GetString()))
                return true;

            if (analysis.TryGetProperty("data_collection_results", out var dcr)
                && dcr.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                return true;
        }

        return false;
    }

    private async Task<BookingOutcome> EnrichBookingOutcomeWithClaudeAsync(
        JsonElement data,
        BookingOutcome outcome,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_anthropic.ApiKey) || string.IsNullOrWhiteSpace(_anthropic.Model))
        {
            _logger.LogInformation("Claude slot extraction skipped — Anthropic ApiKey/Model not configured.");
            return outcome;
        }

        var context = BuildConversationExtractionContext(data);
        if (string.IsNullOrWhiteSpace(context))
            return outcome;

        try
        {
            const string system = """
                You extract dental appointment booking results from phone-call transcripts and analysis.
                All times are US Pacific wall-clock (no timezone conversion).
                Reply with ONLY compact JSON (no markdown):
                {"status":"booked"|"no_slot"|"declined"|"unknown","appointment_date":"yyyy-MM-dd"|null,"appointment_time":"h:mm AM/PM"|null}
                Rules:
                - If the receptionist confirmed a specific slot and the agent accepted it, status must be "booked" with both date and time.
                - Prefer the final confirmed slot spoken on the call (e.g. "13th at 11:30 AM" → 2026-08-13 and 11:30 AM).
                - Never invent a slot that was not discussed. Use nulls when unknown.
                """;

            var payload = AnthropicApiHelper.BuildPayload(
                _anthropic,
                maxTokens: 250,
                system: system,
                messages: new object[] { new { role = "user", content = context } });

            var client = _httpClientFactory.CreateClient();
            using var httpRequest = AnthropicApiHelper.CreateMessageRequest(_anthropic, payload);
            using var response = await client.SendAsync(httpRequest, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogInformation(
                    "Claude slot extraction HTTP {Status}",
                    (int)response.StatusCode);
                return outcome;
            }

            var text = AnthropicApiHelper.ExtractTextContent(body);
            var json = ExtractJsonObject(text);
            if (string.IsNullOrWhiteSpace(json))
            {
                _logger.LogInformation("Claude slot extraction returned no JSON");
                return outcome;
            }

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var date = root.TryGetProperty("appointment_date", out var dEl) && dEl.ValueKind == JsonValueKind.String
                ? dEl.GetString()
                : null;
            var time = root.TryGetProperty("appointment_time", out var tEl) && tEl.ValueKind == JsonValueKind.String
                ? tEl.GetString()
                : null;
            var status = root.TryGetProperty("status", out var sEl) && sEl.ValueKind == JsonValueKind.String
                ? sEl.GetString()
                : null;

            var booked = outcome.IsBooked;
            var startsAt = outcome.StartsAt;
            var endsAt = outcome.EndsAt;
            var statusHint = outcome.StatusHint;

            if (!string.IsNullOrWhiteSpace(status))
            {
                var mapped = MapStatusHint(status.Trim().ToLowerInvariant());
                if (mapped == VoiceOutboundCallStatuses.Booked)
                    booked = true;
                statusHint ??= mapped;
            }

            if (!string.IsNullOrWhiteSpace(date) && !string.IsNullOrWhiteSpace(time)
                && TryParseAppointmentSlot($"{date.Trim()} {time.Trim()}", out var combined, out var combinedEnd))
            {
                startsAt = combined;
                endsAt = combinedEnd ?? endsAt;
                booked = true;
                statusHint ??= VoiceOutboundCallStatuses.Booked;
                _logger.LogInformation(
                    "Claude extracted booking slot {StartsAt} (date={Date} time={Time})",
                    startsAt, date, time);
            }
            else
            {
                _logger.LogInformation(
                    "Claude slot extraction could not parse date/time. date={Date} time={Time}",
                    date, time);
            }

            var notes = string.IsNullOrWhiteSpace(outcome.Notes)
                ? "claude_slot_extract"
                : $"{outcome.Notes} | claude_slot_extract";
            return new BookingOutcome(booked, startsAt, endsAt, statusHint, notes);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Claude slot extraction failed.");
            return outcome;
        }
    }

    private static string BuildConversationExtractionContext(JsonElement data)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Extract the confirmed appointment date and time from this ElevenLabs conversation payload.");
        sb.AppendLine();

        if (data.TryGetProperty("analysis", out var analysis) && analysis.ValueKind == JsonValueKind.Object)
        {
            if (analysis.TryGetProperty("transcript_summary", out var summary)
                && summary.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(summary.GetString()))
            {
                sb.AppendLine("Transcript summary:");
                sb.AppendLine(summary.GetString());
                sb.AppendLine();
            }

            if (analysis.TryGetProperty("data_collection_results", out var dcr))
            {
                sb.AppendLine("Data collection results:");
                foreach (var (id, value) in EnumerateDataCollectionEntries(dcr))
                    sb.AppendLine($"- {id}: {value}");
                sb.AppendLine();
            }
        }

        if (data.TryGetProperty("transcript", out var transcript) && transcript.ValueKind == JsonValueKind.Array)
        {
            sb.AppendLine("Transcript:");
            var count = 0;
            foreach (var turn in transcript.EnumerateArray())
            {
                var role = turn.TryGetProperty("role", out var roleEl) ? roleEl.GetString() : "unknown";
                var message = turn.TryGetProperty("message", out var msgEl) && msgEl.ValueKind == JsonValueKind.String
                    ? msgEl.GetString()
                    : null;
                if (string.IsNullOrWhiteSpace(message))
                    continue;
                sb.AppendLine($"{role}: {message}");
                count++;
                if (count >= 40)
                    break;
            }
        }

        return sb.ToString();
    }

    private static string? ExtractJsonObject(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var trimmed = text.Trim();
        var fenceStart = trimmed.IndexOf("```", StringComparison.Ordinal);
        if (fenceStart >= 0)
        {
            var afterFence = trimmed[(fenceStart + 3)..];
            if (afterFence.StartsWith("json", StringComparison.OrdinalIgnoreCase))
                afterFence = afterFence[4..];
            var fenceEnd = afterFence.IndexOf("```", StringComparison.Ordinal);
            if (fenceEnd >= 0)
                trimmed = afterFence[..fenceEnd].Trim();
            else
                trimmed = afterFence.Trim();
        }

        var start = trimmed.IndexOf('{');
        var end = trimmed.LastIndexOf('}');
        if (start < 0 || end <= start)
            return null;
        return trimmed[start..(end + 1)];
    }

    private sealed record BookingOutcome(
        bool IsBooked,
        DateTime? StartsAt,
        DateTime? EndsAt,
        string? StatusHint,
        string? Notes);
}
