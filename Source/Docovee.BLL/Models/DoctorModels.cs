namespace Docovee.BLL.Models;

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
}
