using Docovee.DS.Models;
using Docovee.BLL.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Docovee.Pages.Admin.Doctors;

public class CreateModel : PageModel
{
    private readonly IAdminDoctorService _doctorService;

    public CreateModel(IAdminDoctorService doctorService) => _doctorService = doctorService;

    [BindProperty]
    public DoctorAdminEditModel Input { get; set; } = new();

    public string? ErrorMessage { get; set; }

    public void OnGet() { }

    public async Task<IActionResult> OnPostAsync(IFormFile? photo)
    {
        var (success, error) = await _doctorService.CreateAsync(Input, photo);
        if (!success)
        {
            ErrorMessage = error;
            return Page();
        }
        return RedirectToPage("Index");
    }
}
