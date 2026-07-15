namespace Docovee.DS.Models;

public class DoctorInsuranceRowDto
{
    public int CarrierId { get; set; }
    public string CarrierName { get; set; } = string.Empty;
    public string AcceptedPrograms { get; set; } = string.Empty;
    public string AcceptedPlanTypes { get; set; } = string.Empty;
    public IReadOnlyList<string> Programs { get; set; } = Array.Empty<string>();
    public IReadOnlyList<string> PlanTypes { get; set; } = Array.Empty<string>();
    public IReadOnlyList<string> PlanNames { get; set; } = Array.Empty<string>();
}

public class AddDoctorInsurancesInput
{
    public List<int> CarrierIds { get; set; } = new();
}

public class SelectableInsuranceCarrierDto
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Code { get; set; } = string.Empty;
    public bool Selected { get; set; }
}

public class SelectInsuranceInput
{
    public List<int> CarrierIds { get; set; } = new();
}
