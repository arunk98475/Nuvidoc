using System.Security.Claims;
using Docovee.BLL.Auth;
using Docovee.BLL.Services;
using Docovee.DS.Entities;
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
    public int ActionRequiredCount { get; private set; }
    public int BookingsThisMonth { get; private set; }
    public int BookingsLastMonthSamePoint { get; private set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken = default)
    {
        if (!int.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var doctorId))
            return RedirectToPage("/Account/Login");

        var profile = await _profileService.GetDoctorProfileAsync(doctorId);
        if (profile != null)
            DisplayName = !string.IsNullOrWhiteSpace(profile.PracticeName) ? profile.PracticeName! : profile.Name;

        ActionRequiredCount = await _appointments.CountActionRequiredAsync(doctorId, cancellationToken);

        var now = DateTime.Today;
        var monthStart = new DateTime(now.Year, now.Month, 1);
        var nextMonth = monthStart.AddMonths(1);
        var lastMonthStart = monthStart.AddMonths(-1);
        var lastMonthSamePoint = lastMonthStart.AddDays(Math.Min(now.Day, DateTime.DaysInMonth(lastMonthStart.Year, lastMonthStart.Month)) - 1)
            .AddDays(1);

        var monthAppts = await _appointments.GetForDoctorAsync(
            doctorId, fromUtc: monthStart, toUtc: nextMonth, cancellationToken: cancellationToken);
        BookingsThisMonth = monthAppts.Count(a =>
            a.Status is not AppointmentStatuses.Cancelled);

        var lastMonthAppts = await _appointments.GetForDoctorAsync(
            doctorId, fromUtc: lastMonthStart, toUtc: lastMonthSamePoint, cancellationToken: cancellationToken);
        BookingsLastMonthSamePoint = lastMonthAppts.Count(a =>
            a.Status is not AppointmentStatuses.Cancelled);

        return Page();
    }
}
