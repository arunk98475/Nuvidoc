using Docovee.DS.Models;
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
        var username = !string.IsNullOrWhiteSpace(request.Username)
            ? request.Username.Trim()
            : request.Email?.Trim() ?? string.Empty;

        if (string.IsNullOrWhiteSpace(username))
            return new PatientRegisterResponse { Success = false, Message = "Email or username is required." };

        if (await _db.Patients.AnyAsync(p => p.Username == username, cancellationToken))
        {
            return new PatientRegisterResponse { Success = false, Message = "An account with this username already exists." };
        }

        var session = await _db.SearchSessions
            .FirstOrDefaultAsync(s => s.SessionKey == request.SessionKey, cancellationToken);

        if (session == null)
        {
            return new PatientRegisterResponse { Success = false, Message = "Search session not found." };
        }

        if (!request.DateOfBirth.HasValue)
        {
            return new PatientRegisterResponse
            {
                Success = false,
                Message = "Date of birth is required."
            };
        }

        var phone = PhoneNumberHelper.NormalizeLast10(request.Phone)
            ?? PhoneNumberHelper.DigitsOnly(request.Phone);
        if (phone.Length > 30)
            phone = phone[..30];

        var patient = new Patient
        {
            Username = username,
            FullName = request.FullName,
            DateOfBirth = request.DateOfBirth.Value,
            Phone = phone,
            EmailVerified = request.EmailVerified,
            PhoneVerified = request.PhoneVerified
        };
        patient.PasswordHash = _passwordHasher.HashPassword(patient, request.Password);

        _db.Patients.Add(patient);
        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex)
        {
            _logger.LogError(ex, "Patient registration save failed");
            _db.Entry(patient).State = EntityState.Detached;
            return new PatientRegisterResponse
            {
                Success = false,
                Message = "Something went wrong creating your account. Please check your phone number and try again."
            };
        }

        session.PatientId = patient.Id;
        session.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Patient registered");

        return new PatientRegisterResponse { Success = true, Message = "Registration successful." };
    }
}
