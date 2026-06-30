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
    public string AvatarInitials { get; set; } = string.Empty;
    public string? TagLine { get; set; }
    public Gender Gender { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public string? Username { get; set; }
    public string? PasswordHash { get; set; }
    public string? OnboardingProfileJson { get; set; }

    public ICollection<DoctorInsurance> DoctorInsurances { get; set; } = new List<DoctorInsurance>();
    public ICollection<DoctorPatientReview> PatientReviews { get; set; } = new List<DoctorPatientReview>();
    public ICollection<DoctorDoctorLanguage> DoctorLanguages { get; set; } = new List<DoctorDoctorLanguage>();
}
