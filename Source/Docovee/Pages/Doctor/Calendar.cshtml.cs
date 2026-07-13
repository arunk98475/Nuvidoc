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
    public IReadOnlyList<int> HourStarts { get; } = [9, 10, 11, 12, 13, 14, 15, 16, 17, 18];

    public async Task<IActionResult> OnGetAsync(string? week = null, CancellationToken cancellationToken = default)
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
        if (!DateOnly.TryParse(week, out var anchor))
            anchor = today;

        // Week starts Sunday (matches existing 6-day Sun–Fri grid; include Sat in list but UI uses 6 cols)
        var daysFromSunday = (int)anchor.DayOfWeek;
        WeekStart = anchor.AddDays(-daysFromSunday);
        MonthFocus = anchor;
        WeekDays = Enumerable.Range(0, 6).Select(i => WeekStart.AddDays(i)).ToList();

        var from = WeekStart.ToDateTime(TimeOnly.MinValue);
        var to = WeekStart.AddDays(7).ToDateTime(TimeOnly.MinValue);
        var all = await _appointments.GetForDoctorAsync(doctorId, fromUtc: from, toUtc: to, cancellationToken: cancellationToken);
        Appointments = all
            .Where(a => a.Status is AppointmentStatuses.New or AppointmentStatuses.Confirmed or AppointmentStatuses.Reschedule)
            .OrderBy(a => a.StartsAt)
            .ToList();

        return Page();
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
        DateOnly? dateOfBirth = null;
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
                dateOfBirth = patient.DateOfBirth;
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
            status = appointment.Status,
            statusLabel = StatusLabel(appointment.Status),
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
                status = h.Status,
                statusLabel = StatusLabel(h.Status)
            })
        });
    }

    public DoctorAppointmentDto? FindAppointment(DateOnly day, int hour)
    {
        return Appointments.FirstOrDefault(a =>
        {
            var local = a.StartsAt;
            return DateOnly.FromDateTime(local) == day && local.Hour == hour;
        });
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

    private static string StatusLabel(string status) => status switch
    {
        AppointmentStatuses.New => "New booking",
        AppointmentStatuses.Reschedule => "Reschedule",
        AppointmentStatuses.Confirmed => "Confirmed",
        AppointmentStatuses.Completed => "Completed",
        AppointmentStatuses.Cancelled => "Cancelled",
        _ => status
    };

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
