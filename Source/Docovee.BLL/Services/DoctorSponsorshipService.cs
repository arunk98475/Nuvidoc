using Docovee.BLL.Services.Billing;
using Docovee.DS;
using Docovee.DS.Models;
using Docovee.logging;
using Microsoft.EntityFrameworkCore;

namespace Docovee.BLL.Services;

public interface IDoctorSponsorshipService
{
    Task<DoctorSponsorshipStatusDto?> GetStatusAsync(int doctorId, CancellationToken cancellationToken = default);
    Task<BillingOperationResultDto> SetEnabledAsync(int doctorId, bool enabled, CancellationToken cancellationToken = default);
}

/// <summary>
/// Sponsorship uses the admin-configured billing schedule; ranking within sponsored results still depends on quality.
/// </summary>
public sealed class DoctorSponsorshipService : IDoctorSponsorshipService
{
    private readonly DocoveeDbContext _db;
    private readonly IDoctorQualityScoreService _qualityScore;
    private readonly IAppSettingsService _appSettings;
    private readonly IStripePaymentMethodService _paymentMethods;
    private readonly IDocoveeLogger _logger;

    public DoctorSponsorshipService(
        DocoveeDbContext db,
        IDoctorQualityScoreService qualityScore,
        IAppSettingsService appSettings,
        IStripePaymentMethodService paymentMethods,
        IDocoveeLogger logger)
    {
        _db = db;
        _qualityScore = qualityScore;
        _appSettings = appSettings;
        _paymentMethods = paymentMethods;
        _logger = logger;
    }

    public async Task<DoctorSponsorshipStatusDto?> GetStatusAsync(
        int doctorId,
        CancellationToken cancellationToken = default)
    {
        var doctor = await _db.Doctors.AsNoTracking()
            .FirstOrDefaultAsync(d => d.Id == doctorId, cancellationToken);
        if (doctor == null)
            return null;

        var stale = !doctor.QualityScoreUpdatedAt.HasValue
            || doctor.QualityScoreUpdatedAt.Value < DateTime.UtcNow.AddHours(-24);
        var quality = stale
            ? await _qualityScore.RecomputeAndPersistAsync(doctorId, cancellationToken)
            : await _qualityScore.GetAsync(doctorId, cancellationToken);
        if (quality == null)
            return null;

        var admin = await _appSettings.GetSponsorshipAdminSettingsAsync(cancellationToken);
        var hasPaymentMethod = await HasActivePaymentMethodAsync(doctorId, cancellationToken);
        var meetsQuality = quality.Score >= admin.MinQualityScoreForSponsorship;
        var meetsGoogleReviews = doctor.GoogleReviewCount >= admin.MinGoogleReviewCountForSponsorship;

        var paused = !quality.IsSponsored
            && quality.SponsorshipEnabledAt.HasValue
            && quality.Score < admin.MinQualityScoreForSponsorship;

        return new DoctorSponsorshipStatusDto
        {
            Enabled = quality.IsSponsored,
            CanEnable = meetsQuality && meetsGoogleReviews && hasPaymentMethod,
            QualityScore = quality.Score,
            MinRequired = admin.MinQualityScoreForSponsorship,
            GoogleReviewCount = doctor.GoogleReviewCount,
            MinGoogleReviewsRequired = admin.MinGoogleReviewCountForSponsorship,
            MeetsQualityRequirement = meetsQuality,
            MeetsGoogleReviewRequirement = meetsGoogleReviews,
            HasPaymentMethod = hasPaymentMethod,
            Paused = paused,
            PausedMessage = paused
                ? "Sponsorship paused — quality score dropped below the minimum."
                : null,
            SponsorshipBillingAmountCents = admin.Billing.AmountCents,
            SponsorshipBillingInterval = admin.Billing.Interval,
            SponsorshipBillingCustomDays = admin.Billing.CustomDays,
            SponsorshipBillingSummary = admin.Billing.AmountCents <= 0
                ? "Sponsorship billing amount is not configured yet."
                : $"Billing: {(admin.Billing.AmountCents / 100m).ToString("C")} {admin.Billing.IntervalLabel}.",
            Components = quality.Components,
            Tips = quality.Tips
        };
    }

    public async Task<BillingOperationResultDto> SetEnabledAsync(
        int doctorId,
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        var quality = await _qualityScore.RecomputeAndPersistAsync(doctorId, cancellationToken);
        var doctor = await _db.Doctors.FirstOrDefaultAsync(d => d.Id == doctorId, cancellationToken);
        if (doctor == null)
            return new BillingOperationResultDto { Success = false, Message = "Doctor not found." };

        if (enabled)
        {
            var admin = await _appSettings.GetSponsorshipAdminSettingsAsync(cancellationToken);

            if (quality.Score < admin.MinQualityScoreForSponsorship)
            {
                return new BillingOperationResultDto
                {
                    Success = false,
                    Message = $"Complete your profile and add Google reviews — current score {quality.Score}, need {admin.MinQualityScoreForSponsorship}."
                };
            }

            if (doctor.GoogleReviewCount < admin.MinGoogleReviewCountForSponsorship)
            {
                return new BillingOperationResultDto
                {
                    Success = false,
                    Message = $"You need at least {admin.MinGoogleReviewCountForSponsorship} Google reviews to enable sponsorship (you have {doctor.GoogleReviewCount})."
                };
            }

            if (!await HasActivePaymentMethodAsync(doctorId, cancellationToken))
            {
                return new BillingOperationResultDto
                {
                    Success = false,
                    Message = "Add a credit card under Payment methods before enabling sponsorship."
                };
            }

            doctor.IsSponsored = true;
            doctor.SponsorshipEnabledAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(cancellationToken);
            _logger.LogInformation("Doctor {DoctorId} enabled sponsorship (quality {Score}).", doctorId, quality.Score);
            return new BillingOperationResultDto { Success = true, Message = "Sponsorship enabled." };
        }

        doctor.IsSponsored = false;
        doctor.SponsorshipEnabledAt = null;
        await _db.SaveChangesAsync(cancellationToken);
        _logger.LogInformation("Doctor {DoctorId} disabled sponsorship.", doctorId);
        return new BillingOperationResultDto { Success = true, Message = "Sponsorship disabled." };
    }

    private async Task<bool> HasActivePaymentMethodAsync(int doctorId, CancellationToken cancellationToken)
    {
        var methods = await _paymentMethods.ListPaymentMethodsAsync(doctorId, cancellationToken);
        return methods.Count > 0;
    }
}
