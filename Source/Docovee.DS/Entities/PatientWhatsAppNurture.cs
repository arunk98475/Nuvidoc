namespace Docovee.DS.Entities;

/// <summary>
/// Interactive WhatsApp nurture conversation state (post lead-handoff until feedback or opt-out).
/// </summary>
public class PatientWhatsAppNurture
{
    public int Id { get; set; }
    public int PatientId { get; set; }
    public Patient Patient { get; set; } = null!;
    public int AppointmentId { get; set; }
    public Appointment Appointment { get; set; } = null!;
    public int DoctorId { get; set; }
    public Doctor Doctor { get; set; } = null!;
    public string Stage { get; set; } = PatientWhatsAppNurtureStages.AskBooked;
    public string? WhatsAppTo { get; set; }
    public DateTime? NextFollowUpAtUtc { get; set; }
    public int? Rating { get; set; }
    public string? ExperienceText { get; set; }
    public string? LastOutboundMessageSid { get; set; }
    public string? LastError { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAtUtc { get; set; }
}

public static class PatientWhatsAppNurtureStages
{
    public const string AskBooked = "AskBooked";
    public const string AskContactPracticeNotBooked = "AskContactPracticeNotBooked";
    public const string AskAttended = "AskAttended";
    public const string AskUpcomingOrPassed = "AskUpcomingOrPassed";
    public const string AskUpcomingWindow = "AskUpcomingWindow";
    public const string AskContactPracticeMissed = "AskContactPracticeMissed";
    public const string AskRating = "AskRating";
    public const string AskExperience = "AskExperience";
    public const string FollowUpScheduled = "FollowUpScheduled";
    public const string Completed = "Completed";

    public static bool IsOpen(string? stage) =>
        !string.IsNullOrWhiteSpace(stage)
        && !string.Equals(stage, Completed, StringComparison.OrdinalIgnoreCase);
}
