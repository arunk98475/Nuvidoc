using Docovee.BLL.Configuration;
using Docovee.BLL.Services.Billing;
using Docovee.DS;
using Docovee.DS.Entities;
using Docovee.DS.Enums;
using Docovee.DS.Models;
using Docovee.logging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Stripe;

namespace Docovee.BLL.Services;

public enum SponsorshipBillingChargeTrigger
{
    Booking,
    Recurring
}

public interface ISponsorshipBillingService
{
    Task<SponsorshipBillingSettings> GetSettingsAsync(CancellationToken cancellationToken = default);
    Task<VisitChargeResultDto> TryChargeAsync(
        int doctorId,
        SponsorshipBillingChargeTrigger trigger,
        int? appointmentId = null,
        CancellationToken cancellationToken = default);
    Task<int> ProcessDueRecurringChargesAsync(CancellationToken cancellationToken = default);
    bool IsDueForRecurringCharge(DateTime? lastChargedAtUtc, SponsorshipBillingSettings settings, DateTime utcNow);
}

public sealed class SponsorshipBillingService : ISponsorshipBillingService
{
    private readonly DocoveeDbContext _db;
    private readonly IAppSettingsService _appSettings;
    private readonly IStripeCustomerService _customers;
    private readonly IStripePaymentMethodService _paymentMethods;
    private readonly StripeOptions _options;
    private readonly IDocoveeLogger _logger;

    public SponsorshipBillingService(
        DocoveeDbContext db,
        IAppSettingsService appSettings,
        IStripeCustomerService customers,
        IStripePaymentMethodService paymentMethods,
        IOptions<StripeOptions> options,
        IDocoveeLogger logger)
    {
        _db = db;
        _appSettings = appSettings;
        _customers = customers;
        _paymentMethods = paymentMethods;
        _options = options.Value;
        _logger = logger;
    }

    public Task<SponsorshipBillingSettings> GetSettingsAsync(CancellationToken cancellationToken = default) =>
        _appSettings.GetSponsorshipBillingSettingsAsync(cancellationToken);

    public async Task<VisitChargeResultDto> TryChargeAsync(
        int doctorId,
        SponsorshipBillingChargeTrigger trigger,
        int? appointmentId = null,
        CancellationToken cancellationToken = default)
    {
        var settings = await GetSettingsAsync(cancellationToken);
        if (settings.AmountCents <= 0)
        {
            return Skipped("Sponsorship billing amount is not configured.");
        }

        var doctor = await _db.Doctors.AsNoTracking()
            .FirstOrDefaultAsync(d => d.Id == doctorId, cancellationToken);
        if (doctor == null)
            return Fail("Doctor not found.");
        if (!doctor.IsSponsored)
            return Skipped("Doctor is not sponsored.");

        if (trigger == SponsorshipBillingChargeTrigger.Booking)
        {
            if (settings.Interval != SponsorshipBillingInterval.PerBooking)
                return Skipped("Sponsorship billing is not charged per booking.");
            if (appointmentId is null or <= 0)
                return Fail("Appointment id is required for per-booking sponsorship billing.");
        }
        else if (settings.Interval == SponsorshipBillingInterval.PerBooking)
        {
            return Skipped("Sponsorship billing is charged per booking, not on a schedule.");
        }

        if (trigger == SponsorshipBillingChargeTrigger.Recurring)
        {
            var lastChargedAt = await GetLastSuccessfulChargeAtAsync(doctorId, cancellationToken);
            if (!IsDueForRecurringCharge(lastChargedAt, settings, DateTime.UtcNow))
                return Skipped("Sponsorship billing is not due yet.");
        }

        if (!_options.IsConfigured)
        {
            await SaveChargeAsync(doctorId, appointmentId, settings, BillingChargeStatuses.Failed,
                null, "Stripe is not configured.", cancellationToken);
            return Fail("Stripe is not configured.");
        }

        StripeApi.Apply(Microsoft.Extensions.Options.Options.Create(_options));

        var (customerOk, customerMessage, customerId) = await _customers.GetOrCreateCustomerIdAsync(doctorId, cancellationToken);
        if (!customerOk || string.IsNullOrWhiteSpace(customerId))
        {
            await SaveChargeAsync(doctorId, appointmentId, settings, BillingChargeStatuses.Failed,
                null, customerMessage, cancellationToken);
            return Fail(customerMessage);
        }

        var methods = await _paymentMethods.ListPaymentMethodsAsync(doctorId, cancellationToken);
        var defaultMethod = methods.FirstOrDefault(m => m.IsDefault) ?? methods.FirstOrDefault();
        if (defaultMethod == null)
        {
            await SaveChargeAsync(doctorId, appointmentId, settings, BillingChargeStatuses.Failed,
                null, "No payment method on file.", cancellationToken);
            return Fail("Add a credit card under Settings → Billing before enabling sponsorship.");
        }

        var chargeRow = new DoctorSponsorshipCharge
        {
            DoctorId = doctorId,
            AppointmentId = appointmentId,
            AmountCents = settings.AmountCents,
            Currency = _options.Currency,
            BillingInterval = settings.Interval.ToString(),
            Status = BillingChargeStatuses.Pending,
            CreatedAt = DateTime.UtcNow
        };
        _db.DoctorSponsorshipCharges.Add(chargeRow);
        await _db.SaveChangesAsync(cancellationToken);

        try
        {
            var piService = new PaymentIntentService();
            var intent = await piService.CreateAsync(new PaymentIntentCreateOptions
            {
                Amount = settings.AmountCents,
                Currency = _options.Currency,
                Customer = customerId,
                PaymentMethod = defaultMethod.Id,
                Confirm = true,
                OffSession = true,
                Description = BuildStripeDescription(settings, appointmentId),
                Metadata = new Dictionary<string, string>
                {
                    ["doctor_id"] = doctorId.ToString(),
                    ["sponsorship_charge_id"] = chargeRow.Id.ToString(),
                    ["billing_interval"] = settings.Interval.ToString(),
                    ["appointment_id"] = appointmentId?.ToString() ?? string.Empty
                }
            }, cancellationToken: cancellationToken);

            await ApplyPaymentIntentResultAsync(chargeRow.Id, intent, cancellationToken);

            var updated = await _db.DoctorSponsorshipCharges.AsNoTracking()
                .FirstAsync(c => c.Id == chargeRow.Id, cancellationToken);

            var succeeded = updated.Status == BillingChargeStatuses.Succeeded;
            return new VisitChargeResultDto
            {
                Success = succeeded,
                Message = succeeded
                    ? "Sponsorship charge processed."
                    : updated.FailureMessage ?? "Payment failed.",
                ChargeStatus = updated.Status,
                AmountCents = updated.AmountCents
            };
        }
        catch (StripeException ex)
        {
            _logger.LogWarning(
                "Stripe sponsorship charge failed for doctor {DoctorId}: {Message}",
                doctorId,
                ex.StripeError?.Message ?? ex.Message);

            chargeRow = await _db.DoctorSponsorshipCharges.FirstAsync(c => c.Id == chargeRow.Id, cancellationToken);
            chargeRow.Status = BillingChargeStatuses.Failed;
            chargeRow.FailureMessage = ex.StripeError?.Message ?? ex.Message;
            await _db.SaveChangesAsync(cancellationToken);

            return new VisitChargeResultDto
            {
                Success = false,
                Message = chargeRow.FailureMessage ?? "Payment failed.",
                ChargeStatus = BillingChargeStatuses.Failed,
                AmountCents = chargeRow.AmountCents
            };
        }
    }

    public async Task<int> ProcessDueRecurringChargesAsync(CancellationToken cancellationToken = default)
    {
        var settings = await GetSettingsAsync(cancellationToken);
        if (settings.AmountCents <= 0 || settings.Interval == SponsorshipBillingInterval.PerBooking)
            return 0;

        var sponsoredDoctorIds = await _db.Doctors.AsNoTracking()
            .Where(d => d.IsActive && d.IsSponsored)
            .Select(d => d.Id)
            .ToListAsync(cancellationToken);

        var charged = 0;
        foreach (var doctorId in sponsoredDoctorIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var lastChargedAt = await GetLastSuccessfulChargeAtAsync(doctorId, cancellationToken);
            if (!IsDueForRecurringCharge(lastChargedAt, settings, DateTime.UtcNow))
                continue;

            var result = await TryChargeAsync(doctorId, SponsorshipBillingChargeTrigger.Recurring, cancellationToken: cancellationToken);
            if (result.Success)
                charged++;
        }

        return charged;
    }

    public bool IsDueForRecurringCharge(DateTime? lastChargedAtUtc, SponsorshipBillingSettings settings, DateTime utcNow)
    {
        if (settings.AmountCents <= 0)
            return false;
        if (settings.Interval == SponsorshipBillingInterval.PerBooking)
            return false;
        if (!lastChargedAtUtc.HasValue)
            return true;

        var elapsed = utcNow - lastChargedAtUtc.Value;
        var dueAfter = settings.Interval switch
        {
            SponsorshipBillingInterval.Daily => TimeSpan.FromDays(1),
            SponsorshipBillingInterval.Weekly => TimeSpan.FromDays(7),
            SponsorshipBillingInterval.Monthly => TimeSpan.FromDays(30),
            SponsorshipBillingInterval.CustomDays => TimeSpan.FromDays(Math.Clamp(settings.CustomDays, 1, 365)),
            _ => TimeSpan.FromDays(30)
        };

        return elapsed >= dueAfter;
    }

    private async Task<DateTime?> GetLastSuccessfulChargeAtAsync(int doctorId, CancellationToken cancellationToken) =>
        await _db.DoctorSponsorshipCharges.AsNoTracking()
            .Where(c => c.DoctorId == doctorId && c.Status == BillingChargeStatuses.Succeeded)
            .OrderByDescending(c => c.ChargedAt ?? c.CreatedAt)
            .Select(c => (DateTime?)(c.ChargedAt ?? c.CreatedAt))
            .FirstOrDefaultAsync(cancellationToken);

    private async Task SaveChargeAsync(
        int doctorId,
        int? appointmentId,
        SponsorshipBillingSettings settings,
        string status,
        string? paymentIntentId,
        string? failureMessage,
        CancellationToken cancellationToken)
    {
        _db.DoctorSponsorshipCharges.Add(new DoctorSponsorshipCharge
        {
            DoctorId = doctorId,
            AppointmentId = appointmentId,
            AmountCents = settings.AmountCents,
            Currency = _options.Currency,
            BillingInterval = settings.Interval.ToString(),
            Status = status,
            StripePaymentIntentId = paymentIntentId,
            FailureMessage = failureMessage,
            ChargedAt = status == BillingChargeStatuses.Succeeded ? DateTime.UtcNow : null,
            CreatedAt = DateTime.UtcNow
        });
        await _db.SaveChangesAsync(cancellationToken);
    }

    private async Task ApplyPaymentIntentResultAsync(
        int chargeId,
        PaymentIntent intent,
        CancellationToken cancellationToken)
    {
        var charge = await _db.DoctorSponsorshipCharges.FirstAsync(c => c.Id == chargeId, cancellationToken);
        charge.StripePaymentIntentId = intent.Id;
        charge.Status = intent.Status switch
        {
            "succeeded" => BillingChargeStatuses.Succeeded,
            "processing" => BillingChargeStatuses.Pending,
            _ => BillingChargeStatuses.Failed
        };
        charge.FailureMessage = intent.LastPaymentError?.Message;
        charge.ChargedAt = charge.Status == BillingChargeStatuses.Succeeded ? DateTime.UtcNow : null;
        await _db.SaveChangesAsync(cancellationToken);
    }

    private static string BuildStripeDescription(SponsorshipBillingSettings settings, int? appointmentId) =>
        settings.Interval == SponsorshipBillingInterval.PerBooking
            ? $"NuviDoc sponsorship — booking #{appointmentId}"
            : $"NuviDoc sponsorship — {settings.IntervalLabel}";

    private static VisitChargeResultDto Fail(string message) =>
        new() { Success = false, Message = message, ChargeStatus = BillingChargeStatuses.Failed };

    private static VisitChargeResultDto Skipped(string message) =>
        new() { Success = true, Message = message, ChargeStatus = BillingChargeStatuses.Skipped, AmountCents = 0 };
}
