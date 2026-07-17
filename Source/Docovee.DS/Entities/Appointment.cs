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
    public string Status { get; set; } = AppointmentStatuses.Unconfirmed;
    public string Source { get; set; } = AppointmentSources.PublicProfile;
    public int? SearchSessionId { get; set; }
    public DateOnly? PatientDateOfBirth { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public static class AppointmentStatuses
{
    // Canonical statuses
    public const string Confirmed = "Confirmed";
    public const string Unconfirmed = "Unconfirmed";
    public const string PracticeRescheduled = "PracticeRescheduled";
    public const string PatientRescheduled = "PatientRescheduled";
    public const string PracticeCanceled = "PracticeCanceled";
    public const string PatientCanceled = "PatientCanceled";
    public const string PatientNoShow = "PatientNoShow";

    // Legacy values still present in older rows
    public const string New = "New";
    public const string Reschedule = "Reschedule";
    public const string Cancelled = "Cancelled";
    public const string Completed = "Completed";

    private static readonly HashSet<string> Known = new(StringComparer.OrdinalIgnoreCase)
    {
        Confirmed,
        Unconfirmed,
        PracticeRescheduled,
        PatientRescheduled,
        PracticeCanceled,
        PatientCanceled,
        PatientNoShow,
        New,
        Reschedule,
        Cancelled,
        Completed
    };

    public static bool IsKnown(string? status) =>
        !string.IsNullOrWhiteSpace(status) && Known.Contains(status);

    public static string Normalize(string? status)
    {
        if (string.IsNullOrWhiteSpace(status))
            return Unconfirmed;

        return status switch
        {
            New => Unconfirmed,
            Reschedule => PatientRescheduled,
            Cancelled => PatientCanceled,
            _ => status
        };
    }

    public static string DisplayLabel(string? status) => Normalize(status) switch
    {
        Confirmed => "Confirmed",
        Unconfirmed => "Unconfirmed",
        PracticeRescheduled => "Practice rescheduled",
        PatientRescheduled => "Patient rescheduled",
        PracticeCanceled => "Practice canceled",
        PatientCanceled => "Patient canceled",
        PatientNoShow => "Patient no-show",
        Completed => "Completed",
        _ => status ?? "Unknown"
    };

    public static bool IsActive(string? status)
    {
        var s = Normalize(status);
        return s is Unconfirmed or Confirmed or PracticeRescheduled or PatientRescheduled;
    }

    public static bool IsCanceled(string? status)
    {
        var s = Normalize(status);
        return s is PracticeCanceled or PatientCanceled;
    }

    public static bool IsRescheduled(string? status)
    {
        var s = Normalize(status);
        return s is PracticeRescheduled or PatientRescheduled;
    }

    public static bool IsUnconfirmed(string? status) =>
        string.Equals(Normalize(status), Unconfirmed, StringComparison.OrdinalIgnoreCase);

    public static bool NeedsDoctorAttention(string? status)
    {
        var s = Normalize(status);
        return s is Unconfirmed or PracticeRescheduled or PatientRescheduled;
    }

    public static bool CanConfirm(string? status)
    {
        var s = Normalize(status);
        return s is Unconfirmed or PracticeRescheduled or PatientRescheduled;
    }

    public static bool CanPracticeCancel(string? status) => IsActive(status);

    public static bool CanMarkNoShow(string? status)
    {
        var s = Normalize(status);
        return s is Confirmed or Unconfirmed or PracticeRescheduled or PatientRescheduled;
    }

    public static bool IsConfirmedWithDoctor(string? status) =>
        string.Equals(Normalize(status), Confirmed, StringComparison.OrdinalIgnoreCase);

    public static bool CanPatientLeaveReview(
        string? status,
        DateTime startsAt,
        int daysAfterConfirmed,
        bool hasExistingReview)
    {
        if (hasExistingReview)
            return false;
        if (!IsConfirmedWithDoctor(status))
            return false;

        var eligibleFrom = startsAt.Date.AddDays(Math.Max(0, daysAfterConfirmed));
        return DateTime.Today >= eligibleFrom;
    }

    public static DateOnly GetReviewAvailableOn(DateTime startsAt, int daysAfterConfirmed) =>
        DateOnly.FromDateTime(startsAt.Date.AddDays(Math.Max(0, daysAfterConfirmed)));
}

public static class AppointmentSources
{
    public const string PublicProfile = "PublicProfile";
    public const string NuviChat = "NuviChat";
    public const string PmsInbound = "PmsInbound";
}
