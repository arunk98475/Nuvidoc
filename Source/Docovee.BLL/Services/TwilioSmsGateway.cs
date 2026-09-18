using System.Net.Http.Json;
using System.Text.Json;
using Docovee.BLL.Configuration;
using Docovee.logging;
using Microsoft.Extensions.Options;
using Twilio;
using Twilio.Rest.Api.V2010.Account;
using Twilio.Types;

namespace Docovee.BLL.Services;

public sealed class TwilioSmsSendResult
{
    public bool Success { get; init; }
    public string? Sid { get; init; }
    public string? Error { get; init; }
    public bool UsedMock { get; init; }
}

public interface ITwilioSmsGateway
{
    /// <summary>
    /// Sends SMS. Applies <see cref="TwilioOptions.OutboundOverrideToNumber"/> when set.
    /// When <see cref="TwilioOptions.UseMock"/> is true, posts to the local TwilioMock app instead of Twilio.
    /// </summary>
    TwilioSmsSendResult SendSms(string? intendedTo, string body, string? fromOverride = null);

    /// <summary>
    /// Sends WhatsApp (optional Content template). Applies outbound override when set.
    /// </summary>
    TwilioSmsSendResult SendWhatsApp(
        string? intendedTo,
        string? body = null,
        string? contentSid = null,
        string? contentVariablesJson = null,
        string? fromOverride = null);
}

public sealed class TwilioSmsGateway : ITwilioSmsGateway
{
    private readonly TwilioOptions _twilio;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IDocoveeLogger _logger;

    public TwilioSmsGateway(
        IOptions<TwilioOptions> twilio,
        IHttpClientFactory httpClientFactory,
        IDocoveeLogger logger)
    {
        _twilio = twilio.Value;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public TwilioSmsSendResult SendSms(string? intendedTo, string body, string? fromOverride = null)
    {
        var intended = ElevenLabsTwilioCallingService.ToE164(intendedTo);
        var to = TwilioOutboundRouting.ResolveToNumber(_twilio, intendedTo);
        if (string.IsNullOrWhiteSpace(to))
            return Fail("Missing or invalid SMS destination.");

        var from = FirstNonEmpty(fromOverride, _twilio.SmsFromNumber, _twilio.FromNumber);
        if (string.IsNullOrWhiteSpace(from))
            return Fail("Missing SMS from number.");

        if (_twilio.UseMock)
            return SendToMock(intended, to, from.Trim(), body, isWhatsApp: false, contentSid: null, contentVariables: null);

        return SendViaTwilio(to, from.Trim(), body, contentSid: null, contentVariables: null);
    }

    public TwilioSmsSendResult SendWhatsApp(
        string? intendedTo,
        string? body = null,
        string? contentSid = null,
        string? contentVariablesJson = null,
        string? fromOverride = null)
    {
        var intendedE164 = ElevenLabsTwilioCallingService.ToE164(StripWhatsApp(intendedTo));
        var to = TwilioOutboundRouting.ResolveWhatsAppTo(_twilio, intendedTo);
        if (string.IsNullOrWhiteSpace(to))
            return Fail("Missing or invalid WhatsApp destination.");

        var from = NormalizeWhatsApp(FirstNonEmpty(fromOverride, _twilio.WhatsAppFromNumber));
        if (string.IsNullOrWhiteSpace(from))
            return Fail("Missing WhatsApp from number.");

        if (_twilio.UseMock)
        {
            return SendToMock(
                intendedE164,
                StripWhatsApp(to) ?? to,
                StripWhatsApp(from) ?? from,
                body ?? $"[WhatsApp template {contentSid}]",
                isWhatsApp: true,
                contentSid,
                contentVariablesJson);
        }

        return SendViaTwilio(to, from!, body, contentSid, contentVariablesJson);
    }

    private TwilioSmsSendResult SendViaTwilio(
        string to,
        string from,
        string? body,
        string? contentSid,
        string? contentVariables)
    {
        if (string.IsNullOrWhiteSpace(_twilio.AccountSid) || string.IsNullOrWhiteSpace(_twilio.AuthToken))
            return Fail("Twilio credentials are not configured.");

        try
        {
            TwilioClient.Init(_twilio.AccountSid.Trim(), _twilio.AuthToken.Trim());
            var options = new CreateMessageOptions(new PhoneNumber(to))
            {
                From = new PhoneNumber(from)
            };
            if (!string.IsNullOrWhiteSpace(contentSid))
            {
                options.ContentSid = contentSid.Trim();
                if (!string.IsNullOrWhiteSpace(contentVariables))
                    options.ContentVariables = contentVariables;
            }
            else
            {
                options.Body = body ?? "";
            }

            var msg = MessageResource.Create(options);
            return new TwilioSmsSendResult { Success = true, Sid = msg.Sid };
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Twilio send failed: {Error}", ex.Message);
            return Fail(ex.Message);
        }
    }

    private TwilioSmsSendResult SendToMock(
        string? intendedTo,
        string actualTo,
        string from,
        string body,
        bool isWhatsApp,
        string? contentSid,
        string? contentVariables)
    {
        var baseUrl = (_twilio.MockBaseUrl ?? "").Trim().TrimEnd('/');
        if (string.IsNullOrWhiteSpace(baseUrl))
            return Fail("Twilio UseMock is true but MockBaseUrl is empty.");

        try
        {
            var client = _httpClientFactory.CreateClient("TwilioMock");
            var payload = new
            {
                intendedTo,
                to = actualTo,
                from,
                body,
                isWhatsApp,
                contentSid,
                contentVariables
            };
            using var response = client.PostAsJsonAsync($"{baseUrl}/api/outbound", payload)
                .GetAwaiter()
                .GetResult();
            if (!response.IsSuccessStatusCode)
            {
                var err = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                return Fail($"Mock SMS failed ({(int)response.StatusCode}): {Truncate(err, 200)}");
            }

            using var doc = JsonDocument.Parse(
                response.Content.ReadAsStringAsync().GetAwaiter().GetResult());
            var sid = doc.RootElement.TryGetProperty("sid", out var sidEl)
                ? sidEl.GetString()
                : $"SM{Guid.NewGuid():N}"[..34];

            _logger.LogInformation(
                "Twilio mock SMS sent. Intended={Intended} Actual={Actual} Sid={Sid}",
                intendedTo, actualTo, sid);
            return new TwilioSmsSendResult { Success = true, Sid = sid, UsedMock = true };
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Twilio mock send failed: " + ex.Message);
            return Fail($"Mock SMS unavailable: {ex.Message}. Is TwilioMock running at {baseUrl}?");
        }
    }

    private static TwilioSmsSendResult Fail(string error) =>
        new() { Success = false, Error = error };

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim();

    private static string? StripWhatsApp(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return value;
        var trimmed = value.Trim();
        if (trimmed.StartsWith("whatsapp:", StringComparison.OrdinalIgnoreCase))
            return trimmed["whatsapp:".Length..].Trim();
        return trimmed;
    }

    private static string? NormalizeWhatsApp(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var trimmed = value.Trim();
        if (trimmed.StartsWith("whatsapp:", StringComparison.OrdinalIgnoreCase))
            return trimmed;
        var e164 = ElevenLabsTwilioCallingService.ToE164(trimmed) ?? trimmed;
        return e164.StartsWith("whatsapp:", StringComparison.OrdinalIgnoreCase)
            ? e164
            : "whatsapp:" + e164;
    }

    private static string Truncate(string? value, int max)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;
        return value.Length <= max ? value : value[..max] + "…";
    }
}
