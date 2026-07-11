using System.Security.Claims;
using Docovee.BLL.Auth;
using Docovee.BLL.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Docovee.Pages.Doctor;

[Authorize(Roles = AuthRoles.Doctor)]
public class IndexModel : PageModel
{
    private readonly IProfileService _profileService;

    public IndexModel(IProfileService profileService) => _profileService = profileService;

    public string DisplayName { get; private set; } = "Doctor";

    public async Task<IActionResult> OnGetAsync()
    {
        if (!int.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var doctorId))
            return RedirectToPage("/Account/Login");

        var profile = await _profileService.GetDoctorProfileAsync(doctorId);
        if (profile != null)
            DisplayName = !string.IsNullOrWhiteSpace(profile.PracticeName) ? profile.PracticeName! : profile.Name;

        return Page();
    }
}
