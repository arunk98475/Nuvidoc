using Docovee.BLL.Auth;
using Docovee.BLL.Models;
using Docovee.BLL.Services;
using Docovee.DS;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Docovee.Pages.Account;

public class LoginModel : PageModel
{
    private readonly IAccountAuthService _auth;
    private readonly DocoveeDbContext _db;

    public LoginModel(IAccountAuthService auth, DocoveeDbContext db)
    {
        _auth = auth;
        _db = db;
    }

    [BindProperty]
    public AccountLoginRequest Input { get; set; } = new();

    public string? ErrorMessage { get; set; }

    public async Task<IActionResult> OnGetAsync()
    {
        if (User.Identity?.IsAuthenticated == true)
            return Redirect(await GetRedirectUrlAsync());

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

        return Redirect(await GetRedirectForAccountTypeAsync(Input.AccountType));
    }

    private async Task<string> GetRedirectForAccountTypeAsync(AccountType accountType)
    {
        if (accountType == AccountType.Doctor)
        {
            var doctorIdClaim = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
            if (int.TryParse(doctorIdClaim, out var doctorId))
            {
                var doctor = await _db.Doctors.AsNoTracking().FirstOrDefaultAsync(d => d.Id == doctorId);
                if (doctor != null && !DoctorOnboardingProgress.IsOnboardingComplete(doctor))
                    return "/Account/Register/Doctor";
            }
            return "/Account/DoctorProfile";
        }

        return "/Account/Profile";
    }

    private async Task<string> GetRedirectUrlAsync()
    {
        if (User.IsInRole(AuthRoles.Admin)) return "/Admin/Patients";
        if (User.IsInRole(AuthRoles.Doctor))
            return await GetRedirectForAccountTypeAsync(AccountType.Doctor);
        if (User.IsInRole(AuthRoles.Patient)) return "/Account/Profile";
        return "/";
    }
}
