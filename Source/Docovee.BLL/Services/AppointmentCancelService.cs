using Docovee.BLL.Configuration;
using Docovee.DS;
using Docovee.DS.Entities;
using Docovee.logging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Docovee.BLL.Services;

public interface IAppointmentCancelService
{
    Task<AppointmentCancelResult> RequestCancelAsync(
        int patientId,
        int appointmentId,
        CancellationToken cancellationToken = default);
}

public sealed class AppointmentCancelResult
{
    public bool Success { get; init; }
    public string Message { get; init; } = string.Empty;
    public bool VoiceCallStarted { get; init; }
    public bool CanceledImmediately { get; init; }
    public string? ConversationId { get; init; }
}

public sealed class AppointmentCancelService : IAppointmentCancelService
{
    private readonly DocoveeDbContext _db;
    private readonly IAppointmentService _appointments;
    private readonly INuviVoiceCallingService _voiceCalling;
    private readonly IVoiceCallBookingService _voiceBookings;
    private readonly TwilioOptions _twilio;
    private readonly IDocoveeLogger _logger;

    public AppointmentCancelService(
        DocoveeDbContext db,
        IAppointmentService appointments,
        INuviVoiceCallingService voiceCalling,
        IVoiceCallBookingService voiceBookings,
        IOptions<TwilioOptions> twilio,
        IDocoveeLogger logger)
    {
        _db = db;
        _appointments = appointments;
        _voiceCalling = voiceCalling;
        _voiceBookings = voiceBookings;
        _twilio = twilio.Value;
        _logger = logger;
    }

    public async Task<AppointmentCancelResult> RequestCancelAsync(
        int patientId,
        int appointmentId,
        CancellationToken cancellationToken = default)
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
            return Fail("This appointment can no longer be canceled.");

        var pendingCancel = await _db.VoiceOutboundCalls.AsNoTracking()
            .AnyAsync(c =>
                c.AppointmentId == appointmentId
                && c.CallIntent == VoiceOutboundCallIntents.Cancel
                && c.Status == VoiceOutboundCallStatuses.Initiated,
                cancellationToken);
        if (pendingCancel)
            return Fail("A cancellation call is already in progress for this appointment.");

        var doctor = await _db.Doctors.AsNoTracking()
            .FirstOrDefaultAsync(d => d.Id == appointment.DoctorId, cancellationToken);
        if (doctor == null)
            return Fail("Doctor not found for this appointment.");

        var overrideTo = ElevenLabsTwilioCallingService.ToE164(_twilio.OutboundOverrideToNumber);
        var officePhone = ElevenLabsTwilioCallingService.ToE164(doctor.OfficePhoneNumber);
        var dialNumber = !string.IsNullOrWhiteSpace(overrideTo) ? overrideTo! : officePhone;

        if (string.IsNullOrWhiteSpace(dialNumber) || !_voiceCalling.IsConfigured)
        {
            var immediate = await _appointments.UpdateStatusAsPatientAsync(
                patientId,
                appointmentId,
                AppointmentStatuses.PatientCanceled,
                cancellationToken);
            if (!immediate.Success)
                return Fail(immediate.Error ?? "Could not cancel the appointment.");

            return new AppointmentCancelResult
            {
                Success = true,
                CanceledImmediately = true,
                Message = "Your appointment has been canceled."
            };
        }

        var slotStart = appointment.StartsAt;
        var appointmentDate = slotStart.ToString("yyyy-MM-dd");
        var appointmentTime = slotStart.ToString("h:mm tt");
        var appointmentDateTime =
            $"{slotStart:dddd, MMMM d, yyyy} at {appointmentTime} Pacific";

        var sessionKey = Guid.NewGuid();
        if (appointment.SearchSessionId is int searchSessionId)
        {
            var session = await _db.SearchSessions.AsNoTracking()
                .FirstOrDefaultAsync(s => s.Id == searchSessionId, cancellationToken);
            if (session != null)
                sessionKey = session.SessionKey;
        }

        var patientName = string.IsNullOrWhiteSpace(appointment.PatientName)
            ? patient.FullName
            : appointment.PatientName;
        if (string.IsNullOrWhiteSpace(patientName))
            patientName = "Patient";

        var callResult = await _voiceCalling.PlaceOfficeCallAsync(new NuviOutboundCallRequest
        {
            Intent = VoiceOutboundCallIntents.Cancel,
            ToNumber = dialNumber,
            DoctorName = doctor.Name,
            PracticeName = doctor.PracticeName,
            PracticePhone = officePhone,
            PatientName = patientName,
            PatientPhone = appointment.PatientPhone ?? patient.Phone,
            PatientEmail = appointment.PatientEmail ?? patient.Username,
            AppointmentId = appointmentId,
            AppointmentDate = appointmentDate,
            AppointmentTime = appointmentTime,
            AppointmentDateTime = appointmentDateTime,
            AppointmentType = appointment.VisitReason,
            ChiefComplaint = appointment.VisitReason,
            CallContext = $"Cancel appointment #{appointmentId} for {patientName}.",
            SessionKey = sessionKey.ToString()
        }, cancellationToken);

        if (!callResult.Success)
        {
            _logger.LogInformation(
                "Cancel call failed for appointment {AppointmentId}: {Message}",
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
                SearchSessionId = appointment.SearchSessionId,
                PatientId = patientId,
                DoctorId = doctor.Id,
                PatientName = patientName,
                PatientPhone = appointment.PatientPhone ?? patient.Phone,
                PatientEmail = appointment.PatientEmail ?? patient.Username,
                VisitReason = appointment.VisitReason,
                ToNumber = dialNumber,
                CallIntent = VoiceOutboundCallIntents.Cancel,
                AppointmentId = appointmentId
            }, cancellationToken);
            _voiceBookings.ScheduleConversationPolling(callResult.ConversationId!);
        }

        var doctorLabel = string.IsNullOrWhiteSpace(doctor.Name) ? "your dentist" : doctor.Name;
        return new AppointmentCancelResult
        {
            Success = true,
            VoiceCallStarted = true,
            ConversationId = callResult.ConversationId,
            Message =
                $"Nuvi is calling {doctorLabel}'s office now to cancel your visit on {VoiceCallBookingService.FormatPstSlot(slotStart, slotStart.AddHours(1))}. We'll notify you when it's confirmed."
        };
    }

    private static AppointmentCancelResult Fail(string message) =>
        new() { Success = false, Message = message };
}
