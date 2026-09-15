using System.Text.Json;
using Docovee.BLL.Configuration;
using Docovee.DS;
using Docovee.DS.Entities;
using Docovee.DS.Models;
using Docovee.logging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Twilio;
using Twilio.Rest.Api.V2010.Account;
using Twilio.Types;

namespace Docovee.BLL.Services;

public interface IPatientNurtureService
{
    Task<int> ProcessDueNurtureRemindersAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Cultivation nurture for patients until they leave feedback (or admin stops nurturing).
/// Continues after lead handoff / optional doctor booking — WhatsApp/SMS/email check-ins
/// like "Did you book?", "Did you go?", "Have you received treatment?", "How was it?".
/// </summary>
public sealed class PatientNurtureService : IPatientNurtureService
{
    private enum NurturePhase
    {
        AskBooked,
        AskUpcoming,
        AskAttended,
        AskTreatment,
        AskExperience
    }

    private readonly DocoveeDbContext _db;
    private readonly IAppSettingsService _appSettings;
    private readonly IPatientWhatsAppNurtureService _whatsAppNurture;
    private readonly IEmailSender _email;
    private readonly IBrandingService _branding;
    private readonly TwilioOptions _twilio;
    private readonly EmailOptions _emailOptions;
    private readonly IDocoveeLogger _logger;

    public PatientNurtureService(
        DocoveeDbContext db,
        IAppSettingsService appSettings,
        IPatientWhatsAppNurtureService whatsAppNurture,
        IEmailSender email,
        IBrandingService branding,
        IOptions<TwilioOptions> twilio,
        IOptions<EmailOptions> emailOptions,
        IDocoveeLogger logger)
    {
        _db = db;
        _appSettings = appSettings;
        _whatsAppNurture = whatsAppNurture;
        _email = email;
        _branding = branding;
        _twilio = twilio.Value;
        _emailOptions = emailOptions.Value;
        _logger = logger;
    }

    public async Task<int> ProcessDueNurtureRemindersAsync(CancellationToken cancellationToken = default)
    {
        var settings = await _appSettings.GetPatientBookingReminderSettingsAsync(cancellationToken);
        if (!settings.Enabled)
            return 0;
        if (!settings.EnableSms && !settings.EnableWhatsApp && !settings.EnableEmail)
            return 0;

        var sent = 0;
        if (settings.EnableWhatsApp)
            sent += await _whatsAppNurture.ProcessDueFollowUpsAsync(cancellationToken);

        var intervalDays = Math.Clamp(settings.IntervalDays, 1, 90);
        var stopAfterMonths = Math.Clamp(settings.StopAfterMonths, 1, 24);
        var now = ClinicTime.Now;
        var oldestCreatedUtc = DateTime.UtcNow.AddMonths(-(stopAfterMonths + 1));

        // Continue nurturing after booking until feedback — stop only when admin opted out,
        // patient left feedback/review, soft-deleted, or past stop window.
        var patients = await _db.Patients
            .AsNoTracking()
            .Where(p => !p.IsDeleted)
            .Where(p => !p.NurtureStopped)
            .Where(p => p.CreatedAt >= oldestCreatedUtc)
            .Where(p => !_db.DoctorPatientReviews.Any(r => r.PatientId == p.Id))
            .Where(p => !_db.AppointmentFeedbackRequests.Any(f =>
                f.PatientId == p.Id
                && f.Stage == AppointmentFeedbackStages.Completed))
            .Select(p => new
            {
                p.Id,
                p.FullName,
                p.Username,
                p.Phone,
                p.PhoneVerified,
                p.CreatedAt
            })
            .ToListAsync(cancellationToken);

        if (patients.Count == 0)
            return 0;

        var patientIds = patients.Select(p => p.Id).ToList();
        var implantFlags = await LoadImplantFlagsAsync(patientIds, cancellationToken);
        var appointmentStates = await LoadAppointmentStatesAsync(patientIds, cancellationToken);
        var priorSends = await _db.PatientNurtureSends
            .AsNoTracking()
            .Where(s => patientIds.Contains(s.PatientId))
            .Select(s => new { s.PatientId, s.StepDay, s.Channel })
            .ToListAsync(cancellationToken);
        var sentKeys = priorSends
            .Select(s => (s.PatientId, s.StepDay, s.Channel))
            .ToHashSet();

        var siteName = string.IsNullOrWhiteSpace(_branding.SiteName) ? "NuviDoc" : _branding.SiteName;
        var baseUrl = FirstNonEmpty(_emailOptions.PublicBaseUrl, _twilio.PublicBaseUrl)?.TrimEnd('/') ?? "";

        foreach (var patient in patients)
        {
            var createdPacific = ClinicTime.FromUtc(patient.CreatedAt);
            var cutoffDate = createdPacific.Date.AddMonths(stopAfterMonths);
            if (now.Date > cutoffDate)
                continue;

            appointmentStates.TryGetValue(patient.Id, out var apptState);
            implantFlags.TryGetValue(patient.Id, out var isImplant);
            var firstName = FirstName(patient.FullName);

            for (var stepDay = intervalDays; ; stepDay += intervalDays)
            {
                var dueDate = createdPacific.Date.AddDays(stepDay);
                if (dueDate > cutoffDate)
                    break;

                var dueAt = dueDate.AddHours(9);
                if (dueAt > now)
                    break;

                if (!StepHasPendingSend(patient.Id, patient.PhoneVerified, patient.Username, stepDay, settings, sentKeys))
                    continue;

                var phase = ResolvePhase(apptState, now, stepDay, intervalDays);
                var (body, subject) = BuildCopy(firstName, siteName, baseUrl, isImplant, phase);

                if (settings.EnableSms
                    && patient.PhoneVerified
                    && !sentKeys.Contains((patient.Id, stepDay, PatientNurtureChannels.Sms)))
                {
                    if (TrySendSms(patient.Phone, body))
                    {
                        await RecordSendAsync(patient.Id, stepDay, PatientNurtureChannels.Sms, cancellationToken);
                        sentKeys.Add((patient.Id, stepDay, PatientNurtureChannels.Sms));
                        sent++;
                    }
                }

                if (settings.EnableWhatsApp
                    && patient.PhoneVerified
                    && !sentKeys.Contains((patient.Id, stepDay, PatientNurtureChannels.WhatsApp)))
                {
                    if (await _whatsAppNurture.TryStartConversationAsync(
                            patient.Id,
                            patient.Phone,
                            patient.FullName,
                            stepDay,
                            cancellationToken))
                    {
                        await RecordSendAsync(patient.Id, stepDay, PatientNurtureChannels.WhatsApp, cancellationToken);
                        sentKeys.Add((patient.Id, stepDay, PatientNurtureChannels.WhatsApp));
                        sent++;
                    }
                }

                if (settings.EnableEmail
                    && HasEmailAddress(patient.Username)
                    && !sentKeys.Contains((patient.Id, stepDay, PatientNurtureChannels.Email)))
                {
                    if (await TrySendEmailAsync(patient.Username, subject, body, cancellationToken))
                    {
                        await RecordSendAsync(patient.Id, stepDay, PatientNurtureChannels.Email, cancellationToken);
                        sentKeys.Add((patient.Id, stepDay, PatientNurtureChannels.Email));
                        sent++;
                    }
                }

                // One step per patient per cycle so catch-up does not blast multiple overdue reminders at once.
                break;
            }
        }

        return sent;
    }

    private async Task<Dictionary<int, AppointmentNurtureState>> LoadAppointmentStatesAsync(
        IReadOnlyList<int> patientIds,
        CancellationToken cancellationToken)
    {
        var rows = await _db.Appointments
            .AsNoTracking()
            .Where(a => a.PatientId != null
                        && patientIds.Contains(a.PatientId.Value)
                        && a.Source != AppointmentSources.PmsInbound)
            .Select(a => new { PatientId = a.PatientId!.Value, a.StartsAt, a.Status })
            .ToListAsync(cancellationToken);

        var today = ClinicTime.Now.Date;
        var result = new Dictionary<int, AppointmentNurtureState>();
        foreach (var group in rows.GroupBy(r => r.PatientId))
        {
            var active = group.Where(g => !AppointmentStatuses.IsCanceled(g.Status)).ToList();
            if (active.Count == 0)
                continue;

            var booked = active.Where(g => g.StartsAt != null).Select(g => g.StartsAt!.Value).ToList();
            DateTime? nextStart = booked
                .Where(s => s.Date >= today)
                .OrderBy(s => s)
                .Cast<DateTime?>()
                .FirstOrDefault();
            DateTime? latestStart = booked.Count == 0 ? null : booked.Max();

            result[group.Key] = new AppointmentNurtureState
            {
                HasLeadOrBooking = true,
                HasBookedSlot = booked.Count > 0,
                NextStartsAt = nextStart,
                LatestStartsAt = latestStart
            };
        }

        return result;
    }

    private static NurturePhase ResolvePhase(
        AppointmentNurtureState? state,
        DateTime now,
        int stepDay,
        int intervalDays)
    {
        if (state is null || !state.HasLeadOrBooking || !state.HasBookedSlot)
            return NurturePhase.AskBooked;

        if (state.NextStartsAt is DateTime upcoming && upcoming.Date > now.Date)
            return NurturePhase.AskUpcoming;

        // Visit day or past — rotate check-in questions until feedback.
        var cycle = Math.Max(1, stepDay / Math.Max(1, intervalDays));
        return (cycle % 3) switch
        {
            1 => NurturePhase.AskAttended,
            2 => NurturePhase.AskTreatment,
            _ => NurturePhase.AskExperience
        };
    }

    private async Task<Dictionary<int, bool>> LoadImplantFlagsAsync(
        IReadOnlyList<int> patientIds,
        CancellationToken cancellationToken)
    {
        var sessions = await _db.SearchSessions
            .AsNoTracking()
            .Where(s => s.PatientId != null && patientIds.Contains(s.PatientId.Value))
            .OrderByDescending(s => s.UpdatedAt)
            .Select(s => new { PatientId = s.PatientId!.Value, s.SearchContextJson, s.Specialty })
            .ToListAsync(cancellationToken);

        var result = new Dictionary<int, bool>();
        foreach (var session in sessions)
        {
            if (result.ContainsKey(session.PatientId))
                continue;

            var implant = false;
            if (!string.IsNullOrWhiteSpace(session.Specialty)
                && session.Specialty.Contains("implant", StringComparison.OrdinalIgnoreCase))
            {
                implant = true;
            }
            else if (!string.IsNullOrWhiteSpace(session.SearchContextJson))
            {
                try
                {
                    var ctx = JsonSerializer.Deserialize<SearchContextData>(
                        session.SearchContextJson,
                        SearchContextHelper.JsonOptions);
                    implant = ctx?.ImplantQualificationComplete == true
                              || ctx?.ImplantIntentQualified == true;
                }
                catch
                {
                    // ignore bad JSON
                }
            }

            result[session.PatientId] = implant;
        }

        return result;
    }

    private async Task RecordSendAsync(
        int patientId,
        int stepDay,
        string channel,
        CancellationToken cancellationToken)
    {
        _db.PatientNurtureSends.Add(new PatientNurtureSend
        {
            PatientId = patientId,
            StepDay = stepDay,
            Channel = channel,
            SentAtUtc = DateTime.UtcNow
        });
        await _db.SaveChangesAsync(cancellationToken);
    }

    private static (string Body, string Subject) BuildCopy(
        string firstName,
        string siteName,
        string baseUrl,
        bool implant,
        NurturePhase phase)
    {
        var hello = string.IsNullOrWhiteSpace(firstName) ? "Hi" : $"Hi {firstName}";
        var link = string.IsNullOrWhiteSpace(baseUrl) ? siteName : baseUrl;
        var appointmentsUrl = string.IsNullOrWhiteSpace(baseUrl)
            ? "your appointments"
            : $"{baseUrl.TrimEnd('/')}/Account/Appointments";

        return phase switch
        {
            NurturePhase.AskBooked => (
                implant
                    ? $"{hello}, did you book your dental implant consult yet? {siteName} can still help you compare options — {link}"
                    : $"{hello}, did you book with the office we connected you with? If not, reply here or open {link} and we'll help.",
                $"Did you book? — {siteName}"),

            NurturePhase.AskUpcoming => (
                $"{hello}, your visit is coming up. Reply if you need to reschedule, or if anything changed. {link}",
                $"Your upcoming visit — {siteName}"),

            NurturePhase.AskAttended => (
                $"{hello}, did you go to your appointment? Reply yes/no — we're here if you still need help. {link}",
                $"Did you go? — {siteName}"),

            NurturePhase.AskTreatment => (
                $"{hello}, have you received treatment yet? Let us know how things are going. {link}",
                $"Have you received treatment? — {siteName}"),

            _ => (
                $"{hello}, if you did get care — how was it? Reply here or leave feedback: {appointmentsUrl}",
                $"How was your visit? — {siteName}")
        };
    }

    private bool TrySendSms(string? phone, string body)
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
            _logger.LogWarning("Booking nurture SMS failed: {Error}", ex.Message);
            return false;
        }
    }

    private bool TrySendWhatsApp(string? phone, string firstName, string body)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(_twilio.WhatsAppNurtureContentSid))
                return false;
            if (string.IsNullOrWhiteSpace(_twilio.AccountSid) || string.IsNullOrWhiteSpace(_twilio.AuthToken))
                return false;

            var toE164 = ElevenLabsTwilioCallingService.ToE164(phone);
            if (string.IsNullOrWhiteSpace(toE164))
                return false;

            var from = NormalizeWhatsAppAddress(_twilio.WhatsAppFromNumber);
            var to = NormalizeWhatsAppAddress(toE164);
            if (string.IsNullOrWhiteSpace(from) || string.IsNullOrWhiteSpace(to))
                return false;

            var shortText = body.Length > 200 ? body[..197] + "..." : body;
            var variables = JsonSerializer.Serialize(new Dictionary<string, string>
            {
                ["1"] = string.IsNullOrWhiteSpace(firstName) ? "there" : firstName,
                ["2"] = shortText
            });

            TwilioClient.Init(_twilio.AccountSid.Trim(), _twilio.AuthToken.Trim());
            MessageResource.Create(new CreateMessageOptions(new PhoneNumber(to))
            {
                From = new PhoneNumber(from),
                ContentSid = _twilio.WhatsAppNurtureContentSid.Trim(),
                ContentVariables = variables
            });
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Booking nurture WhatsApp failed: {Error}", ex.Message);
            return false;
        }
    }

    private async Task<bool> TrySendEmailAsync(
        string toAddress,
        string subject,
        string body,
        CancellationToken cancellationToken)
    {
        if (!_email.IsConfigured)
            return false;

        try
        {
            var result = await _email.SendAsync(toAddress.Trim(), subject, body, htmlBody: null, cancellationToken);
            if (!result.Success)
            {
                _logger.LogWarning("Booking nurture email failed: {Message}", result.Message);
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Booking nurture email failed: {Error}", ex.Message);
            return false;
        }
    }

    private static string? NormalizeWhatsAppAddress(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var trimmed = value.Trim();
        if (trimmed.StartsWith("whatsapp:", StringComparison.OrdinalIgnoreCase))
            return trimmed;
        var e164 = ElevenLabsTwilioCallingService.ToE164(trimmed) ?? trimmed;
        return e164.StartsWith("whatsapp:", StringComparison.OrdinalIgnoreCase)
            ? e164
            : "whatsapp:" + e164;
    }

    private static bool StepHasPendingSend(
        int patientId,
        bool phoneVerified,
        string? username,
        int stepDay,
        PatientBookingReminderSettings settings,
        HashSet<(int PatientId, int StepDay, string Channel)> sentKeys)
    {
        if (settings.EnableSms
            && phoneVerified
            && !sentKeys.Contains((patientId, stepDay, PatientNurtureChannels.Sms)))
            return true;
        if (settings.EnableWhatsApp
            && phoneVerified
            && !sentKeys.Contains((patientId, stepDay, PatientNurtureChannels.WhatsApp)))
            return true;
        if (settings.EnableEmail
            && HasEmailAddress(username)
            && !sentKeys.Contains((patientId, stepDay, PatientNurtureChannels.Email)))
            return true;
        return false;
    }

    private static string FirstName(string? fullName)
    {
        if (string.IsNullOrWhiteSpace(fullName))
            return "";
        var part = fullName.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return part ?? "";
    }

    private static bool HasEmailAddress(string? username) =>
        !string.IsNullOrWhiteSpace(username) && username.Contains('@');

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim();

    private sealed class AppointmentNurtureState
    {
        public bool HasLeadOrBooking { get; init; }
        public bool HasBookedSlot { get; init; }
        public DateTime? NextStartsAt { get; init; }
        public DateTime? LatestStartsAt { get; init; }
    }
}
