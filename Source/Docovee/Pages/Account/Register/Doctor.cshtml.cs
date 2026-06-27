using Docovee.BLL.Auth;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Docovee.Pages.Account.Register;

public class DoctorModel : PageModel
{
    public IActionResult OnGet()
    {
        if (User.Identity?.IsAuthenticated == true)
        {
            if (User.IsInRole(AuthRoles.Doctor))
                return Redirect("/Account/DoctorProfile");
            return Redirect("/");
        }
        return Page();
    }
}
