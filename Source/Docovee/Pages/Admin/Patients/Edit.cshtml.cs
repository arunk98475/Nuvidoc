using Docovee.BLL.Configuration;
using Docovee.BLL.Services;
using Docovee.DS.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;

namespace Docovee.Pages.Admin.Patients;

public class EditModel : PageModel
{
    private readonly IAdminPatientService _patientService;
    private readonly AccountOptions _account;

    public EditModel(IAdminPatientService patientService, IOptions<AccountOptions> account)
    {
        _patientService = patientService;
        _account = account.Value;
    }

    [BindProperty]
    public PatientAdminEditModel Input { get; set; } = new();

    public string? ErrorMessage { get; set; }
    public string? AccountMessage { get; set; }
    public bool AccountMessageSuccess { get; set; }
    public int HardDeleteWaitDays { get; private set; }
    public bool CanRemoveAccount { get; private set; }
    public DateTime? RemoveAvailableAtUtc { get; private set; }

    public async Task<IActionResult> OnGetAsync(int id)
    {
        var patient = await _patientService.GetForEditAsync(id);
        if (patient == null)
            return RedirectToPage("Index");

        Input = patient;
        LoadAccountDeletionState();
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        var (success, error) = await _patientService.UpdateAsync(Input);
        if (!success)
        {
            ErrorMessage = error;
            LoadAccountDeletionState();
            return Page();
        }

        return RedirectToPage("Index");
    }

    public async Task<IActionResult> OnPostCloseAccountAsync(int id)
    {
        var (success, error) = await _patientService.SoftDeleteAsync(id);
        if (!success)
        {
            AccountMessage = error ?? "Unable to close account.";
            AccountMessageSuccess = false;
            var patient = await _patientService.GetForEditAsync(id);
            if (patient == null)
                return RedirectToPage("Index");
            Input = patient;
            LoadAccountDeletionState();
            return Page();
        }

        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostActivateAccountAsync(int id)
    {
        var (success, error) = await _patientService.ActivateAsync(id);
        if (!success)
        {
            AccountMessage = error ?? "Unable to activate account.";
            AccountMessageSuccess = false;
            var patient = await _patientService.GetForEditAsync(id);
            if (patient == null)
                return RedirectToPage("Index");
            Input = patient;
            LoadAccountDeletionState();
            return Page();
        }

        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostRemoveAccountAsync(int id)
    {
        var (success, error) = await _patientService.HardDeleteAsync(id);
        if (!success)
        {
            AccountMessage = error ?? "Unable to remove account.";
            AccountMessageSuccess = false;
            var patient = await _patientService.GetForEditAsync(id);
            if (patient == null)
                return RedirectToPage("Index");
            Input = patient;
            LoadAccountDeletionState();
            return Page();
        }

        return RedirectToPage("Index");
    }

    private void LoadAccountDeletionState()
    {
        HardDeleteWaitDays = Math.Max(0, _account.HardDeleteWaitDays);
        CanRemoveAccount = DeletedAccountHelper.CanPermanentlyRemove(Input.DeletedAtUtc, HardDeleteWaitDays);
        RemoveAvailableAtUtc = DeletedAccountHelper.PermanentRemoveAvailableAtUtc(Input.DeletedAtUtc, HardDeleteWaitDays);
    }
}
