namespace Docovee.DS.Entities;

public class DoctorSponsorshipCharge
{
    public int Id { get; set; }
    public int DoctorId { get; set; }
    public Doctor Doctor { get; set; } = null!;
    /// <summary>Set when billing interval is per booking.</summary>
    public int? AppointmentId { get; set; }
    public int AmountCents { get; set; }
    public string Currency { get; set; } = "usd";
    /// <summary>Snapshot of <see cref="Docovee.DS.Enums.SponsorshipBillingInterval"/> at charge time.</summary>
    public string BillingInterval { get; set; } = string.Empty;
    public string Status { get; set; } = BillingChargeStatuses.Pending;
    public string? StripePaymentIntentId { get; set; }
    public string? FailureMessage { get; set; }
    public DateTime? ChargedAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
