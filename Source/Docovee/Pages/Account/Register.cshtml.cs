using Docovee.BLL.Auth;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Docovee.Pages.Account;

public class RegisterModel : PageModel
{
    public IActionResult OnGet()
    {
        if (User.Identity?.IsAuthenticated == true)
            return Redirect(GetRedirectUrl());
        return Page();
    }

    private string GetRedirectUrl()
    {
        if (User.IsInRole(AuthRoles.Admin)) return "/Admin/Patients";
        if (User.IsInRole(AuthRoles.Doctor)) return "/Account/DoctorProfile";
        if (User.IsInRole(AuthRoles.Patient)) return "/Account/Profile";
        return "/";
    }
}
