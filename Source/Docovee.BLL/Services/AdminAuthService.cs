using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Docovee.BLL.Audit;
using Docovee.BLL.Auth;
using Docovee.BLL.Configuration;
using Docovee.BLL.Security;
using Docovee.DS;
using Docovee.DS.Entities;
using Docovee.DS.Models;
using Docovee.logging;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Twilio;
using Twilio.Rest.Api.V2010.Account;
using Twilio.Types;

namespace Docovee.BLL.Services;

public interface IAdminAuthService
{
    Task<AdminLoginResult> StartLoginAsync(
        AdminLoginRequest request,
        HttpContext httpContext,
        CancellationToken cancellationToken = default);
    Task<AdminLoginResult> CompleteLoginAsync(
        string sessionToken,
        string code,
        HttpContext httpContext,
        CancellationToken cancellationToken = default);
    Task<AdminLoginResult> ResendOtpAsync(string sessionToken, CancellationToken cancellationToken = default);
    Task LogoutAsync(HttpContext httpContext);
}

public sealed class AdminAuthService : IAdminAuthService
{
    public const string AdminRole = AuthRoles.Admin;
    private const string PendingLoginCachePrefix = "admin-login-otp:";

    private readonly DocoveeDbContext _db;
    private readonly IDocoveeLogger _logger;
    private readonly IAuditTrailService _audit;
    private readonly ILoginLockoutService _lockout;
    private readonly IMemoryCache _cache;
    private readonly IEmailSender _email;
    private readonly AdminOptions _adminOptions;
    private readonly TwilioOptions _twilio;
    private readonly SiteOptions _site;
    private readonly PasswordHasher<Admin> _passwordHasher = new();

    public AdminAuthService(
        DocoveeDbContext db,
        IDocoveeLogger logger,
        IAuditTrailService audit,
        ILoginLockoutService lockout,
        IMemoryCache cache,
        IEmailSender email,
        IOptions<AdminOptions> adminOptions,
        IOptions<TwilioOptions> twilio,
        IOptions<SiteOptions> site)
    {
        _db = db;
        _logger = logger;
        _audit = audit;
        _lockout = lockout;
        _cache = cache;
        _email = email;
        _adminOptions = adminOptions.Value;
        _twilio = twilio.Value;
        _site = site.Value;
    }

    public async Task<AdminLoginResult> StartLoginAsync(
        AdminLoginRequest request,
        HttpContext httpContext,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.Username) || string.IsNullOrWhiteSpace(request.Password))
            return Fail("Username and password are required.");

        if (_lockout.IsLockedOut(AdminRole, request.Username))
        {
            await LogLoginFailedAsync("Account locked out.", cancellationToken);
            return Fail(LockedMessage(request.Username));
        }

        var admin = await _db.Admins
            .FirstOrDefaultAsync(a => a.Username == request.Username, cancellationToken);

        if (admin == null)
        {
            _lockout.RecordFailure(AdminRole, request.Username);
            await LogLoginFailedAsync("Invalid username or password.", cancellationToken);
            return Fail(LockedMessageOrInvalid(request.Username));
        }

        if (_passwordHasher.VerifyHashedPassword(admin, admin.PasswordHash, request.Password)
            == PasswordVerificationResult.Failed)
        {
            _lockout.RecordFailure(AdminRole, request.Username);
            await LogLoginFailedAsync("Invalid username or password.", cancellationToken);
            return Fail(LockedMessageOrInvalid(request.Username));
        }

        _lockout.Reset(AdminRole, request.Username);

        if (!_adminOptions.RequiresOtp)
        {
            await SignInAsync(httpContext, admin, cancellationToken, otpVerified: false);
            return new AdminLoginResult { Success = true };
        }

        return await BeginOtpStepAsync(admin, cancellationToken);
    }

    public async Task<AdminLoginResult> CompleteLoginAsync(
        string sessionToken,
        string code,
        HttpContext httpContext,
        CancellationToken cancellationToken = default)
    {
        if (!TryGetPendingLogin(sessionToken, out var pending) || pending == null)
            return Fail("Your sign-in session expired. Start again with username and password.");

        if (pending.ExpiresAtUtc < DateTime.UtcNow)
        {
            _cache.Remove(CacheKey(sessionToken));
            return Fail("That code expired. Sign in again to receive a new code.");
        }

        var digits = PhoneNumberHelper.DigitsOnly(code);
        if (digits.Length != 6)
            return Fail("Enter the 6-digit code we sent.");

        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(pending.OtpHash),
                Encoding.UTF8.GetBytes(HashCode(digits))))
        {
            return Fail("That code doesn't match. Try again.");
        }

        _cache.Remove(CacheKey(sessionToken));

        var admin = await _db.Admins.FirstOrDefaultAsync(a => a.Id == pending.AdminId, cancellationToken);
        if (admin == null)
            return Fail("Admin account not found.");

        await SignInAsync(httpContext, admin, cancellationToken, otpVerified: true);
        return new AdminLoginResult { Success = true };
    }

    public async Task<AdminLoginResult> ResendOtpAsync(
        string sessionToken,
        CancellationToken cancellationToken = default)
    {
        if (!TryGetPendingLogin(sessionToken, out var pending) || pending == null)
            return Fail("Your sign-in session expired. Start again with username and password.");

        var admin = await _db.Admins.FirstOrDefaultAsync(a => a.Id == pending.AdminId, cancellationToken);
        if (admin == null)
            return Fail("Admin account not found.");

        var send = await SendOtpAsync(admin.Username, cancellationToken);
        if (!send.Success)
            return Fail(send.Error ?? "Could not resend the verification code.");

        var expiryMinutes = Math.Clamp(_twilio.VerifyCodeExpiryMinutes, 5, 60);
        var updated = pending with
        {
            OtpHash = send.OtpHash,
            ExpiresAtUtc = DateTime.UtcNow.AddMinutes(expiryMinutes)
        };
        _cache.Set(CacheKey(sessionToken), updated, updated.ExpiresAtUtc);

        return new AdminLoginResult
        {
            Success = true,
            RequiresOtp = true,
            OtpSessionToken = sessionToken,
            OtpMessage = send.Message
        };
    }

    public async Task LogoutAsync(HttpContext httpContext)
    {
        var ctx = _audit.GetCurrentContext();
        if (httpContext.User?.Identity?.IsAuthenticated == true)
        {
            await _audit.LogAsync(_db, new AuditLogRequest
            {
                Action = AuditActions.Logout,
                EntityType = AuditEntityTypes.Authentication,
                EntityId = ctx.ActorUserId,
                Summary = $"{AdminRole} logout",
                Context = ctx
            });
        }

        await httpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    }

    private async Task<AdminLoginResult> BeginOtpStepAsync(Admin admin, CancellationToken cancellationToken)
    {
        var send = await SendOtpAsync(admin.Username, cancellationToken);
        if (!send.Success)
            return Fail(send.Error ?? "Could not send the verification code.");

        var expiryMinutes = Math.Clamp(_twilio.VerifyCodeExpiryMinutes, 5, 60);
        var token = CreateSessionToken();
        var pending = new AdminPendingLoginCacheEntry
        {
            AdminId = admin.Id,
            Username = admin.Username,
            OtpHash = send.OtpHash,
            ExpiresAtUtc = DateTime.UtcNow.AddMinutes(expiryMinutes)
        };
        _cache.Set(CacheKey(token), pending, pending.ExpiresAtUtc);

        return new AdminLoginResult
        {
            Success = true,
            RequiresOtp = true,
            OtpSessionToken = token,
            OtpMessage = send.Message
        };
    }

    private async Task<(bool Success, string? Error, string OtpHash, string Message)> SendOtpAsync(
        string adminUsername,
        CancellationToken cancellationToken)
    {
        var code = RandomNumberGenerator.GetInt32(100000, 1000000).ToString();
        var expiryMinutes = Math.Clamp(_twilio.VerifyCodeExpiryMinutes, 5, 60);
        var channels = new List<string>();
        var errors = new List<string>();

        if (!string.IsNullOrWhiteSpace(_adminOptions.Email))
        {
            var emailResult = await SendEmailOtpAsync(_adminOptions.Email.Trim(), code, expiryMinutes, cancellationToken);
            if (emailResult.Success)
                channels.Add("email");
            else
                errors.Add(emailResult.Message);
        }

        if (!string.IsNullOrWhiteSpace(_adminOptions.ResolvedSms))
        {
            var smsResult = SendSmsOtp(_adminOptions.ResolvedSms, code, expiryMinutes);
            if (smsResult.Success)
                channels.Add("SMS");
            else
                errors.Add(smsResult.Message);
        }

        if (!string.IsNullOrWhiteSpace(_adminOptions.ResolvedWhatsApp))
        {
            var waResult = SendWhatsAppOtp(_adminOptions.ResolvedWhatsApp, code, expiryMinutes);
            if (waResult.Success)
                channels.Add("WhatsApp");
            else
                errors.Add(waResult.Message);
        }

        if (channels.Count == 0)
        {
            var detail = errors.Count > 0 ? " " + string.Join(" ", errors) : "";
            return (false, "Could not send an admin verification code." + detail, "", "");
        }

        _logger.LogInformation(
            "Admin login OTP sent to {ChannelCount} channel(s) for user {AdminUsername}",
            channels.Count,
            adminUsername);

        var message = channels.Count == 1
            ? $"We sent a verification code via {channels[0]}."
            : $"We sent the same verification code via {string.Join(", ", channels)}.";

        if (errors.Count > 0)
            message += " Some channels could not be reached; use a code from a channel that succeeded.";

        return (true, null, HashCode(code), message);
    }

    private async Task<(bool Success, string Message)> SendEmailOtpAsync(
        string toAddress,
        string code,
        int expiryMinutes,
        CancellationToken cancellationToken)
    {
        if (!_email.IsConfigured)
            return (false, "Email is not configured.");

        var site = string.IsNullOrWhiteSpace(_site.Name) ? "NuviDoc" : _site.Name;
        var subject = $"{site} admin sign-in code";
        var text =
            $"Your {site} admin sign-in code is {code}.\n\n" +
            $"It expires in {expiryMinutes} minutes.\n\n" +
            "If you did not try to sign in, ignore this message.\n";
        var html =
            $"<p>Your {site} admin sign-in code is <strong>{code}</strong>.</p>" +
            $"<p>It expires in {expiryMinutes} minutes.</p>" +
            "<p>If you did not try to sign in, ignore this message.</p>";

        var send = await _email.SendAsync(toAddress, subject, text, html, cancellationToken);
        return send.Success
            ? (true, send.Message)
            : (false, send.Message);
    }

    private (bool Success, string Message) SendSmsOtp(string phone, string code, int expiryMinutes)
    {
        if (string.IsNullOrWhiteSpace(_twilio.AccountSid) || string.IsNullOrWhiteSpace(_twilio.AuthToken))
            return (false, "Twilio is not configured for SMS.");

        var toE164 = ElevenLabsTwilioCallingService.ToE164(phone);
        if (string.IsNullOrWhiteSpace(toE164))
            return (false, "Admin SMS number is invalid.");

        var from = FirstNonEmpty(_twilio.SmsFromNumber, _twilio.FromNumber);
        if (string.IsNullOrWhiteSpace(from))
            return (false, "Twilio SmsFromNumber or FromNumber is required.");

        try
        {
            TwilioClient.Init(_twilio.AccountSid.Trim(), _twilio.AuthToken.Trim());
            MessageResource.Create(new CreateMessageOptions(new PhoneNumber(toE164))
            {
                From = new PhoneNumber(from.Trim()),
                Body = $"Your NuviDoc admin sign-in code is {code}. It expires in {expiryMinutes} minutes."
            });
            return (true, "SMS sent.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Admin login SMS OTP failed");
            return (false, "SMS delivery failed.");
        }
    }

    private (bool Success, string Message) SendWhatsAppOtp(string phone, string code, int expiryMinutes)
    {
        if (string.IsNullOrWhiteSpace(_twilio.AccountSid) || string.IsNullOrWhiteSpace(_twilio.AuthToken))
            return (false, "Twilio is not configured for WhatsApp.");

        var toE164 = ElevenLabsTwilioCallingService.ToE164(phone);
        if (string.IsNullOrWhiteSpace(toE164))
            return (false, "Admin WhatsApp number is invalid.");

        var from = NormalizeWhatsAppAddress(_twilio.WhatsAppFromNumber);
        var to = NormalizeWhatsAppAddress(toE164);
        if (string.IsNullOrWhiteSpace(from) || string.IsNullOrWhiteSpace(_twilio.WhatsAppContentSid))
            return (false, "Twilio WhatsAppFromNumber and WhatsAppContentSid are required.");

        try
        {
            TwilioClient.Init(_twilio.AccountSid.Trim(), _twilio.AuthToken.Trim());
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
            return (true, "WhatsApp sent.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Admin login WhatsApp OTP failed");
            return (false, "WhatsApp delivery failed.");
        }
    }

    private async Task SignInAsync(
        HttpContext httpContext,
        Admin admin,
        CancellationToken cancellationToken,
        bool otpVerified)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.Name, admin.Username),
            new(ClaimTypes.Role, AdminRole),
            new(ClaimTypes.NameIdentifier, admin.Id.ToString())
        };

        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        var principal = new ClaimsPrincipal(identity);

        await httpContext.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            principal,
            new AuthenticationProperties
            {
                IsPersistent = false,
                AllowRefresh = false,
                ExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(15)
            });

        _logger.LogInformation(otpVerified ? "Admin logged in with OTP" : "Admin logged in");

        await _audit.LogAsync(_db, new AuditLogRequest
        {
            Action = AuditActions.Login,
            EntityType = AuditEntityTypes.Authentication,
            EntityId = admin.Id.ToString(),
            Success = true,
            Summary = otpVerified ? $"{AdminRole} login (OTP verified)" : $"{AdminRole} login",
            NewValuesJson = $"{{\"role\":\"{AdminRole}\",\"userId\":\"{admin.Id}\"}}"
        }, cancellationToken);
    }

    private bool TryGetPendingLogin(string sessionToken, out AdminPendingLoginCacheEntry? entry)
    {
        entry = null;
        if (string.IsNullOrWhiteSpace(sessionToken))
            return false;

        return _cache.TryGetValue(CacheKey(sessionToken), out entry);
    }

    private static string CacheKey(string token) => PendingLoginCachePrefix + token;

    private static string CreateSessionToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

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

    private string LockedMessage(string username)
    {
        var remaining = _lockout.GetRemainingLockout(AdminRole, username) ?? LoginLockoutService.LockoutDuration;
        var minutes = Math.Max(1, (int)Math.Ceiling(remaining.TotalMinutes));
        return $"Too many failed sign-in attempts. Try again in {minutes} minute{(minutes == 1 ? "" : "s")}.";
    }

    private string LockedMessageOrInvalid(string username) =>
        _lockout.IsLockedOut(AdminRole, username) ? LockedMessage(username) : "Invalid username or password.";

    private async Task LogLoginFailedAsync(string reason, CancellationToken cancellationToken) =>
        await _audit.LogAsync(_db, new AuditLogRequest
        {
            Action = AuditActions.LoginFailed,
            EntityType = AuditEntityTypes.Authentication,
            Success = false,
            ErrorMessage = reason,
            Summary = $"{AdminRole} login failed",
            NewValuesJson = $"{{\"role\":\"{AdminRole}\"}}"
        }, cancellationToken);

    private static AdminLoginResult Fail(string message) =>
        new() { Success = false, Error = message };
}

internal sealed record AdminPendingLoginCacheEntry
{
    public int AdminId { get; init; }
    public string Username { get; init; } = string.Empty;
    public string OtpHash { get; init; } = string.Empty;
    public DateTime ExpiresAtUtc { get; init; }
}

public static class AdminSeedHelper
{
    public static async Task EnsureAdminAsync(DocoveeDbContext db, AdminOptions options)
    {
        if (await db.Admins.AnyAsync())
            return;

        if (string.IsNullOrWhiteSpace(options.Username) || string.IsNullOrWhiteSpace(options.Password))
            return;

        var hasher = new PasswordHasher<Admin>();
        var admin = new Admin { Username = options.Username.Trim() };
        admin.PasswordHash = hasher.HashPassword(admin, options.Password);
        db.Admins.Add(admin);
        await db.SaveChangesAsync();
    }
}
