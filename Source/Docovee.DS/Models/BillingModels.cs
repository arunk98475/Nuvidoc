namespace Docovee.DS.Models;

using Docovee.DS.Enums;

public sealed class SponsorshipBillingSettings
{
    public int AmountCents { get; set; }
    public SponsorshipBillingInterval Interval { get; set; } = SponsorshipBillingInterval.Monthly;
    public int CustomDays { get; set; } = 30;
    /// <summary>Per-booking only: charge when the patient is marked as showed instead of at booking time.</summary>
    public bool ChargeOnlyIfPatientShowed { get; set; }

    public decimal AmountUsd => Math.Max(0, AmountCents) / 100m;

    public string IntervalLabel => Interval switch
    {
        SponsorshipBillingInterval.PerBooking when ChargeOnlyIfPatientShowed => "each booking when the patient is marked as showed",
        SponsorshipBillingInterval.PerBooking => "each booking",
        SponsorshipBillingInterval.Daily => "every day",
        SponsorshipBillingInterval.Weekly => "every week",
        SponsorshipBillingInterval.Monthly => "every month",
        SponsorshipBillingInterval.CustomDays when CustomDays == 1 => "every day",
        SponsorshipBillingInterval.CustomDays => $"every {CustomDays} days",
        _ => "periodically"
    };
}

public sealed class SponsorshipAdminSettings
{
    public int MinQualityScoreForSponsorship { get; set; } = 40;
    public int MinGoogleReviewCountForSponsorship { get; set; } = 5;
    public SponsorshipBillingSettings Billing { get; set; } = new();
}

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
    /// <summary>Visit or Sponsorship.</summary>
    public string ChargeKind { get; set; } = "Visit";
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
    public int GoogleReviewCount { get; set; }
    public int MinGoogleReviewsRequired { get; set; }
    public bool MeetsQualityRequirement { get; set; }
    public bool MeetsGoogleReviewRequirement { get; set; }
    public bool HasPaymentMethod { get; set; }
    public bool Paused { get; set; }
    public string? PausedMessage { get; set; }
    public int SponsorshipBillingAmountCents { get; set; }
    public SponsorshipBillingInterval SponsorshipBillingInterval { get; set; }
    public int SponsorshipBillingCustomDays { get; set; }
    public string? SponsorshipBillingSummary { get; set; }
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

public sealed class DoctorPerformanceDayPointDto
{
    public DateOnly Date { get; init; }
    public int MarketplaceCount { get; init; }
    public int NuviMatchingCount { get; init; }
}

public sealed class DoctorPerformanceOverviewDto
{
    public DateTime PeriodStart { get; init; }
    public DateTime PeriodEndExclusive { get; init; }
    public string PeriodLabel { get; init; } = string.Empty;
    public int Bookings { get; init; }
    public int BookingsPriorPeriod { get; init; }
    public int CompletedAppointments { get; init; }
    public int CompletedPriorPeriod { get; init; }
    public int SpendCents { get; init; }
    public int AverageCostPerBookingCents { get; init; }
    public int MarketplaceBookings { get; init; }
    public int NuviMatchingBookings { get; init; }
    public int NewPatientBookings { get; init; }
    public IReadOnlyList<DoctorPerformanceDayPointDto> DailySeries { get; init; }
        = Array.Empty<DoctorPerformanceDayPointDto>();
}
