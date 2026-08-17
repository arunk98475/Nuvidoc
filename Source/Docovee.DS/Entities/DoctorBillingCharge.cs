namespace Docovee.DS.Entities;

public class DoctorBillingCharge
{
    public int Id { get; set; }
    public int DoctorId { get; set; }
    public Doctor Doctor { get; set; } = null!;
    public int AppointmentId { get; set; }
    public Appointment Appointment { get; set; } = null!;
    public int AmountCents { get; set; }
    public string Currency { get; set; } = "usd";
    public string Status { get; set; } = BillingChargeStatuses.Pending;
    public string? StripePaymentIntentId { get; set; }
    public string? FailureMessage { get; set; }
    public DateTime? ChargedAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public static class BillingChargeStatuses
{
    public const string Pending = "pending";
    public const string Succeeded = "succeeded";
    public const string Failed = "failed";
    public const string Refunded = "refunded";
    public const string Skipped = "skipped";
}
