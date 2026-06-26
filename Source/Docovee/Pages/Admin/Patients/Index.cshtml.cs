using Docovee.BLL.Models;
using Docovee.BLL.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Docovee.Pages.Admin.Patients;

public class IndexModel : PageModel
{
    private readonly IAdminPatientService _patientService;

    public IndexModel(IAdminPatientService patientService) => _patientService = patientService;

    [BindProperty(SupportsGet = true)]
    public string? Name { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Phone { get; set; }

    [BindProperty(SupportsGet = true)]
    public DateOnly? DateOfBirth { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? IssueKeyword { get; set; }

    [BindProperty(SupportsGet = true)]
    public int PageNum { get; set; } = 1;

    public PagedResult<PatientAdminDto> Results { get; set; } = new();

    public async Task OnGetAsync()
    {
        Results = await _patientService.SearchAsync(new PatientSearchRequest
        {
            Name = Name,
            Phone = Phone,
            DateOfBirth = DateOfBirth,
            IssueKeyword = IssueKeyword,
            Page = PageNum,
            PageSize = 20
        });
    }

    public async Task<IActionResult> OnPostDeleteAsync(int id)
    {
        await _patientService.DeleteAsync(id);
        return RedirectToPage(new { Name, Phone, DateOfBirth, IssueKeyword, PageNum });
    }
}
