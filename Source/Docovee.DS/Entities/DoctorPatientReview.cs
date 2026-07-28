namespace Docovee.DS.Entities;

public class DoctorPatientReview
{
    public int Id { get; set; }
    public int DoctorId { get; set; }
    public Doctor Doctor { get; set; } = null!;
    public int? PatientId { get; set; }
    public Patient? Patient { get; set; }
    public string ReviewerName { get; set; } = string.Empty;
    public int Rating { get; set; }
    public string ReviewText { get; set; } = string.Empty;
    /// <summary>Excellent, Good, Average, or Bad.</summary>
    public string? WaitingTime { get; set; }
    /// <summary>Highly Recommended, Neutral, or Not Recommended.</summary>
    public string? Recommendation { get; set; }
    public string? PhotoUrl { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
