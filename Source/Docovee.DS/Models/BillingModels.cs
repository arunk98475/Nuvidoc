namespace Docovee.DS.Models;

public sealed class DoctorBillingContactDto
{
    public string? BillingEmail { get; set; }
    public string? Line1 { get; set; }
    public string? Line2 { get; set; }
    public string? City { get; set; }
    public string? State { get; set; }
    public string? PostalCode { get; set; }
    public string? Country { get; set; }
}

public sealed class DoctorPaymentMethodDto
{
    public string Id { get; set; } = string.Empty;
    public string Brand { get; set; } = string.Empty;
    public string Last4 { get; set; } = string.Empty;
    public int ExpMonth { get; set; }
    public int ExpYear { get; set; }
    public bool IsDefault { get; set; }
}

public sealed class DoctorBillingChargeDto
{
    public int Id { get; set; }
    public int AppointmentId { get; set; }
    public string PatientName { get; set; } = string.Empty;
    public DateTime AppointmentStartsAt { get; set; }
    public int AmountCents { get; set; }
    public string Currency { get; set; } = "usd";
    public string Status { get; set; } = string.Empty;
    public string? FailureMessage { get; set; }
    public DateTime? ChargedAt { get; set; }
    public DateTime CreatedAt { get; set; }
}

public sealed class SetupIntentResultDto
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public string? ClientSecret { get; set; }
}

public sealed class BillingOperationResultDto
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
}

public sealed class SetSponsorshipRequest
{
    public bool Enabled { get; set; }
}

public sealed class DoctorQualityComponentDto
{
    public string Name { get; set; } = string.Empty;
    public int WeightPercent { get; set; }
    public int Score { get; set; }
}

public sealed class DoctorQualityScoreResult
{
    public int Score { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public int MinRequired { get; set; }
    public bool IsSponsored { get; set; }
    public DateTime? SponsorshipEnabledAt { get; set; }
    public IReadOnlyList<DoctorQualityComponentDto> Components { get; set; } = Array.Empty<DoctorQualityComponentDto>();
    public IReadOnlyList<string> Tips { get; set; } = Array.Empty<string>();
}

public sealed class DoctorSponsorshipStatusDto
{
    public bool Enabled { get; set; }
    public bool CanEnable { get; set; }
    public int QualityScore { get; set; }
    public int MinRequired { get; set; }
    public bool Paused { get; set; }
    public string? PausedMessage { get; set; }
    public IReadOnlyList<DoctorQualityComponentDto> Components { get; set; } = Array.Empty<DoctorQualityComponentDto>();
    public IReadOnlyList<string> Tips { get; set; } = Array.Empty<string>();
}

public sealed class VisitChargeResultDto
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public string? ChargeStatus { get; set; }
    public int? AmountCents { get; set; }
}
