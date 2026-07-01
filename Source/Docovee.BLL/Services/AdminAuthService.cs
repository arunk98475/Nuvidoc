using System.Security.Claims;
using Docovee.BLL.Auth;
using Docovee.BLL.Configuration;
using Docovee.DS.Models;
using Docovee.DS;
using Docovee.DS.Entities;
using Docovee.logging;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Docovee.BLL.Services;

public interface IAdminAuthService
{
    Task<bool> LoginAsync(AdminLoginRequest request, HttpContext httpContext, CancellationToken cancellationToken = default);
    Task LogoutAsync(HttpContext httpContext);
}

public class AdminAuthService : IAdminAuthService
{
    public const string AdminRole = AuthRoles.Admin;

    private readonly DocoveeDbContext _db;
    private readonly IDocoveeLogger _logger;
    private readonly PasswordHasher<Admin> _passwordHasher = new();

    public AdminAuthService(DocoveeDbContext db, IDocoveeLogger logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<bool> LoginAsync(AdminLoginRequest request, HttpContext httpContext, CancellationToken cancellationToken = default)
    {
        var admin = await _db.Admins
            .FirstOrDefaultAsync(a => a.Username == request.Username, cancellationToken);

        if (admin == null)
            return false;

        var result = _passwordHasher.VerifyHashedPassword(admin, admin.PasswordHash, request.Password);
        if (result == PasswordVerificationResult.Failed)
            return false;

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
                IsPersistent = true,
                ExpiresUtc = DateTimeOffset.UtcNow.AddHours(8)
            });

        _logger.LogInformation("Admin logged in: {Username}", admin.Username);
        return true;
    }

    public async Task LogoutAsync(HttpContext httpContext)
    {
        await httpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    }
}

public static class AdminSeedHelper
{
    public static async Task EnsureAdminAsync(DocoveeDbContext db, AdminOptions options)
    {
        if (await db.Admins.AnyAsync())
            return;

        var hasher = new PasswordHasher<Admin>();
        var admin = new Admin { Username = options.Username };
        admin.PasswordHash = hasher.HashPassword(admin, options.Password);
        db.Admins.Add(admin);
        await db.SaveChangesAsync();
    }
}
