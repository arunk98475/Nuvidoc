using Docovee.BLL.Configuration;
using Docovee.BLL.Services;
using Docovee.DS;
using Docovee.DS.Entities;
using Docovee.DS.Models;
using Docovee.logging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Stripe;

namespace Docovee.BLL.Services.Billing;

public interface IVisitBillingService
{
    Task<VisitChargeResultDto> ChargeForCompletedVisitAsync(
        int doctorId,
        int appointmentId,
        CancellationToken cancellationToken = default);

    Task UpdateChargeFromPaymentIntentAsync(
        string paymentIntentId,
        string status,
        string? failureMessage,
        CancellationToken cancellationToken = default);
}

public sealed class VisitBillingService : IVisitBillingService
{
    private readonly DocoveeDbContext _db;
    private readonly IStripeCustomerService _customers;
    private readonly IStripePaymentMethodService _paymentMethods;
    private readonly IAppSettingsService _appSettings;
    private readonly StripeOptions _options;
    private readonly IDocoveeLogger _logger;

    public VisitBillingService(
        DocoveeDbContext db,
        IStripeCustomerService customers,
        IStripePaymentMethodService paymentMethods,
        IAppSettingsService appSettings,
        IOptions<StripeOptions> options,
        IDocoveeLogger logger)
    {
        _db = db;
        _customers = customers;
        _paymentMethods = paymentMethods;
        _appSettings = appSettings;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<VisitChargeResultDto> ChargeForCompletedVisitAsync(
        int doctorId,
        int appointmentId,
        CancellationToken cancellationToken = default)
    {
        var existing = await _db.DoctorBillingCharges.AsNoTracking()
            .FirstOrDefaultAsync(c => c.AppointmentId == appointmentId, cancellationToken);
        if (existing != null)
        {
            return new VisitChargeResultDto
            {
                Success = existing.Status is BillingChargeStatuses.Succeeded or BillingChargeStatuses.Skipped,
                Message = "Charge already recorded for this visit.",
                ChargeStatus = existing.Status,
                AmountCents = existing.AmountCents
            };
        }

        var appointment = await _db.Appointments.AsNoTracking()
            .FirstOrDefaultAsync(a => a.Id == appointmentId && a.DoctorId == doctorId, cancellationToken);
        if (appointment == null)
            return Fail("Appointment not found.");

        var doctor = await _db.Doctors.AsNoTracking()
            .FirstOrDefaultAsync(d => d.Id == doctorId, cancellationToken);
        if (doctor == null)
            return Fail("Doctor not found.");

        if (AppointmentSources.IsPmsInbound(appointment.Source))
            return Fail("PMS appointments are not billed through NuviDoc.");

        if (!AppointmentSources.IsNuvidocBooking(appointment.Source))
            return Fail("This appointment is not billable.");

        var defaultFeeCents = await _appSettings.GetDefaultPerVisitFeeCentsAsync(cancellationToken);
        var amount = doctor.OverridePerVisitFee
            ? Math.Max(0, doctor.PerVisitFeeCents)
            : Math.Max(0, defaultFeeCents);

        var freeVisitAllowance = await _appSettings.GetFreeVisitCountAsync(cancellationToken);
        if (freeVisitAllowance > 0)
        {
            var priorCompleted = await _db.Appointments.AsNoTracking()
                .CountAsync(a =>
                    a.DoctorId == doctorId
                    && a.Id != appointmentId
                    && a.Source != AppointmentSources.PmsInbound
                    && a.Status == AppointmentStatuses.Completed,
                    cancellationToken);

            if (priorCompleted < freeVisitAllowance)
            {
                var used = priorCompleted + 1;
                await SaveChargeAsync(doctorId, appointmentId, 0, _options.Currency, BillingChargeStatuses.Skipped,
                    null, $"Free visit {used} of {freeVisitAllowance}.", cancellationToken);
                return new VisitChargeResultDto
                {
                    Success = true,
                    Message = $"Free visit {used} of {freeVisitAllowance}. No charge.",
                    ChargeStatus = BillingChargeStatuses.Skipped,
                    AmountCents = 0
                };
            }
        }

        if (amount == 0)
        {
            await SaveChargeAsync(doctorId, appointmentId, 0, _options.Currency, BillingChargeStatuses.Skipped, null, null, cancellationToken);
            return new VisitChargeResultDto
            {
                Success = true,
                Message = "Visit recorded. Per-visit fee is $0 for this doctor.",
                ChargeStatus = BillingChargeStatuses.Skipped,
                AmountCents = 0
            };
        }

        if (!_options.IsConfigured)
        {
            await SaveChargeAsync(doctorId, appointmentId, amount, _options.Currency, BillingChargeStatuses.Failed,
                null, "Stripe is not configured.", cancellationToken);
            return Fail("Stripe is not configured. Add payment settings before billing visits.");
        }

        StripeApi.Apply(Microsoft.Extensions.Options.Options.Create(_options));

        var (customerOk, customerMessage, customerId) = await _customers.GetOrCreateCustomerIdAsync(doctorId, cancellationToken);
        if (!customerOk || string.IsNullOrWhiteSpace(customerId))
        {
            await SaveChargeAsync(doctorId, appointmentId, amount, _options.Currency, BillingChargeStatuses.Failed,
                null, customerMessage, cancellationToken);
            return Fail(customerMessage);
        }

        var methods = await _paymentMethods.ListPaymentMethodsAsync(doctorId, cancellationToken);
        var defaultMethod = methods.FirstOrDefault(m => m.IsDefault) ?? methods.FirstOrDefault();
        if (defaultMethod == null)
        {
            await SaveChargeAsync(doctorId, appointmentId, amount, _options.Currency, BillingChargeStatuses.Failed,
                null, "No payment method on file.", cancellationToken);
            return Fail("Add a credit card under Settings → Billing before marking patients as showed.");
        }

        var chargeRow = new DoctorBillingCharge
        {
            DoctorId = doctorId,
            AppointmentId = appointmentId,
            AmountCents = amount,
            Currency = _options.Currency,
            Status = BillingChargeStatuses.Pending,
            CreatedAt = DateTime.UtcNow
        };
        _db.DoctorBillingCharges.Add(chargeRow);
        await _db.SaveChangesAsync(cancellationToken);

        try
        {
            var piService = new PaymentIntentService();
            var intent = await piService.CreateAsync(new PaymentIntentCreateOptions
            {
                Amount = amount,
                Currency = _options.Currency,
                Customer = customerId,
                PaymentMethod = defaultMethod.Id,
                Confirm = true,
                OffSession = true,
                Description = $"NuviDoc visit charge — appointment #{appointmentId}",
                Metadata = new Dictionary<string, string>
                {
                    ["doctor_id"] = doctorId.ToString(),
                    ["appointment_id"] = appointmentId.ToString(),
                    ["billing_charge_id"] = chargeRow.Id.ToString()
                }
            }, cancellationToken: cancellationToken);

            await ApplyPaymentIntentResultAsync(chargeRow.Id, intent, cancellationToken);

            var updated = await _db.DoctorBillingCharges.AsNoTracking()
                .FirstAsync(c => c.Id == chargeRow.Id, cancellationToken);

            var succeeded = updated.Status == BillingChargeStatuses.Succeeded;
            return new VisitChargeResultDto
            {
                Success = succeeded,
                Message = succeeded
                    ? "Visit charge processed."
                    : updated.FailureMessage ?? "Payment failed.",
                ChargeStatus = updated.Status,
                AmountCents = updated.AmountCents
            };
        }
        catch (StripeException ex)
        {
            _logger.LogWarning(
                "Stripe charge failed for appointment {AppointmentId}: {Message}",
                appointmentId,
                ex.StripeError?.Message ?? ex.Message);
            chargeRow = await _db.DoctorBillingCharges.FirstAsync(c => c.Id == chargeRow.Id, cancellationToken);
            chargeRow.Status = BillingChargeStatuses.Failed;
            chargeRow.FailureMessage = ex.StripeError?.Message ?? ex.Message;
            await _db.SaveChangesAsync(cancellationToken);

            return new VisitChargeResultDto
            {
                Success = false,
                Message = chargeRow.FailureMessage,
                ChargeStatus = BillingChargeStatuses.Failed,
                AmountCents = amount
            };
        }
    }

    public async Task UpdateChargeFromPaymentIntentAsync(
        string paymentIntentId,
        string status,
        string? failureMessage,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(paymentIntentId))
            return;

        var charge = await _db.DoctorBillingCharges
            .FirstOrDefaultAsync(c => c.StripePaymentIntentId == paymentIntentId, cancellationToken);
        if (charge == null)
            return;

        charge.Status = MapStripeStatus(status);
        charge.FailureMessage = charge.Status == BillingChargeStatuses.Failed ? failureMessage : null;
        if (charge.Status == BillingChargeStatuses.Succeeded)
            charge.ChargedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync(cancellationToken);
    }

    private async Task ApplyPaymentIntentResultAsync(
        int chargeRowId,
        PaymentIntent intent,
        CancellationToken cancellationToken)
    {
        var charge = await _db.DoctorBillingCharges.FirstAsync(c => c.Id == chargeRowId, cancellationToken);
        charge.StripePaymentIntentId = intent.Id;
        charge.Status = MapStripeStatus(intent.Status);
        charge.FailureMessage = charge.Status == BillingChargeStatuses.Failed
            ? intent.LastPaymentError?.Message
            : null;
        if (charge.Status == BillingChargeStatuses.Succeeded)
            charge.ChargedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);
    }

    private async Task SaveChargeAsync(
        int doctorId,
        int appointmentId,
        int amountCents,
        string currency,
        string status,
        string? paymentIntentId,
        string? failureMessage,
        CancellationToken cancellationToken)
    {
        _db.DoctorBillingCharges.Add(new DoctorBillingCharge
        {
            DoctorId = doctorId,
            AppointmentId = appointmentId,
            AmountCents = amountCents,
            Currency = currency,
            Status = status,
            StripePaymentIntentId = paymentIntentId,
            FailureMessage = failureMessage,
            ChargedAt = status == BillingChargeStatuses.Succeeded ? DateTime.UtcNow : null,
            CreatedAt = DateTime.UtcNow
        });
        await _db.SaveChangesAsync(cancellationToken);
    }

    private static string MapStripeStatus(string? stripeStatus) => stripeStatus switch
    {
        "succeeded" => BillingChargeStatuses.Succeeded,
        "processing" => BillingChargeStatuses.Pending,
        "requires_payment_method" or "requires_action" or "canceled" => BillingChargeStatuses.Failed,
        _ => BillingChargeStatuses.Pending
    };

    private static VisitChargeResultDto Fail(string message) =>
        new() { Success = false, Message = message };
}
