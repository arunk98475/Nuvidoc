namespace Docovee.DS.Entities;

public class Appointment
{
    public int Id { get; set; }
    public int DoctorId { get; set; }
    public Doctor Doctor { get; set; } = null!;
    public int? PatientId { get; set; }
    public Patient? Patient { get; set; }
    public string PatientName { get; set; } = string.Empty;
    public string? PatientPhone { get; set; }
    public string? PatientEmail { get; set; }
    public string VisitReason { get; set; } = string.Empty;
    public DateTime StartsAt { get; set; }
    public string Status { get; set; } = AppointmentStatuses.New;
    public string Source { get; set; } = AppointmentSources.PublicProfile;
    public int? SearchSessionId { get; set; }
    public DateOnly? PatientDateOfBirth { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public static class AppointmentStatuses
{
    public const string New = "New";
    public const string Confirmed = "Confirmed";
    public const string Reschedule = "Reschedule";
    public const string Cancelled = "Cancelled";
    public const string Completed = "Completed";
}

public static class AppointmentSources
{
    public const string PublicProfile = "PublicProfile";
    public const string NuviChat = "NuviChat";
}
