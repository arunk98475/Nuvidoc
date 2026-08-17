namespace Docovee.BLL.Configuration;

public class EmailOptions
{
    public const string SectionName = "Email";

    /// <summary>Verified SES From address, e.g. info@nuvidoc.com.</summary>
    public string FromAddress { get; set; } = "info@nuvidoc.com";

    public string FromDisplayName { get; set; } = "NuviDoc";

    /// <summary>AWS region for SES, e.g. us-east-1.</summary>
    public string Region { get; set; } = "us-east-1";

    public string AccessKeyId { get; set; } = string.Empty;
    public string SecretAccessKey { get; set; } = string.Empty;

    /// <summary>Public site base URL for links in emails (no trailing slash).</summary>
    public string PublicBaseUrl { get; set; } = string.Empty;

    public int VerificationLinkExpiryMinutes { get; set; } = 60;
    public int PasswordResetLinkExpiryMinutes { get; set; } = 60;

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(FromAddress)
        && !string.IsNullOrWhiteSpace(AccessKeyId)
        && !string.IsNullOrWhiteSpace(SecretAccessKey)
        && !string.IsNullOrWhiteSpace(Region);
}
