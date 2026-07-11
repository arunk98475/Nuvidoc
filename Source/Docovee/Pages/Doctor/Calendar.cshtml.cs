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
public class CalendarModel : PageModel
{
    private readonly IProfileService _profileService;
    private readonly IAppointmentService _appointments;

    public CalendarModel(IProfileService profileService, IAppointmentService appointments)
    {
        _profileService = profileService;
        _appointments = appointments;
    }

    public string ProviderName { get; private set; } = "Doctor";
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
}
