using System.Security.Claims;
using Docovee.BLL.Audit;
using Docovee.BLL.Auth;
using Docovee.BLL.Security;
using Docovee.DS.Models;
using Docovee.DS;
using Docovee.DS.Entities;
using Docovee.logging;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Docovee.BLL.Services;

public interface IAccountAuthService
{
    Task<(bool Success, string? Error)> LoginAsync(AccountLoginRequest request, HttpContext httpContext, CancellationToken cancellationToken = default);
    Task<(bool Success, string? Error)> SignInExternalPatientAsync(
        string email,
        string? fullName,
        string provider,
        HttpContext httpContext,
        CancellationToken cancellationToken = default);
    Task LogoutAsync(HttpContext httpContext);
}

public class AccountAuthService : IAccountAuthService
{
    private readonly DocoveeDbContext _db;
    private readonly IDocoveeLogger _logger;
    private readonly IAuditTrailService _audit;
    private readonly ILoginLockoutService _lockout;
    private readonly PasswordHasher<Patient> _patientHasher = new();
    private readonly PasswordHasher<Doctor> _doctorHasher = new();
    private readonly PasswordHasher<Admin> _adminHasher = new();

    public AccountAuthService(
        DocoveeDbContext db,
        IDocoveeLogger logger,
        IAuditTrailService audit,
        ILoginLockoutService lockout)
    {
        _db = db;
        _logger = logger;
        _audit = audit;
        _lockout = lockout;
    }

    public async Task<(bool Success, string? Error)> LoginAsync(
        AccountLoginRequest request,
        HttpContext httpContext,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.Username) || string.IsNullOrWhiteSpace(request.Password))
            return (false, "Username and password are required.");

        return request.AccountType switch
        {
            AccountType.Patient => await LoginPatientAsync(request, httpContext, cancellationToken),
            AccountType.Doctor => await LoginDoctorAsync(request, httpContext, cancellationToken),
            AccountType.Admin => await LoginAdminAsync(request, httpContext, cancellationToken),
            _ => (false, "Invalid account type.")
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
                Summary = $"{ctx.ActorRole} logout",
                Context = ctx
            });
        }

        await httpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    }

    public async Task<(bool Success, string? Error)> SignInExternalPatientAsync(
        string email,
        string? fullName,
        string provider,
        HttpContext httpContext,
        CancellationToken cancellationToken = default)
    {
        var username = email?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(username) || !username.Contains('@'))
            return (false, $"{provider} did not return an email address. Please try again or use email log in.");

        var patient = await _db.Patients
            .FirstOrDefaultAsync(p => p.Username == username, cancellationToken);

        if (patient != null && patient.IsDeleted)
            return (false, "Invalid username or password.");

        if (patient == null)
        {
            patient = new Patient
            {
                Username = username,
                FullName = string.IsNullOrWhiteSpace(fullName) ? username.Split('@')[0] : fullName.Trim(),
                // Social providers do not supply DOB; profile/booking can collect it later.
                DateOfBirth = new DateOnly(1900, 1, 1),
                Phone = string.Empty
            };
            patient.PasswordHash = _patientHasher.HashPassword(patient, Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N"));
            _db.Patients.Add(patient);
            await _db.SaveChangesAsync(cancellationToken);
            _logger.LogInformation("Patient created via {Provider}", provider);
        }

        _lockout.Reset(AuthRoles.Patient, username);
        await SignInAsync(httpContext, patient.Username, AuthRoles.Patient, patient.Id);
        await LogAuthSuccessAsync(httpContext, AuthRoles.Patient, patient.Id.ToString(), patient.Username, cancellationToken);
        _logger.LogInformation("Patient logged in via {Provider}", provider);
        return (true, null);
    }

    private async Task<(bool Success, string? Error)> LoginPatientAsync(
        AccountLoginRequest request,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        if (TryLockout(AuthRoles.Patient, request.Username, out var lockoutError))
        {
            await LogAuthFailureAsync(httpContext, AuthRoles.Patient, request.Username, "Account locked out.", cancellationToken);
            return (false, lockoutError);
        }

        var patient = await _db.Patients
            .FirstOrDefaultAsync(p => p.Username == request.Username, cancellationToken);

        if (patient == null)
        {
            _lockout.RecordFailure(AuthRoles.Patient, request.Username);
            await LogAuthFailureAsync(httpContext, AuthRoles.Patient, request.Username, "Invalid username or password.", cancellationToken);
            return (false, "Invalid username or password.");
        }

        if (patient.IsDeleted || DeletedAccountHelper.IsDeletedUsername(patient.Username))
        {
            _lockout.RecordFailure(AuthRoles.Patient, request.Username);
            await LogAuthFailureAsync(httpContext, AuthRoles.Patient, request.Username, "Invalid username or password (closed account).", cancellationToken);
            return (false, "Invalid username or password.");
        }

        if (_patientHasher.VerifyHashedPassword(patient, patient.PasswordHash, request.Password) == PasswordVerificationResult.Failed)
        {
            _lockout.RecordFailure(AuthRoles.Patient, request.Username);
            await LogAuthFailureAsync(httpContext, AuthRoles.Patient, request.Username, "Invalid username or password.", cancellationToken);
            return (false, LockedMessageOrInvalid(AuthRoles.Patient, request.Username));
        }

        _lockout.Reset(AuthRoles.Patient, request.Username);
        await SignInAsync(httpContext, patient.Username, AuthRoles.Patient, patient.Id);
        await LogAuthSuccessAsync(httpContext, AuthRoles.Patient, patient.Id.ToString(), patient.Username, cancellationToken);
        _logger.LogInformation("Patient logged in");
        return (true, null);
    }

    private async Task<(bool Success, string? Error)> LoginDoctorAsync(
        AccountLoginRequest request,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        if (TryLockout(AuthRoles.Doctor, request.Username, out var lockoutError))
        {
            await LogAuthFailureAsync(httpContext, AuthRoles.Doctor, request.Username, "Account locked out.", cancellationToken);
            return (false, lockoutError);
        }

        var doctor = await _db.Doctors
            .FirstOrDefaultAsync(d => d.Username == request.Username, cancellationToken);

        if (doctor == null || string.IsNullOrEmpty(doctor.PasswordHash))
        {
            _lockout.RecordFailure(AuthRoles.Doctor, request.Username);
            await LogAuthFailureAsync(httpContext, AuthRoles.Doctor, request.Username, "Invalid username or password.", cancellationToken);
            return (false, "Invalid username or password.");
        }

        if (doctor.IsDeleted || !doctor.IsActive)
        {
            _lockout.RecordFailure(AuthRoles.Doctor, request.Username);
            await LogAuthFailureAsync(httpContext, AuthRoles.Doctor, request.Username, "Invalid username or password (closed/inactive).", cancellationToken);
            return (false, "Invalid username or password.");
        }

        if (_doctorHasher.VerifyHashedPassword(doctor, doctor.PasswordHash, request.Password) == PasswordVerificationResult.Failed)
        {
            _lockout.RecordFailure(AuthRoles.Doctor, request.Username);
            await LogAuthFailureAsync(httpContext, AuthRoles.Doctor, request.Username, "Invalid username or password.", cancellationToken);
            return (false, LockedMessageOrInvalid(AuthRoles.Doctor, request.Username));
        }

        _lockout.Reset(AuthRoles.Doctor, request.Username);
        await SignInAsync(httpContext, doctor.Username!, AuthRoles.Doctor, doctor.Id);
        await LogAuthSuccessAsync(httpContext, AuthRoles.Doctor, doctor.Id.ToString(), doctor.Username!, cancellationToken);
        _logger.LogInformation("Doctor logged in");
        return (true, null);
    }

    private async Task<(bool Success, string? Error)> LoginAdminAsync(
        AccountLoginRequest request,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        if (TryLockout(AuthRoles.Admin, request.Username, out var lockoutError))
        {
            await LogAuthFailureAsync(httpContext, AuthRoles.Admin, request.Username, "Account locked out.", cancellationToken);
            return (false, lockoutError);
        }

        var admin = await _db.Admins
            .FirstOrDefaultAsync(a => a.Username == request.Username, cancellationToken);

        if (admin == null)
        {
            _lockout.RecordFailure(AuthRoles.Admin, request.Username);
            await LogAuthFailureAsync(httpContext, AuthRoles.Admin, request.Username, "Invalid username or password.", cancellationToken);
            return (false, "Invalid username or password.");
        }

        if (_adminHasher.VerifyHashedPassword(admin, admin.PasswordHash, request.Password) == PasswordVerificationResult.Failed)
        {
            _lockout.RecordFailure(AuthRoles.Admin, request.Username);
            await LogAuthFailureAsync(httpContext, AuthRoles.Admin, request.Username, "Invalid username or password.", cancellationToken);
            return (false, LockedMessageOrInvalid(AuthRoles.Admin, request.Username));
        }

        _lockout.Reset(AuthRoles.Admin, request.Username);
        await SignInAsync(httpContext, admin.Username, AuthRoles.Admin, admin.Id);
        await LogAuthSuccessAsync(httpContext, AuthRoles.Admin, admin.Id.ToString(), admin.Username, cancellationToken);
        _logger.LogInformation("Admin logged in");
        return (true, null);
    }

    private bool TryLockout(string role, string username, out string error)
    {
        if (!_lockout.IsLockedOut(role, username))
        {
            error = "";
            return false;
        }

        var remaining = _lockout.GetRemainingLockout(role, username) ?? LoginLockoutService.LockoutDuration;
        var minutes = Math.Max(1, (int)Math.Ceiling(remaining.TotalMinutes));
        error = $"Too many failed sign-in attempts. Try again in {minutes} minute{(minutes == 1 ? "" : "s")}.";
        return true;
    }

    private string LockedMessageOrInvalid(string role, string username)
    {
        if (_lockout.IsLockedOut(role, username))
        {
            var remaining = _lockout.GetRemainingLockout(role, username) ?? LoginLockoutService.LockoutDuration;
            var minutes = Math.Max(1, (int)Math.Ceiling(remaining.TotalMinutes));
            return $"Too many failed sign-in attempts. Try again in {minutes} minute{(minutes == 1 ? "" : "s")}.";
        }

        return "Invalid username or password.";
    }

    private async Task LogAuthSuccessAsync(
        HttpContext httpContext,
        string role,
        string userId,
        string username,
        CancellationToken cancellationToken)
    {
        await _audit.LogAsync(_db, new AuditLogRequest
        {
            Action = AuditActions.Login,
            EntityType = AuditEntityTypes.Authentication,
            EntityId = userId,
            Success = true,
            Summary = $"{role} login",
            NewValuesJson = $"{{\"role\":\"{role}\",\"userId\":\"{userId}\"}}"
        }, cancellationToken);
    }

    private async Task LogAuthFailureAsync(
        HttpContext httpContext,
        string role,
        string username,
        string reason,
        CancellationToken cancellationToken)
    {
        await _audit.LogAsync(_db, new AuditLogRequest
        {
            Action = AuditActions.LoginFailed,
            EntityType = AuditEntityTypes.Authentication,
            Success = false,
            ErrorMessage = reason,
            Summary = $"{role} login failed",
            NewValuesJson = $"{{\"role\":\"{role}\"}}"
        }, cancellationToken);
    }

    private static async Task SignInAsync(HttpContext httpContext, string username, string role, int userId)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.Name, username),
            new(ClaimTypes.Role, role),
            new(ClaimTypes.NameIdentifier, userId.ToString())
        };

        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        var principal = new ClaimsPrincipal(identity);

        // Patient/doctor: sliding idle via cookie ExpireTimeSpan (30m), absolute max 8h.
        // Admin: hard 15-minute session (no sliding refresh).
        var isAdmin = string.Equals(role, AuthRoles.Admin, StringComparison.Ordinal);
        await httpContext.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            principal,
            new AuthenticationProperties
            {
                IsPersistent = false,
                AllowRefresh = !isAdmin,
                ExpiresUtc = DateTimeOffset.UtcNow.Add(isAdmin ? TimeSpan.FromMinutes(15) : TimeSpan.FromHours(8))
            });
    }
}
