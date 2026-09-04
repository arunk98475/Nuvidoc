using Docovee.DS;
using Docovee.DS.Entities;
using Docovee.DS.Models;
using Microsoft.EntityFrameworkCore;

namespace Docovee.BLL.Services;

public interface IPublicDoctorService
{
    Task<IReadOnlyList<FeaturedDoctorCardDto>> GetFeaturedAsync(int count = 3, CancellationToken cancellationToken = default);
    Task<PublicDoctorProfileDto?> GetPublicProfileAsync(
        int doctorId,
        bool liveGoogleReviews = false,
        CancellationToken cancellationToken = default);
}

public class PublicDoctorService : IPublicDoctorService
{
    private readonly DocoveeDbContext _db;
    private readonly IClaudeGoogleReviewService _googleReviews;

    public PublicDoctorService(DocoveeDbContext db, IClaudeGoogleReviewService googleReviews)
    {
        _db = db;
        _googleReviews = googleReviews;
    }

    public async Task<IReadOnlyList<FeaturedDoctorCardDto>> GetFeaturedAsync(
        int count = 3, CancellationToken cancellationToken = default)
    {
        var take = Math.Clamp(count, 1, 6);
        var doctors = await _db.Doctors
            .AsNoTracking()
            .Include(d => d.PatientReviews)
            .Where(d => d.IsActive && !d.IsDeleted)
            .OrderByDescending(d => d.GoogleRating)
            .ThenByDescending(d => d.GoogleReviewCount)
            .ThenBy(d => d.Name)
            .ToListAsync(cancellationToken);

        // Homepage is Houston-first — never feature out-of-market doctors (e.g. Phoenix, Dallas).
        var houston = doctors.Where(IsHoustonAreaDoctor).ToList();

        var dental = houston
            .Where(d => IsDentalSpecialty(d.Specialty))
            .Take(take)
            .ToList();

        // Fall back within Houston only if the network is not yet dentist-seeded.
        if (dental.Count == 0)
            dental = houston.Take(take).ToList();

        return dental
            .Select((doctor, index) => MapFeatured(doctor, index == 1))
            .ToList();
    }

    private static bool IsDentalSpecialty(string? specialty)
    {
        if (string.IsNullOrWhiteSpace(specialty))
            return false;
        var s = specialty.ToLowerInvariant();
        return s.Contains("dent")
            || s.Contains("oral")
            || s.Contains("orthodont")
            || s.Contains("periodont")
            || s.Contains("endodont")
            || s.Contains("prosthodont")
            || s.Contains("hygien");
    }

    /// <summary>Houston + common surrounding cities / ZIPs (770–775).</summary>
    private static bool IsHoustonAreaDoctor(Doctor doctor)
    {
        var city = (doctor.City ?? string.Empty).ToLowerInvariant();
        var location = (doctor.Location ?? string.Empty).ToLowerInvariant();
        var address = (doctor.Address ?? string.Empty).ToLowerInvariant();

        if (city.Contains("houston") || location.Contains("houston") || address.Contains("houston"))
            return true;

        var zip = ExtractZip(doctor.ZipCode);
        if (zip != null && IsHoustonAreaZip(zip))
            return true;

        var state = (doctor.State ?? string.Empty).Trim();
        if (state.Equals("TX", StringComparison.OrdinalIgnoreCase)
            && (city.Contains("katy") || city.Contains("sugar land") || city.Contains("pasadena")
                || city.Contains("pearland") || city.Contains("baytown") || city.Contains("spring")
                || city.Contains("cypress") || city.Contains("humble") || city.Contains("missouri city")))
            return true;

        return false;
    }

    private static bool IsHoustonAreaZip(string zip) =>
        zip.StartsWith("770", StringComparison.Ordinal)
        || zip.StartsWith("772", StringComparison.Ordinal)
        || zip.StartsWith("773", StringComparison.Ordinal)
        || zip.StartsWith("774", StringComparison.Ordinal)
        || zip.StartsWith("775", StringComparison.Ordinal);

    private static string? ExtractZip(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var match = System.Text.RegularExpressions.Regex.Match(value, @"\b(\d{5})(?:-\d{4})?\b");
        if (!match.Success)
            return null;
        var zip = match.Groups[1].Value;
        return zip is "00000" ? null : zip;
    }

    public async Task<PublicDoctorProfileDto?> GetPublicProfileAsync(
        int doctorId,
        bool liveGoogleReviews = false,
        CancellationToken cancellationToken = default)
    {
        var doctor = await _db.Doctors
            .AsNoTracking()
            .Include(d => d.PatientReviews)
            .Include(d => d.Media)
            .Include(d => d.Locations)
            .Include(d => d.DoctorInsurances).ThenInclude(di => di.InsuranceCarrier).ThenInclude(c => c.Plans)
            .Include(d => d.DoctorLanguages).ThenInclude(dl => dl.DoctorLanguage)
            .FirstOrDefaultAsync(d => d.Id == doctorId && d.IsActive && !d.IsDeleted, cancellationToken);

        if (doctor == null)
            return null;

        var profile = MapProfile(doctor);

        if (liveGoogleReviews)
        {
            var live = await _googleReviews.LookupAsync(doctor, cancellationToken);
            if (live != null && live.Found)
            {
                if (live.GoogleRating > 0)
                    profile.GoogleRating = live.GoogleRating;
                if (live.GoogleReviewCount > 0)
                    profile.GoogleReviewCount = live.GoogleReviewCount;
                if (!string.IsNullOrWhiteSpace(live.SummaryOfReviews))
                    profile.SummaryOfReviews = live.SummaryOfReviews;
                profile.GoogleReviews = live.Reviews;
                profile.GoogleReviewsLive = !live.FromCache && (live.Reviews.Count > 0 || live.GoogleRating > 0);
            }
        }
        else
        {
            // Public profile: show last saved Google review file without calling Claude.
            var cached = await _googleReviews.GetCachedAsync(doctor, cancellationToken);
            if (cached != null && cached.Found && cached.Reviews.Count > 0)
            {
                profile.GoogleReviews = cached.Reviews;
                profile.GoogleReviewsLive = false;
                if (cached.GoogleRating > 0 && profile.GoogleRating <= 0)
                    profile.GoogleRating = cached.GoogleRating;
                if (cached.GoogleReviewCount > 0 && profile.GoogleReviewCount <= 0)
                    profile.GoogleReviewCount = cached.GoogleReviewCount;
            }
        }

        return profile;
    }

    private static FeaturedDoctorCardDto MapFeatured(Doctor doctor, bool isFeatured) => new()
    {
        Id = doctor.Id,
        Name = doctor.Name,
        Specialty = doctor.Specialty,
        City = doctor.City,
        State = doctor.State,
        PhotoUrl = DoctorPhotoHelper.GetDisplayPhotoUrl(doctor.PhotoUrl, doctor.GmbPhotoLink),
        AvatarInitials = doctor.AvatarInitials,
        GoogleRating = doctor.GoogleRating,
        GoogleReviewCount = doctor.GoogleReviewCount,
        HighlightText = GetHighlightText(doctor),
        Niche = doctor.Niche ?? doctor.TagLine,
        IsFeatured = isFeatured
    };

    private static PublicDoctorProfileDto MapProfile(Doctor doctor)
    {
        var reviews = doctor.PatientReviews
            .OrderByDescending(r => r.Rating)
            .ThenByDescending(r => r.CreatedAt)
            .Take(8)
            .Select(r => new PublicDoctorReviewDto
            {
                ReviewerName = r.ReviewerName,
                Rating = r.Rating,
                ReviewText = r.ReviewText,
                WaitingTime = r.WaitingTime,
                Recommendation = r.Recommendation,
                PhotoUrl = r.PhotoUrl
            })
            .ToList();

        var media = doctor.Media
            .OrderBy(m => m.MediaType)
            .ThenBy(m => m.SortOrder)
            .ThenBy(m => m.Id)
            .Select(m => new DoctorMediaDto
            {
                Id = m.Id,
                MediaType = m.MediaType,
                Url = m.Url,
                Caption = m.Caption,
                FileSizeBytes = m.FileSizeBytes,
                SortOrder = m.SortOrder
            })
            .ToList();

        var firstUploadedVideo = media
            .FirstOrDefault(m => string.Equals(m.MediaType, "Video", StringComparison.OrdinalIgnoreCase)
                                 && !string.IsNullOrWhiteSpace(m.Url))
            ?.Url;

        var legacyVideoUrl = !string.IsNullOrWhiteSpace(doctor.VideoUrl)
            ? doctor.VideoUrl.Trim()
            : DoctorProfileHelper.ExtractVideoUrl(doctor.OnboardingProfileJson);

        var insurers = doctor.DoctorInsurances
            .Where(di => di.InsuranceCarrier != null && di.InsuranceCarrier.IsActive)
            .Select(di => di.InsuranceCarrier!)
            .DistinctBy(c => c.Id)
            .OrderBy(c => c.Name)
            .ToList();

        var acceptedInsurances = insurers
            .Select(c => new PublicDoctorInsuranceDto
            {
                CarrierId = c.Id,
                CarrierName = c.Name,
                CarrierCode = c.Code,
                Plans = c.Plans
                    .Where(p => p.IsActive)
                    .OrderBy(p => p.SortOrder)
                    .ThenBy(p => p.Name)
                    .Select(p => p.Name)
                    .ToList()
            })
            .ToList();

        var languages = doctor.DoctorLanguages
            .Select(dl => dl.DoctorLanguage?.Name)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(n => n!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n)
            .ToList();

        var locations = doctor.Locations
            .Where(l => l.IsActive)
            .OrderByDescending(l => l.IsPrimary)
            .ThenBy(l => l.SortOrder)
            .ThenBy(l => l.City)
            .Select(l => new PublicDoctorLocationDto
            {
                Name = l.Name,
                Address1 = l.Address1,
                Address2 = l.Address2,
                City = l.City,
                State = l.State,
                ZipCode = l.ZipCode,
                IsPrimary = l.IsPrimary
            })
            .ToList();

        return new PublicDoctorProfileDto
        {
            Id = doctor.Id,
            Name = doctor.Name,
            Specialty = doctor.Specialty,
            PracticeName = doctor.PracticeName,
            City = doctor.City,
            State = doctor.State,
            ZipCode = doctor.ZipCode,
            Address = doctor.Address,
            PhotoUrl = DoctorPhotoHelper.GetDisplayPhotoUrl(doctor.PhotoUrl, doctor.GmbPhotoLink),
            PracticeLogoUrl = doctor.PracticeLogoUrl,
            AvatarInitials = doctor.AvatarInitials,
            OfficePhoneNumber = doctor.OfficePhoneNumber,
            Niche = doctor.Niche ?? doctor.TagLine,
            SummaryOfReviews = doctor.SummaryOfReviews,
            Top3Procedures = doctor.Top3Procedures,
            YearsOfPractice = doctor.YearsOfPractice,
            GraduationYear = doctor.GraduationYear,
            GoogleRating = doctor.GoogleRating,
            GoogleReviewCount = doctor.GoogleReviewCount,
            VideoUrl = firstUploadedVideo ?? legacyVideoUrl,
            FacebookUrl = doctor.FacebookUrl,
            InstagramUrl = doctor.InstagramUrl,
            TikTokUrl = doctor.TikTokUrl,
            LinkedInUrl = doctor.LinkedInUrl,
            YoutubeChannelUrl = doctor.YoutubeChannelUrl,
            Website = !string.IsNullOrWhiteSpace(doctor.Website)
                ? doctor.Website.Trim()
                : DoctorProfileHelper.ExtractPracticeSettings(doctor.OnboardingProfileJson).Website,
            OffersDentalImplants = doctor.OffersDentalImplants,
            OffersTmj = doctor.OffersTmj,
            OffersBotox = doctor.OffersBotox,
            InsuranceCarriers = acceptedInsurances.Select(i => i.CarrierName).ToList(),
            AcceptedInsuranceCarrierIds = acceptedInsurances.Select(i => i.CarrierId).ToList(),
            AcceptedInsurances = acceptedInsurances,
            Languages = languages,
            Reviews = reviews,
            Media = media,
            VisitReasons = DoctorProfileHelper.GetPublicVisitReasonNames(doctor.OnboardingProfileJson),
            Locations = locations
        };
    }

    private static string? GetHighlightText(Doctor doctor)
    {
        if (!string.IsNullOrWhiteSpace(doctor.SummaryOfReviews))
            return doctor.SummaryOfReviews;

        var topReview = doctor.PatientReviews
            .OrderByDescending(r => r.Rating)
            .ThenByDescending(r => r.CreatedAt)
            .FirstOrDefault();

        if (topReview != null)
            return $"\"{topReview.ReviewText}\"";

        if (!string.IsNullOrWhiteSpace(doctor.Niche))
            return doctor.Niche;

        return $"Highly rated {doctor.Specialty} in {doctor.City}, {doctor.State}.";
    }
}
