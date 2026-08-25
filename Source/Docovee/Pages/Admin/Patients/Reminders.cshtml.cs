using Docovee.BLL.Services;
using Docovee.DS.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Docovee.Pages.Admin.Patients;

public class RemindersModel : PageModel
{
    private readonly IAppSettingsService _settings;

    public RemindersModel(IAppSettingsService settings) => _settings = settings;

    [BindProperty]
    public PatientBookingReminderSettings Input { get; set; } = new();

    public string? SuccessMessage { get; set; }
    public string? ErrorMessage { get; set; }

    public async Task OnGetAsync()
    {
        Input = await _settings.GetPatientBookingReminderSettingsAsync();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        var (success, error) = await _settings.SavePatientBookingReminderSettingsAsync(Input);
        if (!success)
        {
            ErrorMessage = error ?? "Could not save booking reminder settings.";
            return Page();
        }

        SuccessMessage = "Booking reminder settings saved.";
        Input = await _settings.GetPatientBookingReminderSettingsAsync();
        return Page();
    }
}
