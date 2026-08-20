using Docovee.DS;
using Docovee.DS.Entities;
using Docovee.DS.Models;
using Docovee.logging;
using Microsoft.EntityFrameworkCore;

namespace Docovee.BLL.Services;

public interface IDoctorQualityScoreService
{
    Task<DoctorQualityScoreResult> RecomputeAndPersistAsync(int doctorId, CancellationToken cancellationToken = default);
    Task<DoctorQualityScoreResult?> GetAsync(int doctorId, CancellationToken cancellationToken = default);
    Task<int> RecomputeStaleAsync(TimeSpan maxAge, CancellationToken cancellationToken = default);
}

public class DoctorQualityScoreService : IDoctorQualityScoreService
{
    public const int GoogleRatingWeight = 25;
    public const int GoogleVolumeWeight = 15;
    public const int ProfileWeight = 25;
    public const int ContentWeight = 20;
    public const int NuviReviewWeight = 5;
    public const int CredibilityWeight = 10;

    private readonly DocoveeDbContext _db;
    private readonly IAppSettingsService _appSettings;
    private readonly IDocoveeLogger _logger;

    public DoctorQualityScoreService(
        DocoveeDbContext db,
        IAppSettingsService appSettings,
        IDocoveeLogger logger)
    {
        _db = db;
        _appSettings = appSettings;
        _logger = logger;
    }

    public async Task<DoctorQualityScoreResult?> GetAsync(int doctorId, CancellationToken cancellationToken = default)
    {
        var doctor = await LoadDoctorAsync(doctorId, cancellationToken);
        if (doctor == null)
            return null;

        if (!doctor.QualityScoreUpdatedAt.HasValue)
            return await RecomputeAndPersistAsync(doctorId, cancellationToken);

        return BuildResult(doctor, await _appSettings.GetMinQualityScoreForSponsorshipAsync(cancellationToken));
    }

    public async Task<DoctorQualityScoreResult> RecomputeAndPersistAsync(
        int doctorId,
        CancellationToken cancellationToken = default)
    {
        var doctor = await LoadDoctorAsync(doctorId, cancellationToken)
            ?? throw new InvalidOperationException($"Doctor {doctorId} not found.");

        var computed = Compute(doctor);
        doctor.QualityScore = computed.Score;
        doctor.QualityScoreUpdatedAt = DateTime.UtcNow;

        var minRequired = await _appSettings.GetMinQualityScoreForSponsorshipAsync(cancellationToken);
        if (doctor.IsSponsored && doctor.QualityScore < minRequired)
        {
            doctor.IsSponsored = false;
            _logger.LogInformation(
                "Sponsorship paused for doctor {DoctorId}: quality score {Score} is below minimum {Min}.",
                doctor.Id, doctor.QualityScore, minRequired);
        }

        await _db.SaveChangesAsync(cancellationToken);
        return BuildResult(doctor, minRequired, computed.Tips, computed.Components);
    }

    public async Task<int> RecomputeStaleAsync(TimeSpan maxAge, CancellationToken cancellationToken = default)
    {
        var cutoff = DateTime.UtcNow - maxAge;
        var ids = await _db.Doctors.AsNoTracking()
            .Where(d => d.IsActive && (d.QualityScoreUpdatedAt == null || d.QualityScoreUpdatedAt < cutoff))
            .OrderBy(d => d.QualityScoreUpdatedAt)
            .Select(d => d.Id)
            .Take(100)
            .ToListAsync(cancellationToken);

        var count = 0;
        foreach (var id in ids)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await RecomputeAndPersistAsync(id, cancellationToken);
                count++;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Quality score recompute failed for doctor {DoctorId}.", id);
            }
        }

        return count;
    }

    private async Task<Doctor?> LoadDoctorAsync(int doctorId, CancellationToken cancellationToken) =>
        await _db.Doctors
            .Include(d => d.PatientReviews)
            .Include(d => d.DoctorInsurances)
            .Include(d => d.DoctorLanguages)
            .Include(d => d.Media)
            .FirstOrDefaultAsync(d => d.Id == doctorId, cancellationToken);

    private static DoctorQualityScoreResult BuildResult(
        Doctor doctor,
        int minRequired,
        IReadOnlyList<string>? tips = null,
        IReadOnlyList<DoctorQualityComponentDto>? components = null)
    {
        var computed = components == null || tips == null ? Compute(doctor) : null;
        return new DoctorQualityScoreResult
        {
            Score = doctor.QualityScore,
            UpdatedAt = doctor.QualityScoreUpdatedAt,
            MinRequired = minRequired,
            IsSponsored = doctor.IsSponsored,
            SponsorshipEnabledAt = doctor.SponsorshipEnabledAt,
            Components = components ?? computed!.Components,
            Tips = tips ?? computed!.Tips
        };
    }

    private static ComputedQuality Compute(Doctor doctor)
    {
        var googleRating = ScoreGoogleRating(doctor.GoogleRating);
        var googleVolume = ScoreGoogleVolume(doctor.GoogleReviewCount);
        var profile = ScoreProfile(doctor);
        var content = ScoreContent(doctor);
        var nuvi = ScoreNuviReviews(doctor);
        var credibility = ScoreCredibility(doctor);

        var score = (int)Math.Round(
            googleRating * GoogleRatingWeight / 100.0
            + googleVolume * GoogleVolumeWeight / 100.0
            + profile * ProfileWeight / 100.0
            + content * ContentWeight / 100.0
            + nuvi * NuviReviewWeight / 100.0
            + credibility * CredibilityWeight / 100.0);

        score = Math.Clamp(score, 0, 100);

        var components = new List<DoctorQualityComponentDto>
        {
            new() { Name = "Google rating", WeightPercent = GoogleRatingWeight, Score = googleRating },
            new() { Name = "Google review volume", WeightPercent = GoogleVolumeWeight, Score = googleVolume },
            new() { Name = "Profile completeness", WeightPercent = ProfileWeight, Score = profile },
            new() { Name = "Content richness", WeightPercent = ContentWeight, Score = content },
            new() { Name = "NuviDoc reviews", WeightPercent = NuviReviewWeight, Score = nuvi },
            new() { Name = "Practice credibility", WeightPercent = CredibilityWeight, Score = credibility }
        };

        return new ComputedQuality(score, components, BuildTips(doctor));
    }

    private static int ScoreGoogleRating(decimal rating)
    {
        if (rating <= 0)
            return 0;
        return (int)Math.Round(Math.Clamp((double)rating / 5.0, 0, 1) * 100);
    }

    private static int ScoreGoogleVolume(int count)
    {
        var capped = Math.Min(Math.Max(count, 0), 200);
        if (capped <= 0)
            return 0;
        var ratio = Math.Log10(1 + capped) / Math.Log10(1 + 200);
        return (int)Math.Round(Math.Clamp(ratio, 0, 1) * 100);
    }

    private static int ScoreProfile(Doctor doctor)
    {
        var completion = Math.Clamp(doctor.ProfileCompletionPercent, 0, 100);
        var photo = HasValue(doctor.PhotoUrl) || HasValue(doctor.GmbPhotoLink) || HasValue(doctor.PracticeLogoUrl);
        var video = HasValue(doctor.VideoUrl) || doctor.Media.Any(m =>
            string.Equals(m.MediaType, DoctorMediaTypes.Video, StringComparison.OrdinalIgnoreCase));
        var insurance = doctor.DoctorInsurances.Count > 0;
        var languages = doctor.DoctorLanguages.Count > 0;

        var bonus = (photo ? 10 : 0) + (video ? 10 : 0) + (insurance ? 10 : 0) + (languages ? 10 : 0);
        return Math.Clamp((int)Math.Round(completion * 0.6) + bonus, 0, 100);
    }

    private static int ScoreContent(Doctor doctor)
    {
        var score = 0;
        if (HasValue(doctor.Top3Procedures)) score += 15;
        if (HasValue(doctor.Niche)) score += 15;
        if (HasValue(doctor.SummaryOfReviews) && doctor.SummaryOfReviews!.Length >= 40) score += 15;
        if (HasValue(doctor.Website)) score += 20;
        var socialCount = new[]
        {
            doctor.FacebookUrl, doctor.InstagramUrl, doctor.TikTokUrl, doctor.LinkedInUrl, doctor.YoutubeChannelUrl
        }.Count(HasValue);
        score += Math.Min(socialCount, 4) * 5;
        var mediaCount = doctor.Media?.Count ?? 0;
        score += Math.Min(mediaCount, 4) * 5;
        return Math.Clamp(score, 0, 100);
    }

    /// <summary>
    /// Implant / one-time visits rarely produce NuviDoc reviews. Missing reviews are treated as
    /// a neutral 50 so Google + profile still drive the score.
    /// </summary>
    private static int ScoreNuviReviews(Doctor doctor)
    {
        var reviews = doctor.PatientReviews?.ToList() ?? [];
        if (reviews.Count == 0)
            return 50;

        var avg = reviews.Average(r => r.Rating);
        var avgScore = avg / 5.0 * 100;
        var volumeScore = Math.Min(reviews.Count / 10.0, 1.0) * 100;
        return (int)Math.Round(0.7 * avgScore + 0.3 * volumeScore);
    }

    private static int ScoreCredibility(Doctor doctor)
    {
        var years = doctor.YearsOfPractice.GetValueOrDefault();
        var yearsScore = Math.Min(Math.Max(years, 0) / 20.0, 1.0) * 40;
        var gradScore = doctor.GraduationYear is >= 1950 and <= 2100 ? 30 : 0;
        var procedureScore = Math.Min(Math.Max(doctor.ProcedureCount.GetValueOrDefault(), 0) / 1000.0, 1.0) * 30;
        return (int)Math.Round(yearsScore + gradScore + procedureScore);
    }

    private static List<string> BuildTips(Doctor doctor)
    {
        var tips = new List<string>();
        if (doctor.GoogleReviewCount < 20)
            tips.Add("Ask patients to leave Google reviews — those carry the most weight for implant cases.");
        if (!HasValue(doctor.Website))
            tips.Add("Add a practice website in Settings → Practice profile.");
        if (!HasValue(doctor.VideoUrl) && (doctor.Media == null || doctor.Media.Count == 0))
            tips.Add("Add a practice video to improve your content score.");
        if (doctor.DoctorInsurances.Count == 0)
            tips.Add("List accepted insurance plans in Settings → Insurance.");
        if (doctor.ProfileCompletionPercent < 80)
            tips.Add("Finish onboarding and fill out your practice profile.");
        if (!HasValue(doctor.Top3Procedures) || !HasValue(doctor.Niche))
            tips.Add("Add your top procedures and clinical niche so patients can match on expertise.");
        if (tips.Count == 0)
            tips.Add("Your quality profile is strong. Keep Google reviews and content up to date.");
        return tips;
    }

    private static bool HasValue(string? value) => !string.IsNullOrWhiteSpace(value);

    private sealed record ComputedQuality(
        int Score,
        IReadOnlyList<DoctorQualityComponentDto> Components,
        IReadOnlyList<string> Tips);
}
