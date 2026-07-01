namespace Docovee.BLL.Models;

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
}

public class SiteSettingsModel
{
    public int DoctorSearchResultCount { get; set; } = 10;
    public string PromotedDoctorIds { get; set; } = string.Empty;
    public int MaxAiQuestions { get; set; } = 3;
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
    public int PatientReviewCount { get; set; }
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
    public int? PatientId { get; set; }
}

public class DoctorReviewDto
{
    public int Id { get; set; }
    public string ReviewerName { get; set; } = string.Empty;
    public int Rating { get; set; }
    public string ReviewText { get; set; } = string.Empty;
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
