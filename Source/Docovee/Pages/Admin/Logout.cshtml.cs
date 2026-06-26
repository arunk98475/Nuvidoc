using Docovee.BLL.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Docovee.Pages.Admin;

public class LogoutModel : PageModel
{
    private readonly IAdminAuthService _adminAuth;

    public LogoutModel(IAdminAuthService adminAuth) => _adminAuth = adminAuth;

    public async Task<IActionResult> OnGetAsync()
    {
        await _adminAuth.LogoutAsync(HttpContext);
        return RedirectToPage("/Index");
    }
}
