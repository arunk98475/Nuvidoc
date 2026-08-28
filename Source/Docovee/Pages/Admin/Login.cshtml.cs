using Docovee.BLL.Auth;
using Docovee.DS.Models;
using Docovee.BLL.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;

namespace Docovee.Pages.Admin;

[EnableRateLimiting("auth")]
public class LoginModel : PageModel
{
    private readonly IAdminAuthService _adminAuth;

    public LoginModel(IAdminAuthService adminAuth) => _adminAuth = adminAuth;

    [BindProperty]
    public AdminLoginRequest Input { get; set; } = new();

    [BindProperty]
    public string? OtpCode { get; set; }

    [BindProperty]
    public string? OtpSessionToken { get; set; }

    public string? ErrorMessage { get; set; }
    public string? InfoMessage { get; set; }
    public bool ShowOtpStep { get; set; }

    public IActionResult OnGet()
    {
        if (User.Identity?.IsAuthenticated == true && User.IsInRole(AuthRoles.Admin))
            return RedirectToPage("/Admin/Dashboard/Index");

        return Redirect("/Account/Admin");
    }

    public async Task<IActionResult> OnPostAsync()
    {
        var result = await _adminAuth.StartLoginAsync(Input, HttpContext);
        if (!result.Success)
        {
            ErrorMessage = result.Error;
            return Page();
        }

        if (result.RequiresOtp)
        {
            ShowOtpStep = true;
            OtpSessionToken = result.OtpSessionToken;
            InfoMessage = result.OtpMessage;
            return Page();
        }

        return RedirectToPage("/Admin/Dashboard/Index");
    }

    [EnableRateLimiting("phoneVerify")]
    public async Task<IActionResult> OnPostVerifyOtpAsync()
    {
        ShowOtpStep = true;
        var result = await _adminAuth.CompleteLoginAsync(OtpSessionToken ?? "", OtpCode ?? "", HttpContext);
        if (!result.Success)
        {
            ErrorMessage = result.Error;
            return Page();
        }

        return RedirectToPage("/Admin/Dashboard/Index");
    }

    [EnableRateLimiting("phoneVerify")]
    public async Task<IActionResult> OnPostResendOtpAsync()
    {
        ShowOtpStep = true;
        var result = await _adminAuth.ResendOtpAsync(OtpSessionToken ?? "");
        if (!result.Success)
        {
            ErrorMessage = result.Error;
            return Page();
        }

        OtpSessionToken = result.OtpSessionToken;
        InfoMessage = result.OtpMessage;
        return Page();
    }
}
