using Docovee.BLL.Models;
using Docovee.BLL.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Docovee.Pages.Admin.Doctors;

public class IndexModel : PageModel
{
    private readonly IAdminDoctorService _doctorService;

    public IndexModel(IAdminDoctorService doctorService) => _doctorService = doctorService;

    public PagedResult<DoctorAdminDto> Results { get; set; } = new();

    [BindProperty(SupportsGet = true)]
    public string? Search { get; set; }

    [BindProperty(SupportsGet = true)]
    public int PageNum { get; set; } = 1;

    public async Task OnGetAsync() =>
        Results = await _doctorService.ListAsync(PageNum, 20, Search);

    public async Task<IActionResult> OnPostDeleteAsync(int id)
    {
        await _doctorService.DeleteAsync(id);
        return RedirectToPage(new { Search, PageNum });
    }
}
