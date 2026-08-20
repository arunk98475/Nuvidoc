namespace Docovee.DS.Entities;

/// <summary>
/// Immutable audit log for HIPAA-aligned tracking of who did what, when, and to which record.
/// </summary>
public class AuditTrail
{
    public long Id { get; set; }
    public DateTime OccurredAtUtc { get; set; } = DateTime.UtcNow;

    /// <summary>Create, Update, Delete, Login, Logout, LoginFailed, Read, etc.</summary>
    public string Action { get; set; } = string.Empty;

    /// <summary>Logical entity name, e.g. Patient, Appointment, Authentication.</summary>
    public string EntityType { get; set; } = string.Empty;

    /// <summary>Primary key of affected record, when applicable.</summary>
    public string? EntityId { get; set; }

    public string? ActorUserId { get; set; }
    public string? ActorUsername { get; set; }
    public string? ActorRole { get; set; }

    public string? IpAddress { get; set; }
    public string? UserAgent { get; set; }

    public bool Success { get; set; } = true;
    public string? ErrorMessage { get; set; }

    /// <summary>Short human-readable summary.</summary>
    public string? Summary { get; set; }

    /// <summary>JSON snapshot of changed/removed values (sensitive fields redacted).</summary>
    public string? OldValuesJson { get; set; }

    /// <summary>JSON snapshot of new values (sensitive fields redacted).</summary>
    public string? NewValuesJson { get; set; }
}

public static class AuditActions
{
    public const string Create = "Create";
    public const string Update = "Update";
    public const string Delete = "Delete";
    public const string Login = "Login";
    public const string Logout = "Logout";
    public const string LoginFailed = "LoginFailed";
    public const string Read = "Read";
    public const string Export = "Export";
    public const string Search = "Search";
    public const string Disclose = "Disclose";
}

public static class AuditEntityTypes
{
    public const string Authentication = "Authentication";
    public const string Patient = "Patient";
    public const string Doctor = "Doctor";
    public const string Admin = "Admin";
    public const string Appointment = "Appointment";
    public const string SearchSession = "SearchSession";
    public const string ChatMessage = "ChatMessage";
    public const string DoctorPatientReview = "DoctorPatientReview";
    public const string PatientDoctorContactView = "PatientDoctorContactView";
    public const string PmsConnection = "PmsConnection";
    public const string PmsExternalRef = "PmsExternalRef";
    public const string DoctorLocation = "DoctorLocation";
    public const string DoctorOnboardingSession = "DoctorOnboardingSession";
    public const string VoiceOutboundCall = "VoiceOutboundCall";
    public const string PatientInsurance = "PatientInsurance";
    public const string DataExport = "DataExport";
}
