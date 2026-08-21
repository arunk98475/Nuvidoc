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

public sealed class DoctorAccountResult
{
    public bool Success { get; init; }
    public string Message { get; init; } = string.Empty;
    public string Channel { get; init; } = string.Empty;
}

public interface IDoctorAccountService
{
    Task<DoctorAccountResult> SendEmailVerificationAsync(int doctorId, string? publicBaseUrl = null, CancellationToken cancellationToken = default);
    Task<DoctorAccountResult> ConfirmEmailVerificationAsync(string token, CancellationToken cancellationToken = default);
    Task<DoctorAccountResult> UpdatePhoneAsync(int doctorId, string? phone, CancellationToken cancellationToken = default);
    Task<DoctorAccountResult> SendPhoneVerificationAsync(int doctorId, string channel, CancellationToken cancellationToken = default);
    Task<DoctorAccountResult> ConfirmPhoneVerificationAsync(int doctorId, string code, CancellationToken cancellationToken = default);
}

public sealed class DoctorAccountService : IDoctorAccountService
{
    private readonly DocoveeDbContext _db;
    private readonly IEmailSender _email;
    private readonly EmailOptions _emailOptions;
    private readonly SiteOptions _site;
    private readonly TwilioOptions _twilio;
    private readonly IHostEnvironment _environment;
    private readonly IDocoveeLogger _logger;

    public DoctorAccountService(
        DocoveeDbContext db,
        IEmailSender email,
        IOptions<EmailOptions> emailOptions,
        IOptions<SiteOptions> site,
        IOptions<TwilioOptions> twilio,
        IHostEnvironment environment,
        IDocoveeLogger logger)
    {
        _db = db;
        _email = email;
        _emailOptions = emailOptions.Value;
        _site = site.Value;
        _twilio = twilio.Value;
        _environment = environment;
        _logger = logger;
    }

    public async Task<DoctorAccountResult> SendEmailVerificationAsync(
        int doctorId,
        string? publicBaseUrl = null,
        CancellationToken cancellationToken = default)
    {
        if (!_email.IsConfigured)
            return Fail("Email is not configured yet. Add SES keys under Email in appsettings.");

        var doctor = await _db.Doctors.FirstOrDefaultAsync(d => d.Id == doctorId, cancellationToken);
        if (doctor == null)
            return Fail("Doctor not found.");

        if (doctor.EmailVerified)
            return Ok("Your email is already verified.");

        var email = doctor.Username?.Trim() ?? "";
        if (!email.Contains('@'))
            return Fail("Your login username must be a valid email address.");

        var raw = CreateToken();
        var minutes = Math.Clamp(_emailOptions.VerificationLinkExpiryMinutes, 10, 24 * 60);
        doctor.EmailVerificationTokenHash = HashToken(raw);
        doctor.EmailVerificationExpiresAtUtc = DateTime.UtcNow.AddMinutes(minutes);
        await _db.SaveChangesAsync(cancellationToken);

        var baseUrl = ResolveBaseUrl(publicBaseUrl);
        var link = $"{baseUrl}/Account/VerifyEmail?token={Uri.EscapeDataString(raw)}&kind=doctor";
        var site = string.IsNullOrWhiteSpace(_site.Name) ? "NuviDoc" : _site.Name;
        var subject = $"Verify your {site} email";
        var text =
            $"Hi {FirstName(doctor.Name)},\n\n" +
            $"Please verify your email for {site} by opening this link (expires in {minutes} minutes):\n\n" +
            $"{link}\n\n" +
            "If you did not request this, you can ignore this email.\n";
        var html =
            $"<p>Hi {Escape(FirstName(doctor.Name))},</p>" +
            $"<p>Please verify your email for {Escape(site)}.</p>" +
            $"<p><a href=\"{Escape(link)}\">Verify email</a></p>" +
            $"<p>This link expires in {minutes} minutes. If you did not request this, ignore this email.</p>";

        var send = await _email.SendAsync(email, subject, text, html, cancellationToken);
        return send.Success
            ? Ok("Check your inbox for a verification link.")
            : Fail(send.Message);
    }

    public async Task<DoctorAccountResult> ConfirmEmailVerificationAsync(
        string token,
        CancellationToken cancellationToken = default)
    {
        var hash = HashToken(token);
        if (string.IsNullOrWhiteSpace(hash))
            return Fail("That verification link is invalid.");

        var doctor = await _db.Doctors.FirstOrDefaultAsync(
            d => d.EmailVerificationTokenHash == hash,
            cancellationToken);
        if (doctor == null)
            return Fail("That verification link is invalid or has already been used.");

        if (doctor.EmailVerificationExpiresAtUtc is null
            || doctor.EmailVerificationExpiresAtUtc.Value < DateTime.UtcNow)
        {
            return Fail("That verification link has expired. Request a new one from Account settings.");
        }

        doctor.EmailVerified = true;
        doctor.EmailVerificationTokenHash = null;
        doctor.EmailVerificationExpiresAtUtc = null;
        await _db.SaveChangesAsync(cancellationToken);
        return Ok("Your email is verified.");
    }

    public async Task<DoctorAccountResult> UpdatePhoneAsync(
        int doctorId,
        string? phone,
        CancellationToken cancellationToken = default)
    {
        var doctor = await _db.Doctors.FirstOrDefaultAsync(d => d.Id == doctorId, cancellationToken);
        if (doctor == null)
            return Fail("Doctor not found.");

        var trimmed = (phone ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
            return Fail("Enter a phone number.");

        var normalized = PhoneNumberHelper.NormalizeLast10(trimmed)
            ?? PhoneNumberHelper.DigitsOnly(trimmed);
        if (string.IsNullOrWhiteSpace(normalized) || normalized.Length < 10)
            return Fail("Enter a valid 10-digit US phone number.");

        var display = FormatPhone(normalized) ?? trimmed;
        if (!string.Equals(doctor.OfficePhoneNumber, display, StringComparison.Ordinal)
            && !string.Equals(PhoneNumberHelper.NormalizeLast10(doctor.OfficePhoneNumber), normalized, StringComparison.Ordinal))
        {
            doctor.OfficePhoneNumber = display;
            doctor.PhoneVerified = false;
            doctor.PhoneVerificationCodeHash = null;
            doctor.PhoneVerificationExpiresAtUtc = null;
            await _db.SaveChangesAsync(cancellationToken);
        }

        return Ok("Phone number updated.");
    }

    public async Task<DoctorAccountResult> SendPhoneVerificationAsync(
        int doctorId,
        string channel,
        CancellationToken cancellationToken = default)
    {
        var doctor = await _db.Doctors.FirstOrDefaultAsync(d => d.Id == doctorId, cancellationToken);
        if (doctor == null)
            return Fail("Doctor not found.");

        var toE164 = ElevenLabsTwilioCallingService.ToE164(doctor.OfficePhoneNumber);
        if (string.IsNullOrWhiteSpace(toE164))
            return Fail("Add a valid phone number first.");

        if (string.IsNullOrWhiteSpace(_twilio.AccountSid) || string.IsNullOrWhiteSpace(_twilio.AuthToken))
            return Fail("Twilio AccountSid and AuthToken are not configured.");

        var useWhatsApp = string.Equals(
            PhoneVerificationChannels.Normalize(channel),
            PhoneVerificationChannels.WhatsApp,
            StringComparison.OrdinalIgnoreCase);

        var code = RandomNumberGenerator.GetInt32(100000, 1000000).ToString();
        var expiryMinutes = Math.Clamp(_twilio.VerifyCodeExpiryMinutes, 5, 60);
        doctor.PhoneVerificationCodeHash = HashCode(code);
        doctor.PhoneVerificationExpiresAtUtc = DateTime.UtcNow.AddMinutes(expiryMinutes);
        doctor.PhoneVerified = false;
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
            _logger.LogError(ex, "Doctor Twilio phone verification send failed via {Channel}", useWhatsApp ? "whatsapp" : "sms");
            return Fail($"Could not send the verification message: {ex.Message}");
        }

        if (useWhatsApp)
        {
            var message = "We sent a WhatsApp verification message.";
            if (_environment.IsDevelopment())
            {
                message +=
                    " If you haven't joined the Twilio sandbox yet, open WhatsApp and send the join code to +1 (415) 523-8886 first, then tap Send again.";
            }

            return new DoctorAccountResult
            {
                Success = true,
                Channel = PhoneVerificationChannels.WhatsApp,
                Message = message
            };
        }

        return new DoctorAccountResult
        {
            Success = true,
            Channel = PhoneVerificationChannels.Sms,
            Message = "We sent a text with your verification code."
        };
    }

    public async Task<DoctorAccountResult> ConfirmPhoneVerificationAsync(
        int doctorId,
        string code,
        CancellationToken cancellationToken = default)
    {
        var digits = PhoneNumberHelper.DigitsOnly(code);
        if (digits.Length != 6)
            return Fail("Enter the 6-digit code we sent.");

        var doctor = await _db.Doctors.FirstOrDefaultAsync(d => d.Id == doctorId, cancellationToken);
        if (doctor == null)
            return Fail("Doctor not found.");

        if (string.IsNullOrWhiteSpace(doctor.PhoneVerificationCodeHash)
            || !doctor.PhoneVerificationExpiresAtUtc.HasValue)
        {
            return Fail("Request a new verification code first.");
        }

        if (doctor.PhoneVerificationExpiresAtUtc.Value < DateTime.UtcNow)
            return Fail("That code expired. Request a new one.");

        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(doctor.PhoneVerificationCodeHash),
                Encoding.UTF8.GetBytes(HashCode(digits))))
        {
            return Fail("That code doesn't match. Try again.");
        }

        doctor.PhoneVerified = true;
        doctor.PhoneVerificationCodeHash = null;
        doctor.PhoneVerificationExpiresAtUtc = null;
        await _db.SaveChangesAsync(cancellationToken);
        return Ok("Your phone number is verified.");
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

    private string ResolveBaseUrl(string? publicBaseUrl)
    {
        var configured = (publicBaseUrl ?? _emailOptions.PublicBaseUrl ?? "").Trim().TrimEnd('/');
        if (!string.IsNullOrWhiteSpace(configured))
            return configured;
        return "https://nuvidoc.com";
    }

    private static string? FormatPhone(string normalized)
    {
        if (normalized.Length == 10)
            return $"({normalized[..3]}) {normalized[3..6]}-{normalized[6..]}";
        return normalized;
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

    private static string CreateToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private static string HashToken(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
            return string.Empty;
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(token.Trim()));
        return Convert.ToHexString(hash);
    }

    private static string HashCode(string code)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(code.Trim()));
        return Convert.ToHexString(hash);
    }

    private static string FirstName(string? fullName)
    {
        var name = (fullName ?? "").Trim();
        if (string.IsNullOrEmpty(name))
            return "there";
        return name.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
    }

    private static string Escape(string value) =>
        System.Net.WebUtility.HtmlEncode(value);

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim();

    private static DoctorAccountResult Ok(string message) =>
        new() { Success = true, Message = message };

    private static DoctorAccountResult Fail(string message) =>
        new() { Success = false, Message = message };
}
