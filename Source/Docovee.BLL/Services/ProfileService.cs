using Docovee.BLL.Data;
using Docovee.BLL.Models;
using Docovee.DS;
using Docovee.DS.Entities;
using Docovee.logging;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Docovee.BLL.Services;

public interface IProfileService
{
    Task<PatientProfileDto?> GetPatientProfileAsync(int patientId, CancellationToken cancellationToken = default);
    Task<DoctorProfileDto?> GetDoctorProfileAsync(int doctorId, CancellationToken cancellationToken = default);
    Task<PatientProfileEditModel?> GetPatientForEditAsync(int patientId, CancellationToken cancellationToken = default);
    Task<DoctorProfileEditModel?> GetDoctorForEditAsync(int doctorId, CancellationToken cancellationToken = default);
    Task<(bool Success, string? Error)> UpdatePatientProfileAsync(int patientId, PatientProfileEditModel model, CancellationToken cancellationToken = default);
    Task<(bool Success, string? Error)> UpdateDoctorProfileAsync(int doctorId, DoctorProfileEditModel model, IFormFile? photo, CancellationToken cancellationToken = default);
}

public class ProfileService : IProfileService
{
    private readonly DocoveeDbContext _db;
    private readonly IDoctorFileService _fileService;
    private readonly IDocoveeLogger _logger;
    private readonly PasswordHasher<Patient> _patientHasher = new();
    private readonly PasswordHasher<Doctor> _doctorHasher = new();

    public ProfileService(DocoveeDbContext db, IDoctorFileService fileService, IDocoveeLogger logger)
    {
        _db = db;
        _fileService = fileService;
        _logger = logger;
    }

    public async Task<PatientProfileDto?> GetPatientProfileAsync(int patientId, CancellationToken cancellationToken = default)
    {
        var patient = await _db.Patients.AsNoTracking()
            .Include(p => p.SearchSessions)
            .FirstOrDefaultAsync(p => p.Id == patientId, cancellationToken);

        if (patient == null) return null;

        return new PatientProfileDto
        {
            Username = patient.Username,
            FullName = patient.FullName,
            DateOfBirth = patient.DateOfBirth,
            Phone = patient.Phone,
            MemberSince = patient.CreatedAt,
            SearchHistory = patient.SearchSessions
                .OrderByDescending(s => s.UpdatedAt)
                .Select(s => new PatientSearchHistoryDto
                {
                    Date = s.UpdatedAt,
                    Specialty = s.Specialty,
                    Location = s.Location,
                    MedicalIssuesSummary = s.MedicalIssuesSummary
                })
                .ToList()
        };
    }

    public async Task<DoctorProfileDto?> GetDoctorProfileAsync(int doctorId, CancellationToken cancellationToken = default)
    {
        var doctor = await _db.Doctors.AsNoTracking()
            .Include(d => d.PatientReviews)
            .Include(d => d.DoctorInsurances)
            .ThenInclude(di => di.InsuranceCarrier)
            .FirstOrDefaultAsync(d => d.Id == doctorId, cancellationToken);

        if (doctor == null) return null;

        var reviewCount = doctor.PatientReviews.Count;
        decimal? reviewAvg = reviewCount > 0
            ? (decimal)doctor.PatientReviews.Average(r => r.Rating)
            : null;

        return new DoctorProfileDto
        {
            Id = doctor.Id,
            Username = doctor.Username,
            Name = doctor.Name,
            Specialty = doctor.Specialty,
            PracticeName = doctor.PracticeName,
            Location = doctor.Location ?? $"{doctor.City}, {doctor.State}",
            Address = doctor.Address,
            City = doctor.City,
            State = doctor.State,
            ZipCode = doctor.ZipCode,
            OfficePhoneNumber = doctor.OfficePhoneNumber,
            PhotoUrl = DoctorPhotoHelper.GetDisplayPhotoUrl(doctor.PhotoUrl, doctor.GmbPhotoLink),
            GmbPhotoLink = doctor.GmbPhotoLink,
            GoogleRating = doctor.GoogleRating,
            GoogleReviewCount = doctor.GoogleReviewCount,
            PatientReviewCount = reviewCount,
            PatientReviewAverage = reviewAvg,
            TagLine = doctor.TagLine,
            Niche = doctor.Niche,
            IsActive = doctor.IsActive,
            MemberSince = doctor.CreatedAt,
            ProfileCompletionPercent = doctor.ProfileCompletionPercent,
            InsuranceCarriers = doctor.DoctorInsurances
                .Select(di => di.InsuranceCarrier.Name)
                .OrderBy(n => n)
                .ToList()
        };
    }

    public async Task<PatientProfileEditModel?> GetPatientForEditAsync(int patientId, CancellationToken cancellationToken = default)
    {
        var patient = await _db.Patients.AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == patientId, cancellationToken);
        if (patient == null) return null;

        return new PatientProfileEditModel
        {
            Username = patient.Username,
            FullName = patient.FullName,
            DateOfBirth = patient.DateOfBirth,
            Phone = patient.Phone
        };
    }

    public async Task<DoctorProfileEditModel?> GetDoctorForEditAsync(int doctorId, CancellationToken cancellationToken = default)
    {
        var doctor = await _db.Doctors.AsNoTracking()
            .Include(d => d.DoctorInsurances)
            .FirstOrDefaultAsync(d => d.Id == doctorId, cancellationToken);
        if (doctor == null) return null;

        return new DoctorProfileEditModel
        {
            Username = doctor.Username ?? string.Empty,
            Name = doctor.Name,
            PracticeName = doctor.PracticeName,
            Specialty = doctor.Specialty,
            Address = doctor.Address,
            City = doctor.City,
            State = doctor.State,
            ZipCode = doctor.ZipCode,
            OfficePhoneNumber = doctor.OfficePhoneNumber,
            GmbPhotoLink = doctor.GmbPhotoLink,
            TagLine = doctor.TagLine,
            Niche = doctor.Niche,
            InsuranceCarrierIds = doctor.DoctorInsurances.Select(di => di.InsuranceCarrierId).ToList()
        };
    }

    public async Task<(bool Success, string? Error)> UpdatePatientProfileAsync(
        int patientId,
        PatientProfileEditModel model,
        CancellationToken cancellationToken = default)
    {
        var patient = await _db.Patients.FirstOrDefaultAsync(p => p.Id == patientId, cancellationToken);
        if (patient == null) return (false, "Patient not found.");

        if (string.IsNullOrWhiteSpace(model.FullName))
            return (false, "Full name is required.");
        if (string.IsNullOrWhiteSpace(model.Phone))
            return (false, "Phone number is required.");

        if (!string.IsNullOrWhiteSpace(model.NewPassword))
        {
            if (model.NewPassword.Length < 6)
                return (false, "Password must be at least 6 characters.");
            patient.PasswordHash = _patientHasher.HashPassword(patient, model.NewPassword);
        }

        patient.FullName = model.FullName.Trim();
        patient.Phone = model.Phone.Trim();
        patient.DateOfBirth = model.DateOfBirth;

        await _db.SaveChangesAsync(cancellationToken);
        _logger.LogInformation("Patient updated profile {PatientId}", patientId);
        return (true, null);
    }

    public async Task<(bool Success, string? Error)> UpdateDoctorProfileAsync(
        int doctorId,
        DoctorProfileEditModel model,
        IFormFile? photo,
        CancellationToken cancellationToken = default)
    {
        var doctor = await _db.Doctors
            .Include(d => d.DoctorInsurances)
            .FirstOrDefaultAsync(d => d.Id == doctorId, cancellationToken);
        if (doctor == null) return (false, "Doctor not found.");

        if (string.IsNullOrWhiteSpace(model.Name))
            return (false, "Doctor name is required.");
        if (string.IsNullOrWhiteSpace(model.Specialty))
            return (false, "Specialty is required.");
        if (string.IsNullOrWhiteSpace(model.City))
            return (false, "City is required.");
        if (string.IsNullOrWhiteSpace(model.State))
            return (false, "State is required.");
        if (!UsStates.IsValid(model.State))
            return (false, "Please select a valid US state.");
        if (string.IsNullOrWhiteSpace(model.ZipCode))
            return (false, "Zip code is required.");

        if (!string.IsNullOrWhiteSpace(model.NewPassword))
        {
            if (model.NewPassword.Length < 6)
                return (false, "Password must be at least 6 characters.");
            doctor.PasswordHash = _doctorHasher.HashPassword(doctor, model.NewPassword);
        }

        var state = UsStates.Normalize(model.State)!;
        doctor.Name = model.Name.Trim();
        doctor.PracticeName = model.PracticeName?.Trim();
        doctor.Specialty = model.Specialty.Trim();
        doctor.SpecialtyCategory = model.Specialty.Trim();
        doctor.Address = model.Address?.Trim();
        doctor.City = model.City.Trim();
        doctor.State = state;
        doctor.ZipCode = model.ZipCode.Trim();
        doctor.Location = $"{doctor.City}, {state}";
        doctor.OfficePhoneNumber = model.OfficePhoneNumber?.Trim();
        doctor.GmbPhotoLink = DoctorPhotoHelper.NormalizeStoredLink(model.GmbPhotoLink);
        doctor.TagLine = model.TagLine?.Trim();
        doctor.Niche = model.Niche?.Trim();
        doctor.AvatarInitials = BuildInitials(model.Name);

        if (photo != null)
        {
            var photoUrl = await _fileService.SaveUploadedPhotoAsync(photo, cancellationToken);
            if (photoUrl != null)
                doctor.PhotoUrl = photoUrl;
        }

        doctor.PhotoUrl = DoctorPhotoHelper.GetDisplayPhotoUrl(doctor.PhotoUrl, doctor.GmbPhotoLink);

        _db.DoctorInsurances.RemoveRange(doctor.DoctorInsurances);
        var validIds = await _db.InsuranceCarriers.AsNoTracking()
            .Where(c => c.IsActive && model.InsuranceCarrierIds.Contains(c.Id))
            .Select(c => c.Id)
            .ToListAsync(cancellationToken);

        foreach (var carrierId in validIds.Distinct())
        {
            _db.DoctorInsurances.Add(new DoctorInsurance
            {
                DoctorId = doctor.Id,
                InsuranceCarrierId = carrierId
            });
        }

        await _db.SaveChangesAsync(cancellationToken);
        _logger.LogInformation("Doctor updated profile {DoctorId}", doctorId);
        return (true, null);
    }

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
