using Docovee.DS.Models;
using Docovee.BLL.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Docovee.Pages.Admin.DoctorLanguages;

public class CreateModel : PageModel
{
    private readonly IDoctorLanguageService _languageService;

    public CreateModel(IDoctorLanguageService languageService) => _languageService = languageService;

    [BindProperty]
    public DoctorLanguageEditModel Input { get; set; } = new();

    public string? ErrorMessage { get; set; }

    public void OnGet() { }

    public async Task<IActionResult> OnPostAsync()
    {
        var (success, error) = await _languageService.CreateAsync(Input);
        if (!success)
        {
            ErrorMessage = error;
            return Page();
        }
        return RedirectToPage("Index");
    }
}
