using System.Globalization;
using System.Text.Json;
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
    Task HandleInboundWhatsAppAsync(
        string fromWhatsApp,
        string? body,
        string? buttonPayload,
        string? listId,
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
    private readonly IBrandingService _branding;
    private readonly TwilioOptions _twilio;
    private readonly EmailOptions _emailOptions;
    private readonly IDocoveeLogger _logger;

    public AppointmentFeedbackService(
        DocoveeDbContext db,
        IAppSettingsService appSettings,
        IAppointmentService appointments,
        IDoctorReviewService reviews,
        IBrandingService branding,
        IOptions<TwilioOptions> twilio,
        IOptions<EmailOptions> emailOptions,
        IDocoveeLogger logger)
    {
        _db = db;
        _appSettings = appSettings;
        _appointments = appointments;
        _reviews = reviews;
        _branding = branding;
        _twilio = twilio.Value;
        _emailOptions = emailOptions.Value;
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

    public async Task HandleInboundWhatsAppAsync(
        string fromWhatsApp,
        string? body,
        string? buttonPayload,
        string? listId,
        CancellationToken cancellationToken = default)
    {
        var toKey = NormalizeWhatsAppAddress(fromWhatsApp);
        if (string.IsNullOrWhiteSpace(toKey))
            return;

        var feedback = await _db.AppointmentFeedbackRequests
            .Where(f => f.WhatsAppTo == toKey
                && f.Stage != AppointmentFeedbackStages.Completed
                && f.Stage != AppointmentFeedbackStages.NoShow
                && f.Stage != AppointmentFeedbackStages.Failed
                && f.Stage != AppointmentFeedbackStages.Pending)
            .OrderByDescending(f => f.UpdatedAtUtc)
            .FirstOrDefaultAsync(cancellationToken);

        if (feedback == null)
        {
            _logger.LogInformation("No open feedback survey for WhatsApp {From}", toKey);
            return;
        }

        var selection = FirstNonEmpty(listId, buttonPayload, body)?.Trim();
        if (string.IsNullOrWhiteSpace(selection))
            return;

        switch (feedback.Stage)
        {
            case AppointmentFeedbackStages.RatingSent:
                await HandleRatingReplyAsync(feedback, selection, cancellationToken);
                break;
            case AppointmentFeedbackStages.WaitingSent:
                await HandleWaitingReplyAsync(feedback, selection, cancellationToken);
                break;
            case AppointmentFeedbackStages.RecommendSent:
                await HandleRecommendReplyAsync(feedback, selection, cancellationToken);
                break;
            case AppointmentFeedbackStages.ReviewTextAwaiting:
                await HandleReviewTextReplyAsync(feedback, body ?? selection, cancellationToken);
                break;
        }
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
        var bookedTime = appointment.StartsAt.ToString("MMM d, yyyy 'at' h:mm tt", CultureInfo.InvariantCulture);
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

        var waTo = NormalizeWhatsAppAddress(ElevenLabsTwilioCallingService.ToE164(phone));
        row.WhatsAppTo = waTo;

        var (waOk, waSid, waError) = TrySendWhatsAppContent(
            phone,
            _twilio.WhatsAppFeedbackRatingContentSid,
            new Dictionary<string, string>
            {
                ["1"] = doctorName,
                ["2"] = bookedTime
            });

        if (waOk)
        {
            row.Channel = AppointmentFeedbackChannels.WhatsApp;
            row.Stage = AppointmentFeedbackStages.RatingSent;
            row.SentAtUtc = DateTime.UtcNow;
            row.LastOutboundMessageSid = waSid;
            row.UpdatedAtUtc = DateTime.UtcNow;
            _db.AppointmentFeedbackRequests.Add(row);
            await _db.SaveChangesAsync(cancellationToken);
            return true;
        }

        var smsOk = TrySendSmsFallback(phone, doctorName, bookedTime);
        if (smsOk)
        {
            row.Channel = AppointmentFeedbackChannels.SmsFallback;
            row.Stage = AppointmentFeedbackStages.Failed;
            row.SentAtUtc = DateTime.UtcNow;
            row.LastError = string.IsNullOrWhiteSpace(waError)
                ? "WhatsApp unavailable; SMS fallback sent."
                : $"WhatsApp failed ({waError}); SMS fallback sent.";
            row.UpdatedAtUtc = DateTime.UtcNow;
            _db.AppointmentFeedbackRequests.Add(row);
            await _db.SaveChangesAsync(cancellationToken);
            return true;
        }

        row.Channel = AppointmentFeedbackChannels.Pending;
        row.Stage = AppointmentFeedbackStages.Failed;
        row.LastError = FirstNonEmpty(waError, "No usable phone for WhatsApp or SMS.");
        row.UpdatedAtUtc = DateTime.UtcNow;
        _db.AppointmentFeedbackRequests.Add(row);
        await _db.SaveChangesAsync(cancellationToken);
        return false;
    }

    private async Task HandleRatingReplyAsync(
        AppointmentFeedbackRequest feedback,
        string selection,
        CancellationToken cancellationToken)
    {
        if (AppointmentFeedbackItemIds.IsNoShow(selection))
        {
            await ApplyNoShowAsync(feedback.DoctorId, feedback.AppointmentId, cancellationToken);
            feedback.Stage = AppointmentFeedbackStages.NoShow;
            feedback.UpdatedAtUtc = DateTime.UtcNow;
            await _db.SaveChangesAsync(cancellationToken);
            TrySendWhatsAppText(feedback.WhatsAppTo, "Thank you. We recorded that you did not attend.");
            return;
        }

        var rating = AppointmentFeedbackItemIds.ParseStarRating(selection);
        if (rating is null)
        {
            // Allow typed fallbacks like "5" or "5 stars"
            if (int.TryParse(selection.Trim().Split(' ', '-')[0], out var n) && n is >= 1 and <= 5)
                rating = n;
            else if (selection.Contains("did not", StringComparison.OrdinalIgnoreCase)
                     || selection.Contains("noshow", StringComparison.OrdinalIgnoreCase)
                     || selection.Contains("no show", StringComparison.OrdinalIgnoreCase))
            {
                await HandleRatingReplyAsync(feedback, AppointmentFeedbackItemIds.NoShow, cancellationToken);
                return;
            }
            else
                return;
        }

        await ApplyShowedAsync(feedback.DoctorId, feedback.AppointmentId, cancellationToken);
        feedback.Rating = rating;
        feedback.Stage = AppointmentFeedbackStages.WaitingSent;
        feedback.UpdatedAtUtc = DateTime.UtcNow;

        var (ok, sid, err) = TrySendWhatsAppContent(feedback.WhatsAppTo, _twilio.WhatsAppFeedbackWaitingContentSid, null);
        if (!ok)
        {
            feedback.LastError = err;
            TrySendWhatsAppText(feedback.WhatsAppTo, "How was the waiting time? Reply Excellent, Good, Average, or Bad.");
        }
        else
            feedback.LastOutboundMessageSid = sid;

        await _db.SaveChangesAsync(cancellationToken);
    }

    private async Task HandleWaitingReplyAsync(
        AppointmentFeedbackRequest feedback,
        string selection,
        CancellationToken cancellationToken)
    {
        var waiting = AppointmentFeedbackItemIds.ParseWaitingTime(selection)
                      ?? PatientReviewOptions.NormalizeWaitingTime(selection);
        if (waiting is null)
            return;

        feedback.WaitingTime = waiting;
        feedback.Stage = AppointmentFeedbackStages.RecommendSent;
        feedback.UpdatedAtUtc = DateTime.UtcNow;

        var (ok, sid, err) = TrySendWhatsAppContent(feedback.WhatsAppTo, _twilio.WhatsAppFeedbackRecommendContentSid, null);
        if (!ok)
        {
            feedback.LastError = err;
            TrySendWhatsAppText(
                feedback.WhatsAppTo,
                "How would you recommend this doctor? Reply Highly Recommended, Neutral, or Not Recommended.");
        }
        else
            feedback.LastOutboundMessageSid = sid;

        await _db.SaveChangesAsync(cancellationToken);
    }

    private async Task HandleRecommendReplyAsync(
        AppointmentFeedbackRequest feedback,
        string selection,
        CancellationToken cancellationToken)
    {
        var recommendation = AppointmentFeedbackItemIds.ParseRecommendation(selection)
                             ?? PatientReviewOptions.NormalizeRecommendation(selection);
        if (recommendation is null)
            return;

        feedback.Recommendation = recommendation;
        feedback.Stage = AppointmentFeedbackStages.ReviewTextAwaiting;
        feedback.UpdatedAtUtc = DateTime.UtcNow;

        var (ok, sid, err) = TrySendWhatsAppContent(feedback.WhatsAppTo, _twilio.WhatsAppFeedbackCommentContentSid, null);
        if (!ok)
        {
            feedback.LastError = err;
            TrySendWhatsAppText(feedback.WhatsAppTo, "Please share your review.");
        }
        else
            feedback.LastOutboundMessageSid = sid;

        await _db.SaveChangesAsync(cancellationToken);
    }

    private async Task HandleReviewTextReplyAsync(
        AppointmentFeedbackRequest feedback,
        string reviewText,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(reviewText))
            return;

        if (!feedback.Rating.HasValue
            || string.IsNullOrWhiteSpace(feedback.WaitingTime)
            || string.IsNullOrWhiteSpace(feedback.Recommendation))
            return;

        if (!feedback.PatientId.HasValue)
        {
            feedback.LastError = "Patient account required to save review.";
            feedback.UpdatedAtUtc = DateTime.UtcNow;
            await _db.SaveChangesAsync(cancellationToken);
            TrySendWhatsAppText(
                feedback.WhatsAppTo,
                "Thanks! Please finish your review at https://www.nuvidoc.com/Account/Appointments");
            return;
        }

        var (success, error) = await _reviews.AddReviewForPatientAsync(
            feedback.PatientId.Value,
            feedback.DoctorId,
            feedback.Rating.Value,
            reviewText.Trim(),
            feedback.WaitingTime,
            feedback.Recommendation,
            appointmentId: feedback.AppointmentId,
            cancellationToken: cancellationToken);

        if (!success)
        {
            feedback.LastError = error;
            feedback.UpdatedAtUtc = DateTime.UtcNow;
            await _db.SaveChangesAsync(cancellationToken);
            TrySendWhatsAppText(feedback.WhatsAppTo, error ?? "We could not save your review. Please try again later.");
            return;
        }

        feedback.ReviewText = reviewText.Trim();
        feedback.Stage = AppointmentFeedbackStages.Completed;
        feedback.UpdatedAtUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);
        TrySendWhatsAppText(feedback.WhatsAppTo, "Thank you for your feedback!");
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

    private (bool Ok, string? Sid, string? Error) TrySendWhatsAppContent(
        string? phoneOrWhatsApp,
        string? contentSid,
        Dictionary<string, string>? variables)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(contentSid))
                return (false, null, "Feedback Content SID is not configured.");
            if (string.IsNullOrWhiteSpace(_twilio.AccountSid) || string.IsNullOrWhiteSpace(_twilio.AuthToken))
                return (false, null, "Twilio credentials are not configured.");

            var from = NormalizeWhatsAppAddress(_twilio.WhatsAppFromNumber);
            var to = NormalizeWhatsAppAddress(
                phoneOrWhatsApp?.StartsWith("whatsapp:", StringComparison.OrdinalIgnoreCase) == true
                    ? phoneOrWhatsApp
                    : ElevenLabsTwilioCallingService.ToE164(phoneOrWhatsApp));
            if (string.IsNullOrWhiteSpace(from) || string.IsNullOrWhiteSpace(to))
                return (false, null, "Invalid WhatsApp addresses.");

            TwilioClient.Init(_twilio.AccountSid.Trim(), _twilio.AuthToken.Trim());
            var options = new CreateMessageOptions(new PhoneNumber(to))
            {
                From = new PhoneNumber(from),
                ContentSid = contentSid.Trim()
            };
            if (variables is { Count: > 0 })
                options.ContentVariables = JsonSerializer.Serialize(variables);

            var msg = MessageResource.Create(options);
            return (true, msg.Sid, null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Feedback WhatsApp Content send failed: {Error}", ex.Message);
            return (false, null, ex.Message);
        }
    }

    private bool TrySendWhatsAppText(string? whatsAppTo, string body)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(whatsAppTo))
                return false;
            if (string.IsNullOrWhiteSpace(_twilio.AccountSid) || string.IsNullOrWhiteSpace(_twilio.AuthToken))
                return false;

            var from = NormalizeWhatsAppAddress(_twilio.WhatsAppFromNumber);
            var to = NormalizeWhatsAppAddress(whatsAppTo);
            if (string.IsNullOrWhiteSpace(from) || string.IsNullOrWhiteSpace(to))
                return false;

            TwilioClient.Init(_twilio.AccountSid.Trim(), _twilio.AuthToken.Trim());
            MessageResource.Create(new CreateMessageOptions(new PhoneNumber(to))
            {
                From = new PhoneNumber(from),
                Body = body
            });
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Feedback WhatsApp text send failed: {Error}", ex.Message);
            return false;
        }
    }

    private bool TrySendSmsFallback(string? phone, string doctorName, string bookedTime)
    {
        try
        {
            var toE164 = ElevenLabsTwilioCallingService.ToE164(phone);
            if (string.IsNullOrWhiteSpace(toE164))
                return false;
            if (string.IsNullOrWhiteSpace(_twilio.AccountSid) || string.IsNullOrWhiteSpace(_twilio.AuthToken))
                return false;

            var from = FirstNonEmpty(_twilio.SmsFromNumber, _twilio.FromNumber);
            if (string.IsNullOrWhiteSpace(from))
                return false;

            var baseUrl = FirstNonEmpty(_emailOptions.PublicBaseUrl, _twilio.PublicBaseUrl)?.TrimEnd('/')
                          ?? "https://www.nuvidoc.com";
            var link = $"{baseUrl}/Account/Appointments";
            var site = _branding.SiteName;
            var body =
                $"Please rate your experience with {doctorName} on {bookedTime}, or let us know if you did not attend: {link} — {site}";

            TwilioClient.Init(_twilio.AccountSid.Trim(), _twilio.AuthToken.Trim());
            MessageResource.Create(new CreateMessageOptions(new PhoneNumber(toE164))
            {
                From = new PhoneNumber(from.Trim()),
                Body = body
            });
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Feedback SMS fallback failed: {Error}", ex.Message);
            return false;
        }
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

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
}
