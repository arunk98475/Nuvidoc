namespace Docovee.DS.Entities;

/// <summary>
/// HIPAA §164.508-style authorization artifact (not a Yes/No bool alone).
/// </summary>
public class HipaaAuthorization
{
    public int Id { get; set; }
    public int PatientId { get; set; }
    public Patient Patient { get; set; } = null!;

    /// <summary>Exact form text version the patient saw (e.g. 2026-08-v1).</summary>
    public string FormVersion { get; set; } = string.Empty;

    public bool Granted { get; set; }
    public DateTime OccurredAtUtc { get; set; } = DateTime.UtcNow;
    public string? IpAddress { get; set; }
    public string? UserAgent { get; set; }
    public string? ESignName { get; set; }
}

public static class HipaaAuthorizationFormVersions
{
    /// <summary>Bump when Privacy / HIPAA Authorization wording changes.</summary>
    public const string Current = "2026-08-v1";
}
