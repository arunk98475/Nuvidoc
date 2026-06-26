using Docovee.DS.Enums;

namespace Docovee.DS.Entities;

public class SearchSession
{
    public int Id { get; set; }
    public Guid SessionKey { get; set; } = Guid.NewGuid();
    public int? PatientId { get; set; }
    public Patient? Patient { get; set; }

    public string? Location { get; set; }
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }
    public int? InsuranceCarrierId { get; set; }
    public string? InsurancePlanText { get; set; }
    public InsuranceCarrier? InsuranceCarrier { get; set; }

    public string? Specialty { get; set; }
    public UrgencyLevel Urgency { get; set; } = UrgencyLevel.Routine;
    public string? SearchNotes { get; set; }
    public string? MedicalIssuesSummary { get; set; }
    public GenderPreference GenderPreference { get; set; } = GenderPreference.NoPreference;
    public string? CommunicationStyle { get; set; }
    public string? AvailabilityPreference { get; set; }
    public string? SearchContextJson { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<ChatMessage> ChatMessages { get; set; } = new List<ChatMessage>();
}
