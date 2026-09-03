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

    [BindProperty]
    public PatientNuviVerificationSettings Verification { get; set; } = new();

    public string? SuccessMessage { get; set; }
    public string? ErrorMessage { get; set; }
    public string? VerificationSuccessMessage { get; set; }
    public string? VerificationErrorMessage { get; set; }

    public async Task OnGetAsync()
    {
        Input = await _settings.GetPatientAccountLifecycleSettingsAsync();
        Verification = await _settings.GetPatientNuviVerificationSettingsAsync();
    }

    public async Task<IActionResult> OnPostLifecycleAsync()
    {
        Verification = await _settings.GetPatientNuviVerificationSettingsAsync();
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

    public async Task<IActionResult> OnPostVerificationAsync()
    {
        Input = await _settings.GetPatientAccountLifecycleSettingsAsync();
        var (success, error) = await _settings.SavePatientNuviVerificationSettingsAsync(Verification);
        if (!success)
        {
            VerificationErrorMessage = error ?? "Could not save verification settings.";
            return Page();
        }

        VerificationSuccessMessage = "Nuvi verification settings saved.";
        Verification = await _settings.GetPatientNuviVerificationSettingsAsync();
        return Page();
    }
}
