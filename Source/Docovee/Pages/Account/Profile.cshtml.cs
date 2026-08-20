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
    private readonly IAppointmentService _appointments;
    private readonly IInsuranceService _insuranceService;
    private readonly IPatientInsuranceProfileService _insuranceProfile;
    private readonly IPatientNotificationService _notifications;
    private readonly IAppointmentCancelService _appointmentCancel;
    private readonly IPhoneVerificationService _phoneVerification;
    private readonly IPatientPreferenceService _preferences;
    private readonly IPatientReminderService _reminders;
    private readonly IPatientEmailAuthService _emailAuth;

    public ProfileModel(
        IProfileService profileService,
        IAppointmentService appointments,
        IInsuranceService insuranceService,
        IPatientInsuranceProfileService insuranceProfile,
        IPatientNotificationService notifications,
        IAppointmentCancelService appointmentCancel,
        IPhoneVerificationService phoneVerification,
        IPatientPreferenceService preferences,
        IPatientReminderService reminders,
        IPatientEmailAuthService emailAuth)
    {
        _profileService = profileService;
        _appointments = appointments;
        _insuranceService = insuranceService;
        _insuranceProfile = insuranceProfile;
        _notifications = notifications;
        _appointmentCancel = appointmentCancel;
        _phoneVerification = phoneVerification;
        _preferences = preferences;
        _reminders = reminders;
        _emailAuth = emailAuth;
    }

    public PatientProfileDto? Profile { get; set; }
    public PatientInsuranceProfileDto InsuranceProfile { get; set; } = new();
    public IReadOnlyList<InsuranceCarrierDto> InsuranceCatalog { get; set; } = Array.Empty<InsuranceCarrierDto>();
    public string Section { get; set; } = "personal";
    /// <summary>True when /Account/Profile is opened with no section — mobile shows the settings list only.</summary>
    public bool IsMenuHub { get; set; }
    public string? EditField { get; set; }
    public bool Saved { get; set; }
    public bool PasswordChanged { get; set; }
    public bool InsuranceSaved { get; set; }
    public bool PrivacySaved { get; set; }
    public bool PermissionsSaved { get; set; }
    public bool PreferencesSaved { get; set; }
    public bool RemindersSaved { get; set; }
    public PatientReminderSettingsDto ReminderSettings { get; set; } = new();
    public PatientPreferencePageModel PreferencePage { get; set; } = new();
    public PatientPrivacySettingsDto PrivacySettings { get; set; } = new();
    public string? FormError { get; set; }
    public string? FormSuccess { get; set; }

    public IReadOnlyList<PatientAppointmentDto> UpcomingAppointments { get; set; } = Array.Empty<PatientAppointmentDto>();
    public IReadOnlyList<PatientNotificationDto> Notifications { get; set; } = Array.Empty<PatientNotificationDto>();
    public int UnreadNotificationCount { get; set; }

    [BindProperty]
    public PatientProfileEditModel PersonalInput { get; set; } = new();

    [BindProperty]
    public PatientInsuranceSaveModel InsuranceInput { get; set; } = new();

    [BindProperty]
    public string? NewPassword { get; set; }

    [BindProperty]
    public string? ConfirmPassword { get; set; }

    [BindProperty]
    public bool? HipaaOptIn { get; set; }

    [BindProperty]
    public bool CookieTrackingOptOut { get; set; }

    [BindProperty]
    public bool AutofillEnabled { get; set; }

    [BindProperty]
    public string? PhoneVerificationCode { get; set; }

    [BindProperty]
    public List<PatientPreferenceAnswerInput> PreferenceInput { get; set; } = new();

    [BindProperty]
    public PatientReminderSettingsSaveRequest ReminderInput { get; set; } = new();

    public async Task<IActionResult> OnGetAsync(
        string? section = null,
        string? edit = null,
        bool saved = false,
        bool passwordChanged = false,
        bool insuranceSaved = false,
        bool privacySaved = false,
        bool permissionsSaved = false,
        bool preferencesSaved = false,
        bool remindersSaved = false)
    {
        IsMenuHub = string.IsNullOrWhiteSpace(section);
        var normalized = NormalizeSection(section);
        if (normalized is "appointment-history" or "history")
            return RedirectToPage("/Account/Appointments");

        Section = normalized;
        EditField = NormalizeEditField(edit);
        Saved = saved;
        PasswordChanged = passwordChanged;
        InsuranceSaved = insuranceSaved;
        PrivacySaved = privacySaved;
        PermissionsSaved = permissionsSaved;
        PreferencesSaved = preferencesSaved;
        RemindersSaved = remindersSaved;

        if (!int.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var patientId))
            return RedirectToPage("Login");

        await LoadAsync(patientId);
        if (Profile == null) return NotFound();

        if (normalized == "notifications")
        {
            await _notifications.MarkAllReadAsync(patientId);
            UnreadNotificationCount = 0;
        }

        return Page();
    }

    public async Task<IActionResult> OnPostUpdatePersonalAsync()
    {
        Section = "personal";
        EditField ??= NormalizeEditField(Request.Form["edit"].ToString()) ?? "name";
        if (!int.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var patientId))
            return RedirectToPage("Login");

        PersonalInput.NewPassword = null;
        var attempted = PersonalInput;
        var (success, error) = await _profileService.UpdatePatientProfileAsync(patientId, attempted);
        if (!success)
        {
            FormError = error;
            await LoadAsync(patientId);
            PersonalInput.FullName = attempted.FullName;
            PersonalInput.Phone = attempted.Phone;
            PersonalInput.DateOfBirth = attempted.DateOfBirth;
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

        var baseUrl = $"{Request.Scheme}://{Request.Host}";
        var result = await _emailAuth.SendEmailVerificationAsync(patientId, baseUrl);
        await LoadAsync(patientId);
        if (result.Success)
            FormSuccess = result.Message;
        else
            FormError = result.Message;
        return Page();
    }

    public async Task<IActionResult> OnPostUpdatePhoneAsync()
    {
        Section = "security";
        EditField = "phone";
        if (!int.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var patientId))
            return RedirectToPage("Login");

        PersonalInput.NewPassword = null;
        var attemptedPhone = PersonalInput.Phone;
        var current = await _profileService.GetPatientForEditAsync(patientId);
        if (current == null)
            return NotFound();

        current.Phone = attemptedPhone;
        var (success, error) = await _profileService.UpdatePatientProfileAsync(patientId, current);
        if (!success)
        {
            FormError = error;
            await LoadAsync(patientId);
            PersonalInput.Phone = attemptedPhone;
            return Page();
        }

        return RedirectToPage(new { section = "security", saved = true });
    }

    public async Task<IActionResult> OnPostRequestPhoneVerificationAsync(string channel)
    {
        Section = "security";
        if (!int.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var patientId))
            return RedirectToPage("Login");

        if (!PhoneVerificationChannels.IsKnown(channel))
        {
            await LoadAsync(patientId);
            FormError = "Choose Verify via SMS or Verify via WhatsApp.";
            return Page();
        }

        var result = await _phoneVerification.SendCodeAsync(patientId, channel);
        await LoadAsync(patientId);
        if (result.Success)
            FormSuccess = result.Message;
        else
            FormError = result.Message;
        return Page();
    }

    public async Task<IActionResult> OnPostConfirmPhoneVerificationAsync()
    {
        Section = "security";
        if (!int.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var patientId))
            return RedirectToPage("Login");

        var result = await _phoneVerification.VerifyCodeAsync(patientId, PhoneVerificationCode ?? "");
        await LoadAsync(patientId);
        if (result.Success)
            FormSuccess = result.Message;
        else
            FormError = result.Message;
        return Page();
    }

    public async Task<IActionResult> OnPostSaveInsuranceAsync()
    {
        Section = "insurance";
        if (!int.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var patientId))
            return RedirectToPage("Login");

        var (success, error) = await _insuranceProfile.SaveAsync(patientId, InsuranceInput);

        if (!success)
        {
            FormError = error;
            await LoadAsync(patientId);
            return Page();
        }

        return RedirectToPage(new { section = "insurance", insuranceSaved = true });
    }

    public async Task<IActionResult> OnPostSaveHipaaAsync()
    {
        Section = "privacy";
        if (!int.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var patientId))
            return RedirectToPage("Login");

        if (!HipaaOptIn.HasValue)
        {
            FormError = "Please select Yes or No.";
            await LoadAsync(patientId);
            return Page();
        }

        var (success, error) = await _profileService.UpdatePatientHipaaOptInAsync(patientId, HipaaOptIn.Value);
        if (!success)
        {
            FormError = error;
            await LoadAsync(patientId);
            return Page();
        }

        return RedirectToPage(new { section = "privacy", privacySaved = true });
    }

    public async Task<IActionResult> OnPostSaveCookiesAsync()
    {
        Section = "privacy";
        if (!int.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var patientId))
            return RedirectToPage("Login");

        var (success, error) = await _profileService.UpdatePatientCookieOptOutAsync(patientId, CookieTrackingOptOut);
        if (!success)
        {
            FormError = error;
            await LoadAsync(patientId);
            return Page();
        }

        return RedirectToPage(new { section = "privacy", privacySaved = true });
    }

    public async Task<IActionResult> OnPostSavePermissionsAsync()
    {
        Section = "permissions";
        if (!int.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var patientId))
            return RedirectToPage("Login");

        var (success, error) = await _profileService.UpdatePatientAutofillAsync(patientId, AutofillEnabled);
        if (!success)
        {
            FormError = error;
            await LoadAsync(patientId);
            return Page();
        }

        return RedirectToPage(new { section = "permissions", permissionsSaved = true });
    }

    public async Task<IActionResult> OnPostSavePreferencesAsync()
    {
        Section = "preferences";
        if (!int.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var patientId))
            return RedirectToPage("Login");

        var (success, error) = await _preferences.SaveAsync(patientId, PreferenceInput);
        if (!success)
        {
            FormError = error;
            await LoadAsync(patientId);
            return Page();
        }

        return RedirectToPage(new { section = "preferences", preferencesSaved = true });
    }

    public async Task<IActionResult> OnPostSaveRemindersAsync()
    {
        Section = "reminders";
        if (!int.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var patientId))
            return RedirectToPage("Login");

        var (success, error) = await _reminders.SaveAsync(patientId, ReminderInput);
        if (!success)
        {
            FormError = error;
            await LoadAsync(patientId);
            return Page();
        }

        return RedirectToPage(new { section = "reminders", remindersSaved = true });
    }

    public async Task<IActionResult> OnGetDownloadSavedInformationAsync()
    {
        if (!int.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var patientId))
            return RedirectToPage("Login");

        var json = await _profileService.GetPatientSavedInformationJsonAsync(patientId);
        if (json == null) return NotFound();

        var fileName = $"nuvidoc-saved-information-{DateTime.UtcNow:yyyyMMdd}.json";
        return File(System.Text.Encoding.UTF8.GetBytes(json), "application/json", fileName);
    }

    public async Task<IActionResult> OnPostRequestDataAccessAsync()
    {
        Section = "privacy";
        if (!int.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var patientId))
            return RedirectToPage("Login");

        await LoadAsync(patientId);
        FormSuccess = "Data access requests will be available once identity verification (SMS PIN) is connected. You can already view most of your information under Personal information, Insurance, and Appointment history.";
        return Page();
    }

    public async Task<IActionResult> OnPostContactServiceAsync()
    {
        Section = "privacy";
        if (!int.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var patientId))
            return RedirectToPage("Login");

        await LoadAsync(patientId);
        FormSuccess = "Please contact support@nuvidoc.com for help updating your account information. Identity verification will be required before changes are made.";
        return Page();
    }

    public async Task<IActionResult> OnPostDeleteAccountAsync()
    {
        Section = "privacy";
        if (!int.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var patientId))
            return RedirectToPage("Login");

        await LoadAsync(patientId);
        FormSuccess = "Account deletion requests will be available once identity verification (SMS PIN) is connected. Deletion is permanent and cannot be reversed.";
        return Page();
    }

    public async Task<IActionResult> OnPostCancelAppointmentAsync(int appointmentId)
    {
        Section = "appointments";
        if (!int.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var patientId))
            return RedirectToPage("Login");

        var result = await _appointmentCancel.RequestCancelAsync(patientId, appointmentId);
        await LoadAsync(patientId);
        if (Profile == null)
            return NotFound();

        if (result.Success)
            FormSuccess = result.Message;
        else
            FormError = result.Message;

        return Page();
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
        var startOfToday = DateTime.Today;
        UpcomingAppointments = all
            .Where(a => a.StartsAt >= startOfToday
                        && !AppointmentStatuses.IsCanceled(a.Status)
                        && a.Status != AppointmentStatuses.Completed
                        && AppointmentStatuses.Normalize(a.Status) != AppointmentStatuses.PatientNoShow)
            .OrderBy(a => a.StartsAt)
            .ToList();

        Notifications = await _notifications.GetForPatientAsync(patientId);
        UnreadNotificationCount = await _notifications.CountUnreadAsync(patientId);

        InsuranceCatalog = await _insuranceService.GetCarriersWithPlansAsync();
        InsuranceProfile = await _insuranceProfile.GetAsync(patientId);
        InsuranceInput = MapInsuranceInput(InsuranceProfile);

        var privacy = await _profileService.GetPatientPrivacySettingsAsync(patientId);
        PrivacySettings = privacy ?? new PatientPrivacySettingsDto();
        HipaaOptIn = PrivacySettings.HipaaDataSharingOptIn;
        CookieTrackingOptOut = PrivacySettings.CookieTrackingOptOut;

        var permissions = await _profileService.GetPatientPermissionsSettingsAsync(patientId);
        AutofillEnabled = permissions?.AutofillEnabled ?? false;

        ReminderSettings = await _reminders.GetAsync(patientId);
        ReminderInput = new PatientReminderSettingsSaveRequest
        {
            Enable7Days = ReminderSettings.Enable7Days,
            Time7Days = ReminderSettings.Time7Days,
            Enable3Days = ReminderSettings.Enable3Days,
            Time3Days = ReminderSettings.Time3Days,
            Enable1Day = ReminderSettings.Enable1Day,
            Time1Day = ReminderSettings.Time1Day,
            EnableSameDay = ReminderSettings.EnableSameDay,
            SameDayHoursBefore = ReminderSettings.SameDayHoursBefore,
            ShowNotification = ReminderSettings.ShowNotification,
            EnableEmail = ReminderSettings.EnableEmail,
            EnableSms = ReminderSettings.EnableSms
        };

        PreferencePage = await _preferences.GetForEditAsync(patientId);
        if (PreferenceInput.Count == 0)
        {
            PreferenceInput = PreferencePage.Questions
                .Select(q => new PatientPreferenceAnswerInput
                {
                    QuestionId = q.QuestionId,
                    Answer = q.Answer,
                    FollowUp = q.FollowUp
                })
                .ToList();
        }
    }

    private static PatientInsuranceSaveModel MapInsuranceInput(PatientInsuranceProfileDto profile)
    {
        var dental = profile.Coverages.FirstOrDefault(c =>
            string.Equals(c.Type, PatientInsuranceTypes.Dental, StringComparison.OrdinalIgnoreCase));

        return new PatientInsuranceSaveModel
        {
            DentalCarrierId = dental?.InsuranceCarrierId,
            DentalPlanId = dental?.InsurancePlanId
        };
    }

    public PatientInsuranceRowDto? Coverage(string type) =>
        InsuranceProfile.Coverages.FirstOrDefault(c =>
            string.Equals(c.Type, type, StringComparison.OrdinalIgnoreCase));

    /// <summary>e.g. "Mon, Aug 10 · 9:00 AM (PST)" — start time only (no end).</summary>
    public static string FormatPstSlot(DateTime startsAt, DateTime endsAt)
    {
        _ = endsAt;
        var date = startsAt.ToString("ddd, MMM d", System.Globalization.CultureInfo.InvariantCulture);
        var start = startsAt.ToString("h:mm tt", System.Globalization.CultureInfo.InvariantCulture);
        return $"{date} · {start} (PST)";
    }

    private static string NormalizeSection(string? section) =>
        (section ?? "personal").Trim().ToLowerInvariant() switch
        {
            "family" or "family-members" => "family",
            "security" => "security",
            "notifications" or "notification" => "notifications",
            "appointments" or "appointment" or "upcoming" => "appointments",
            "appointment-history" or "history" or "past-appointments" => "appointment-history",
            "permissions" or "permission" => "permissions",
            "insurance" or "insurance-id" or "insurance-id-cards" => "insurance",
            "privacy" => "privacy",
            "preference" or "preferences" => "preferences",
            "reminder" or "reminders" => "reminders",
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
