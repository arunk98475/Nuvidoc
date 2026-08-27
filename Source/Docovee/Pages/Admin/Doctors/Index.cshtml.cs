using Docovee.DS.Models;
using Docovee.DS.Enums;
using Docovee.BLL.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Routing;

namespace Docovee.Pages.Admin.Doctors;

public class IndexModel : PageModel
{
    private readonly IAdminDoctorService _doctorService;
    private readonly IAppSettingsService _appSettings;

    public IndexModel(IAdminDoctorService doctorService, IAppSettingsService appSettings)
    {
        _doctorService = doctorService;
        _appSettings = appSettings;
    }

    public PagedResult<DoctorAdminDto> Results { get; set; } = new();
    public IReadOnlyList<string> SpecialtyOptions { get; set; } = Array.Empty<string>();
    public bool BillingDefaultsSaved { get; private set; }
    public string? BillingDefaultsError { get; private set; }
    public bool SponsorshipSettingsSaved { get; private set; }
    public string? SponsorshipSettingsError { get; private set; }

    [BindProperty(SupportsGet = true)]
    public string? Search { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Location { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Specialty { get; set; }

    [BindProperty(SupportsGet = true)]
    public decimal? MinRating { get; set; }

    [BindProperty(SupportsGet = true)]
    public int? MinPatientReviews { get; set; }

    [BindProperty(SupportsGet = true)]
    public int? FreeVisitsRemaining { get; set; }

    /// <summary>Empty = any, yes = verified, no = not verified.</summary>
    [BindProperty(SupportsGet = true)]
    public string? PaymentVerified { get; set; }

    [BindProperty(SupportsGet = true)]
    public int PageNum { get; set; } = 1;

    [BindProperty]
    public decimal DefaultPerVisitFeeUsd { get; set; }

    [BindProperty]
    public int FreeVisitCount { get; set; }

    [BindProperty]
    public bool VisitBillingChargeOnlyIfPatientShowed { get; set; } = true;

    [BindProperty]
    public int MinQualityScoreForSponsorship { get; set; }

    [BindProperty]
    public int MinGoogleReviewCountForSponsorship { get; set; }

    [BindProperty]
    public decimal SponsorshipBillingAmountUsd { get; set; }

    [BindProperty]
    public SponsorshipBillingInterval SponsorshipBillingInterval { get; set; } = SponsorshipBillingInterval.Monthly;

    [BindProperty]
    public int SponsorshipBillingCustomDays { get; set; } = 30;

    [BindProperty]
    public bool SponsorshipBillingChargeOnlyIfPatientShowed { get; set; }

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        SpecialtyOptions = await _doctorService.GetSpecialtyOptionsAsync(cancellationToken);
        Results = await _doctorService.ListAsync(BuildFilters(), PageNum, 20, cancellationToken);
        await LoadAdminDoctorSettingsAsync(cancellationToken);
    }

    public async Task<IActionResult> OnPostUpdateBillingDefaultsAsync(CancellationToken cancellationToken)
    {
        SpecialtyOptions = await _doctorService.GetSpecialtyOptionsAsync(cancellationToken);
        Results = await _doctorService.ListAsync(BuildFilters(), PageNum, 20, cancellationToken);

        if (DefaultPerVisitFeeUsd < 0)
        {
            BillingDefaultsError = "Per-visit fee cannot be negative.";
            await LoadSponsorshipFieldsAsync(cancellationToken);
            return Page();
        }

        if (FreeVisitCount < 0)
        {
            BillingDefaultsError = "Number of free visits cannot be negative.";
            await LoadSponsorshipFieldsAsync(cancellationToken);
            return Page();
        }

        await _appSettings.SaveDoctorBillingDefaultsAsync(
            DefaultPerVisitFeeUsd,
            FreeVisitCount,
            VisitBillingChargeOnlyIfPatientShowed,
            cancellationToken);
        BillingDefaultsSaved = true;
        await LoadAdminDoctorSettingsAsync(cancellationToken);
        return Page();
    }

    public async Task<IActionResult> OnPostUpdateSponsorshipSettingsAsync(CancellationToken cancellationToken)
    {
        SpecialtyOptions = await _doctorService.GetSpecialtyOptionsAsync(cancellationToken);
        Results = await _doctorService.ListAsync(BuildFilters(), PageNum, 20, cancellationToken);

        if (MinQualityScoreForSponsorship < 0 || MinQualityScoreForSponsorship > 100)
        {
            SponsorshipSettingsError = "Minimum quality score must be between 0 and 100.";
            await LoadPageStateForSponsorshipErrorAsync(cancellationToken);
            return Page();
        }

        if (MinGoogleReviewCountForSponsorship < 0)
        {
            SponsorshipSettingsError = "Minimum Google review count cannot be negative.";
            await LoadPageStateForSponsorshipErrorAsync(cancellationToken);
            return Page();
        }

        if (SponsorshipBillingAmountUsd < 0)
        {
            SponsorshipSettingsError = "Sponsorship billing amount cannot be negative.";
            await LoadPageStateForSponsorshipErrorAsync(cancellationToken);
            return Page();
        }

        if (!Enum.IsDefined(SponsorshipBillingInterval))
        {
            SponsorshipSettingsError = "Select a valid sponsorship billing interval.";
            await LoadPageStateForSponsorshipErrorAsync(cancellationToken);
            return Page();
        }

        if (SponsorshipBillingInterval == SponsorshipBillingInterval.CustomDays
            && (SponsorshipBillingCustomDays < 1 || SponsorshipBillingCustomDays > 365))
        {
            SponsorshipSettingsError = "Custom sponsorship billing days must be between 1 and 365.";
            await LoadPageStateForSponsorshipErrorAsync(cancellationToken);
            return Page();
        }

        var settings = new SponsorshipAdminSettings
        {
            MinQualityScoreForSponsorship = MinQualityScoreForSponsorship,
            MinGoogleReviewCountForSponsorship = MinGoogleReviewCountForSponsorship,
            Billing = new SponsorshipBillingSettings
            {
                AmountCents = (int)Math.Round(Math.Max(0, SponsorshipBillingAmountUsd) * 100m, MidpointRounding.AwayFromZero),
                Interval = SponsorshipBillingInterval,
                CustomDays = Math.Clamp(SponsorshipBillingCustomDays, 1, 365),
                ChargeOnlyIfPatientShowed = SponsorshipBillingInterval == SponsorshipBillingInterval.PerBooking
                    && SponsorshipBillingChargeOnlyIfPatientShowed
            }
        };

        await _appSettings.SaveSponsorshipAdminSettingsAsync(settings, cancellationToken);
        SponsorshipSettingsSaved = true;
        await LoadAdminDoctorSettingsAsync(cancellationToken);
        return Page();
    }

    public RouteValueDictionary BuildRouteValues(int? pageNum = null)
    {
        var values = new RouteValueDictionary
        {
            ["Search"] = Search,
            ["Location"] = Location,
            ["Specialty"] = Specialty,
            ["MinRating"] = MinRating,
            ["MinPatientReviews"] = MinPatientReviews,
            ["FreeVisitsRemaining"] = FreeVisitsRemaining,
            ["PaymentVerified"] = PaymentVerified,
            ["PageNum"] = pageNum ?? PageNum
        };

        foreach (var key in values.Keys.ToList())
        {
            if (values[key] == null || (values[key] is string s && string.IsNullOrWhiteSpace(s)))
                values.Remove(key);
        }

        return values;
    }

    private DoctorAdminListFilters BuildFilters() => new()
    {
        Search = Search,
        Location = Location,
        Specialty = Specialty,
        MinRating = MinRating,
        MinPatientReviews = MinPatientReviews,
        FreeVisitsRemaining = FreeVisitsRemaining,
        PaymentVerified = ParsePaymentVerifiedFilter(PaymentVerified)
    };

    private static bool? ParsePaymentVerifiedFilter(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        return value.Trim().ToLowerInvariant() switch
        {
            "yes" or "true" or "1" => true,
            "no" or "false" or "0" => false,
            _ => null
        };
    }

    private async Task LoadAdminDoctorSettingsAsync(CancellationToken cancellationToken)
    {
        await LoadBillingDefaultsAsync(cancellationToken);
        await LoadSponsorshipFieldsAsync(cancellationToken);
    }

    private async Task LoadSponsorshipFieldsAsync(CancellationToken cancellationToken)
    {
        var sponsorship = await _appSettings.GetSponsorshipAdminSettingsAsync(cancellationToken);
        MinQualityScoreForSponsorship = sponsorship.MinQualityScoreForSponsorship;
        MinGoogleReviewCountForSponsorship = sponsorship.MinGoogleReviewCountForSponsorship;
        SponsorshipBillingAmountUsd = sponsorship.Billing.AmountUsd;
        SponsorshipBillingInterval = sponsorship.Billing.Interval;
        SponsorshipBillingCustomDays = sponsorship.Billing.CustomDays;
        SponsorshipBillingChargeOnlyIfPatientShowed = sponsorship.Billing.ChargeOnlyIfPatientShowed;
    }

    private async Task LoadBillingDefaultsAsync(CancellationToken cancellationToken)
    {
        var cents = await _appSettings.GetDefaultPerVisitFeeCentsAsync(cancellationToken);
        DefaultPerVisitFeeUsd = Math.Max(0, cents) / 100m;
        FreeVisitCount = await _appSettings.GetFreeVisitCountAsync(cancellationToken);
        VisitBillingChargeOnlyIfPatientShowed = await _appSettings.GetVisitBillingChargeOnlyIfPatientShowedAsync(cancellationToken);
    }

    private async Task LoadPageStateForSponsorshipErrorAsync(CancellationToken cancellationToken)
    {
        await LoadBillingDefaultsAsync(cancellationToken);
    }
}
