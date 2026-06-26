using Docovee.BLL.Models;
using Docovee.BLL.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Docovee.Pages.Admin.Patients;

public class CreateModel : PageModel
{
    private readonly IAdminPatientService _patientService;

    public CreateModel(IAdminPatientService patientService) => _patientService = patientService;

    [BindProperty]
    public PatientAdminEditModel Input { get; set; } = new();

    public string? ErrorMessage { get; set; }

    public void OnGet() { }

    public async Task<IActionResult> OnPostAsync()
    {
        var (success, error) = await _patientService.CreateAsync(Input);
        if (!success)
        {
            ErrorMessage = error;
            return Page();
        }

        return RedirectToPage("Index");
    }
}
