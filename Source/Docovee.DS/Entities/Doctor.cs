using Docovee.DS.Enums;

namespace Docovee.DS.Entities;

public class Doctor
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Specialty { get; set; } = string.Empty;
    public string SpecialtyCategory { get; set; } = string.Empty;
    public string? Location { get; set; }
    public string? PracticeName { get; set; }
    public string? Address { get; set; }
    public string? OfficePhoneNumber { get; set; }
    public string? PhotoUrl { get; set; }
    public string? PracticeLogoUrl { get; set; }
    public string? GmbPhotoLink { get; set; }
    public string? VideoUrl { get; set; }
    public string? FacebookUrl { get; set; }
    public string? InstagramUrl { get; set; }
    public string? TikTokUrl { get; set; }
    public string? LinkedInUrl { get; set; }
    public string? YoutubeChannelUrl { get; set; }
    public string? Website { get; set; }
    public bool AllowGoogleBookings { get; set; } = true;
    public string? SummaryOfReviews { get; set; }
    public string? Top3Procedures { get; set; }
    public string? Niche { get; set; }
    public bool OffersDentalImplants { get; set; }
    public bool OffersTmj { get; set; }
    public bool OffersBotox { get; set; }
    public int? Age { get; set; }
    public int? YearsOfPractice { get; set; }
    public int? ProcedureCount { get; set; }
    public int? GraduationYear { get; set; }
    public int? PracticeCount { get; set; }
    public string City { get; set; } = string.Empty;
    public string State { get; set; } = string.Empty;
    public string ZipCode { get; set; } = string.Empty;
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }
    public decimal GoogleRating { get; set; }
    public int GoogleReviewCount { get; set; }
    /// <summary>UTC timestamp of the last successful Claude Google-review fetch.</summary>
    public DateTime? GoogleReviewsFetchedAt { get; set; }
    /// <summary>Relative path under doctor uploads, e.g. "{id}/google-reviews.json".</summary>
    public string? GoogleReviewsFilePath { get; set; }
    public string AvatarInitials { get; set; } = string.Empty;
    public string? TagLine { get; set; }
    public Gender Gender { get; set; }
    public bool IsActive { get; set; } = true;
    public bool IsDeleted { get; set; }
    public DateTime? DeletedAtUtc { get; set; }
    /// <summary>Sponsored listings appear in the top search tier; order within the tier uses QualityScore.</summary>
    public bool IsSponsored { get; set; }
    /// <summary>Cached 0–100 merit score used to rank sponsored and organic results.</summary>
    public int QualityScore { get; set; }
    public DateTime? QualityScoreUpdatedAt { get; set; }
    /// <summary>When the doctor last opted into sponsorship (kept after auto-pause so Billing can show a paused state).</summary>
    public DateTime? SponsorshipEnabledAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public string? Username { get; set; }
    public string? PasswordHash { get; set; }
    public bool EmailVerified { get; set; }
    public string? EmailVerificationTokenHash { get; set; }
    public DateTime? EmailVerificationExpiresAtUtc { get; set; }
    public bool PhoneVerified { get; set; }
    public string? PhoneVerificationCodeHash { get; set; }
    public DateTime? PhoneVerificationExpiresAtUtc { get; set; }
    public string? OnboardingProfileJson { get; set; }
    public int OnboardingQuestionIndex { get; set; }
    public int ProfileCompletionPercent { get; set; }

    public string? StripeCustomerId { get; set; }
    public string? BillingEmail { get; set; }
    public string? BillingAddressJson { get; set; }
    /// <summary>When true, PerVisitFeeCents is used instead of the platform default.</summary>
    public bool OverridePerVisitFee { get; set; }
    /// <summary>Amount charged to this doctor when a NuviDoc patient is marked as showed (cents). Used only if OverridePerVisitFee is true.</summary>
    public int PerVisitFeeCents { get; set; }

    /// <summary>
    /// When set, we already emailed the doctor that Nuvi calling is paused until they add a payment method
    /// after free visits were used.
    /// </summary>
    public DateTime? BillingCallBlockedNotifiedAtUtc { get; set; }

    public ICollection<DoctorInsurance> DoctorInsurances { get; set; } = new List<DoctorInsurance>();
    public ICollection<DoctorBillingCharge> BillingCharges { get; set; } = new List<DoctorBillingCharge>();
    public ICollection<DoctorPatientReview> PatientReviews { get; set; } = new List<DoctorPatientReview>();
    public ICollection<DoctorDoctorLanguage> DoctorLanguages { get; set; } = new List<DoctorDoctorLanguage>();
    public ICollection<DoctorLocation> Locations { get; set; } = new List<DoctorLocation>();
    public ICollection<DoctorMedia> Media { get; set; } = new List<DoctorMedia>();
    public ICollection<DoctorPracticeFee> PracticeFees { get; set; } = new List<DoctorPracticeFee>();
}
