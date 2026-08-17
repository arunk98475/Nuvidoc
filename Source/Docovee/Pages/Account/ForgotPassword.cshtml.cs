using System.ComponentModel.DataAnnotations;
using Docovee.BLL.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Docovee.Pages.Account;

[AllowAnonymous]
public class ForgotPasswordModel : PageModel
{
    private readonly IPatientEmailAuthService _emailAuth;

    public ForgotPasswordModel(IPatientEmailAuthService emailAuth) => _emailAuth = emailAuth;

    [BindProperty]
    public InputModel Input { get; set; } = new();

    public string? FormError { get; set; }
    public string? FormSuccess { get; set; }

    public class InputModel
    {
        [Required, EmailAddress]
        public string Email { get; set; } = string.Empty;
    }

    public void OnGet(string? email = null)
    {
        if (!string.IsNullOrWhiteSpace(email))
            Input.Email = email.Trim();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (string.IsNullOrWhiteSpace(Input.Email) || !Input.Email.Contains('@'))
        {
            FormError = "Enter a valid email address.";
            return Page();
        }

        var baseUrl = $"{Request.Scheme}://{Request.Host}";
        var result = await _emailAuth.SendPasswordResetAsync(Input.Email.Trim(), baseUrl);
        if (result.Success)
            FormSuccess = result.Message;
        else
            FormError = result.Message;
        return Page();
    }
}
