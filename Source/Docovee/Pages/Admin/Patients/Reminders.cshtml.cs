using Docovee.BLL.Services;
using Docovee.DS;
using Docovee.DS.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Docovee.Pages.Admin.Patients;

public class RemindersModel : PageModel
{
    private const int RecentSendLimit = 25;

    private readonly IAppSettingsService _settings;
    private readonly DocoveeDbContext _db;

    public RemindersModel(IAppSettingsService settings, DocoveeDbContext db)
    {
        _settings = settings;
        _db = db;
    }

    [BindProperty]
    public PatientBookingReminderSettings Input { get; set; } = new();

    public PatientBookingReminderRunStatus RunStatus { get; set; } = new();
    public IReadOnlyList<PatientNurtureSendAdminRow> RecentSends { get; set; } = Array.Empty<PatientNurtureSendAdminRow>();
    public int TotalSendCount { get; set; }

    public string? SuccessMessage { get; set; }
    public string? ErrorMessage { get; set; }

    public async Task OnGetAsync()
    {
        await LoadPageAsync();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        var (success, error) = await _settings.SavePatientBookingReminderSettingsAsync(Input);
        if (!success)
        {
            ErrorMessage = error ?? "Could not save booking reminder settings.";
            await LoadPageAsync();
            return Page();
        }

        SuccessMessage = "Booking reminder settings saved.";
        await LoadPageAsync();
        return Page();
    }

    private async Task LoadPageAsync()
    {
        Input = await _settings.GetPatientBookingReminderSettingsAsync();
        RunStatus = await _settings.GetPatientBookingReminderRunStatusAsync();
        TotalSendCount = await _db.PatientNurtureSends.AsNoTracking().CountAsync();
        RecentSends = await _db.PatientNurtureSends
            .AsNoTracking()
            .OrderByDescending(s => s.SentAtUtc)
            .Take(RecentSendLimit)
            .Select(s => new PatientNurtureSendAdminRow
            {
                PatientId = s.PatientId,
                PatientName = s.Patient.FullName,
                PatientUsername = s.Patient.Username,
                StepDay = s.StepDay,
                Channel = s.Channel,
                SentAtUtc = s.SentAtUtc
            })
            .ToListAsync();
    }
}
