using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Docovee.BLL.Configuration;
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
    private readonly IDocoveeLogger _logger;
    private string? _resolvedPhoneNumberId;

    public ElevenLabsTwilioCallingService(
        HttpClient httpClient,
        IOptions<ElevenLabsOptions> elevenLabs,
        IOptions<TwilioOptions> twilio,
        IDocoveeLogger logger)
    {
        _httpClient = httpClient;
        _elevenLabs = elevenLabs.Value;
        _twilio = twilio.Value;
        _logger = logger;

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

        var payload = new Dictionary<string, object?>
        {
            ["agent_id"] = _elevenLabs.AgentId.Trim(),
            ["agent_phone_number_id"] = phoneNumberId,
            ["to_number"] = toNumber,
            ["conversation_initiation_client_data"] = new Dictionary<string, object?>
            {
                // Do NOT override first_message unless the agent Security → Overrides
                // toggle for First message is enabled (otherwise call fails instantly).
                ["dynamic_variables"] = BuildDynamicVariables(request)
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
                    "ElevenLabs outbound call failed. Status={Status} Body={Body}",
                    (int)response.StatusCode,
                    Truncate(body, 500));

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
                "ElevenLabs outbound call started. To={To} ConversationId={ConversationId} CallSid={CallSid}",
                toNumber, conversationId ?? "", callSid ?? "");

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
            _logger.LogError(ex, "ElevenLabs outbound call request failed for {ToNumber}", toNumber);
            return new NuviOutboundCallResult
            {
                Success = false,
                Message = "Unable to reach ElevenLabs to place the call. Please try again shortly.",
                ToNumber = toNumber
            };
        }
    }

    private static Dictionary<string, string> BuildDynamicVariables(NuviOutboundCallRequest request)
    {
        var vars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var dateTime = FirstNonEmpty(
            request.PreferredDate,
            request.AvailabilityWindow,
            "within the next 30 days");

        var timeWindow = FirstNonEmpty(
            request.PreferredTimeWindow,
            "any available time during office hours (Pacific Time)");

        // Practices / ElevenLabs are US Pacific — give the agent an explicit "today" so past-date rules work.
        // App timing is always PST/PDT regardless of the server's local timezone (e.g. IST).
        var nowPacific = GetClinicNow();
        vars["current_date"] = nowPacific.ToString("dddd, MMMM d, yyyy");
        vars["current_datetime"] = nowPacific.ToString("yyyy-MM-dd HH:mm");
        vars["current_timezone"] = "America/Los_Angeles (US Pacific Time)";
        vars["today"] = nowPacific.ToString("yyyy-MM-dd");

        // Required by the agent first message / prompt — always send (never omit).
        vars["patient_name"] = FirstNonEmpty(request.PatientName, "a patient");
        vars["date_time"] = dateTime;
        vars["preferred_date"] = dateTime;
        vars["preferred_time_window"] = timeWindow;
        vars["availability_window"] = dateTime;
        vars["appointment_type"] = FirstNonEmpty(request.AppointmentType, request.ChiefComplaint, "dental appointment");
        vars["practice_name"] = FirstNonEmpty(request.PracticeName, request.DoctorName, "your office");
        vars["external_call_id"] = FirstNonEmpty(request.SessionKey, Guid.NewGuid().ToString("N"));

        AddVar(vars, "booking_window_start", request.BookingWindowStart);
        AddVar(vars, "booking_window_end", request.BookingWindowEnd);
        // Capture instruction for post-call analysis / agent behavior.
        vars["appointment_datetime_format"] =
            "When booked, report the exact confirmed slot in Pacific Time as yyyy-MM-dd HH:mm (example: 2026-08-12 09:00). If a range like 9-10 AM was confirmed, use the start time and mention the end in confirmation_notes.";

        AddVar(vars, "patient_phone", request.PatientPhone);
        AddVar(vars, "patient_email", request.PatientEmail);
        AddVar(vars, "insurance_name", request.InsuranceName);
        AddVar(vars, "practice_phone", request.PracticePhone);
        AddVar(vars, "call_context", request.CallContext);
        AddVar(vars, "doctor_name", request.DoctorName);
        AddVar(vars, "call_preference", request.CallPreference);
        AddVar(vars, "chief_complaint", request.ChiefComplaint);
        AddVar(vars, "session_key", request.SessionKey);
        return vars;
    }

    /// <summary>Clinic-local "now" in US Pacific (PST/PDT). Server local time (e.g. IST) is ignored.</summary>
    internal static DateTime GetClinicNow()
    {
        try
        {
            var tz = TimeZoneInfo.FindSystemTimeZoneById(
                OperatingSystem.IsWindows() ? "Pacific Standard Time" : "America/Los_Angeles");
            return TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, tz);
        }
        catch
        {
            return DateTime.UtcNow.AddHours(-8); // PST fallback approx
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
