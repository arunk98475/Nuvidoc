using Docovee.BLL.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Docovee.Pages.Account;

public class LogoutModel : PageModel
{
    private readonly IAccountAuthService _auth;

    public LogoutModel(IAccountAuthService auth) => _auth = auth;

    public async Task<IActionResult> OnGetAsync()
    {
        await _auth.LogoutAsync(HttpContext);
        return RedirectToPage("/Index");
    }
}
