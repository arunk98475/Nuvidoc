using Docovee.DS.Models;
using Docovee.DS.Enums;
using Docovee.BLL.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

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
    public bool BillingDefaultsSaved { get; private set; }
    public string? BillingDefaultsError { get; private set; }
    public bool SponsorshipSettingsSaved { get; private set; }
    public string? SponsorshipSettingsError { get; private set; }

    [BindProperty(SupportsGet = true)]
    public string? Search { get; set; }

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
        Results = await _doctorService.ListAsync(PageNum, 20, Search, cancellationToken);
        await LoadAdminDoctorSettingsAsync(cancellationToken);
    }

    public async Task<IActionResult> OnPostDeleteAsync(int id)
    {
        await _doctorService.DeleteAsync(id);
        return RedirectToPage(new { Search, PageNum });
    }

    public async Task<IActionResult> OnPostUpdateBillingDefaultsAsync(CancellationToken cancellationToken)
    {
        Results = await _doctorService.ListAsync(PageNum, 20, Search, cancellationToken);

        if (DefaultPerVisitFeeUsd < 0)
        {
            BillingDefaultsError = "Per-visit fee cannot be negative.";
            var sponsorship = await _appSettings.GetSponsorshipAdminSettingsAsync(cancellationToken);
            MinQualityScoreForSponsorship = sponsorship.MinQualityScoreForSponsorship;
            MinGoogleReviewCountForSponsorship = sponsorship.MinGoogleReviewCountForSponsorship;
            SponsorshipBillingAmountUsd = sponsorship.Billing.AmountUsd;
            SponsorshipBillingInterval = sponsorship.Billing.Interval;
            SponsorshipBillingCustomDays = sponsorship.Billing.CustomDays;
            return Page();
        }

        if (FreeVisitCount < 0)
        {
            BillingDefaultsError = "Number of free visits cannot be negative.";
            var sponsorship = await _appSettings.GetSponsorshipAdminSettingsAsync(cancellationToken);
            MinQualityScoreForSponsorship = sponsorship.MinQualityScoreForSponsorship;
            MinGoogleReviewCountForSponsorship = sponsorship.MinGoogleReviewCountForSponsorship;
            SponsorshipBillingAmountUsd = sponsorship.Billing.AmountUsd;
            SponsorshipBillingInterval = sponsorship.Billing.Interval;
            SponsorshipBillingCustomDays = sponsorship.Billing.CustomDays;
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
        Results = await _doctorService.ListAsync(PageNum, 20, Search, cancellationToken);

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

    private async Task LoadAdminDoctorSettingsAsync(CancellationToken cancellationToken)
    {
        await LoadBillingDefaultsAsync(cancellationToken);
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
