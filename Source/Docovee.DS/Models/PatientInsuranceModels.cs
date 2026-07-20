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
    public string? MemberId { get; set; }
    public string? CardPhotoUrl { get; set; }
}

public class PatientInsuranceProfileDto
{
    public IReadOnlyList<PatientInsuranceRowDto> Coverages { get; set; } = Array.Empty<PatientInsuranceRowDto>();
    public string? IdCardPhotoUrl { get; set; }
}

public class PatientInsuranceSaveModel
{
    public int? MedicalCarrierId { get; set; }
    public int? MedicalPlanId { get; set; }
    public string? MedicalMemberId { get; set; }

    public int? DentalCarrierId { get; set; }
    public int? DentalPlanId { get; set; }
    public string? DentalMemberId { get; set; }

    public int? VisionCarrierId { get; set; }
    public int? VisionPlanId { get; set; }
    public string? VisionMemberId { get; set; }

    public string? SecondaryCarrierName { get; set; }
    public string? SecondaryPlanName { get; set; }
    public string? SecondaryMemberId { get; set; }
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
