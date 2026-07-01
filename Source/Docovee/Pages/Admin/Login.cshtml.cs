using Docovee.BLL.Auth;
using Docovee.DS.Models;
using Docovee.BLL.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Docovee.Pages.Admin;

public class LoginModel : PageModel
{
    private readonly IAdminAuthService _adminAuth;

    public LoginModel(IAdminAuthService adminAuth) => _adminAuth = adminAuth;

    [BindProperty]
    public AdminLoginRequest Input { get; set; } = new();

    public string? ErrorMessage { get; set; }

    public IActionResult OnGet()
    {
        if (User.Identity?.IsAuthenticated == true && User.IsInRole(AuthRoles.Admin))
            return RedirectToPage("/Admin/Patients/Index");

        return Redirect("/Account/Admin");
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (string.IsNullOrWhiteSpace(Input.Username) || string.IsNullOrWhiteSpace(Input.Password))
        {
            ErrorMessage = "Username and password are required.";
            return Page();
        }

        var success = await _adminAuth.LoginAsync(Input, HttpContext);
        if (!success)
        {
            ErrorMessage = "Invalid username or password.";
            return Page();
        }

        return RedirectToPage("/Admin/Patients/Index");
    }
}
