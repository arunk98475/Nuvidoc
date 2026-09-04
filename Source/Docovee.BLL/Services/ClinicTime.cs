namespace Docovee.BLL.Services;

/// <summary>Clinic-local time is US Central (CST/CDT) — Houston / America/Chicago.</summary>
public static class ClinicTime
{
    public static TimeZoneInfo Zone { get; } = TimeZoneInfo.FindSystemTimeZoneById(
        OperatingSystem.IsWindows() ? "Central Standard Time" : "America/Chicago");

    public static DateTime Now => TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, Zone);

    public static DateTime FromUtc(DateTime utc)
    {
        var instant = utc.Kind == DateTimeKind.Unspecified
            ? DateTime.SpecifyKind(utc, DateTimeKind.Utc)
            : utc.ToUniversalTime();
        return TimeZoneInfo.ConvertTimeFromUtc(instant, Zone);
    }
}
