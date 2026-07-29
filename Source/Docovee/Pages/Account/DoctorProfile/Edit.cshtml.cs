using System.Security.Claims;
using Docovee.BLL.Auth;
using Docovee.BLL.Configuration;
using Docovee.DS.Models;
using Docovee.BLL.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;
using UsStateList = Docovee.BLL.Data.UsStates;

namespace Docovee.Pages.Account.DoctorProfile;

[Authorize(Roles = AuthRoles.Doctor)]
public class EditModel : PageModel
{
    private readonly IProfileService _profileService;
    private readonly IInsuranceService _insuranceService;
    private readonly UploadOptions _uploadOptions;

    public EditModel(
        IProfileService profileService,
        IInsuranceService insuranceService,
        IOptions<UploadOptions> uploadOptions)
    {
        _profileService = profileService;
        _insuranceService = insuranceService;
        _uploadOptions = uploadOptions.Value;
    }

    [BindProperty]
    public DoctorProfileEditModel Input { get; set; } = new();

    public IReadOnlyList<InsuranceCarrierDto> InsuranceCarriers { get; set; } = Array.Empty<InsuranceCarrierDto>();
    public IReadOnlyList<(string Code, string Name)> UsStates => UsStateList.All;
    public string? CurrentPhotoUrl { get; set; }
    public string? CurrentVideoUrl { get; set; }
    public string? ErrorMessage { get; set; }
    public int MaxVideoUploadMb => _uploadOptions.MaxUploadMb;

    public async Task<IActionResult> OnGetAsync()
    {
        if (!int.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var doctorId))
            return RedirectToPage("/Account/Login");

        InsuranceCarriers = await _insuranceService.GetCarriersAsync();

        var model = await _profileService.GetDoctorForEditAsync(doctorId);
        if (model == null) return NotFound();
        Input = model;

        var profile = await _profileService.GetDoctorProfileAsync(doctorId);
        CurrentPhotoUrl = profile?.PhotoUrl;
        CurrentVideoUrl = model.VideoUrl;
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(IFormFile? Photo, IFormFile? Video)
    {
        if (!int.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var doctorId))
            return RedirectToPage("/Account/Login");

        InsuranceCarriers = await _insuranceService.GetCarriersAsync();

        if (!ModelState.IsValid)
        {
            ErrorMessage = "Please check your entries and try again.";
            var existing = await _profileService.GetDoctorProfileAsync(doctorId);
            CurrentPhotoUrl = existing?.PhotoUrl;
            return Page();
        }

        var (success, error) = await _profileService.UpdateDoctorProfileAsync(doctorId, Input, Photo, Video);
        if (!success)
        {
            ErrorMessage = error;
            var profile = await _profileService.GetDoctorProfileAsync(doctorId);
            CurrentPhotoUrl = profile?.PhotoUrl;
            return Page();
        }

        return Redirect("/Account/DoctorProfile?saved=true");
    }
}
