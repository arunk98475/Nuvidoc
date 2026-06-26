using System.Security.Claims;
using Docovee.BLL.Auth;
using Docovee.BLL.Models;
using Docovee.BLL.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Docovee.Pages.Account;

[Authorize(Roles = AuthRoles.Patient)]
public class ProfileModel : PageModel
{
    private readonly IProfileService _profileService;

    public ProfileModel(IProfileService profileService) => _profileService = profileService;

    public PatientProfileDto? Profile { get; set; }
    public bool Saved { get; set; }

    public async Task<IActionResult> OnGetAsync(bool saved = false)
    {
        Saved = saved;
        if (!int.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var patientId))
            return RedirectToPage("Login");

        Profile = await _profileService.GetPatientProfileAsync(patientId);
        if (Profile == null) return NotFound();
        return Page();
    }
}
