using System.Security.Cryptography;
using System.Text;
using Docovee.BLL.Configuration;
using Docovee.DS;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Docovee.BLL.Services;

public sealed class PatientEmailAuthResult
{
    public bool Success { get; init; }
    public string Message { get; init; } = string.Empty;
}

public interface IPatientEmailAuthService
{
    Task<PatientEmailAuthResult> SendEmailVerificationAsync(int patientId, string? publicBaseUrl = null, CancellationToken cancellationToken = default);
    Task<PatientEmailAuthResult> ConfirmEmailVerificationAsync(string token, CancellationToken cancellationToken = default);
    Task<PatientEmailAuthResult> SendPasswordResetAsync(string email, string? publicBaseUrl = null, CancellationToken cancellationToken = default);
    Task<PatientEmailAuthResult> ResetPasswordAsync(string token, string newPassword, CancellationToken cancellationToken = default);
    Task<bool> IsPasswordResetTokenValidAsync(string token, CancellationToken cancellationToken = default);
}

public sealed class PatientEmailAuthService : IPatientEmailAuthService
{
    private readonly DocoveeDbContext _db;
    private readonly IEmailSender _email;
    private readonly EmailOptions _options;
    private readonly SiteOptions _site;
    private readonly PasswordHasher<DS.Entities.Patient> _hasher = new();

    public PatientEmailAuthService(
        DocoveeDbContext db,
        IEmailSender email,
        IOptions<EmailOptions> options,
        IOptions<SiteOptions> site)
    {
        _db = db;
        _email = email;
        _options = options.Value;
        _site = site.Value;
    }

    public async Task<PatientEmailAuthResult> SendEmailVerificationAsync(
        int patientId,
        string? publicBaseUrl = null,
        CancellationToken cancellationToken = default)
    {
        if (!_email.IsConfigured)
            return Fail("Email is not configured yet. Add SES keys under Email in appsettings.");

        var patient = await _db.Patients.FirstOrDefaultAsync(p => p.Id == patientId, cancellationToken);
        if (patient == null)
            return Fail("Patient not found.");

        if (patient.EmailVerified)
            return Ok("Your email is already verified.");

        var email = patient.Username?.Trim() ?? "";
        if (!email.Contains('@'))
            return Fail("Your login username must be a valid email address.");

        var raw = CreateToken();
        var minutes = Math.Clamp(_options.VerificationLinkExpiryMinutes, 10, 24 * 60);
        patient.EmailVerificationTokenHash = HashToken(raw);
        patient.EmailVerificationExpiresAtUtc = DateTime.UtcNow.AddMinutes(minutes);
        await _db.SaveChangesAsync(cancellationToken);

        var baseUrl = ResolveBaseUrl(publicBaseUrl);
        var link = $"{baseUrl}/Account/VerifyEmail?token={Uri.EscapeDataString(raw)}";
        var site = string.IsNullOrWhiteSpace(_site.Name) ? "NuviDoc" : _site.Name;
        var subject = $"Verify your {site} email";
        var text =
            $"Hi {FirstName(patient.FullName)},\n\n" +
            $"Please verify your email for {site} by opening this link (expires in {minutes} minutes):\n\n" +
            $"{link}\n\n" +
            "If you did not request this, you can ignore this email.\n";
        var html =
            $"<p>Hi {Escape(FirstName(patient.FullName))},</p>" +
            $"<p>Please verify your email for {Escape(site)}.</p>" +
            $"<p><a href=\"{Escape(link)}\">Verify email</a></p>" +
            $"<p>This link expires in {minutes} minutes. If you did not request this, ignore this email.</p>";

        var send = await _email.SendAsync(email, subject, text, html, cancellationToken);
        return send.Success
            ? Ok("Check your inbox for a verification link.")
            : Fail(send.Message);
    }

    public async Task<PatientEmailAuthResult> ConfirmEmailVerificationAsync(
        string token,
        CancellationToken cancellationToken = default)
    {
        var hash = HashToken(token);
        if (string.IsNullOrWhiteSpace(hash))
            return Fail("That verification link is invalid.");

        var patient = await _db.Patients.FirstOrDefaultAsync(
            p => p.EmailVerificationTokenHash == hash,
            cancellationToken);
        if (patient == null)
            return Fail("That verification link is invalid or has already been used.");

        if (patient.EmailVerificationExpiresAtUtc is null
            || patient.EmailVerificationExpiresAtUtc.Value < DateTime.UtcNow)
        {
            return Fail("That verification link has expired. Request a new one from Login and security.");
        }

        patient.EmailVerified = true;
        patient.EmailVerificationTokenHash = null;
        patient.EmailVerificationExpiresAtUtc = null;
        await _db.SaveChangesAsync(cancellationToken);
        return Ok("Your email is verified.");
    }

    public async Task<PatientEmailAuthResult> SendPasswordResetAsync(
        string email,
        string? publicBaseUrl = null,
        CancellationToken cancellationToken = default)
    {
        // Always return the same message to avoid account enumeration.
        const string generic = "If an account exists for that email, we sent a password reset link.";

        if (!_email.IsConfigured)
            return Fail("Email is not configured yet. Add SES keys under Email in appsettings.");

        var normalized = (email ?? string.Empty).Trim();
        if (!normalized.Contains('@'))
            return Ok(generic);

        var patient = await _db.Patients.FirstOrDefaultAsync(
            p => p.Username == normalized,
            cancellationToken);
        if (patient == null)
            return Ok(generic);

        var raw = CreateToken();
        var minutes = Math.Clamp(_options.PasswordResetLinkExpiryMinutes, 10, 24 * 60);
        patient.PasswordResetTokenHash = HashToken(raw);
        patient.PasswordResetExpiresAtUtc = DateTime.UtcNow.AddMinutes(minutes);
        await _db.SaveChangesAsync(cancellationToken);

        var baseUrl = ResolveBaseUrl(publicBaseUrl);
        var link = $"{baseUrl}/Account/ResetPassword?token={Uri.EscapeDataString(raw)}";
        var site = string.IsNullOrWhiteSpace(_site.Name) ? "NuviDoc" : _site.Name;
        var subject = $"Reset your {site} password";
        var text =
            $"Hi {FirstName(patient.FullName)},\n\n" +
            $"We received a request to reset your {site} password. Open this link (expires in {minutes} minutes):\n\n" +
            $"{link}\n\n" +
            "If you did not request this, you can ignore this email.\n";
        var html =
            $"<p>Hi {Escape(FirstName(patient.FullName))},</p>" +
            $"<p>We received a request to reset your {Escape(site)} password.</p>" +
            $"<p><a href=\"{Escape(link)}\">Reset password</a></p>" +
            $"<p>This link expires in {minutes} minutes. If you did not request this, ignore this email.</p>";

        var send = await _email.SendAsync(normalized, subject, text, html, cancellationToken);
        return send.Success ? Ok(generic) : Fail(send.Message);
    }

    public async Task<bool> IsPasswordResetTokenValidAsync(string token, CancellationToken cancellationToken = default)
    {
        var hash = HashToken(token);
        if (string.IsNullOrWhiteSpace(hash))
            return false;

        return await _db.Patients.AsNoTracking().AnyAsync(
            p => p.PasswordResetTokenHash == hash
                 && p.PasswordResetExpiresAtUtc != null
                 && p.PasswordResetExpiresAtUtc > DateTime.UtcNow,
            cancellationToken);
    }

    public async Task<PatientEmailAuthResult> ResetPasswordAsync(
        string token,
        string newPassword,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(newPassword) || newPassword.Length < 6)
            return Fail("Password must be at least 6 characters.");

        var hash = HashToken(token);
        if (string.IsNullOrWhiteSpace(hash))
            return Fail("That reset link is invalid.");

        var patient = await _db.Patients.FirstOrDefaultAsync(
            p => p.PasswordResetTokenHash == hash,
            cancellationToken);
        if (patient == null)
            return Fail("That reset link is invalid or has already been used.");

        if (patient.PasswordResetExpiresAtUtc is null
            || patient.PasswordResetExpiresAtUtc.Value < DateTime.UtcNow)
        {
            return Fail("That reset link has expired. Request a new one.");
        }

        patient.PasswordHash = _hasher.HashPassword(patient, newPassword);
        patient.PasswordResetTokenHash = null;
        patient.PasswordResetExpiresAtUtc = null;
        await _db.SaveChangesAsync(cancellationToken);
        return Ok("Your password has been updated. You can sign in now.");
    }

    private string ResolveBaseUrl(string? publicBaseUrl)
    {
        var configured = (publicBaseUrl ?? _options.PublicBaseUrl ?? "").Trim().TrimEnd('/');
        if (!string.IsNullOrWhiteSpace(configured))
            return configured;
        return "https://nuvidoc.com";
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

    private static string FirstName(string? fullName)
    {
        var name = (fullName ?? "").Trim();
        if (string.IsNullOrEmpty(name))
            return "there";
        return name.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
    }

    private static string Escape(string value) =>
        System.Net.WebUtility.HtmlEncode(value);

    private static PatientEmailAuthResult Ok(string message) =>
        new() { Success = true, Message = message };

    private static PatientEmailAuthResult Fail(string message) =>
        new() { Success = false, Message = message };
}
