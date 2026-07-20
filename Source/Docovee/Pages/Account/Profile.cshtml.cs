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

    public ProfileModel(
        IProfileService profileService,
        IAppointmentService appointments,
        IInsuranceService insuranceService,
        IPatientInsuranceProfileService insuranceProfile)
    {
        _profileService = profileService;
        _appointments = appointments;
        _insuranceService = insuranceService;
        _insuranceProfile = insuranceProfile;
    }

    public PatientProfileDto? Profile { get; set; }
    public PatientInsuranceProfileDto InsuranceProfile { get; set; } = new();
    public IReadOnlyList<InsuranceCarrierDto> InsuranceCatalog { get; set; } = Array.Empty<InsuranceCarrierDto>();
    public string Section { get; set; } = "personal";
    public string? EditField { get; set; }
    public bool Saved { get; set; }
    public bool PasswordChanged { get; set; }
    public bool InsuranceSaved { get; set; }
    public bool PrivacySaved { get; set; }
    public bool PermissionsSaved { get; set; }
    public PatientPrivacySettingsDto PrivacySettings { get; set; } = new();
    public string? FormError { get; set; }
    public string? FormSuccess { get; set; }

    public IReadOnlyList<PatientAppointmentDto> UpcomingAppointments { get; set; } = Array.Empty<PatientAppointmentDto>();

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

    public async Task<IActionResult> OnGetAsync(
        string? section = null,
        string? edit = null,
        bool saved = false,
        bool passwordChanged = false,
        bool insuranceSaved = false,
        bool privacySaved = false,
        bool permissionsSaved = false)
    {
        var normalized = NormalizeSection(section);
        if (normalized == "history")
            return RedirectToPage("/Account/Appointments");

        Section = normalized;
        EditField = NormalizeEditField(edit);
        Saved = saved;
        PasswordChanged = passwordChanged;
        InsuranceSaved = insuranceSaved;
        PrivacySaved = privacySaved;
        PermissionsSaved = permissionsSaved;

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

    public async Task<IActionResult> OnPostSaveInsuranceAsync(
        IFormFile? MedicalCardPhoto,
        IFormFile? IdCardPhoto)
    {
        Section = "insurance";
        if (!int.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var patientId))
            return RedirectToPage("Login");

        var (success, error) = await _insuranceProfile.SaveAsync(
            patientId,
            InsuranceInput,
            MedicalCardPhoto,
            IdCardPhoto);

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
        FormSuccess = "Data access requests will be available once identity verification (SMS PIN) is connected. You can already view most of your information under Personal information, Insurance & ID Cards, and Appointment history.";
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

        InsuranceCatalog = await _insuranceService.GetCarriersWithPlansAsync();
        InsuranceProfile = await _insuranceProfile.GetAsync(patientId);
        InsuranceInput = MapInsuranceInput(InsuranceProfile);

        var privacy = await _profileService.GetPatientPrivacySettingsAsync(patientId);
        PrivacySettings = privacy ?? new PatientPrivacySettingsDto();
        HipaaOptIn = PrivacySettings.HipaaDataSharingOptIn;
        CookieTrackingOptOut = PrivacySettings.CookieTrackingOptOut;

        var permissions = await _profileService.GetPatientPermissionsSettingsAsync(patientId);
        AutofillEnabled = permissions?.AutofillEnabled ?? false;
    }

    private static PatientInsuranceSaveModel MapInsuranceInput(PatientInsuranceProfileDto profile)
    {
        var byType = profile.Coverages.ToDictionary(c => c.Type, StringComparer.OrdinalIgnoreCase);
        byType.TryGetValue(PatientInsuranceTypes.Medical, out var medical);
        byType.TryGetValue(PatientInsuranceTypes.Dental, out var dental);
        byType.TryGetValue(PatientInsuranceTypes.Vision, out var vision);
        byType.TryGetValue(PatientInsuranceTypes.Secondary, out var secondary);

        return new PatientInsuranceSaveModel
        {
            MedicalCarrierId = medical?.InsuranceCarrierId,
            MedicalPlanId = medical?.InsurancePlanId,
            MedicalMemberId = medical?.MemberId,
            DentalCarrierId = dental?.InsuranceCarrierId,
            DentalPlanId = dental?.InsurancePlanId,
            DentalMemberId = dental?.MemberId,
            VisionCarrierId = vision?.InsuranceCarrierId,
            VisionPlanId = vision?.InsurancePlanId,
            VisionMemberId = vision?.MemberId,
            SecondaryCarrierName = secondary?.CustomCarrierName,
            SecondaryPlanName = secondary?.CustomPlanName,
            SecondaryMemberId = secondary?.MemberId
        };
    }

    public PatientInsuranceRowDto? Coverage(string type) =>
        InsuranceProfile.Coverages.FirstOrDefault(c =>
            string.Equals(c.Type, type, StringComparison.OrdinalIgnoreCase));

    private static string NormalizeSection(string? section) =>
        (section ?? "personal").Trim().ToLowerInvariant() switch
        {
            "family" or "family-members" => "family",
            "security" => "security",
            "notifications" or "notification" => "notifications",
            "permissions" or "permission" => "permissions",
            "insurance" or "insurance-id" or "insurance-id-cards" => "insurance",
            "privacy" => "privacy",
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
