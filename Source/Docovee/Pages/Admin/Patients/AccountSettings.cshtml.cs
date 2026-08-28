using Docovee.BLL.Services;
using Docovee.DS.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Docovee.Pages.Admin.Patients;

public class AccountSettingsModel : PageModel
{
    private readonly IAppSettingsService _settings;

    public AccountSettingsModel(IAppSettingsService settings) => _settings = settings;

    [BindProperty]
    public PatientAccountLifecycleSettings Input { get; set; } = new();

    public string? SuccessMessage { get; set; }
    public string? ErrorMessage { get; set; }

    public async Task OnGetAsync()
    {
        Input = await _settings.GetPatientAccountLifecycleSettingsAsync();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        var (success, error) = await _settings.SavePatientAccountLifecycleSettingsAsync(Input);
        if (!success)
        {
            ErrorMessage = error ?? "Could not save account settings.";
            return Page();
        }

        SuccessMessage = "Patient account settings saved.";
        Input = await _settings.GetPatientAccountLifecycleSettingsAsync();
        return Page();
    }
}
