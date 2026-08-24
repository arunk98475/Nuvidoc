using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Docovee.Pages.Admin.DoctorLanguages;

public class RedirectModel : PageModel
{
    public IActionResult OnGet() => RedirectPermanent("/Admin/Doctors/Languages");
}
