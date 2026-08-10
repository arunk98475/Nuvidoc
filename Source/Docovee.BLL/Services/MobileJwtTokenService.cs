using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Docovee.BLL.Auth;
using Docovee.BLL.Configuration;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace Docovee.BLL.Services;

public interface IMobileJwtTokenService
{
    (string Token, DateTime ExpiresAtUtc) CreatePatientToken(int patientId, string email, string fullName);
}

public sealed class MobileJwtTokenService : IMobileJwtTokenService
{
    private readonly MobileJwtOptions _options;

    public MobileJwtTokenService(IOptions<MobileJwtOptions> options) => _options = options.Value;

    public (string Token, DateTime ExpiresAtUtc) CreatePatientToken(int patientId, string email, string fullName)
    {
        var expires = DateTime.UtcNow.AddHours(Math.Max(1, _options.ExpiresHours));
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(EnsureKey(_options.SigningKey)));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, patientId.ToString()),
            new(ClaimTypes.Name, email),
            new(ClaimTypes.Email, email),
            new(ClaimTypes.Role, AuthRoles.Patient),
            new("full_name", fullName ?? string.Empty)
        };

        var token = new JwtSecurityToken(
            issuer: _options.Issuer,
            audience: _options.Audience,
            claims: claims,
            notBefore: DateTime.UtcNow.AddMinutes(-1),
            expires: expires,
            signingCredentials: creds);

        return (new JwtSecurityTokenHandler().WriteToken(token), expires);
    }

    private static string EnsureKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Length < 32)
            return "CHANGE-ME-NUVIDOC-MOBILE-JWT-SIGNING-KEY-32+CHARS-MIN";
        return key;
    }
}
