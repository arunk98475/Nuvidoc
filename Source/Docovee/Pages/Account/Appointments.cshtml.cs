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
    private readonly IAppointmentFeedbackService _feedback;
    private readonly IAppointmentService _appointments;
    private readonly IPatientNotificationService _notifications;
    private readonly IAppSettingsService _appSettings;
    private readonly IDoctorFileService _fileService;

    public AppointmentsModel(
        IProfileService profileService,
        IAppointmentFeedbackService feedback,
        IAppointmentService appointments,
        IPatientNotificationService notifications,
        IAppSettingsService appSettings,
        IDoctorFileService fileService)
    {
        _profileService = profileService;
        _feedback = feedback;
        _appointments = appointments;
        _notifications = notifications;
        _appSettings = appSettings;
        _fileService = fileService;
    }

    public PatientProfileDto? Profile { get; set; }
    public IReadOnlyList<PatientAppointmentDto> PastAppointments { get; set; } = Array.Empty<PatientAppointmentDto>();
    public IReadOnlyList<PatientAppointmentDto> UpcomingAppointments { get; set; } = Array.Empty<PatientAppointmentDto>();
    public int UnreadNotificationCount { get; private set; }
    public bool FeedbackRequestEnabled { get; private set; } = true;
    public int FeedbackRequestHoursAfterBooking { get; private set; } = 24;
    public bool ReviewSubmitted { get; set; }
    public bool NoShowSubmitted { get; set; }
    public string? ReviewError { get; set; }

    public async Task<IActionResult> OnGetAsync(bool reviewSubmitted = false, bool noShowSubmitted = false)
    {
        ReviewSubmitted = reviewSubmitted;
        NoShowSubmitted = noShowSubmitted;
        if (!int.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var patientId))
            return RedirectToPage("Login");

        await LoadAsync(patientId);
        if (Profile == null) return NotFound();
        return Page();
    }

    public async Task<IActionResult> OnPostReportNoShowAsync(int appointmentId)
    {
        if (!int.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var patientId))
            return RedirectToPage("Login");

        var (success, error) = await _feedback.ReportNoShowAsPatientAsync(patientId, appointmentId);
        if (!success)
        {
            ReviewError = error;
            await LoadAsync(patientId);
            return Page();
        }

        return RedirectToPage(new { noShowSubmitted = true });
    }

    public async Task<IActionResult> OnPostSubmitReviewAsync(
        int appointmentId,
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
            photoUrl = await _fileService.SaveUploadedPhotoAsync(doctorId, photo);
            if (photoUrl == null)
            {
                ReviewError = "Could not save that photo. Use JPG, PNG, WebP, or GIF.";
                await LoadAsync(patientId);
                return Page();
            }
        }

        var (success, error) = await _feedback.SubmitReviewAsPatientAsync(
            patientId,
            appointmentId,
            doctorId,
            rating,
            reviewText,
            waitingTime,
            recommendation,
            photoUrl);

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
        FeedbackRequestEnabled = await _appSettings.GetFeedbackRequestEnabledAsync();
        FeedbackRequestHoursAfterBooking = await _appSettings.GetFeedbackRequestHoursAfterBookingAsync();
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
            .Select(a => ApplyFeedbackEligibility(a))
            .OrderByDescending(a => a.StartsAt)
            .ToList();

        UnreadNotificationCount = await _notifications.CountUnreadAsync(patientId);
    }

    private PatientAppointmentDto ApplyFeedbackEligibility(PatientAppointmentDto appointment)
    {
        var canLeave = AppointmentStatuses.CanPatientLeaveFeedback(
            appointment.Status,
            appointment.CreatedAt,
            appointment.StartsAt,
            FeedbackRequestEnabled,
            FeedbackRequestHoursAfterBooking,
            appointment.HasReview);

        appointment.CanLeaveReview = canLeave
            && !AppointmentStatuses.IsPatientNoShow(appointment.Status)
            && !AppointmentStatuses.IsCanceled(appointment.Status);
        appointment.CanReportNoShow = canLeave
            && AppointmentStatuses.CanMarkNoShow(appointment.Status);
        appointment.FeedbackAvailableAtUtc = AppointmentStatuses.GetFeedbackAvailableAtUtc(
            appointment.CreatedAt,
            FeedbackRequestEnabled,
            FeedbackRequestHoursAfterBooking);
        return appointment;
    }

    public static string StatusLabel(string status) => AppointmentStatuses.DisplayLabel(status);
}
