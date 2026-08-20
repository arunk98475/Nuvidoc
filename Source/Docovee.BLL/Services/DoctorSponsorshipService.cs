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
/// v1: opt-in flag only. No Stripe charge. Plug a subscription here when monthly sponsorship billing goes live.
/// </summary>
public sealed class DoctorSponsorshipService : IDoctorSponsorshipService
{
    private readonly DocoveeDbContext _db;
    private readonly IDoctorQualityScoreService _qualityScore;
    private readonly IDocoveeLogger _logger;

    public DoctorSponsorshipService(
        DocoveeDbContext db,
        IDoctorQualityScoreService qualityScore,
        IDocoveeLogger logger)
    {
        _db = db;
        _qualityScore = qualityScore;
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

        var paused = !quality.IsSponsored
            && quality.SponsorshipEnabledAt.HasValue
            && quality.Score < quality.MinRequired;

        return new DoctorSponsorshipStatusDto
        {
            Enabled = quality.IsSponsored,
            CanEnable = quality.Score >= quality.MinRequired,
            QualityScore = quality.Score,
            MinRequired = quality.MinRequired,
            Paused = paused,
            PausedMessage = paused
                ? "Sponsorship paused — quality score dropped below the minimum."
                : null,
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
            if (quality.Score < quality.MinRequired)
            {
                return new BillingOperationResultDto
                {
                    Success = false,
                    Message = $"Complete your profile and add Google reviews — current score {quality.Score}, need {quality.MinRequired}."
                };
            }

            // Future: require a card and create a Stripe subscription before setting IsSponsored.
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
}
