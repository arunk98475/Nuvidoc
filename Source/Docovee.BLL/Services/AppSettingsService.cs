using Docovee.BLL.Configuration;

using Docovee.DS;
using Docovee.DS.Entities;
using Docovee.DS.Models;
using Microsoft.EntityFrameworkCore;

namespace Docovee.BLL.Services;

public interface IAppSettingsService
{
    Task<int> GetDoctorSearchResultCountAsync(CancellationToken cancellationToken = default);
    Task<int> GetMaxAiQuestionsAsync(CancellationToken cancellationToken = default);
    Task<int> GetReviewEligibleDaysAfterConfirmedAsync(CancellationToken cancellationToken = default);
    Task<SiteSettingsModel> GetSiteSettingsAsync(CancellationToken cancellationToken = default);
    Task SaveSiteSettingsAsync(SiteSettingsModel settings, CancellationToken cancellationToken = default);
    Task<int> GetDefaultPerVisitFeeCentsAsync(CancellationToken cancellationToken = default);
    Task<int> GetFreeVisitCountAsync(CancellationToken cancellationToken = default);
    Task SaveDoctorBillingDefaultsAsync(decimal perVisitFeeUsd, int freeVisitCount, CancellationToken cancellationToken = default);
    Task<int> GetMinQualityScoreForSponsorshipAsync(CancellationToken cancellationToken = default);
}

public class AppSettingsService : IAppSettingsService
{
    private const int DefaultResultCount = 10;
    private const int MaxResultCount = 50;
    private const int DefaultMaxAiQuestions = 3;
    private const int MinAiQuestions = 2;
    private const int MaxAiQuestionsLimit = 5;
    private const int DefaultReviewEligibleDays = 1;
    private const int MinReviewEligibleDays = 0;
    private const int MaxReviewEligibleDays = 90;
    private const int DefaultMinQualityScoreForSponsorship = 40;

    private readonly DocoveeDbContext _db;

    public AppSettingsService(DocoveeDbContext db) => _db = db;

    public async Task<int> GetDoctorSearchResultCountAsync(CancellationToken cancellationToken = default)
    {
        var value = await GetValueAsync(AppSettingKeys.DoctorSearchResultCount, cancellationToken);
        if (int.TryParse(value, out var count))
            return Math.Clamp(count, 1, MaxResultCount);
        return DefaultResultCount;
    }

    public async Task<int> GetMaxAiQuestionsAsync(CancellationToken cancellationToken = default)
    {
        var value = await GetValueAsync(AppSettingKeys.MaxAiQuestions, cancellationToken);
        if (int.TryParse(value, out var count))
            return Math.Clamp(count, MinAiQuestions, MaxAiQuestionsLimit);
        return DefaultMaxAiQuestions;
    }

    public async Task<int> GetReviewEligibleDaysAfterConfirmedAsync(CancellationToken cancellationToken = default)
    {
        var value = await GetValueAsync(AppSettingKeys.ReviewEligibleDaysAfterConfirmed, cancellationToken);
        if (int.TryParse(value, out var days))
            return Math.Clamp(days, MinReviewEligibleDays, MaxReviewEligibleDays);
        return DefaultReviewEligibleDays;
    }

    public async Task<SiteSettingsModel> GetSiteSettingsAsync(CancellationToken cancellationToken = default)
    {
        var keys = new[]
        {
            AppSettingKeys.DoctorSearchResultCount,
            AppSettingKeys.PromotedDoctorIds,
            AppSettingKeys.MaxAiQuestions,
            AppSettingKeys.ReviewEligibleDaysAfterConfirmed,
            AppSettingKeys.FooterFacebookUrl,
            AppSettingKeys.FooterInstagramUrl,
            AppSettingKeys.FooterTwitterUrl,
            AppSettingKeys.FooterLinkedInUrl,
            AppSettingKeys.FooterAppStoreUrl,
            AppSettingKeys.FooterPlayStoreUrl,
            AppSettingKeys.FooterTermsPdfUrl,
            AppSettingKeys.FooterPrivacyPdfUrl,
            AppSettingKeys.FooterConsumerHealthPdfUrl,
            AppSettingKeys.FooterPrivacyChoicesPdfUrl
        };

        var rows = await _db.AppSettings.AsNoTracking()
            .Where(s => keys.Contains(s.Key))
            .ToListAsync(cancellationToken);

        string Val(string key) => rows.FirstOrDefault(s => s.Key == key)?.Value ?? string.Empty;

        return new SiteSettingsModel
        {
            DoctorSearchResultCount = int.TryParse(Val(AppSettingKeys.DoctorSearchResultCount), out var count)
                ? Math.Clamp(count, 1, MaxResultCount) : DefaultResultCount,
            PromotedDoctorIds = Val(AppSettingKeys.PromotedDoctorIds),
            MaxAiQuestions = int.TryParse(Val(AppSettingKeys.MaxAiQuestions), out var mq)
                ? Math.Clamp(mq, MinAiQuestions, MaxAiQuestionsLimit) : DefaultMaxAiQuestions,
            ReviewEligibleDaysAfterConfirmed = int.TryParse(Val(AppSettingKeys.ReviewEligibleDaysAfterConfirmed), out var rd)
                ? Math.Clamp(rd, MinReviewEligibleDays, MaxReviewEligibleDays) : DefaultReviewEligibleDays,
            FacebookUrl = Val(AppSettingKeys.FooterFacebookUrl),
            InstagramUrl = Val(AppSettingKeys.FooterInstagramUrl),
            TwitterUrl = Val(AppSettingKeys.FooterTwitterUrl),
            LinkedInUrl = Val(AppSettingKeys.FooterLinkedInUrl),
            AppStoreUrl = Val(AppSettingKeys.FooterAppStoreUrl),
            PlayStoreUrl = Val(AppSettingKeys.FooterPlayStoreUrl),
            TermsPdfUrl = Val(AppSettingKeys.FooterTermsPdfUrl),
            PrivacyPdfUrl = Val(AppSettingKeys.FooterPrivacyPdfUrl),
            ConsumerHealthPdfUrl = Val(AppSettingKeys.FooterConsumerHealthPdfUrl),
            PrivacyChoicesPdfUrl = Val(AppSettingKeys.FooterPrivacyChoicesPdfUrl)
        };
    }

    public async Task SaveSiteSettingsAsync(SiteSettingsModel settings, CancellationToken cancellationToken = default)
    {
        var count = Math.Clamp(settings.DoctorSearchResultCount, 1, MaxResultCount);
        var maxQuestions = Math.Clamp(settings.MaxAiQuestions, MinAiQuestions, MaxAiQuestionsLimit);
        var reviewDays = Math.Clamp(settings.ReviewEligibleDaysAfterConfirmed, MinReviewEligibleDays, MaxReviewEligibleDays);
        await SetValueAsync(AppSettingKeys.DoctorSearchResultCount, count.ToString(), cancellationToken);
        await SetValueAsync(AppSettingKeys.PromotedDoctorIds, settings.PromotedDoctorIds?.Trim() ?? string.Empty, cancellationToken);
        await SetValueAsync(AppSettingKeys.MaxAiQuestions, maxQuestions.ToString(), cancellationToken);
        await SetValueAsync(AppSettingKeys.ReviewEligibleDaysAfterConfirmed, reviewDays.ToString(), cancellationToken);
        await SetValueAsync(AppSettingKeys.FooterFacebookUrl, NormalizeUrl(settings.FacebookUrl), cancellationToken);
        await SetValueAsync(AppSettingKeys.FooterInstagramUrl, NormalizeUrl(settings.InstagramUrl), cancellationToken);
        await SetValueAsync(AppSettingKeys.FooterTwitterUrl, NormalizeUrl(settings.TwitterUrl), cancellationToken);
        await SetValueAsync(AppSettingKeys.FooterLinkedInUrl, NormalizeUrl(settings.LinkedInUrl), cancellationToken);
        await SetValueAsync(AppSettingKeys.FooterAppStoreUrl, NormalizeUrl(settings.AppStoreUrl), cancellationToken);
        await SetValueAsync(AppSettingKeys.FooterPlayStoreUrl, NormalizeUrl(settings.PlayStoreUrl), cancellationToken);
        await SetValueAsync(AppSettingKeys.FooterTermsPdfUrl, settings.TermsPdfUrl?.Trim() ?? string.Empty, cancellationToken);
        await SetValueAsync(AppSettingKeys.FooterPrivacyPdfUrl, settings.PrivacyPdfUrl?.Trim() ?? string.Empty, cancellationToken);
        await SetValueAsync(AppSettingKeys.FooterConsumerHealthPdfUrl, settings.ConsumerHealthPdfUrl?.Trim() ?? string.Empty, cancellationToken);
        await SetValueAsync(AppSettingKeys.FooterPrivacyChoicesPdfUrl, settings.PrivacyChoicesPdfUrl?.Trim() ?? string.Empty, cancellationToken);
    }

    public async Task<int> GetDefaultPerVisitFeeCentsAsync(CancellationToken cancellationToken = default)
    {
        var value = await GetValueAsync(AppSettingKeys.DefaultPerVisitFeeCents, cancellationToken);
        if (int.TryParse(value, out var cents))
            return Math.Max(0, cents);
        return 0;
    }

    public async Task<int> GetFreeVisitCountAsync(CancellationToken cancellationToken = default)
    {
        var value = await GetValueAsync(AppSettingKeys.FreeVisitCount, cancellationToken);
        if (int.TryParse(value, out var count))
            return Math.Clamp(count, 0, 10_000);
        return 0;
    }

    public async Task<int> GetMinQualityScoreForSponsorshipAsync(CancellationToken cancellationToken = default)
    {
        var value = await GetValueAsync(AppSettingKeys.MinQualityScoreForSponsorship, cancellationToken);
        if (int.TryParse(value, out var score))
            return Math.Clamp(score, 0, 100);
        return DefaultMinQualityScoreForSponsorship;
    }

    public async Task SaveDoctorBillingDefaultsAsync(
        decimal perVisitFeeUsd,
        int freeVisitCount,
        CancellationToken cancellationToken = default)
    {
        var cents = perVisitFeeUsd < 0
            ? 0
            : (int)Math.Round(perVisitFeeUsd * 100m, MidpointRounding.AwayFromZero);
        var visits = Math.Clamp(freeVisitCount, 0, 10_000);
        await SetValueAsync(AppSettingKeys.DefaultPerVisitFeeCents, cents.ToString(), cancellationToken);
        await SetValueAsync(AppSettingKeys.FreeVisitCount, visits.ToString(), cancellationToken);
    }

    private static string NormalizeUrl(string? url)
    {
        var value = url?.Trim() ?? string.Empty;
        if (value.Length == 0)
            return string.Empty;
        if (value.StartsWith('/') || value.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return value;
        return "https://" + value;
    }

    private async Task<string?> GetValueAsync(string key, CancellationToken cancellationToken)
    {
        var setting = await _db.AppSettings.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Key == key, cancellationToken);
        return setting?.Value;
    }

    private async Task SetValueAsync(string key, string value, CancellationToken cancellationToken)
    {
        var setting = await _db.AppSettings.FirstOrDefaultAsync(s => s.Key == key, cancellationToken);
        if (setting == null)
        {
            _db.AppSettings.Add(new AppSetting { Key = key, Value = value });
        }
        else
        {
            setting.Value = value;
            setting.UpdatedAt = DateTime.UtcNow;
        }
        await _db.SaveChangesAsync(cancellationToken);
    }
}
