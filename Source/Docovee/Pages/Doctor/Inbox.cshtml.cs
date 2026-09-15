using System.Globalization;
using System.Security.Claims;
using Docovee.BLL.Auth;
using Docovee.BLL.Services;
using Docovee.DS.Entities;
using Docovee.DS.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Docovee.Pages.Doctor;

[Authorize(Roles = AuthRoles.Doctor)]
[IgnoreAntiforgeryToken]
public class InboxModel : PageModel
{
    private readonly IAppointmentService _appointments;

    public InboxModel(IAppointmentService appointments) => _appointments = appointments;

    public string? Filter { get; private set; }
    public string? Search { get; private set; }
    public IReadOnlyList<DoctorAppointmentDto> Appointments { get; private set; } = Array.Empty<DoctorAppointmentDto>();
    public int NewCount { get; private set; }
    public int RescheduleCount { get; private set; }
    public int CancelledCount { get; private set; }

    public async Task<IActionResult> OnGetAsync(string? filter = null, string? q = null, CancellationToken cancellationToken = default)
    {
        if (!int.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var doctorId))
            return RedirectToPage("/Account/Login");

        Filter = string.IsNullOrWhiteSpace(filter) ? "all" : filter.Trim().ToLowerInvariant();
        Search = q?.Trim();

        var all = await _appointments.GetForDoctorAsync(doctorId, cancellationToken: cancellationToken);
        all = all.Where(a => AppointmentSources.IsNuvidocBooking(a.Source)).ToList();
        NewCount = all.Count(a => AppointmentStatuses.IsUnconfirmed(a.Status) || a.StartsAt is null);
        RescheduleCount = all.Count(a => AppointmentStatuses.IsRescheduled(a.Status));
        CancelledCount = all.Count(a => AppointmentStatuses.IsCanceled(a.Status));

        IEnumerable<DoctorAppointmentDto> filtered = Filter switch
        {
            "new" => all.Where(a => AppointmentStatuses.IsUnconfirmed(a.Status) || a.StartsAt is null),
            "reschedule" => all.Where(a => AppointmentStatuses.IsRescheduled(a.Status)),
            "cancelled" => all.Where(a => AppointmentStatuses.IsCanceled(a.Status)),
            _ => all
        };

        if (!string.IsNullOrWhiteSpace(Search))
        {
            filtered = filtered.Where(a =>
                a.PatientName.Contains(Search, StringComparison.OrdinalIgnoreCase)
                || (a.PatientPhone?.Contains(Search, StringComparison.OrdinalIgnoreCase) ?? false)
                || (a.VisitReason?.Contains(Search, StringComparison.OrdinalIgnoreCase) ?? false));
        }

        Appointments = filtered.ToList();
        return Page();
    }

    public async Task<IActionResult> OnPostCreateBookingAsync(
        [FromForm] int appointmentId,
        [FromForm] string date,
        [FromForm] string timeLabel,
        CancellationToken cancellationToken = default)
    {
        if (!int.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var doctorId))
            return new JsonResult(new { success = false, error = "Not signed in." });

        if (!TryParseSlot(date, timeLabel, out var startsAt, out var error))
            return new JsonResult(new { success = false, error });

        var result = await _appointments.SetSlotAsDoctorAsync(
            doctorId, appointmentId, startsAt, isReschedule: false, cancellationToken);
        if (!result.Success)
            return new JsonResult(new { success = false, error = result.Error });

        return new JsonResult(new
        {
            success = true,
            startsAt = result.Appointment!.StartsAt?.ToString("MMM d, yyyy · h:mm tt"),
            status = result.Appointment.Status,
            statusLabel = AppointmentStatuses.DisplayLabel(result.Appointment.Status)
        });
    }

    public async Task<IActionResult> OnPostRescheduleAsync(
        [FromForm] int appointmentId,
        [FromForm] string date,
        [FromForm] string timeLabel,
        CancellationToken cancellationToken = default)
    {
        if (!int.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var doctorId))
            return new JsonResult(new { success = false, error = "Not signed in." });

        if (!TryParseSlot(date, timeLabel, out var startsAt, out var error))
            return new JsonResult(new { success = false, error });

        var result = await _appointments.SetSlotAsDoctorAsync(
            doctorId, appointmentId, startsAt, isReschedule: true, cancellationToken);
        if (!result.Success)
            return new JsonResult(new { success = false, error = result.Error });

        return new JsonResult(new
        {
            success = true,
            startsAt = result.Appointment!.StartsAt?.ToString("MMM d, yyyy · h:mm tt"),
            status = result.Appointment.Status,
            statusLabel = AppointmentStatuses.DisplayLabel(result.Appointment.Status)
        });
    }

    public async Task<IActionResult> OnPostCancelAsync(
        [FromForm] int appointmentId,
        CancellationToken cancellationToken = default)
    {
        if (!int.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var doctorId))
            return new JsonResult(new { success = false, error = "Not signed in." });

        var result = await _appointments.CancelAsDoctorAsync(doctorId, appointmentId, cancellationToken);
        if (!result.Success)
            return new JsonResult(new { success = false, error = result.Error });

        return new JsonResult(new
        {
            success = true,
            startsAt = result.Appointment!.StartsAt?.ToString("MMM d, yyyy · h:mm tt"),
            status = result.Appointment.Status,
            statusLabel = AppointmentStatuses.DisplayLabel(result.Appointment.Status)
        });
    }

    private static bool TryParseSlot(string? date, string? timeLabel, out DateTime startsAt, out string? error)
    {
        startsAt = default;
        error = null;
        if (!DateOnly.TryParse(date, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
        {
            error = "Invalid date.";
            return false;
        }

        if (!AppointmentService.TryParseTimeLabel(timeLabel, out var time))
        {
            error = "Invalid time.";
            return false;
        }

        startsAt = d.ToDateTime(time);
        return true;
    }

    public static string RelativeTime(DateTime utcOrUnspecified)
    {
        var when = utcOrUnspecified.Kind == DateTimeKind.Unspecified
            ? DateTime.SpecifyKind(utcOrUnspecified, DateTimeKind.Utc)
            : utcOrUnspecified.ToUniversalTime();
        var span = DateTime.UtcNow - when;
        if (span.TotalMinutes < 1) return "Just now";
        if (span.TotalMinutes < 60) return $"{(int)span.TotalMinutes} min ago";
        if (span.TotalHours < 24) return $"{(int)span.TotalHours} hour{((int)span.TotalHours == 1 ? "" : "s")} ago";
        if (span.TotalDays < 7) return $"{(int)span.TotalDays} day{((int)span.TotalDays == 1 ? "" : "s")} ago";
        return when.ToLocalTime().ToString("MMM d, yyyy");
    }

    public static string StatusLabel(string status) => AppointmentStatuses.DisplayLabel(status);

    public static string StatusCss(string status)
    {
        var s = AppointmentStatuses.Normalize(status);
        return s switch
        {
            AppointmentStatuses.Unconfirmed => "green",
            AppointmentStatuses.Confirmed => "green",
            AppointmentStatuses.PracticeRescheduled or AppointmentStatuses.PatientRescheduled => "blue",
            AppointmentStatuses.PracticeCanceled or AppointmentStatuses.PatientCanceled or AppointmentStatuses.PatientNoShow => "yellow",
            _ => "yellow"
        };
    }
}
