namespace Docovee.DS.Entities;

public class PatientInsuranceCoverage
{
    public int Id { get; set; }
    public int PatientId { get; set; }
    public Patient Patient { get; set; } = null!;
    public string InsuranceType { get; set; } = string.Empty;
    public int? InsuranceCarrierId { get; set; }
    public InsuranceCarrier? InsuranceCarrier { get; set; }
    public int? InsurancePlanId { get; set; }
    public InsurancePlan? InsurancePlan { get; set; }
    public string? CustomCarrierName { get; set; }
    public string? CustomPlanName { get; set; }
    public string? MemberId { get; set; }
    public string? CardPhotoUrl { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public static class PatientInsuranceTypes
{
    public const string Medical = "Medical";
    public const string Dental = "Dental";
    public const string Vision = "Vision";
    public const string Secondary = "Secondary";

    public static readonly string[] All = [Medical, Dental, Vision, Secondary];
}
