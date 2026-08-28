namespace Docovee.DS.Models;

public class DoctorSearchRequest
{
    public Guid SessionKey { get; set; }
    public string Location { get; set; } = string.Empty;
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }
    public string? InsurancePlan { get; set; }
    public int? InsuranceCarrierId { get; set; }
    public string? GenderPreference { get; set; }
    public string? CommunicationStyle { get; set; }
    public string? AvailabilityPreference { get; set; }
    public string? PreferredLanguage { get; set; }
    public string? AdditionalPreference { get; set; }
}

public class DoctorDto
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Specialty { get; set; } = string.Empty;
    public string? PracticeName { get; set; }
    public string Location { get; set; } = string.Empty;
    public string? PhotoUrl { get; set; }
    public string AvatarInitials { get; set; } = string.Empty;
    public string AvatarBg { get; set; } = "#EAF2EE";
    public string AvatarColor { get; set; } = "#3D6B5A";
    public int MatchScore { get; set; }
    public decimal GoogleRating { get; set; }
    public int GoogleReviewCount { get; set; }
    public string Tag { get; set; } = string.Empty;
    public string? MatchReason { get; set; }
    public bool Recommended { get; set; }
    public double? DistanceMiles { get; set; }
    public string? Niche { get; set; }
    public string? Top3Procedures { get; set; }
    public string? SummaryOfReviews { get; set; }
    public decimal? PatientReviewAverage { get; set; }
    public int PatientReviewCount { get; set; }
    public string? OfficePhoneNumber { get; set; }
    public int? YearsOfPractice { get; set; }
    public bool IsSponsored { get; set; }
    public int QualityScore { get; set; }
}

public class FeaturedDoctorCardDto
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Specialty { get; set; } = string.Empty;
    public string City { get; set; } = string.Empty;
    public string State { get; set; } = string.Empty;
    public string? PhotoUrl { get; set; }
    public string AvatarInitials { get; set; } = string.Empty;
    public decimal GoogleRating { get; set; }
    public int GoogleReviewCount { get; set; }
    public string? HighlightText { get; set; }
    public string? Niche { get; set; }
    public bool IsFeatured { get; set; }
}

public class PublicDoctorReviewDto
{
    public string ReviewerName { get; set; } = string.Empty;
    public int Rating { get; set; }
    public string ReviewText { get; set; } = string.Empty;
    public string? WaitingTime { get; set; }
    public string? Recommendation { get; set; }
    public string? PhotoUrl { get; set; }
    /// <summary>e.g. "Google" for live Google reviews; null/empty for NuviDoc patient reviews.</summary>
    public string? Source { get; set; }
}

public class DoctorMediaDto
{
    public int Id { get; set; }
    public string MediaType { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public string? Caption { get; set; }
    public long FileSizeBytes { get; set; }
    public int SortOrder { get; set; }
}

public class PublicDoctorProfileDto
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Specialty { get; set; } = string.Empty;
    public string? PracticeName { get; set; }
    public string City { get; set; } = string.Empty;
    public string State { get; set; } = string.Empty;
    public string ZipCode { get; set; } = string.Empty;
    public string? Address { get; set; }
    public string? PhotoUrl { get; set; }
    public string? PracticeLogoUrl { get; set; }
    public string AvatarInitials { get; set; } = string.Empty;
    public string? OfficePhoneNumber { get; set; }
    public string? Niche { get; set; }
    public string? SummaryOfReviews { get; set; }
    public string? Top3Procedures { get; set; }
    public int? YearsOfPractice { get; set; }
    public int? GraduationYear { get; set; }
    public decimal GoogleRating { get; set; }
    public int GoogleReviewCount { get; set; }
    public IReadOnlyList<PublicDoctorReviewDto> GoogleReviews { get; set; } = Array.Empty<PublicDoctorReviewDto>();
    public bool GoogleReviewsLive { get; set; }
    public string? VideoUrl { get; set; }
    public string? FacebookUrl { get; set; }
    public string? InstagramUrl { get; set; }
    public string? TikTokUrl { get; set; }
    public string? LinkedInUrl { get; set; }
    public string? YoutubeChannelUrl { get; set; }
    public string? Website { get; set; }
    public bool OffersDentalImplants { get; set; }
    public bool OffersTmj { get; set; }
    public bool OffersBotox { get; set; }
    public IReadOnlyList<string> InsuranceCarriers { get; set; } = Array.Empty<string>();
    public IReadOnlyList<int> AcceptedInsuranceCarrierIds { get; set; } = Array.Empty<int>();
    public IReadOnlyList<PublicDoctorInsuranceDto> AcceptedInsurances { get; set; } = Array.Empty<PublicDoctorInsuranceDto>();
    public IReadOnlyList<string> Languages { get; set; } = Array.Empty<string>();
    public IReadOnlyList<PublicDoctorReviewDto> Reviews { get; set; } = Array.Empty<PublicDoctorReviewDto>();
    public IReadOnlyList<DoctorMediaDto> Media { get; set; } = Array.Empty<DoctorMediaDto>();
    public IReadOnlyList<string> VisitReasons { get; set; } = Array.Empty<string>();
}

public class PublicDoctorInsuranceDto
{
    public int CarrierId { get; set; }
    public string CarrierName { get; set; } = string.Empty;
    public string CarrierCode { get; set; } = string.Empty;
    public IReadOnlyList<string> Plans { get; set; } = Array.Empty<string>();
}

public class DoctorQaItemVm
{
    public int QuestionId { get; set; }
    public string Category { get; set; } = string.Empty;
    public string Question { get; set; } = string.Empty;
    public string InputType { get; set; } = string.Empty;
    public string Placeholder { get; set; } = string.Empty;
    public string Answer { get; set; } = string.Empty;
}

public class DoctorQaPageModel
{
    public IReadOnlyList<DoctorQaItemVm> Items { get; set; } = Array.Empty<DoctorQaItemVm>();
}
