namespace Docovee.DS.Entities;

public class InsurancePlan
{
    public int Id { get; set; }
    public int InsuranceCarrierId { get; set; }
    public InsuranceCarrier InsuranceCarrier { get; set; } = null!;
    public string Name { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
    public int SortOrder { get; set; }
}
