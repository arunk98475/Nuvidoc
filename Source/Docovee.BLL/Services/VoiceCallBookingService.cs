using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Docovee.BLL.Configuration;
using Docovee.DS;
using Docovee.DS.Entities;
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
}

public sealed class PatientNotificationService : IPatientNotificationService
{
    private readonly DocoveeDbContext _db;

    public PatientNotificationService(DocoveeDbContext db) => _db = db;

    public async Task<IReadOnlyList<PatientNotificationDto>> GetForPatientAsync(
        int patientId, CancellationToken cancellationToken = default)
    {
        return await _db.PatientNotifications.AsNoTracking()
            .Where(n => n.PatientId == patientId)
            .OrderByDescending(n => n.CreatedAt)
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
                CreatedAt = n.CreatedAt
            })
            .ToListAsync(cancellationToken);
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

    public VoiceCallBookingService(
        DocoveeDbContext db,
        IHttpClientFactory httpClientFactory,
        IOptions<ElevenLabsOptions> elevenLabs,
        IDocoveeLogger logger,
        IServiceScopeFactory scopeFactory)
    {
        _db = db;
        _httpClientFactory = httpClientFactory;
        _elevenLabs = elevenLabs.Value;
        _logger = logger;
        _scopeFactory = scopeFactory;
    }

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
        call.UpdatedAt = DateTime.UtcNow;
        call.CompletedAt = DateTime.UtcNow;
        call.OutcomeNotes = Truncate(outcome.Notes, 2000);

        if (!outcome.IsBooked)
        {
            call.Status = outcome.StatusHint ?? VoiceOutboundCallStatuses.Completed;
            await _db.SaveChangesAsync(cancellationToken);
            if (call.PatientId is > 0 && !string.IsNullOrWhiteSpace(outcome.Notes))
            {
                await AddNotificationAsync(
                    call.PatientId.Value,
                    PatientNotificationTypes.VoiceCallUpdate,
                    "Nuvi call update",
                    outcome.Notes!,
                    doctorId: call.DoctorId,
                    cancellationToken: cancellationToken);
            }

            return true;
        }

        var startsAt = outcome.StartsAt ?? DateTime.Now.Date.AddDays(1).AddHours(10);
        var doctor = await _db.Doctors.AsNoTracking().FirstOrDefaultAsync(d => d.Id == call.DoctorId, cancellationToken);
        DateOnly? dob = null;
        if (call.PatientId is > 0)
        {
            dob = await _db.Patients.AsNoTracking()
                .Where(p => p.Id == call.PatientId.Value)
                .Select(p => p.DateOfBirth)
                .FirstOrDefaultAsync(cancellationToken);
        }

        var appointment = new Appointment
        {
            DoctorId = call.DoctorId,
            PatientId = call.PatientId,
            PatientName = call.PatientName,
            PatientPhone = call.PatientPhone,
            PatientEmail = call.PatientEmail,
            PatientDateOfBirth = dob is DateOnly d && d.Year > 1900 ? d : new DateOnly(1990, 1, 1),
            VisitReason = string.IsNullOrWhiteSpace(call.VisitReason) ? "Dental appointment (Nuvi booked)" : call.VisitReason!,
            StartsAt = startsAt,
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

        if (call.PatientId is > 0)
        {
            var doctorName = doctor?.Name ?? "your dentist";
            var when = startsAt.ToString("ddd, MMM d 'at' h:mm tt", CultureInfo.InvariantCulture);
            await AddNotificationAsync(
                call.PatientId.Value,
                PatientNotificationTypes.AppointmentBooked,
                "Appointment booked",
                $"Nuvi booked your visit with {doctorName} on {when}.",
                appointment.Id,
                call.DoctorId,
                cancellationToken);
        }

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

    private async Task AddNotificationAsync(
        int patientId,
        string type,
        string title,
        string body,
        int? appointmentId = null,
        int? doctorId = null,
        CancellationToken cancellationToken = default)
    {
        _db.PatientNotifications.Add(new PatientNotification
        {
            PatientId = patientId,
            Type = type,
            Title = title,
            Body = body,
            AppointmentId = appointmentId,
            DoctorId = doctorId,
            IsRead = false,
            CreatedAt = DateTime.UtcNow
        });
        await _db.SaveChangesAsync(cancellationToken);
    }

    private static BookingOutcome ExtractBookingOutcome(JsonElement data)
    {
        var notes = new List<string>();
        if (data.TryGetProperty("analysis", out var analysis))
        {
            if (analysis.TryGetProperty("transcript_summary", out var summary)
                && summary.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(summary.GetString()))
                notes.Add(summary.GetString()!);

            if (analysis.TryGetProperty("call_successful", out var ok)
                && ok.ValueKind == JsonValueKind.String)
                notes.Add($"call_successful={ok.GetString()}");
        }

        string? statusHint = null;
        DateTime? startsAt = null;
        var booked = false;

        // data_collection_results can be object or array depending on API version
        if (data.TryGetProperty("analysis", out var analysis2)
            && analysis2.TryGetProperty("data_collection_results", out var dcr))
        {
            foreach (var item in EnumerateDataCollection(dcr))
            {
                var id = item.TryGetProperty("data_collection_id", out var idEl)
                    ? idEl.GetString()
                    : item.TryGetProperty("id", out var idEl2) ? idEl2.GetString() : null;
                var value = item.TryGetProperty("value", out var valEl)
                    ? valEl.ToString()
                    : null;
                if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(value) || value == "null")
                    continue;

                var idLower = id.ToLowerInvariant();
                var valueLower = value.Trim().Trim('"').ToLowerInvariant();

                if (idLower is "status" or "outcome" or "booking_status")
                {
                    statusHint = MapStatusHint(valueLower);
                    if (valueLower is "booked" or "confirmed" or "success" or "scheduled")
                        booked = true;
                }

                if (idLower is "appointment_datetime" or "appointment_time" or "booked_datetime" or "starts_at")
                {
                    if (TryParseAppointmentDateTime(value.Trim().Trim('"'), out var dt))
                    {
                        startsAt = dt;
                        booked = true;
                    }
                }
            }
        }

        // Tool results / transcript may include end_call status
        if (data.TryGetProperty("transcript", out var transcript) && transcript.ValueKind == JsonValueKind.Array)
        {
            foreach (var turn in transcript.EnumerateArray())
            {
                if (!turn.TryGetProperty("tool_results", out var tools) || tools.ValueKind != JsonValueKind.Array)
                    continue;
                foreach (var tool in tools.EnumerateArray())
                {
                    var toolName = tool.TryGetProperty("tool_name", out var tn) ? tn.GetString() : null;
                    var resultValue = tool.TryGetProperty("result_value", out var rv) ? rv.GetString() : null;
                    if (string.IsNullOrWhiteSpace(resultValue))
                        continue;

                    if (toolName?.Contains("end_call", StringComparison.OrdinalIgnoreCase) == true
                        || resultValue.Contains("booked", StringComparison.OrdinalIgnoreCase))
                    {
                        if (resultValue.Contains("\"status\":\"booked\"", StringComparison.OrdinalIgnoreCase)
                            || resultValue.Contains("\"status\": \"booked\"", StringComparison.OrdinalIgnoreCase)
                            || resultValue.Contains("booked", StringComparison.OrdinalIgnoreCase))
                        {
                            booked = true;
                            statusHint ??= VoiceOutboundCallStatuses.Booked;
                        }

                        if (TryExtractJsonDate(resultValue, out var dt))
                            startsAt = dt;
                    }
                }
            }
        }

        var joinedNotes = notes.Count > 0 ? string.Join(" | ", notes) : null;
        if (!booked && !string.IsNullOrWhiteSpace(joinedNotes))
        {
            var n = joinedNotes.ToLowerInvariant();
            if (n.Contains("booked") || n.Contains("scheduled") || n.Contains("confirmed an appointment"))
                booked = true;
        }

        return new BookingOutcome(booked, startsAt, statusHint, joinedNotes);
    }

    private static IEnumerable<JsonElement> EnumerateDataCollection(JsonElement dcr)
    {
        if (dcr.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in dcr.EnumerateArray())
                yield return item;
            yield break;
        }

        if (dcr.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in dcr.EnumerateObject())
            {
                if (prop.Value.ValueKind == JsonValueKind.Object)
                    yield return prop.Value;
            }
        }
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

    private static bool TryParseAppointmentDateTime(string raw, out DateTime startsAt)
    {
        startsAt = default;
        if (string.IsNullOrWhiteSpace(raw))
            return false;

        if (DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out startsAt))
            return true;
        if (DateTime.TryParse(raw, CultureInfo.CurrentCulture, DateTimeStyles.AllowWhiteSpaces, out startsAt))
            return true;

        return false;
    }

    private static bool TryExtractJsonDate(string json, out DateTime startsAt)
    {
        startsAt = default;
        try
        {
            using var doc = JsonDocument.Parse(json);
            foreach (var name in new[] { "appointment_datetime", "appointment_time", "starts_at", "datetime" })
            {
                if (doc.RootElement.TryGetProperty(name, out var el)
                    && el.ValueKind == JsonValueKind.String
                    && TryParseAppointmentDateTime(el.GetString() ?? "", out startsAt))
                    return true;
            }
        }
        catch
        {
            // ignore
        }

        return false;
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

    private sealed record BookingOutcome(bool IsBooked, DateTime? StartsAt, string? StatusHint, string? Notes);
}
