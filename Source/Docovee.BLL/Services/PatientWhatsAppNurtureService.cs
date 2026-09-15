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

public interface IPatientWhatsAppNurtureService
{
    Task<int> ProcessDueFollowUpsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Starts the interactive WhatsApp nurture for a patient due on this step, if eligible.
    /// Returns true when a conversation was started (counts as one nurture send).
    /// </summary>
    Task<bool> TryStartConversationAsync(
        int patientId,
        string? phone,
        string? fullName,
        int stepDay,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Handles inbound WhatsApp replies for the nurture flowchart.
    /// Returns true when the message was consumed by nurture (skip feedback handler).
    /// </summary>
    Task<bool> HandleInboundWhatsAppAsync(
        string fromWhatsApp,
        string? body,
        string? buttonPayload,
        string? listId,
        CancellationToken cancellationToken = default);
}

public sealed class PatientWhatsAppNurtureService : IPatientWhatsAppNurtureService
{
    private readonly DocoveeDbContext _db;
    private readonly ILeadHandoffService _leadHandoff;
    private readonly IAppointmentService _appointments;
    private readonly IDoctorQualityScoreService _qualityScore;
    private readonly TwilioOptions _twilio;
    private readonly IDocoveeLogger _logger;

    public PatientWhatsAppNurtureService(
        DocoveeDbContext db,
        ILeadHandoffService leadHandoff,
        IAppointmentService appointments,
        IDoctorQualityScoreService qualityScore,
        IOptions<TwilioOptions> twilio,
        IDocoveeLogger logger)
    {
        _db = db;
        _leadHandoff = leadHandoff;
        _appointments = appointments;
        _qualityScore = qualityScore;
        _twilio = twilio.Value;
        _logger = logger;
    }

    public async Task<int> ProcessDueFollowUpsAsync(CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        var due = await _db.PatientWhatsAppNurtures
            .Include(n => n.Doctor)
            .Where(n => n.Stage == PatientWhatsAppNurtureStages.FollowUpScheduled
                        && n.NextFollowUpAtUtc != null
                        && n.NextFollowUpAtUtc <= now)
            .OrderBy(n => n.NextFollowUpAtUtc)
            .Take(30)
            .ToListAsync(cancellationToken);

        var sent = 0;
        foreach (var row in due)
        {
            if (await IsNurtureBlockedAsync(row.PatientId, cancellationToken))
            {
                await CompleteAsync(row, cancellationToken);
                continue;
            }

            var practice = PracticeLabel(row.Doctor);
            var msg = $"Hi again — did you go to your appointment with {practice}? Reply YES or NO.";
            if (SendTextDetailed(row.WhatsAppTo, msg, out var sid, out var err))
            {
                row.Stage = PatientWhatsAppNurtureStages.AskAttended;
                row.NextFollowUpAtUtc = null;
                row.LastOutboundMessageSid = sid;
                row.LastError = null;
                row.UpdatedAtUtc = DateTime.UtcNow;
                await _db.SaveChangesAsync(cancellationToken);
                sent++;
            }
            else
            {
                row.LastError = err;
                row.UpdatedAtUtc = DateTime.UtcNow;
                await _db.SaveChangesAsync(cancellationToken);
            }
        }

        return sent;
    }

    public async Task<bool> TryStartConversationAsync(
        int patientId,
        string? phone,
        string? fullName,
        int stepDay,
        CancellationToken cancellationToken = default)
    {
        if (await IsNurtureBlockedAsync(patientId, cancellationToken))
            return false;

        if (await HasOpenConversationAsync(patientId, cancellationToken))
            return false;

        var appt = await FindLatestLeadHandoffAppointmentAsync(patientId, cancellationToken);
        if (appt == null)
            return false;

        if (await _db.PatientWhatsAppNurtures.AnyAsync(
                n => n.AppointmentId == appt.Id, cancellationToken))
            return false;

        var waTo = NormalizeWhatsAppAddress(ElevenLabsTwilioCallingService.ToE164(phone));
        if (string.IsNullOrWhiteSpace(waTo))
            return false;

        await _db.Entry(appt).Reference(a => a.Doctor).LoadAsync(cancellationToken);
        var firstName = FirstName(fullName);
        var practice = PracticeLabel(appt.Doctor);
        var greeting = string.IsNullOrWhiteSpace(firstName) ? "Hi" : $"Hi {firstName}";
        var msg =
            $"{greeting}, did you book an appointment with {practice}? Reply YES or NO.";

        if (!SendTextDetailed(waTo, msg, out var sid, out var err))
        {
            _logger.LogWarning("WhatsApp nurture start failed patient={PatientId}: {Error}", patientId, err);
            return false;
        }

        _db.PatientWhatsAppNurtures.Add(new PatientWhatsAppNurture
        {
            PatientId = patientId,
            AppointmentId = appt.Id,
            DoctorId = appt.DoctorId,
            Stage = PatientWhatsAppNurtureStages.AskBooked,
            WhatsAppTo = waTo,
            LastOutboundMessageSid = sid,
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow
        });
        await _db.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "WhatsApp nurture started patient={PatientId} appointment={AppointmentId} stepDay={StepDay}",
            patientId, appt.Id, stepDay);
        return true;
    }

    public async Task<bool> HandleInboundWhatsAppAsync(
        string fromWhatsApp,
        string? body,
        string? buttonPayload,
        string? listId,
        CancellationToken cancellationToken = default)
    {
        var toKey = NormalizeWhatsAppAddress(fromWhatsApp);
        if (string.IsNullOrWhiteSpace(toKey))
            return false;

        var selection = FirstNonEmpty(listId, buttonPayload, body)?.Trim();
        if (string.IsNullOrWhiteSpace(selection))
            return false;

        var nurture = await _db.PatientWhatsAppNurtures
            .Include(n => n.Doctor)
            .Include(n => n.Appointment)
            .Where(n => n.WhatsAppTo == toKey
                        && PatientWhatsAppNurtureStages.IsOpen(n.Stage))
            .OrderByDescending(n => n.UpdatedAtUtc)
            .FirstOrDefaultAsync(cancellationToken);

        if (nurture == null)
            return false;

        if (await IsNurtureBlockedAsync(nurture.PatientId, cancellationToken))
        {
            await CompleteAsync(nurture, cancellationToken);
            SendText(nurture.WhatsAppTo, "Thanks — we won't send more check-ins.");
            return true;
        }

        switch (nurture.Stage)
        {
            case PatientWhatsAppNurtureStages.AskBooked:
                await HandleAskBookedAsync(nurture, selection, cancellationToken);
                break;
            case PatientWhatsAppNurtureStages.AskContactPracticeNotBooked:
                await HandleAskContactPracticeNotBookedAsync(nurture, selection, cancellationToken);
                break;
            case PatientWhatsAppNurtureStages.AskAttended:
                await HandleAskAttendedAsync(nurture, selection, cancellationToken);
                break;
            case PatientWhatsAppNurtureStages.AskUpcomingOrPassed:
                await HandleAskUpcomingOrPassedAsync(nurture, selection, cancellationToken);
                break;
            case PatientWhatsAppNurtureStages.AskUpcomingWindow:
                await HandleAskUpcomingWindowAsync(nurture, selection, cancellationToken);
                break;
            case PatientWhatsAppNurtureStages.AskContactPracticeMissed:
                await HandleAskContactPracticeMissedAsync(nurture, selection, cancellationToken);
                break;
            case PatientWhatsAppNurtureStages.AskRating:
                await HandleAskRatingAsync(nurture, selection, cancellationToken);
                break;
            case PatientWhatsAppNurtureStages.AskExperience:
                await HandleAskExperienceAsync(nurture, body ?? selection, cancellationToken);
                break;
            default:
                return false;
        }

        return true;
    }

    private async Task HandleAskBookedAsync(
        PatientWhatsAppNurture nurture,
        string selection,
        CancellationToken cancellationToken)
    {
        if (IsYes(selection))
        {
            SendText(nurture.WhatsAppTo,
                "Great! Did you go to your appointment? Reply YES or NO.");
            nurture.Stage = PatientWhatsAppNurtureStages.AskAttended;
            nurture.UpdatedAtUtc = DateTime.UtcNow;
            await _db.SaveChangesAsync(cancellationToken);
            return;
        }

        if (!IsNo(selection))
            return;

        SendText(nurture.WhatsAppTo,
            "Should we contact the practice again on your behalf? Reply YES or NO.");
        nurture.Stage = PatientWhatsAppNurtureStages.AskContactPracticeNotBooked;
        nurture.UpdatedAtUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);
    }

    private async Task HandleAskContactPracticeNotBookedAsync(
        PatientWhatsAppNurture nurture,
        string selection,
        CancellationToken cancellationToken)
    {
        if (IsYes(selection))
        {
            var (ok, error) = await _leadHandoff.ResendLeadToOfficeAsync(nurture.AppointmentId, cancellationToken);
            var practice = PracticeLabel(nurture.Doctor);
            SendText(nurture.WhatsAppTo, ok
                ? $"We've contacted {practice} again. They may reach out soon. Thank you!"
                : error ?? "We couldn't reach the practice right now. Please try calling them directly.");
            await CompleteAsync(nurture, cancellationToken);
            return;
        }

        if (!IsNo(selection))
            return;

        SendText(nurture.WhatsAppTo, "Thank you. We're here if you need us later.");
        await CompleteAsync(nurture, cancellationToken);
    }

    private async Task HandleAskAttendedAsync(
        PatientWhatsAppNurture nurture,
        string selection,
        CancellationToken cancellationToken)
    {
        if (IsYes(selection))
        {
            await MarkAttendedAsync(nurture, cancellationToken);
            SendText(nurture.WhatsAppTo,
                "How was your visit? Reply with a star rating from 1 to 5 (5 is best).");
            nurture.Stage = PatientWhatsAppNurtureStages.AskRating;
            nurture.UpdatedAtUtc = DateTime.UtcNow;
            await _db.SaveChangesAsync(cancellationToken);
            return;
        }

        if (!IsNo(selection))
            return;

        SendText(nurture.WhatsAppTo,
            "Which is true? Reply UPCOMING if your appointment is still ahead, or PASSED if the date has passed.");
        nurture.Stage = PatientWhatsAppNurtureStages.AskUpcomingOrPassed;
        nurture.UpdatedAtUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);
    }

    private async Task HandleAskUpcomingOrPassedAsync(
        PatientWhatsAppNurture nurture,
        string selection,
        CancellationToken cancellationToken)
    {
        if (IsUpcoming(selection))
        {
            SendText(nurture.WhatsAppTo,
                "When is your appointment roughly? Reply: 10 DAYS, 20 DAYS, 30 DAYS, or 2 MONTHS.");
            nurture.Stage = PatientWhatsAppNurtureStages.AskUpcomingWindow;
            nurture.UpdatedAtUtc = DateTime.UtcNow;
            await _db.SaveChangesAsync(cancellationToken);
            return;
        }

        if (!IsPassed(selection))
            return;

        SendText(nurture.WhatsAppTo,
            "Would you like us to contact the practice again? Reply YES or NO.");
        nurture.Stage = PatientWhatsAppNurtureStages.AskContactPracticeMissed;
        nurture.UpdatedAtUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);
    }

    private async Task HandleAskUpcomingWindowAsync(
        PatientWhatsAppNurture nurture,
        string selection,
        CancellationToken cancellationToken)
    {
        var days = ParseUpcomingWindowDays(selection);
        if (days is null)
            return;

        nurture.NextFollowUpAtUtc = DateTime.UtcNow.AddDays(days.Value);
        nurture.Stage = PatientWhatsAppNurtureStages.FollowUpScheduled;
        nurture.UpdatedAtUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);
        SendText(nurture.WhatsAppTo, "Thanks! We'll check back with you then.");
    }

    private async Task HandleAskContactPracticeMissedAsync(
        PatientWhatsAppNurture nurture,
        string selection,
        CancellationToken cancellationToken)
    {
        if (IsYes(selection))
        {
            var (ok, error) = await _leadHandoff.ResendLeadToOfficeAsync(nurture.AppointmentId, cancellationToken);
            var practice = PracticeLabel(nurture.Doctor);
            SendText(nurture.WhatsAppTo, ok
                ? $"We've contacted {practice} again. They may reach out soon. Thank you!"
                : error ?? "We couldn't reach the practice right now. Please try calling them directly.");
            await CompleteAsync(nurture, cancellationToken);
            return;
        }

        if (!IsNo(selection))
            return;

        SendText(nurture.WhatsAppTo, "Thank you. We're here if you need us later.");
        await CompleteAsync(nurture, cancellationToken);
    }

    private async Task HandleAskRatingAsync(
        PatientWhatsAppNurture nurture,
        string selection,
        CancellationToken cancellationToken)
    {
        var rating = ParseRating(selection);
        if (rating is null)
            return;

        nurture.Rating = rating;
        nurture.Stage = PatientWhatsAppNurtureStages.AskExperience;
        nurture.UpdatedAtUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);
        SendText(nurture.WhatsAppTo, "Please describe your experience in a few words.");
    }

    private async Task HandleAskExperienceAsync(
        PatientWhatsAppNurture nurture,
        string? text,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Trim().Length < 3)
            return;

        if (!nurture.Rating.HasValue)
            return;

        nurture.ExperienceText = text.Trim();
        var saved = await SaveReviewAsync(nurture, text.Trim(), cancellationToken);
        SendText(nurture.WhatsAppTo, saved
            ? "Thank you for your feedback!"
            : "Thanks! We saved your note and appreciate you sharing.");
        await CompleteAsync(nurture, cancellationToken);
    }

    private async Task<bool> SaveReviewAsync(
        PatientWhatsAppNurture nurture,
        string reviewText,
        CancellationToken cancellationToken)
    {
        if (await _db.DoctorPatientReviews.AnyAsync(
                r => r.PatientId == nurture.PatientId && r.DoctorId == nurture.DoctorId,
                cancellationToken))
            return true;

        var patient = await _db.Patients.AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == nurture.PatientId, cancellationToken);
        if (patient == null)
            return false;

        _db.DoctorPatientReviews.Add(new DoctorPatientReview
        {
            DoctorId = nurture.DoctorId,
            PatientId = nurture.PatientId,
            ReviewerName = patient.FullName,
            Rating = nurture.Rating!.Value,
            ReviewText = reviewText,
            CreatedAt = DateTime.UtcNow
        });
        await _db.SaveChangesAsync(cancellationToken);
        await _qualityScore.RecomputeAndPersistAsync(nurture.DoctorId, cancellationToken);
        return true;
    }

    private async Task MarkAttendedAsync(
        PatientWhatsAppNurture nurture,
        CancellationToken cancellationToken)
    {
        var appt = nurture.Appointment
                   ?? await _db.Appointments.FirstOrDefaultAsync(a => a.Id == nurture.AppointmentId, cancellationToken);
        if (appt == null)
            return;

        if (appt.StartsAt == null)
        {
            appt.StartsAt = DateTime.UtcNow;
            appt.UpdatedAt = DateTime.UtcNow;
        }

        if (!string.Equals(
                AppointmentStatuses.Normalize(appt.Status),
                AppointmentStatuses.Completed,
                StringComparison.OrdinalIgnoreCase)
            && !AppointmentStatuses.IsCanceled(appt.Status)
            && !AppointmentStatuses.IsPatientNoShow(appt.Status))
        {
            await _appointments.UpdateStatusAsync(
                appt.DoctorId,
                appt.Id,
                AppointmentStatuses.Completed,
                cancellationToken);
        }
    }

    private async Task CompleteAsync(PatientWhatsAppNurture nurture, CancellationToken cancellationToken)
    {
        nurture.Stage = PatientWhatsAppNurtureStages.Completed;
        nurture.CompletedAtUtc = DateTime.UtcNow;
        nurture.UpdatedAtUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);
    }

    private async Task<bool> IsNurtureBlockedAsync(int patientId, CancellationToken cancellationToken)
    {
        var patient = await _db.Patients.AsNoTracking()
            .Where(p => p.Id == patientId)
            .Select(p => new { p.NurtureStopped, p.IsDeleted })
            .FirstOrDefaultAsync(cancellationToken);
        if (patient == null || patient.IsDeleted || patient.NurtureStopped)
            return true;

        if (await _db.DoctorPatientReviews.AnyAsync(r => r.PatientId == patientId, cancellationToken))
            return true;

        return await _db.AppointmentFeedbackRequests.AnyAsync(
            f => f.PatientId == patientId && f.Stage == AppointmentFeedbackStages.Completed,
            cancellationToken);
    }

    private async Task<bool> HasOpenConversationAsync(int patientId, CancellationToken cancellationToken) =>
        await _db.PatientWhatsAppNurtures.AnyAsync(
            n => n.PatientId == patientId && PatientWhatsAppNurtureStages.IsOpen(n.Stage),
            cancellationToken);

    private async Task<Appointment?> FindLatestLeadHandoffAppointmentAsync(
        int patientId,
        CancellationToken cancellationToken) =>
        await _db.Appointments
            .Where(a => a.PatientId == patientId
                        && a.Source == AppointmentSources.NuviLeadHandoff
                        && !AppointmentStatuses.IsCanceled(a.Status))
            .OrderByDescending(a => a.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);

    private void SendText(string? whatsAppTo, string body) =>
        SendTextDetailed(whatsAppTo, body, out _, out _);

    private bool SendTextDetailed(string? whatsAppTo, string body, out string? sid, out string? error)
    {
        sid = null;
        error = null;
        try
        {
            if (string.IsNullOrWhiteSpace(whatsAppTo))
            {
                error = "Missing WhatsApp address.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(_twilio.AccountSid) || string.IsNullOrWhiteSpace(_twilio.AuthToken))
            {
                error = "Twilio not configured.";
                return false;
            }

            var from = NormalizeWhatsAppAddress(_twilio.WhatsAppFromNumber);
            var to = NormalizeWhatsAppAddress(whatsAppTo);
            if (string.IsNullOrWhiteSpace(from) || string.IsNullOrWhiteSpace(to))
            {
                error = "Invalid WhatsApp addresses.";
                return false;
            }

            TwilioClient.Init(_twilio.AccountSid.Trim(), _twilio.AuthToken.Trim());
            var msg = MessageResource.Create(new CreateMessageOptions(new PhoneNumber(to))
            {
                From = new PhoneNumber(from),
                Body = body
            });
            sid = msg.Sid;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            _logger.LogWarning("WhatsApp nurture send failed: {Error}", ex.Message);
            return false;
        }
    }

    private static string PracticeLabel(Doctor? doctor)
    {
        if (doctor == null)
            return "the practice";
        if (!string.IsNullOrWhiteSpace(doctor.PracticeName))
            return doctor.PracticeName.Trim();
        if (!string.IsNullOrWhiteSpace(doctor.Name))
            return doctor.Name.Trim();
        return "the practice";
    }

    private static int? ParseRating(string selection)
    {
        var star = AppointmentFeedbackItemIds.ParseStarRating(selection);
        if (star is >= 1 and <= 5)
            return star;

        if (int.TryParse(selection.Trim().Split(' ', '-')[0], out var n) && n is >= 1 and <= 5)
            return n;

        return null;
    }

    private static int? ParseUpcomingWindowDays(string selection)
    {
        var s = selection.Trim().ToLowerInvariant();
        if (s.Contains("10") || s.Contains("ten"))
            return 10;
        if (s.Contains("20"))
            return 20;
        if (s.Contains("30"))
            return 30;
        if (s.Contains("2 month") || s.Contains("two month") || s.Contains("60"))
            return 60;
        return null;
    }

    private static bool IsYes(string s)
    {
        var t = s.Trim().ToLowerInvariant();
        return t is "yes" or "y" or "yeah" or "yep" or "sure" or "ok" or "okay" or "booked";
    }

    private static bool IsNo(string s)
    {
        var t = s.Trim().ToLowerInvariant();
        return t is "no" or "n" or "nope" or "not" or "not yet" or "haven't" or "havent";
    }

    private static bool IsUpcoming(string s)
    {
        var t = s.Trim().ToLowerInvariant();
        return t.Contains("upcoming") || t.Contains("future") || t.Contains("ahead") || t.Contains("not yet");
    }

    private static bool IsPassed(string s)
    {
        var t = s.Trim().ToLowerInvariant();
        return t.Contains("passed") || t.Contains("missed") || t.Contains("no show")
               || t.Contains("noshow") || t.Contains("didn't go") || t.Contains("didnt go");
    }

    private static string? NormalizeWhatsAppAddress(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var v = value.Trim();
        if (v.StartsWith("whatsapp:", StringComparison.OrdinalIgnoreCase))
            return "whatsapp:" + v["whatsapp:".Length..].Trim();
        var e164 = ElevenLabsTwilioCallingService.ToE164(v);
        return string.IsNullOrWhiteSpace(e164) ? null : "whatsapp:" + e164;
    }

    private static string FirstName(string? fullName)
    {
        if (string.IsNullOrWhiteSpace(fullName))
            return "";
        return fullName.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
    }

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
}
