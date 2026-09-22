using Docovee.BLL.Services;
using Docovee.DS.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Docovee.Pages.Admin.Patients;

public class IndexModel : PageModel
{
    private readonly IAdminPatientService _patientService;
    private readonly IAppSettingsService _appSettings;
    private readonly IPatientSmsNurtureService _smsNurture;

    public IndexModel(
        IAdminPatientService patientService,
        IAppSettingsService appSettings,
        IPatientSmsNurtureService smsNurture)
    {
        _patientService = patientService;
        _appSettings = appSettings;
        _smsNurture = smsNurture;
    }

    [BindProperty(SupportsGet = true)]
    public string? Name { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Phone { get; set; }

    [BindProperty(SupportsGet = true)]
    public DateOnly? DateOfBirth { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? IssueKeyword { get; set; }

    [BindProperty(SupportsGet = true)]
    public int PageNum { get; set; } = 1;

    [BindProperty]
    public int FirstNurtureDaysAfterCreation { get; set; } = 30;

    public PagedResult<PatientAdminDto> Results { get; set; } = new();
    public string? SuccessMessage { get; set; }
    public string? ErrorMessage { get; set; }

    public async Task OnGetAsync()
    {
        await LoadAsync();
    }

    public async Task<IActionResult> OnPostSaveNurtureSettingsAsync()
    {
        var settings = await _appSettings.GetPatientBookingReminderSettingsAsync();
        settings.IntervalDays = FirstNurtureDaysAfterCreation;
        var (success, error) = await _appSettings.SavePatientBookingReminderSettingsAsync(settings);
        if (!success)
        {
            ErrorMessage = error ?? "Could not save nurture settings.";
            await LoadAsync();
            return Page();
        }

        SuccessMessage = "Nurture settings saved.";
        await LoadAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostStartNurtureAsync(int patientId)
    {
        var (success, message) = await _smsNurture.StartNurtureManuallyAsync(patientId);
        if (success)
            SuccessMessage = message;
        else
            ErrorMessage = message;

        await LoadAsync();
        return Page();
    }

    private async Task LoadAsync()
    {
        var settings = await _appSettings.GetPatientBookingReminderSettingsAsync();
        FirstNurtureDaysAfterCreation = settings.IntervalDays;

        Results = await _patientService.SearchAsync(new PatientSearchRequest
        {
            Name = Name,
            Phone = Phone,
            DateOfBirth = DateOfBirth,
            IssueKeyword = IssueKeyword,
            Page = PageNum,
            PageSize = 20
        });
    }
}
