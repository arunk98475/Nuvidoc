namespace Docovee.DS.Models;

public class PatientInsuranceRowDto
{
    public string Type { get; set; } = string.Empty;
    public int? InsuranceCarrierId { get; set; }
    public string? InsuranceCarrierName { get; set; }
    public int? InsurancePlanId { get; set; }
    public string? InsurancePlanName { get; set; }
    public string? CustomCarrierName { get; set; }
    public string? CustomPlanName { get; set; }
}

public class PatientInsuranceProfileDto
{
    public IReadOnlyList<PatientInsuranceRowDto> Coverages { get; set; } = Array.Empty<PatientInsuranceRowDto>();
}

public class PatientInsuranceSaveModel
{
    public int? DentalCarrierId { get; set; }
    public int? DentalPlanId { get; set; }
}

public class PatientPrivacySettingsDto
{
    public bool? HipaaDataSharingOptIn { get; set; }
    public bool CookieTrackingOptOut { get; set; }
}

public class PatientPermissionsSettingsDto
{
    public bool AutofillEnabled { get; set; }
}
