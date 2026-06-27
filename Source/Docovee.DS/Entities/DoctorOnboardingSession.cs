namespace Docovee.DS.Entities;

public class DoctorOnboardingSession
{
    public int Id { get; set; }
    public Guid SessionKey { get; set; } = Guid.NewGuid();
    public string ContextJson { get; set; } = string.Empty;
    public int? DoctorId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
