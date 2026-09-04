using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Docovee.BLL.Audit;
using Docovee.BLL.Configuration;
using Docovee.DS;
using Docovee.DS.Entities;
using Docovee.logging;
using Microsoft.Extensions.Options;

namespace Docovee.BLL.Services;

/// <summary>
/// Places outbound PSTN calls via ElevenLabs Conversational AI + Twilio integration
/// (POST /v1/convai/twilio/outbound-call).
/// </summary>
public sealed class ElevenLabsTwilioCallingService : INuviVoiceCallingService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly HttpClient _httpClient;
    private readonly ElevenLabsOptions _elevenLabs;
    private readonly TwilioOptions _twilio;
    private readonly VoiceOptions _voice;
    private readonly IDocoveeLogger _logger;
    private readonly DocoveeDbContext _db;
    private readonly IAuditTrailService _audit;
    private string? _resolvedPhoneNumberId;

    public ElevenLabsTwilioCallingService(
        HttpClient httpClient,
        IOptions<ElevenLabsOptions> elevenLabs,
        IOptions<TwilioOptions> twilio,
        IOptions<VoiceOptions> voice,
        IDocoveeLogger logger,
        DocoveeDbContext db,
        IAuditTrailService audit)
    {
        _httpClient = httpClient;
        _elevenLabs = elevenLabs.Value;
        _twilio = twilio.Value;
        _voice = voice.Value;
        _logger = logger;
        _db = db;
        _audit = audit;

        var baseUrl = string.IsNullOrWhiteSpace(_elevenLabs.BaseUrl)
            ? "https://api.elevenlabs.io"
            : _elevenLabs.BaseUrl.TrimEnd('/');
        _httpClient.BaseAddress = new Uri(baseUrl + "/");
        _httpClient.DefaultRequestHeaders.Remove("xi-api-key");
        if (!string.IsNullOrWhiteSpace(_elevenLabs.ApiKey))
            _httpClient.DefaultRequestHeaders.TryAddWithoutValidation("xi-api-key", _elevenLabs.ApiKey.Trim());
        _httpClient.DefaultRequestHeaders.Accept.Clear();
        _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(_elevenLabs.ApiKey)
        && !string.IsNullOrWhiteSpace(_elevenLabs.AgentId)
        && (!string.IsNullOrWhiteSpace(_elevenLabs.AgentPhoneNumberId)
            || !string.IsNullOrWhiteSpace(_twilio.FromNumber));

    public async Task<NuviOutboundCallResult> PlaceOfficeCallAsync(
        NuviOutboundCallRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!IsConfigured)
        {
            return new NuviOutboundCallResult
            {
                Success = false,
                Message = "Voice calling is not configured. Add ElevenLabs ApiKey, AgentId, and AgentPhoneNumberId (or Twilio FromNumber).",
                ToNumber = request.ToNumber
            };
        }

        var toNumber = ToE164(request.ToNumber);
        if (string.IsNullOrWhiteSpace(toNumber))
        {
            return new NuviOutboundCallResult
            {
                Success = false,
                Message = "The office phone number is missing or invalid.",
                ToNumber = request.ToNumber
            };
        }

        string phoneNumberId;
        try
        {
            phoneNumberId = await ResolveAgentPhoneNumberIdAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to resolve ElevenLabs agent phone number id.");
            return new NuviOutboundCallResult
            {
                Success = false,
#if DEBUG
                Message =
                    "Could not resolve the ElevenLabs Twilio phone number. "
                    + ex.Message
                    + " In the new ElevenLabs workspace: Agents → Phone Numbers → Import Twilio number, "
                    + "then set ElevenLabs:AgentPhoneNumberId to that phone_number_id (and restart the API).",
                ToNumber = toNumber
#else
                Message = "Error in calling : "+toNumber,
#endif
            };
        }

        var agentId = _elevenLabs.AgentId.Trim();

        var payload = new Dictionary<string, object?>
        {
            ["agent_id"] = agentId,
            ["agent_phone_number_id"] = phoneNumberId,
            ["to_number"] = toNumber,
            ["conversation_initiation_client_data"] = new Dictionary<string, object?>
            {
                // Agent first message in ElevenLabs dashboard should be {{first_message}} only.
                // Do NOT pass conversation_config_override.first_message unless Security → Overrides allows it.
                ["dynamic_variables"] = BuildDynamicVariables(request, _voice.IncludePhi)
            }
        };

        try
        {
            using var content = new StringContent(JsonSerializer.Serialize(payload, JsonOptions), Encoding.UTF8, "application/json");
            using var response = await _httpClient.PostAsync("v1/convai/twilio/outbound-call", content, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogInformation(
                    "ElevenLabs outbound call failed. Status={Status}",
                    (int)response.StatusCode);

                return new NuviOutboundCallResult
                {
                    Success = false,
                    Message = $"ElevenLabs call failed ({(int)response.StatusCode}): {ExtractErrorMessage(body)}",
                    ToNumber = toNumber
                };
            }

            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
            var root = doc.RootElement;
            var success = !root.TryGetProperty("success", out var successEl) || successEl.ValueKind != JsonValueKind.False;
            var message = root.TryGetProperty("message", out var msgEl) && msgEl.ValueKind == JsonValueKind.String
                ? msgEl.GetString() ?? "Call initiated."
                : "Call initiated.";
            var conversationId = root.TryGetProperty("conversation_id", out var convEl) && convEl.ValueKind == JsonValueKind.String
                ? convEl.GetString()
                : null;
            var callSid = root.TryGetProperty("callSid", out var sidEl) && sidEl.ValueKind == JsonValueKind.String
                ? sidEl.GetString()
                : root.TryGetProperty("call_sid", out var sidEl2) && sidEl2.ValueKind == JsonValueKind.String
                    ? sidEl2.GetString()
                    : null;

            if (!success)
            {
                return new NuviOutboundCallResult
                {
                    Success = false,
                    Message = message,
                    ConversationId = conversationId,
                    CallSid = callSid,
                    ToNumber = toNumber
                };
            }

            _logger.LogInformation(
                "ElevenLabs outbound call started. ConversationId={ConversationId} CallSid={CallSid}",
                conversationId ?? "", callSid ?? "");

            await _audit.LogDiscloseAsync(
                _db,
                AuditEntityTypes.VoiceOutboundCall,
                conversationId,
                "ElevenLabs outbound call started",
                cancellationToken: cancellationToken);

            return new NuviOutboundCallResult
            {
                Success = true,
                Message = message,
                ConversationId = conversationId,
                CallSid = callSid,
                ToNumber = toNumber
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ElevenLabs outbound call request failed");
            return new NuviOutboundCallResult
            {
                Success = false,
                Message = "Unable to reach ElevenLabs to place the call. Please try again shortly.",
                ToNumber = toNumber
            };
        }
    }

    private static Dictionary<string, string> BuildDynamicVariables(NuviOutboundCallRequest request, bool includePhi)
    {
        var vars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var intent = string.IsNullOrWhiteSpace(request.Intent)
            ? VoiceOutboundCallIntents.Book
            : request.Intent.Trim();
        var isCancel = string.Equals(intent, VoiceOutboundCallIntents.Cancel, StringComparison.OrdinalIgnoreCase);
        var isReschedule = string.Equals(intent, VoiceOutboundCallIntents.Reschedule, StringComparison.OrdinalIgnoreCase);

        var nowClinic = GetClinicNow();
        vars["current_date"] = nowClinic.ToString("dddd, MMMM d, yyyy");
        vars["current_datetime"] = nowClinic.ToString("yyyy-MM-dd HH:mm");
        vars["current_timezone"] = "America/Chicago";
        vars["today"] = nowClinic.ToString("yyyy-MM-dd");

        // Minimum necessary: generic label unless IncludePhi is explicitly enabled.
        vars["patient_name"] = includePhi
            ? FirstNonEmpty(request.PatientName, "a patient")
            : "a patient";
        vars["practice_name"] = FirstNonEmpty(request.PracticeName, request.DoctorName, "your office");
        vars["external_call_id"] = FirstNonEmpty(request.SessionKey, Guid.NewGuid().ToString("N"));
        vars["call_intent"] = isCancel
            ? VoiceOutboundCallIntents.Cancel
            : isReschedule
                ? VoiceOutboundCallIntents.Reschedule
                : VoiceOutboundCallIntents.Book;

        // Callback number is required for the office to reach the patient.
        AddVar(vars, "patient_phone", request.PatientPhone);
        AddVar(vars, "practice_phone", request.PracticePhone);
        AddVar(vars, "doctor_name", request.DoctorName);
        AddVar(vars, "session_key", request.SessionKey);

        if (includePhi)
        {
            AddVar(vars, "patient_email", request.PatientEmail);
            AddVar(vars, "call_context", request.CallContext);
            AddVar(vars, "insurance_name", request.InsuranceName);

            // Patient details the office needs once a slot is available:
            // full name (patient_name above), phone, DOB, reason for visit.
            var visitReason = FirstNonEmpty(
                request.ChiefComplaint,
                request.AppointmentType,
                "dental appointment");
            vars["chief_complaint"] = visitReason;
            vars["visit_reason"] = visitReason;
            vars["appointment_type"] = visitReason;

            if (TryFormatPatientDateOfBirth(request.PatientDateOfBirth, out var dobSpoken))
                vars["patient_date_of_birth"] = dobSpoken;
        }
        else
        {
            // Generic visit reason only — no free-text complaint, DOB, or insurance detail.
            vars["chief_complaint"] = "dental appointment";
            vars["visit_reason"] = "dental appointment";
            vars["appointment_type"] = FirstNonEmpty(request.AppointmentType, "dental appointment");
        }

        if (isCancel)
        {
            AddVar(vars, "appointment_date", request.AppointmentDate);
            AddVar(vars, "appointment_time", request.AppointmentTime);
            var cancelSlot = FirstNonEmpty(
                request.AppointmentDateTime,
                !string.IsNullOrWhiteSpace(request.AppointmentDate) && !string.IsNullOrWhiteSpace(request.AppointmentTime)
                    ? $"{request.AppointmentDate} at {request.AppointmentTime}"
                    : null);
            vars["appointment_datetime"] = cancelSlot;
            vars["date_time"] = cancelSlot;
            vars["preferred_date"] = cancelSlot;
            vars["availability_window"] = cancelSlot;
            if (!string.IsNullOrWhiteSpace(request.AppointmentTime))
                vars["preferred_time_window"] = request.AppointmentTime.Trim();
        }
        else if (isReschedule)
        {
            AddVar(vars, "appointment_date", request.AppointmentDate);
            AddVar(vars, "appointment_time", request.AppointmentTime);
            var currentSlot = FirstNonEmpty(
                request.AppointmentDateTime,
                !string.IsNullOrWhiteSpace(request.AppointmentDate) && !string.IsNullOrWhiteSpace(request.AppointmentTime)
                    ? $"{request.AppointmentDate} at {request.AppointmentTime}"
                    : null);
            vars["appointment_datetime"] = currentSlot;

            var window = FirstNonEmpty(
                request.PreferredDate,
                request.AvailabilityWindow,
                "within the next 30 days");
            vars["date_time"] = window;
            vars["preferred_date"] = window;
            vars["availability_window"] = window;
            vars["preferred_time_window"] = FirstNonEmpty(
                request.PreferredTimeWindow,
                "any available time during office hours");
            AddVar(vars, "booking_window_start", request.BookingWindowStart);
            AddVar(vars, "booking_window_end", request.BookingWindowEnd);
            vars["appointment_datetime_format"] =
                "When rescheduled, report the exact new confirmed slot as yyyy-MM-dd HH:mm (example: 2026-08-12 09:00). Do not mention a timezone.";
        }
        else
        {
            var dateTime = FirstNonEmpty(
                request.PreferredDate,
                request.AvailabilityWindow,
                "within the next 30 days");

            var timeWindow = FirstNonEmpty(
                request.PreferredTimeWindow,
                "any available time during office hours");

            vars["date_time"] = dateTime;
            vars["preferred_date"] = dateTime;
            vars["preferred_time_window"] = timeWindow;
            vars["availability_window"] = dateTime;

            AddVar(vars, "booking_window_start", request.BookingWindowStart);
            AddVar(vars, "booking_window_end", request.BookingWindowEnd);
            vars["appointment_datetime_format"] =
                "When booked, report the exact confirmed slot as yyyy-MM-dd HH:mm (example: 2026-08-12 09:00). If a range like 9-10 AM was confirmed, use the start time and mention the end in confirmation_notes. Do not mention a timezone.";

            if (includePhi)
                AddVar(vars, "call_preference", request.CallPreference);
        }

        vars["first_message"] = BuildFirstMessage(intent, vars);
        return vars;
    }

    /// <summary>
    /// Full opener sent as {{first_message}} — set the ElevenLabs agent first message to that variable only.
    /// Book opener matches client script: introduce Nuvi, patient nearby, implant consult, screening questions.
    /// </summary>
    private static string BuildFirstMessage(string intent, IReadOnlyDictionary<string, string> vars)
    {
        var patientName = vars.TryGetValue("patient_name", out var pn) && !string.IsNullOrWhiteSpace(pn)
            ? pn.Trim()
            : "a patient";
        var isCancel = string.Equals(intent, VoiceOutboundCallIntents.Cancel, StringComparison.OrdinalIgnoreCase);
        var isReschedule = string.Equals(intent, VoiceOutboundCallIntents.Reschedule, StringComparison.OrdinalIgnoreCase);

        if (isCancel)
        {
            return
                $"Hi, I'm Nuvi. I'm calling on behalf of {patientName}. I'm helping them cancel a dental appointment. Do you have a moment?";
        }

        if (isReschedule)
        {
            return
                $"Hi, I'm Nuvi. I'm calling on behalf of {patientName}. I'm helping them reschedule a dental appointment. Do you have a moment?";
        }

        var consultPhrase = BuildBookConsultPhrase(vars);
        return
            $"Hi, I'm Nuvi. I'm calling on behalf of {patientName}. They live nearby and are looking for {consultPhrase}. I have a few questions to see if your office might be a good fit.";
    }

    /// <summary>
    /// Prefer visit reason when it already describes the consult; otherwise default to implant consultation.
    /// </summary>
    private static string BuildBookConsultPhrase(IReadOnlyDictionary<string, string> vars)
    {
        var reason = vars.TryGetValue("visit_reason", out var vr) ? vr?.Trim() : null;
        if (string.IsNullOrWhiteSpace(reason)
            || string.Equals(reason, "dental appointment", StringComparison.OrdinalIgnoreCase))
        {
            return "a consultation for dental implants";
        }

        if (reason.Contains("consult", StringComparison.OrdinalIgnoreCase)
            || reason.Contains("implant", StringComparison.OrdinalIgnoreCase))
        {
            return reason.StartsWith("a ", StringComparison.OrdinalIgnoreCase)
                   || reason.StartsWith("an ", StringComparison.OrdinalIgnoreCase)
                ? reason
                : $"a consultation for {reason}";
        }

        return $"a consultation for {reason}";
    }

    /// <summary>Clinic-local "now" in US Central (CST/CDT). Uses <see cref="ClinicTime"/>.</summary>
    internal static DateTime GetClinicNow()
    {
        try
        {
            return ClinicTime.Now;
        }
        catch
        {
            return DateTime.UtcNow.AddHours(-5); // CDT fallback approx
        }
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
                return value.Trim();
        }

        return string.Empty;
    }

    /// <summary>
    /// Formats DOB for the agent to speak. Skips unset / placeholder values (year ≤ 1900 or 1990-01-01).
    /// </summary>
    internal static bool TryFormatPatientDateOfBirth(DateOnly? dateOfBirth, out string spoken)
    {
        spoken = string.Empty;
        if (dateOfBirth is not DateOnly dob)
            return false;
        if (dob.Year <= 1900 || dob == new DateOnly(1990, 1, 1))
            return false;

        spoken = dob.ToString("MMMM d, yyyy", System.Globalization.CultureInfo.InvariantCulture);
        return true;
    }

    /// <summary>Returns a usable patient DOB, preferring appointment then patient, skipping placeholders.</summary>
    internal static DateOnly? PreferPatientDateOfBirth(DateOnly? preferred, DateOnly? fallback = null)
    {
        if (TryFormatPatientDateOfBirth(preferred, out _))
            return preferred;
        if (TryFormatPatientDateOfBirth(fallback, out _))
            return fallback;
        return null;
    }

    private static void AddVar(IDictionary<string, string> vars, string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            vars[key] = value.Trim();
    }

    private async Task<string> ResolveAgentPhoneNumberIdAsync(CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(_resolvedPhoneNumberId))
            return _resolvedPhoneNumberId;

        var configured = _elevenLabs.AgentPhoneNumberId?.Trim();

        // Explicit ElevenLabs phone_number_id (e.g. phnum_...) — use as-is, never treat as E.164.
        if (!string.IsNullOrWhiteSpace(configured) && IsElevenLabsPhoneNumberId(configured))
        {
            _resolvedPhoneNumberId = configured;
            return configured;
        }

        if (!string.IsNullOrWhiteSpace(configured) && !LooksLikePhoneNumber(configured))
        {
            _resolvedPhoneNumberId = configured;
            return configured;
        }

        var matchPhone = LooksLikePhoneNumber(configured)
            ? configured
            : _twilio.FromNumber;

        using var response = await _httpClient.GetAsync("v1/convai/phone-numbers?provider=twilio", cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"List phone numbers failed ({(int)response.StatusCode}): {Truncate(body, 300)}");

        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "[]" : body);
        var root = doc.RootElement;
        // API may return a bare array or { "phone_numbers": [ ... ] }.
        var list = root.ValueKind == JsonValueKind.Array
            ? root
            : root.ValueKind == JsonValueKind.Object && root.TryGetProperty("phone_numbers", out var nested)
                ? nested
                : default;
        if (list.ValueKind != JsonValueKind.Array || list.GetArrayLength() == 0)
            throw new InvalidOperationException(
                "No Twilio phone numbers are imported in this ElevenLabs workspace yet.");

        string? resolved = null;
        foreach (var item in list.EnumerateArray())
        {
            var id = item.TryGetProperty("phone_number_id", out var idEl) ? idEl.GetString()
                : item.TryGetProperty("id", out var idEl2) ? idEl2.GetString()
                : null;
            var number = item.TryGetProperty("phone_number", out var numEl) ? numEl.GetString()
                : item.TryGetProperty("number", out var numEl2) ? numEl2.GetString()
                : null;
            if (string.IsNullOrWhiteSpace(id))
                continue;

            if (string.IsNullOrWhiteSpace(matchPhone) || PhoneNumberHelper.Matches(matchPhone, number))
            {
                resolved = id;
                if (!string.IsNullOrWhiteSpace(matchPhone) && PhoneNumberHelper.Matches(matchPhone, number))
                    break;
            }
        }

        if (string.IsNullOrWhiteSpace(resolved))
            throw new InvalidOperationException(
                $"No imported ElevenLabs Twilio number matched '{matchPhone}'. "
                + "Import that FromNumber in Agents → Phone Numbers, or set AgentPhoneNumberId explicitly.");

        _resolvedPhoneNumberId = resolved;
        return resolved;
    }

    private static bool IsElevenLabsPhoneNumberId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;
        var trimmed = value.Trim();
        return trimmed.StartsWith("phnum_", StringComparison.OrdinalIgnoreCase)
               || trimmed.StartsWith("phone_", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikePhoneNumber(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;
        var trimmed = value.Trim();
        if (IsElevenLabsPhoneNumberId(trimmed))
            return false;
        // Real phone numbers should not contain letters (IDs like phnum_… do).
        if (trimmed.Any(char.IsLetter))
            return false;
        if (trimmed.StartsWith('+'))
            return true;
        var digits = new string(trimmed.Where(char.IsDigit).ToArray());
        return digits.Length >= 10
               && digits.Length == trimmed.Count(c => char.IsDigit(c) || char.IsWhiteSpace(c) || c is '-' or '(' or ')');
    }

    public static string? ToE164(string? phone)
    {
        if (string.IsNullOrWhiteSpace(phone))
            return null;

        var trimmed = phone.Trim();
        var digits = new string(trimmed.Where(char.IsDigit).ToArray());
        if (digits.Length < 10)
            return null;

        if (trimmed.StartsWith('+'))
            return "+" + digits;

        if (digits.Length == 10)
            return "+1" + digits;

        if (digits.Length == 11 && digits.StartsWith('1'))
            return "+" + digits;

        return "+" + digits;
    }

    private static string ExtractErrorMessage(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("detail", out var detail))
            {
                if (detail.ValueKind == JsonValueKind.String)
                    return detail.GetString() ?? body;
                return Truncate(detail.ToString(), 300);
            }

            if (doc.RootElement.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String)
                return message.GetString() ?? body;
        }
        catch
        {
            // fall through
        }

        return Truncate(body, 300);
    }

    private static string Truncate(string? value, int max)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;
        return value.Length <= max ? value : value[..max] + "…";
    }
}
