using Docovee.BLL.Auth;
using Docovee.BLL.Models;
using Docovee.BLL.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Docovee.Pages.Account;

public class LoginModel : PageModel
{
    private readonly IAccountAuthService _auth;

    public LoginModel(IAccountAuthService auth) => _auth = auth;

    [BindProperty]
    public AccountLoginRequest Input { get; set; } = new();

    public string? ErrorMessage { get; set; }

    public IActionResult OnGet()
    {
        if (User.Identity?.IsAuthenticated == true)
            return Redirect(GetRedirectUrl());

        Input.AccountType = AccountType.Patient;
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (Input.AccountType == AccountType.Admin)
        {
            ErrorMessage = "Please use the correct sign-in page for your account type.";
            Input.AccountType = AccountType.Patient;
            return Page();
        }

        var (success, error) = await _auth.LoginAsync(Input, HttpContext);
        if (!success)
        {
            ErrorMessage = error;
            return Page();
        }

        return Redirect(GetRedirectForAccountType(Input.AccountType));
    }

    private string GetRedirectUrl()
    {
        if (User.IsInRole(AuthRoles.Admin)) return "/Admin/Patients";
        if (User.IsInRole(AuthRoles.Doctor)) return "/Account/DoctorProfile";
        if (User.IsInRole(AuthRoles.Patient)) return "/Account/Profile";
        return "/";
    }

    private static string GetRedirectForAccountType(AccountType accountType) => accountType switch
    {
        AccountType.Doctor => "/Account/DoctorProfile",
        _ => "/Account/Profile"
    };
}
