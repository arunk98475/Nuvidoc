using Docovee.DS;
using Docovee.DS.Entities;
using Docovee.DS.Models;
using Microsoft.EntityFrameworkCore;

namespace Docovee.BLL.Services;

public interface IPublicDoctorService
{
    Task<IReadOnlyList<FeaturedDoctorCardDto>> GetFeaturedAsync(int count = 3, CancellationToken cancellationToken = default);
    Task<PublicDoctorProfileDto?> GetPublicProfileAsync(int doctorId, CancellationToken cancellationToken = default);
}

public class PublicDoctorService : IPublicDoctorService
{
    private readonly DocoveeDbContext _db;

    public PublicDoctorService(DocoveeDbContext db) => _db = db;

    public async Task<IReadOnlyList<FeaturedDoctorCardDto>> GetFeaturedAsync(
        int count = 3, CancellationToken cancellationToken = default)
    {
        var take = Math.Clamp(count, 1, 6);
        var doctors = await _db.Doctors
            .AsNoTracking()
            .Include(d => d.PatientReviews)
            .Where(d => d.IsActive)
            .OrderByDescending(d => d.GoogleRating)
            .ThenByDescending(d => d.GoogleReviewCount)
            .ThenBy(d => d.Name)
            .ToListAsync(cancellationToken);

        var dental = doctors
            .Where(d => IsDentalSpecialty(d.Specialty))
            .Take(take)
            .ToList();

        // Fall back if the network is not yet dentist-seeded.
        if (dental.Count == 0)
            dental = doctors.Take(take).ToList();

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

    public async Task<PublicDoctorProfileDto?> GetPublicProfileAsync(
        int doctorId, CancellationToken cancellationToken = default)
    {
        var doctor = await _db.Doctors
            .AsNoTracking()
            .Include(d => d.PatientReviews)
            .FirstOrDefaultAsync(d => d.Id == doctorId && d.IsActive, cancellationToken);

        return doctor == null ? null : MapProfile(doctor);
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
            .Take(5)
            .Select(r => new PublicDoctorReviewDto
            {
                ReviewerName = r.ReviewerName,
                Rating = r.Rating,
                ReviewText = r.ReviewText,
                WaitingTime = r.WaitingTime,
                Recommendation = r.Recommendation
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
            Address = doctor.Address,
            PhotoUrl = DoctorPhotoHelper.GetDisplayPhotoUrl(doctor.PhotoUrl, doctor.GmbPhotoLink),
            AvatarInitials = doctor.AvatarInitials,
            OfficePhoneNumber = doctor.OfficePhoneNumber,
            Niche = doctor.Niche ?? doctor.TagLine,
            SummaryOfReviews = doctor.SummaryOfReviews,
            Top3Procedures = doctor.Top3Procedures,
            YearsOfPractice = doctor.YearsOfPractice,
            GoogleRating = doctor.GoogleRating,
            GoogleReviewCount = doctor.GoogleReviewCount,
            VideoUrl = !string.IsNullOrWhiteSpace(doctor.VideoUrl)
                ? doctor.VideoUrl.Trim()
                : DoctorProfileHelper.ExtractVideoUrl(doctor.OnboardingProfileJson),
            Reviews = reviews
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
