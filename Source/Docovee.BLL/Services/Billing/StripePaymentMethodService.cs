using System.Text.Json;
using Docovee.BLL.Configuration;
using Docovee.BLL.Services;
using Docovee.DS;
using Docovee.DS.Models;
using Docovee.logging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Stripe;

namespace Docovee.BLL.Services.Billing;

public interface IStripePaymentMethodService
{
    Task<SetupIntentResultDto> CreateSetupIntentAsync(int doctorId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DoctorPaymentMethodDto>> ListPaymentMethodsAsync(int doctorId, CancellationToken cancellationToken = default);
    Task<BillingOperationResultDto> SetDefaultPaymentMethodAsync(int doctorId, string paymentMethodId, CancellationToken cancellationToken = default);
    Task<BillingOperationResultDto> DetachPaymentMethodAsync(int doctorId, string paymentMethodId, CancellationToken cancellationToken = default);
}

public sealed class StripePaymentMethodService : IStripePaymentMethodService
{
    private readonly DocoveeDbContext _db;
    private readonly IStripeCustomerService _customers;
    private readonly StripeOptions _options;
    private readonly IDocoveeLogger _logger;

    public StripePaymentMethodService(
        DocoveeDbContext db,
        IStripeCustomerService customers,
        IOptions<StripeOptions> options,
        IDocoveeLogger logger)
    {
        _db = db;
        _customers = customers;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<SetupIntentResultDto> CreateSetupIntentAsync(int doctorId, CancellationToken cancellationToken = default)
    {
        if (!_options.IsConfigured)
            return FailSetup("Stripe is not configured.");

        StripeApi.Apply(Microsoft.Extensions.Options.Options.Create(_options));

        var (ok, message, customerId) = await _customers.GetOrCreateCustomerIdAsync(doctorId, cancellationToken);
        if (!ok || string.IsNullOrWhiteSpace(customerId))
            return FailSetup(message);

        var setupService = new SetupIntentService();
        var setup = await setupService.CreateAsync(new SetupIntentCreateOptions
        {
            Customer = customerId,
            PaymentMethodTypes = ["card"],
            Usage = "off_session",
            Metadata = new Dictionary<string, string>
            {
                ["doctor_id"] = doctorId.ToString()
            }
        }, cancellationToken: cancellationToken);

        return new SetupIntentResultDto
        {
            Success = true,
            Message = "OK",
            ClientSecret = setup.ClientSecret
        };
    }

    public async Task<IReadOnlyList<DoctorPaymentMethodDto>> ListPaymentMethodsAsync(
        int doctorId,
        CancellationToken cancellationToken = default)
    {
        if (!_options.IsConfigured)
            return Array.Empty<DoctorPaymentMethodDto>();

        StripeApi.Apply(Microsoft.Extensions.Options.Options.Create(_options));

        var doctor = await _db.Doctors.AsNoTracking()
            .FirstOrDefaultAsync(d => d.Id == doctorId, cancellationToken);
        if (doctor == null || string.IsNullOrWhiteSpace(doctor.StripeCustomerId))
            return Array.Empty<DoctorPaymentMethodDto>();

        var customerService = new CustomerService();
        var customer = await customerService.GetAsync(doctor.StripeCustomerId, cancellationToken: cancellationToken);
        var defaultId = customer.InvoiceSettings?.DefaultPaymentMethodId;

        var pmService = new PaymentMethodService();
        var methods = await pmService.ListAsync(new PaymentMethodListOptions
        {
            Customer = doctor.StripeCustomerId,
            Type = "card"
        }, cancellationToken: cancellationToken);

        return methods.Data
            .Where(m => m.Card != null)
            .Select(m => new DoctorPaymentMethodDto
            {
                Id = m.Id,
                Brand = m.Card!.Brand ?? "card",
                Last4 = m.Card.Last4 ?? "????",
                ExpMonth = (int)(m.Card.ExpMonth),
                ExpYear = (int)(m.Card.ExpYear),
                IsDefault = string.Equals(m.Id, defaultId, StringComparison.Ordinal)
            })
            .OrderByDescending(m => m.IsDefault)
            .ThenBy(m => m.Brand)
            .ToList();
    }

    public async Task<BillingOperationResultDto> SetDefaultPaymentMethodAsync(
        int doctorId,
        string paymentMethodId,
        CancellationToken cancellationToken = default)
    {
        if (!_options.IsConfigured)
            return FailOp("Stripe is not configured.");
        if (string.IsNullOrWhiteSpace(paymentMethodId))
            return FailOp("Payment method id is required.");

        StripeApi.Apply(Microsoft.Extensions.Options.Options.Create(_options));

        var doctor = await _db.Doctors.FirstOrDefaultAsync(d => d.Id == doctorId, cancellationToken);
        if (doctor == null || string.IsNullOrWhiteSpace(doctor.StripeCustomerId))
            return FailOp("Add a card before setting a default.");

        var pmService = new PaymentMethodService();
        var pm = await pmService.GetAsync(paymentMethodId.Trim(), cancellationToken: cancellationToken);
        if (!string.Equals(pm.CustomerId, doctor.StripeCustomerId, StringComparison.Ordinal))
            return FailOp("Payment method not found.");

        var customerService = new CustomerService();
        await customerService.UpdateAsync(doctor.StripeCustomerId, new CustomerUpdateOptions
        {
            InvoiceSettings = new CustomerInvoiceSettingsOptions
            {
                DefaultPaymentMethod = paymentMethodId.Trim()
            }
        }, cancellationToken: cancellationToken);

        return OkOp("Default payment method updated.");
    }

    public async Task<BillingOperationResultDto> DetachPaymentMethodAsync(
        int doctorId,
        string paymentMethodId,
        CancellationToken cancellationToken = default)
    {
        if (!_options.IsConfigured)
            return FailOp("Stripe is not configured.");
        if (string.IsNullOrWhiteSpace(paymentMethodId))
            return FailOp("Payment method id is required.");

        StripeApi.Apply(Microsoft.Extensions.Options.Options.Create(_options));

        var doctor = await _db.Doctors.AsNoTracking()
            .FirstOrDefaultAsync(d => d.Id == doctorId, cancellationToken);
        if (doctor == null || string.IsNullOrWhiteSpace(doctor.StripeCustomerId))
            return FailOp("No payment methods on file.");

        var pmService = new PaymentMethodService();
        var pm = await pmService.GetAsync(paymentMethodId.Trim(), cancellationToken: cancellationToken);
        if (!string.Equals(pm.CustomerId, doctor.StripeCustomerId, StringComparison.Ordinal))
            return FailOp("Payment method not found.");

        await pmService.DetachAsync(paymentMethodId.Trim(), cancellationToken: cancellationToken);
        _logger.LogInformation("Detached payment method {PmId} for doctor {DoctorId}", paymentMethodId, doctorId);
        return OkOp("Card removed.");
    }

    private static SetupIntentResultDto FailSetup(string message) =>
        new() { Success = false, Message = message };

    private static BillingOperationResultDto FailOp(string message) =>
        new() { Success = false, Message = message };

    private static BillingOperationResultDto OkOp(string message) =>
        new() { Success = true, Message = message };
}

public interface IDoctorBillingService
{
    Task<DoctorBillingContactDto> GetBillingContactAsync(int doctorId, CancellationToken cancellationToken = default);
    Task<BillingOperationResultDto> UpdateBillingContactAsync(int doctorId, DoctorBillingContactDto contact, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DoctorBillingChargeDto>> GetChargesAsync(int doctorId, int? year, CancellationToken cancellationToken = default);
    Task<int> GetPerVisitFeeCentsAsync(int doctorId, CancellationToken cancellationToken = default);
}

public sealed class DoctorBillingService : IDoctorBillingService
{
    private readonly DocoveeDbContext _db;
    private readonly StripeOptions _options;
    private readonly IAppSettingsService _appSettings;

    public DoctorBillingService(
        DocoveeDbContext db,
        IOptions<StripeOptions> options,
        IAppSettingsService appSettings)
    {
        _db = db;
        _options = options.Value;
        _appSettings = appSettings;
    }

    public async Task<DoctorBillingContactDto> GetBillingContactAsync(int doctorId, CancellationToken cancellationToken = default)
    {
        var doctor = await _db.Doctors.AsNoTracking()
            .FirstOrDefaultAsync(d => d.Id == doctorId, cancellationToken);
        if (doctor == null)
            return new DoctorBillingContactDto();

        var dto = new DoctorBillingContactDto
        {
            BillingEmail = doctor.BillingEmail ?? doctor.Username
        };

        if (!string.IsNullOrWhiteSpace(doctor.BillingAddressJson))
        {
            try
            {
                var parsed = JsonSerializer.Deserialize<DoctorBillingContactDto>(doctor.BillingAddressJson);
                if (parsed != null)
                {
                    dto.Line1 = parsed.Line1;
                    dto.Line2 = parsed.Line2;
                    dto.City = parsed.City;
                    dto.State = parsed.State;
                    dto.PostalCode = parsed.PostalCode;
                    dto.Country = parsed.Country;
                }
            }
            catch
            {
                // ignore malformed json
            }
        }

        if (string.IsNullOrWhiteSpace(dto.Country))
            dto.Country = "US";

        return dto;
    }

    public async Task<BillingOperationResultDto> UpdateBillingContactAsync(
        int doctorId,
        DoctorBillingContactDto contact,
        CancellationToken cancellationToken = default)
    {
        var doctor = await _db.Doctors.FirstOrDefaultAsync(d => d.Id == doctorId, cancellationToken);
        if (doctor == null)
            return new BillingOperationResultDto { Success = false, Message = "Doctor not found." };

        doctor.BillingEmail = string.IsNullOrWhiteSpace(contact.BillingEmail)
            ? null
            : contact.BillingEmail.Trim();

        var addressPayload = new DoctorBillingContactDto
        {
            Line1 = contact.Line1?.Trim(),
            Line2 = contact.Line2?.Trim(),
            City = contact.City?.Trim(),
            State = contact.State?.Trim(),
            PostalCode = contact.PostalCode?.Trim(),
            Country = string.IsNullOrWhiteSpace(contact.Country) ? "US" : contact.Country.Trim()
        };

        doctor.BillingAddressJson = JsonSerializer.Serialize(addressPayload);
        await _db.SaveChangesAsync(cancellationToken);

        if (_options.IsConfigured && !string.IsNullOrWhiteSpace(doctor.StripeCustomerId))
        {
            StripeApi.Apply(Microsoft.Extensions.Options.Options.Create(_options));
            var customerService = new CustomerService();
            await customerService.UpdateAsync(doctor.StripeCustomerId, new CustomerUpdateOptions
            {
                Email = doctor.BillingEmail,
                Address = new AddressOptions
                {
                    Line1 = addressPayload.Line1,
                    Line2 = addressPayload.Line2,
                    City = addressPayload.City,
                    State = addressPayload.State,
                    PostalCode = addressPayload.PostalCode,
                    Country = addressPayload.Country
                }
            }, cancellationToken: cancellationToken);
        }

        return new BillingOperationResultDto { Success = true, Message = "Billing contact saved." };
    }

    public async Task<IReadOnlyList<DoctorBillingChargeDto>> GetChargesAsync(
        int doctorId,
        int? year,
        CancellationToken cancellationToken = default)
    {
        var query = _db.DoctorBillingCharges.AsNoTracking()
            .Include(c => c.Appointment)
            .Where(c => c.DoctorId == doctorId);

        if (year is > 0)
        {
            var from = new DateTime(year.Value, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var to = from.AddYears(1);
            query = query.Where(c => c.CreatedAt >= from && c.CreatedAt < to);
        }

        var rows = await query
            .OrderByDescending(c => c.CreatedAt)
            .Take(200)
            .ToListAsync(cancellationToken);

        return rows.Select(c => new DoctorBillingChargeDto
        {
            Id = c.Id,
            AppointmentId = c.AppointmentId,
            PatientName = c.Appointment?.PatientName ?? "Patient",
            AppointmentStartsAt = c.Appointment?.StartsAt ?? c.CreatedAt,
            AmountCents = c.AmountCents,
            Currency = c.Currency,
            Status = c.Status,
            FailureMessage = c.FailureMessage,
            ChargedAt = c.ChargedAt,
            CreatedAt = c.CreatedAt
        }).ToList();
    }

    public async Task<int> GetPerVisitFeeCentsAsync(int doctorId, CancellationToken cancellationToken = default)
    {
        var doctor = await _db.Doctors.AsNoTracking()
            .Where(d => d.Id == doctorId)
            .Select(d => new { d.OverridePerVisitFee, d.PerVisitFeeCents })
            .FirstOrDefaultAsync(cancellationToken);
        if (doctor == null)
            return 0;
        if (doctor.OverridePerVisitFee)
            return Math.Max(0, doctor.PerVisitFeeCents);
        return Math.Max(0, await _appSettings.GetDefaultPerVisitFeeCentsAsync(cancellationToken));
    }
}
