using System.Net.Mail;
using System.Text;
using Docovee.BLL.Configuration;
using Docovee.DS;
using Docovee.DS.Entities;
using Docovee.logging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Twilio;
using Twilio.Rest.Api.V2010.Account;
using Twilio.Types;

namespace Docovee.BLL.Services;

public sealed class LeadHandoffDoctorTarget
{
    public int DoctorId { get; init; }
    public string DoctorName { get; init; } = string.Empty;
    public string? PracticeName { get; init; }
    public string? OfficePhone { get; init; }
    public string? UsernameEmail { get; init; }
}

public sealed class LeadHandoffRequest
{
    public int? SearchSessionId { get; init; }
    public Guid? SessionKey { get; init; }
    public int? PatientId { get; init; }
    public string PatientName { get; init; } = "Patient";
    public string? PatientPhone { get; init; }
    public string? PatientEmail { get; init; }
    public string VisitReason { get; init; } = "dental appointment";
    public string? PreferredTimingNote { get; init; }
    public IReadOnlyList<LeadHandoffDoctorTarget> Doctors { get; init; } = Array.Empty<LeadHandoffDoctorTarget>();
}

public sealed class LeadHandoffDoctorResult
{
    public int DoctorId { get; init; }
    public string DoctorName { get; init; } = string.Empty;
    public string? PracticeName { get; init; }
    public string? OfficePhone { get; init; }
    public int? AppointmentId { get; init; }
    public bool OfficeSmsSent { get; init; }
    public bool OfficeEmailSent { get; init; }
    public bool PatientSmsSent { get; init; }
    public bool PatientEmailSent { get; init; }
    public string? ErrorNotes { get; init; }
}

public sealed class LeadHandoffBatchResult
{
    public bool Success { get; init; }
    public string Message { get; init; } = string.Empty;
    public IReadOnlyList<LeadHandoffDoctorResult> Results { get; init; } = Array.Empty<LeadHandoffDoctorResult>();
}

public interface ILeadHandoffService
{
    Task<LeadHandoffBatchResult> HandoffAsync(
        LeadHandoffRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Resends the lead SMS/email to the practice for an existing handoff appointment.</summary>
    Task<(bool Success, string? Error)> ResendLeadToOfficeAsync(
        int appointmentId,
        CancellationToken cancellationToken = default);
}

public sealed class LeadHandoffService : ILeadHandoffService
{
    private readonly DocoveeDbContext _db;
    private readonly IEmailSender _email;
    private readonly TwilioOptions _twilio;
    private readonly IDocoveeLogger _logger;

    public LeadHandoffService(
        DocoveeDbContext db,
        IEmailSender email,
        IOptions<TwilioOptions> twilio,
        IDocoveeLogger logger)
    {
        _db = db;
        _email = email;
        _twilio = twilio.Value;
        _logger = logger;
    }

    public async Task<(bool Success, string? Error)> ResendLeadToOfficeAsync(
        int appointmentId,
        CancellationToken cancellationToken = default)
    {
        var appointment = await _db.Appointments.AsNoTracking()
            .FirstOrDefaultAsync(a => a.Id == appointmentId, cancellationToken);
        if (appointment == null)
            return (false, "Appointment not found.");
        if (!string.Equals(appointment.Source, AppointmentSources.NuviLeadHandoff, StringComparison.OrdinalIgnoreCase))
            return (false, "Not a lead handoff appointment.");

        var doctor = await _db.Doctors.AsNoTracking()
            .FirstOrDefaultAsync(d => d.Id == appointment.DoctorId, cancellationToken);
        if (doctor == null)
            return (false, "Doctor not found.");

        var patientName = string.IsNullOrWhiteSpace(appointment.PatientName)
            ? "Patient"
            : appointment.PatientName.Trim();
        var visitReason = string.IsNullOrWhiteSpace(appointment.VisitReason)
            ? "dental appointment"
            : appointment.VisitReason.Trim();
        var practiceLabel = FormatPracticeLabel(doctor.PracticeName, doctor.Name);
        var officeBody = BuildOfficeMessage(
            patientName,
            appointment.PatientPhone,
            visitReason,
            timing: null,
            practiceLabel);

        var errors = new List<string>();
        var officeSms = TrySendSms(doctor.OfficePhoneNumber, officeBody, "office");
        if (!officeSms.ok && !string.IsNullOrWhiteSpace(doctor.OfficePhoneNumber))
            errors.Add(officeSms.error ?? "Office SMS failed.");

        var officeEmail = false;
        if (LooksLikeEmail(doctor.Username))
        {
            officeEmail = await TrySendEmailAsync(
                doctor.Username!,
                $"Follow-up NuviDoc patient lead — {patientName}",
                officeBody,
                cancellationToken);
            if (!officeEmail)
                errors.Add("Office email failed.");
        }

        if (!officeSms.ok && !officeEmail)
            return (false, errors.Count > 0 ? string.Join(" ", errors) : "Could not reach the practice.");

        _logger.LogInformation(
            "Lead handoff resent appointment={AppointmentId} doctor={DoctorId} officeSms={OfficeSms} officeEmail={OfficeEmail}",
            appointmentId, doctor.Id, officeSms.ok, officeEmail);
        return (true, null);
    }

    public async Task<LeadHandoffBatchResult> HandoffAsync(
        LeadHandoffRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request.Doctors.Count == 0)
        {
            return new LeadHandoffBatchResult
            {
                Success = false,
                Message = "No doctors selected for handoff."
            };
        }

        var patientName = string.IsNullOrWhiteSpace(request.PatientName) ? "Patient" : request.PatientName.Trim();
        var visitReason = string.IsNullOrWhiteSpace(request.VisitReason) ? "dental appointment" : request.VisitReason.Trim();
        var results = new List<LeadHandoffDoctorResult>();

        foreach (var doctor in request.Doctors)
        {
            results.Add(await HandoffOneAsync(
                request, doctor, patientName, visitReason, cancellationToken));
        }

        var anyOk = results.Any(r =>
            r.AppointmentId is > 0
            || r.OfficeSmsSent
            || r.OfficeEmailSent
            || r.PatientSmsSent
            || r.PatientEmailSent);

        return new LeadHandoffBatchResult
        {
            Success = anyOk,
            Message = anyOk
                ? "Lead shared with office and patient."
                : "Could not share the lead via SMS or email.",
            Results = results
        };
    }

    private async Task<LeadHandoffDoctorResult> HandoffOneAsync(
        LeadHandoffRequest request,
        LeadHandoffDoctorTarget doctor,
        string patientName,
        string visitReason,
        CancellationToken cancellationToken)
    {
        var errors = new List<string>();
        var now = DateTime.UtcNow;

        // Idempotency: skip duplicate handoff for same session+doctor within a short window.
        if (request.SearchSessionId is > 0)
        {
            var existing = await _db.LeadHandoffs.AsNoTracking()
                .Where(h => h.SearchSessionId == request.SearchSessionId
                            && h.DoctorId == doctor.DoctorId)
                .OrderByDescending(h => h.Id)
                .FirstOrDefaultAsync(cancellationToken);
            if (existing != null && existing.CreatedAt > now.AddHours(-12))
            {
                return new LeadHandoffDoctorResult
                {
                    DoctorId = doctor.DoctorId,
                    DoctorName = doctor.DoctorName,
                    PracticeName = doctor.PracticeName,
                    OfficePhone = doctor.OfficePhone,
                    AppointmentId = existing.AppointmentId,
                    OfficeSmsSent = existing.OfficeSmsSent,
                    OfficeEmailSent = existing.OfficeEmailSent,
                    PatientSmsSent = existing.PatientSmsSent,
                    PatientEmailSent = existing.PatientEmailSent,
                    ErrorNotes = "Already handed off recently."
                };
            }
        }

        var appointment = new Appointment
        {
            DoctorId = doctor.DoctorId,
            PatientId = request.PatientId is > 0 ? request.PatientId : null,
            PatientName = patientName,
            PatientPhone = NullIfWhite(request.PatientPhone),
            PatientEmail = NullIfWhite(request.PatientEmail),
            VisitReason = visitReason.Length > 200 ? visitReason[..200] : visitReason,
            StartsAt = null,
            Status = AppointmentStatuses.Unconfirmed,
            Source = AppointmentSources.NuviLeadHandoff,
            SearchSessionId = request.SearchSessionId,
            CreatedAt = now,
            UpdatedAt = now
        };
        _db.Appointments.Add(appointment);
        await _db.SaveChangesAsync(cancellationToken);

        var officePhoneDisplay = NullIfWhite(doctor.OfficePhone) ?? "the office";
        var practiceLabel = FormatPracticeLabel(doctor.PracticeName, doctor.DoctorName);
        var timing = string.IsNullOrWhiteSpace(request.PreferredTimingNote)
            ? null
            : request.PreferredTimingNote.Trim();

        var officeBody = BuildOfficeMessage(patientName, request.PatientPhone, visitReason, timing, practiceLabel);
        var patientBody = BuildPatientMessage(practiceLabel, doctor.DoctorName, officePhoneDisplay);

        var officeSms = TrySendSms(doctor.OfficePhone, officeBody, "office");
        if (!officeSms.ok && !string.IsNullOrWhiteSpace(doctor.OfficePhone))
            errors.Add(officeSms.error ?? "Office SMS failed.");

        var officeEmail = false;
        if (LooksLikeEmail(doctor.UsernameEmail))
        {
            officeEmail = await TrySendEmailAsync(
                doctor.UsernameEmail!,
                $"New NuviDoc patient lead — {patientName}",
                officeBody,
                cancellationToken);
            if (!officeEmail)
                errors.Add("Office email failed.");
        }

        var patientSms = TrySendSms(request.PatientPhone, patientBody, "patient");
        if (!patientSms.ok && !string.IsNullOrWhiteSpace(request.PatientPhone))
            errors.Add(patientSms.error ?? "Patient SMS failed.");

        var patientEmail = false;
        if (LooksLikeEmail(request.PatientEmail))
        {
            patientEmail = await TrySendEmailAsync(
                request.PatientEmail!,
                $"Your NuviDoc connection — {practiceLabel}",
                patientBody,
                cancellationToken);
            if (!patientEmail)
                errors.Add("Patient email failed.");
        }

        var handoff = new LeadHandoff
        {
            SearchSessionId = request.SearchSessionId,
            PatientId = request.PatientId is > 0 ? request.PatientId : null,
            DoctorId = doctor.DoctorId,
            AppointmentId = appointment.Id,
            OfficeSmsSent = officeSms.ok,
            OfficeEmailSent = officeEmail,
            PatientSmsSent = patientSms.ok,
            PatientEmailSent = patientEmail,
            ErrorNotes = errors.Count == 0 ? null : string.Join(" ", errors),
            CreatedAt = now
        };
        _db.LeadHandoffs.Add(handoff);
        await _db.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "Lead handoff session={SessionId} doctor={DoctorId} appt={AppointmentId} officeSms={OfficeSms} officeEmail={OfficeEmail} patientSms={PatientSms} patientEmail={PatientEmail}",
            request.SearchSessionId, doctor.DoctorId, appointment.Id,
            officeSms.ok, officeEmail, patientSms.ok, patientEmail);

        return new LeadHandoffDoctorResult
        {
            DoctorId = doctor.DoctorId,
            DoctorName = doctor.DoctorName,
            PracticeName = doctor.PracticeName,
            OfficePhone = doctor.OfficePhone,
            AppointmentId = appointment.Id,
            OfficeSmsSent = officeSms.ok,
            OfficeEmailSent = officeEmail,
            PatientSmsSent = patientSms.ok,
            PatientEmailSent = patientEmail,
            ErrorNotes = handoff.ErrorNotes
        };
    }

    private static string BuildOfficeMessage(
        string patientName,
        string? patientPhone,
        string visitReason,
        string? timing,
        string practiceLabel)
    {
        _ = visitReason;
        var sb = new StringBuilder();
        sb.AppendLine("NuviDoc:");
        sb.Append("Lead for ").Append(practiceLabel).AppendLine(".");
        sb.AppendLine("Reason: Dental Implant.");
        sb.Append("Patient name: ").Append(patientName);
        if (!string.IsNullOrWhiteSpace(patientPhone))
            sb.AppendLine(",").Append("phone : ").Append(patientPhone.Trim()).AppendLine(".");
        else
            sb.AppendLine(".");
        //if (!string.IsNullOrWhiteSpace(timing))
        //    sb.Append("Preferred timing: ").Append(timing.Trim()).AppendLine(".");
        sb.Append("Please call the patient to schedule.");
        return sb.ToString();
    }

    private static string BuildPatientMessage(string practiceLabel, string doctorName, string officePhone)
    {
        var who = string.IsNullOrWhiteSpace(doctorName) ? practiceLabel : doctorName.Trim();
        return
            $"NuviDoc shared your info with {who} ({practiceLabel}). " +
            $"Office phone: {officePhone}. They may call you, or you can call them. " +
            "We'll follow up if nothing happens yet.";
    }

    private static string FormatPracticeLabel(string? practiceName, string? doctorName)
    {
        if (!string.IsNullOrWhiteSpace(practiceName))
            return practiceName.Trim();
        if (!string.IsNullOrWhiteSpace(doctorName))
            return doctorName.Trim();
        return "the practice";
    }

    private (bool ok, string? error) TrySendSms(string? phone, string body, string label)
    {
        try
        {
            var toE164 = TwilioOutboundRouting.ResolveToNumber(_twilio, phone);
            if (string.IsNullOrWhiteSpace(toE164))
                return (false, $"No valid {label} phone.");
            if (string.IsNullOrWhiteSpace(_twilio.AccountSid) || string.IsNullOrWhiteSpace(_twilio.AuthToken))
                return (false, "Twilio not configured.");

            var from = FirstNonEmpty(_twilio.SmsFromNumber, _twilio.FromNumber);
            if (string.IsNullOrWhiteSpace(from))
                return (false, "Twilio SMS from-number missing.");

            TwilioClient.Init(_twilio.AccountSid.Trim(), _twilio.AuthToken.Trim());
            MessageResource.Create(new CreateMessageOptions(new PhoneNumber(toE164))
            {
                From = new PhoneNumber(from.Trim()),
                Body = body
            });
            return (true, null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Lead handoff {Label} SMS failed: {Error}", label, ex.Message);
            return (false, $"{label} SMS: {ex.Message}");
        }
    }

    private async Task<bool> TrySendEmailAsync(
        string toAddress,
        string subject,
        string body,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await _email.SendAsync(toAddress.Trim(), subject, body, htmlBody: null, cancellationToken);
            return result.Success;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Lead handoff email to {To} failed: {Error}", toAddress, ex.Message);
            return false;
        }
    }

    public static bool LooksLikeEmail(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 200 || !value.Contains('@'))
            return false;
        try
        {
            _ = new MailAddress(value.Trim());
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string? NullIfWhite(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? FirstNonEmpty(params string?[] values)
    {
        foreach (var v in values)
        {
            if (!string.IsNullOrWhiteSpace(v))
                return v;
        }
        return null;
    }
}
