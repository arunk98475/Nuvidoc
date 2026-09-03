namespace Docovee.DS.Entities;

public class DoctorPracticeFee
{
    public int Id { get; set; }
    public int DoctorId { get; set; }
    public Doctor Doctor { get; set; } = null!;

    public string ProcedureName { get; set; } = string.Empty;
    /// <summary>Patient-facing procedure fee in cents (e.g. 30000 = $300).</summary>
    public int ProcedureFeeCents { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
