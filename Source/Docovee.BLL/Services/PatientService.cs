using Docovee.BLL.Models;
using Docovee.DS;
using Docovee.DS.Entities;
using Docovee.logging;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Docovee.BLL.Services;

public interface IPatientService
{
    Task<PatientRegisterResponse> RegisterAsync(PatientRegisterRequest request, CancellationToken cancellationToken = default);
}

public class PatientService : IPatientService
{
    private readonly DocoveeDbContext _db;
    private readonly IDocoveeLogger _logger;
    private readonly PasswordHasher<Patient> _passwordHasher = new();

    public PatientService(DocoveeDbContext db, IDocoveeLogger logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<PatientRegisterResponse> RegisterAsync(PatientRegisterRequest request, CancellationToken cancellationToken = default)
    {
        var username = !string.IsNullOrWhiteSpace(request.Email)
            ? request.Email.Trim()
            : request.Username.Trim();

        if (string.IsNullOrWhiteSpace(username))
            return new PatientRegisterResponse { Success = false, Message = "Email or username is required." };

        if (await _db.Patients.AnyAsync(p => p.Username == username, cancellationToken))
        {
            return new PatientRegisterResponse { Success = false, Message = "An account with this email already exists." };
        }

        var session = await _db.SearchSessions
            .FirstOrDefaultAsync(s => s.SessionKey == request.SessionKey, cancellationToken);

        if (session == null)
        {
            return new PatientRegisterResponse { Success = false, Message = "Search session not found." };
        }

        var patient = new Patient
        {
            Username = username,
            FullName = request.FullName,
            DateOfBirth = request.DateOfBirth ?? new DateOnly(1990, 1, 1),
            Phone = request.Phone
        };
        patient.PasswordHash = _passwordHasher.HashPassword(patient, request.Password);

        _db.Patients.Add(patient);
        await _db.SaveChangesAsync(cancellationToken);

        session.PatientId = patient.Id;
        session.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Patient registered: {Username}", request.Username);

        return new PatientRegisterResponse { Success = true, Message = "Registration successful." };
    }
}
