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
    private readonly IDoctorReviewService _reviewService;

    public ProfileModel(IProfileService profileService, IDoctorReviewService reviewService)
    {
        _profileService = profileService;
        _reviewService = reviewService;
    }

    public PatientProfileDto? Profile { get; set; }
    public bool Saved { get; set; }
    public bool ReviewSubmitted { get; set; }
    public string? ReviewError { get; set; }

    public async Task<IActionResult> OnGetAsync(bool saved = false, bool reviewSubmitted = false)
    {
        Saved = saved;
        ReviewSubmitted = reviewSubmitted;
        if (!int.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var patientId))
            return RedirectToPage("Login");

        Profile = await _profileService.GetPatientProfileAsync(patientId);
        if (Profile == null) return NotFound();
        return Page();
    }

    public async Task<IActionResult> OnPostSubmitReviewAsync(int doctorId, int rating, string reviewText)
    {
        if (!int.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var patientId))
            return RedirectToPage("Login");

        var (success, error) = await _reviewService.AddReviewForPatientAsync(
            patientId, doctorId, rating, reviewText);

        if (!success)
        {
            ReviewError = error;
            Profile = await _profileService.GetPatientProfileAsync(patientId);
            return Page();
        }

        return RedirectToPage(new { reviewSubmitted = true });
    }
}
