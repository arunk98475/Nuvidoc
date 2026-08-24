using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Docovee.Pages.Admin.DoctorLanguages;

public class EditRedirectModel : PageModel
{
    public IActionResult OnGet(int id) => RedirectPermanent($"/Admin/Doctors/Languages/Edit/{id}");
}
