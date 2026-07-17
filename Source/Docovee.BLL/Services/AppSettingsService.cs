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
        var countValue = await GetValueAsync(AppSettingKeys.DoctorSearchResultCount, cancellationToken);
        var promoted = await GetValueAsync(AppSettingKeys.PromotedDoctorIds, cancellationToken);
        var maxQuestions = await GetValueAsync(AppSettingKeys.MaxAiQuestions, cancellationToken);
        var reviewDays = await GetValueAsync(AppSettingKeys.ReviewEligibleDaysAfterConfirmed, cancellationToken);

        return new SiteSettingsModel
        {
            DoctorSearchResultCount = int.TryParse(countValue, out var count) ? Math.Clamp(count, 1, MaxResultCount) : DefaultResultCount,
            PromotedDoctorIds = promoted ?? string.Empty,
            MaxAiQuestions = int.TryParse(maxQuestions, out var mq) ? Math.Clamp(mq, MinAiQuestions, MaxAiQuestionsLimit) : DefaultMaxAiQuestions,
            ReviewEligibleDaysAfterConfirmed = int.TryParse(reviewDays, out var rd)
                ? Math.Clamp(rd, MinReviewEligibleDays, MaxReviewEligibleDays)
                : DefaultReviewEligibleDays
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
