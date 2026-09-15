namespace Docovee.DS.Entities;

/// <summary>
/// Audit row for SMS/email lead handoff from Nuvi chat to a practice + patient.
/// </summary>
public class LeadHandoff
{
    public int Id { get; set; }
    public int? SearchSessionId { get; set; }
    public SearchSession? SearchSession { get; set; }
    public int? PatientId { get; set; }
    public Patient? Patient { get; set; }
    public int DoctorId { get; set; }
    public Doctor Doctor { get; set; } = null!;
    public int? AppointmentId { get; set; }
    public Appointment? Appointment { get; set; }
    public bool OfficeSmsSent { get; set; }
    public bool OfficeEmailSent { get; set; }
    public bool PatientSmsSent { get; set; }
    public bool PatientEmailSent { get; set; }
    public string? ErrorNotes { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
