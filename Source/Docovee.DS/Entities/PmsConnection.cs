namespace Docovee.DS.Entities;

public class PmsConnection
{
    public int Id { get; set; }
    public int DoctorId { get; set; }
    public Doctor Doctor { get; set; } = null!;
    public string Provider { get; set; } = "";
    public bool IsEnabled { get; set; }
    public string? DeveloperApiKey { get; set; }
    public string? CustomerApiKey { get; set; }
    public string? ApiKey { get; set; }
    public string? InstitutionId { get; set; }
    public string? LocationExternalId { get; set; }
    public string? ProviderExternalId { get; set; }
    public string? OperatoryId { get; set; }
    public string? ClinicNum { get; set; }
    public string? BaseUrl { get; set; }
    public string? LastError { get; set; }
    public DateTime? LastSyncAt { get; set; }
    public DateTime? LastTestAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public class PmsExternalRef
{
    public int Id { get; set; }
    public int DoctorId { get; set; }
    public Doctor Doctor { get; set; } = null!;
    public int? AppointmentId { get; set; }
    public Appointment? Appointment { get; set; }
    public string Provider { get; set; } = "";
    public string ExternalAppointmentId { get; set; } = "";
    public string? ExternalPatientId { get; set; }
    public string? ExternalLocationId { get; set; }
    public string SyncDirection { get; set; } = "Outbound";
    public string? LastError { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
