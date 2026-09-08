using Docovee.BLL.Services;
using Docovee.DS;
using Docovee.DS.Entities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Docovee.Pages.Admin.Dashboard;

public class IndexModel : PageModel
{
    private readonly DocoveeDbContext _db;
    private readonly IBlogGenerationService _blogGen;

    public IndexModel(DocoveeDbContext db, IBlogGenerationService blogGen)
    {
        _db = db;
        _blogGen = blogGen;
    }

    public int DoctorCount { get; private set; }
    public int PatientCount { get; private set; }
    public int BookingCount { get; private set; }
    public int BookingsThisWeek { get; private set; }
    public DateOnly WeekStart { get; private set; }
    public DateOnly WeekEnd { get; private set; }
    public IReadOnlyList<AdminNotification> UnreadNotifications { get; private set; } =
        Array.Empty<AdminNotification>();

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        var (weekStartUtc, weekEndUtc, weekStartLocal, weekEndLocal) = GetCurrentWeekBounds();
        WeekStart = DateOnly.FromDateTime(weekStartLocal);
        WeekEnd = DateOnly.FromDateTime(weekEndLocal.AddDays(-1));

        DoctorCount = await _db.Doctors.CountAsync(a => a.City == "Houston", cancellationToken);
        PatientCount = await _db.Patients.CountAsync(cancellationToken);
        BookingCount = await _db.Appointments.CountAsync(cancellationToken);
        BookingsThisWeek = await _db.Appointments
            .CountAsync(a => a.CreatedAt >= weekStartUtc && a.CreatedAt < weekEndUtc, cancellationToken);
        UnreadNotifications = await _blogGen.GetUnreadAdminNotificationsAsync(cancellationToken);
    }

    public async Task<IActionResult> OnPostMarkReadAsync(int id, CancellationToken cancellationToken)
    {
        await _blogGen.MarkAdminNotificationReadAsync(id, cancellationToken);
        return RedirectToPage();
    }

    private static (DateTime WeekStartUtc, DateTime WeekEndUtc, DateTime WeekStartLocal, DateTime WeekEndLocal)
        GetCurrentWeekBounds()
    {
        var tz = GetHoustonTimeZone();
        var utcNow = DateTime.UtcNow;
        if (utcNow.Kind != DateTimeKind.Utc)
            utcNow = DateTime.SpecifyKind(utcNow, DateTimeKind.Utc);

        var localNow = TimeZoneInfo.ConvertTimeFromUtc(utcNow, tz);
        var diff = ((int)localNow.DayOfWeek - (int)DayOfWeek.Monday + 7) % 7;
        var weekStartLocal = localNow.Date.AddDays(-diff);
        var weekEndLocal = weekStartLocal.AddDays(7);

        var weekStartUtc = TimeZoneInfo.ConvertTimeToUtc(
            DateTime.SpecifyKind(weekStartLocal, DateTimeKind.Unspecified), tz);
        var weekEndUtc = TimeZoneInfo.ConvertTimeToUtc(
            DateTime.SpecifyKind(weekEndLocal, DateTimeKind.Unspecified), tz);

        return (weekStartUtc, weekEndUtc, weekStartLocal, weekEndLocal);
    }

    private static TimeZoneInfo GetHoustonTimeZone()
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(
                OperatingSystem.IsWindows() ? "Central Standard Time" : "America/Chicago");
        }
        catch
        {
            return TimeZoneInfo.Local;
        }
    }
}
