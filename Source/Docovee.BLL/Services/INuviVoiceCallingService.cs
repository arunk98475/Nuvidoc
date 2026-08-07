namespace Docovee.BLL.Services;

public interface INuviVoiceCallingService
{
    bool IsConfigured { get; }

    Task<NuviOutboundCallResult> PlaceOfficeCallAsync(
        NuviOutboundCallRequest request,
        CancellationToken cancellationToken = default);
}

public sealed class NuviOutboundCallRequest
{
    public required string ToNumber { get; init; }
    public string? DoctorName { get; init; }
    public string? PracticeName { get; init; }
    public string? PracticePhone { get; init; }
    public string? PatientName { get; init; }
    public string? PatientPhone { get; init; }
    public string? PatientEmail { get; init; }
    public string? CallPreference { get; init; }
    public string? AvailabilityWindow { get; init; }
    public string? PreferredDate { get; init; }
    public string? PreferredTimeWindow { get; init; }
    public string? AppointmentType { get; init; }
    public string? InsuranceName { get; init; }
    public string? ChiefComplaint { get; init; }
    public string? CallContext { get; init; }
    public string? SessionKey { get; init; }
}

public sealed class NuviOutboundCallResult
{
    public bool Success { get; init; }
    public string Message { get; init; } = string.Empty;
    public string? ConversationId { get; init; }
    public string? CallSid { get; init; }
    public string? ToNumber { get; init; }
}
