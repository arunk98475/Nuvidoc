using Docovee.BLL.Configuration;
using Docovee.DS;
using Docovee.DS.Entities;
using Docovee.logging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Docovee.BLL.Services;

public interface IAppointmentRescheduleService
{
    Task<AppointmentRescheduleResult> RequestRescheduleAsync(
        int patientId,
        int appointmentId,
        string urgencyPreference,
        CancellationToken cancellationToken = default,
        int? currentSearchSessionId = null);
}

public sealed class AppointmentRescheduleResult
{
    public bool Success { get; init; }
    public string Message { get; init; } = string.Empty;
    public bool VoiceCallStarted { get; init; }
    public string? ConversationId { get; init; }
}

public sealed class AppointmentRescheduleService : IAppointmentRescheduleService
{
    private readonly DocoveeDbContext _db;
    private readonly INuviVoiceCallingService _voiceCalling;
    private readonly IVoiceCallBookingService _voiceBookings;
    private readonly TwilioOptions _twilio;
    private readonly IDocoveeLogger _logger;

    public AppointmentRescheduleService(
        DocoveeDbContext db,
        INuviVoiceCallingService voiceCalling,
        IVoiceCallBookingService voiceBookings,
        IOptions<TwilioOptions> twilio,
        IDocoveeLogger logger)
    {
        _db = db;
        _voiceCalling = voiceCalling;
        _voiceBookings = voiceBookings;
        _twilio = twilio.Value;
        _logger = logger;
    }

    public async Task<AppointmentRescheduleResult> RequestRescheduleAsync(
        int patientId,
        int appointmentId,
        string urgencyPreference,
        CancellationToken cancellationToken = default,
        int? currentSearchSessionId = null)
    {
        var patient = await _db.Patients.AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == patientId, cancellationToken);
        if (patient == null)
            return Fail("Patient not found.");

        var appointment = await _db.Appointments.AsNoTracking()
            .FirstOrDefaultAsync(a =>
                a.Id == appointmentId
                && (a.PatientId == patientId
                    || (a.PatientId == null && !string.IsNullOrWhiteSpace(patient.Username)
                        && a.PatientEmail == patient.Username)),
                cancellationToken);
        if (appointment == null)
            return Fail("Appointment not found.");

        if (!AppointmentStatuses.IsActive(appointment.Status))
            return Fail("This appointment can no longer be rescheduled.");

        var pending = await _db.VoiceOutboundCalls.AsNoTracking()
            .AnyAsync(c =>
                c.AppointmentId == appointmentId
                && c.CallIntent == VoiceOutboundCallIntents.Reschedule
                && c.Status == VoiceOutboundCallStatuses.Initiated,
                cancellationToken);
        if (pending)
            return Fail("A reschedule call is already in progress for this appointment.");

        var doctor = await _db.Doctors.AsNoTracking()
            .FirstOrDefaultAsync(d => d.Id == appointment.DoctorId, cancellationToken);
        if (doctor == null)
            return Fail("Doctor not found for this appointment.");

        var overrideTo = ElevenLabsTwilioCallingService.ToE164(_twilio.OutboundOverrideToNumber);
        var officePhone = ElevenLabsTwilioCallingService.ToE164(doctor.OfficePhoneNumber);
        var dialNumber = !string.IsNullOrWhiteSpace(overrideTo) ? overrideTo! : officePhone;

        if (string.IsNullOrWhiteSpace(dialNumber) || !_voiceCalling.IsConfigured)
            return Fail("Voice calling isn't available right now. Please contact the office directly to reschedule.");

        var window = BuildClinicBookingWindow(urgencyPreference);
        var slotStart = appointment.StartsAt;
        var appointmentDate = slotStart.ToString("yyyy-MM-dd");
        var appointmentTime = slotStart.ToString("h:mm tt");
        var appointmentDateTime =
            $"{slotStart:dddd, MMMM d, yyyy} at {appointmentTime}";

        var (chatSessionId, sessionKey) = await ResolveChatSessionAsync(
            patientId, appointment.SearchSessionId, currentSearchSessionId, cancellationToken);

        var patientName = string.IsNullOrWhiteSpace(appointment.PatientName)
            ? patient.FullName
            : appointment.PatientName;
        if (string.IsNullOrWhiteSpace(patientName))
            patientName = "Patient";

        var callResult = await _voiceCalling.PlaceOfficeCallAsync(new NuviOutboundCallRequest
        {
            Intent = VoiceOutboundCallIntents.Reschedule,
            ToNumber = dialNumber,
            DoctorName = doctor.Name,
            PracticeName = doctor.PracticeName,
            PracticePhone = officePhone,
            PatientName = patientName,
            PatientPhone = appointment.PatientPhone ?? patient.Phone,
            PatientEmail = appointment.PatientEmail ?? patient.Username,
            PatientDateOfBirth = ElevenLabsTwilioCallingService.PreferPatientDateOfBirth(
                appointment.PatientDateOfBirth,
                patient.DateOfBirth),
            AppointmentId = appointmentId,
            AppointmentDate = appointmentDate,
            AppointmentTime = appointmentTime,
            AppointmentDateTime = appointmentDateTime,
            AppointmentType = appointment.VisitReason,
            ChiefComplaint = appointment.VisitReason,
            PreferredDate = window.Phrase,
            AvailabilityWindow = window.Phrase,
            BookingWindowStart = window.StartDate,
            BookingWindowEnd = window.EndDate,
            PreferredTimeWindow = "any available time during office hours",
            CallContext =
                $"Reschedule appointment #{appointmentId} for {patientName}. Current slot {appointmentDateTime}. New window: {window.Phrase}.",
            SessionKey = sessionKey.ToString()
        }, cancellationToken);

        if (!callResult.Success)
        {
            _logger.LogInformation(
                "Reschedule call failed for appointment {AppointmentId}: {Message}",
                appointmentId, callResult.Message);
            return Fail(callResult.Message);
        }

        if (!string.IsNullOrWhiteSpace(callResult.ConversationId))
        {
            await _voiceBookings.RecordInitiatedCallAsync(new VoiceOutboundCallRecordRequest
            {
                ConversationId = callResult.ConversationId!,
                CallSid = callResult.CallSid,
                SessionKey = sessionKey,
                SearchSessionId = chatSessionId,
                PatientId = patientId,
                DoctorId = doctor.Id,
                PatientName = patientName,
                PatientPhone = appointment.PatientPhone ?? patient.Phone,
                PatientEmail = appointment.PatientEmail ?? patient.Username,
                VisitReason = appointment.VisitReason,
                ToNumber = dialNumber,
                CallIntent = VoiceOutboundCallIntents.Reschedule,
                AppointmentId = appointmentId
            }, cancellationToken);
            _voiceBookings.ScheduleConversationPolling(callResult.ConversationId!);
        }

        var practiceLabel = VoiceCallBookingService.FormatPracticeLabel(doctor.PracticeName, doctor.Name);
        return new AppointmentRescheduleResult
        {
            Success = true,
            VoiceCallStarted = true,
            ConversationId = callResult.ConversationId,
            Message = VoiceCallBookingService.FormatCallingPracticeChat(practiceLabel)
        };
    }

    /// <summary>Same window rules as booking chat (ASAP=7d, month=30d, no rush=120d, exploring=180d).</summary>
    internal static (string Phrase, string StartDate, string EndDate) BuildClinicBookingWindow(string? urgencyPreference)
    {
        var u = (urgencyPreference ?? string.Empty).Trim().ToLowerInvariant();
        var start = ElevenLabsTwilioCallingService.GetClinicNow().Date;
        int days;
        string label;

        if (u.Contains("asap") || u.Contains("this week") || u.Contains("1 week") || u.Contains("one week"))
        {
            days = 7;
            label = "within the next 7 days (this week)";
        }
        else if (u.Contains("month"))
        {
            days = 30;
            label = "within the next 30 days";
        }
        else if (u.Contains("no rush"))
        {
            days = 120;
            label = "within the next 120 days";
        }
        else if (u.Contains("explor"))
        {
            days = 180;
            label = "within the next 180 days";
        }
        else
        {
            days = 30;
            label = "within the next 30 days";
        }

        var end = start.AddDays(days);
        var phrase =
            $"{label}: any day from {start:dddd, MMMM d, yyyy} through {end:dddd, MMMM d, yyyy}";
        return (phrase, start.ToString("yyyy-MM-dd"), end.ToString("yyyy-MM-dd"));
    }

    private async Task<(int? SessionId, Guid SessionKey)> ResolveChatSessionAsync(
        int patientId,
        int? appointmentSearchSessionId,
        int? currentSearchSessionId,
        CancellationToken cancellationToken)
    {
        if (currentSearchSessionId is > 0)
        {
            var current = await _db.SearchSessions.AsNoTracking()
                .FirstOrDefaultAsync(s =>
                    s.Id == currentSearchSessionId
                    && (s.PatientId == null || s.PatientId == patientId),
                    cancellationToken);
            if (current != null)
                return (current.Id, current.SessionKey);
        }

        var latest = await _db.SearchSessions.AsNoTracking()
            .Where(s => s.PatientId == patientId)
            .OrderByDescending(s => s.UpdatedAt)
            .ThenByDescending(s => s.Id)
            .FirstOrDefaultAsync(cancellationToken);
        if (latest != null)
            return (latest.Id, latest.SessionKey);

        if (appointmentSearchSessionId is > 0)
        {
            var booked = await _db.SearchSessions.AsNoTracking()
                .FirstOrDefaultAsync(s => s.Id == appointmentSearchSessionId, cancellationToken);
            if (booked != null)
                return (booked.Id, booked.SessionKey);
        }

        return (appointmentSearchSessionId, Guid.NewGuid());
    }

    private static AppointmentRescheduleResult Fail(string message) =>
        new() { Success = false, Message = message };
}
