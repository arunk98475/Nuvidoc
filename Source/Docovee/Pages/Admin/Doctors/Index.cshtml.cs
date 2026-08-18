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

    [BindProperty(SupportsGet = true)]
    public string? Search { get; set; }

    [BindProperty(SupportsGet = true)]
    public int PageNum { get; set; } = 1;

    [BindProperty]
    public decimal DefaultPerVisitFeeUsd { get; set; }

    [BindProperty]
    public int FreeVisitCount { get; set; }

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        Results = await _doctorService.ListAsync(PageNum, 20, Search, cancellationToken);
        await LoadBillingDefaultsAsync(cancellationToken);
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
            return Page();
        }

        if (FreeVisitCount < 0)
        {
            BillingDefaultsError = "Number of free visits cannot be negative.";
            return Page();
        }

        await _appSettings.SaveDoctorBillingDefaultsAsync(DefaultPerVisitFeeUsd, FreeVisitCount, cancellationToken);
        BillingDefaultsSaved = true;
        await LoadBillingDefaultsAsync(cancellationToken);
        return Page();
    }

    private async Task LoadBillingDefaultsAsync(CancellationToken cancellationToken)
    {
        var cents = await _appSettings.GetDefaultPerVisitFeeCentsAsync(cancellationToken);
        DefaultPerVisitFeeUsd = Math.Max(0, cents) / 100m;
        FreeVisitCount = await _appSettings.GetFreeVisitCountAsync(cancellationToken);
    }
}
