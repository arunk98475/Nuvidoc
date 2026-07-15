using System.Security.Claims;
using System.Text.Json;
using Docovee.BLL.Auth;
using Docovee.BLL.Data;
using Docovee.BLL.Services;
using Docovee.DS.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Docovee.Pages.Doctor;

[Authorize(Roles = AuthRoles.Doctor)]
public class SettingsModel : PageModel
{
    private static readonly HashSet<string> AllowedSections = new(StringComparer.OrdinalIgnoreCase)
    {
        "practice",
        "locations",
        "location", // legacy alias
        "visit-reasons",
        "insurance",
        "working-hours",
        "booking-link",
        "legal"
    };

    private readonly IProfileService _profileService;
    private readonly IDoctorLocationService _locationService;
    private readonly IDoctorInsuranceService _insuranceService;

    public SettingsModel(
        IProfileService profileService,
        IDoctorLocationService locationService,
        IDoctorInsuranceService insuranceService)
    {
        _profileService = profileService;
        _locationService = locationService;
        _insuranceService = insuranceService;
    }

    [BindProperty]
    public PracticeProfileInput PracticeInput { get; set; } = new();

    [BindProperty]
    public SaveDoctorLocationsInput LocationsForm { get; set; } = new();

    [BindProperty]
    public VisitReasonPreferencesInput VisitReasonsForm { get; set; } = new();

    [BindProperty]
    public AddDoctorInsurancesInput InsuranceForm { get; set; } = new();

    public string Section { get; private set; } = "practice";
    public string SectionTitle { get; private set; } = "Practice profile";
    public DoctorProfileDto? Profile { get; private set; }
    public IReadOnlyList<DoctorLocationDto> Locations { get; private set; } = Array.Empty<DoctorLocationDto>();
    public IReadOnlyList<VisitReasonCategoryViewModel> VisitReasonCategories { get; private set; } = Array.Empty<VisitReasonCategoryViewModel>();
    public IReadOnlyList<DoctorInsuranceRowDto> InsuranceRows { get; private set; } = Array.Empty<DoctorInsuranceRowDto>();
    public IReadOnlyList<InsuranceCarrierDto> AvailableCarriers { get; private set; } = Array.Empty<InsuranceCarrierDto>();
    public string LocationsJson { get; private set; } = "[]";
    public IReadOnlyList<(string Code, string Name)> StateOptions => UsStates.All;
    public string BookingLink { get; private set; } = "";
    public bool Saved { get; private set; }
    public string? ErrorMessage { get; private set; }

    public async Task<IActionResult> OnGetAsync(string? section = null, bool? saved = null, CancellationToken cancellationToken = default)
    {
        if (!int.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var doctorId))
            return RedirectToPage("/Account/Login");

        Saved = saved == true;
        return await LoadPageAsync(doctorId, section, cancellationToken);
    }

    public async Task<IActionResult> OnPostPracticeAsync(CancellationToken cancellationToken = default)
    {
        if (!int.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var doctorId))
            return RedirectToPage("/Account/Login");

        Section = "practice";
        SectionTitle = "Practice profile";

        var logo = Request.Form.Files.GetFile("PracticeLogo");
        var (success, error) = await _profileService.UpdatePracticeProfileAsync(doctorId, PracticeInput, logo, cancellationToken);
        if (!success)
        {
            ErrorMessage = error;
            return await LoadPageAsync(doctorId, "practice", cancellationToken);
        }

        return RedirectToPage(new { section = "practice", saved = true });
    }

    public async Task<IActionResult> OnPostLocationsAsync(CancellationToken cancellationToken = default)
    {
        if (!int.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var doctorId))
            return RedirectToPage("/Account/Login");

        var entries = LocationsForm.Locations
            .Where(l => !string.IsNullOrWhiteSpace(l.PhoneNumber)
                || !string.IsNullOrWhiteSpace(l.Address1)
                || !string.IsNullOrWhiteSpace(l.City))
            .ToList();

        if (entries.Count == 0)
        {
            ErrorMessage = "Add at least one location.";
            return await LoadPageAsync(doctorId, "locations", cancellationToken);
        }

        if (entries.Count == 1 && entries[0].Id is int locationId and > 0)
        {
            var (updated, updateError) = await _locationService.UpdateLocationAsync(doctorId, entries[0], cancellationToken);
            if (!updated)
            {
                ErrorMessage = updateError;
                return await LoadPageAsync(doctorId, "locations", cancellationToken);
            }
        }
        else
        {
            var toAdd = entries.Where(e => e.Id is null or <= 0).ToList();
            if (toAdd.Count == 0)
            {
                ErrorMessage = "No new locations to add.";
                return await LoadPageAsync(doctorId, "locations", cancellationToken);
            }

            var (added, addError) = await _locationService.AddLocationsAsync(doctorId, toAdd, cancellationToken);
            if (!added)
            {
                ErrorMessage = addError;
                return await LoadPageAsync(doctorId, "locations", cancellationToken);
            }
        }

        return RedirectToPage(new { section = "locations", saved = true });
    }

    public async Task<IActionResult> OnPostDeleteLocationAsync(int locationId, CancellationToken cancellationToken = default)
    {
        if (!int.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var doctorId))
            return RedirectToPage("/Account/Login");

        var (success, error) = await _locationService.DeleteLocationAsync(doctorId, locationId, cancellationToken);
        if (!success)
        {
            ErrorMessage = error;
            return await LoadPageAsync(doctorId, "locations", cancellationToken);
        }

        return RedirectToPage(new { section = "locations", saved = true });
    }

    public async Task<IActionResult> OnPostVisitReasonsAsync(CancellationToken cancellationToken = default)
    {
        if (!int.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var doctorId))
            return RedirectToPage("/Account/Login");

        var (success, error) = await _profileService.UpdateVisitReasonPreferencesAsync(doctorId, VisitReasonsForm, cancellationToken);
        if (!success)
        {
            ErrorMessage = error;
            return await LoadPageAsync(doctorId, "visit-reasons", cancellationToken);
        }

        return RedirectToPage(new { section = "visit-reasons", saved = true });
    }

    public async Task<IActionResult> OnPostAddInsuranceAsync(CancellationToken cancellationToken = default)
    {
        if (!int.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var doctorId))
            return RedirectToPage("/Account/Login");

        var (success, error) = await _insuranceService.AddCarriersAsync(doctorId, InsuranceForm.CarrierIds, cancellationToken);
        if (!success)
        {
            ErrorMessage = error;
            return await LoadPageAsync(doctorId, "insurance", cancellationToken);
        }

        return RedirectToPage(new { section = "insurance", saved = true });
    }

    public async Task<IActionResult> OnPostRemoveInsuranceAsync(int carrierId, CancellationToken cancellationToken = default)
    {
        if (!int.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var doctorId))
            return RedirectToPage("/Account/Login");

        var (success, error) = await _insuranceService.RemoveCarrierAsync(doctorId, carrierId, cancellationToken);
        if (!success)
        {
            ErrorMessage = error;
            return await LoadPageAsync(doctorId, "insurance", cancellationToken);
        }

        return RedirectToPage(new { section = "insurance", saved = true });
    }

    private async Task<IActionResult> LoadPageAsync(int doctorId, string? section, CancellationToken cancellationToken)
    {
        Section = string.IsNullOrWhiteSpace(section) || !AllowedSections.Contains(section.Trim())
            ? "practice"
            : section.Trim().ToLowerInvariant();
        if (Section == "location")
            Section = "locations";

        SectionTitle = Section switch
        {
            "locations" => "Locations",
            "visit-reasons" => "Visit reasons",
            "insurance" => "Insurance",
            "working-hours" => "Working hours",
            "booking-link" => "Booking Link",
            "legal" => "Legal",
            _ => "Practice profile"
        };

        Profile = await _profileService.GetDoctorProfileAsync(doctorId, cancellationToken);
        if (Profile == null)
            return NotFound();

        if (Section == "practice" && string.IsNullOrEmpty(PracticeInput.PracticeName))
        {
            PracticeInput = new PracticeProfileInput
            {
                PracticeName = Profile.PracticeName ?? Profile.Name,
                PracticeDescription = Profile.PracticeDescription,
                YoutubeVideoUrl = Profile.VideoUrl,
                PracticeWebsite = Profile.PracticeWebsite,
                AllowGoogleBookings = Profile.AllowGoogleBookings
            };
        }

        if (Section == "locations")
        {
            Locations = await _locationService.GetLocationsAsync(doctorId, cancellationToken);
            var inputs = await _locationService.GetAllLocationInputsAsync(doctorId, cancellationToken);
            LocationsJson = JsonSerializer.Serialize(inputs);
        }

        if (Section == "visit-reasons")
            VisitReasonCategories = await _profileService.GetVisitReasonPreferencesAsync(doctorId, cancellationToken);

        if (Section == "insurance")
        {
            InsuranceRows = await _insuranceService.GetDoctorInsurancesAsync(doctorId, cancellationToken);
            AvailableCarriers = await _insuranceService.GetAvailableCarriersAsync(doctorId, cancellationToken);
        }

        BookingLink = $"{Request.Scheme}://{Request.Host}/doctors/{doctorId}";
        return Page();
    }
}
