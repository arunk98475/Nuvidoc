using System.Globalization;
using System.Text.Json;
using Docovee.BLL.Configuration;
using Docovee.BLL.Services.PatientPush;
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

public interface IPatientReminderService
{
    Task<PatientReminderSettingsDto> GetAsync(int patientId, CancellationToken cancellationToken = default);
    Task<(bool Success, string? Error)> SaveAsync(
        int patientId,
        PatientReminderSettingsSaveRequest input,
        CancellationToken cancellationToken = default);
    Task<int> ProcessDueRemindersAsync(CancellationToken cancellationToken = default);
}

public sealed class PatientReminderService : IPatientReminderService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private static readonly TimeSpan LateGrace = TimeSpan.FromHours(12);

    private readonly DocoveeDbContext _db;
    private readonly IPatientPushDispatcher _push;
    private readonly TwilioOptions _twilio;
    private readonly IDocoveeLogger _logger;

    public PatientReminderService(
        DocoveeDbContext db,
        IPatientPushDispatcher push,
        IOptions<TwilioOptions> twilio,
        IDocoveeLogger logger)
    {
        _db = db;
        _push = push;
        _twilio = twilio.Value;
        _logger = logger;
    }

    public async Task<PatientReminderSettingsDto> GetAsync(int patientId, CancellationToken cancellationToken = default)
    {
        var patient = await _db.Patients.AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == patientId, cancellationToken);
        if (patient == null)
            return ApplyChannelFlags(new PatientReminderSettingsDto(), phoneVerified: false, hasEmail: false, emailVerified: false);

        var dto = Deserialize(patient.ReminderSettingsJson);
        return ApplyChannelFlags(dto, patient.PhoneVerified, HasEmailAddress(patient.Username), patient.EmailVerified);
    }

    public async Task<(bool Success, string? Error)> SaveAsync(
        int patientId,
        PatientReminderSettingsSaveRequest input,
        CancellationToken cancellationToken = default)
    {
        var patient = await _db.Patients.FirstOrDefaultAsync(p => p.Id == patientId, cancellationToken);
        if (patient == null)
            return (false, "Patient not found.");

        var dto = new PatientReminderSettingsDto
        {
            Enable7Days = input.Enable7Days,
            Time7Days = NormalizeTime(input.Time7Days),
            Enable3Days = input.Enable3Days,
            Time3Days = NormalizeTime(input.Time3Days),
            Enable1Day = input.Enable1Day,
            Time1Day = NormalizeTime(input.Time1Day),
            EnableSameDay = input.EnableSameDay,
            SameDayHoursBefore = Math.Clamp(input.SameDayHoursBefore, 1, 24),
            ShowNotification = input.ShowNotification,
            EnableEmail = false,
            EnableSms = input.EnableSms && patient.PhoneVerified
        };

        patient.ReminderSettingsJson = JsonSerializer.Serialize(dto, JsonOptions);
        await _db.SaveChangesAsync(cancellationToken);
        return (true, null);
    }

    public async Task<int> ProcessDueRemindersAsync(CancellationToken cancellationToken = default)
    {
        var now = ClinicTime.Now;
        var windowStart = now.Date.AddDays(-1);
        var windowEnd = now.Date.AddDays(8);

        var appointments = await _db.Appointments
            .Include(a => a.Doctor)
            .Include(a => a.Patient)
            .Where(a => a.PatientId != null
                        && a.StartsAt >= windowStart
                        && a.StartsAt <= windowEnd)
            .ToListAsync(cancellationToken);

        var sent = 0;
        foreach (var appt in appointments)
        {
            if (appt.Patient == null || appt.PatientId is not int patientId)
                continue;
            if (!AppointmentStatuses.IsActive(appt.Status))
                continue;
            if (appt.StartsAt <= now)
                continue;

            var settings = Deserialize(appt.Patient.ReminderSettingsJson);
            settings = ApplyChannelFlags(settings, appt.Patient.PhoneVerified, HasEmailAddress(appt.Patient.Username), appt.Patient.EmailVerified);
            if (!settings.ShowNotification && !(settings.EnableSms && settings.PhoneVerified))
                continue;

            var createdPacific = ClinicTime.FromUtc(appt.CreatedAt);

            foreach (var (kind, dueAt, enabled) in EnumerateDueTimes(appt.StartsAt, settings))
            {
                if (!enabled)
                    continue;
                if (dueAt > now)
                    continue;
                if (dueAt < createdPacific)
                    continue;
                if (now - dueAt > LateGrace)
                    continue;

                var already = await _db.PatientAppointmentReminderSends
                    .AnyAsync(s => s.AppointmentId == appt.Id && s.ReminderKind == kind, cancellationToken);
                if (already)
                    continue;

                if (await SendReminderAsync(appt, patientId, settings, cancellationToken))
                {
                    _db.PatientAppointmentReminderSends.Add(new PatientAppointmentReminderSend
                    {
                        PatientId = patientId,
                        AppointmentId = appt.Id,
                        ReminderKind = kind,
                        SentAtUtc = DateTime.UtcNow
                    });
                    await _db.SaveChangesAsync(cancellationToken);
                    sent++;
                }
            }
        }

        return sent;
    }

    private async Task<bool> SendReminderAsync(
        Appointment appt,
        int patientId,
        PatientReminderSettingsDto settings,
        CancellationToken cancellationToken)
    {
        var practice = string.IsNullOrWhiteSpace(appt.Doctor?.PracticeName)
            ? (appt.Doctor?.Name ?? "your practice")
            : appt.Doctor.PracticeName;
        var date = appt.StartsAt.ToString("MMMM d, yyyy", CultureInfo.InvariantCulture);
        var time = appt.StartsAt.ToString("h:mm tt", CultureInfo.InvariantCulture);
        var body = $"You have appointment in {practice} on {date} at {time}";
        var title = "Appointment reminder";

        var any = false;
        if (settings.ShowNotification)
        {
            var row = new PatientNotification
            {
                PatientId = patientId,
                Type = PatientNotificationTypes.AppointmentReminder,
                Title = title,
                Body = body,
                AppointmentId = appt.Id,
                DoctorId = appt.DoctorId,
                IsRead = false,
                CreatedAt = DateTime.UtcNow
            };
            _db.PatientNotifications.Add(row);
            await _db.SaveChangesAsync(cancellationToken);

            await _push.DispatchAsync(new PatientPushMessage
            {
                Type = PatientNotificationTypes.AppointmentReminder,
                PatientId = patientId,
                Status = PatientNotificationTypes.AppointmentReminder,
                Title = title,
                Body = body,
                DoctorId = appt.DoctorId,
                DoctorName = appt.Doctor?.Name,
                AppointmentId = appt.Id,
                StartsAt = appt.StartsAt,
                EndsAt = appt.StartsAt.AddHours(1),
                SlotLabel = VoiceCallBookingService.FormatPstSlot(appt.StartsAt, appt.StartsAt.AddHours(1)),
                NotificationId = row.Id
            }, cancellationToken);
            any = true;
        }

        if (settings.EnableSms && settings.PhoneVerified)
        {
            try
            {
                SendSms(appt.Patient?.Phone, body);
                any = true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    "Appointment reminder SMS failed for patient {PatientId} appointment {AppointmentId}: {Error}",
                    patientId, appt.Id, ex.Message);
            }
        }

        // Email is stored as a preference only until the client provides a mailer.
        return any;
    }

    private void SendSms(string? phone, string body)
    {
        var toE164 = ElevenLabsTwilioCallingService.ToE164(phone);
        if (string.IsNullOrWhiteSpace(toE164))
            return;
        if (string.IsNullOrWhiteSpace(_twilio.AccountSid) || string.IsNullOrWhiteSpace(_twilio.AuthToken))
            return;

        var from = FirstNonEmpty(_twilio.SmsFromNumber, _twilio.FromNumber);
        if (string.IsNullOrWhiteSpace(from))
            return;

        TwilioClient.Init(_twilio.AccountSid.Trim(), _twilio.AuthToken.Trim());
        MessageResource.Create(new CreateMessageOptions(new PhoneNumber(toE164))
        {
            From = new PhoneNumber(from.Trim()),
            Body = body
        });
    }

    private static IEnumerable<(string Kind, DateTime DueAt, bool Enabled)> EnumerateDueTimes(
        DateTime startsAt,
        PatientReminderSettingsDto settings)
    {
        yield return (AppointmentReminderKinds.Days7, CombineDateAndTime(startsAt.Date.AddDays(-7), settings.Time7Days), settings.Enable7Days);
        yield return (AppointmentReminderKinds.Days3, CombineDateAndTime(startsAt.Date.AddDays(-3), settings.Time3Days), settings.Enable3Days);
        yield return (AppointmentReminderKinds.Days1, CombineDateAndTime(startsAt.Date.AddDays(-1), settings.Time1Day), settings.Enable1Day);
        yield return (AppointmentReminderKinds.SameDay, startsAt.AddHours(-Math.Clamp(settings.SameDayHoursBefore, 1, 24)), settings.EnableSameDay);
    }

    private static DateTime CombineDateAndTime(DateTime date, string hhmm)
    {
        var tod = ParseTime(hhmm);
        return date.Date.Add(tod);
    }

    private static TimeSpan ParseTime(string? value)
    {
        if (TimeSpan.TryParse(NormalizeTime(value), CultureInfo.InvariantCulture, out var ts))
            return ts;
        return new TimeSpan(9, 0, 0);
    }

    private static string NormalizeTime(string? value)
    {
        if (TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out var ts))
            return ts.ToString(@"hh\:mm", CultureInfo.InvariantCulture);
        return "09:00";
    }

    private static PatientReminderSettingsDto Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return new PatientReminderSettingsDto();
        try
        {
            return JsonSerializer.Deserialize<PatientReminderSettingsDto>(json, JsonOptions)
                   ?? new PatientReminderSettingsDto();
        }
        catch
        {
            return new PatientReminderSettingsDto();
        }
    }

    private static PatientReminderSettingsDto ApplyChannelFlags(
        PatientReminderSettingsDto dto,
        bool phoneVerified,
        bool hasEmail,
        bool emailVerified)
    {
        dto.PhoneVerified = phoneVerified;
        dto.EmailVerified = emailVerified;
        dto.EmailDeliveryAvailable = false;
        dto.EnableEmail = false;
        if (!phoneVerified)
            dto.EnableSms = false;

        dto.PhoneNote = phoneVerified
            ? "Receiving time will depend on network connectivity."
            : "Phone is not verified. Verify your phone in Login and security to enable SMS reminders. Receiving time will depend on network connectivity.";
        dto.EmailNote = !hasEmail
            ? "Add and verify an email in Login and security. Email delivery will be added later. Receiving time will depend on network connectivity."
            : emailVerified
                ? "Email is verified. Reminder emails will send once the mailer is enabled for reminders. Receiving time will depend on network connectivity."
                : "Email is not verified. Verify your email in Login and security. Receiving time will depend on network connectivity.";
        return dto;
    }

    private static bool HasEmailAddress(string? username) =>
        !string.IsNullOrWhiteSpace(username) && username.Contains('@');

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim();
}
