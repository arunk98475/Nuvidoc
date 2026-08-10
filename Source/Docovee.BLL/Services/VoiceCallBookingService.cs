using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Docovee.BLL.Configuration;
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

    Task<IReadOnlyList<MobileVoiceCallDto>> GetCallsForSessionAsync(
        Guid sessionKey,
        CancellationToken cancellationToken = default);
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
    private readonly ElevenLabsOptions _elevenLabs;
    private readonly IDocoveeLogger _logger;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IPatientPushDispatcher _push;

    public VoiceCallBookingService(
        DocoveeDbContext db,
        IHttpClientFactory httpClientFactory,
        IOptions<ElevenLabsOptions> elevenLabs,
        IDocoveeLogger logger,
        IServiceScopeFactory scopeFactory,
        IPatientPushDispatcher push)
    {
        _db = db;
        _httpClientFactory = httpClientFactory;
        _elevenLabs = elevenLabs.Value;
        _logger = logger;
        _scopeFactory = scopeFactory;
        _push = push;
    }

    public async Task<IReadOnlyList<MobileVoiceCallDto>> GetCallsForSessionAsync(
        Guid sessionKey,
        CancellationToken cancellationToken = default)
    {
        if (sessionKey == Guid.Empty)
            return Array.Empty<MobileVoiceCallDto>();

        var rows = await (
            from c in _db.VoiceOutboundCalls.AsNoTracking()
            join d in _db.Doctors.AsNoTracking() on c.DoctorId equals d.Id into dj
            from d in dj.DefaultIfEmpty()
            where c.SessionKey == sessionKey
            orderby c.CreatedAt descending
            select new
            {
                c.Id,
                c.ConversationId,
                c.SessionKey,
                c.DoctorId,
                DoctorName = d != null ? d.Name : "",
                c.Status,
                c.AppointmentId,
                c.OutcomeNotes,
                c.CreatedAt,
                c.UpdatedAt,
                StartsAt = c.AppointmentId == null
                    ? (DateTime?)null
                    : _db.Appointments.Where(a => a.Id == c.AppointmentId).Select(a => (DateTime?)a.StartsAt).FirstOrDefault()
            }).ToListAsync(cancellationToken);

        return rows.Select(r =>
        {
            var terminal = IsTerminalStatus(r.Status);
            string? slot = null;
            if (r.StartsAt is DateTime start)
                slot = FormatPstSlot(start, start.AddHours(1));

            return new MobileVoiceCallDto
            {
                Id = r.Id,
                ConversationId = r.ConversationId,
                SessionKey = r.SessionKey,
                DoctorId = r.DoctorId,
                DoctorName = r.DoctorName ?? "",
                Status = r.Status,
                IsTerminal = terminal,
                AppointmentId = r.AppointmentId,
                AppointmentStartsAt = r.StartsAt,
                AppointmentSlotLabel = slot,
                OutcomeNotes = r.OutcomeNotes,
                CreatedAt = r.CreatedAt,
                UpdatedAt = r.UpdatedAt
            };
        }).ToList();
    }

    private static bool IsTerminalStatus(string status) =>
        status is VoiceOutboundCallStatuses.Booked
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

        _ = Task.Run(async () =>
        {
            try
            {
                // Wait for the live call + ElevenLabs analysis to finish.
                await Task.Delay(TimeSpan.FromSeconds(45));
                for (var i = 0; i < 20; i++)
                {
                    using var scope = _scopeFactory.CreateScope();
                    var service = scope.ServiceProvider.GetRequiredService<IVoiceCallBookingService>();
                    var done = await service.ProcessConversationAsync(conversationId);
                    if (done)
                        return;
                    await Task.Delay(TimeSpan.FromSeconds(30));
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
        if (!string.IsNullOrWhiteSpace(type)
            && !type.Contains("transcription", StringComparison.OrdinalIgnoreCase)
            && !type.Contains("post_call", StringComparison.OrdinalIgnoreCase))
        {
            return true; // ignore audio-only / initiation failure for booking
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

        return await ProcessConversationPayloadAsync(conversationId, data, cancellationToken);
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
                "Failed to fetch ElevenLabs conversation {ConversationId}: {Status} {Body}",
                conversationId, (int)response.StatusCode, Truncate(body, 300));
            return false;
        }

        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
        return await ProcessConversationPayloadAsync(conversationId, doc.RootElement, cancellationToken);
    }

    private async Task<bool> ProcessConversationPayloadAsync(
        string conversationId,
        JsonElement data,
        CancellationToken cancellationToken)
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

        if (call.AppointmentId.HasValue
            || call.Status is VoiceOutboundCallStatuses.Booked
                or VoiceOutboundCallStatuses.NoSlot
                or VoiceOutboundCallStatuses.Declined
                or VoiceOutboundCallStatuses.NoAnswer)
        {
            return true;
        }

        var status = data.TryGetProperty("status", out var statusEl) ? statusEl.GetString() : null;
        var stillInProgress = string.Equals(status, "processing", StringComparison.OrdinalIgnoreCase)
            || string.Equals(status, "initiated", StringComparison.OrdinalIgnoreCase)
            || string.Equals(status, "in-progress", StringComparison.OrdinalIgnoreCase);
        if (stillInProgress)
            return false;

        var outcome = ExtractBookingOutcome(data);

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
            int? notificationId = null;
            var title = "Nuvi call update";
            var body = string.IsNullOrWhiteSpace(outcome.Notes)
                ? $"Call finished with status {call.Status}."
                : outcome.Notes!;
            if (call.PatientId is > 0)
            {
                notificationId = await AddNotificationAsync(
                    call.PatientId.Value,
                    PatientNotificationTypes.VoiceCallUpdate,
                    title,
                    body,
                    doctorId: call.DoctorId,
                    cancellationToken: cancellationToken);
            }

            await DispatchTerminalPushAsync(call, call.Status, title, body, notificationId, cancellationToken: cancellationToken);
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
                "Booked without parseable datetime for conversation {ConversationId}. Notes={Notes}",
                conversationId, Truncate(outcome.Notes, 500));
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
            var pastTitle = "Nuvi call update";
            var pastBody =
                $"The office offered a past time ({startsAt:ddd, MMM d yyyy h:mm tt}). No appointment was saved.";
            int? notificationId = null;
            if (call.PatientId is > 0)
            {
                notificationId = await AddNotificationAsync(
                    call.PatientId.Value,
                    PatientNotificationTypes.VoiceCallUpdate,
                    pastTitle,
                    pastBody,
                    doctorId: call.DoctorId,
                    cancellationToken: cancellationToken);
            }

            await DispatchTerminalPushAsync(call, call.Status, pastTitle, pastBody, notificationId, cancellationToken: cancellationToken);
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
        var when = FormatPstSlot(appointmentStartsAt, appointmentEndsAt);
        var bookedTitle = "Appointment booked";
        var bookedBody = $"Nuvi booked your visit with {doctorName} on {when}.";
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
            cancellationToken);

        _logger.LogInformation(
            "Voice booking saved. Conversation={ConversationId} Appointment={AppointmentId}",
            conversationId, appointment.Id);
        return true;
    }

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

        await _push.DispatchAsync(new PatientPushMessage
        {
            Type = status == VoiceOutboundCallStatuses.Booked
                ? PatientNotificationTypes.AppointmentBooked
                : PatientNotificationTypes.VoiceCallUpdate,
            PatientId = call.PatientId,
            SessionKey = call.SessionKey,
            ConversationId = call.ConversationId,
            Status = status,
            Title = title,
            Body = body,
            DoctorId = call.DoctorId,
            DoctorName = resolvedDoctorName,
            AppointmentId = call.AppointmentId,
            StartsAt = startsAt,
            EndsAt = endsAt ?? (startsAt?.AddHours(1)),
            SlotLabel = slotLabel,
            NotificationId = notificationId
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

        // ElevenLabs format examples:
        //   appointment_datetime: { "value": "2026-08-15 10:00 AM" }
        //   or split: appointment_date + appointment_start_time + appointment_end_time
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
                    if (valueLower is "booked" or "confirmed" or "success" or "scheduled")
                        booked = true;
                }

                // Split fields (preferred for ElevenLabs data collection).
                if (idLower is "appointment_date" or "date" or "day" or "appointment_day")
                {
                    dateOnlyRaw ??= valueClean;
                    if (LooksLikeDateWithoutTime(valueClean) || TryParseAppointmentSlot(valueClean, out _, out _))
                        booked = true;
                    continue;
                }

                if (idLower is "appointment_start_time" or "start_time" or "slot_time" or "appointment_start")
                {
                    timeOnlyRaw ??= valueClean;
                    booked = true;
                    continue;
                }

                if (idLower is "appointment_end_time" or "end_time" or "appointment_end")
                {
                    endTimeRaw ??= valueClean;
                    continue;
                }

                // Combined datetime (legacy / fallback).
                if (idLower is "appointment_datetime" or "appointment_time" or "booked_datetime"
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

                if (idLower is "time")
                    timeOnlyRaw ??= valueClean;
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

        var joinedNotes = notes.Count > 0 ? string.Join(" | ", notes) : null;
        if (!booked && !string.IsNullOrWhiteSpace(joinedNotes))
        {
            var n = joinedNotes.ToLowerInvariant();
            if (n.Contains("booked") || n.Contains("scheduled") || n.Contains("confirmed an appointment")
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
                || TryExtractSpokenSlot(joinedNotes, out fromNotes, out fromNotesEnd))
            && !IsLikelyMidnightPlaceholder(fromNotes))
        {
            startsAt = fromNotes;
            endsAt = fromNotesEnd ?? endsAt;
        }

        // Never save a midnight "placeholder" as a real dental booking time.
        if (startsAt != null && IsLikelyMidnightPlaceholder(startsAt.Value))
            startsAt = null;

        return new BookingOutcome(booked, startsAt, endsAt, statusHint, joinedNotes);
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
        if (item.TryGetProperty("rationale", out var rationale)
            && rationale.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(rationale.GetString()))
            return rationale.GetString();

        return null;
    }

    private static string? MapStatusHint(string valueLower) => valueLower switch
    {
        "booked" or "confirmed" or "scheduled" or "success" => VoiceOutboundCallStatuses.Booked,
        "no_slot" or "no slot" or "unavailable" => VoiceOutboundCallStatuses.NoSlot,
        "declined" or "dnc" => VoiceOutboundCallStatuses.Declined,
        "no_answer" or "voicemail" or "noanswer" => VoiceOutboundCallStatuses.NoAnswer,
        "failed" => VoiceOutboundCallStatuses.Failed,
        _ => VoiceOutboundCallStatuses.Completed
    };

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
            foreach (var name in new[]
                     {
                         "appointment_datetime", "appointment_time", "booked_datetime",
                         "starts_at", "datetime", "appointment_date", "scheduled_at",
                         "time_slot", "appointment_slot"
                     })
            {
                if (!doc.RootElement.TryGetProperty(name, out var el))
                    continue;

                var raw = el.ValueKind == JsonValueKind.String ? el.GetString() : el.ToString();
                if (!string.IsNullOrWhiteSpace(raw) && TryParseAppointmentSlot(raw, out startsAt, out endsAt))
                    return true;
            }

            if (doc.RootElement.TryGetProperty("appointment_end", out var endEl)
                || doc.RootElement.TryGetProperty("ends_at", out endEl)
                || doc.RootElement.TryGetProperty("end_time", out endEl))
            {
                var endRaw = endEl.ValueKind == JsonValueKind.String ? endEl.GetString() : endEl.ToString();
                if (!string.IsNullOrWhiteSpace(endRaw) && TryParseAppointmentDateTimeCore(endRaw, out var endDt))
                    endsAt = endDt;
            }
        }
        catch
        {
            // ignore non-JSON payloads
        }

        return false;
    }

    /// <summary>e.g. "Mon, Aug 10 · 9:00 AM – 10:00 AM (PST)"</summary>
    public static string FormatPstSlot(DateTime startsAt, DateTime endsAt)
    {
        var date = startsAt.ToString("ddd, MMM d", CultureInfo.InvariantCulture);
        var start = startsAt.ToString("h:mm tt", CultureInfo.InvariantCulture);
        var end = endsAt.ToString("h:mm tt", CultureInfo.InvariantCulture);
        return $"{date} · {start} – {end} (PST)";
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

    private sealed record BookingOutcome(
        bool IsBooked,
        DateTime? StartsAt,
        DateTime? EndsAt,
        string? StatusHint,
        string? Notes);
}
