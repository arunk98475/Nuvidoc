using System.Security.Claims;
using Docovee.BLL.Auth;
using Docovee.DS.Models;
using Docovee.BLL.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Docovee.Pages.Account;

[Authorize(Roles = AuthRoles.Doctor)]
public class DoctorProfileModel : PageModel
{
    private readonly IProfileService _profileService;

    public DoctorProfileModel(IProfileService profileService) => _profileService = profileService;

    public DoctorProfileDto? Profile { get; set; }
    public bool Saved { get; set; }

    public async Task<IActionResult> OnGetAsync(bool saved = false)
    {
        Saved = saved;
        if (!int.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var doctorId))
            return RedirectToPage("Login");

        Profile = await _profileService.GetDoctorProfileAsync(doctorId);
        if (Profile == null) return NotFound();
        return Page();
    }
}
