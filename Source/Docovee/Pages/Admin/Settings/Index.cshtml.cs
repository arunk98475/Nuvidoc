using Docovee.BLL.Models;
using Docovee.BLL.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Docovee.Pages.Admin.Settings;

public class IndexModel : PageModel
{
    private readonly IAppSettingsService _settingsService;

    public IndexModel(IAppSettingsService settingsService) => _settingsService = settingsService;

    [BindProperty]
    public SiteSettingsModel Input { get; set; } = new();

    public string? SuccessMessage { get; set; }

    public async Task OnGetAsync()
    {
        Input = await _settingsService.GetSiteSettingsAsync();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        await _settingsService.SaveSiteSettingsAsync(Input);
        SuccessMessage = "Settings saved successfully.";
        Input = await _settingsService.GetSiteSettingsAsync();
        return Page();
    }
}
