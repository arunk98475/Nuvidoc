using System.Security.Claims;
using System.Text.Json;
using Docovee.BLL.Auth;
using Docovee.BLL.Configuration;
using Docovee.BLL.Data;
using Docovee.BLL.Services;
using Docovee.DS.Models;
using Docovee.Integrations.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;

namespace Docovee.Pages.Doctor;

[Authorize(Roles = AuthRoles.Doctor)]
public class SettingsModel : PageModel
{
    private static readonly HashSet<string> AllowedSections = new(StringComparer.OrdinalIgnoreCase)
    {
        "practice",
        "locations",
        "location", // legacy alias
        "media",
        "visit-reasons",
        "insurance",
        "working-hours",
        "booking-link",
        "integrations",
        "legal"
    };

    private readonly IProfileService _profileService;
    private readonly IDoctorLocationService _locationService;
    private readonly IDoctorInsuranceService _insuranceService;
    private readonly IDoctorMediaService _mediaService;
    private readonly IPmsCalendarService _pms;
    private readonly UploadOptions _uploadOptions;

    public SettingsModel(
        IProfileService profileService,
        IDoctorLocationService locationService,
        IDoctorInsuranceService insuranceService,
        IDoctorMediaService mediaService,
        IPmsCalendarService pms,
        IOptions<UploadOptions> uploadOptions)
    {
        _profileService = profileService;
        _locationService = locationService;
        _insuranceService = insuranceService;
        _mediaService = mediaService;
        _pms = pms;
        _uploadOptions = uploadOptions.Value;
    }

    public int MaxVideoUploadMb => _uploadOptions.MaxUploadMb;
    public long VideoBytesUsed { get; private set; }
    public int VideoMbUsed => (int)(VideoBytesUsed / (1024L * 1024L));
    public int VideoMbRemaining => Math.Max(0, (int)((_uploadOptions.MaxUploadBytes - VideoBytesUsed) / (1024L * 1024L)));

    [BindProperty]
    public PracticeProfileInput PracticeInput { get; set; } = new();

    [BindProperty]
    public SaveDoctorLocationsInput LocationsForm { get; set; } = new();

    [BindProperty]
    public VisitReasonPreferencesInput VisitReasonsForm { get; set; } = new();

    [BindProperty]
    public AddDoctorInsurancesInput InsuranceForm { get; set; } = new();

    [BindProperty]
    public WorkingHoursInput WorkingHoursForm { get; set; } = new();

    public string Section { get; private set; } = "practice";
    public string SectionTitle { get; private set; } = "Practice profile";
    public DoctorProfileDto? Profile { get; private set; }
    public IReadOnlyList<DoctorLocationDto> Locations { get; private set; } = Array.Empty<DoctorLocationDto>();
    public IReadOnlyList<VisitReasonCategoryViewModel> VisitReasonCategories { get; private set; } = Array.Empty<VisitReasonCategoryViewModel>();
    public IReadOnlyList<DoctorInsuranceRowDto> InsuranceRows { get; private set; } = Array.Empty<DoctorInsuranceRowDto>();
    public IReadOnlyList<InsuranceCarrierDto> AvailableCarriers { get; private set; } = Array.Empty<InsuranceCarrierDto>();
    public WorkingHoursPageModel? WorkingHours { get; private set; }
    public IReadOnlyList<DoctorMediaDto> MediaItems { get; private set; } = Array.Empty<DoctorMediaDto>();
    public IReadOnlyList<string> TimeOptions { get; private set; } = BuildTimeOptions();
    public string LocationsJson { get; private set; } = "[]";
    public IReadOnlyList<(string Code, string Name)> StateOptions => UsStates.All;
    public string BookingLink { get; private set; } = "";
    public bool BookingLinkCreateStep { get; private set; }
    public PmsConnectionSettingsDto? NexHealthConnection { get; private set; }
    public bool Saved { get; private set; }
    public string? ErrorMessage { get; private set; }

    private static IReadOnlyList<string> BuildTimeOptions()
    {
        var list = new List<string>();
        for (var minutes = 0; minutes < 24 * 60; minutes += 30)
        {
            var ts = TimeSpan.FromMinutes(minutes);
            list.Add(ts.ToString(@"hh\:mm"));
        }
        return list;
    }

    public static string FormatTimeLabel(string value)
    {
        if (!TimeSpan.TryParse(value, out var ts))
            return value;
        var dt = DateTime.Today.Add(ts);
        return dt.ToString("h:mm tt");
    }

    public async Task<IActionResult> OnGetAsync(string? section = null, bool? saved = null, string? step = null, CancellationToken cancellationToken = default)
    {
        if (!int.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var doctorId))
            return RedirectToPage("/Account/Login");

        Saved = saved == true;
        BookingLinkCreateStep = string.Equals(step, "create", StringComparison.OrdinalIgnoreCase);
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

    public async Task<IActionResult> OnPostAddMediaAsync(string mediaType, string? caption, CancellationToken cancellationToken = default)
    {
        if (!int.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var doctorId))
            return RedirectToPage("/Account/Login");

        var file = Request.Form.Files.GetFile("MediaFile");
        if (file == null || file.Length == 0)
        {
            ErrorMessage = "Please choose a photo to upload.";
            return await LoadPageAsync(doctorId, "practice", cancellationToken);
        }

        var (success, error) = await _mediaService.AddPhotoAsync(doctorId, mediaType, file, caption, cancellationToken);
        if (!success)
        {
            ErrorMessage = error;
            return await LoadPageAsync(doctorId, "practice", cancellationToken);
        }

        return RedirectToPage(new { section = "practice", saved = true });
    }

    public async Task<IActionResult> OnPostAddVideoAsync(string? caption, CancellationToken cancellationToken = default)
    {
        if (!int.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var doctorId))
            return RedirectToPage("/Account/Login");

        var file = Request.Form.Files.GetFile("MediaFile");
        if (file == null || file.Length == 0)
        {
            ErrorMessage = "Please choose a video to upload.";
            return await LoadPageAsync(doctorId, "practice", cancellationToken);
        }

        var (success, error) = await _mediaService.AddVideoAsync(doctorId, file, caption, cancellationToken);
        if (!success)
        {
            ErrorMessage = error;
            return await LoadPageAsync(doctorId, "practice", cancellationToken);
        }

        return RedirectToPage(new { section = "practice", saved = true });
    }

    public async Task<IActionResult> OnPostAddYoutubeVideoAsync(string? youtubeUrl, string? caption, CancellationToken cancellationToken = default)
    {
        if (!int.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var doctorId))
            return RedirectToPage("/Account/Login");

        var (success, error) = await _mediaService.AddYoutubeVideoAsync(doctorId, youtubeUrl ?? "", caption, cancellationToken);
        if (!success)
        {
            ErrorMessage = error;
            return await LoadPageAsync(doctorId, "practice", cancellationToken);
        }

        return RedirectToPage(new { section = "practice", saved = true });
    }

    public async Task<IActionResult> OnPostDeleteMediaAsync(int mediaId, CancellationToken cancellationToken = default)
    {
        if (!int.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var doctorId))
            return RedirectToPage("/Account/Login");

        var (success, error) = await _mediaService.DeleteAsync(doctorId, mediaId, cancellationToken);
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

    public async Task<IActionResult> OnPostWorkingHoursAsync(CancellationToken cancellationToken = default)
    {
        if (!int.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var doctorId))
            return RedirectToPage("/Account/Login");

        var (success, error) = await _profileService.UpdateWorkingHoursAsync(doctorId, WorkingHoursForm, cancellationToken);
        if (!success)
        {
            ErrorMessage = error;
            return await LoadPageAsync(doctorId, "working-hours", cancellationToken);
        }

        return RedirectToPage(new { section = "working-hours", saved = true });
    }

    private async Task<IActionResult> LoadPageAsync(int doctorId, string? section, CancellationToken cancellationToken)
    {
        Section = string.IsNullOrWhiteSpace(section) || !AllowedSections.Contains(section.Trim())
            ? "practice"
            : section.Trim().ToLowerInvariant();
        if (Section == "location")
            Section = "locations";

        if (Section == "media")
            Section = "practice";

        // Integrations is read-only for doctors; only visible after admin enables NexHealth.
        if (Section == "integrations")
        {
            var nhCheck = await _pms.GetConnectionAsync(doctorId, PmsProviders.NexHealth, cancellationToken);
            if (nhCheck is not { IsEnabled: true })
                return RedirectToPage(new { section = "practice" });
        }

        SectionTitle = Section switch
        {
            "locations" => "Locations",
            "visit-reasons" => "Visit reasons",
            "insurance" => "Insurance",
            "working-hours" => "Working hours",
            "booking-link" => BookingLinkCreateStep ? "Create a Booking Link" : "Booking Link",
            "integrations" => "Integrations",
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
                PracticeWebsite = Profile.PracticeWebsite,
                AllowGoogleBookings = Profile.AllowGoogleBookings,
                FacebookUrl = Profile.FacebookUrl,
                InstagramUrl = Profile.InstagramUrl,
                TikTokUrl = Profile.TikTokUrl,
                LinkedInUrl = Profile.LinkedInUrl,
                YoutubeChannelUrl = Profile.YoutubeChannelUrl
            };
        }

        if (Section == "practice")
        {
            MediaItems = await _mediaService.GetForDoctorAsync(doctorId, cancellationToken);
            VideoBytesUsed = await _mediaService.GetVideoBytesUsedAsync(doctorId, cancellationToken);
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

        if (Section == "working-hours")
        {
            _ = await _locationService.GetLocationsAsync(doctorId, cancellationToken);
            WorkingHours = await _profileService.GetWorkingHoursAsync(doctorId, cancellationToken);
            if (WorkingHours != null && WorkingHoursForm.Days.Count == 0)
                WorkingHoursForm = WorkingHours.Hours;
        }

        if (Section == "integrations")
        {
            NexHealthConnection = await _pms.GetConnectionAsync(doctorId, PmsProviders.NexHealth, cancellationToken);
        }

        BookingLink = $"{Request.Scheme}://{Request.Host}/doctors/{doctorId}";
        return Page();
    }

    public static string? GetVideoEmbedUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return null;

        var trimmed = url.Trim();
        var yt = System.Text.RegularExpressions.Regex.Match(
            trimmed,
            @"(?:youtube\.com\/watch\?v=|youtu\.be\/|youtube\.com\/embed\/|youtube\.com\/shorts\/)([\w-]+)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (yt.Success)
            return $"https://www.youtube.com/embed/{yt.Groups[1].Value}";

        return null;
    }

    public static bool IsDirectVideoFile(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return false;

        return url.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)
            || url.EndsWith(".webm", StringComparison.OrdinalIgnoreCase)
            || url.EndsWith(".ogg", StringComparison.OrdinalIgnoreCase)
            || url.EndsWith(".mov", StringComparison.OrdinalIgnoreCase)
            || url.EndsWith(".m4v", StringComparison.OrdinalIgnoreCase)
            || System.Text.RegularExpressions.Regex.IsMatch(
                url,
                @"/uploads/doctors/\d+/videos/",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase)
            || url.Contains("/uploads/doctors/videos/", StringComparison.OrdinalIgnoreCase);
    }
}
