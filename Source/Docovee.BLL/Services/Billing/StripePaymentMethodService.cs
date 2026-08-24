using System.Text.Json;
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

        var list = methods.Data
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

        if (list.Count > 0 && doctor.BillingCallBlockedNotifiedAtUtc != null)
        {
            var tracked = await _db.Doctors.FirstOrDefaultAsync(d => d.Id == doctorId, cancellationToken);
            if (tracked?.BillingCallBlockedNotifiedAtUtc != null)
            {
                tracked.BillingCallBlockedNotifiedAtUtc = null;
                await _db.SaveChangesAsync(cancellationToken);
            }
        }

        return list;
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

        doctor.BillingCallBlockedNotifiedAtUtc = null;
        await _db.SaveChangesAsync(cancellationToken);

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
    Task<DoctorPerformanceOverviewDto> GetPerformanceOverviewAsync(int doctorId, CancellationToken cancellationToken = default);
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
        DateTime? from = null;
        DateTime? to = null;
        if (year is > 0)
        {
            from = new DateTime(year.Value, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            to = from.Value.AddYears(1);
        }

        var visitQuery = _db.DoctorBillingCharges.AsNoTracking()
            .Include(c => c.Appointment)
            .Where(c => c.DoctorId == doctorId);
        if (from.HasValue)
            visitQuery = visitQuery.Where(c => c.CreatedAt >= from && c.CreatedAt < to);

        var visitRows = await visitQuery
            .OrderByDescending(c => c.CreatedAt)
            .Take(200)
            .ToListAsync(cancellationToken);

        var sponsorshipQuery = _db.DoctorSponsorshipCharges.AsNoTracking()
            .Where(c => c.DoctorId == doctorId
                && c.Status != BillingChargeStatuses.Skipped);
        if (from.HasValue)
            sponsorshipQuery = sponsorshipQuery.Where(c => c.CreatedAt >= from && c.CreatedAt < to);

        var sponsorshipRows = await sponsorshipQuery
            .OrderByDescending(c => c.CreatedAt)
            .Take(200)
            .ToListAsync(cancellationToken);

        var appointmentIds = sponsorshipRows
            .Where(c => c.AppointmentId is > 0)
            .Select(c => c.AppointmentId!.Value)
            .Distinct()
            .ToList();

        var appointmentNames = appointmentIds.Count == 0
            ? new Dictionary<int, string>()
            : await _db.Appointments.AsNoTracking()
                .Where(a => appointmentIds.Contains(a.Id))
                .Select(a => new { a.Id, a.PatientName })
                .ToDictionaryAsync(a => a.Id, a => a.PatientName ?? "Patient", cancellationToken);

        var visitDtos = visitRows.Select(c => new DoctorBillingChargeDto
        {
            Id = c.Id,
            ChargeKind = "Visit",
            AppointmentId = c.AppointmentId,
            PatientName = c.Appointment?.PatientName ?? "Patient",
            AppointmentStartsAt = c.Appointment?.StartsAt ?? c.CreatedAt,
            AmountCents = c.AmountCents,
            Currency = c.Currency,
            Status = c.Status,
            FailureMessage = c.FailureMessage,
            ChargedAt = c.ChargedAt,
            CreatedAt = c.CreatedAt
        });

        var sponsorshipDtos = sponsorshipRows.Select(c =>
        {
            var patient = c.AppointmentId is > 0
                && appointmentNames.TryGetValue(c.AppointmentId.Value, out var name)
                ? name
                : null;
            return new DoctorBillingChargeDto
            {
                Id = c.Id,
                ChargeKind = "Sponsorship",
                AppointmentId = c.AppointmentId ?? 0,
                PatientName = string.IsNullOrWhiteSpace(patient)
                    ? "Sponsorship"
                    : $"Sponsorship — {patient}",
                AppointmentStartsAt = c.ChargedAt ?? c.CreatedAt,
                AmountCents = c.AmountCents,
                Currency = c.Currency,
                Status = c.Status,
                FailureMessage = c.FailureMessage,
                ChargedAt = c.ChargedAt,
                CreatedAt = c.CreatedAt
            };
        });

        return visitDtos
            .Concat(sponsorshipDtos)
            .OrderByDescending(c => c.CreatedAt)
            .Take(200)
            .ToList();
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

    public async Task<DoctorPerformanceOverviewDto> GetPerformanceOverviewAsync(
        int doctorId,
        CancellationToken cancellationToken = default)
    {
        var today = DateTime.Today;
        var periodStart = new DateTime(today.Year, today.Month, 1);
        var periodEnd = periodStart.AddMonths(1);
        var priorStart = periodStart.AddMonths(-1);
        // Same day-of-month window last month (month-to-date comparison).
        var priorEndExclusive = priorStart.AddDays((today.Date - periodStart.Date).Days + 1);
        if (priorEndExclusive > periodStart)
            priorEndExclusive = periodStart;
        var periodLabel =
            $"This month ({periodStart:MM/dd/yy} – {today:MM/dd/yy})";

        var appointments = await _db.Appointments.AsNoTracking()
            .Where(a => a.DoctorId == doctorId && a.Source != AppointmentSources.PmsInbound)
            .Select(a => new
            {
                a.Id,
                a.Source,
                a.Status,
                a.StartsAt,
                a.CreatedAt,
                a.UpdatedAt
            })
            .ToListAsync(cancellationToken);

        static bool IsBookingInRange(
            DateTime createdAt,
            DateTime startsAt,
            DateTime from,
            DateTime toExclusive)
        {
            // Prefer booking time; fall back to start if CreatedAt is unset/min.
            var bookedAt = createdAt > DateTime.MinValue.AddYears(1) ? createdAt : startsAt;
            return bookedAt >= from && bookedAt < toExclusive;
        }

        static bool NotCanceled(string status) => !AppointmentStatuses.IsCanceled(status);

        var periodBookings = appointments
            .Where(a => NotCanceled(a.Status) && IsBookingInRange(a.CreatedAt, a.StartsAt, periodStart, periodEnd))
            .ToList();
        var priorBookings = appointments
            .Where(a => NotCanceled(a.Status) && IsBookingInRange(a.CreatedAt, a.StartsAt, priorStart, priorEndExclusive))
            .ToList();

        var completed = appointments.Count(a =>
            string.Equals(a.Status, AppointmentStatuses.Completed, StringComparison.OrdinalIgnoreCase)
            && a.StartsAt >= periodStart && a.StartsAt < periodEnd);
        var completedPrior = appointments.Count(a =>
            string.Equals(a.Status, AppointmentStatuses.Completed, StringComparison.OrdinalIgnoreCase)
            && a.StartsAt >= priorStart && a.StartsAt < priorEndExclusive);

        var marketplace = periodBookings.Count(a =>
            !string.Equals(a.Source, AppointmentSources.NuviChat, StringComparison.OrdinalIgnoreCase));
        var nuviMatching = periodBookings.Count(a =>
            string.Equals(a.Source, AppointmentSources.NuviChat, StringComparison.OrdinalIgnoreCase));

        var visitSpend = await _db.DoctorBillingCharges.AsNoTracking()
            .Where(c => c.DoctorId == doctorId
                && c.Status == BillingChargeStatuses.Succeeded
                && ((c.ChargedAt ?? c.CreatedAt) >= periodStart)
                && ((c.ChargedAt ?? c.CreatedAt) < periodEnd))
            .SumAsync(c => (int?)c.AmountCents, cancellationToken) ?? 0;

        var sponsorshipSpend = await _db.DoctorSponsorshipCharges.AsNoTracking()
            .Where(c => c.DoctorId == doctorId
                && c.Status == BillingChargeStatuses.Succeeded
                && ((c.ChargedAt ?? c.CreatedAt) >= periodStart)
                && ((c.ChargedAt ?? c.CreatedAt) < periodEnd))
            .SumAsync(c => (int?)c.AmountCents, cancellationToken) ?? 0;

        var spendCents = visitSpend + sponsorshipSpend;
        var bookingCount = periodBookings.Count;
        var avgCents = bookingCount > 0
            ? (int)Math.Round(spendCents / (decimal)bookingCount, MidpointRounding.AwayFromZero)
            : 0;

        var daysInSeries = Math.Max(1, (today.Date - periodStart.Date).Days + 1);
        var daily = new List<DoctorPerformanceDayPointDto>(daysInSeries);
        for (var i = 0; i < daysInSeries; i++)
        {
            var day = DateOnly.FromDateTime(periodStart.AddDays(i));
            var dayStart = day.ToDateTime(TimeOnly.MinValue);
            var dayEnd = dayStart.AddDays(1);
            var dayRows = periodBookings
                .Where(a => IsBookingInRange(a.CreatedAt, a.StartsAt, dayStart, dayEnd))
                .ToList();
            daily.Add(new DoctorPerformanceDayPointDto
            {
                Date = day,
                MarketplaceCount = dayRows.Count(a =>
                    !string.Equals(a.Source, AppointmentSources.NuviChat, StringComparison.OrdinalIgnoreCase)),
                NuviMatchingCount = dayRows.Count(a =>
                    string.Equals(a.Source, AppointmentSources.NuviChat, StringComparison.OrdinalIgnoreCase))
            });
        }

        return new DoctorPerformanceOverviewDto
        {
            PeriodStart = periodStart,
            PeriodEndExclusive = periodEnd,
            PeriodLabel = periodLabel,
            Bookings = bookingCount,
            BookingsPriorPeriod = priorBookings.Count,
            CompletedAppointments = completed,
            CompletedPriorPeriod = completedPrior,
            SpendCents = spendCents,
            AverageCostPerBookingCents = avgCents,
            MarketplaceBookings = marketplace,
            NuviMatchingBookings = nuviMatching,
            NewPatientBookings = bookingCount,
            DailySeries = daily
        };
    }
}
