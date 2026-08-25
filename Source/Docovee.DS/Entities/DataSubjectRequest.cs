namespace Docovee.DS.Entities;

/// <summary>
/// Tracks Privacy Rule / consumer access, amendment, and deletion requests with SLA clocks.
/// </summary>
public class DataSubjectRequest
{
    public int Id { get; set; }
    public int PatientId { get; set; }
    public Patient Patient { get; set; } = null!;

    /// <summary>access | amend | delete</summary>
    public string RequestType { get; set; } = string.Empty;

    /// <summary>awaiting_verification | pending | in_progress | completed | denied | extended</summary>
    public string Status { get; set; } = DataSubjectRequestStatuses.AwaitingVerification;

    public DateTime RequestedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? VerifiedAtUtc { get; set; }

    /// <summary>HIPAA access clock (§164.524): typically 30 days.</summary>
    public DateTime? HipaaDueAtUtc { get; set; }

    /// <summary>Consumer-style clock shown in UI (45 days) or amendment (60 days).</summary>
    public DateTime? ConsumerDueAtUtc { get; set; }

    public DateTime? CompletedAtUtc { get; set; }
    public string? ExtensionReason { get; set; }
    public DateTime? ExtendedDueAtUtc { get; set; }
    public string? DenialReason { get; set; }

    /// <summary>Patient notes (e.g. amendment details).</summary>
    public string? RequestNotes { get; set; }

    /// <summary>Amendment: JSON of requested field changes.</summary>
    public string? AmendmentPayloadJson { get; set; }

    /// <summary>SHA-256 of one-time download token for access exports.</summary>
    public string? DownloadTokenHash { get; set; }
    public DateTime? DownloadExpiresAtUtc { get; set; }

    /// <summary>Export package (JSON). Served via time-limited download link — not emailed as body.</summary>
    public string? ExportPayloadJson { get; set; }

    public bool PmsRemoteCopyNoted { get; set; }
    public string? StaffNotes { get; set; }
}

public static class DataSubjectRequestTypes
{
    public const string Access = "access";
    public const string Amend = "amend";
    public const string Delete = "delete";
}

public static class DataSubjectRequestStatuses
{
    public const string AwaitingVerification = "awaiting_verification";
    public const string Pending = "pending";
    public const string InProgress = "in_progress";
    public const string Completed = "completed";
    public const string Denied = "denied";
    public const string Extended = "extended";
}
