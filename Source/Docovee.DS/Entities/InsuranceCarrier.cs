namespace Docovee.DS.Entities;

public class InsuranceCarrier
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Code { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;

    public ICollection<DoctorInsurance> DoctorInsurances { get; set; } = new List<DoctorInsurance>();
}
