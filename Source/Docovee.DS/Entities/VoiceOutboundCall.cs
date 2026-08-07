namespace Docovee.DS.Entities;

public class VoiceOutboundCall
{
    public int Id { get; set; }
    public string ConversationId { get; set; } = string.Empty;
    public string? CallSid { get; set; }
    public Guid SessionKey { get; set; }
    public int? SearchSessionId { get; set; }
    public SearchSession? SearchSession { get; set; }
    public int? PatientId { get; set; }
    public Patient? Patient { get; set; }
    public int DoctorId { get; set; }
    public Doctor Doctor { get; set; } = null!;
    public string PatientName { get; set; } = string.Empty;
    public string? PatientPhone { get; set; }
    public string? PatientEmail { get; set; }
    public string? VisitReason { get; set; }
    public string? ToNumber { get; set; }
    public string Status { get; set; } = VoiceOutboundCallStatuses.Initiated;
    public string? OutcomeNotes { get; set; }
    public int? AppointmentId { get; set; }
    public Appointment? Appointment { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }
}

public static class VoiceOutboundCallStatuses
{
    public const string Initiated = "Initiated";
    public const string Completed = "Completed";
    public const string Booked = "Booked";
    public const string Failed = "Failed";
    public const string NoSlot = "NoSlot";
    public const string Declined = "Declined";
    public const string NoAnswer = "NoAnswer";
}

public class PatientNotification
{
    public int Id { get; set; }
    public int PatientId { get; set; }
    public Patient Patient { get; set; } = null!;
    public string Type { get; set; } = PatientNotificationTypes.AppointmentBooked;
    public string Title { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
    public int? AppointmentId { get; set; }
    public Appointment? Appointment { get; set; }
    public int? DoctorId { get; set; }
    public bool IsRead { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public static class PatientNotificationTypes
{
    public const string AppointmentBooked = "AppointmentBooked";
    public const string AppointmentUpdate = "AppointmentUpdate";
    public const string VoiceCallUpdate = "VoiceCallUpdate";
}
