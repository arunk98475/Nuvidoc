using System.Security.Claims;
using Docovee.BLL.Audit;
using Docovee.BLL.Auth;
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
    private readonly PasswordHasher<Patient> _patientHasher = new();
    private readonly PasswordHasher<Doctor> _doctorHasher = new();
    private readonly PasswordHasher<Admin> _adminHasher = new();

    public AccountAuthService(DocoveeDbContext db, IDocoveeLogger logger, IAuditTrailService audit)
    {
        _db = db;
        _logger = logger;
        _audit = audit;
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
            await _audit.LogAsync(new AuditLogRequest
            {
                Action = AuditActions.Logout,
                EntityType = AuditEntityTypes.Authentication,
                EntityId = ctx.ActorUserId,
                Summary = $"{ctx.ActorRole} logout: {ctx.ActorUsername}",
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
            _logger.LogInformation("Patient created via {Provider}: {Username}", provider, username);
        }

        await SignInAsync(httpContext, patient.Username, AuthRoles.Patient, patient.Id);
        await LogAuthSuccessAsync(httpContext, AuthRoles.Patient, patient.Id.ToString(), patient.Username, cancellationToken);
        _logger.LogInformation("Patient logged in via {Provider}: {Username}", provider, username);
        return (true, null);
    }

    private async Task<(bool Success, string? Error)> LoginPatientAsync(
        AccountLoginRequest request,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var patient = await _db.Patients
            .FirstOrDefaultAsync(p => p.Username == request.Username, cancellationToken);

        if (patient == null)
        {
            await LogAuthFailureAsync(httpContext, AuthRoles.Patient, request.Username, "Invalid username or password.", cancellationToken);
            return (false, "Invalid username or password.");
        }

        if (_patientHasher.VerifyHashedPassword(patient, patient.PasswordHash, request.Password) == PasswordVerificationResult.Failed)
        {
            await LogAuthFailureAsync(httpContext, AuthRoles.Patient, request.Username, "Invalid username or password.", cancellationToken);
            return (false, "Invalid username or password.");
        }

        await SignInAsync(httpContext, patient.Username, AuthRoles.Patient, patient.Id);
        await LogAuthSuccessAsync(httpContext, AuthRoles.Patient, patient.Id.ToString(), patient.Username, cancellationToken);
        _logger.LogInformation("Patient logged in: {Username}", patient.Username);
        return (true, null);
    }

    private async Task<(bool Success, string? Error)> LoginDoctorAsync(
        AccountLoginRequest request,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var doctor = await _db.Doctors
            .FirstOrDefaultAsync(d => d.Username == request.Username, cancellationToken);

        if (doctor == null || string.IsNullOrEmpty(doctor.PasswordHash))
        {
            await LogAuthFailureAsync(httpContext, AuthRoles.Doctor, request.Username, "Invalid username or password.", cancellationToken);
            return (false, "Invalid username or password.");
        }

        if (!doctor.IsActive)
        {
            await LogAuthFailureAsync(httpContext, AuthRoles.Doctor, request.Username, "Doctor account inactive.", cancellationToken);
            return (false, "This doctor account is inactive. Contact the administrator.");
        }

        if (_doctorHasher.VerifyHashedPassword(doctor, doctor.PasswordHash, request.Password) == PasswordVerificationResult.Failed)
        {
            await LogAuthFailureAsync(httpContext, AuthRoles.Doctor, request.Username, "Invalid username or password.", cancellationToken);
            return (false, "Invalid username or password.");
        }

        await SignInAsync(httpContext, doctor.Username!, AuthRoles.Doctor, doctor.Id);
        await LogAuthSuccessAsync(httpContext, AuthRoles.Doctor, doctor.Id.ToString(), doctor.Username!, cancellationToken);
        _logger.LogInformation("Doctor logged in: {Username}", doctor?.Username ?? "Unknown");
        return (true, null);
    }

    private async Task<(bool Success, string? Error)> LoginAdminAsync(
        AccountLoginRequest request,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var admin = await _db.Admins
            .FirstOrDefaultAsync(a => a.Username == request.Username, cancellationToken);

        if (admin == null)
        {
            await LogAuthFailureAsync(httpContext, AuthRoles.Admin, request.Username, "Invalid username or password.", cancellationToken);
            return (false, "Invalid username or password.");
        }

        if (_adminHasher.VerifyHashedPassword(admin, admin.PasswordHash, request.Password) == PasswordVerificationResult.Failed)
        {
            await LogAuthFailureAsync(httpContext, AuthRoles.Admin, request.Username, "Invalid username or password.", cancellationToken);
            return (false, "Invalid username or password.");
        }

        await SignInAsync(httpContext, admin.Username, AuthRoles.Admin, admin.Id);
        await LogAuthSuccessAsync(httpContext, AuthRoles.Admin, admin.Id.ToString(), admin.Username, cancellationToken);
        _logger.LogInformation("Admin logged in: {Username}", admin.Username);
        return (true, null);
    }

    private async Task LogAuthSuccessAsync(
        HttpContext httpContext,
        string role,
        string userId,
        string username,
        CancellationToken cancellationToken)
    {
        await _audit.LogAsync(new AuditLogRequest
        {
            Action = AuditActions.Login,
            EntityType = AuditEntityTypes.Authentication,
            EntityId = userId,
            Success = true,
            Summary = $"{role} login: {username}",
            NewValuesJson = $"{{\"role\":\"{role}\",\"username\":\"{username}\"}}"
        }, cancellationToken);
    }

    private async Task LogAuthFailureAsync(
        HttpContext httpContext,
        string role,
        string username,
        string reason,
        CancellationToken cancellationToken)
    {
        await _audit.LogAsync(new AuditLogRequest
        {
            Action = AuditActions.LoginFailed,
            EntityType = AuditEntityTypes.Authentication,
            Success = false,
            ErrorMessage = reason,
            Summary = $"{role} login failed: {username}",
            NewValuesJson = $"{{\"role\":\"{role}\",\"username\":\"{username}\"}}"
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

        await httpContext.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            principal,
            new AuthenticationProperties
            {
                IsPersistent = true,
                ExpiresUtc = DateTimeOffset.UtcNow.AddHours(8)
            });
    }
}
