using System.ComponentModel.DataAnnotations;
using Docovee.BLL.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Docovee.Pages.Account;

[AllowAnonymous]
public class ResetPasswordModel : PageModel
{
    private readonly IPatientEmailAuthService _emailAuth;

    public ResetPasswordModel(IPatientEmailAuthService emailAuth) => _emailAuth = emailAuth;

    [BindProperty(SupportsGet = true)]
    public string? Token { get; set; }

    [BindProperty]
    public InputModel Input { get; set; } = new();

    public string? FormError { get; set; }
    public string? FormSuccess { get; set; }
    public bool TokenValid { get; set; }

    public class InputModel
    {
        [Required, MinLength(6)]
        public string NewPassword { get; set; } = string.Empty;

        [Required]
        public string ConfirmPassword { get; set; } = string.Empty;
    }

    public async Task OnGetAsync()
    {
        TokenValid = !string.IsNullOrWhiteSpace(Token)
            && await _emailAuth.IsPasswordResetTokenValidAsync(Token);
        if (!TokenValid)
            FormError = "That reset link is invalid or has expired. Request a new one.";
    }

    public async Task<IActionResult> OnPostAsync()
    {
        TokenValid = !string.IsNullOrWhiteSpace(Token)
            && await _emailAuth.IsPasswordResetTokenValidAsync(Token);
        if (!TokenValid)
        {
            FormError = "That reset link is invalid or has expired. Request a new one.";
            return Page();
        }

        if (Input.NewPassword != Input.ConfirmPassword)
        {
            FormError = "New password and confirmation do not match.";
            return Page();
        }

        var result = await _emailAuth.ResetPasswordAsync(Token!, Input.NewPassword);
        if (result.Success)
        {
            FormSuccess = result.Message;
            TokenValid = false;
        }
        else
        {
            FormError = result.Message;
            TokenValid = await _emailAuth.IsPasswordResetTokenValidAsync(Token!);
        }

        return Page();
    }
}
