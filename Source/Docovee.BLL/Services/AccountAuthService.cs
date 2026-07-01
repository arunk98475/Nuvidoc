using System.Security.Claims;
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
    Task LogoutAsync(HttpContext httpContext);
}

public class AccountAuthService : IAccountAuthService
{
    private readonly DocoveeDbContext _db;
    private readonly IDocoveeLogger _logger;
    private readonly PasswordHasher<Patient> _patientHasher = new();
    private readonly PasswordHasher<Doctor> _doctorHasher = new();
    private readonly PasswordHasher<Admin> _adminHasher = new();

    public AccountAuthService(DocoveeDbContext db, IDocoveeLogger logger)
    {
        _db = db;
        _logger = logger;
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

    public async Task LogoutAsync(HttpContext httpContext) =>
        await httpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);

    private async Task<(bool Success, string? Error)> LoginPatientAsync(
        AccountLoginRequest request,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var patient = await _db.Patients
            .FirstOrDefaultAsync(p => p.Username == request.Username, cancellationToken);

        if (patient == null)
            return (false, "Invalid username or password.");

        if (_patientHasher.VerifyHashedPassword(patient, patient.PasswordHash, request.Password) == PasswordVerificationResult.Failed)
            return (false, "Invalid username or password.");

        await SignInAsync(httpContext, patient.Username, AuthRoles.Patient, patient.Id);
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
            return (false, "Invalid username or password.");

        if (!doctor.IsActive)
            return (false, "This doctor account is inactive. Contact the administrator.");

        if (_doctorHasher.VerifyHashedPassword(doctor, doctor.PasswordHash, request.Password) == PasswordVerificationResult.Failed)
            return (false, "Invalid username or password.");

        await SignInAsync(httpContext, doctor.Username!, AuthRoles.Doctor, doctor.Id);
        _logger.LogInformation("Doctor logged in: {Username}", doctor.Username);
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
            return (false, "Invalid username or password.");

        if (_adminHasher.VerifyHashedPassword(admin, admin.PasswordHash, request.Password) == PasswordVerificationResult.Failed)
            return (false, "Invalid username or password.");

        await SignInAsync(httpContext, admin.Username, AuthRoles.Admin, admin.Id);
        _logger.LogInformation("Admin logged in: {Username}", admin.Username);
        return (true, null);
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
