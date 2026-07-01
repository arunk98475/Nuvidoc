using System.Security.Claims;
using Docovee.BLL.Auth;
using Docovee.DS.Models;
using Docovee.BLL.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Docovee.Pages.Account.Profile;

[Authorize(Roles = AuthRoles.Patient)]
public class EditModel : PageModel
{
    private readonly IProfileService _profileService;

    public EditModel(IProfileService profileService) => _profileService = profileService;

    [BindProperty]
    public PatientProfileEditModel Input { get; set; } = new();

    public string? ErrorMessage { get; set; }

    public async Task<IActionResult> OnGetAsync()
    {
        if (!int.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var patientId))
            return RedirectToPage("/Account/Login");

        var model = await _profileService.GetPatientForEditAsync(patientId);
        if (model == null) return NotFound();
        Input = model;
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (!int.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var patientId))
            return RedirectToPage("/Account/Login");

        if (!ModelState.IsValid)
        {
            ErrorMessage = "Please check your entries and try again.";
            return Page();
        }

        var (success, error) = await _profileService.UpdatePatientProfileAsync(patientId, Input);
        if (!success)
        {
            ErrorMessage = error;
            return Page();
        }

        return Redirect("/Account/Profile?saved=true");
    }
}
