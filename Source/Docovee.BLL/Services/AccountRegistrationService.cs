using Docovee.BLL.Data;
using Docovee.DS.Models;
using Docovee.DS;
using Docovee.DS.Entities;
using Docovee.DS.Enums;
using Docovee.logging;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Docovee.BLL.Services;

public interface IAccountRegistrationService
{
    Task<AccountRegisterResponse> RegisterAsync(
        AccountRegisterRequest request,
        IFormFile? doctorPhoto = null,
        CancellationToken cancellationToken = default);
}

public class AccountRegistrationService : IAccountRegistrationService
{
    private readonly DocoveeDbContext _db;
    private readonly IDocoveeLogger _logger;
    private readonly IDoctorFileService _fileService;
    private readonly PasswordHasher<Patient> _patientHasher = new();
    private readonly PasswordHasher<Doctor> _doctorHasher = new();

    public AccountRegistrationService(DocoveeDbContext db, IDocoveeLogger logger, IDoctorFileService fileService)
    {
        _db = db;
        _logger = logger;
        _fileService = fileService;
    }

    public Task<AccountRegisterResponse> RegisterAsync(
        AccountRegisterRequest request,
        IFormFile? doctorPhoto = null,
        CancellationToken cancellationToken = default) =>
        request.AccountType switch
        {
            AccountType.Patient => RegisterPatientAsync(request, cancellationToken),
            AccountType.Doctor => RegisterDoctorAsync(request, doctorPhoto, cancellationToken),
            _ => Task.FromResult(new AccountRegisterResponse
            {
                Success = false,
                Message = "Invalid account type.",
                AccountType = request.AccountType
            })
        };

    private async Task<AccountRegisterResponse> RegisterPatientAsync(
        AccountRegisterRequest request,
        CancellationToken cancellationToken)
    {
        var validationError = ValidateCredentials(request);
        if (validationError != null)
            return Fail(request.AccountType, validationError);

        if (string.IsNullOrWhiteSpace(request.FullName))
            return Fail(request.AccountType, "Full name is required.");
        if (!request.DateOfBirth.HasValue)
            return Fail(request.AccountType, "Date of birth is required.");
        if (string.IsNullOrWhiteSpace(request.Phone))
            return Fail(request.AccountType, "Phone number is required.");

        if (await _db.Patients.AnyAsync(p => p.Username == request.Username.Trim(), cancellationToken))
            return Fail(request.AccountType, "Username is already taken.");

        var patient = new Patient
        {
            Username = request.Username.Trim(),
            FullName = request.FullName.Trim(),
            DateOfBirth = request.DateOfBirth.Value,
            Phone = request.Phone.Trim()
        };
        patient.PasswordHash = _patientHasher.HashPassword(patient, request.Password);

        _db.Patients.Add(patient);
        await _db.SaveChangesAsync(cancellationToken);
        _logger.LogInformation("Patient self-registered: {Username}", patient.Username);

        return new AccountRegisterResponse
        {
            Success = true,
            Message = "Registration successful.",
            AccountType = AccountType.Patient
        };
    }

    private async Task<AccountRegisterResponse> RegisterDoctorAsync(
        AccountRegisterRequest request,
        IFormFile? doctorPhoto,
        CancellationToken cancellationToken)
    {
        var validationError = ValidateCredentials(request);
        if (validationError != null)
            return Fail(request.AccountType, validationError);

        if (string.IsNullOrWhiteSpace(request.DoctorName))
            return Fail(request.AccountType, "Doctor name is required.");
        if (string.IsNullOrWhiteSpace(request.Specialty))
            return Fail(request.AccountType, "Specialty is required.");
        if (string.IsNullOrWhiteSpace(request.City))
            return Fail(request.AccountType, "City is required.");
        if (string.IsNullOrWhiteSpace(request.State))
            return Fail(request.AccountType, "State is required.");
        if (!UsStates.IsValid(request.State))
            return Fail(request.AccountType, "Please select a valid US state.");
        if (string.IsNullOrWhiteSpace(request.ZipCode))
            return Fail(request.AccountType, "Zip code is required.");

        var username = request.Username.Trim();
        if (await _db.Doctors.AnyAsync(d => d.Username == username, cancellationToken))
            return Fail(request.AccountType, "Username is already taken.");

        var state = UsStates.Normalize(request.State)!;
        var doctor = new Doctor
        {
            Name = request.DoctorName.Trim(),
            Username = username,
            PracticeName = request.PracticeName?.Trim(),
            Specialty = request.Specialty.Trim(),
            SpecialtyCategory = request.Specialty.Trim(),
            City = request.City.Trim(),
            State = state,
            ZipCode = request.ZipCode.Trim(),
            Location = $"{request.City.Trim()}, {state}",
            Address = request.Address?.Trim(),
            OfficePhoneNumber = request.OfficePhoneNumber?.Trim(),
            GmbPhotoLink = DoctorPhotoHelper.NormalizeStoredLink(request.GmbPhotoLink),
            AvatarInitials = BuildInitials(request.DoctorName),
            Gender = Gender.Other,
            IsActive = true
        };
        doctor.PasswordHash = _doctorHasher.HashPassword(doctor, request.Password);

        if (doctorPhoto != null)
        {
            var photoUrl = await _fileService.SaveUploadedPhotoAsync(doctorPhoto, cancellationToken);
            if (photoUrl != null)
                doctor.PhotoUrl = photoUrl;
        }

        doctor.PhotoUrl = DoctorPhotoHelper.GetDisplayPhotoUrl(doctor.PhotoUrl, doctor.GmbPhotoLink);

        _db.Doctors.Add(doctor);
        await _db.SaveChangesAsync(cancellationToken);

        await SetDoctorInsurancesAsync(doctor.Id, request.InsuranceCarrierIds, cancellationToken);

        _logger.LogInformation("Doctor self-registered: {Username}", doctor.Username);

        return new AccountRegisterResponse
        {
            Success = true,
            Message = "Registration successful.",
            AccountType = AccountType.Doctor
        };
    }

    private async Task SetDoctorInsurancesAsync(int doctorId, IEnumerable<int> carrierIds, CancellationToken cancellationToken)
    {
        var validIds = await _db.InsuranceCarriers.AsNoTracking()
            .Where(c => c.IsActive && carrierIds.Contains(c.Id))
            .Select(c => c.Id)
            .ToListAsync(cancellationToken);

        foreach (var carrierId in validIds.Distinct())
        {
            _db.DoctorInsurances.Add(new DoctorInsurance
            {
                DoctorId = doctorId,
                InsuranceCarrierId = carrierId
            });
        }

        if (validIds.Count > 0)
            await _db.SaveChangesAsync(cancellationToken);
    }

    private static string? ValidateCredentials(AccountRegisterRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Username))
            return "Username is required.";
        if (string.IsNullOrWhiteSpace(request.Password))
            return "Password is required.";
        if (request.Password.Length < 6)
            return "Password must be at least 6 characters.";
        if (request.Password != request.ConfirmPassword)
            return "Passwords do not match.";
        return null;
    }

    private static AccountRegisterResponse Fail(AccountType accountType, string message) => new()
    {
        Success = false,
        Message = message,
        AccountType = accountType
    };

    private static string BuildInitials(string name)
    {
        var parts = name.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(p => !p.Equals("Dr.", StringComparison.OrdinalIgnoreCase) && !p.Equals("Dr", StringComparison.OrdinalIgnoreCase))
            .Take(2)
            .Select(p => p[0])
            .ToArray();
        return parts.Length > 0 ? new string(parts).ToUpperInvariant() : "DR";
    }
}
