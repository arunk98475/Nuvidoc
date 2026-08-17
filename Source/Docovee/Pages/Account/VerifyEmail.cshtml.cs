using Docovee.BLL.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Docovee.Pages.Account;

[AllowAnonymous]
public class VerifyEmailModel : PageModel
{
    private readonly IPatientEmailAuthService _emailAuth;

    public VerifyEmailModel(IPatientEmailAuthService emailAuth) => _emailAuth = emailAuth;

    public string? Message { get; set; }
    public bool Success { get; set; }

    public async Task<IActionResult> OnGetAsync(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            Success = false;
            Message = "Missing verification token.";
            return Page();
        }

        var result = await _emailAuth.ConfirmEmailVerificationAsync(token);
        Success = result.Success;
        Message = result.Message;
        return Page();
    }
}
