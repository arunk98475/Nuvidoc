namespace Docovee.DS.Entities;

public class PatientDoctorContactView
{
    public int Id { get; set; }
    public int PatientId { get; set; }
    public Patient Patient { get; set; } = null!;
    public int DoctorId { get; set; }
    public Doctor Doctor { get; set; } = null!;
    public int? SearchSessionId { get; set; }
    public SearchSession? SearchSession { get; set; }
    public DateTime ViewedAt { get; set; } = DateTime.UtcNow;
}
