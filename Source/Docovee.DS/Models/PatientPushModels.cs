namespace Docovee.DS.Models;

/// <summary>
/// Transport-agnostic patient alert payload.
/// Used by SignalR now; FCM/APNs channels can consume the same shape later.
/// </summary>
public sealed class PatientPushMessage
{
    public string Type { get; set; } = "VoiceCallUpdate";
    public int? PatientId { get; set; }
    public Guid? SessionKey { get; set; }
    public string? ConversationId { get; set; }
    public string Status { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
    public int? DoctorId { get; set; }
    public string? DoctorName { get; set; }
    public int? AppointmentId { get; set; }
    public DateTime? StartsAt { get; set; }
    public DateTime? EndsAt { get; set; }
    public string? SlotLabel { get; set; }
    public int? NotificationId { get; set; }
    public Dictionary<string, string>? Data { get; set; }
}

public static class PatientPushGroupNames
{
    public static string Session(Guid sessionKey) => $"session:{sessionKey:D}";
    public static string Patient(int patientId) => $"patient:{patientId}";
}

public static class PatientPushClientMethods
{
    public const string BookingUpdated = "bookingUpdated";
}
