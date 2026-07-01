using Docovee.DS.Models;
using Docovee.BLL.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Docovee.Pages.Admin.Patients;

public class EditModel : PageModel
{
    private readonly IAdminPatientService _patientService;

    public EditModel(IAdminPatientService patientService) => _patientService = patientService;

    [BindProperty]
    public PatientAdminEditModel Input { get; set; } = new();

    public string? ErrorMessage { get; set; }

    public async Task<IActionResult> OnGetAsync(int id)
    {
        var patient = await _patientService.GetForEditAsync(id);
        if (patient == null)
            return RedirectToPage("Index");

        Input = patient;
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        var (success, error) = await _patientService.UpdateAsync(Input);
        if (!success)
        {
            ErrorMessage = error;
            return Page();
        }

        return RedirectToPage("Index");
    }
}
