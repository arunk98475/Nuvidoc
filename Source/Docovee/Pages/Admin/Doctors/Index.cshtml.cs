using Docovee.DS.Models;
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
    public int MinQualityScoreForSponsorship { get; set; }

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
            MinQualityScoreForSponsorship = await _appSettings.GetMinQualityScoreForSponsorshipAsync(cancellationToken);
            return Page();
        }

        if (FreeVisitCount < 0)
        {
            BillingDefaultsError = "Number of free visits cannot be negative.";
            MinQualityScoreForSponsorship = await _appSettings.GetMinQualityScoreForSponsorshipAsync(cancellationToken);
            return Page();
        }

        await _appSettings.SaveDoctorBillingDefaultsAsync(DefaultPerVisitFeeUsd, FreeVisitCount, cancellationToken);
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
            await LoadBillingDefaultsAsync(cancellationToken);
            return Page();
        }

        await _appSettings.SaveMinQualityScoreForSponsorshipAsync(MinQualityScoreForSponsorship, cancellationToken);
        SponsorshipSettingsSaved = true;
        await LoadAdminDoctorSettingsAsync(cancellationToken);
        return Page();
    }

    private async Task LoadAdminDoctorSettingsAsync(CancellationToken cancellationToken)
    {
        await LoadBillingDefaultsAsync(cancellationToken);
        MinQualityScoreForSponsorship = await _appSettings.GetMinQualityScoreForSponsorshipAsync(cancellationToken);
    }

    private async Task LoadBillingDefaultsAsync(CancellationToken cancellationToken)
    {
        var cents = await _appSettings.GetDefaultPerVisitFeeCentsAsync(cancellationToken);
        DefaultPerVisitFeeUsd = Math.Max(0, cents) / 100m;
        FreeVisitCount = await _appSettings.GetFreeVisitCountAsync(cancellationToken);
    }
}
