namespace Docovee.DS.Entities;

public class DoctorInsurance
{
    public int DoctorId { get; set; }
    public Doctor Doctor { get; set; } = null!;

    public int InsuranceCarrierId { get; set; }
    public InsuranceCarrier InsuranceCarrier { get; set; } = null!;
}
