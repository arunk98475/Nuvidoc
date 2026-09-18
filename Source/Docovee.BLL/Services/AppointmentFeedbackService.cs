using System.Globalization;
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

public interface IAppointmentFeedbackService
{
    Task<int> ProcessDueFeedbackRequestsAsync(CancellationToken cancellationToken = default);
    Task<bool> HandleInboundSmsAsync(
        string fromPhone,
        string? body,
        CancellationToken cancellationToken = default);
    Task<(bool Success, string? Error)> ReportNoShowAsPatientAsync(
        int patientId,
        int appointmentId,
        CancellationToken cancellationToken = default);
    Task<(bool Success, string? Error)> SubmitReviewAsPatientAsync(
        int patientId,
        int appointmentId,
        int doctorId,
        int rating,
        string reviewText,
        string? waitingTime,
        string? recommendation,
        string? photoUrl = null,
        CancellationToken cancellationToken = default);
}

public sealed class AppointmentFeedbackService : IAppointmentFeedbackService
{
    private readonly DocoveeDbContext _db;
    private readonly IAppSettingsService _appSettings;
    private readonly IAppointmentService _appointments;
    private readonly IDoctorReviewService _reviews;
    private readonly ISmsReplyExtractionService _extractor;
    private readonly TwilioOptions _twilio;
    private readonly IDocoveeLogger _logger;

    public AppointmentFeedbackService(
        DocoveeDbContext db,
        IAppSettingsService appSettings,
        IAppointmentService appointments,
        IDoctorReviewService reviews,
        ISmsReplyExtractionService extractor,
        IOptions<TwilioOptions> twilio,
        IDocoveeLogger logger)
    {
        _db = db;
        _appSettings = appSettings;
        _appointments = appointments;
        _reviews = reviews;
        _extractor = extractor;
        _twilio = twilio.Value;
        _logger = logger;
    }

    public async Task<int> ProcessDueFeedbackRequestsAsync(CancellationToken cancellationToken = default)
    {
        if (!await _appSettings.GetFeedbackRequestEnabledAsync(cancellationToken))
            return 0;

        var hours = await _appSettings.GetFeedbackRequestHoursAfterBookingAsync(cancellationToken);
        var now = DateTime.UtcNow;
        var dueBefore = now.AddHours(-hours);

        var excludedStatuses = new[]
        {
            AppointmentStatuses.PracticeCanceled,
            AppointmentStatuses.PatientCanceled,
            AppointmentStatuses.Cancelled,
            AppointmentStatuses.PatientNoShow
        };

        var dueAppointments = await _db.Appointments.AsNoTracking()
            .Include(a => a.Doctor)
            .Where(a => a.CreatedAt <= dueBefore
                && a.Source != AppointmentSources.PmsInbound
                && !excludedStatuses.Contains(a.Status)
                && !_db.AppointmentFeedbackRequests.Any(f => f.AppointmentId == a.Id))
            .OrderBy(a => a.CreatedAt)
            .Take(50)
            .ToListAsync(cancellationToken);

        var sent = 0;
        foreach (var appointment in dueAppointments)
        {
            try
            {
                if (await SendInitialFeedbackAsync(appointment, hours, cancellationToken))
                    sent++;
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Feedback send failed for appointment {AppointmentId}: {Error}", appointment.Id, ex.Message);
            }
        }

        return sent;
    }

    public async Task<bool> HandleInboundSmsAsync(
        string fromPhone,
        string? body,
        CancellationToken cancellationToken = default)
    {
        var e164 = ElevenLabsTwilioCallingService.ToE164(fromPhone);
        if (string.IsNullOrWhiteSpace(e164))
            return false;

        var feedback = await _db.AppointmentFeedbackRequests
            .Where(f => (f.PhoneE164 == e164 || f.WhatsAppTo == "whatsapp:" + e164)
                && f.Stage != AppointmentFeedbackStages.Completed
                && f.Stage != AppointmentFeedbackStages.NoShow
                && f.Stage != AppointmentFeedbackStages.Failed
                && f.Stage != AppointmentFeedbackStages.Pending)
            .OrderByDescending(f => f.UpdatedAtUtc)
            .FirstOrDefaultAsync(cancellationToken);

        if (feedback == null)
        {
            _logger.LogInformation("No open feedback survey for SMS {From}", e164);
            return false;
        }

        var selection = body?.Trim();
        if (string.IsNullOrWhiteSpace(selection))
            return true;

        var extracted = await _extractor.ExtractAsync(
            feedback.Stage,
            QuestionHint(feedback.Stage),
            selection,
            cancellationToken);

        if (extracted.IsOptOut)
        {
            feedback.Stage = AppointmentFeedbackStages.Failed;
            feedback.LastError = "Patient opted out.";
            feedback.UpdatedAtUtc = DateTime.UtcNow;
            if (feedback.PatientId is int pid)
            {
                var patient = await _db.Patients.FirstOrDefaultAsync(p => p.Id == pid, cancellationToken);
                if (patient != null)
                    patient.NurtureStopped = true;
            }

            await _db.SaveChangesAsync(cancellationToken);
            TrySendSms(ConversationPhone(feedback), "You have been unsubscribed from NuviDoc check-ins.");
            return true;
        }

        if (extracted.IsUnclear)
        {
            TrySendSms(ConversationPhone(feedback), _extractor.ClarifyPrompt(feedback.Stage));
            return true;
        }

        switch (feedback.Stage)
        {
            case AppointmentFeedbackStages.RatingSent:
                await HandleRatingReplyAsync(feedback, extracted, cancellationToken);
                break;
            case AppointmentFeedbackStages.WaitingSent:
                await HandleWaitingReplyAsync(feedback, extracted, cancellationToken);
                break;
            case AppointmentFeedbackStages.RecommendSent:
                await HandleRecommendReplyAsync(feedback, extracted, cancellationToken);
                break;
            case AppointmentFeedbackStages.ReviewTextAwaiting:
                await HandleReviewTextReplyAsync(feedback, extracted, cancellationToken);
                break;
        }

        return true;
    }

    public async Task<(bool Success, string? Error)> ReportNoShowAsPatientAsync(
        int patientId,
        int appointmentId,
        CancellationToken cancellationToken = default)
    {
        var appointment = await _db.Appointments
            .FirstOrDefaultAsync(a => a.Id == appointmentId, cancellationToken);
        if (appointment == null)
            return (false, "Appointment not found.");
        if (!await PatientOwnsAppointmentAsync(patientId, appointment, cancellationToken))
            return (false, "Appointment not found.");

        var feedbackEnabled = await _appSettings.GetFeedbackRequestEnabledAsync(cancellationToken);
        var hours = await _appSettings.GetFeedbackRequestHoursAfterBookingAsync(cancellationToken);
        if (!AppointmentStatuses.CanPatientLeaveFeedback(
                appointment.Status,
                appointment.CreatedAt,
                appointment.StartsAt,
                feedbackEnabled,
                hours,
                hasExistingReview: false))
            return (false, "Feedback is not available for this appointment yet.");

        var result = await ApplyNoShowAsync(appointment.DoctorId, appointment.Id, cancellationToken);
        if (result.Success)
        {
            var row = await _db.AppointmentFeedbackRequests
                .FirstOrDefaultAsync(f => f.AppointmentId == appointmentId, cancellationToken);
            if (row != null)
            {
                row.Stage = AppointmentFeedbackStages.NoShow;
                row.UpdatedAtUtc = DateTime.UtcNow;
                await _db.SaveChangesAsync(cancellationToken);
            }
        }

        return result;
    }

    public async Task<(bool Success, string? Error)> SubmitReviewAsPatientAsync(
        int patientId,
        int appointmentId,
        int doctorId,
        int rating,
        string reviewText,
        string? waitingTime,
        string? recommendation,
        string? photoUrl = null,
        CancellationToken cancellationToken = default)
    {
        var appointment = await _db.Appointments
            .FirstOrDefaultAsync(a => a.Id == appointmentId && a.DoctorId == doctorId, cancellationToken);
        if (appointment == null)
            return (false, "Appointment not found.");
        if (!await PatientOwnsAppointmentAsync(patientId, appointment, cancellationToken))
            return (false, "Appointment not found.");

        var showed = await ApplyShowedAsync(doctorId, appointmentId, cancellationToken);
        if (!showed.Success && !string.IsNullOrWhiteSpace(showed.Error))
            _logger.LogWarning("Could not mark appointment {Id} showed before review: {Error}", appointmentId, showed.Error);

        var (success, error) = await _reviews.AddReviewForPatientAsync(
            patientId,
            doctorId,
            rating,
            reviewText,
            waitingTime,
            recommendation,
            photoUrl,
            appointmentId,
            cancellationToken);

        if (success)
            await MarkFeedbackCompletedFromWebAsync(appointmentId, rating, waitingTime, recommendation, reviewText, cancellationToken);

        return (success, error);
    }

    private async Task<bool> SendInitialFeedbackAsync(
        Appointment appointment,
        int hours,
        CancellationToken cancellationToken)
    {
        var doctorName = appointment.Doctor?.Name ?? "your doctor";
        var bookedTime = appointment.StartsAt?.ToString("MMM d, yyyy 'at' h:mm tt", CultureInfo.InvariantCulture) ?? "your visit";
        var phone = appointment.PatientPhone;
        if (string.IsNullOrWhiteSpace(phone) && appointment.PatientId.HasValue)
        {
            phone = await _db.Patients.AsNoTracking()
                .Where(p => p.Id == appointment.PatientId.Value)
                .Select(p => p.Phone)
                .FirstOrDefaultAsync(cancellationToken);
        }

        var row = new AppointmentFeedbackRequest
        {
            AppointmentId = appointment.Id,
            PatientId = appointment.PatientId,
            DoctorId = appointment.DoctorId,
            ScheduledAtUtc = appointment.CreatedAt.ToUniversalTime().AddHours(hours),
            Channel = AppointmentFeedbackChannels.Pending,
            Stage = AppointmentFeedbackStages.Pending,
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow
        };

        var e164 = ElevenLabsTwilioCallingService.ToE164(phone);
        row.PhoneE164 = e164;

        var ratingQuestion =
            $"How was your visit with {doctorName} on {bookedTime}? Reply 1-5 (5 is best), or DID NOT ATTEND.";
        var (smsOk, smsSid, smsError) = TrySendSms(e164, ratingQuestion);

        if (smsOk)
        {
            row.Channel = AppointmentFeedbackChannels.Sms;
            row.Stage = AppointmentFeedbackStages.RatingSent;
            row.SentAtUtc = DateTime.UtcNow;
            row.LastOutboundMessageSid = smsSid;
            row.UpdatedAtUtc = DateTime.UtcNow;
            _db.AppointmentFeedbackRequests.Add(row);
            await _db.SaveChangesAsync(cancellationToken);
            return true;
        }

        row.Channel = AppointmentFeedbackChannels.Pending;
        row.Stage = AppointmentFeedbackStages.Failed;
        row.LastError = FirstNonEmpty(smsError, "No usable phone for SMS.");
        row.UpdatedAtUtc = DateTime.UtcNow;
        _db.AppointmentFeedbackRequests.Add(row);
        await _db.SaveChangesAsync(cancellationToken);
        return false;
    }

    private async Task HandleRatingReplyAsync(
        AppointmentFeedbackRequest feedback,
        SmsReplyExtraction extracted,
        CancellationToken cancellationToken)
    {
        if (extracted.IsNoShow)
        {
            await ApplyNoShowAsync(feedback.DoctorId, feedback.AppointmentId, cancellationToken);
            feedback.Stage = AppointmentFeedbackStages.NoShow;
            feedback.UpdatedAtUtc = DateTime.UtcNow;
            await _db.SaveChangesAsync(cancellationToken);
            TrySendSms(ConversationPhone(feedback), "Thank you. We recorded that you did not attend.");
            return;
        }

        if (extracted.Rating is null)
        {
            TrySendSms(ConversationPhone(feedback), _extractor.ClarifyPrompt(feedback.Stage));
            return;
        }

        await ApplyShowedAsync(feedback.DoctorId, feedback.AppointmentId, cancellationToken);
        feedback.Rating = extracted.Rating;
        feedback.Stage = AppointmentFeedbackStages.WaitingSent;
        feedback.UpdatedAtUtc = DateTime.UtcNow;

        var (ok, sid, err) = TrySendSms(
            ConversationPhone(feedback),
            "How was the waiting time? Reply Excellent, Good, Average, or Bad.");
        if (!ok)
            feedback.LastError = err;
        else
            feedback.LastOutboundMessageSid = sid;

        await _db.SaveChangesAsync(cancellationToken);
    }

    private async Task HandleWaitingReplyAsync(
        AppointmentFeedbackRequest feedback,
        SmsReplyExtraction extracted,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(extracted.WaitingTime))
        {
            TrySendSms(ConversationPhone(feedback), _extractor.ClarifyPrompt(feedback.Stage));
            return;
        }

        feedback.WaitingTime = extracted.WaitingTime;
        feedback.Stage = AppointmentFeedbackStages.RecommendSent;
        feedback.UpdatedAtUtc = DateTime.UtcNow;

        var (ok, sid, err) = TrySendSms(
            ConversationPhone(feedback),
            "How would you recommend this doctor? Reply Highly Recommended, Neutral, or Not Recommended.");
        if (!ok)
            feedback.LastError = err;
        else
            feedback.LastOutboundMessageSid = sid;

        await _db.SaveChangesAsync(cancellationToken);
    }

    private async Task HandleRecommendReplyAsync(
        AppointmentFeedbackRequest feedback,
        SmsReplyExtraction extracted,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(extracted.Recommendation))
        {
            TrySendSms(ConversationPhone(feedback), _extractor.ClarifyPrompt(feedback.Stage));
            return;
        }

        feedback.Recommendation = extracted.Recommendation;
        feedback.Stage = AppointmentFeedbackStages.ReviewTextAwaiting;
        feedback.UpdatedAtUtc = DateTime.UtcNow;

        var (ok, sid, err) = TrySendSms(
            ConversationPhone(feedback),
            "Please share your review in a few words.");
        if (!ok)
            feedback.LastError = err;
        else
            feedback.LastOutboundMessageSid = sid;

        await _db.SaveChangesAsync(cancellationToken);
    }

    private async Task HandleReviewTextReplyAsync(
        AppointmentFeedbackRequest feedback,
        SmsReplyExtraction extracted,
        CancellationToken cancellationToken)
    {
        var reviewText = extracted.ExperienceText?.Trim();
        if (string.IsNullOrWhiteSpace(reviewText))
        {
            TrySendSms(ConversationPhone(feedback), _extractor.ClarifyPrompt(feedback.Stage));
            return;
        }

        if (!feedback.Rating.HasValue
            || string.IsNullOrWhiteSpace(feedback.WaitingTime)
            || string.IsNullOrWhiteSpace(feedback.Recommendation))
        {
            TrySendSms(ConversationPhone(feedback), _extractor.ClarifyPrompt(feedback.Stage));
            return;
        }

        if (!feedback.PatientId.HasValue)
        {
            feedback.LastError = "Patient account required to save review.";
            feedback.UpdatedAtUtc = DateTime.UtcNow;
            await _db.SaveChangesAsync(cancellationToken);
            TrySendSms(
                ConversationPhone(feedback),
                "Thanks! Please finish your review at https://www.nuvidoc.com/Account/Appointments");
            return;
        }

        var (success, error) = await _reviews.AddReviewForPatientAsync(
            feedback.PatientId.Value,
            feedback.DoctorId,
            feedback.Rating.Value,
            reviewText,
            feedback.WaitingTime,
            feedback.Recommendation,
            appointmentId: feedback.AppointmentId,
            cancellationToken: cancellationToken);

        if (!success)
        {
            feedback.LastError = error;
            feedback.UpdatedAtUtc = DateTime.UtcNow;
            await _db.SaveChangesAsync(cancellationToken);
            TrySendSms(ConversationPhone(feedback), error ?? "We could not save your review. Please try again later.");
            return;
        }

        feedback.ReviewText = reviewText;
        feedback.Stage = AppointmentFeedbackStages.Completed;
        feedback.UpdatedAtUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);
        TrySendSms(ConversationPhone(feedback), "Thank you for your feedback!");
    }

    private async Task<(bool Success, string? Error)> ApplyNoShowAsync(
        int doctorId,
        int appointmentId,
        CancellationToken cancellationToken)
    {
        var (success, error, _, _, _) = await _appointments.UpdateStatusAsync(
            doctorId,
            appointmentId,
            AppointmentStatuses.PatientNoShow,
            cancellationToken);
        return (success, error);
    }

    private async Task<(bool Success, string? Error)> ApplyShowedAsync(
        int doctorId,
        int appointmentId,
        CancellationToken cancellationToken)
    {
        var appointment = await _db.Appointments.AsNoTracking()
            .FirstOrDefaultAsync(a => a.Id == appointmentId && a.DoctorId == doctorId, cancellationToken);
        if (appointment == null)
            return (false, "Appointment not found.");

        if (string.Equals(
                AppointmentStatuses.Normalize(appointment.Status),
                AppointmentStatuses.Completed,
                StringComparison.OrdinalIgnoreCase))
            return (true, null);

        if (AppointmentStatuses.IsPatientNoShow(appointment.Status)
            || AppointmentStatuses.IsCanceled(appointment.Status))
            return (false, "This appointment can no longer be marked as attended.");

        var (success, error, _, _, _) = await _appointments.UpdateStatusAsync(
            doctorId,
            appointmentId,
            AppointmentStatuses.Completed,
            cancellationToken);
        return (success, error);
    }

    private async Task MarkFeedbackCompletedFromWebAsync(
        int appointmentId,
        int rating,
        string? waitingTime,
        string? recommendation,
        string reviewText,
        CancellationToken cancellationToken)
    {
        var row = await _db.AppointmentFeedbackRequests
            .FirstOrDefaultAsync(f => f.AppointmentId == appointmentId, cancellationToken);
        if (row == null)
            return;

        row.Rating = rating;
        row.WaitingTime = waitingTime;
        row.Recommendation = recommendation;
        row.ReviewText = reviewText;
        row.Stage = AppointmentFeedbackStages.Completed;
        row.UpdatedAtUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);
    }

    private async Task<bool> PatientOwnsAppointmentAsync(
        int patientId,
        Appointment appointment,
        CancellationToken cancellationToken)
    {
        if (appointment.PatientId == patientId)
            return true;

        var email = await _db.Patients.AsNoTracking()
            .Where(p => p.Id == patientId)
            .Select(p => p.Username)
            .FirstOrDefaultAsync(cancellationToken);
        return appointment.PatientId == null
               && !string.IsNullOrWhiteSpace(email)
               && string.Equals(appointment.PatientEmail, email, StringComparison.OrdinalIgnoreCase);
    }

    private (bool Ok, string? Sid, string? Error) TrySendSms(string? phone, string body)
    {
        try
        {
            var toE164 = TwilioOutboundRouting.ResolveToNumber(_twilio, phone);
            if (string.IsNullOrWhiteSpace(toE164))
                return (false, null, "Missing SMS address.");
            if (string.IsNullOrWhiteSpace(_twilio.AccountSid) || string.IsNullOrWhiteSpace(_twilio.AuthToken))
                return (false, null, "Twilio credentials are not configured.");

            var from = FirstNonEmpty(_twilio.SmsFromNumber, _twilio.FromNumber);
            if (string.IsNullOrWhiteSpace(from))
                return (false, null, "Missing SMS from number.");

            TwilioClient.Init(_twilio.AccountSid.Trim(), _twilio.AuthToken.Trim());
            var msg = MessageResource.Create(new CreateMessageOptions(new PhoneNumber(toE164))
            {
                From = new PhoneNumber(from.Trim()),
                Body = body
            });
            return (true, msg.Sid, null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Feedback SMS send failed: {Error}", ex.Message);
            return (false, null, ex.Message);
        }
    }

    private static string? ConversationPhone(AppointmentFeedbackRequest row) =>
        FirstNonEmpty(row.PhoneE164, ElevenLabsTwilioCallingService.ToE164(row.WhatsAppTo));

    private static string QuestionHint(string stage) => stage switch
    {
        AppointmentFeedbackStages.RatingSent => "Rate the visit from 1 to 5, or DID NOT ATTEND.",
        AppointmentFeedbackStages.WaitingSent => "How was the waiting time? Excellent, Good, Average, or Bad.",
        AppointmentFeedbackStages.RecommendSent => "Recommend this doctor? Highly Recommended, Neutral, or Not Recommended.",
        AppointmentFeedbackStages.ReviewTextAwaiting => "Share a short review in your own words.",
        _ => "Reply to the previous question."
    };

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
}
