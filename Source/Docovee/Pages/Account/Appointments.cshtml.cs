using System.Security.Claims;
using Docovee.BLL.Auth;
using Docovee.BLL.Services;
using Docovee.DS.Entities;
using Docovee.DS.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Docovee.Pages.Account;

[Authorize(Roles = AuthRoles.Patient)]
public class AppointmentsModel : PageModel
{
    private readonly IProfileService _profileService;
    private readonly IDoctorReviewService _reviewService;
    private readonly IAppointmentService _appointments;
    private readonly IAppSettingsService _appSettings;
    private readonly IDoctorFileService _fileService;

    public AppointmentsModel(
        IProfileService profileService,
        IDoctorReviewService reviewService,
        IAppointmentService appointments,
        IAppSettingsService appSettings,
        IDoctorFileService fileService)
    {
        _profileService = profileService;
        _reviewService = reviewService;
        _appointments = appointments;
        _appSettings = appSettings;
        _fileService = fileService;
    }

    public PatientProfileDto? Profile { get; set; }
    public IReadOnlyList<PatientAppointmentDto> PastAppointments { get; set; } = Array.Empty<PatientAppointmentDto>();
    public IReadOnlyList<PatientAppointmentDto> UpcomingAppointments { get; set; } = Array.Empty<PatientAppointmentDto>();
    public int ReviewEligibleDaysAfterConfirmed { get; private set; } = 1;
    public bool ReviewSubmitted { get; set; }
    public string? ReviewError { get; set; }

    public async Task<IActionResult> OnGetAsync(bool reviewSubmitted = false)
    {
        ReviewSubmitted = reviewSubmitted;
        if (!int.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var patientId))
            return RedirectToPage("Login");

        await LoadAsync(patientId);
        if (Profile == null) return NotFound();
        return Page();
    }

    public async Task<IActionResult> OnPostSubmitReviewAsync(
        int doctorId,
        int rating,
        string reviewText,
        string waitingTime,
        string recommendation)
    {
        if (!int.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var patientId))
            return RedirectToPage("Login");

        string? photoUrl = null;
        var photo = Request.Form.Files.GetFile("ReviewPhoto");
        if (photo != null && photo.Length > 0)
        {
            photoUrl = await _fileService.SaveUploadedPhotoAsync(photo);
            if (photoUrl == null)
            {
                ReviewError = "Could not save that photo. Use JPG, PNG, WebP, or GIF.";
                await LoadAsync(patientId);
                return Page();
            }
        }

        var (success, error) = await _reviewService.AddReviewForPatientAsync(
            patientId, doctorId, rating, reviewText, waitingTime, recommendation, photoUrl);

        if (!success)
        {
            ReviewError = error;
            await LoadAsync(patientId);
            return Page();
        }

        return RedirectToPage(new { reviewSubmitted = true });
    }

    private async Task LoadAsync(int patientId)
    {
        Profile = await _profileService.GetPatientProfileAsync(patientId);
        if (Profile == null) return;

        var all = await _appointments.GetForPatientAsync(patientId);
        ReviewEligibleDaysAfterConfirmed = await _appSettings.GetReviewEligibleDaysAfterConfirmedAsync();
        var startOfToday = DateTime.Today;

        UpcomingAppointments = all
            .Where(a => a.StartsAt >= startOfToday
                        && !AppointmentStatuses.IsCanceled(a.Status)
                        && a.Status != AppointmentStatuses.Completed
                        && AppointmentStatuses.Normalize(a.Status) != AppointmentStatuses.PatientNoShow)
            .OrderBy(a => a.StartsAt)
            .ToList();

        PastAppointments = all
            .Where(a => a.StartsAt < startOfToday
                        || a.Status == AppointmentStatuses.Completed
                        || AppointmentStatuses.IsCanceled(a.Status)
                        || AppointmentStatuses.Normalize(a.Status) == AppointmentStatuses.PatientNoShow)
            .Select(a => ApplyReviewEligibility(a, ReviewEligibleDaysAfterConfirmed))
            .OrderByDescending(a => a.StartsAt)
            .ToList();
    }

    private static PatientAppointmentDto ApplyReviewEligibility(
        PatientAppointmentDto appointment,
        int reviewEligibleDaysAfterConfirmed)
    {
        appointment.CanLeaveReview = AppointmentStatuses.CanPatientLeaveReview(
            appointment.Status,
            appointment.StartsAt,
            reviewEligibleDaysAfterConfirmed,
            appointment.HasReview);
        appointment.ReviewAvailableOn = appointment.HasReview
            || !AppointmentStatuses.IsConfirmedWithDoctor(appointment.Status)
            ? null
            : AppointmentStatuses.GetReviewAvailableOn(appointment.StartsAt, reviewEligibleDaysAfterConfirmed);
        return appointment;
    }

    public static string StatusLabel(string status) => AppointmentStatuses.DisplayLabel(status);
}
