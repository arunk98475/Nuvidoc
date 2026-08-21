using Docovee.BLL.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Docovee.Pages.Account;

[AllowAnonymous]
public class VerifyEmailModel : PageModel
{
    private readonly IPatientEmailAuthService _patientEmailAuth;
    private readonly IDoctorAccountService _doctorAccount;

    public VerifyEmailModel(IPatientEmailAuthService patientEmailAuth, IDoctorAccountService doctorAccount)
    {
        _patientEmailAuth = patientEmailAuth;
        _doctorAccount = doctorAccount;
    }

    public string? Message { get; set; }
    public bool Success { get; set; }
    public bool IsDoctor { get; set; }

    public async Task<IActionResult> OnGetAsync(string? token, string? kind = null)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            Success = false;
            Message = "Missing verification token.";
            return Page();
        }

        IsDoctor = string.Equals(kind, "doctor", StringComparison.OrdinalIgnoreCase);
        if (IsDoctor)
        {
            var doctorResult = await _doctorAccount.ConfirmEmailVerificationAsync(token);
            Success = doctorResult.Success;
            Message = doctorResult.Message;
            return Page();
        }

        var patientResult = await _patientEmailAuth.ConfirmEmailVerificationAsync(token);
        if (patientResult.Success)
        {
            Success = true;
            Message = patientResult.Message;
            return Page();
        }

        // Fallback: doctor links without kind=doctor still resolve.
        var fallback = await _doctorAccount.ConfirmEmailVerificationAsync(token);
        if (fallback.Success)
        {
            IsDoctor = true;
            Success = true;
            Message = fallback.Message;
            return Page();
        }

        Success = false;
        Message = patientResult.Message;
        return Page();
    }
}
