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
public class IndexModel : PageModel
{
    private readonly IProfileService _profileService;
    private readonly IAppointmentService _appointments;

    public IndexModel(IProfileService profileService, IAppointmentService appointments)
    {
        _profileService = profileService;
        _appointments = appointments;
    }

    public string DisplayName { get; private set; } = "Doctor";

    public int AppointmentsThisWeek { get; private set; }
    public int TotalBookings { get; private set; }
    public int PendingAppointments { get; private set; }
    public int ConfirmedAppointments { get; private set; }
    public int CompletedAppointments { get; private set; }
    public int AppointmentsToday { get; private set; }
    public int BookingsThisMonth { get; private set; }
    public int BookingsLastMonth { get; private set; }
    public int CancelledThisMonth { get; private set; }

    public int TotalReviews { get; private set; }
    public decimal? AverageRating { get; private set; }
    public int GoogleReviewCount { get; private set; }
    public decimal GoogleRating { get; private set; }
    public int CompletionRatePercent { get; private set; }
    public int? BookingsMonthChangePercent { get; private set; }

    public IReadOnlyList<UpcomingAppointmentRow> UpcomingThisWeek { get; private set; }
        = Array.Empty<UpcomingAppointmentRow>();

    public IReadOnlyList<InboxPulseRow> RecentNotifications { get; private set; }
        = Array.Empty<InboxPulseRow>();

    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken = default)
    {
        if (!int.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var doctorId))
            return RedirectToPage("/Account/Login");

        var profile = await _profileService.GetDoctorProfileAsync(doctorId);
        if (profile != null)
        {
            DisplayName = !string.IsNullOrWhiteSpace(profile.PracticeName) ? profile.PracticeName! : profile.Name;
            TotalReviews = profile.PatientReviewCount;
            AverageRating = profile.PatientReviewAverage;
            GoogleReviewCount = profile.GoogleReviewCount;
            GoogleRating = profile.GoogleRating;
        }

        var all = await _appointments.GetForDoctorAsync(doctorId, cancellationToken: cancellationToken);

        var today = DateTime.Today;
        var weekStart = StartOfWeek(today);
        var weekEnd = weekStart.AddDays(7);
        var monthStart = new DateTime(today.Year, today.Month, 1);
        var nextMonth = monthStart.AddMonths(1);
        var lastMonthStart = monthStart.AddMonths(-1);

        bool NotCancelled(DoctorAppointmentDto a) => !AppointmentStatuses.IsCanceled(a.Status);

        AppointmentsThisWeek = all.Count(a =>
            NotCancelled(a) && a.StartsAt >= weekStart && a.StartsAt < weekEnd);

        TotalBookings = all.Count(a =>
            NotCancelled(a)
            && string.Equals(a.Source, AppointmentSources.PublicProfile, StringComparison.OrdinalIgnoreCase));

        PendingAppointments = all.Count(a => AppointmentStatuses.NeedsDoctorAttention(a.Status));

        ConfirmedAppointments = all.Count(a =>
            string.Equals(AppointmentStatuses.Normalize(a.Status), AppointmentStatuses.Confirmed, StringComparison.OrdinalIgnoreCase)
            && a.StartsAt >= today);

        CompletedAppointments = all.Count(a =>
            string.Equals(a.Status, AppointmentStatuses.Completed, StringComparison.OrdinalIgnoreCase));

        AppointmentsToday = all.Count(a =>
            NotCancelled(a) && a.StartsAt.Date == today);

        BookingsThisMonth = all.Count(a =>
            NotCancelled(a) && a.StartsAt >= monthStart && a.StartsAt < nextMonth);

        BookingsLastMonth = all.Count(a =>
            NotCancelled(a) && a.StartsAt >= lastMonthStart && a.StartsAt < monthStart);

        CancelledThisMonth = all.Count(a =>
            AppointmentStatuses.IsCanceled(a.Status)
            && a.UpdatedAt >= monthStart && a.UpdatedAt < nextMonth);

        var decided = CompletedAppointments + all.Count(a => AppointmentStatuses.IsCanceled(a.Status));
        CompletionRatePercent = decided > 0
            ? (int)Math.Round(100.0 * CompletedAppointments / decided)
            : 0;

        if (BookingsLastMonth > 0)
            BookingsMonthChangePercent = (int)Math.Round(100.0 * (BookingsThisMonth - BookingsLastMonth) / BookingsLastMonth);
        else if (BookingsThisMonth > 0)
            BookingsMonthChangePercent = 100;

        UpcomingThisWeek = all
            .Where(a =>
                NotCancelled(a)
                && a.StartsAt >= today
                && a.StartsAt < weekEnd)
            .OrderBy(a => a.StartsAt)
            .Take(8)
            .Select(a => new UpcomingAppointmentRow
            {
                PatientName = a.PatientName,
                VisitReason = a.VisitReason,
                StartsAt = a.StartsAt,
                Status = a.Status,
                StatusLabel = StatusLabel(a.Status),
                StatusTone = StatusTone(a.Status)
            })
            .ToList();

        RecentNotifications = all
            .Where(a => a.Status is AppointmentStatuses.New or AppointmentStatuses.Reschedule or AppointmentStatuses.Cancelled)
            .OrderByDescending(a => a.UpdatedAt)
            .Take(5)
            .Select(a => new InboxPulseRow
            {
                AppointmentId = a.Id,
                PatientName = a.PatientName,
                VisitReason = a.VisitReason,
                StartsAt = a.StartsAt,
                UpdatedAt = a.UpdatedAt,
                StatusLabel = StatusLabel(a.Status),
                StatusTone = StatusTone(a.Status)
            })
            .ToList();

        return Page();
    }

    private static DateTime StartOfWeek(DateTime date)
    {
        var diff = ((int)date.DayOfWeek - (int)DayOfWeek.Monday + 7) % 7;
        return date.Date.AddDays(-diff);
    }

    private static string StatusLabel(string status) => AppointmentStatuses.DisplayLabel(status);

    private static string StatusTone(string status)
    {
        var s = AppointmentStatuses.Normalize(status);
        return s switch
        {
            AppointmentStatuses.Unconfirmed => "amber",
            AppointmentStatuses.PracticeRescheduled or AppointmentStatuses.PatientRescheduled => "blue",
            AppointmentStatuses.Confirmed => "green",
            AppointmentStatuses.Completed => "muted",
            _ => "muted"
        };
    }

    public sealed class UpcomingAppointmentRow
    {
        public string PatientName { get; init; } = "";
        public string VisitReason { get; init; } = "";
        public DateTime StartsAt { get; init; }
        public string Status { get; init; } = "";
        public string StatusLabel { get; init; } = "";
        public string StatusTone { get; init; } = "muted";
    }

    public sealed class InboxPulseRow
    {
        public int AppointmentId { get; init; }
        public string PatientName { get; init; } = "";
        public string VisitReason { get; init; } = "";
        public DateTime StartsAt { get; init; }
        public DateTime UpdatedAt { get; init; }
        public string StatusLabel { get; init; } = "";
        public string StatusTone { get; init; } = "muted";
    }
}
