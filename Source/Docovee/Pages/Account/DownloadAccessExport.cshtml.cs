using Docovee.BLL.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Docovee.Pages.Account;

[AllowAnonymous]
public class DownloadAccessExportModel : PageModel
{
    private readonly IPatientPrivacyRightsService _privacyRights;

    public DownloadAccessExportModel(IPatientPrivacyRightsService privacyRights)
    {
        _privacyRights = privacyRights;
    }

    public string? ErrorMessage { get; set; }

    public async Task<IActionResult> OnGetAsync(string? token)
    {
        var (bytes, fileName, error) = await _privacyRights.GetExportByDownloadTokenAsync(token ?? "");
        if (bytes == null || fileName == null)
        {
            ErrorMessage = error ?? "Download failed.";
            return Page();
        }

        return File(bytes, "application/json", fileName);
    }
}
