using Docovee.BLL.Auth;
using Docovee.BLL.Models;
using Docovee.BLL.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.RazorPages;
using UsStateList = Docovee.BLL.Data.UsStates;

namespace Docovee.Pages.Account;

public class RegisterModel : PageModel
{
    private readonly IAccountRegistrationService _registration;
    private readonly IAccountAuthService _auth;
    private readonly IInsuranceService _insuranceService;

    public RegisterModel(
        IAccountRegistrationService registration,
        IAccountAuthService auth,
        IInsuranceService insuranceService)
    {
        _registration = registration;
        _auth = auth;
        _insuranceService = insuranceService;
    }

    [BindProperty]
    public AccountRegisterRequest Input { get; set; } = new();

    [BindNever]
    public string? ErrorMessage { get; set; }

    public IReadOnlyList<InsuranceCarrierDto> InsuranceCarriers { get; set; } = Array.Empty<InsuranceCarrierDto>();
    public IReadOnlyList<(string Code, string Name)> UsStates => UsStateList.All;

    public async Task<IActionResult> OnGetAsync()
    {
        if (User.Identity?.IsAuthenticated == true)
            return Redirect(GetRedirectUrl());

        await LoadLookupsAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(IFormFile? DoctorPhoto)
    {
        await LoadLookupsAsync();

        if (!ModelState.IsValid)
        {
            ErrorMessage = GetModelStateError() ?? "Please check your entries and try again.";
            return Page();
        }

        if (Input.AccountType == AccountType.Admin)
        {
            ErrorMessage = "Admin accounts cannot be created here.";
            Input.AccountType = AccountType.Patient;
            return Page();
        }

        var result = await _registration.RegisterAsync(Input, DoctorPhoto);
        if (!result.Success)
        {
            ErrorMessage = result.Message ?? "Registration failed. Please try again.";
            return Page();
        }

        var loginResult = await _auth.LoginAsync(new AccountLoginRequest
        {
            AccountType = result.AccountType,
            Username = Input.Username,
            Password = Input.Password
        }, HttpContext);

        if (!loginResult.Success)
            return RedirectToPage("Login");

        return Redirect(GetRedirectForAccountType(result.AccountType));
    }

    private async Task LoadLookupsAsync() =>
        InsuranceCarriers = await _insuranceService.GetCarriersAsync();

    private string? GetModelStateError()
    {
        var errors = ModelState.Values
            .SelectMany(v => v.Errors)
            .Select(e => e.ErrorMessage)
            .Where(m => !string.IsNullOrWhiteSpace(m))
            .ToList();
        return errors.Count > 0 ? string.Join(" ", errors) : null;
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
