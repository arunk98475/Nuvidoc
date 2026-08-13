using Docovee.BLL.Auth;
using Docovee.DS.Models;
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

    /// <summary>When true, render the password step (e.g. after a failed login).</summary>
    public bool ShowPasswordStep { get; set; }

    public async Task<IActionResult> OnGetAsync(string? type = null)
    {
        if (User.Identity?.IsAuthenticated == true)
            return Redirect(await GetRedirectUrlAsync());

        Input.AccountType = ParseAccountType(type);

        // Patient login uses the homepage modal (Zocdoc-style popup).
        if (Input.AccountType == AccountType.Patient)
            return Redirect("/?login=patient");

        // This page is the doctor login flow.
        Input.AccountType = AccountType.Doctor;
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (Input.AccountType == AccountType.Admin)
        {
            ErrorMessage = "Please use the correct sign-in page for your account type.";
            return Redirect("/Account/Login?type=Doctor");
        }

        // Doctor login page always authenticates as Doctor.
        if (Input.AccountType != AccountType.Patient)
            Input.AccountType = AccountType.Doctor;

        var (success, error) = await _auth.LoginAsync(Input, HttpContext);
        if (!success)
        {
            if (Input.AccountType == AccountType.Patient)
            {
                var msg = Uri.EscapeDataString(error ?? "Invalid email or password.");
                var email = Uri.EscapeDataString(Input.Username ?? "");
                return Redirect($"/?login=patient&error={msg}&email={email}");
            }

            ErrorMessage = error;
            ShowPasswordStep = true;
            Input.Password = string.Empty;
            return Page();
        }

        return Redirect(await GetRedirectForAccountTypeAsync(Input.AccountType));
    }

    private static AccountType ParseAccountType(string? type)
    {
        if (string.Equals(type, "Doctor", StringComparison.OrdinalIgnoreCase)
            || string.Equals(type, "Dentists", StringComparison.OrdinalIgnoreCase)
            || string.Equals(type, "Dentist", StringComparison.OrdinalIgnoreCase))
            return AccountType.Doctor;

        return AccountType.Patient;
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
            return "/Doctor";
        }

        return "/";
    }

    private async Task<string> GetRedirectUrlAsync()
    {
        if (User.IsInRole(AuthRoles.Admin)) return "/Admin/Dashboard";
        if (User.IsInRole(AuthRoles.Doctor))
            return await GetRedirectForAccountTypeAsync(AccountType.Doctor);
        if (User.IsInRole(AuthRoles.Patient)) return "/";
        return "/";
    }
}