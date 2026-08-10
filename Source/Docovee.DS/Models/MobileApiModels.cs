namespace Docovee.DS.Models;

public class MobileVoiceCallDto
{
    public int Id { get; set; }
    public string ConversationId { get; set; } = string.Empty;
    public Guid SessionKey { get; set; }
    public int DoctorId { get; set; }
    public string DoctorName { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public bool IsTerminal { get; set; }
    public int? AppointmentId { get; set; }
    public DateTime? AppointmentStartsAt { get; set; }
    public string? AppointmentSlotLabel { get; set; }
    public string? OutcomeNotes { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public class MobileNotificationDto
{
    public int Id { get; set; }
    public string Type { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
    public int? AppointmentId { get; set; }
    public int? DoctorId { get; set; }
    public DateTime? AppointmentStartsAt { get; set; }
    public DateTime? AppointmentEndsAt { get; set; }
    public string? SlotLabel { get; set; }
    public bool IsRead { get; set; }
    public DateTime CreatedAt { get; set; }
    public bool IsFutureAppointment { get; set; }
}

public class MobileNotificationsResponse
{
    public int UnreadCount { get; set; }
    public IReadOnlyList<MobileNotificationDto> Items { get; set; } = Array.Empty<MobileNotificationDto>();
}

public class MobileAppointmentsResponse
{
    public IReadOnlyList<PatientAppointmentDto> Upcoming { get; set; } = Array.Empty<PatientAppointmentDto>();
    public IReadOnlyList<PatientAppointmentDto> Past { get; set; } = Array.Empty<PatientAppointmentDto>();
}

public class MobileMeResponse
{
    public int PatientId { get; set; }
    public string Email { get; set; } = string.Empty;
    public string FullName { get; set; } = string.Empty;
    public string? Phone { get; set; }
}
