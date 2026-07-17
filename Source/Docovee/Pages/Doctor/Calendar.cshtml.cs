using System.Security.Claims;
using System.Text.Json;
using Docovee.BLL.Auth;
using Docovee.BLL.Services;
using Docovee.DS;
using Docovee.DS.Entities;
using Docovee.DS.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Docovee.Pages.Doctor;

[Authorize(Roles = AuthRoles.Doctor)]
public class CalendarModel : PageModel
{
    private readonly IProfileService _profileService;
    private readonly IAppointmentService _appointments;
    private readonly DocoveeDbContext _db;

    public CalendarModel(
        IProfileService profileService,
        IAppointmentService appointments,
        DocoveeDbContext db)
    {
        _profileService = profileService;
        _appointments = appointments;
        _db = db;
    }

    public string ProviderName { get; private set; } = "Doctor";
    public string? PracticeName { get; private set; }
    public string? PhotoUrl { get; private set; }
    public DateOnly WeekStart { get; private set; }
    public DateOnly MonthFocus { get; private set; }
    public IReadOnlyList<DoctorAppointmentDto> Appointments { get; private set; } = Array.Empty<DoctorAppointmentDto>();
    public IReadOnlyList<DateOnly> WeekDays { get; private set; } = Array.Empty<DateOnly>();
    public IReadOnlyList<int> HourStarts { get; private set; } = [8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18];

    public async Task<IActionResult> OnGetAsync(string? week = null, int? appointmentId = null, CancellationToken cancellationToken = default)
    {
        if (!int.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var doctorId))
            return RedirectToPage("/Account/Login");

        var profile = await _profileService.GetDoctorProfileAsync(doctorId);
        if (profile != null)
        {
            ProviderName = profile.Name;
            PracticeName = profile.PracticeName;
            PhotoUrl = profile.PhotoUrl;
        }

        var today = DateOnly.FromDateTime(DateTime.Today);
        DateOnly anchor;

        if (appointmentId is > 0 && string.IsNullOrWhiteSpace(week))
        {
            var target = await _appointments.GetForDoctorByIdAsync(doctorId, appointmentId.Value, cancellationToken);
            anchor = target != null
                ? DateOnly.FromDateTime(target.StartsAt)
                : today;
        }
        else if (!DateOnly.TryParse(week, out anchor))
        {
            anchor = today;
        }

        // Week starts Sunday; show Sun–Sat on the grid.
        var daysFromSunday = (int)anchor.DayOfWeek;
        WeekStart = anchor.AddDays(-daysFromSunday);
        MonthFocus = anchor;
        WeekDays = Enumerable.Range(0, 7).Select(i => WeekStart.AddDays(i)).ToList();

        var from = WeekStart.ToDateTime(TimeOnly.MinValue);
        var to = WeekStart.AddDays(7).ToDateTime(TimeOnly.MinValue);
        var all = await _appointments.GetForDoctorAsync(doctorId, fromUtc: from, toUtc: to, cancellationToken: cancellationToken);
        Appointments = all
            .Where(a => AppointmentStatuses.IsActive(a.Status))
            .OrderBy(a => a.StartsAt)
            .ToList();

        HourStarts = BuildHourStarts(Appointments);

        return Page();
    }

    public async Task<IActionResult> OnPostUpdateStatusAsync(
        int appointmentId,
        string status,
        CancellationToken cancellationToken = default)
    {
        if (!int.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var doctorId))
            return new JsonResult(new { message = "Not signed in." }) { StatusCode = StatusCodes.Status401Unauthorized };

        var (success, error, newStatus, statusLabel) = await _appointments.UpdateStatusAsync(
            doctorId,
            appointmentId,
            status,
            cancellationToken);

        if (!success)
            return BadRequest(new { message = error ?? "Could not update status." });

        return new JsonResult(new
        {
            success = true,
            status = newStatus,
            statusLabel,
            remainsOnCalendar = AppointmentStatuses.IsActive(newStatus)
        });
    }

    public async Task<IActionResult> OnGetPanelAsync(int appointmentId, CancellationToken cancellationToken = default)
    {
        if (!int.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var doctorId))
            return Unauthorized();

        var appointment = await _appointments.GetForDoctorByIdAsync(doctorId, appointmentId, cancellationToken);
        if (appointment == null)
            return NotFound(new { message = "Appointment not found." });

        var profile = await _profileService.GetDoctorProfileAsync(doctorId, cancellationToken);

        string fullName = appointment.PatientName;
        string? phone = appointment.PatientPhone;
        string? email = appointment.PatientEmail;
        DateOnly? dateOfBirth = appointment.PatientDateOfBirth;
        DateTime? memberSince = null;
        var hasAccount = false;
        string? preferenceJson = null;

        IReadOnlyList<DoctorAppointmentDto> history;

        if (appointment.PatientId is int patientId)
        {
            var patient = await _db.Patients.AsNoTracking()
                .FirstOrDefaultAsync(p => p.Id == patientId, cancellationToken);
            if (patient != null)
            {
                hasAccount = true;
                fullName = patient.FullName;
                phone = string.IsNullOrWhiteSpace(patient.Phone) ? appointment.PatientPhone : patient.Phone;
                email = string.IsNullOrWhiteSpace(patient.Username) ? appointment.PatientEmail : patient.Username;
                dateOfBirth = patient.DateOfBirth != default
                    && patient.DateOfBirth != new DateOnly(1990, 1, 1)
                    ? patient.DateOfBirth
                    : dateOfBirth;
                memberSince = patient.CreatedAt;
                preferenceJson = patient.PreferenceProfileJson;
            }

            history = (await _appointments.GetForDoctorAsync(doctorId, cancellationToken: cancellationToken))
                .Where(a => a.PatientId == patientId)
                .OrderByDescending(a => a.StartsAt)
                .Take(20)
                .ToList();
        }
        else
        {
            history = (await _appointments.GetForDoctorAsync(doctorId, cancellationToken: cancellationToken))
                .Where(a =>
                    a.Id == appointment.Id
                    || (!string.IsNullOrWhiteSpace(appointment.PatientEmail)
                        && string.Equals(a.PatientEmail, appointment.PatientEmail, StringComparison.OrdinalIgnoreCase))
                    || string.Equals(a.PatientName, appointment.PatientName, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(a => a.StartsAt)
                .Take(20)
                .ToList();
        }

        int? ageYears = null;
        if (dateOfBirth is DateOnly dob)
        {
            var today = DateOnly.FromDateTime(DateTime.Today);
            ageYears = today.Year - dob.Year;
            if (dob > today.AddYears(-ageYears.Value))
                ageYears--;
        }

        var insurance = await ResolveInsuranceAsync(
            doctorId,
            appointment.PatientId,
            appointment.SearchSessionId,
            preferenceJson,
            cancellationToken);

        return new JsonResult(new
        {
            appointmentId = appointment.Id,
            status = AppointmentStatuses.Normalize(appointment.Status),
            statusLabel = StatusLabel(appointment.Status),
            canConfirm = AppointmentStatuses.CanConfirm(appointment.Status),
            canCancel = AppointmentStatuses.CanPracticeCancel(appointment.Status),
            canMarkNoShow = AppointmentStatuses.CanMarkNoShow(appointment.Status),
            patientName = fullName,
            patientType = history.Count > 1 ? "Existing patient" : "New patient",
            hasAccount,
            dateOfBirth = dateOfBirth?.ToString("MM/dd/yyyy"),
            ageYears,
            phone,
            email,
            memberSince = memberSince?.ToString("MMM d, yyyy"),
            startsAt = appointment.StartsAt.ToString("dddd MMM d 'at' h:mm tt"),
            startsAtIso = appointment.StartsAt.ToString("o"),
            isPast = DateOnly.FromDateTime(appointment.StartsAt) < DateOnly.FromDateTime(DateTime.Today),
            visitReason = appointment.VisitReason,
            providerName = profile?.Name ?? ProviderName,
            practiceName = profile?.PracticeName,
            location = FormatLocation(profile),
            insurance,
            history = history.Select(h => new
            {
                id = h.Id,
                startsAt = h.StartsAt.ToString("MMM d, yyyy · h:mm tt"),
                visitReason = h.VisitReason,
                status = AppointmentStatuses.Normalize(h.Status),
                statusLabel = StatusLabel(h.Status)
            })
        });
    }

    public DoctorAppointmentDto? FindAppointment(DateOnly day, int hour)
    {
        var slotStart = day.ToDateTime(new TimeOnly(hour, 0));
        var slotEnd = slotStart.AddHours(1);
        return Appointments.FirstOrDefault(a => a.StartsAt >= slotStart && a.StartsAt < slotEnd);
    }

    private static IReadOnlyList<int> BuildHourStarts(IReadOnlyList<DoctorAppointmentDto> appointments)
    {
        const int defaultStart = 8;
        const int defaultEnd = 18;
        if (appointments.Count == 0)
            return Enumerable.Range(defaultStart, defaultEnd - defaultStart + 1).ToList();

        var minHour = appointments.Min(a => a.StartsAt.Hour);
        var maxHour = appointments.Max(a => a.StartsAt.Hour);
        minHour = Math.Max(6, Math.Min(minHour, defaultStart));
        maxHour = Math.Min(21, Math.Max(maxHour, defaultEnd));
        return Enumerable.Range(minHour, maxHour - minHour + 1).ToList();
    }

    public static string ShortName(string name)
    {
        var parts = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return name;
        if (parts.Length == 1) return parts[0];
        return $"{parts[0]} {parts[^1][0]}.";
    }

    private async Task<object> ResolveInsuranceAsync(
        int doctorId,
        int? patientId,
        int? searchSessionId,
        string? preferenceJson,
        CancellationToken cancellationToken)
    {
        string? planText = null;
        string? category = null;
        string? carrierName = null;
        DateTime? submittedOn = null;

        if (searchSessionId is int sessionId)
        {
            var session = await _db.SearchSessions.AsNoTracking()
                .Include(s => s.InsuranceCarrier)
                .FirstOrDefaultAsync(s => s.Id == sessionId, cancellationToken);
            if (session != null)
            {
                planText = session.InsurancePlanText;
                carrierName = session.InsuranceCarrier?.Name;
                submittedOn = session.UpdatedAt;
            }
        }

        if (patientId is int pid)
        {
            if (string.IsNullOrWhiteSpace(planText) || string.IsNullOrWhiteSpace(carrierName))
            {
                var lastSession = await _db.SearchSessions.AsNoTracking()
                    .Include(s => s.InsuranceCarrier)
                    .Where(s => s.PatientId == pid
                        && ((s.InsurancePlanText != null && s.InsurancePlanText != "")
                            || s.InsuranceCarrierId != null))
                    .OrderByDescending(s => s.UpdatedAt)
                    .FirstOrDefaultAsync(cancellationToken);

                if (lastSession != null)
                {
                    planText ??= lastSession.InsurancePlanText;
                    carrierName ??= lastSession.InsuranceCarrier?.Name;
                    submittedOn ??= lastSession.UpdatedAt;
                }
            }

            if (!string.IsNullOrWhiteSpace(preferenceJson))
            {
                try
                {
                    using var doc = JsonDocument.Parse(preferenceJson);
                    var root = doc.RootElement;
                    if (string.IsNullOrWhiteSpace(planText)
                        && root.TryGetProperty("insurancePreference", out var pref)
                        && pref.ValueKind == JsonValueKind.String)
                    {
                        planText = pref.GetString();
                    }

                    if (root.TryGetProperty("insuranceCategory", out var cat)
                        && cat.ValueKind == JsonValueKind.String)
                    {
                        category = cat.GetString();
                    }
                }
                catch
                {
                    // Ignore malformed preference JSON.
                }
            }
        }

        var doctorCarriers = await _db.DoctorInsurances.AsNoTracking()
            .Where(di => di.DoctorId == doctorId)
            .Select(di => di.InsuranceCarrier.Name)
            .ToListAsync(cancellationToken);

        var isSelfPay = string.Equals(planText, "Self-pay", StringComparison.OrdinalIgnoreCase)
            || string.Equals(category, "self-pay", StringComparison.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(carrierName) && !string.IsNullOrWhiteSpace(planText) && !isSelfPay)
        {
            carrierName = doctorCarriers.FirstOrDefault(c =>
                planText.Contains(c, StringComparison.OrdinalIgnoreCase)
                || c.Contains(planText, StringComparison.OrdinalIgnoreCase));
            carrierName ??= planText;
        }

        bool? inNetwork = null;
        if (!isSelfPay && (!string.IsNullOrWhiteSpace(planText) || !string.IsNullOrWhiteSpace(carrierName)))
            inNetwork = InsuranceMatchHelper.IsPlanAccepted(planText ?? carrierName, doctorCarriers);

        var hasDetails = !string.IsNullOrWhiteSpace(planText)
            || !string.IsNullOrWhiteSpace(carrierName)
            || isSelfPay;

        return new
        {
            hasDetails,
            isSelfPay,
            inNetwork,
            carrier = isSelfPay ? null : carrierName,
            planName = isSelfPay ? "Self-pay" : planText,
            memberId = (string?)null,
            category,
            submittedOn = submittedOn?.ToString("MM/dd/yyyy"),
            acceptedByPractice = doctorCarriers
        };
    }

    private static string StatusLabel(string status) => AppointmentStatuses.DisplayLabel(status);

    private static string? FormatLocation(DoctorProfileDto? profile)
    {
        if (profile == null) return null;
        if (!string.IsNullOrWhiteSpace(profile.Address))
            return profile.Address;
        var parts = new[] { profile.City, profile.State, profile.ZipCode }
            .Where(p => !string.IsNullOrWhiteSpace(p) && p != "NA");
        var joined = string.Join(", ", parts);
        return string.IsNullOrWhiteSpace(joined) ? profile.PracticeName : joined;
    }
}
