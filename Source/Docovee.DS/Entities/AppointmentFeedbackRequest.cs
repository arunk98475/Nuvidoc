namespace Docovee.DS.Entities;

public class AppointmentFeedbackRequest
{
    public int Id { get; set; }
    public int AppointmentId { get; set; }
    public Appointment Appointment { get; set; } = null!;
    public int? PatientId { get; set; }
    public Patient? Patient { get; set; }
    public int DoctorId { get; set; }
    public Doctor Doctor { get; set; } = null!;
    public DateTime ScheduledAtUtc { get; set; }
    public DateTime? SentAtUtc { get; set; }
    public string Channel { get; set; } = AppointmentFeedbackChannels.Pending;
    public string Stage { get; set; } = AppointmentFeedbackStages.Pending;
    public int? Rating { get; set; }
    public string? WaitingTime { get; set; }
    public string? Recommendation { get; set; }
    public string? ReviewText { get; set; }
    public string? WhatsAppTo { get; set; }
    public string? LastOutboundMessageSid { get; set; }
    public string? LastError { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}

public static class AppointmentFeedbackChannels
{
    public const string Pending = "Pending";
    public const string WhatsApp = "WhatsApp";
    public const string SmsFallback = "SmsFallback";
}

public static class AppointmentFeedbackStages
{
    public const string Pending = "Pending";
    public const string RatingSent = "RatingSent";
    public const string WaitingSent = "WaitingSent";
    public const string RecommendSent = "RecommendSent";
    public const string ReviewTextAwaiting = "ReviewTextAwaiting";
    public const string Completed = "Completed";
    public const string NoShow = "NoShow";
    public const string Failed = "Failed";
}

public static class AppointmentFeedbackItemIds
{
    public const string Star5 = "star_5";
    public const string Star4 = "star_4";
    public const string Star3 = "star_3";
    public const string Star2 = "star_2";
    public const string Star1 = "star_1";
    public const string NoShow = "no_show";

    public const string WaitExcellent = "wait_excellent";
    public const string WaitGood = "wait_good";
    public const string WaitAverage = "wait_average";
    public const string WaitBad = "wait_bad";

    public const string RecHigh = "rec_high";
    public const string RecNeutral = "rec_neutral";
    public const string RecNot = "rec_not";

    public static int? ParseStarRating(string? itemId) => itemId?.Trim().ToLowerInvariant() switch
    {
        Star5 => 5,
        Star4 => 4,
        Star3 => 3,
        Star2 => 2,
        Star1 => 1,
        _ => null
    };

    public static string? ParseWaitingTime(string? itemId) => itemId?.Trim().ToLowerInvariant() switch
    {
        WaitExcellent => "Excellent",
        WaitGood => "Good",
        WaitAverage => "Average",
        WaitBad => "Bad",
        _ => null
    };

    public static string? ParseRecommendation(string? itemId) => itemId?.Trim().ToLowerInvariant() switch
    {
        RecHigh => "Highly Recommended",
        RecNeutral => "Neutral",
        RecNot => "Not Recommended",
        _ => null
    };

    public static bool IsNoShow(string? itemId) =>
        string.Equals(itemId?.Trim(), NoShow, StringComparison.OrdinalIgnoreCase);
}
