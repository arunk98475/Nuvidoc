using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Docovee.BLL.Configuration;
using Docovee.logging;
using Microsoft.Extensions.Options;
using Twilio;
using Twilio.Rest.Api.V2010.Account;
using Twilio.Types;

namespace Docovee.BLL.Services;

public sealed class NuviSignupOtpSendResult
{
    public bool Success { get; init; }
    public string Message { get; init; } = string.Empty;
    public string? CodeHash { get; init; }
    public DateTime? ExpiresAtUtc { get; init; }
}

public interface INuviSignupOtpService
{
    Task<NuviSignupOtpSendResult> SendEmailOtpAsync(string email, CancellationToken cancellationToken = default);
    Task<NuviSignupOtpSendResult> SendPhoneOtpAsync(string phone, string channel, CancellationToken cancellationToken = default);
    bool VerifyCode(string code, string? expectedHash, DateTime? expiresAtUtc);
}

/// <summary>Session-scoped OTP for Nuvi new-patient signup (before a Patient row exists).</summary>
public sealed class NuviSignupOtpService : INuviSignupOtpService
{
    private readonly IEmailSender _email;
    private readonly TwilioOptions _twilio;
    private readonly SiteOptions _site;
    private readonly IDocoveeLogger _logger;

    public NuviSignupOtpService(
        IEmailSender email,
        IOptions<TwilioOptions> twilio,
        IOptions<SiteOptions> site,
        IDocoveeLogger logger)
    {
        _email = email;
        _twilio = twilio.Value;
        _site = site.Value;
        _logger = logger;
    }

    public async Task<NuviSignupOtpSendResult> SendEmailOtpAsync(string email, CancellationToken cancellationToken = default)
    {
        if (!_email.IsConfigured)
            return Fail("Email is not configured.");

        if (string.IsNullOrWhiteSpace(email) || !email.Contains('@'))
            return Fail("A valid email address is required.");

        var code = CreateCode();
        var expiryMinutes = ExpiryMinutes();
        var expiresAt = DateTime.UtcNow.AddMinutes(expiryMinutes);
        var site = string.IsNullOrWhiteSpace(_site.Name) ? "NuviDoc" : _site.Name.Trim();
        var subject = $"{site} verification code";
        var text =
            $"Your {site} verification code is {code}.\n\n" +
            $"It expires in {expiryMinutes} minutes.\n\n" +
            "If you did not request this, you can ignore this message.\n";
        var html =
            $"<p>Your {site} verification code is <strong>{code}</strong>.</p>" +
            $"<p>It expires in {expiryMinutes} minutes.</p>" +
            "<p>If you did not request this, you can ignore this message.</p>";

        var send = await _email.SendAsync(email.Trim(), subject, text, html, cancellationToken);
        if (!send.Success)
            return Fail(send.Message);

        _logger.LogInformation("Nuvi signup email OTP sent");
        return new NuviSignupOtpSendResult
        {
            Success = true,
            Message = "We sent a verification code to your email.",
            CodeHash = HashCode(code),
            ExpiresAtUtc = expiresAt
        };
    }

    public Task<NuviSignupOtpSendResult> SendPhoneOtpAsync(
        string phone,
        string channel,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_twilio.AccountSid) || string.IsNullOrWhiteSpace(_twilio.AuthToken))
            return Task.FromResult(Fail("Twilio AccountSid and AuthToken are not configured."));

        var toE164 = ElevenLabsTwilioCallingService.ToE164(phone);
        if (string.IsNullOrWhiteSpace(toE164))
            return Task.FromResult(Fail("A valid phone number is required."));

        var useWhatsApp = string.Equals(
            PhoneVerificationChannels.Normalize(channel),
            PhoneVerificationChannels.WhatsApp,
            StringComparison.OrdinalIgnoreCase);

        var code = CreateCode();
        var expiryMinutes = ExpiryMinutes();
        var expiresAt = DateTime.UtcNow.AddMinutes(expiryMinutes);

        try
        {
            TwilioClient.Init(_twilio.AccountSid.Trim(), _twilio.AuthToken.Trim());
            if (useWhatsApp)
                SendWhatsApp(toE164, code, expiryMinutes);
            else
                SendSms(toE164, code, expiryMinutes);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Nuvi signup phone OTP failed via {Channel}", useWhatsApp ? "whatsapp" : "sms");
            return Task.FromResult(Fail($"Could not send the verification message: {ex.Message}"));
        }

        _logger.LogInformation("Nuvi signup phone OTP sent via {Channel}", useWhatsApp ? "whatsapp" : "sms");
        return Task.FromResult(new NuviSignupOtpSendResult
        {
            Success = true,
            Message = useWhatsApp
                ? "We sent a WhatsApp verification message."
                : "We sent a text with your verification code.",
            CodeHash = HashCode(code),
            ExpiresAtUtc = expiresAt
        });
    }

    public bool VerifyCode(string code, string? expectedHash, DateTime? expiresAtUtc)
    {
        var digits = PhoneNumberHelper.DigitsOnly(code);
        if (digits.Length != 6 || string.IsNullOrWhiteSpace(expectedHash) || !expiresAtUtc.HasValue)
            return false;

        if (expiresAtUtc.Value < DateTime.UtcNow)
            return false;

        var actual = HashCode(digits);
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(expectedHash),
            Encoding.UTF8.GetBytes(actual));
    }

    private void SendSms(string toE164, string code, int expiryMinutes)
    {
        var from = FirstNonEmpty(_twilio.SmsFromNumber, _twilio.FromNumber);
        if (string.IsNullOrWhiteSpace(from))
            throw new InvalidOperationException("Twilio SmsFromNumber or FromNumber is required for SMS verification.");

        MessageResource.Create(new CreateMessageOptions(new PhoneNumber(toE164))
        {
            From = new PhoneNumber(from.Trim()),
            Body = $"Your NuviDoc verification code is {code}. It expires in {expiryMinutes} minutes."
        });
    }

    private void SendWhatsApp(string toE164, string code, int expiryMinutes)
    {
        var from = NormalizeWhatsAppAddress(_twilio.WhatsAppFromNumber);
        var to = NormalizeWhatsAppAddress(toE164);
        if (string.IsNullOrWhiteSpace(from) || string.IsNullOrWhiteSpace(_twilio.WhatsAppContentSid))
            throw new InvalidOperationException("Twilio WhatsAppFromNumber and WhatsAppContentSid are required for WhatsApp verification.");

        var variables = JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["1"] = code,
            ["2"] = $"{expiryMinutes} min"
        });

        MessageResource.Create(new CreateMessageOptions(new PhoneNumber(to))
        {
            From = new PhoneNumber(from),
            ContentSid = _twilio.WhatsAppContentSid.Trim(),
            ContentVariables = variables
        });
    }

    private int ExpiryMinutes() => Math.Clamp(_twilio.VerifyCodeExpiryMinutes, 5, 60);

    private static string CreateCode() => RandomNumberGenerator.GetInt32(100000, 1000000).ToString();

    private static string HashCode(string code)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(code.Trim()));
        return Convert.ToHexString(hash);
    }

    private static string? NormalizeWhatsAppAddress(string? value)
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

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim();

    private static NuviSignupOtpSendResult Fail(string message) =>
        new() { Success = false, Message = message };
}
