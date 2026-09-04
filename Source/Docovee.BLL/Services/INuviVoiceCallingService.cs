namespace Docovee.BLL.Services;

using Docovee.DS.Entities;

public interface INuviVoiceCallingService
{
    bool IsConfigured { get; }

    Task<NuviOutboundCallResult> PlaceOfficeCallAsync(
        NuviOutboundCallRequest request,
        CancellationToken cancellationToken = default);
}

public sealed class NuviOutboundCallRequest
{
    /// <summary>Book (default), Cancel, or Reschedule.</summary>
    public string Intent { get; init; } = VoiceOutboundCallIntents.Book;
    public required string ToNumber { get; init; }
    public string? DoctorName { get; init; }
    public string? PracticeName { get; init; }
    public string? PracticePhone { get; init; }
    public string? PatientName { get; init; }
    public string? PatientPhone { get; init; }
    public string? PatientEmail { get; init; }
    /// <summary>Patient DOB for the office (spoken via {{patient_date_of_birth}} when IncludePhi is on).</summary>
    public DateOnly? PatientDateOfBirth { get; init; }
    public string? CallPreference { get; init; }
    public string? AvailabilityWindow { get; init; }
    public string? PreferredDate { get; init; }
    public string? PreferredTimeWindow { get; init; }
    /// <summary>Inclusive window start (yyyy-MM-dd, clinic local).</summary>
    public string? BookingWindowStart { get; init; }
    /// <summary>Inclusive window end (yyyy-MM-dd, clinic local).</summary>
    public string? BookingWindowEnd { get; init; }
    /// <summary>Existing appointment id (cancel calls).</summary>
    public int? AppointmentId { get; init; }
    /// <summary>yyyy-MM-dd clinic local (cancel calls).</summary>
    public string? AppointmentDate { get; init; }
    /// <summary>Start clock time with AM/PM (cancel calls).</summary>
    public string? AppointmentTime { get; init; }
    /// <summary>Human-readable slot for cancel agent prompt.</summary>
    public string? AppointmentDateTime { get; init; }
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
