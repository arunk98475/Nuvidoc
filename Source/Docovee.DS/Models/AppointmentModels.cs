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
    public string PatientName { get; set; } = string.Empty;
    public string? PatientPhone { get; set; }
    public string VisitReason { get; set; } = string.Empty;
    public DateTime StartsAt { get; set; }
    public string Status { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
