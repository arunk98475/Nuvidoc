using Docovee.BLL.Auth;
using Docovee.DS.Models;
using Docovee.BLL.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Docovee.Pages.Account.Admin;

public class IndexModel : PageModel
{
    private readonly IAccountAuthService _auth;

    public IndexModel(IAccountAuthService auth) => _auth = auth;

    [BindProperty]
    public AccountLoginRequest Input { get; set; } = new() { AccountType = AccountType.Admin };

    public string? ErrorMessage { get; set; }

    public IActionResult OnGet()
    {
        if (User.Identity?.IsAuthenticated == true && User.IsInRole(AuthRoles.Admin))
            return Redirect("/Admin/Patients");

        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        Input.AccountType = AccountType.Admin;

        var (success, error) = await _auth.LoginAsync(Input, HttpContext);
        if (!success)
        {
            ErrorMessage = error;
            return Page();
        }

        return Redirect("/Admin/Patients");
    }
}
