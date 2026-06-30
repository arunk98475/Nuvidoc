using Docovee.BLL.Models;
using Docovee.BLL.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Docovee.Pages.Admin.DoctorLanguages;

public class IndexModel : PageModel
{
    private readonly IDoctorLanguageService _languageService;

    public IndexModel(IDoctorLanguageService languageService) => _languageService = languageService;

    public IReadOnlyList<DoctorLanguageDto> Languages { get; set; } = Array.Empty<DoctorLanguageDto>();

    public async Task OnGetAsync() =>
        Languages = await _languageService.GetAllAsync();

    public async Task<IActionResult> OnPostDeleteAsync(int id)
    {
        await _languageService.DeleteAsync(id);
        return RedirectToPage();
    }
}
