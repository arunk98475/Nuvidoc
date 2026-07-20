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
public class ProfileModel : PageModel
{
    private readonly IProfileService _profileService;
    private readonly IDoctorReviewService _reviewService;
    private readonly IAppointmentService _appointments;
    private readonly IAppSettingsService _appSettings;

    public ProfileModel(
        IProfileService profileService,
        IDoctorReviewService reviewService,
        IAppointmentService appointments,
        IAppSettingsService appSettings)
    {
        _profileService = profileService;
        _reviewService = reviewService;
        _appointments = appointments;
        _appSettings = appSettings;
    }

    public PatientProfileDto? Profile { get; set; }
    public string Section { get; set; } = "personal";
    public string? EditField { get; set; }
    public bool Saved { get; set; }
    public bool PasswordChanged { get; set; }
    public bool ReviewSubmitted { get; set; }
    public string? ReviewError { get; set; }
    public string? FormError { get; set; }
    public string? FormSuccess { get; set; }

    public IReadOnlyList<PatientAppointmentDto> UpcomingAppointments { get; set; } = Array.Empty<PatientAppointmentDto>();
    public IReadOnlyList<PatientAppointmentDto> PastAppointments { get; set; } = Array.Empty<PatientAppointmentDto>();
    public int ReviewEligibleDaysAfterConfirmed { get; private set; } = 1;

    [BindProperty]
    public PatientProfileEditModel PersonalInput { get; set; } = new();

    [BindProperty]
    public string? NewPassword { get; set; }

    [BindProperty]
    public string? ConfirmPassword { get; set; }

    public async Task<IActionResult> OnGetAsync(
        string? section = null,
        string? edit = null,
        bool saved = false,
        bool passwordChanged = false,
        bool reviewSubmitted = false)
    {
        Section = NormalizeSection(section);
        EditField = NormalizeEditField(edit);
        Saved = saved;
        PasswordChanged = passwordChanged;
        ReviewSubmitted = reviewSubmitted;

        if (!int.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var patientId))
            return RedirectToPage("Login");

        await LoadAsync(patientId);
        if (Profile == null) return NotFound();
        return Page();
    }

    public async Task<IActionResult> OnPostUpdatePersonalAsync()
    {
        Section = "personal";
        if (!int.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var patientId))
            return RedirectToPage("Login");

        PersonalInput.NewPassword = null;
        var (success, error) = await _profileService.UpdatePatientProfileAsync(patientId, PersonalInput);
        if (!success)
        {
            FormError = error;
            EditField ??= "name";
            await LoadAsync(patientId);
            return Page();
        }

        return RedirectToPage(new { section = "personal", saved = true });
    }

    public async Task<IActionResult> OnPostChangePasswordAsync()
    {
        Section = "security";
        EditField = "password";
        if (!int.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var patientId))
            return RedirectToPage("Login");

        if (string.IsNullOrWhiteSpace(NewPassword) || NewPassword.Length < 6)
        {
            FormError = "Password must be at least 6 characters.";
            await LoadAsync(patientId);
            return Page();
        }

        if (NewPassword != ConfirmPassword)
        {
            FormError = "New password and confirmation do not match.";
            await LoadAsync(patientId);
            return Page();
        }

        var edit = await _profileService.GetPatientForEditAsync(patientId);
        if (edit == null) return NotFound();
        edit.NewPassword = NewPassword;

        var (success, error) = await _profileService.UpdatePatientProfileAsync(patientId, edit);
        if (!success)
        {
            FormError = error;
            await LoadAsync(patientId);
            return Page();
        }

        return RedirectToPage(new { section = "security", passwordChanged = true });
    }

    public async Task<IActionResult> OnPostRequestEmailVerificationAsync()
    {
        Section = "security";
        if (!int.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var patientId))
            return RedirectToPage("Login");

        await LoadAsync(patientId);
        FormSuccess = "Email verification will be available once the site email service (e.g. Amazon SES) is connected. Your login email is ready to use.";
        return Page();
    }

    public async Task<IActionResult> OnPostRequestPhoneVerificationAsync()
    {
        Section = "security";
        if (!int.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var patientId))
            return RedirectToPage("Login");

        await LoadAsync(patientId);
        FormSuccess = "Phone verification (SMS) will be available in a future update. You can still update your phone number under Personal Information.";
        return Page();
    }

    public async Task<IActionResult> OnPostSubmitReviewAsync(
        int doctorId,
        int rating,
        string reviewText,
        string waitingTime,
        string recommendation)
    {
        Section = "history";
        if (!int.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var patientId))
            return RedirectToPage("Login");

        var (success, error) = await _reviewService.AddReviewForPatientAsync(
            patientId, doctorId, rating, reviewText, waitingTime, recommendation);

        if (!success)
        {
            ReviewError = error;
            await LoadAsync(patientId);
            return Page();
        }

        return RedirectToPage(new { section = "history", reviewSubmitted = true });
    }

    private async Task LoadAsync(int patientId)
    {
        Profile = await _profileService.GetPatientProfileAsync(patientId);
        if (Profile == null) return;

        PersonalInput = new PatientProfileEditModel
        {
            Username = Profile.Username,
            FullName = Profile.FullName,
            DateOfBirth = Profile.DateOfBirth,
            Phone = Profile.Phone
        };

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

    private static string NormalizeSection(string? section) =>
        (section ?? "personal").Trim().ToLowerInvariant() switch
        {
            "security" => "security",
            "notifications" or "notification" => "notifications",
            "history" or "appointments" => "history",
            _ => "personal"
        };

    private static string? NormalizeEditField(string? edit) =>
        (edit ?? "").Trim().ToLowerInvariant() switch
        {
            "name" => "name",
            "dob" or "dateofbirth" or "birth" => "dob",
            "phone" => "phone",
            "email" => "email",
            "password" => "password",
            _ => null
        };

    public static string StatusLabel(string status) => AppointmentStatuses.DisplayLabel(status);
}
