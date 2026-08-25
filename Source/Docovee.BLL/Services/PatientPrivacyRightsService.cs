using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Docovee.BLL.Audit;
using Docovee.BLL.Configuration;
using Docovee.DS;
using Docovee.DS.Entities;
using Docovee.logging;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Docovee.BLL.Services;

public sealed class PrivacyRightsResult
{
    public bool Success { get; init; }
    public string Message { get; init; } = string.Empty;
    public int? RequestId { get; init; }
    public string? RequestType { get; init; }
    public bool AccountDeleted { get; init; }
    public string? DownloadUrl { get; init; }
}

public interface IPatientPrivacyRightsService
{
    Task RecordHipaaAuthorizationAsync(
        int patientId,
        bool granted,
        string? eSignName = null,
        CancellationToken cancellationToken = default);

    Task<PrivacyRightsResult> StartRequestAsync(
        int patientId,
        string requestType,
        string phoneChannel,
        string? notes = null,
        CancellationToken cancellationToken = default);

    Task<PrivacyRightsResult> ConfirmRequestAsync(
        int patientId,
        string requestType,
        string code,
        string? publicBaseUrl = null,
        CancellationToken cancellationToken = default);

    Task<(byte[]? Bytes, string? FileName, string? Error)> GetExportByDownloadTokenAsync(
        string token,
        CancellationToken cancellationToken = default);

    Task<DataSubjectRequest?> GetOpenAwaitingVerificationAsync(
        int patientId,
        CancellationToken cancellationToken = default);
}

public sealed class PatientPrivacyRightsService : IPatientPrivacyRightsService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly DocoveeDbContext _db;
    private readonly IPhoneVerificationService _phoneVerification;
    private readonly IEmailSender _email;
    private readonly IAuditTrailService _audit;
    private readonly SiteOptions _site;
    private readonly EmailOptions _emailOptions;
    private readonly IDocoveeLogger _logger;
    private readonly PasswordHasher<Patient> _hasher = new();

    public PatientPrivacyRightsService(
        DocoveeDbContext db,
        IPhoneVerificationService phoneVerification,
        IEmailSender email,
        IAuditTrailService audit,
        IOptions<SiteOptions> site,
        IOptions<EmailOptions> emailOptions,
        IDocoveeLogger logger)
    {
        _db = db;
        _phoneVerification = phoneVerification;
        _email = email;
        _audit = audit;
        _site = site.Value;
        _emailOptions = emailOptions.Value;
        _logger = logger;
    }

    public async Task RecordHipaaAuthorizationAsync(
        int patientId,
        bool granted,
        string? eSignName = null,
        CancellationToken cancellationToken = default)
    {
        var ctx = _audit.GetCurrentContext();
        _db.HipaaAuthorizations.Add(new HipaaAuthorization
        {
            PatientId = patientId,
            FormVersion = HipaaAuthorizationFormVersions.Current,
            Granted = granted,
            OccurredAtUtc = DateTime.UtcNow,
            IpAddress = Truncate(ctx.IpAddress, 64),
            UserAgent = Truncate(ctx.UserAgent, 500),
            ESignName = Truncate(eSignName, 200)
        });
        await _db.SaveChangesAsync(cancellationToken);

        await _audit.LogAsync(_db, new AuditLogRequest
        {
            Action = granted ? AuditActions.Create : AuditActions.Update,
            EntityType = AuditEntityTypes.HipaaAuthorization,
            EntityId = patientId.ToString(),
            Summary = granted
                ? $"HIPAA authorization granted (form {HipaaAuthorizationFormVersions.Current})"
                : $"HIPAA authorization revoked (form {HipaaAuthorizationFormVersions.Current})"
        }, cancellationToken);
    }

    public async Task<PrivacyRightsResult> StartRequestAsync(
        int patientId,
        string requestType,
        string phoneChannel,
        string? notes = null,
        CancellationToken cancellationToken = default)
    {
        var type = NormalizeType(requestType);
        if (type == null)
            return Fail("Unknown privacy request type.");

        if (!PhoneVerificationChannels.IsKnown(phoneChannel))
            return Fail("Choose Verify via SMS or Verify via WhatsApp.");

        var patient = await _db.Patients.FirstOrDefaultAsync(p => p.Id == patientId, cancellationToken);
        if (patient == null)
            return Fail("Patient not found.");

        if (IsDeletedAccount(patient))
            return Fail("This account has already been deleted.");

        var open = await _db.DataSubjectRequests
            .Where(r => r.PatientId == patientId
                        && r.RequestType == type
                        && (r.Status == DataSubjectRequestStatuses.AwaitingVerification
                            || r.Status == DataSubjectRequestStatuses.Pending
                            || r.Status == DataSubjectRequestStatuses.InProgress))
            .OrderByDescending(r => r.RequestedAtUtc)
            .FirstOrDefaultAsync(cancellationToken);

        if (open is { Status: DataSubjectRequestStatuses.Pending or DataSubjectRequestStatuses.InProgress })
        {
            return new PrivacyRightsResult
            {
                Success = true,
                Message = "You already have an open request of this type. Our team is working on it.",
                RequestId = open.Id,
                RequestType = type
            };
        }

        var send = await _phoneVerification.SendCodeAsync(
            patientId,
            phoneChannel,
            cancellationToken,
            resetVerifiedStatus: false);
        if (!send.Success)
            return Fail(send.Message);

        var now = DateTime.UtcNow;
        DataSubjectRequest row;
        if (open is { Status: DataSubjectRequestStatuses.AwaitingVerification })
        {
            row = open;
            row.RequestedAtUtc = now;
            row.RequestNotes = string.IsNullOrWhiteSpace(notes) ? row.RequestNotes : notes.Trim();
            SetDueDates(row, type, now);
        }
        else
        {
            row = new DataSubjectRequest
            {
                PatientId = patientId,
                RequestType = type,
                Status = DataSubjectRequestStatuses.AwaitingVerification,
                RequestedAtUtc = now,
                RequestNotes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim()
            };
            SetDueDates(row, type, now);
            _db.DataSubjectRequests.Add(row);
        }

        await _db.SaveChangesAsync(cancellationToken);

        return new PrivacyRightsResult
        {
            Success = true,
            Message = send.Message + " Enter the PIN below to confirm your request.",
            RequestId = row.Id,
            RequestType = type
        };
    }

    public async Task<PrivacyRightsResult> ConfirmRequestAsync(
        int patientId,
        string requestType,
        string code,
        string? publicBaseUrl = null,
        CancellationToken cancellationToken = default)
    {
        var type = NormalizeType(requestType);
        if (type == null)
            return Fail("Unknown privacy request type.");

        var verify = await _phoneVerification.VerifyCodeAsync(patientId, code, cancellationToken);
        if (!verify.Success)
            return Fail(verify.Message);

        var row = await _db.DataSubjectRequests
            .Where(r => r.PatientId == patientId
                        && r.RequestType == type
                        && r.Status == DataSubjectRequestStatuses.AwaitingVerification)
            .OrderByDescending(r => r.RequestedAtUtc)
            .FirstOrDefaultAsync(cancellationToken);

        if (row == null)
            return Fail("No pending privacy request found. Start the request again to receive a new PIN.");

        row.VerifiedAtUtc = DateTime.UtcNow;

        return type switch
        {
            DataSubjectRequestTypes.Access => await CompleteAccessAsync(row, publicBaseUrl, cancellationToken),
            DataSubjectRequestTypes.Amend => await CompleteAmendAsync(row, cancellationToken),
            DataSubjectRequestTypes.Delete => await CompleteDeleteAsync(row, cancellationToken),
            _ => Fail("Unknown privacy request type.")
        };
    }

    public async Task<(byte[]? Bytes, string? FileName, string? Error)> GetExportByDownloadTokenAsync(
        string token,
        CancellationToken cancellationToken = default)
    {
        var hash = HashToken(token);
        if (string.IsNullOrEmpty(hash))
            return (null, null, "Invalid download link.");

        var row = await _db.DataSubjectRequests
            .FirstOrDefaultAsync(r => r.DownloadTokenHash == hash, cancellationToken);

        if (row == null
            || row.Status != DataSubjectRequestStatuses.Completed
            || string.IsNullOrEmpty(row.ExportPayloadJson))
            return (null, null, "That download link is invalid or has already expired.");

        if (row.DownloadExpiresAtUtc is null || row.DownloadExpiresAtUtc.Value < DateTime.UtcNow)
            return (null, null, "That download link has expired. Submit a new access request from Privacy settings.");

        await _audit.LogExportAsync(
            _db,
            AuditEntityTypes.DataExport,
            row.PatientId.ToString(),
            $"Access export downloaded (request {row.Id})",
            cancellationToken);

        var bytes = Encoding.UTF8.GetBytes(row.ExportPayloadJson);
        var fileName = $"nuvidoc-access-export-{row.Id}-{DateTime.UtcNow:yyyyMMdd}.json";
        return (bytes, fileName, null);
    }

    public Task<DataSubjectRequest?> GetOpenAwaitingVerificationAsync(
        int patientId,
        CancellationToken cancellationToken = default) =>
        _db.DataSubjectRequests.AsNoTracking()
            .Where(r => r.PatientId == patientId
                        && r.Status == DataSubjectRequestStatuses.AwaitingVerification)
            .OrderByDescending(r => r.RequestedAtUtc)
            .FirstOrDefaultAsync(cancellationToken);

    private async Task<PrivacyRightsResult> CompleteAccessAsync(
        DataSubjectRequest row,
        string? publicBaseUrl,
        CancellationToken cancellationToken)
    {
        var json = await BuildAccessExportJsonAsync(row.PatientId, cancellationToken);
        if (json == null)
            return Fail("Patient not found.");

        var rawToken = CreateToken();
        row.ExportPayloadJson = json;
        row.DownloadTokenHash = HashToken(rawToken);
        row.DownloadExpiresAtUtc = DateTime.UtcNow.AddDays(7);
        row.Status = DataSubjectRequestStatuses.Completed;
        row.CompletedAtUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);

        await _audit.LogExportAsync(
            _db,
            AuditEntityTypes.DataExport,
            row.PatientId.ToString(),
            $"Access export prepared (request {row.Id})",
            cancellationToken);

        var baseUrl = ResolveBaseUrl(publicBaseUrl);
        var downloadUrl = $"{baseUrl}/Account/DownloadAccessExport?token={Uri.EscapeDataString(rawToken)}";

        var patient = await _db.Patients.AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == row.PatientId, cancellationToken);
        var email = patient?.Username?.Trim() ?? "";
        if (_email.IsConfigured && email.Contains('@'))
        {
            var site = string.IsNullOrWhiteSpace(_site.Name) ? "NuviDoc" : _site.Name;
            var subject = $"Your {site} data download is ready";
            var text =
                $"Your verified data access request is ready.\n\n" +
                $"Download your information (link expires in 7 days):\n{downloadUrl}\n\n" +
                "If you did not request this, contact support@nuvidoc.com.\n";
            var html =
                "<p>Your verified data access request is ready.</p>" +
                $"<p><a href=\"{Escape(downloadUrl)}\">Download your information</a> (expires in 7 days).</p>" +
                "<p>If you did not request this, contact support@nuvidoc.com.</p>";
            var send = await _email.SendAsync(email, subject, text, html, cancellationToken);
            if (!send.Success)
                _logger.LogWarning("Access export email failed for patient {PatientId}: {Message}", row.PatientId, send.Message);
        }

        return new PrivacyRightsResult
        {
            Success = true,
            Message = "Identity verified. Your data export is ready. We emailed a secure download link when email is configured; you can also download it now.",
            RequestId = row.Id,
            RequestType = DataSubjectRequestTypes.Access,
            DownloadUrl = downloadUrl
        };
    }

    private async Task<PrivacyRightsResult> CompleteAmendAsync(
        DataSubjectRequest row,
        CancellationToken cancellationToken)
    {
        row.Status = DataSubjectRequestStatuses.Pending;
        row.AmendmentPayloadJson = JsonSerializer.Serialize(new
        {
            notes = row.RequestNotes,
            submittedAtUtc = DateTime.UtcNow
        });
        await _db.SaveChangesAsync(cancellationToken);

        await _audit.LogAsync(_db, new AuditLogRequest
        {
            Action = AuditActions.Create,
            EntityType = AuditEntityTypes.DataSubjectRequest,
            EntityId = row.Id.ToString(),
            Summary = "Amendment request verified and queued for review"
        }, cancellationToken);

        return new PrivacyRightsResult
        {
            Success = true,
            Message = "Identity verified. Your correction request was submitted. We will review it within 60 days and contact you at your account email. You can also update most fields under Personal information.",
            RequestId = row.Id,
            RequestType = DataSubjectRequestTypes.Amend
        };
    }

    private async Task<PrivacyRightsResult> CompleteDeleteAsync(
        DataSubjectRequest row,
        CancellationToken cancellationToken)
    {
        var patientId = row.PatientId;
        var patient = await _db.Patients
            .Include(p => p.InsuranceCoverages)
            .FirstOrDefaultAsync(p => p.Id == patientId, cancellationToken);
        if (patient == null)
            return Fail("Patient not found.");

        var hadPmsRefs = await (
            from r in _db.PmsExternalRefs.AsNoTracking()
            join a in _db.Appointments.AsNoTracking() on r.AppointmentId equals a.Id
            where a.PatientId == patientId
            select r.Id).AnyAsync(cancellationToken)
            || await _db.Appointments.AnyAsync(a => a.PatientId == patientId, cancellationToken);

        // Chat + search session PHI
        var sessionIds = await _db.SearchSessions
            .Where(s => s.PatientId == patientId)
            .Select(s => s.Id)
            .ToListAsync(cancellationToken);
        if (sessionIds.Count > 0)
        {
            await _db.ChatMessages
                .Where(m => sessionIds.Contains(m.SearchSessionId))
                .ExecuteDeleteAsync(cancellationToken);
            await _db.SearchSessions
                .Where(s => s.PatientId == patientId)
                .ExecuteDeleteAsync(cancellationToken);
        }

        await _db.PatientNotifications
            .Where(n => n.PatientId == patientId)
            .ExecuteDeleteAsync(cancellationToken);
        await _db.PatientAppointmentReminderSends
            .Where(s => s.PatientId == patientId)
            .ExecuteDeleteAsync(cancellationToken);
        await _db.PatientDoctorContactViews
            .Where(v => v.PatientId == patientId)
            .ExecuteDeleteAsync(cancellationToken);

        if (patient.InsuranceCoverages.Count > 0)
        {
            _db.PatientInsuranceCoverages.RemoveRange(patient.InsuranceCoverages);
        }

        // De-identify appointments (keep calendar slots for the practice; strip identifiers)
        var appointments = await _db.Appointments
            .Where(a => a.PatientId == patientId)
            .ToListAsync(cancellationToken);
        foreach (var a in appointments)
        {
            a.PatientId = null;
            a.PatientName = "Deleted Patient";
            a.PatientPhone = null;
            a.PatientEmail = null;
            a.PatientDateOfBirth = null;
            a.VisitReason = "[redacted]";
            a.UpdatedAt = DateTime.UtcNow;
        }

        // Soft-de-identify patient row (keep id for FK history / request trail)
        patient.FullName = "Deleted Patient";
        patient.Username = $"deleted-{patientId}@deleted.invalid";
        patient.PasswordHash = _hasher.HashPassword(patient, Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
        patient.Phone = "";
        patient.PhoneVerified = false;
        patient.PhoneVerificationCodeHash = null;
        patient.PhoneVerificationExpiresAtUtc = null;
        patient.EmailVerified = false;
        patient.EmailVerificationTokenHash = null;
        patient.EmailVerificationExpiresAtUtc = null;
        patient.PasswordResetTokenHash = null;
        patient.PasswordResetExpiresAtUtc = null;
        patient.PreferenceProfileJson = null;
        patient.ReminderSettingsJson = null;
        patient.IdCardPhotoUrl = null;
        patient.HipaaDataSharingOptIn = false;
        patient.CookieTrackingOptOut = true;
        patient.AutofillEnabled = false;
        patient.DateOfBirth = DateOnly.FromDateTime(DateTime.UnixEpoch);

        row.Status = DataSubjectRequestStatuses.Completed;
        row.CompletedAtUtc = DateTime.UtcNow;
        row.PmsRemoteCopyNoted = hadPmsRefs;
        row.StaffNotes = hadPmsRefs
            ? "Local NuviDoc PHI purged/de-identified. PMS / practice chart copies may remain — see Docs/HIPAA_Code_Modifications.md § PMS remote copies."
            : "Local NuviDoc PHI purged/de-identified. No linked appointments noted for PMS follow-up.";

        await _db.SaveChangesAsync(cancellationToken);

        await _audit.LogAsync(_db, new AuditLogRequest
        {
            Action = AuditActions.Delete,
            EntityType = AuditEntityTypes.Patient,
            EntityId = patientId.ToString(),
            Summary = hadPmsRefs
                ? $"Account deleted/de-identified (request {row.Id}); PMS remote copies noted"
                : $"Account deleted/de-identified (request {row.Id})"
        }, cancellationToken);

        _logger.LogInformation("Patient account de-identified after privacy delete request {RequestId}", row.Id);

        return new PrivacyRightsResult
        {
            Success = true,
            Message = hadPmsRefs
                ? "Your NuviDoc account data was deleted. Information already sent to a dental practice’s system may remain with that practice; contact them for their records. You have been signed out."
                : "Your NuviDoc account and personal information were deleted. You have been signed out.",
            RequestId = row.Id,
            RequestType = DataSubjectRequestTypes.Delete,
            AccountDeleted = true
        };
    }

    private async Task<string?> BuildAccessExportJsonAsync(int patientId, CancellationToken cancellationToken)
    {
        var patient = await _db.Patients.AsNoTracking()
            .Include(p => p.InsuranceCoverages)
            .ThenInclude(c => c.InsuranceCarrier)
            .Include(p => p.InsuranceCoverages)
            .ThenInclude(c => c.InsurancePlan)
            .FirstOrDefaultAsync(p => p.Id == patientId, cancellationToken);
        if (patient == null) return null;

        var appointments = await _db.Appointments.AsNoTracking()
            .Where(a => a.PatientId == patientId)
            .OrderByDescending(a => a.StartsAt)
            .Select(a => new
            {
                a.Id,
                a.StartsAt,
                a.Status,
                a.Source,
                a.VisitReason,
                a.PatientName,
                a.PatientPhone,
                a.PatientEmail,
                dateOfBirth = a.PatientDateOfBirth.HasValue ? a.PatientDateOfBirth.Value.ToString("yyyy-MM-dd") : null,
                a.DoctorId
            })
            .ToListAsync(cancellationToken);

        var sessions = await _db.SearchSessions.AsNoTracking()
            .Where(s => s.PatientId == patientId)
            .OrderByDescending(s => s.CreatedAt)
            .Select(s => new
            {
                s.Id,
                s.SessionKey,
                s.Specialty,
                s.Location,
                s.CreatedAt,
                s.MedicalIssuesSummary,
                messageCount = s.ChatMessages.Count
            })
            .ToListAsync(cancellationToken);

        // Chat content included for access right; legal may later exclude — toggle here if needed.
        var sessionIdList = sessions.Select(s => s.Id).ToList();
        var messages = sessionIdList.Count == 0
            ? new List<object>()
            : (await _db.ChatMessages.AsNoTracking()
                .Where(m => sessionIdList.Contains(m.SearchSessionId))
                .OrderBy(m => m.CreatedAt)
                .Select(m => new { m.SearchSessionId, m.Role, m.Content, m.CreatedAt })
                .ToListAsync(cancellationToken))
            .Cast<object>()
            .ToList();

        var authorizations = await _db.HipaaAuthorizations.AsNoTracking()
            .Where(h => h.PatientId == patientId)
            .OrderByDescending(h => h.OccurredAtUtc)
            .Select(h => new
            {
                h.FormVersion,
                h.Granted,
                h.OccurredAtUtc,
                h.ESignName
            })
            .ToListAsync(cancellationToken);

        var payload = new
        {
            exportedAtUtc = DateTime.UtcNow,
            exportType = "verified_access_request",
            profile = new
            {
                patient.FullName,
                patient.Username,
                dateOfBirth = patient.DateOfBirth.ToString("yyyy-MM-dd"),
                patient.Phone,
                patient.PhoneVerified,
                patient.EmailVerified,
                memberSince = patient.CreatedAt
            },
            insurance = patient.InsuranceCoverages
                .OrderBy(c => c.InsuranceType)
                .Select(c => new
                {
                    c.InsuranceType,
                    carrier = c.InsuranceCarrier?.Name ?? c.CustomCarrierName,
                    plan = c.InsurancePlan?.Name ?? c.CustomPlanName,
                    hasMemberId = !string.IsNullOrEmpty(c.MemberId),
                    hasCardPhoto = !string.IsNullOrEmpty(c.CardPhotoUrl)
                }),
            preferences = new
            {
                patient.AutofillEnabled,
                patient.HipaaDataSharingOptIn,
                patient.CookieTrackingOptOut,
                preferenceProfileJson = patient.PreferenceProfileJson,
                reminderSettingsJson = patient.ReminderSettingsJson
            },
            appointments,
            searchSessions = sessions,
            chatMessages = messages,
            hipaaAuthorizations = authorizations
        };

        return JsonSerializer.Serialize(payload, JsonOptions);
    }

    private static void SetDueDates(DataSubjectRequest row, string type, DateTime now)
    {
        switch (type)
        {
            case DataSubjectRequestTypes.Access:
                row.HipaaDueAtUtc = now.AddDays(30);
                row.ConsumerDueAtUtc = now.AddDays(45);
                break;
            case DataSubjectRequestTypes.Amend:
                row.HipaaDueAtUtc = now.AddDays(60);
                row.ConsumerDueAtUtc = now.AddDays(60);
                break;
            case DataSubjectRequestTypes.Delete:
                row.HipaaDueAtUtc = now.AddDays(30);
                row.ConsumerDueAtUtc = now.AddDays(45);
                break;
        }
    }

    private static string? NormalizeType(string? requestType) =>
        (requestType ?? "").Trim().ToLowerInvariant() switch
        {
            DataSubjectRequestTypes.Access => DataSubjectRequestTypes.Access,
            DataSubjectRequestTypes.Amend => DataSubjectRequestTypes.Amend,
            DataSubjectRequestTypes.Delete => DataSubjectRequestTypes.Delete,
            _ => null
        };

    private static bool IsDeletedAccount(Patient patient) =>
        patient.Username.StartsWith("deleted-", StringComparison.OrdinalIgnoreCase)
        && patient.Username.EndsWith("@deleted.invalid", StringComparison.OrdinalIgnoreCase);

    private string ResolveBaseUrl(string? publicBaseUrl)
    {
        if (!string.IsNullOrWhiteSpace(publicBaseUrl))
            return publicBaseUrl.Trim().TrimEnd('/');
        if (!string.IsNullOrWhiteSpace(_emailOptions.PublicBaseUrl))
            return _emailOptions.PublicBaseUrl.Trim().TrimEnd('/');
        return "https://nuvidoc.com";
    }

    private static string CreateToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static string HashToken(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return "";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(token.Trim()));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string Escape(string value) =>
        System.Net.WebUtility.HtmlEncode(value);

    private static string? Truncate(string? value, int max) =>
        string.IsNullOrEmpty(value) ? value : value.Length <= max ? value : value[..max];

    private static PrivacyRightsResult Fail(string message) =>
        new() { Success = false, Message = message };
}
