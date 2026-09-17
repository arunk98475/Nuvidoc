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

public interface IPatientSmsNurtureService
{
    Task<int> ProcessDueFollowUpsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Starts the interactive SMS nurture for a patient due on this step, if eligible.
    /// Returns true when a conversation was started (counts as one nurture send).
    /// </summary>
    Task<bool> TryStartConversationAsync(
        int patientId,
        string? phone,
        string? fullName,
        int stepDay,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Handles inbound SMS (or WhatsApp-compat) replies for the nurture flowchart.
    /// Returns true when the message was consumed by nurture (skip feedback handler).
    /// </summary>
    Task<bool> HandleInboundSmsAsync(
        string fromPhone,
        string? body,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// STOP / unsubscribe: halt nurture, close open conversations, send one confirmation.
    /// </summary>
    Task HandleStopRequestAsync(string fromPhone, CancellationToken cancellationToken = default);
}

public sealed class PatientSmsNurtureService : IPatientSmsNurtureService
{
    private readonly DocoveeDbContext _db;
    private readonly ILeadHandoffService _leadHandoff;
    private readonly IAppointmentService _appointments;
    private readonly IDoctorQualityScoreService _qualityScore;
    private readonly ISmsReplyExtractionService _extractor;
    private readonly TwilioOptions _twilio;
    private readonly IDocoveeLogger _logger;

    public PatientSmsNurtureService(
        DocoveeDbContext db,
        ILeadHandoffService leadHandoff,
        IAppointmentService appointments,
        IDoctorQualityScoreService qualityScore,
        ISmsReplyExtractionService extractor,
        IOptions<TwilioOptions> twilio,
        IDocoveeLogger logger)
    {
        _db = db;
        _leadHandoff = leadHandoff;
        _appointments = appointments;
        _qualityScore = qualityScore;
        _extractor = extractor;
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
            if (SendTextDetailed(ConversationPhone(row), msg, out var sid, out var err))
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

        var e164 = ElevenLabsTwilioCallingService.ToE164(phone);
        if (string.IsNullOrWhiteSpace(e164))
            return false;

        await _db.Entry(appt).Reference(a => a.Doctor).LoadAsync(cancellationToken);
        var firstName = FirstName(fullName);
        var practice = PracticeLabel(appt.Doctor);
        var greeting = string.IsNullOrWhiteSpace(firstName) ? "Hi" : $"Hi {firstName}";
        var msg =
            $"{greeting}, did you book an appointment with {practice}? Reply YES or NO.";

        if (!SendTextDetailed(e164, msg, out var sid, out var err))
        {
            _logger.LogWarning("SMS nurture start failed patient={PatientId}: {Error}", patientId, err);
            return false;
        }

        _db.PatientWhatsAppNurtures.Add(new PatientWhatsAppNurture
        {
            PatientId = patientId,
            AppointmentId = appt.Id,
            DoctorId = appt.DoctorId,
            Stage = PatientWhatsAppNurtureStages.AskBooked,
            PhoneE164 = e164,
            LastOutboundMessageSid = sid,
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow
        });
        await _db.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "SMS nurture started patient={PatientId} appointment={AppointmentId} stepDay={StepDay}",
            patientId, appt.Id, stepDay);
        return true;
    }

    public async Task<bool> HandleInboundSmsAsync(
        string fromPhone,
        string? body,
        CancellationToken cancellationToken = default)
    {
        var e164 = ElevenLabsTwilioCallingService.ToE164(fromPhone);
        if (string.IsNullOrWhiteSpace(e164))
            return false;

        var selection = body?.Trim();
        if (string.IsNullOrWhiteSpace(selection))
            return false;

        var nurture = await FindOpenByPhoneAsync(e164, cancellationToken);
        if (nurture == null)
            return false;

        if (await IsNurtureBlockedAsync(nurture.PatientId, cancellationToken))
        {
            await CompleteAsync(nurture, cancellationToken);
            SendText(ConversationPhone(nurture), "Thanks — we won't send more check-ins.");
            return true;
        }

        var extracted = await _extractor.ExtractAsync(
            nurture.Stage,
            QuestionHint(nurture.Stage),
            selection,
            cancellationToken);

        if (extracted.IsOptOut)
        {
            await HandleStopRequestAsync(e164, cancellationToken);
            return true;
        }

        if (extracted.IsUnclear)
        {
            SendText(ConversationPhone(nurture), _extractor.ClarifyPrompt(nurture.Stage));
            return true;
        }

        switch (nurture.Stage)
        {
            case PatientWhatsAppNurtureStages.AskBooked:
                await HandleAskBookedAsync(nurture, extracted, cancellationToken);
                break;
            case PatientWhatsAppNurtureStages.AskContactPracticeNotBooked:
                await HandleAskContactPracticeNotBookedAsync(nurture, extracted, cancellationToken);
                break;
            case PatientWhatsAppNurtureStages.AskAttended:
                await HandleAskAttendedAsync(nurture, extracted, cancellationToken);
                break;
            case PatientWhatsAppNurtureStages.AskUpcomingOrPassed:
                await HandleAskUpcomingOrPassedAsync(nurture, extracted, cancellationToken);
                break;
            case PatientWhatsAppNurtureStages.AskUpcomingWindow:
                await HandleAskUpcomingWindowAsync(nurture, extracted, cancellationToken);
                break;
            case PatientWhatsAppNurtureStages.AskContactPracticeMissed:
                await HandleAskContactPracticeMissedAsync(nurture, extracted, cancellationToken);
                break;
            case PatientWhatsAppNurtureStages.AskRating:
                await HandleAskRatingAsync(nurture, extracted, cancellationToken);
                break;
            case PatientWhatsAppNurtureStages.AskExperience:
                await HandleAskExperienceAsync(nurture, extracted, cancellationToken);
                break;
            default:
                return false;
        }

        return true;
    }

    public async Task HandleStopRequestAsync(string fromPhone, CancellationToken cancellationToken = default)
    {
        var e164 = ElevenLabsTwilioCallingService.ToE164(fromPhone);
        if (string.IsNullOrWhiteSpace(e164))
            return;

        var now = DateTime.UtcNow;
        var openNurtures = await _db.PatientWhatsAppNurtures
            .Where(n => PatientWhatsAppNurtureStages.IsOpen(n.Stage)
                        && (n.PhoneE164 == e164
                            || n.WhatsAppTo == "whatsapp:" + e164))
            .ToListAsync(cancellationToken);

        var openFeedback = await _db.AppointmentFeedbackRequests
            .Where(f => f.Stage != AppointmentFeedbackStages.Completed
                        && f.Stage != AppointmentFeedbackStages.NoShow
                        && f.Stage != AppointmentFeedbackStages.Failed
                        && f.Stage != AppointmentFeedbackStages.Pending
                        && (f.PhoneE164 == e164
                            || f.WhatsAppTo == "whatsapp:" + e164))
            .ToListAsync(cancellationToken);

        var patientIds = openNurtures.Select(n => n.PatientId)
            .Concat(openFeedback.Where(f => f.PatientId.HasValue).Select(f => f.PatientId!.Value))
            .ToHashSet();

        var last10 = e164.Length >= 10 ? e164[^10..] : e164;
        var extraPatients = await _db.Patients
            .Where(p => !p.IsDeleted
                        && (p.Phone == e164
                            || p.Phone == last10
                            || p.Phone == "1" + last10
                            || p.Phone == "+1" + last10))
            .Select(p => p.Id)
            .ToListAsync(cancellationToken);
        foreach (var id in extraPatients)
            patientIds.Add(id);

        if (patientIds.Count > 0)
        {
            var patients = await _db.Patients
                .Where(p => patientIds.Contains(p.Id))
                .ToListAsync(cancellationToken);
            foreach (var patient in patients)
                patient.NurtureStopped = true;

            var moreNurtures = await _db.PatientWhatsAppNurtures
                .Where(n => patientIds.Contains(n.PatientId) && PatientWhatsAppNurtureStages.IsOpen(n.Stage))
                .ToListAsync(cancellationToken);
            foreach (var row in moreNurtures)
            {
                row.Stage = PatientWhatsAppNurtureStages.Completed;
                row.CompletedAtUtc = now;
                row.UpdatedAtUtc = now;
            }

            var moreFeedback = await _db.AppointmentFeedbackRequests
                .Where(f => f.PatientId != null
                            && patientIds.Contains(f.PatientId.Value)
                            && f.Stage != AppointmentFeedbackStages.Completed
                            && f.Stage != AppointmentFeedbackStages.NoShow
                            && f.Stage != AppointmentFeedbackStages.Failed)
                .ToListAsync(cancellationToken);
            foreach (var row in moreFeedback)
            {
                row.Stage = AppointmentFeedbackStages.Failed;
                row.LastError = "Patient opted out (STOP).";
                row.UpdatedAtUtc = now;
            }
        }
        else
        {
            foreach (var row in openNurtures)
            {
                row.Stage = PatientWhatsAppNurtureStages.Completed;
                row.CompletedAtUtc = now;
                row.UpdatedAtUtc = now;
            }

            foreach (var row in openFeedback)
            {
                row.Stage = AppointmentFeedbackStages.Failed;
                row.LastError = "Patient opted out (STOP).";
                row.UpdatedAtUtc = now;
            }
        }

        await _db.SaveChangesAsync(cancellationToken);
        SendText(e164, "You have been unsubscribed from NuviDoc check-ins. Reply START to your clinic if you need help later.");
    }

    private async Task HandleAskBookedAsync(
        PatientWhatsAppNurture nurture,
        SmsReplyExtraction extracted,
        CancellationToken cancellationToken)
    {
        if (extracted.IsYes)
        {
            SendText(ConversationPhone(nurture),
                "Great! Did you go to your appointment? Reply YES or NO.");
            nurture.Stage = PatientWhatsAppNurtureStages.AskAttended;
            nurture.UpdatedAtUtc = DateTime.UtcNow;
            await _db.SaveChangesAsync(cancellationToken);
            return;
        }

        if (!extracted.IsNo)
        {
            SendText(ConversationPhone(nurture), _extractor.ClarifyPrompt(nurture.Stage));
            return;
        }

        SendText(ConversationPhone(nurture),
            "Should we contact the practice again on your behalf? Reply YES or NO.");
        nurture.Stage = PatientWhatsAppNurtureStages.AskContactPracticeNotBooked;
        nurture.UpdatedAtUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);
    }

    private async Task HandleAskContactPracticeNotBookedAsync(
        PatientWhatsAppNurture nurture,
        SmsReplyExtraction extracted,
        CancellationToken cancellationToken)
    {
        if (extracted.IsYes)
        {
            var (ok, error) = await _leadHandoff.ResendLeadToOfficeAsync(nurture.AppointmentId, cancellationToken);
            var practice = PracticeLabel(nurture.Doctor);
            SendText(ConversationPhone(nurture), ok
                ? $"We've contacted {practice} again. They may reach out soon. Thank you!"
                : error ?? "We couldn't reach the practice right now. Please try calling them directly.");
            await CompleteAsync(nurture, cancellationToken);
            return;
        }

        if (!extracted.IsNo)
        {
            SendText(ConversationPhone(nurture), _extractor.ClarifyPrompt(nurture.Stage));
            return;
        }

        SendText(ConversationPhone(nurture), "Thank you. We're here if you need us later.");
        await CompleteAsync(nurture, cancellationToken);
    }

    private async Task HandleAskAttendedAsync(
        PatientWhatsAppNurture nurture,
        SmsReplyExtraction extracted,
        CancellationToken cancellationToken)
    {
        if (extracted.IsYes)
        {
            await MarkAttendedAsync(nurture, cancellationToken);
            SendText(ConversationPhone(nurture),
                "How was your visit? Reply with a star rating from 1 to 5 (5 is best).");
            nurture.Stage = PatientWhatsAppNurtureStages.AskRating;
            nurture.UpdatedAtUtc = DateTime.UtcNow;
            await _db.SaveChangesAsync(cancellationToken);
            return;
        }

        if (!extracted.IsNo)
        {
            SendText(ConversationPhone(nurture), _extractor.ClarifyPrompt(nurture.Stage));
            return;
        }

        SendText(ConversationPhone(nurture),
            "Which is true? Reply UPCOMING if your appointment is still ahead, or PASSED if the date has passed.");
        nurture.Stage = PatientWhatsAppNurtureStages.AskUpcomingOrPassed;
        nurture.UpdatedAtUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);
    }

    private async Task HandleAskUpcomingOrPassedAsync(
        PatientWhatsAppNurture nurture,
        SmsReplyExtraction extracted,
        CancellationToken cancellationToken)
    {
        if (extracted.IsUpcoming)
        {
            SendText(ConversationPhone(nurture),
                "When is your appointment roughly? Reply: 10 DAYS, 20 DAYS, 30 DAYS, or 2 MONTHS.");
            nurture.Stage = PatientWhatsAppNurtureStages.AskUpcomingWindow;
            nurture.UpdatedAtUtc = DateTime.UtcNow;
            await _db.SaveChangesAsync(cancellationToken);
            return;
        }

        if (!extracted.IsPassed)
        {
            SendText(ConversationPhone(nurture), _extractor.ClarifyPrompt(nurture.Stage));
            return;
        }

        SendText(ConversationPhone(nurture),
            "Would you like us to contact the practice again? Reply YES or NO.");
        nurture.Stage = PatientWhatsAppNurtureStages.AskContactPracticeMissed;
        nurture.UpdatedAtUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);
    }

    private async Task HandleAskUpcomingWindowAsync(
        PatientWhatsAppNurture nurture,
        SmsReplyExtraction extracted,
        CancellationToken cancellationToken)
    {
        if (extracted.WindowDays is null)
        {
            SendText(ConversationPhone(nurture), _extractor.ClarifyPrompt(nurture.Stage));
            return;
        }

        nurture.NextFollowUpAtUtc = DateTime.UtcNow.AddDays(extracted.WindowDays.Value);
        nurture.Stage = PatientWhatsAppNurtureStages.FollowUpScheduled;
        nurture.UpdatedAtUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);
        SendText(ConversationPhone(nurture), "Thanks! We'll check back with you then.");
    }

    private async Task HandleAskContactPracticeMissedAsync(
        PatientWhatsAppNurture nurture,
        SmsReplyExtraction extracted,
        CancellationToken cancellationToken)
    {
        if (extracted.IsYes)
        {
            var (ok, error) = await _leadHandoff.ResendLeadToOfficeAsync(nurture.AppointmentId, cancellationToken);
            var practice = PracticeLabel(nurture.Doctor);
            SendText(ConversationPhone(nurture), ok
                ? $"We've contacted {practice} again. They may reach out soon. Thank you!"
                : error ?? "We couldn't reach the practice right now. Please try calling them directly.");
            await CompleteAsync(nurture, cancellationToken);
            return;
        }

        if (!extracted.IsNo)
        {
            SendText(ConversationPhone(nurture), _extractor.ClarifyPrompt(nurture.Stage));
            return;
        }

        SendText(ConversationPhone(nurture), "Thank you. We're here if you need us later.");
        await CompleteAsync(nurture, cancellationToken);
    }

    private async Task HandleAskRatingAsync(
        PatientWhatsAppNurture nurture,
        SmsReplyExtraction extracted,
        CancellationToken cancellationToken)
    {
        if (extracted.Rating is null)
        {
            if (extracted.IsNoShow)
            {
                SendText(ConversationPhone(nurture),
                    "Which is true? Reply UPCOMING if your appointment is still ahead, or PASSED if the date has passed.");
                nurture.Stage = PatientWhatsAppNurtureStages.AskUpcomingOrPassed;
                nurture.UpdatedAtUtc = DateTime.UtcNow;
                await _db.SaveChangesAsync(cancellationToken);
                return;
            }

            SendText(ConversationPhone(nurture), _extractor.ClarifyPrompt(nurture.Stage));
            return;
        }

        nurture.Rating = extracted.Rating;
        nurture.Stage = PatientWhatsAppNurtureStages.AskExperience;
        nurture.UpdatedAtUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);
        SendText(ConversationPhone(nurture), "Please describe your experience in a few words.");
    }

    private async Task HandleAskExperienceAsync(
        PatientWhatsAppNurture nurture,
        SmsReplyExtraction extracted,
        CancellationToken cancellationToken)
    {
        var text = extracted.ExperienceText?.Trim();
        if (string.IsNullOrWhiteSpace(text) || text.Length < 3 || !nurture.Rating.HasValue)
        {
            SendText(ConversationPhone(nurture), _extractor.ClarifyPrompt(nurture.Stage));
            return;
        }

        nurture.ExperienceText = text;
        var saved = await SaveReviewAsync(nurture, text, cancellationToken);
        SendText(ConversationPhone(nurture), saved
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

    private async Task<PatientWhatsAppNurture?> FindOpenByPhoneAsync(
        string e164,
        CancellationToken cancellationToken) =>
        await _db.PatientWhatsAppNurtures
            .Include(n => n.Doctor)
            .Include(n => n.Appointment)
            .Where(n => PatientWhatsAppNurtureStages.IsOpen(n.Stage)
                        && (n.PhoneE164 == e164 || n.WhatsAppTo == "whatsapp:" + e164))
            .OrderByDescending(n => n.UpdatedAtUtc)
            .FirstOrDefaultAsync(cancellationToken);

    private async Task<Appointment?> FindLatestLeadHandoffAppointmentAsync(
        int patientId,
        CancellationToken cancellationToken) =>
        await _db.Appointments
            .Where(a => a.PatientId == patientId
                        && a.Source == AppointmentSources.NuviLeadHandoff
                        && !AppointmentStatuses.IsCanceled(a.Status))
            .OrderByDescending(a => a.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);

    private void SendText(string? phoneE164, string body) =>
        SendTextDetailed(phoneE164, body, out _, out _);

    private bool SendTextDetailed(string? phoneE164, string body, out string? sid, out string? error)
    {
        sid = null;
        error = null;
        try
        {
            var to = ElevenLabsTwilioCallingService.ToE164(phoneE164);
            if (string.IsNullOrWhiteSpace(to))
            {
                error = "Missing SMS address.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(_twilio.AccountSid) || string.IsNullOrWhiteSpace(_twilio.AuthToken))
            {
                error = "Twilio not configured.";
                return false;
            }

            var from = FirstNonEmpty(_twilio.SmsFromNumber, _twilio.FromNumber);
            if (string.IsNullOrWhiteSpace(from))
            {
                error = "Missing SMS from number.";
                return false;
            }

            TwilioClient.Init(_twilio.AccountSid.Trim(), _twilio.AuthToken.Trim());
            var msg = MessageResource.Create(new CreateMessageOptions(new PhoneNumber(to))
            {
                From = new PhoneNumber(from.Trim()),
                Body = body
            });
            sid = msg.Sid;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            _logger.LogWarning("SMS nurture send failed: {Error}", ex.Message);
            return false;
        }
    }

    private static string? ConversationPhone(PatientWhatsAppNurture row) =>
        FirstNonEmpty(row.PhoneE164, ElevenLabsTwilioCallingService.ToE164(row.WhatsAppTo));

    private static string QuestionHint(string stage) => stage switch
    {
        PatientWhatsAppNurtureStages.AskBooked => "Did you book an appointment? Reply YES or NO.",
        PatientWhatsAppNurtureStages.AskContactPracticeNotBooked => "Should we contact the practice again? Reply YES or NO.",
        PatientWhatsAppNurtureStages.AskAttended => "Did you go to your appointment? Reply YES or NO.",
        PatientWhatsAppNurtureStages.AskUpcomingOrPassed => "Is the appointment UPCOMING or PASSED?",
        PatientWhatsAppNurtureStages.AskUpcomingWindow => "When is the appointment? 10 DAYS, 20 DAYS, 30 DAYS, or 2 MONTHS.",
        PatientWhatsAppNurtureStages.AskContactPracticeMissed => "Should we contact the practice again? Reply YES or NO.",
        PatientWhatsAppNurtureStages.AskRating => "Rate the visit from 1 to 5.",
        PatientWhatsAppNurtureStages.AskExperience => "Describe your experience in a few words.",
        _ => "Reply to the previous question."
    };

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

    private static string FirstName(string? fullName)
    {
        if (string.IsNullOrWhiteSpace(fullName))
            return "";
        return fullName.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
    }

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
}
