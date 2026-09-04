namespace Docovee.DS.Models;

public class CreateAppointmentRequest
{
    public int DoctorId { get; set; }
    public string PatientName { get; set; } = string.Empty;
    public string? PatientPhone { get; set; }
    public string? PatientEmail { get; set; }
    public string VisitReason { get; set; } = string.Empty;
    /// <summary>ISO date yyyy-MM-dd</summary>
    public string Date { get; set; } = string.Empty;
    /// <summary>e.g. 9:00 AM</summary>
    public string TimeLabel { get; set; } = string.Empty;
    /// <summary>Patient date of birth (yyyy-MM-dd or MM/dd/yyyy).</summary>
    public string? DateOfBirth { get; set; }
    public string? Source { get; set; }
}

public class CreateAppointmentResponse
{
    public bool Success { get; set; }
    public string? Message { get; set; }
    public int? AppointmentId { get; set; }
}

public class DoctorAppointmentDto
{
    public int Id { get; set; }
    public int? PatientId { get; set; }
    public int? SearchSessionId { get; set; }
    public string PatientName { get; set; } = string.Empty;
    public string? PatientPhone { get; set; }
    public string? PatientEmail { get; set; }
    public DateOnly? PatientDateOfBirth { get; set; }
    public string VisitReason { get; set; } = string.Empty;
    public DateTime StartsAt { get; set; }
    public string Status { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public class PatientAppointmentDto
{
    public int Id { get; set; }
    public int DoctorId { get; set; }
    public string DoctorName { get; set; } = string.Empty;
    public string DoctorSpecialty { get; set; } = string.Empty;
    public string? DoctorPhotoUrl { get; set; }
    public string? DoctorLocation { get; set; }
    public string VisitReason { get; set; } = string.Empty;
    public DateTime StartsAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public string Status { get; set; } = string.Empty;
    public bool HasReview { get; set; }
    public bool CanLeaveReview { get; set; }
    public bool CanReportNoShow { get; set; }
    public DateTime? FeedbackAvailableAtUtc { get; set; }
    public int? ReviewRating { get; set; }
    public string? ReviewText { get; set; }
    public string? ReviewWaitingTime { get; set; }
    public string? ReviewRecommendation { get; set; }
    public DateTime? ReviewedAt { get; set; }
}
