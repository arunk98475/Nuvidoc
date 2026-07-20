using System.Security.Claims;
using Docovee.BLL.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Google;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Configuration;

namespace Docovee.Pages.Account;

public class ExternalLoginModel : PageModel
{
    public const string ExternalScheme = "External";

    private readonly IConfiguration _config;
    private readonly IAccountAuthService _auth;

    public ExternalLoginModel(IConfiguration config, IAccountAuthService auth)
    {
        _config = config;
        _auth = auth;
    }

    public string Provider { get; private set; } = "Social";
    public string Message { get; private set; } = "";

    public IActionResult OnGet(string? provider = null, string? returnUrl = null)
    {
        Provider = string.IsNullOrWhiteSpace(provider) ? "Social" : provider.Trim();
        var safeReturn = SanitizeReturnUrl(returnUrl);

        if (string.Equals(Provider, "Google", StringComparison.OrdinalIgnoreCase))
        {
            if (!IsGoogleConfigured())
            {
                Message = "Google sign-in is not configured yet. Add ClientId and ClientSecret under Authentication:Google in appsettings.Development.json (see Docs/oauth_google_apple_setup.md).";
                return Page();
            }

            var redirectUrl = Url.Page("/Account/ExternalLogin", pageHandler: "Callback", values: new { returnUrl = safeReturn })
                ?? "/Account/ExternalLogin?handler=Callback";
            var properties = new AuthenticationProperties { RedirectUri = redirectUrl };
            return Challenge(properties, GoogleDefaults.AuthenticationScheme);
        }

        if (string.Equals(Provider, "Apple", StringComparison.OrdinalIgnoreCase))
        {
            Message = "Apple sign-in is not wired yet. Add Apple credentials and follow Docs/oauth_google_apple_setup.md.";
            return Page();
        }

        Message = $"Unknown sign-in provider: {Provider}.";
        return Page();
    }

    public async Task<IActionResult> OnGetCallbackAsync(string? returnUrl = null, string? remoteError = null)
    {
        var safeReturn = SanitizeReturnUrl(returnUrl);

        if (!string.IsNullOrWhiteSpace(remoteError))
        {
            Message = $"Google sign-in failed: {remoteError}";
            Provider = "Google";
            return Page();
        }

        var result = await HttpContext.AuthenticateAsync(ExternalScheme);
        if (!result.Succeeded || result.Principal == null)
        {
            Message = "Google sign-in did not complete. Please try again.";
            Provider = "Google";
            return Page();
        }

        var email = result.Principal.FindFirstValue(ClaimTypes.Email)
            ?? result.Principal.FindFirstValue("email");
        var name = result.Principal.FindFirstValue(ClaimTypes.Name)
            ?? result.Principal.FindFirstValue("name");

        await HttpContext.SignOutAsync(ExternalScheme);

        var (success, error) = await _auth.SignInExternalPatientAsync(email ?? "", name, "Google", HttpContext);
        if (!success)
        {
            Message = error ?? "Unable to sign you in with Google.";
            Provider = "Google";
            return Page();
        }

        return LocalRedirect(safeReturn);
    }

    private bool IsGoogleConfigured() =>
        !string.IsNullOrWhiteSpace(_config["Authentication:Google:ClientId"])
        && !string.IsNullOrWhiteSpace(_config["Authentication:Google:ClientSecret"]);

    private string SanitizeReturnUrl(string? returnUrl)
    {
        if (!string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl))
            return returnUrl;
        return "/";
    }
}
