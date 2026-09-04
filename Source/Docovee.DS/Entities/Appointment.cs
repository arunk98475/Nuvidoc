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

    public static bool CanMarkCompleted(string? status)
    {
        var s = Normalize(status);
        if (s == Completed)
            return false;
        return s is Confirmed or Unconfirmed or PracticeRescheduled or PatientRescheduled;
    }

    public static bool IsConfirmedWithDoctor(string? status) =>
        string.Equals(Normalize(status), Confirmed, StringComparison.OrdinalIgnoreCase);

    public static bool IsPatientNoShow(string? status) =>
        string.Equals(Normalize(status), PatientNoShow, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Patient can leave a review or report no-show once the feedback window opens.
    /// When feedback requests are enabled, that is CreatedAt + hoursAfterBooking.
    /// When disabled, once the visit start date is today/past and status is Confirmed or Completed.
    /// </summary>
    public static bool CanPatientLeaveFeedback(
        string? status,
        DateTime createdAtUtc,
        DateTime startsAt,
        bool feedbackRequestEnabled,
        int hoursAfterBooking,
        bool hasExistingReview,
        DateTime? utcNow = null)
    {
        if (hasExistingReview)
            return false;
        if (IsCanceled(status) || IsPatientNoShow(status))
            return false;

        var now = utcNow ?? DateTime.UtcNow;
        if (feedbackRequestEnabled)
        {
            var hours = Math.Max(1, hoursAfterBooking);
            return now >= createdAtUtc.ToUniversalTime().AddHours(hours);
        }

        var s = Normalize(status);
        if (s is not (Confirmed or Completed))
            return false;
        return startsAt.Date <= DateTime.Today;
    }

    public static DateTime? GetFeedbackAvailableAtUtc(
        DateTime createdAtUtc,
        bool feedbackRequestEnabled,
        int hoursAfterBooking)
    {
        if (!feedbackRequestEnabled)
            return null;
        return createdAtUtc.ToUniversalTime().AddHours(Math.Max(1, hoursAfterBooking));
    }
}

public static class AppointmentSources
{
    public const string PublicProfile = "PublicProfile";
    public const string NuviChat = "NuviChat";
    public const string PmsInbound = "PmsInbound";
    public const string PmsBookedDisplayLabel = "PMS Booked";

    public static bool IsPmsInbound(string? source) =>
        string.Equals(source, PmsInbound, StringComparison.OrdinalIgnoreCase);

    public static bool IsNuvidocBooking(string? source) => !IsPmsInbound(source);
}
