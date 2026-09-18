namespace Docovee.BLL.Configuration;

/// <summary>
/// Resolves the actual To address for outbound doctor/practice emails.
/// When <see cref="EmailOptions.OverrideDoctorEmail"/> is set, every doctor email
/// is redirected there instead of the doctor's address from the database.
/// Leave the override empty for production (real destinations).
/// </summary>
public static class EmailOutboundRouting
{
    public static bool IsDoctorOverrideActive(EmailOptions options) =>
        !string.IsNullOrWhiteSpace(GetDoctorOverride(options));

    public static string? GetDoctorOverride(EmailOptions options) =>
        string.IsNullOrWhiteSpace(options.OverrideDoctorEmail)
            ? null
            : options.OverrideDoctorEmail.Trim();

    /// <summary>
    /// Destination for doctor/practice email.
    /// Override wins when set; otherwise the intended doctor email.
    /// </summary>
    public static string? ResolveDoctorEmail(EmailOptions options, string? intendedEmail)
    {
        var overrideTo = GetDoctorOverride(options);
        if (!string.IsNullOrWhiteSpace(overrideTo))
            return overrideTo;

        return string.IsNullOrWhiteSpace(intendedEmail) ? null : intendedEmail.Trim();
    }
}
