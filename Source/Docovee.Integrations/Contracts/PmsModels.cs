namespace Docovee.Integrations.Contracts;

public sealed class PmsConnectionCredentials
{
    public string ProviderId { get; set; } = "";
    public string? DeveloperApiKey { get; set; }
    public string? CustomerApiKey { get; set; }
    public string? ApiKey { get; set; }
    public string? InstitutionId { get; set; }
    public string? LocationId { get; set; }
    public string? ProviderExternalId { get; set; }
    public string? OperatoryId { get; set; }
    public string? ClinicNum { get; set; }
    public string? BaseUrl { get; set; }
}

public sealed class PmsConnectionResult
{
    public bool Success { get; set; }
    public string? Message { get; set; }
    public string? ExternalPracticeName { get; set; }
}

public sealed class PmsSlot
{
    public DateTime StartsAt { get; set; }
    public DateTime EndsAt { get; set; }
    public string? OperatoryId { get; set; }
    public string? ProviderExternalId { get; set; }
    public string TimeLabel { get; set; } = "";
}

public sealed class PmsAvailabilityRequest
{
    public PmsConnectionCredentials Credentials { get; set; } = new();
    public DateOnly From { get; set; }
    public DateOnly To { get; set; }
    public int SlotMinutes { get; set; } = 40;
}

public sealed class PmsPatientInfo
{
    public string? ExternalPatientId { get; set; }
    public string FullName { get; set; } = "";
    public string? FirstName { get; set; }
    public string? LastName { get; set; }
    public string? Phone { get; set; }
    public string? Email { get; set; }
    public DateOnly? DateOfBirth { get; set; }
}

public sealed class PmsCreateAppointmentRequest
{
    public PmsConnectionCredentials Credentials { get; set; } = new();
    public PmsPatientInfo Patient { get; set; } = new();
    public DateTime StartsAt { get; set; }
    public int DurationMinutes { get; set; } = 40;
    public string VisitReason { get; set; } = "";
    public string? Note { get; set; }
    public string? IdempotencyKey { get; set; }
}

public sealed class PmsUpdateAppointmentRequest
{
    public PmsConnectionCredentials Credentials { get; set; } = new();
    public string ExternalAppointmentId { get; set; } = "";
    public string Status { get; set; } = "";
    public DateTime? NewStartsAt { get; set; }
    public string? Note { get; set; }
}

public sealed class PmsAppointmentResult
{
    public bool Success { get; set; }
    public string? Error { get; set; }
    public string? ExternalAppointmentId { get; set; }
    public string? ExternalPatientId { get; set; }
    public string? RawStatus { get; set; }
}

public sealed class PmsExternalAppointment
{
    public string ExternalAppointmentId { get; set; } = "";
    public string? ExternalPatientId { get; set; }
    public string PatientName { get; set; } = "";
    public string? PatientPhone { get; set; }
    public string? PatientEmail { get; set; }
    public DateTime StartsAt { get; set; }
    public string? VisitReason { get; set; }
    public string RawStatus { get; set; } = "";
    public string MappedStatus { get; set; } = "";
    public DateTime? UpdatedAt { get; set; }
}

public sealed class PmsPullChangesRequest
{
    public PmsConnectionCredentials Credentials { get; set; } = new();
    public DateTime SinceUtc { get; set; }
    public DateOnly? FromDate { get; set; }
    public DateOnly? ToDate { get; set; }
}

public sealed class PmsEnsureProviderRequest
{
    public PmsConnectionCredentials Credentials { get; set; } = new();
    public string FullName { get; set; } = "";
    public string? Email { get; set; }
    public string? Phone { get; set; }
}

public sealed class PmsProviderOption
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Email { get; set; }
    public string? Npi { get; set; }
}

public sealed class PmsFindProviderByNpiRequest
{
    public PmsConnectionCredentials Credentials { get; set; } = new();
    public string Npi { get; set; } = "";
}

public sealed class PmsProviderEnsureResult
{
    public bool Success { get; set; }
    public string? ProviderExternalId { get; set; }
    public string? Message { get; set; }
    public string? Error { get; set; }
    public bool Created { get; set; }
    public IReadOnlyList<PmsProviderOption> Candidates { get; set; } = Array.Empty<PmsProviderOption>();
}
