using Docovee.DS.Models;
using Docovee.BLL.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Docovee.Pages.Admin.DoctorLanguages;

public class EditModel : PageModel
{
    private readonly IDoctorLanguageService _languageService;

    public EditModel(IDoctorLanguageService languageService) => _languageService = languageService;

    [BindProperty]
    public DoctorLanguageEditModel Input { get; set; } = new();

    public string? ErrorMessage { get; set; }

    public async Task<IActionResult> OnGetAsync(int id)
    {
        var model = await _languageService.GetForEditAsync(id);
        if (model == null) return NotFound();
        Input = model;
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        var (success, error) = await _languageService.UpdateAsync(Input);
        if (!success)
        {
            ErrorMessage = error;
            return Page();
        }
        return RedirectToPage("Index");
    }
}
