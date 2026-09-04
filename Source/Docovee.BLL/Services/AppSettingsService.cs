using Docovee.BLL.Configuration;

using Docovee.DS;
using Docovee.DS.Entities;
using Docovee.DS.Enums;
using Docovee.DS.Models;
using Microsoft.EntityFrameworkCore;

namespace Docovee.BLL.Services;

public interface IAppSettingsService
{
    Task<int> GetDoctorSearchResultCountAsync(CancellationToken cancellationToken = default);
    Task<int> GetMaxAiQuestionsAsync(CancellationToken cancellationToken = default);
    Task<bool> GetFeedbackRequestEnabledAsync(CancellationToken cancellationToken = default);
    Task<int> GetFeedbackRequestHoursAfterBookingAsync(CancellationToken cancellationToken = default);
    Task<SiteSettingsModel> GetSiteSettingsAsync(CancellationToken cancellationToken = default);
    Task SaveSiteSettingsAsync(SiteSettingsModel settings, CancellationToken cancellationToken = default);
    Task<bool> GetEnableProcedureCostConsiderationAsync(CancellationToken cancellationToken = default);
    Task<int> GetDefaultPerVisitFeeCentsAsync(CancellationToken cancellationToken = default);
    Task<int> GetFreeVisitCountAsync(CancellationToken cancellationToken = default);
    Task<bool> GetVisitBillingChargeOnlyIfPatientShowedAsync(CancellationToken cancellationToken = default);
    Task SaveDoctorBillingDefaultsAsync(
        decimal perVisitFeeUsd,
        int freeVisitCount,
        bool chargeOnlyIfPatientShowed,
        CancellationToken cancellationToken = default);
    Task<int> GetMinQualityScoreForSponsorshipAsync(CancellationToken cancellationToken = default);
    Task SaveMinQualityScoreForSponsorshipAsync(int minQualityScore, CancellationToken cancellationToken = default);
    Task<SponsorshipBillingSettings> GetSponsorshipBillingSettingsAsync(CancellationToken cancellationToken = default);
    Task<SponsorshipAdminSettings> GetSponsorshipAdminSettingsAsync(CancellationToken cancellationToken = default);
    Task SaveSponsorshipAdminSettingsAsync(SponsorshipAdminSettings settings, CancellationToken cancellationToken = default);
    Task<PatientBookingReminderSettings> GetPatientBookingReminderSettingsAsync(CancellationToken cancellationToken = default);
    Task<(bool Success, string? Error)> SavePatientBookingReminderSettingsAsync(
        PatientBookingReminderSettings settings,
        CancellationToken cancellationToken = default);
    Task<PatientBookingReminderRunStatus> GetPatientBookingReminderRunStatusAsync(
        CancellationToken cancellationToken = default);
    Task RecordPatientBookingReminderRunAsync(
        DateTime runUtc,
        int sentCount,
        CancellationToken cancellationToken = default);
    Task<PatientAccountLifecycleSettings> GetPatientAccountLifecycleSettingsAsync(CancellationToken cancellationToken = default);
    Task<(bool Success, string? Error)> SavePatientAccountLifecycleSettingsAsync(
        PatientAccountLifecycleSettings settings,
        CancellationToken cancellationToken = default);
    Task<PatientNuviVerificationSettings> GetPatientNuviVerificationSettingsAsync(CancellationToken cancellationToken = default);
    Task<(bool Success, string? Error)> SavePatientNuviVerificationSettingsAsync(
        PatientNuviVerificationSettings settings,
        CancellationToken cancellationToken = default);
}

public class AppSettingsService : IAppSettingsService
{
    private const int DefaultResultCount = 10;
    private const int MaxResultCount = 50;
    private const int DefaultMaxAiQuestions = 3;
    private const int MinAiQuestions = 2;
    private const int MaxAiQuestionsLimit = 5;
    private const int DefaultFeedbackHoursAfterBooking = 24;
    private const int MinFeedbackHoursAfterBooking = 1;
    private const int MaxFeedbackHoursAfterBooking = 720;
    private const int DefaultMinQualityScoreForSponsorship = 40;
    private const int DefaultMinGoogleReviewCountForSponsorship = 5;
    private const int DefaultBookingReminderIntervalDays = 30;
    private const int MinBookingReminderIntervalDays = 1;
    private const int MaxBookingReminderIntervalDays = 90;
    private const int DefaultBookingReminderStopAfterMonths = 12;
    private const int MinBookingReminderStopAfterMonths = 1;
    private const int MaxBookingReminderStopAfterMonths = 24;
    private const int DefaultAutoCloseInactiveMonths = 24;
    private const int MinAutoCloseInactiveMonths = 1;
    private const int MaxAutoCloseInactiveMonths = 120;
    private const int DefaultAutoDeleteClosedMonths = 3;
    private const int MinAutoDeleteClosedMonths = 1;
    private const int MaxAutoDeleteClosedMonths = 60;

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

    public async Task<bool> GetFeedbackRequestEnabledAsync(CancellationToken cancellationToken = default)
    {
        var value = await GetValueAsync(AppSettingKeys.FeedbackRequestEnabled, cancellationToken);
        return ParseBoolSetting(value, defaultValue: true);
    }

    public async Task<int> GetFeedbackRequestHoursAfterBookingAsync(CancellationToken cancellationToken = default)
    {
        var value = await GetValueAsync(AppSettingKeys.FeedbackRequestHoursAfterBooking, cancellationToken);
        if (int.TryParse(value, out var hours))
            return Math.Clamp(hours, MinFeedbackHoursAfterBooking, MaxFeedbackHoursAfterBooking);
        return DefaultFeedbackHoursAfterBooking;
    }

    public async Task<SiteSettingsModel> GetSiteSettingsAsync(CancellationToken cancellationToken = default)
    {
        var keys = new[]
        {
            AppSettingKeys.DoctorSearchResultCount,
            AppSettingKeys.PromotedDoctorIds,
            AppSettingKeys.MaxAiQuestions,
            AppSettingKeys.FeedbackRequestEnabled,
            AppSettingKeys.FeedbackRequestHoursAfterBooking,
            AppSettingKeys.EnableProcedureCostConsideration,
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
            FeedbackRequestEnabled = ParseBoolSetting(Val(AppSettingKeys.FeedbackRequestEnabled), defaultValue: true),
            FeedbackRequestHoursAfterBooking = int.TryParse(Val(AppSettingKeys.FeedbackRequestHoursAfterBooking), out var fh)
                ? Math.Clamp(fh, MinFeedbackHoursAfterBooking, MaxFeedbackHoursAfterBooking)
                : DefaultFeedbackHoursAfterBooking,
            EnableProcedureCostConsideration = ParseBoolSetting(Val(AppSettingKeys.EnableProcedureCostConsideration)),
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
        var feedbackHours = Math.Clamp(
            settings.FeedbackRequestHoursAfterBooking,
            MinFeedbackHoursAfterBooking,
            MaxFeedbackHoursAfterBooking);
        await SetValueAsync(AppSettingKeys.DoctorSearchResultCount, count.ToString(), cancellationToken);
        await SetValueAsync(AppSettingKeys.PromotedDoctorIds, settings.PromotedDoctorIds?.Trim() ?? string.Empty, cancellationToken);
        await SetValueAsync(AppSettingKeys.MaxAiQuestions, maxQuestions.ToString(), cancellationToken);
        await SetValueAsync(
            AppSettingKeys.FeedbackRequestEnabled,
            settings.FeedbackRequestEnabled ? "true" : "false",
            cancellationToken);
        await SetValueAsync(AppSettingKeys.FeedbackRequestHoursAfterBooking, feedbackHours.ToString(), cancellationToken);
        await SetValueAsync(
            AppSettingKeys.EnableProcedureCostConsideration,
            settings.EnableProcedureCostConsideration ? "true" : "false",
            cancellationToken);
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

    public async Task<bool> GetEnableProcedureCostConsiderationAsync(CancellationToken cancellationToken = default)
    {
        var value = await GetValueAsync(AppSettingKeys.EnableProcedureCostConsideration, cancellationToken);
        return ParseBoolSetting(value);
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

    public async Task<bool> GetVisitBillingChargeOnlyIfPatientShowedAsync(CancellationToken cancellationToken = default)
    {
        var value = await GetValueAsync(AppSettingKeys.VisitBillingChargeOnlyIfPatientShowed, cancellationToken);
        return ParseBoolSetting(value, defaultValue: true);
    }

    public async Task<int> GetMinQualityScoreForSponsorshipAsync(CancellationToken cancellationToken = default)
    {
        var value = await GetValueAsync(AppSettingKeys.MinQualityScoreForSponsorship, cancellationToken);
        if (int.TryParse(value, out var score))
            return Math.Clamp(score, 0, 100);
        return DefaultMinQualityScoreForSponsorship;
    }

    public async Task SaveMinQualityScoreForSponsorshipAsync(
        int minQualityScore,
        CancellationToken cancellationToken = default)
    {
        var score = Math.Clamp(minQualityScore, 0, 100);
        await SetValueAsync(AppSettingKeys.MinQualityScoreForSponsorship, score.ToString(), cancellationToken);
    }

    public async Task<SponsorshipBillingSettings> GetSponsorshipBillingSettingsAsync(
        CancellationToken cancellationToken = default) =>
        (await GetSponsorshipAdminSettingsAsync(cancellationToken)).Billing;

    public async Task<SponsorshipAdminSettings> GetSponsorshipAdminSettingsAsync(
        CancellationToken cancellationToken = default)
    {
        var keys = new[]
        {
            AppSettingKeys.MinQualityScoreForSponsorship,
            AppSettingKeys.MinGoogleReviewCountForSponsorship,
            AppSettingKeys.SponsorshipBillingAmountCents,
            AppSettingKeys.SponsorshipBillingInterval,
            AppSettingKeys.SponsorshipBillingCustomDays,
            AppSettingKeys.SponsorshipBillingChargeOnlyIfPatientShowed
        };

        var rows = await _db.AppSettings.AsNoTracking()
            .Where(s => keys.Contains(s.Key))
            .ToListAsync(cancellationToken);

        string Val(string key) => rows.FirstOrDefault(s => s.Key == key)?.Value ?? string.Empty;

        var intervalRaw = Val(AppSettingKeys.SponsorshipBillingInterval);
        var interval = Enum.TryParse<SponsorshipBillingInterval>(intervalRaw, ignoreCase: true, out var parsedInterval)
            ? parsedInterval
            : SponsorshipBillingInterval.Monthly;

        var customDays = int.TryParse(Val(AppSettingKeys.SponsorshipBillingCustomDays), out var days)
            ? Math.Clamp(days, 1, 365)
            : 30;

        var amountCents = int.TryParse(Val(AppSettingKeys.SponsorshipBillingAmountCents), out var cents)
            ? Math.Max(0, cents)
            : 0;

        return new SponsorshipAdminSettings
        {
            MinQualityScoreForSponsorship = int.TryParse(Val(AppSettingKeys.MinQualityScoreForSponsorship), out var minScore)
                ? Math.Clamp(minScore, 0, 100)
                : DefaultMinQualityScoreForSponsorship,
            MinGoogleReviewCountForSponsorship = int.TryParse(Val(AppSettingKeys.MinGoogleReviewCountForSponsorship), out var minReviews)
                ? Math.Clamp(minReviews, 0, 10_000)
                : DefaultMinGoogleReviewCountForSponsorship,
            Billing = new SponsorshipBillingSettings
            {
                AmountCents = amountCents,
                Interval = interval,
                CustomDays = customDays,
                ChargeOnlyIfPatientShowed = ParseBoolSetting(Val(AppSettingKeys.SponsorshipBillingChargeOnlyIfPatientShowed))
            }
        };
    }

    public async Task SaveSponsorshipAdminSettingsAsync(
        SponsorshipAdminSettings settings,
        CancellationToken cancellationToken = default)
    {
        var minScore = Math.Clamp(settings.MinQualityScoreForSponsorship, 0, 100);
        var minGoogleReviews = Math.Clamp(settings.MinGoogleReviewCountForSponsorship, 0, 10_000);
        var billing = settings.Billing ?? new SponsorshipBillingSettings();
        var amountCents = Math.Max(0, billing.AmountCents);
        var interval = Enum.IsDefined(billing.Interval)
            ? billing.Interval
            : SponsorshipBillingInterval.Monthly;
        var customDays = Math.Clamp(billing.CustomDays, 1, 365);

        await SetValueAsync(AppSettingKeys.MinQualityScoreForSponsorship, minScore.ToString(), cancellationToken);
        await SetValueAsync(AppSettingKeys.MinGoogleReviewCountForSponsorship, minGoogleReviews.ToString(), cancellationToken);
        await SetValueAsync(AppSettingKeys.SponsorshipBillingAmountCents, Math.Max(0, amountCents).ToString(), cancellationToken);
        await SetValueAsync(AppSettingKeys.SponsorshipBillingInterval, interval.ToString(), cancellationToken);
        await SetValueAsync(AppSettingKeys.SponsorshipBillingCustomDays, customDays.ToString(), cancellationToken);
        var chargeOnlyIfShowed = interval == SponsorshipBillingInterval.PerBooking && billing.ChargeOnlyIfPatientShowed;
        await SetValueAsync(AppSettingKeys.SponsorshipBillingChargeOnlyIfPatientShowed, chargeOnlyIfShowed ? "true" : "false", cancellationToken);
    }

    public async Task<PatientBookingReminderSettings> GetPatientBookingReminderSettingsAsync(
        CancellationToken cancellationToken = default)
    {
        var keys = new[]
        {
            AppSettingKeys.BookingReminderEnabled,
            AppSettingKeys.BookingReminderIntervalDays,
            AppSettingKeys.BookingReminderStopAfterMonths,
            AppSettingKeys.BookingReminderEnableWhatsApp,
            AppSettingKeys.BookingReminderEnableEmail,
            AppSettingKeys.BookingReminderEnableSms
        };

        var rows = await _db.AppSettings.AsNoTracking()
            .Where(s => keys.Contains(s.Key))
            .ToListAsync(cancellationToken);

        string Val(string key) => rows.FirstOrDefault(s => s.Key == key)?.Value ?? string.Empty;

        return new PatientBookingReminderSettings
        {
            Enabled = ParseBoolSetting(Val(AppSettingKeys.BookingReminderEnabled)),
            IntervalDays = int.TryParse(Val(AppSettingKeys.BookingReminderIntervalDays), out var interval)
                ? Math.Clamp(interval, MinBookingReminderIntervalDays, MaxBookingReminderIntervalDays)
                : DefaultBookingReminderIntervalDays,
            StopAfterMonths = int.TryParse(Val(AppSettingKeys.BookingReminderStopAfterMonths), out var months)
                ? Math.Clamp(months, MinBookingReminderStopAfterMonths, MaxBookingReminderStopAfterMonths)
                : DefaultBookingReminderStopAfterMonths,
            EnableWhatsApp = ParseBoolSetting(Val(AppSettingKeys.BookingReminderEnableWhatsApp)),
            EnableEmail = ParseBoolSetting(Val(AppSettingKeys.BookingReminderEnableEmail)),
            EnableSms = ParseBoolSetting(Val(AppSettingKeys.BookingReminderEnableSms))
        };
    }

    public async Task<(bool Success, string? Error)> SavePatientBookingReminderSettingsAsync(
        PatientBookingReminderSettings settings,
        CancellationToken cancellationToken = default)
    {
        var interval = Math.Clamp(settings.IntervalDays, MinBookingReminderIntervalDays, MaxBookingReminderIntervalDays);
        var months = Math.Clamp(settings.StopAfterMonths, MinBookingReminderStopAfterMonths, MaxBookingReminderStopAfterMonths);
        var enabled = settings.Enabled;
        var whatsApp = settings.EnableWhatsApp;
        var email = settings.EnableEmail;
        var sms = settings.EnableSms;

        if (enabled && !whatsApp && !email && !sms)
            return (false, "Turn on at least one channel (WhatsApp, email, or SMS) when reminders are enabled.");

        await SetValueAsync(AppSettingKeys.BookingReminderEnabled, enabled ? "true" : "false", cancellationToken);
        await SetValueAsync(AppSettingKeys.BookingReminderIntervalDays, interval.ToString(), cancellationToken);
        await SetValueAsync(AppSettingKeys.BookingReminderStopAfterMonths, months.ToString(), cancellationToken);
        await SetValueAsync(AppSettingKeys.BookingReminderEnableWhatsApp, whatsApp ? "true" : "false", cancellationToken);
        await SetValueAsync(AppSettingKeys.BookingReminderEnableEmail, email ? "true" : "false", cancellationToken);
        await SetValueAsync(AppSettingKeys.BookingReminderEnableSms, sms ? "true" : "false", cancellationToken);
        return (true, null);
    }

    public async Task<PatientBookingReminderRunStatus> GetPatientBookingReminderRunStatusAsync(
        CancellationToken cancellationToken = default)
    {
        var runUtcRaw = await GetValueAsync(AppSettingKeys.BookingReminderLastRunUtc, cancellationToken);
        var sentRaw = await GetValueAsync(AppSettingKeys.BookingReminderLastRunSentCount, cancellationToken);
        DateTime? lastRunUtc = DateTime.TryParse(
            runUtcRaw,
            null,
            System.Globalization.DateTimeStyles.RoundtripKind,
            out var parsed)
            ? parsed.ToUniversalTime()
            : null;
        _ = int.TryParse(sentRaw, out var sentCount);
        return new PatientBookingReminderRunStatus
        {
            LastRunUtc = lastRunUtc,
            LastRunSentCount = sentCount
        };
    }

    public Task RecordPatientBookingReminderRunAsync(
        DateTime runUtc,
        int sentCount,
        CancellationToken cancellationToken = default)
    {
        var utc = runUtc.Kind == DateTimeKind.Utc
            ? runUtc
            : runUtc.ToUniversalTime();
        return RecordPatientBookingReminderRunCoreAsync(utc, Math.Max(0, sentCount), cancellationToken);
    }

    private async Task RecordPatientBookingReminderRunCoreAsync(
        DateTime runUtc,
        int sentCount,
        CancellationToken cancellationToken)
    {
        await SetValueAsync(AppSettingKeys.BookingReminderLastRunUtc, runUtc.ToString("o"), cancellationToken);
        await SetValueAsync(AppSettingKeys.BookingReminderLastRunSentCount, sentCount.ToString(), cancellationToken);
    }

    public async Task<PatientAccountLifecycleSettings> GetPatientAccountLifecycleSettingsAsync(
        CancellationToken cancellationToken = default)
    {
        var keys = new[]
        {
            AppSettingKeys.PatientAutoCloseInactiveEnabled,
            AppSettingKeys.PatientAutoCloseInactiveMonths,
            AppSettingKeys.PatientAutoDeleteClosedEnabled,
            AppSettingKeys.PatientAutoDeleteClosedMonths
        };

        var rows = await _db.AppSettings.AsNoTracking()
            .Where(s => keys.Contains(s.Key))
            .ToListAsync(cancellationToken);

        string Val(string key) => rows.FirstOrDefault(s => s.Key == key)?.Value ?? string.Empty;

        return new PatientAccountLifecycleSettings
        {
            AutoCloseInactiveEnabled = ParseBoolSetting(Val(AppSettingKeys.PatientAutoCloseInactiveEnabled)),
            AutoCloseInactiveMonths = int.TryParse(Val(AppSettingKeys.PatientAutoCloseInactiveMonths), out var inactiveMonths)
                ? Math.Clamp(inactiveMonths, MinAutoCloseInactiveMonths, MaxAutoCloseInactiveMonths)
                : DefaultAutoCloseInactiveMonths,
            AutoDeleteClosedEnabled = ParseBoolSetting(Val(AppSettingKeys.PatientAutoDeleteClosedEnabled)),
            AutoDeleteClosedMonths = int.TryParse(Val(AppSettingKeys.PatientAutoDeleteClosedMonths), out var closedMonths)
                ? Math.Clamp(closedMonths, MinAutoDeleteClosedMonths, MaxAutoDeleteClosedMonths)
                : DefaultAutoDeleteClosedMonths
        };
    }

    public async Task<(bool Success, string? Error)> SavePatientAccountLifecycleSettingsAsync(
        PatientAccountLifecycleSettings settings,
        CancellationToken cancellationToken = default)
    {
        var inactiveMonths = Math.Clamp(settings.AutoCloseInactiveMonths, MinAutoCloseInactiveMonths, MaxAutoCloseInactiveMonths);
        var closedMonths = Math.Clamp(settings.AutoDeleteClosedMonths, MinAutoDeleteClosedMonths, MaxAutoDeleteClosedMonths);

        await SetValueAsync(AppSettingKeys.PatientAutoCloseInactiveEnabled, settings.AutoCloseInactiveEnabled ? "true" : "false", cancellationToken);
        await SetValueAsync(AppSettingKeys.PatientAutoCloseInactiveMonths, inactiveMonths.ToString(), cancellationToken);
        await SetValueAsync(AppSettingKeys.PatientAutoDeleteClosedEnabled, settings.AutoDeleteClosedEnabled ? "true" : "false", cancellationToken);
        await SetValueAsync(AppSettingKeys.PatientAutoDeleteClosedMonths, closedMonths.ToString(), cancellationToken);
        return (true, null);
    }

    public async Task<PatientNuviVerificationSettings> GetPatientNuviVerificationSettingsAsync(
        CancellationToken cancellationToken = default)
    {
        var keys = new[]
        {
            AppSettingKeys.EnableNuviEmailVerificationForNewPatients,
            AppSettingKeys.EnableNuviPhoneVerificationForNewPatients
        };

        var rows = await _db.AppSettings.AsNoTracking()
            .Where(s => keys.Contains(s.Key))
            .ToListAsync(cancellationToken);

        string Val(string key) => rows.FirstOrDefault(s => s.Key == key)?.Value ?? string.Empty;

        return new PatientNuviVerificationSettings
        {
            EnableEmailVerification = ParseBoolSetting(Val(AppSettingKeys.EnableNuviEmailVerificationForNewPatients)),
            EnablePhoneVerification = ParseBoolSetting(Val(AppSettingKeys.EnableNuviPhoneVerificationForNewPatients))
        };
    }

    public async Task<(bool Success, string? Error)> SavePatientNuviVerificationSettingsAsync(
        PatientNuviVerificationSettings settings,
        CancellationToken cancellationToken = default)
    {
        await SetValueAsync(
            AppSettingKeys.EnableNuviEmailVerificationForNewPatients,
            settings.EnableEmailVerification ? "true" : "false",
            cancellationToken);
        await SetValueAsync(
            AppSettingKeys.EnableNuviPhoneVerificationForNewPatients,
            settings.EnablePhoneVerification ? "true" : "false",
            cancellationToken);
        return (true, null);
    }

    private static bool ParseBoolSetting(string? value, bool defaultValue = false) =>
        string.IsNullOrWhiteSpace(value)
            ? defaultValue
            : bool.TryParse(value, out var parsed)
                ? parsed
                : string.Equals(value, "1", StringComparison.Ordinal);

    public async Task SaveDoctorBillingDefaultsAsync(
        decimal perVisitFeeUsd,
        int freeVisitCount,
        bool chargeOnlyIfPatientShowed,
        CancellationToken cancellationToken = default)
    {
        var cents = perVisitFeeUsd < 0
            ? 0
            : (int)Math.Round(perVisitFeeUsd * 100m, MidpointRounding.AwayFromZero);
        var visits = Math.Clamp(freeVisitCount, 0, 10_000);
        await SetValueAsync(AppSettingKeys.DefaultPerVisitFeeCents, cents.ToString(), cancellationToken);
        await SetValueAsync(AppSettingKeys.FreeVisitCount, visits.ToString(), cancellationToken);
        await SetValueAsync(
            AppSettingKeys.VisitBillingChargeOnlyIfPatientShowed,
            chargeOnlyIfPatientShowed ? "true" : "false",
            cancellationToken);
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
