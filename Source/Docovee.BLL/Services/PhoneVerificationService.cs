using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Docovee.BLL.Configuration;
using Docovee.DS;
using Docovee.logging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Twilio;
using Twilio.Rest.Api.V2010.Account;
using Twilio.Types;

namespace Docovee.BLL.Services;

public static class PhoneVerificationChannels
{
    public const string Sms = "sms";
    public const string WhatsApp = "whatsapp";

    public static bool IsKnown(string? channel) =>
        string.Equals(channel, Sms, StringComparison.OrdinalIgnoreCase)
        || string.Equals(channel, WhatsApp, StringComparison.OrdinalIgnoreCase);

    public static string Normalize(string? channel) =>
        string.Equals(channel, WhatsApp, StringComparison.OrdinalIgnoreCase) ? WhatsApp : Sms;
}

public sealed class PhoneVerificationSendResult
{
    public bool Success { get; init; }
    public string Message { get; init; } = string.Empty;
    public string Channel { get; init; } = string.Empty;
}

public sealed class PhoneVerificationCheckResult
{
    public bool Success { get; init; }
    public string Message { get; init; } = string.Empty;
}

public interface IPhoneVerificationService
{
    Task<PhoneVerificationSendResult> SendCodeAsync(
        int patientId,
        string channel,
        CancellationToken cancellationToken = default,
        bool resetVerifiedStatus = true);
    Task<PhoneVerificationCheckResult> VerifyCodeAsync(int patientId, string code, CancellationToken cancellationToken = default);
}

public sealed class PhoneVerificationService : IPhoneVerificationService
{
    private readonly DocoveeDbContext _db;
    private readonly TwilioOptions _twilio;
    private readonly IHostEnvironment _environment;
    private readonly IDocoveeLogger _logger;

    public PhoneVerificationService(
        DocoveeDbContext db,
        IOptions<TwilioOptions> twilio,
        IHostEnvironment environment,
        IDocoveeLogger logger)
    {
        _db = db;
        _twilio = twilio.Value;
        _environment = environment;
        _logger = logger;
    }

    public async Task<PhoneVerificationSendResult> SendCodeAsync(
        int patientId,
        string channel,
        CancellationToken cancellationToken = default,
        bool resetVerifiedStatus = true)
    {
        var patient = await _db.Patients.FirstOrDefaultAsync(p => p.Id == patientId, cancellationToken);
        if (patient == null)
            return FailSend("Patient not found.");

        var toE164 = ElevenLabsTwilioCallingService.ToE164(patient.Phone);
        if (string.IsNullOrWhiteSpace(toE164))
            return FailSend("Add a valid phone number first.");

        if (string.IsNullOrWhiteSpace(_twilio.AccountSid) || string.IsNullOrWhiteSpace(_twilio.AuthToken))
            return FailSend("Twilio AccountSid and AuthToken are not configured.");

        var useWhatsApp = string.Equals(
            PhoneVerificationChannels.Normalize(channel),
            PhoneVerificationChannels.WhatsApp,
            StringComparison.OrdinalIgnoreCase);

        var code = RandomNumberGenerator.GetInt32(100000, 1000000).ToString();
        var expiryMinutes = Math.Clamp(_twilio.VerifyCodeExpiryMinutes, 5, 60);
        patient.PhoneVerificationCodeHash = HashCode(code);
        patient.PhoneVerificationExpiresAtUtc = DateTime.UtcNow.AddMinutes(expiryMinutes);
        if (resetVerifiedStatus)
            patient.PhoneVerified = false;
        await _db.SaveChangesAsync(cancellationToken);

        try
        {
            TwilioClient.Init(_twilio.AccountSid.Trim(), _twilio.AuthToken.Trim());
            if (useWhatsApp)
                SendWhatsAppTemplate(toE164, code, expiryMinutes);
            else
                SendSms(toE164, code, expiryMinutes);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Twilio phone verification send failed via {Channel}", useWhatsApp ? "whatsapp" : "sms");
            return FailSend($"Could not send the verification message: {ex.Message}");
        }

        _logger.LogInformation(
            "Phone verification code sent via {Channel}",
            useWhatsApp ? "whatsapp" : "sms");

        if (useWhatsApp)
        {
            var message = "We sent a WhatsApp verification message.";
            if (_environment.IsDevelopment())
            {
                message +=
                    " If you haven't joined the Twilio sandbox yet, open WhatsApp and send the join code to +1 (415) 523-8886 first, then tap Send again.";
            }

            return new PhoneVerificationSendResult
            {
                Success = true,
                Channel = PhoneVerificationChannels.WhatsApp,
                Message = message
            };
        }

        return new PhoneVerificationSendResult
        {
            Success = true,
            Channel = PhoneVerificationChannels.Sms,
            Message = "We sent a text with your verification code."
        };
    }

    public async Task<PhoneVerificationCheckResult> VerifyCodeAsync(
        int patientId,
        string code,
        CancellationToken cancellationToken = default)
    {
        var digits = PhoneNumberHelper.DigitsOnly(code);
        if (digits.Length != 6)
            return new PhoneVerificationCheckResult { Success = false, Message = "Enter the 6-digit code we sent." };

        var patient = await _db.Patients.FirstOrDefaultAsync(p => p.Id == patientId, cancellationToken);
        if (patient == null)
            return new PhoneVerificationCheckResult { Success = false, Message = "Patient not found." };

        if (string.IsNullOrWhiteSpace(patient.PhoneVerificationCodeHash)
            || !patient.PhoneVerificationExpiresAtUtc.HasValue)
        {
            return new PhoneVerificationCheckResult
            {
                Success = false,
                Message = "Request a new verification code first."
            };
        }

        if (patient.PhoneVerificationExpiresAtUtc.Value < DateTime.UtcNow)
        {
            return new PhoneVerificationCheckResult
            {
                Success = false,
                Message = "That code expired. Request a new one."
            };
        }

        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(patient.PhoneVerificationCodeHash),
                Encoding.UTF8.GetBytes(HashCode(digits))))
        {
            return new PhoneVerificationCheckResult { Success = false, Message = "That code doesn't match. Try again." };
        }

        patient.PhoneVerified = true;
        patient.PhoneVerificationCodeHash = null;
        patient.PhoneVerificationExpiresAtUtc = null;
        await _db.SaveChangesAsync(cancellationToken);

        return new PhoneVerificationCheckResult { Success = true, Message = "Your phone number is verified." };
    }

    private void SendWhatsAppTemplate(string toE164, string code, int expiryMinutes)
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

        var options = new CreateMessageOptions(new PhoneNumber(to))
        {
            From = new PhoneNumber(from),
            ContentSid = _twilio.WhatsAppContentSid.Trim(),
            ContentVariables = variables
        };
        MessageResource.Create(options);
    }

    private void SendSms(string toE164, string code, int expiryMinutes)
    {
        var from = FirstNonEmpty(_twilio.SmsFromNumber, _twilio.FromNumber);
        if (string.IsNullOrWhiteSpace(from))
            throw new InvalidOperationException("Twilio SmsFromNumber or FromNumber is required for SMS verification.");

        var options = new CreateMessageOptions(new PhoneNumber(toE164))
        {
            From = new PhoneNumber(from.Trim()),
            Body = $"Your NuviDoc verification code is {code}. It expires in {expiryMinutes} minutes."
        };
        MessageResource.Create(options);
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

    private static string HashCode(string code)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(code.Trim()));
        return Convert.ToHexString(hash);
    }

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim();

    private static PhoneVerificationSendResult FailSend(string message) =>
        new() { Success = false, Message = message };
}
