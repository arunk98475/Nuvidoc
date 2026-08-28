namespace Docovee.DS.Models;

public class AdminLoginRequest
{
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
}

public class PatientSearchRequest
{
    public string? Name { get; set; }
    public string? Phone { get; set; }
    public DateOnly? DateOfBirth { get; set; }
    public string? IssueKeyword { get; set; }
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 20;
}

public class PatientAdminDto
{
    public int Id { get; set; }
    public string Username { get; set; } = string.Empty;
    public string FullName { get; set; } = string.Empty;
    public DateOnly DateOfBirth { get; set; }
    public string Phone { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public string? LatestSpecialty { get; set; }
    public string? MedicalIssuesSummary { get; set; }
    public bool IsAccountClosed { get; set; }
}

public class PagedResult<T>
{
    public IReadOnlyList<T> Items { get; set; } = Array.Empty<T>();
    public int TotalCount { get; set; }
    public int Page { get; set; }
    public int PageSize { get; set; }
    public int TotalPages => PageSize > 0 ? (int)Math.Ceiling(TotalCount / (double)PageSize) : 0;
}

public class PatientAdminEditModel
{
    public int Id { get; set; }
    public string Username { get; set; } = string.Empty;
    public string FullName { get; set; } = string.Empty;
    public DateOnly DateOfBirth { get; set; }
    public string Phone { get; set; } = string.Empty;
    public string? Password { get; set; }
    public bool IsDeleted { get; set; }
    public DateTime? DeletedAtUtc { get; set; }
    public DateTime? LastLoginAtUtc { get; set; }
}

/// <summary>Admin settings for reminders to registered patients who have never booked.</summary>
public class PatientBookingReminderSettings
{
    public bool Enabled { get; set; }
    public int IntervalDays { get; set; } = 30;
    public int StopAfterMonths { get; set; } = 12;
    public bool EnableWhatsApp { get; set; }
    public bool EnableEmail { get; set; }
    public bool EnableSms { get; set; }
}

/// <summary>Admin settings for automatic patient account closure and permanent deletion.</summary>
public class PatientAccountLifecycleSettings
{
    public bool AutoCloseInactiveEnabled { get; set; }
    public int AutoCloseInactiveMonths { get; set; } = 24;

    public bool AutoDeleteClosedEnabled { get; set; }
    public int AutoDeleteClosedMonths { get; set; } = 3;
}

public class SiteSettingsModel
{
    public int DoctorSearchResultCount { get; set; } = 10;
    public string PromotedDoctorIds { get; set; } = string.Empty;
    public int MaxAiQuestions { get; set; } = 3;
    public int ReviewEligibleDaysAfterConfirmed { get; set; } = 1;

    public string FacebookUrl { get; set; } = string.Empty;
    public string InstagramUrl { get; set; } = string.Empty;
    public string TwitterUrl { get; set; } = string.Empty;
    public string LinkedInUrl { get; set; } = string.Empty;
    public string AppStoreUrl { get; set; } = string.Empty;
    public string PlayStoreUrl { get; set; } = string.Empty;
    public string TermsPdfUrl { get; set; } = string.Empty;
    public string PrivacyPdfUrl { get; set; } = string.Empty;
    public string ConsumerHealthPdfUrl { get; set; } = string.Empty;
    public string PrivacyChoicesPdfUrl { get; set; } = string.Empty;

    public bool HasSocialLinks =>
        !string.IsNullOrWhiteSpace(FacebookUrl)
        || !string.IsNullOrWhiteSpace(InstagramUrl)
        || !string.IsNullOrWhiteSpace(TwitterUrl)
        || !string.IsNullOrWhiteSpace(LinkedInUrl);

    public bool HasStoreLinks =>
        !string.IsNullOrWhiteSpace(AppStoreUrl) || !string.IsNullOrWhiteSpace(PlayStoreUrl);
}

/// <summary>Editable homepage marketing copy. Empty values use built-in defaults.</summary>
public class HomePageContentModel
{
    public string MetaDescription { get; set; } = string.Empty;
    public string HeroEyebrow { get; set; } = string.Empty;
    public string HeroHeadlineHtml { get; set; } = string.Empty;
    public string HeroSubtext { get; set; } = string.Empty;

    public string Stat1Num { get; set; } = string.Empty;
    public string Stat1Label { get; set; } = string.Empty;
    public string Stat2Num { get; set; } = string.Empty;
    public string Stat2Label { get; set; } = string.Empty;
    public string Stat3Num { get; set; } = string.Empty;
    public string Stat3Label { get; set; } = string.Empty;
    public string Stat4Num { get; set; } = string.Empty;
    public string Stat4Label { get; set; } = string.Empty;

    public string InsuranceTitle { get; set; } = string.Empty;

    public string WhyEyebrow { get; set; } = string.Empty;
    public string WhyHeadlineHtml { get; set; } = string.Empty;
    public string Why1Title { get; set; } = string.Empty;
    public string Why1Body { get; set; } = string.Empty;
    public string Why2Title { get; set; } = string.Empty;
    public string Why2Body { get; set; } = string.Empty;
    public string Why3Title { get; set; } = string.Empty;
    public string Why3Body { get; set; } = string.Empty;

    public string VisitEyebrow { get; set; } = string.Empty;
    public string VisitHeadlineHtml { get; set; } = string.Empty;

    public string SpecialtyEyebrow { get; set; } = string.Empty;
    public string SpecialtyHeadlineHtml { get; set; } = string.Empty;
    public string SpecialtyBody { get; set; } = string.Empty;

    public string DoctorsEyebrow { get; set; } = string.Empty;
    public string DoctorsHeadlineHtml { get; set; } = string.Empty;
    public string DoctorsSubtitle { get; set; } = string.Empty;

    public string HowEyebrow { get; set; } = string.Empty;
    public string HowHeadlineHtml { get; set; } = string.Empty;
    public string How1Title { get; set; } = string.Empty;
    public string How1Body { get; set; } = string.Empty;
    public string How2Title { get; set; } = string.Empty;
    public string How2Body { get; set; } = string.Empty;
    public string How3Title { get; set; } = string.Empty;
    public string How3Body { get; set; } = string.Empty;

    public string CtaEyebrow { get; set; } = string.Empty;
    public string CtaHeadlineHtml { get; set; } = string.Empty;
    public string CtaSubtext { get; set; } = string.Empty;
    public string CtaButtonText { get; set; } = string.Empty;
    public string CtaNote { get; set; } = string.Empty;
}

public class DoctorAdminDto
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Specialty { get; set; } = string.Empty;
    public string? PracticeName { get; set; }
    public string? Location { get; set; }
    public string City { get; set; } = string.Empty;
    public string State { get; set; } = string.Empty;
    public decimal GoogleRating { get; set; }
    public int GoogleReviewCount { get; set; }
    public string? PhotoUrl { get; set; }
    public bool IsActive { get; set; }
    public bool IsDeleted { get; set; }
    public bool IsSponsored { get; set; }
    public int QualityScore { get; set; }
    public int PatientReviewCount { get; set; }
    public bool IsNexHealthIntegrated { get; set; }
    public bool PaymentVerified { get; set; }
    public int FreeVisitsRemaining { get; set; }
}

public class DoctorAdminListFilters
{
    public string? Search { get; set; }
    public string? Location { get; set; }
    public string? Specialty { get; set; }
    public decimal? MinRating { get; set; }
    public int? MinPatientReviews { get; set; }
    public int? FreeVisitsRemaining { get; set; }
    /// <summary>null = any, true = verified, false = not verified</summary>
    public bool? PaymentVerified { get; set; }
}

public class DoctorAdminEditModel
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
    public string? GmbPhotoLink { get; set; }
    public string? VideoUrl { get; set; }
    public string? Website { get; set; }
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
    public string? TagLine { get; set; }
    public string Gender { get; set; } = "Other";
    public bool IsActive { get; set; } = true;
    public bool IsDeleted { get; set; }
    public DateTime? DeletedAtUtc { get; set; }
    public bool OverridePerVisitFee { get; set; }
    /// <summary>USD charged when a NuviDoc patient is marked as showed (maps to PerVisitFeeCents). Used only when OverridePerVisitFee is true.</summary>
    public decimal PerVisitFeeUsd { get; set; }
    public string? Username { get; set; }
    public string? Password { get; set; }
}

public class DoctorImportResult
{
    public int Imported { get; set; }
    public int Failed { get; set; }
    public IReadOnlyList<string> Errors { get; set; } = Array.Empty<string>();
}

public class PollingQuestionDto
{
    public int Id { get; set; }
    public string Question { get; set; } = string.Empty;
    public string? ValidationHint { get; set; }
    public int SortOrder { get; set; }
    public int MatchWeight { get; set; }
    public string? MatchWeightLabel { get; set; }
    public bool IsActive { get; set; }
}

public class PollingQuestionEditModel
{
    public int Id { get; set; }
    public string Question { get; set; } = string.Empty;
    public string? ValidationHint { get; set; }
    public int SortOrder { get; set; }
    public int MatchWeight { get; set; } = 5;
    public string? MatchWeightLabel { get; set; }
    public bool IsActive { get; set; } = true;
}

public class DoctorReviewRequest
{
    public int DoctorId { get; set; }
    public string ReviewerName { get; set; } = string.Empty;
    public int Rating { get; set; }
    public string ReviewText { get; set; } = string.Empty;
    public string? WaitingTime { get; set; }
    public string? Recommendation { get; set; }
    public string? PhotoUrl { get; set; }
    public int? PatientId { get; set; }
}

public class DoctorReviewDto
{
    public int Id { get; set; }
    public string ReviewerName { get; set; } = string.Empty;
    public int Rating { get; set; }
    public string ReviewText { get; set; } = string.Empty;
    public string? WaitingTime { get; set; }
    public string? Recommendation { get; set; }
    public string? PhotoUrl { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class DoctorLanguageDto
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public int SortOrder { get; set; }
    public bool IsActive { get; set; }
}

public class DoctorLanguageEditModel
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public int SortOrder { get; set; }
    public bool IsActive { get; set; } = true;
}
