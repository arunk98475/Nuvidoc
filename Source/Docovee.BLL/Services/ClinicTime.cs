namespace Docovee.BLL.Services;

/// <summary>Clinic-local time is always US Pacific (PST/PDT).</summary>
public static class ClinicTime
{
    public static TimeZoneInfo Zone { get; } = TimeZoneInfo.FindSystemTimeZoneById(
        OperatingSystem.IsWindows() ? "Pacific Standard Time" : "America/Los_Angeles");

    public static DateTime Now => TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, Zone);

    public static DateTime FromUtc(DateTime utc)
    {
        var instant = utc.Kind == DateTimeKind.Unspecified
            ? DateTime.SpecifyKind(utc, DateTimeKind.Utc)
            : utc.ToUniversalTime();
        return TimeZoneInfo.ConvertTimeFromUtc(instant, Zone);
    }
}
