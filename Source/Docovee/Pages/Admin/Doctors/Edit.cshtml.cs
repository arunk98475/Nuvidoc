using Docovee.DS.Models;
using Docovee.BLL.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Docovee.Pages.Admin.Doctors;

public class EditModel : PageModel
{
    private readonly IAdminDoctorService _doctorService;

    public EditModel(IAdminDoctorService doctorService) => _doctorService = doctorService;

    [BindProperty]
    public DoctorAdminEditModel Input { get; set; } = new();

    public string? ErrorMessage { get; set; }

    public async Task<IActionResult> OnGetAsync(int id)
    {
        var doctor = await _doctorService.GetForEditAsync(id);
        if (doctor == null) return NotFound();
        Input = doctor;
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(int id, IFormFile? photo)
    {
        if (Input.Id == 0)
            Input.Id = id;

        var (success, error) = await _doctorService.UpdateAsync(Input, photo);
        if (!success)
        {
            ErrorMessage = error;
            return Page();
        }
        return RedirectToPage("Index");
    }
}
