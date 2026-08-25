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

public sealed class PatientNurtureService : IPatientNurtureService
{
    private static readonly TimeSpan LateGrace = TimeSpan.FromHours(24);

    private readonly DocoveeDbContext _db;
    private readonly IAppSettingsService _appSettings;
    private readonly IEmailSender _email;
    private readonly IBrandingService _branding;
    private readonly TwilioOptions _twilio;
    private readonly EmailOptions _emailOptions;
    private readonly IDocoveeLogger _logger;

    public PatientNurtureService(
        DocoveeDbContext db,
        IAppSettingsService appSettings,
        IEmailSender email,
        IBrandingService branding,
        IOptions<TwilioOptions> twilio,
        IOptions<EmailOptions> emailOptions,
        IDocoveeLogger logger)
    {
        _db = db;
        _appSettings = appSettings;
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

        var intervalDays = Math.Clamp(settings.IntervalDays, 1, 90);
        var stopAfterMonths = Math.Clamp(settings.StopAfterMonths, 1, 24);
        var now = ClinicTime.Now;
        var oldestCreatedUtc = DateTime.UtcNow.AddMonths(-(stopAfterMonths + 1));

        var patients = await _db.Patients
            .AsNoTracking()
            .Where(p => p.CreatedAt >= oldestCreatedUtc)
            .Where(p => !_db.Appointments.Any(a =>
                a.PatientId == p.Id
                && a.Source != AppointmentSources.PmsInbound))
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
        var sent = 0;

        foreach (var patient in patients)
        {
            var createdPacific = ClinicTime.FromUtc(patient.CreatedAt);
            var cutoffDate = createdPacific.Date.AddMonths(stopAfterMonths);
            if (now.Date > cutoffDate)
                continue;

            implantFlags.TryGetValue(patient.Id, out var isImplant);
            var firstName = FirstName(patient.FullName);
            var (body, subject) = BuildCopy(firstName, siteName, baseUrl, isImplant);

            for (var stepDay = intervalDays; ; stepDay += intervalDays)
            {
                var dueDate = createdPacific.Date.AddDays(stepDay);
                if (dueDate > cutoffDate)
                    break;

                var dueAt = dueDate.AddHours(9);
                if (dueAt > now)
                    break;
                if (now - dueAt > LateGrace)
                    continue;

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
                    if (TrySendWhatsApp(patient.Phone, firstName, body))
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
            }
        }

        return sent;
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
        bool implant)
    {
        var hello = string.IsNullOrWhiteSpace(firstName) ? "Hi" : $"Hi {firstName}";
        var link = string.IsNullOrWhiteSpace(baseUrl) ? siteName : baseUrl;
        string body;
        string subject;
        if (implant)
        {
            subject = $"Ready when you are — dental implant consult on {siteName}";
            body =
                $"{hello}, still thinking about dental implants? When you're ready, {siteName} can help you find a dentist and book a consult. {link} Reply STOP to opt out.";
        }
        else
        {
            subject = $"Ready to book your visit on {siteName}?";
            body =
                $"{hello}, you're signed up with {siteName} but haven't booked yet. When you're ready, we can help you find a dentist and schedule. {link} Reply STOP to opt out.";
        }

        return (body, subject);
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
}
