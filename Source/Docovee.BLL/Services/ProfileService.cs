using Docovee.BLL.Audit;
using Docovee.BLL.Data;
using Docovee.DS.Models;
using Docovee.DS;
using Docovee.DS.Entities;
using Docovee.logging;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace Docovee.BLL.Services;

public interface IProfileService
{
    Task<PatientProfileDto?> GetPatientProfileAsync(int patientId, CancellationToken cancellationToken = default);
    Task<DoctorProfileDto?> GetDoctorProfileAsync(int doctorId, CancellationToken cancellationToken = default);
    Task<PatientProfileEditModel?> GetPatientForEditAsync(int patientId, CancellationToken cancellationToken = default);
    Task<DoctorProfileEditModel?> GetDoctorForEditAsync(int doctorId, CancellationToken cancellationToken = default);
    Task<(bool Success, string? Error)> UpdatePatientProfileAsync(int patientId, PatientProfileEditModel model, CancellationToken cancellationToken = default);
    Task<PatientPrivacySettingsDto?> GetPatientPrivacySettingsAsync(int patientId, CancellationToken cancellationToken = default);
    Task<(bool Success, string? Error)> UpdatePatientHipaaOptInAsync(int patientId, bool optIn, CancellationToken cancellationToken = default);
    Task<(bool Success, string? Error)> UpdatePatientCookieOptOutAsync(int patientId, bool optOut, CancellationToken cancellationToken = default);
    Task<PatientPermissionsSettingsDto?> GetPatientPermissionsSettingsAsync(int patientId, CancellationToken cancellationToken = default);
    Task<(bool Success, string? Error)> UpdatePatientAutofillAsync(int patientId, bool enabled, CancellationToken cancellationToken = default);
    Task<string?> GetPatientSavedInformationJsonAsync(int patientId, CancellationToken cancellationToken = default);
    Task<(bool Success, string? Error)> UpdateDoctorProfileAsync(int doctorId, DoctorProfileEditModel model, IFormFile? photo, IFormFile? video = null, CancellationToken cancellationToken = default);
    Task<(bool Success, string? Error)> UpdatePracticeProfileAsync(
        int doctorId,
        PracticeProfileInput model,
        IFormFile? logo,
        CancellationToken cancellationToken = default);
    Task<IReadOnlyList<VisitReasonCategoryViewModel>> GetVisitReasonPreferencesAsync(int doctorId, CancellationToken cancellationToken = default);
    Task<(bool Success, string? Error)> UpdateVisitReasonPreferencesAsync(int doctorId, VisitReasonPreferencesInput model, CancellationToken cancellationToken = default);
    Task<WorkingHoursPageModel?> GetWorkingHoursAsync(int doctorId, CancellationToken cancellationToken = default);
    Task<(bool Success, string? Error)> UpdateWorkingHoursAsync(int doctorId, WorkingHoursInput model, CancellationToken cancellationToken = default);
}

public class ProfileService : IProfileService
{
    private readonly DocoveeDbContext _db;
    private readonly IDoctorFileService _fileService;
    private readonly IPatientDoctorContactService _contactViews;
    private readonly IDocoveeLogger _logger;
    private readonly IDoctorQualityScoreService _qualityScore;
    private readonly IAuditTrailService _audit;
    private readonly PasswordHasher<Patient> _patientHasher = new();
    private readonly PasswordHasher<Doctor> _doctorHasher = new();

    public ProfileService(
        DocoveeDbContext db,
        IDoctorFileService fileService,
        IPatientDoctorContactService contactViews,
        IDocoveeLogger logger,
        IDoctorQualityScoreService qualityScore,
        IAuditTrailService audit)
    {
        _db = db;
        _fileService = fileService;
        _contactViews = contactViews;
        _logger = logger;
        _qualityScore = qualityScore;
        _audit = audit;
    }

    public async Task<PatientProfileDto?> GetPatientProfileAsync(int patientId, CancellationToken cancellationToken = default)
    {
        var patient = await _db.Patients.AsNoTracking()
            .Include(p => p.SearchSessions)
            .FirstOrDefaultAsync(p => p.Id == patientId, cancellationToken);

        if (patient == null) return null;

        await _audit.LogReadAsync(
            _db,
            AuditEntityTypes.Patient,
            patientId.ToString(),
            "Patient profile viewed",
            cancellationToken);

        return new PatientProfileDto
        {
            Username = patient.Username,
            FullName = patient.FullName,
            DateOfBirth = patient.DateOfBirth,
            Phone = patient.Phone,
            PhoneVerified = patient.PhoneVerified,
            PhoneVerificationPending = !patient.PhoneVerified
                && !string.IsNullOrWhiteSpace(patient.PhoneVerificationCodeHash)
                && patient.PhoneVerificationExpiresAtUtc > DateTime.UtcNow,
            EmailVerified = patient.EmailVerified,
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
                .ToList(),
            ViewedDoctors = await _contactViews.GetViewedDoctorsAsync(patientId, cancellationToken)
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

        var (websiteFromJson, _) = DoctorProfileHelper.ExtractPracticeSettings(doctor.OnboardingProfileJson);
        var website = !string.IsNullOrWhiteSpace(doctor.Website)
            ? doctor.Website.Trim()
            : websiteFromJson;
        var allowGoogle = doctor.AllowGoogleBookings;

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
            PracticeLogoUrl = doctor.PracticeLogoUrl,
            GmbPhotoLink = doctor.GmbPhotoLink,
            GoogleRating = doctor.GoogleRating,
            GoogleReviewCount = doctor.GoogleReviewCount,
            PatientReviewCount = reviewCount,
            PatientReviewAverage = reviewAvg,
            TagLine = doctor.TagLine,
            Niche = doctor.Niche,
            VideoUrl = !string.IsNullOrWhiteSpace(doctor.VideoUrl)
                ? doctor.VideoUrl.Trim()
                : DoctorProfileHelper.ExtractVideoUrl(doctor.OnboardingProfileJson),
            FacebookUrl = doctor.FacebookUrl,
            InstagramUrl = doctor.InstagramUrl,
            TikTokUrl = doctor.TikTokUrl,
            LinkedInUrl = doctor.LinkedInUrl,
            YoutubeChannelUrl = doctor.YoutubeChannelUrl,
            IsActive = doctor.IsActive,
            MemberSince = doctor.CreatedAt,
            ProfileCompletionPercent = doctor.ProfileCompletionPercent,
            InsuranceCarriers = doctor.DoctorInsurances
                .Select(di => di.InsuranceCarrier.Name)
                .OrderBy(n => n)
                .ToList(),
            PracticeDescription = doctor.SummaryOfReviews,
            PracticeWebsite = website,
            AllowGoogleBookings = allowGoogle
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
            VideoUrl = doctor.VideoUrl,
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

        var today = DateOnly.FromDateTime(DateTime.Today);
        var postedDob = model.DateOfBirth;
        var hasRealDob = postedDob.Year >= 1901 && postedDob <= today;
        if (!hasRealDob && IsUnsetDateOfBirth(patient.DateOfBirth))
            return (false, "Please enter a valid date of birth.");

        if (!string.IsNullOrWhiteSpace(model.NewPassword))
        {
            if (model.NewPassword.Length < 6)
                return (false, "Password must be at least 6 characters.");
            patient.PasswordHash = _patientHasher.HashPassword(patient, model.NewPassword);
        }

        patient.FullName = model.FullName.Trim();
        var newPhone = (model.Phone ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(newPhone)
            && !string.Equals(patient.Phone, newPhone, StringComparison.Ordinal))
        {
            patient.Phone = newPhone;
            patient.PhoneVerified = false;
            patient.PhoneVerificationCodeHash = null;
            patient.PhoneVerificationExpiresAtUtc = null;
        }

        if (hasRealDob)
            patient.DateOfBirth = postedDob;

        await _db.SaveChangesAsync(cancellationToken);
        _logger.LogInformation("Patient updated profile");
        return (true, null);
    }

    public async Task<PatientPrivacySettingsDto?> GetPatientPrivacySettingsAsync(
        int patientId,
        CancellationToken cancellationToken = default)
    {
        var patient = await _db.Patients.AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == patientId, cancellationToken);
        if (patient == null) return null;

        return new PatientPrivacySettingsDto
        {
            HipaaDataSharingOptIn = patient.HipaaDataSharingOptIn,
            CookieTrackingOptOut = patient.CookieTrackingOptOut
        };
    }

    public async Task<(bool Success, string? Error)> UpdatePatientHipaaOptInAsync(
        int patientId,
        bool optIn,
        CancellationToken cancellationToken = default)
    {
        var patient = await _db.Patients.FirstOrDefaultAsync(p => p.Id == patientId, cancellationToken);
        if (patient == null) return (false, "Patient not found.");

        patient.HipaaDataSharingOptIn = optIn;
        await _db.SaveChangesAsync(cancellationToken);
        _logger.LogInformation("Patient HIPAA data sharing opt-in updated: {OptIn}", optIn);
        return (true, null);
    }

    public async Task<(bool Success, string? Error)> UpdatePatientCookieOptOutAsync(
        int patientId,
        bool optOut,
        CancellationToken cancellationToken = default)
    {
        var patient = await _db.Patients.FirstOrDefaultAsync(p => p.Id == patientId, cancellationToken);
        if (patient == null) return (false, "Patient not found.");

        patient.CookieTrackingOptOut = optOut;
        await _db.SaveChangesAsync(cancellationToken);
        _logger.LogInformation("Patient cookie tracking opt-out updated: {OptOut}", optOut);
        return (true, null);
    }

    public async Task<PatientPermissionsSettingsDto?> GetPatientPermissionsSettingsAsync(
        int patientId,
        CancellationToken cancellationToken = default)
    {
        var patient = await _db.Patients.AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == patientId, cancellationToken);
        if (patient == null) return null;

        return new PatientPermissionsSettingsDto
        {
            AutofillEnabled = patient.AutofillEnabled
        };
    }

    public async Task<(bool Success, string? Error)> UpdatePatientAutofillAsync(
        int patientId,
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        var patient = await _db.Patients.FirstOrDefaultAsync(p => p.Id == patientId, cancellationToken);
        if (patient == null) return (false, "Patient not found.");

        patient.AutofillEnabled = enabled;
        await _db.SaveChangesAsync(cancellationToken);
        _logger.LogInformation("Patient autofill enabled updated: {Enabled}", enabled);
        return (true, null);
    }

    public async Task<string?> GetPatientSavedInformationJsonAsync(
        int patientId,
        CancellationToken cancellationToken = default)
    {
        var patient = await _db.Patients.AsNoTracking()
            .Include(p => p.InsuranceCoverages)
            .ThenInclude(c => c.InsuranceCarrier)
            .Include(p => p.InsuranceCoverages)
            .ThenInclude(c => c.InsurancePlan)
            .FirstOrDefaultAsync(p => p.Id == patientId, cancellationToken);

        if (patient == null) return null;

        await _audit.LogExportAsync(
            _db,
            AuditEntityTypes.DataExport,
            patientId.ToString(),
            "Patient saved information exported",
            cancellationToken);

        var payload = new
        {
            exportedAtUtc = DateTime.UtcNow,
            profile = new
            {
                patient.FullName,
                patient.Username,
                dateOfBirth = patient.DateOfBirth.ToString("yyyy-MM-dd"),
                patient.Phone,
                memberSince = patient.CreatedAt
            },
            insurance = patient.InsuranceCoverages
                .OrderBy(c => c.InsuranceType)
                .Select(c => new
                {
                    c.InsuranceType,
                    carrier = c.InsuranceCarrier?.Name ?? c.CustomCarrierName,
                    plan = c.InsurancePlan?.Name ?? c.CustomPlanName,
                    c.MemberId,
                    hasCardPhoto = !string.IsNullOrEmpty(c.CardPhotoUrl)
                }),
            preferences = new
            {
                patient.AutofillEnabled,
                patient.HipaaDataSharingOptIn,
                patient.CookieTrackingOptOut,
                hasIdCardPhoto = !string.IsNullOrEmpty(patient.IdCardPhotoUrl)
            }
        };

        return JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
    }

    public async Task<(bool Success, string? Error)> UpdateDoctorProfileAsync(
        int doctorId,
        DoctorProfileEditModel model,
        IFormFile? photo,
        IFormFile? video = null,
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
            var photoUrl = await _fileService.SaveUploadedPhotoAsync(doctorId, photo, cancellationToken);
            if (photoUrl != null)
            {
                doctor.PhotoUrl = photoUrl;
                doctor.PracticeLogoUrl = photoUrl;
            }
        }

        doctor.PhotoUrl = DoctorPhotoHelper.GetDisplayPhotoUrl(doctor.PhotoUrl, doctor.GmbPhotoLink);
        if (string.IsNullOrWhiteSpace(doctor.PracticeLogoUrl) && !string.IsNullOrWhiteSpace(doctor.PhotoUrl))
            doctor.PracticeLogoUrl = doctor.PhotoUrl;

        if (video != null && video.Length > 0)
        {
            var videoUrl = await _fileService.SaveUploadedVideoAsync(doctorId, video, cancellationToken: cancellationToken);
            if (videoUrl == null)
                return (false, $"Please upload a valid video (mp4, webm, mov, ogg, m4v — up to {_fileService.MaxVideoMb} MB).");
            doctor.VideoUrl = videoUrl;
        }
        else if (!string.IsNullOrWhiteSpace(model.VideoUrl))
        {
            doctor.VideoUrl = model.VideoUrl.Trim();
        }
        else
        {
            doctor.VideoUrl = null;
        }

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
        await _qualityScore.RecomputeAndPersistAsync(doctorId, cancellationToken);
        return (true, null);
    }

    public async Task<(bool Success, string? Error)> UpdatePracticeProfileAsync(
        int doctorId,
        PracticeProfileInput model,
        IFormFile? logo,
        CancellationToken cancellationToken = default)
    {
        var doctor = await _db.Doctors.FirstOrDefaultAsync(d => d.Id == doctorId, cancellationToken);
        if (doctor == null) return (false, "Doctor not found.");

        if (string.IsNullOrWhiteSpace(model.PracticeName))
            return (false, "Practice name is required.");

        var description = model.PracticeDescription?.Trim();
        if (description?.Length > 20000)
            return (false, "Practice description must be 20,000 characters or fewer.");

        if (!TryNormalizeOptionalUrl(model.FacebookUrl, "Facebook", out var facebookUrl, out var socialError)
            || !TryNormalizeOptionalUrl(model.InstagramUrl, "Instagram", out var instagramUrl, out socialError)
            || !TryNormalizeOptionalUrl(model.TikTokUrl, "TikTok", out var tikTokUrl, out socialError)
            || !TryNormalizeOptionalUrl(model.LinkedInUrl, "LinkedIn", out var linkedInUrl, out socialError)
            || !TryNormalizeOptionalUrl(model.YoutubeChannelUrl, "YouTube channel", out var youtubeChannelUrl, out socialError)
            || !TryNormalizeOptionalUrl(model.PracticeWebsite, "Practice website", out var practiceWebsite, out socialError))
            return (false, socialError);

        doctor.PracticeName = model.PracticeName.Trim();
        doctor.SummaryOfReviews = string.IsNullOrWhiteSpace(description) ? null : description;
        doctor.Website = practiceWebsite;
        doctor.AllowGoogleBookings = model.AllowGoogleBookings;
        doctor.FacebookUrl = facebookUrl;
        doctor.InstagramUrl = instagramUrl;
        doctor.TikTokUrl = tikTokUrl;
        doctor.LinkedInUrl = linkedInUrl;
        doctor.YoutubeChannelUrl = youtubeChannelUrl;
        // Keep JSON in sync for any legacy readers.
        doctor.OnboardingProfileJson = DoctorProfileHelper.MergePracticeSettings(
            doctor.OnboardingProfileJson,
            practiceWebsite,
            model.AllowGoogleBookings);

        if (logo != null && logo.Length > 0)
        {
            var photoUrl = await _fileService.SaveUploadedPhotoAsync(doctorId, logo, cancellationToken);
            if (photoUrl != null)
            {
                // Practice logo and public headshot are the same image.
                doctor.PhotoUrl = photoUrl;
                doctor.PracticeLogoUrl = photoUrl;
            }
        }

        doctor.PhotoUrl = DoctorPhotoHelper.GetDisplayPhotoUrl(doctor.PhotoUrl, doctor.GmbPhotoLink);
        if (string.IsNullOrWhiteSpace(doctor.PracticeLogoUrl) && !string.IsNullOrWhiteSpace(doctor.PhotoUrl))
            doctor.PracticeLogoUrl = doctor.PhotoUrl;

        await _db.SaveChangesAsync(cancellationToken);
        _logger.LogInformation("Doctor updated practice profile {DoctorId}", doctorId);
        await _qualityScore.RecomputeAndPersistAsync(doctorId, cancellationToken);
        return (true, null);
    }

    public async Task<IReadOnlyList<VisitReasonCategoryViewModel>> GetVisitReasonPreferencesAsync(
        int doctorId,
        CancellationToken cancellationToken = default)
    {
        var doctor = await _db.Doctors.AsNoTracking()
            .FirstOrDefaultAsync(d => d.Id == doctorId, cancellationToken);
        if (doctor == null)
            return Array.Empty<VisitReasonCategoryViewModel>();

        var saved = DoctorProfileHelper.ExtractVisitReasonPreferences(doctor.OnboardingProfileJson)
            .ToDictionary(c => c.Key, StringComparer.OrdinalIgnoreCase);

        return DentalVisitReasonCatalog.Categories.Select(def =>
        {
            saved.TryGetValue(def.Key, out var pref);
            var enabled = pref?.Enabled ?? def.DefaultEnabled;
            var popularSelected = pref?.PopularSelectedKeys;
            if (popularSelected == null || popularSelected.Count == 0)
            {
                popularSelected = enabled
                    ? def.PopularItems.Select(p => p.Key).ToList()
                    : new List<string>();
            }

            var selectedSet = popularSelected.ToHashSet(StringComparer.OrdinalIgnoreCase);
            return new VisitReasonCategoryViewModel
            {
                Key = def.Key,
                Title = def.Title,
                Description = def.Description,
                Enabled = enabled,
                NewPatientMinutes = pref?.NewPatientMinutes ?? def.DefaultNewMinutes,
                ExistingPatientMinutes = pref?.ExistingPatientMinutes ?? def.DefaultExistingMinutes,
                PopularItems = def.PopularItems.Select(p => new VisitReasonPopularViewModel
                {
                    Key = p.Key,
                    Name = p.Name,
                    Selected = selectedSet.Contains(p.Key)
                }).ToList()
            };
        }).ToList();
    }

    public async Task<(bool Success, string? Error)> UpdateVisitReasonPreferencesAsync(
        int doctorId,
        VisitReasonPreferencesInput model,
        CancellationToken cancellationToken = default)
    {
        var doctor = await _db.Doctors.FirstOrDefaultAsync(d => d.Id == doctorId, cancellationToken);
        if (doctor == null)
            return (false, "Doctor not found.");

        var byKey = (model.Categories ?? new())
            .Where(c => !string.IsNullOrWhiteSpace(c.Key))
            .ToDictionary(c => c.Key.Trim(), StringComparer.OrdinalIgnoreCase);

        var normalized = new List<VisitReasonCategoryPreference>();
        foreach (var def in DentalVisitReasonCatalog.Categories)
        {
            byKey.TryGetValue(def.Key, out var posted);
            var enabled = posted?.Enabled == true;
            var newMins = ClampMinutes(posted?.NewPatientMinutes ?? def.DefaultNewMinutes, def.DefaultNewMinutes);
            var existMins = ClampMinutes(posted?.ExistingPatientMinutes ?? def.DefaultExistingMinutes, def.DefaultExistingMinutes);
            var allowedPopular = def.PopularItems.Select(p => p.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var popular = (posted?.PopularSelectedKeys ?? new List<string>())
                .Where(k => allowedPopular.Contains(k))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (enabled && popular.Count == 0)
                popular = def.PopularItems.Select(p => p.Key).ToList();

            normalized.Add(new VisitReasonCategoryPreference
            {
                Key = def.Key,
                Enabled = enabled,
                NewPatientMinutes = newMins,
                ExistingPatientMinutes = existMins,
                PopularSelectedKeys = enabled ? popular : new List<string>()
            });
        }

        doctor.OnboardingProfileJson = DoctorProfileHelper.MergeVisitReasonPreferences(
            doctor.OnboardingProfileJson,
            normalized);
        await _db.SaveChangesAsync(cancellationToken);
        _logger.LogInformation("Doctor updated visit reason preferences {DoctorId}", doctorId);
        return (true, null);
    }

    public async Task<WorkingHoursPageModel?> GetWorkingHoursAsync(
        int doctorId,
        CancellationToken cancellationToken = default)
    {
        var doctor = await _db.Doctors.AsNoTracking()
            .FirstOrDefaultAsync(d => d.Id == doctorId, cancellationToken);
        if (doctor == null)
            return null;

        var locations = await _db.DoctorLocations.AsNoTracking()
            .Where(l => l.DoctorId == doctorId)
            .OrderBy(l => l.SortOrder)
            .ThenBy(l => l.Id)
            .Select(l => new WorkingHoursLocationOption
            {
                Id = l.Id,
                Label = string.IsNullOrWhiteSpace(l.Address1)
                    ? (l.Name ?? "Location")
                    : (string.IsNullOrWhiteSpace(l.Address2)
                        ? $"{l.Address1}, {l.City}"
                        : $"{l.Address1}, {l.Address2}")
            })
            .ToListAsync(cancellationToken);

        var hours = DoctorProfileHelper.ExtractWorkingHours(doctor.OnboardingProfileJson);
        var validLocationIds = locations.Select(l => l.Id).ToHashSet();
        foreach (var day in hours.Days)
        {
            foreach (var block in day.Blocks)
                block.LocationIds = block.LocationIds.Where(validLocationIds.Contains).ToList();
        }

        var displayName = doctor.Name.StartsWith("Dr", StringComparison.OrdinalIgnoreCase)
            ? doctor.Name
            : $"Dr. {doctor.Name}";

        return new WorkingHoursPageModel
        {
            DoctorDisplayName = displayName,
            Hours = hours,
            Locations = locations
        };
    }

    public async Task<(bool Success, string? Error)> UpdateWorkingHoursAsync(
        int doctorId,
        WorkingHoursInput model,
        CancellationToken cancellationToken = default)
    {
        var doctor = await _db.Doctors.FirstOrDefaultAsync(d => d.Id == doctorId, cancellationToken);
        if (doctor == null)
            return (false, "Doctor not found.");

        var validLocationIds = await _db.DoctorLocations.AsNoTracking()
            .Where(l => l.DoctorId == doctorId)
            .Select(l => l.Id)
            .ToListAsync(cancellationToken);
        var validSet = validLocationIds.ToHashSet();

        var normalized = new WorkingHoursInput { Days = new List<WorkingHoursDayInput>() };
        foreach (var dayName in DoctorProfileHelper.WorkingHourDays)
        {
            var posted = model.Days?.FirstOrDefault(d =>
                string.Equals(d.Day, dayName, StringComparison.OrdinalIgnoreCase));
            var enabled = posted?.Enabled == true;
            var blocks = (posted?.Blocks ?? new List<WorkingHoursBlockInput>())
                .Select(b => new WorkingHoursBlockInput
                {
                    StartTime = NormalizeTime(b.StartTime) ?? "09:00",
                    EndTime = NormalizeTime(b.EndTime) ?? "17:00",
                    LocationIds = (b.LocationIds ?? new List<int>()).Where(validSet.Contains).Distinct().ToList()
                })
                .Where(b => TimeSpan.TryParse(b.StartTime, out var s)
                            && TimeSpan.TryParse(b.EndTime, out var e)
                            && e > s)
                .ToList();

            if (blocks.Count == 0)
            {
                blocks.Add(new WorkingHoursBlockInput
                {
                    StartTime = "09:00",
                    EndTime = "17:00",
                    LocationIds = new List<int>()
                });
            }

            if (enabled)
            {
                foreach (var block in blocks)
                {
                    if (!TimeSpan.TryParse(block.StartTime, out var start)
                        || !TimeSpan.TryParse(block.EndTime, out var end)
                        || end <= start)
                        return (false, $"{dayName}: end time must be after start time.");
                }
            }

            normalized.Days.Add(new WorkingHoursDayInput
            {
                Day = dayName,
                Enabled = enabled,
                Blocks = blocks
            });
        }

        doctor.OnboardingProfileJson = DoctorProfileHelper.MergeWorkingHours(
            doctor.OnboardingProfileJson,
            normalized);
        await _db.SaveChangesAsync(cancellationToken);
        _logger.LogInformation("Doctor updated working hours {DoctorId}", doctorId);
        return (true, null);
    }

    private static string? NormalizeTime(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        if (TimeSpan.TryParse(value.Trim(), out var ts))
            return ts.ToString(@"hh\:mm");
        return null;
    }

    private static int ClampMinutes(int value, int fallback)
    {
        if (value < 5 || value > 240)
            return fallback;
        return value;
    }

    private static bool TryNormalizeOptionalUrl(
        string? value,
        string label,
        out string? normalized,
        out string? error)
    {
        normalized = null;
        error = null;
        if (string.IsNullOrWhiteSpace(value))
            return true;

        var trimmed = value.Trim();
        if (!trimmed.Contains("://", StringComparison.Ordinal))
            trimmed = "https://" + trimmed;

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            error = $"Please enter a valid {label} URL.";
            return false;
        }

        normalized = uri.ToString();
        if (normalized.Length > 500)
        {
            error = $"{label} URL must be 500 characters or fewer.";
            return false;
        }

        return true;
    }

    private static bool IsUnsetDateOfBirth(DateOnly dateOfBirth) =>
        dateOfBirth.Year <= 1900;

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
