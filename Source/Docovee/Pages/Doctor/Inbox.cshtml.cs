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
        NewCount = all.Count(a => AppointmentStatuses.IsUnconfirmed(a.Status));
        RescheduleCount = all.Count(a => AppointmentStatuses.IsRescheduled(a.Status));
        CancelledCount = all.Count(a => AppointmentStatuses.IsCanceled(a.Status));

        IEnumerable<DoctorAppointmentDto> filtered = Filter switch
        {
            "new" => all.Where(a => AppointmentStatuses.IsUnconfirmed(a.Status)),
            "reschedule" => all.Where(a => AppointmentStatuses.IsRescheduled(a.Status)),
            "cancelled" => all.Where(a => AppointmentStatuses.IsCanceled(a.Status)),
            _ => all
        };

        if (!string.IsNullOrWhiteSpace(Search))
        {
            filtered = filtered.Where(a =>
                a.PatientName.Contains(Search, StringComparison.OrdinalIgnoreCase)
                || (a.VisitReason?.Contains(Search, StringComparison.OrdinalIgnoreCase) ?? false));
        }

        Appointments = filtered.ToList();
        return Page();
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
