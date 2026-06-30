namespace Docovee.BLL.Models;

public enum AccountType
{
    Patient,
    Doctor,
    Admin
}

public class AccountLoginRequest
{
    public AccountType AccountType { get; set; }
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
}

public class PatientProfileDto
{
    public string Username { get; set; } = string.Empty;
    public string FullName { get; set; } = string.Empty;
    public DateOnly DateOfBirth { get; set; }
    public string Phone { get; set; } = string.Empty;
    public DateTime MemberSince { get; set; }
    public IReadOnlyList<PatientSearchHistoryDto> SearchHistory { get; set; } = Array.Empty<PatientSearchHistoryDto>();
}

public class PatientSearchHistoryDto
{
    public DateTime Date { get; set; }
    public string? Specialty { get; set; }
    public string? Location { get; set; }
    public string? MedicalIssuesSummary { get; set; }
}

public class DoctorProfileDto
{
    public int Id { get; set; }
    public string? Username { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Specialty { get; set; } = string.Empty;
    public string? PracticeName { get; set; }
    public string? Location { get; set; }
    public string? Address { get; set; }
    public string? OfficePhoneNumber { get; set; }
    public string? PhotoUrl { get; set; }
    public string? GmbPhotoLink { get; set; }
    public string City { get; set; } = string.Empty;
    public string State { get; set; } = string.Empty;
    public string ZipCode { get; set; } = string.Empty;
    public IReadOnlyList<string> InsuranceCarriers { get; set; } = Array.Empty<string>();
    public decimal GoogleRating { get; set; }
    public int GoogleReviewCount { get; set; }
    public int PatientReviewCount { get; set; }
    public decimal? PatientReviewAverage { get; set; }
    public string? TagLine { get; set; }
    public string? Niche { get; set; }
    public bool IsActive { get; set; }
    public DateTime MemberSince { get; set; }
    public int ProfileCompletionPercent { get; set; }
}

public class AccountRegisterRequest
{
    public AccountType AccountType { get; set; } = AccountType.Patient;

    // Shared
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string ConfirmPassword { get; set; } = string.Empty;

    // Patient
    public string FullName { get; set; } = string.Empty;
    public DateOnly? DateOfBirth { get; set; }
    public string Phone { get; set; } = string.Empty;

    // Doctor
    public string DoctorName { get; set; } = string.Empty;
    public string? PracticeName { get; set; }
    public string Specialty { get; set; } = string.Empty;
    public string City { get; set; } = string.Empty;
    public string State { get; set; } = string.Empty;
    public string ZipCode { get; set; } = string.Empty;
    public string? OfficePhoneNumber { get; set; }
    public string? GmbPhotoLink { get; set; }
    public string? Address { get; set; }
    public string? TagLine { get; set; }
    public string? Niche { get; set; }
    public List<int> InsuranceCarrierIds { get; set; } = new();
}

public class PatientProfileEditModel
{
    public string Username { get; set; } = string.Empty;
    public string FullName { get; set; } = string.Empty;
    public DateOnly DateOfBirth { get; set; }
    public string Phone { get; set; } = string.Empty;
    public string? NewPassword { get; set; }
}

public class DoctorProfileEditModel
{
    public string Username { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? PracticeName { get; set; }
    public string Specialty { get; set; } = string.Empty;
    public string? Address { get; set; }
    public string City { get; set; } = string.Empty;
    public string State { get; set; } = string.Empty;
    public string ZipCode { get; set; } = string.Empty;
    public string? OfficePhoneNumber { get; set; }
    public string? GmbPhotoLink { get; set; }
    public string? TagLine { get; set; }
    public string? Niche { get; set; }
    public string? NewPassword { get; set; }
    public List<int> InsuranceCarrierIds { get; set; } = new();
}

public class AccountRegisterResponse
{
    public bool Success { get; set; }
    public string? Message { get; set; }
    public AccountType AccountType { get; set; }
}
