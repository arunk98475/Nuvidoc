using Docovee.BLL.Configuration;
using Docovee.DS;
using Docovee.DS.Entities;
using Docovee.logging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Docovee.BLL.Services.Billing;

public interface IDoctorCallingEligibilityService
{
    /// <summary>
    /// True when the doctor may appear in Nuvi matching / office-calling lists.
    /// After free visits are used, a valid Stripe payment method is required (when a per-visit fee applies).
    /// </summary>
    Task<bool> IsEligibleForCallingAsync(int doctorId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<int>> FilterEligibleDoctorIdsAsync(
        IEnumerable<int> doctorIds,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends a one-time email when the doctor is blocked for missing a payment method after free visits.
    /// </summary>
    Task NotifyIfPaymentMethodRequiredAsync(int doctorId, CancellationToken cancellationToken = default);

    /// <summary>Clears the one-time block email flag once a card is on file.</summary>
    Task ClearBlockNotificationIfEligibleAsync(int doctorId, CancellationToken cancellationToken = default);
}

public sealed class DoctorCallingEligibilityService : IDoctorCallingEligibilityService
{
    private readonly DocoveeDbContext _db;
    private readonly IAppSettingsService _appSettings;
    private readonly IStripePaymentMethodService _paymentMethods;
    private readonly IEmailSender _email;
    private readonly EmailOptions _emailOptions;
    private readonly SiteOptions _site;
    private readonly IHostEnvironment _environment;
    private readonly IDocoveeLogger _logger;

    public DoctorCallingEligibilityService(
        DocoveeDbContext db,
        IAppSettingsService appSettings,
        IStripePaymentMethodService paymentMethods,
        IEmailSender email,
        IOptions<EmailOptions> emailOptions,
        IOptions<SiteOptions> site,
        IHostEnvironment environment,
        IDocoveeLogger logger)
    {
        _db = db;
        _appSettings = appSettings;
        _paymentMethods = paymentMethods;
        _email = email;
        _emailOptions = emailOptions.Value;
        _site = site.Value;
        _environment = environment;
        _logger = logger;
    }

    public async Task<bool> IsEligibleForCallingAsync(int doctorId, CancellationToken cancellationToken = default)
    {
        // Enforce in Production only; Development shows all doctors for easier local testing.
        if (!_environment.IsProduction())
            return true;

        var status = await EvaluateAsync(doctorId, cancellationToken);
        return status.IsEligible;
    }

    public async Task<IReadOnlyList<int>> FilterEligibleDoctorIdsAsync(
        IEnumerable<int> doctorIds,
        CancellationToken cancellationToken = default)
    {
        var ids = doctorIds.Distinct().ToList();
        if (ids.Count == 0)
            return ids;

        if (!_environment.IsProduction())
            return ids;

        var freeAllowance = await _appSettings.GetFreeVisitCountAsync(cancellationToken);
        var eligible = new List<int>(ids.Count);
        foreach (var id in ids)
        {
            if (await IsEligibleForCallingAsync(id, cancellationToken))
                eligible.Add(id);
            else
            {
                _logger.LogInformation(
                    "Doctor {DoctorId} excluded from calling/matching — free visits allowance is {FreeAllowance} and no payment method on file.",
                    id, freeAllowance);
                await NotifyIfPaymentMethodRequiredAsync(id, cancellationToken);
            }
        }

        if (eligible.Count < ids.Count)
        {
            _logger.LogInformation(
                "Calling eligibility filter kept {EligibleCount} of {TotalCount} doctors (free visits allowance: {FreeAllowance}, environment: {Environment}).",
                eligible.Count, ids.Count, freeAllowance, _environment.EnvironmentName);
        }

        return eligible;
    }

    public async Task NotifyIfPaymentMethodRequiredAsync(int doctorId, CancellationToken cancellationToken = default)
    {
        if (!_environment.IsProduction())
            return;

        var status = await EvaluateAsync(doctorId, cancellationToken);
        if (status.IsEligible || !status.NeedsPaymentMethod)
            return;

        var doctor = await _db.Doctors.FirstOrDefaultAsync(d => d.Id == doctorId, cancellationToken);
        if (doctor == null)
            return;

        if (doctor.BillingCallBlockedNotifiedAtUtc.HasValue)
            return;

        var to = FirstNonEmpty(doctor.BillingEmail, doctor.Username);
        if (string.IsNullOrWhiteSpace(to) || !to.Contains('@'))
        {
            _logger.LogWarning(
                "Doctor {DoctorId} needs a payment method after free visits but has no billing email.",
                doctorId);
            doctor.BillingCallBlockedNotifiedAtUtc = DateTime.UtcNow;
            await _db.SaveChangesAsync(cancellationToken);
            return;
        }

        if (!_email.IsConfigured)
        {
            _logger.LogWarning(
                "Doctor {DoctorId} needs a payment method after free visits but email is not configured.",
                doctorId);
            return;
        }

        var site = string.IsNullOrWhiteSpace(_site.Name) ? "NuviDoc" : _site.Name.Trim();
        var baseUrl = (_emailOptions.PublicBaseUrl ?? "").Trim().TrimEnd('/');
        if (string.IsNullOrWhiteSpace(baseUrl))
            baseUrl = "https://nuvidoc.com";
        var billingUrl = $"{baseUrl}/Doctor/Settings/billing";
        var freeCount = status.FreeVisitAllowance;
        var name = string.IsNullOrWhiteSpace(doctor.Name) ? "there" : doctor.Name.Trim().Split(' ')[0];

        var reason = freeCount <= 0
            ? "A payment method is required before Nuvi can match or call your practice for new patient bookings."
            : $"You've used your {freeCount} free {site} visit{(freeCount == 1 ? "" : "s")}. " +
              "To keep appearing in patient matching and office-calling, please add a valid payment method.";

        var subject = $"{site}: Add a payment method to keep receiving patient calls";
        var text =
            $"Hi {name},\n\n" +
            $"{reason}\n\n" +
            $"{billingUrl}\n\n" +
            "Until a card is on file, Nuvi will pause calling your practice for new patient bookings.\n\n" +
            $"— {site}\n";
        var html =
            $"<p>Hi {Escape(name)},</p>" +
            $"<p>{Escape(reason)}</p>" +
            $"<p><a href=\"{Escape(billingUrl)}\">Add payment method</a></p>" +
            "<p>Until a card is on file, Nuvi will pause calling your practice for new patient bookings.</p>" +
            $"<p>— {Escape(site)}</p>";

        var send = await _email.SendAsync(to, subject, text, html, cancellationToken);
        if (send.Success)
        {
            doctor.BillingCallBlockedNotifiedAtUtc = DateTime.UtcNow;
            await _db.SaveChangesAsync(cancellationToken);
            _logger.LogInformation(
                "Sent payment-method required email to doctor {DoctorId} ({Email}).",
                doctorId, to);
        }
        else
        {
            _logger.LogWarning(
                "Failed to email doctor {DoctorId} about payment method: {Message}",
                doctorId, send.Message);
        }
    }

    public async Task ClearBlockNotificationIfEligibleAsync(int doctorId, CancellationToken cancellationToken = default)
    {
        var doctor = await _db.Doctors.FirstOrDefaultAsync(d => d.Id == doctorId, cancellationToken);
        if (doctor?.BillingCallBlockedNotifiedAtUtc == null)
            return;

        var status = await EvaluateAsync(doctorId, cancellationToken);
        if (!status.IsEligible)
            return;

        doctor.BillingCallBlockedNotifiedAtUtc = null;
        await _db.SaveChangesAsync(cancellationToken);
    }

    private async Task<EligibilityStatus> EvaluateAsync(int doctorId, CancellationToken cancellationToken)
    {
        var doctor = await _db.Doctors.AsNoTracking()
            .Where(d => d.Id == doctorId)
            .Select(d => new { d.Id, d.OverridePerVisitFee, d.PerVisitFeeCents, d.IsActive })
            .FirstOrDefaultAsync(cancellationToken);
        if (doctor == null || !doctor.IsActive)
            return new EligibilityStatus(false, false, 0, 0);

        var defaultFee = await _appSettings.GetDefaultPerVisitFeeCentsAsync(cancellationToken);
        var feeCents = doctor.OverridePerVisitFee
            ? Math.Max(0, doctor.PerVisitFeeCents)
            : Math.Max(0, defaultFee);

        var freeAllowance = await _appSettings.GetFreeVisitCountAsync(cancellationToken);
        var used = await CountBillableVisitsAsync(doctorId, cancellationToken);

        // Still within the configured free-visit allowance.
        if (freeAllowance > 0 && used < freeAllowance)
            return new EligibilityStatus(true, false, freeAllowance, used);

        // No per-visit fee and free visits were configured — nothing to bill after the free tier.
        if (feeCents <= 0 && freeAllowance > 0)
            return new EligibilityStatus(true, false, freeAllowance, used);

        // Free visits exhausted (including freeAllowance == 0) — require a card on file.
        var methods = await _paymentMethods.ListPaymentMethodsAsync(doctorId, cancellationToken);
        var hasCard = methods.Count > 0;
        return new EligibilityStatus(hasCard, !hasCard, freeAllowance, used);
    }

    private async Task<int> CountBillableVisitsAsync(int doctorId, CancellationToken cancellationToken)
    {
        var chargeOnlyIfPatientShowed = await _appSettings.GetVisitBillingChargeOnlyIfPatientShowedAsync(cancellationToken);
        if (chargeOnlyIfPatientShowed)
        {
            return await _db.Appointments.AsNoTracking()
                .CountAsync(a =>
                    a.DoctorId == doctorId
                    && a.Source != AppointmentSources.PmsInbound
                    && a.Status == AppointmentStatuses.Completed,
                    cancellationToken);
        }

        return await _db.Appointments.AsNoTracking()
            .CountAsync(a =>
                a.DoctorId == doctorId
                && a.Source != AppointmentSources.PmsInbound,
                cancellationToken);
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        foreach (var v in values)
        {
            if (!string.IsNullOrWhiteSpace(v))
                return v.Trim();
        }

        return string.Empty;
    }

    private static string Escape(string value) =>
        System.Net.WebUtility.HtmlEncode(value);

    private readonly record struct EligibilityStatus(
        bool IsEligible,
        bool NeedsPaymentMethod,
        int FreeVisitAllowance,
        int VisitsUsed);
}
