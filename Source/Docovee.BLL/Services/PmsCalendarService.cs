using Docovee.DS;
using Docovee.DS.Entities;
using Docovee.Integrations.Configuration;
using Docovee.Integrations.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Docovee.BLL.Services;

public interface IPmsCalendarService
{
    Task<PmsConnectionSettingsDto?> GetConnectionAsync(
        int doctorId,
        string? providerId = null,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<PmsConnectionSettingsDto>> GetConnectionsAsync(
        int doctorId,
        CancellationToken cancellationToken = default);

    Task<(bool Success, string? Error, PmsConnectionSettingsDto? Connection)> SaveConnectionAsync(
        int doctorId,
        PmsConnectionSaveRequest request,
        CancellationToken cancellationToken = default);

    Task<(bool Success, string Message)> TestConnectionAsync(
        int doctorId,
        string providerId,
        CancellationToken cancellationToken = default);

    Task<bool> HasEnabledConnectionAsync(int doctorId, CancellationToken cancellationToken = default);

    bool HasGlobalNexHealthApiKey { get; }

    Task<(bool Success, string Message, string? ProviderExternalId, IReadOnlyList<PmsProviderOption> Candidates)>
        EnsureNexHealthProviderAsync(
            int doctorId,
            string doctorName,
            string? email = null,
            string? phone = null,
            CancellationToken cancellationToken = default);

    Task<IReadOnlyList<PmsSlot>> GetAvailabilityAsync(
        int doctorId,
        DateOnly from,
        DateOnly to,
        int slotMinutes = 40,
        CancellationToken cancellationToken = default);

    Task PushAppointmentCreatedAsync(Appointment appointment, CancellationToken cancellationToken = default);

    Task PushAppointmentStatusAsync(Appointment appointment, CancellationToken cancellationToken = default);

    Task<int> SyncInboundAsync(CancellationToken cancellationToken = default);

    Task<int> SyncInboundForDoctorAsync(int doctorId, CancellationToken cancellationToken = default);
}

public sealed class PmsConnectionSettingsDto
{
    public int Id { get; set; }
    public string Provider { get; set; } = "";
    public bool IsEnabled { get; set; }
    public bool HasCustomerKey { get; set; }
    public bool HasApiKey { get; set; }
    public string? InstitutionId { get; set; }
    public string? LocationExternalId { get; set; }
    public string? ProviderExternalId { get; set; }
    public string? OperatoryId { get; set; }
    public string? ClinicNum { get; set; }
    public string? LastError { get; set; }
    public DateTime? LastSyncAt { get; set; }
    public DateTime? LastTestAt { get; set; }
}

public sealed class PmsConnectionSaveRequest
{
    public string Provider { get; set; } = PmsProviders.OpenDental;
    public bool IsEnabled { get; set; }
    public string? CustomerApiKey { get; set; }
    public string? ApiKey { get; set; }
    public string? InstitutionId { get; set; }
    public string? LocationExternalId { get; set; }
    public string? ProviderExternalId { get; set; }
    public string? OperatoryId { get; set; }
    public string? ClinicNum { get; set; }
    public string? BaseUrl { get; set; }
}

public sealed class PmsCalendarService : IPmsCalendarService
{
    private readonly DocoveeDbContext _db;
    private readonly IEnumerable<IPmsProvider> _providers;
    private readonly NexHealthOptions _nexHealthOptions;
    private readonly ILogger<PmsCalendarService> _logger;

    public PmsCalendarService(
        DocoveeDbContext db,
        IEnumerable<IPmsProvider> providers,
        IOptions<NexHealthOptions> nexHealthOptions,
        ILogger<PmsCalendarService> logger)
    {
        _db = db;
        _providers = providers;
        _nexHealthOptions = nexHealthOptions.Value;
        _logger = logger;
    }

    public bool HasGlobalNexHealthApiKey => !string.IsNullOrWhiteSpace(_nexHealthOptions.ApiKey);

    public async Task<PmsConnectionSettingsDto?> GetConnectionAsync(
        int doctorId,
        string? providerId = null,
        CancellationToken cancellationToken = default)
    {
        var query = _db.PmsConnections.AsNoTracking().Where(c => c.DoctorId == doctorId);
        if (!string.IsNullOrWhiteSpace(providerId))
            query = query.Where(c => c.Provider == providerId);

        var row = await query.OrderByDescending(c => c.IsEnabled).ThenByDescending(c => c.UpdatedAt)
            .FirstOrDefaultAsync(cancellationToken);
        return row == null ? null : ToDto(row);
    }

    public async Task<IReadOnlyList<PmsConnectionSettingsDto>> GetConnectionsAsync(
        int doctorId,
        CancellationToken cancellationToken = default)
    {
        var rows = await _db.PmsConnections.AsNoTracking()
            .Where(c => c.DoctorId == doctorId)
            .OrderBy(c => c.Provider)
            .ToListAsync(cancellationToken);
        return rows.Select(ToDto).ToList();
    }

    public async Task<(bool Success, string? Error, PmsConnectionSettingsDto? Connection)> SaveConnectionAsync(
        int doctorId,
        PmsConnectionSaveRequest request,
        CancellationToken cancellationToken = default)
    {
        var providerId = NormalizeProvider(request.Provider);
        if (providerId is not (PmsProviders.OpenDental or PmsProviders.NexHealth))
            return (false, "Unsupported PMS provider.", null);

        var doctorExists = await _db.Doctors.AsNoTracking().AnyAsync(d => d.Id == doctorId, cancellationToken);
        if (!doctorExists)
            return (false, "Doctor not found.", null);

        var row = await _db.PmsConnections
            .FirstOrDefaultAsync(c => c.DoctorId == doctorId && c.Provider == providerId, cancellationToken);

        var now = DateTime.UtcNow;
        if (row == null)
        {
            row = new PmsConnection
            {
                DoctorId = doctorId,
                Provider = providerId,
                CreatedAt = now
            };
            _db.PmsConnections.Add(row);
        }

        row.IsEnabled = request.IsEnabled;
        row.InstitutionId = NullIfEmpty(request.InstitutionId);
        row.LocationExternalId = NullIfEmpty(request.LocationExternalId);
        row.ProviderExternalId = NullIfEmpty(request.ProviderExternalId);
        row.OperatoryId = NullIfEmpty(request.OperatoryId);
        row.ClinicNum = NullIfEmpty(request.ClinicNum);
        row.BaseUrl = NullIfEmpty(request.BaseUrl);
        row.UpdatedAt = now;

        if (!string.IsNullOrWhiteSpace(request.CustomerApiKey))
            row.CustomerApiKey = request.CustomerApiKey.Trim();
        if (!string.IsNullOrWhiteSpace(request.ApiKey))
            row.ApiKey = request.ApiKey.Trim();

        if (providerId == PmsProviders.OpenDental && string.IsNullOrWhiteSpace(row.CustomerApiKey))
            return (false, "Open Dental customer key is required.", null);
        if (providerId == PmsProviders.NexHealth
            && string.IsNullOrWhiteSpace(row.ApiKey)
            && string.IsNullOrWhiteSpace(_nexHealthOptions.ApiKey))
            return (false, "NexHealth API key is required in appsettings (NexHealth:ApiKey).", null);

        await _db.SaveChangesAsync(cancellationToken);
        return (true, null, ToDto(row));
    }

    public async Task<(bool Success, string Message, string? ProviderExternalId, IReadOnlyList<PmsProviderOption> Candidates)>
        EnsureNexHealthProviderAsync(
            int doctorId,
            string doctorName,
            string? email = null,
            string? phone = null,
            CancellationToken cancellationToken = default)
    {
        var row = await _db.PmsConnections
            .FirstOrDefaultAsync(c => c.DoctorId == doctorId && c.Provider == PmsProviders.NexHealth, cancellationToken);
        if (row == null)
            return (false, "Save subdomain and location first, then click Add Provider.", null, Array.Empty<PmsProviderOption>());

        if (string.IsNullOrWhiteSpace(row.InstitutionId))
            return (false, "Subdomain is required before adding a provider.", null, Array.Empty<PmsProviderOption>());

        var provider = ResolveProvider(PmsProviders.NexHealth);
        if (provider == null)
            return (false, "NexHealth provider is not registered.", null, Array.Empty<PmsProviderOption>());

        var result = await provider.EnsureProviderAsync(new PmsEnsureProviderRequest
        {
            Credentials = ToCredentials(row),
            FullName = doctorName,
            Email = email,
            Phone = phone
        }, cancellationToken);

        if (result.Success && !string.IsNullOrWhiteSpace(result.ProviderExternalId))
        {
            row.ProviderExternalId = result.ProviderExternalId;
            row.UpdatedAt = DateTime.UtcNow;
            row.LastError = null;
            await _db.SaveChangesAsync(cancellationToken);
            return (true, result.Message ?? "Provider linked.", result.ProviderExternalId, result.Candidates);
        }

        if (!string.IsNullOrWhiteSpace(result.Error))
        {
            row.LastError = Truncate(result.Error, 500);
            row.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(cancellationToken);
        }

        return (false, result.Error ?? "Could not resolve NexHealth provider.", null, result.Candidates);
    }

    public async Task<(bool Success, string Message)> TestConnectionAsync(
        int doctorId,
        string providerId,
        CancellationToken cancellationToken = default)
    {
        var normalized = NormalizeProvider(providerId);
        var row = await _db.PmsConnections
            .FirstOrDefaultAsync(c => c.DoctorId == doctorId && c.Provider == normalized, cancellationToken);
        if (row == null)
            return (false, "Save connection settings before testing.");

        var provider = ResolveProvider(normalized);
        if (provider == null)
            return (false, "Provider is not registered.");

        var result = await provider.TestConnectionAsync(ToCredentials(row), cancellationToken);
        row.LastTestAt = DateTime.UtcNow;
        row.LastError = result.Success ? null : result.Message;
        row.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);

        return (result.Success, result.Message ?? (result.Success ? "Connected." : "Connection failed."));
    }

    public async Task<bool> HasEnabledConnectionAsync(int doctorId, CancellationToken cancellationToken = default)
    {
        return await _db.PmsConnections.AsNoTracking()
            .AnyAsync(c => c.DoctorId == doctorId && c.IsEnabled, cancellationToken);
    }

    public async Task<IReadOnlyList<PmsSlot>> GetAvailabilityAsync(
        int doctorId,
        DateOnly from,
        DateOnly to,
        int slotMinutes = 40,
        CancellationToken cancellationToken = default)
    {
        var connection = await GetEnabledConnectionEntityAsync(doctorId, cancellationToken);
        if (connection == null)
            return Array.Empty<PmsSlot>();

        var provider = ResolveProvider(connection.Provider);
        if (provider == null)
            return Array.Empty<PmsSlot>();

        try
        {
            return await provider.GetAvailabilityAsync(new PmsAvailabilityRequest
            {
                Credentials = ToCredentials(connection),
                From = from,
                To = to,
                SlotMinutes = slotMinutes
            }, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "PMS availability failed for doctor {DoctorId}", doctorId);
            await MarkConnectionErrorAsync(connection.Id, ex.Message, cancellationToken);
            return Array.Empty<PmsSlot>();
        }
    }

    public async Task PushAppointmentCreatedAsync(Appointment appointment, CancellationToken cancellationToken = default)
    {
        var connection = await GetEnabledConnectionEntityAsync(appointment.DoctorId, cancellationToken);
        if (connection == null)
            return;

        var existing = await _db.PmsExternalRefs.AsNoTracking()
            .AnyAsync(r => r.AppointmentId == appointment.Id && r.Provider == connection.Provider, cancellationToken);
        if (existing)
            return;

        var provider = ResolveProvider(connection.Provider);
        if (provider == null)
            return;

        try
        {
            var result = await provider.CreateAppointmentAsync(new PmsCreateAppointmentRequest
            {
                Credentials = ToCredentials(connection),
                Patient = new PmsPatientInfo
                {
                    FullName = appointment.PatientName,
                    Phone = appointment.PatientPhone,
                    Email = appointment.PatientEmail,
                    DateOfBirth = appointment.PatientDateOfBirth
                },
                StartsAt = appointment.StartsAt,
                DurationMinutes = 40,
                VisitReason = appointment.VisitReason,
                Note = $"NuviDoc appointment #{appointment.Id}",
                IdempotencyKey = $"nuvidoc-{appointment.Id}"
            }, cancellationToken);

            if (!result.Success || string.IsNullOrWhiteSpace(result.ExternalAppointmentId))
            {
                await MarkConnectionErrorAsync(connection.Id, result.Error ?? "Create appointment failed.", cancellationToken);
                _logger.LogWarning(
                    "PMS outbound create failed for appointment {Id}: {Error}",
                    appointment.Id, result.Error);
                return;
            }

            _db.PmsExternalRefs.Add(new PmsExternalRef
            {
                DoctorId = appointment.DoctorId,
                AppointmentId = appointment.Id,
                Provider = connection.Provider,
                ExternalAppointmentId = result.ExternalAppointmentId,
                ExternalPatientId = result.ExternalPatientId,
                ExternalLocationId = connection.LocationExternalId,
                SyncDirection = "Outbound",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            });
            connection.LastSyncAt = DateTime.UtcNow;
            connection.LastError = null;
            connection.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "PMS outbound create exception for appointment {Id}", appointment.Id);
            await MarkConnectionErrorAsync(connection.Id, ex.Message, cancellationToken);
        }
    }

    public async Task PushAppointmentStatusAsync(Appointment appointment, CancellationToken cancellationToken = default)
    {
        var connection = await GetEnabledConnectionEntityAsync(appointment.DoctorId, cancellationToken);
        if (connection == null)
            return;

        var externalRef = await _db.PmsExternalRefs
            .FirstOrDefaultAsync(r =>
                r.AppointmentId == appointment.Id && r.Provider == connection.Provider, cancellationToken);
        if (externalRef == null || string.IsNullOrWhiteSpace(externalRef.ExternalAppointmentId))
            return;

        var provider = ResolveProvider(connection.Provider);
        if (provider == null)
            return;

        try
        {
            var result = await provider.UpdateAppointmentAsync(new PmsUpdateAppointmentRequest
            {
                Credentials = ToCredentials(connection),
                ExternalAppointmentId = externalRef.ExternalAppointmentId,
                Status = appointment.Status,
                Note = $"NuviDoc status: {appointment.Status}"
            }, cancellationToken);

            if (!result.Success)
            {
                externalRef.LastError = result.Error;
                externalRef.UpdatedAt = DateTime.UtcNow;
                await MarkConnectionErrorAsync(connection.Id, result.Error ?? "Update failed.", cancellationToken);
                await _db.SaveChangesAsync(cancellationToken);
                return;
            }

            externalRef.LastError = null;
            externalRef.UpdatedAt = DateTime.UtcNow;
            connection.LastSyncAt = DateTime.UtcNow;
            connection.LastError = null;
            connection.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "PMS outbound update exception for appointment {Id}", appointment.Id);
            await MarkConnectionErrorAsync(connection.Id, ex.Message, cancellationToken);
        }
    }

    public async Task<int> SyncInboundAsync(CancellationToken cancellationToken = default)
    {
        var doctorIds = await _db.PmsConnections.AsNoTracking()
            .Where(c => c.IsEnabled)
            .Select(c => c.DoctorId)
            .Distinct()
            .ToListAsync(cancellationToken);

        var total = 0;
        foreach (var doctorId in doctorIds)
            total += await SyncInboundForDoctorAsync(doctorId, cancellationToken);
        return total;
    }

    public async Task<int> SyncInboundForDoctorAsync(int doctorId, CancellationToken cancellationToken = default)
    {
        var connections = await _db.PmsConnections
            .Where(c => c.DoctorId == doctorId && c.IsEnabled)
            .ToListAsync(cancellationToken);

        var total = 0;
        foreach (var connection in connections)
            total += await SyncInboundForConnectionAsync(connection, cancellationToken);
        return total;
    }

    private async Task<int> SyncInboundForConnectionAsync(
        PmsConnection connection,
        CancellationToken cancellationToken)
    {
        var provider = ResolveProvider(connection.Provider);
        if (provider == null)
            return 0;

        IReadOnlyList<PmsExternalAppointment> remote;
        try
        {
            remote = await provider.PullRecentAppointmentsAsync(new PmsPullChangesRequest
            {
                Credentials = ToCredentials(connection),
                SinceUtc = connection.LastSyncAt ?? DateTime.UtcNow.AddDays(-7),
                FromDate = DateOnly.FromDateTime(DateTime.Today.AddDays(-30)),
                ToDate = DateOnly.FromDateTime(DateTime.Today.AddDays(90))
            }, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "PMS inbound pull failed for doctor {DoctorId} provider {Provider}",
                connection.DoctorId, connection.Provider);
            await MarkConnectionErrorAsync(connection.Id, ex.Message, cancellationToken);
            return 0;
        }

        var changed = 0;
        foreach (var item in remote)
        {
            if (string.IsNullOrWhiteSpace(item.ExternalAppointmentId))
                continue;

            var externalRef = await _db.PmsExternalRefs
                .Include(r => r.Appointment)
                .FirstOrDefaultAsync(r =>
                    r.DoctorId == connection.DoctorId
                    && r.Provider == connection.Provider
                    && r.ExternalAppointmentId == item.ExternalAppointmentId, cancellationToken);

            if (externalRef?.Appointment != null)
            {
                var appt = externalRef.Appointment;
                var updated = false;
                if (appt.StartsAt != item.StartsAt)
                {
                    appt.StartsAt = ToWallClock(item.StartsAt);
                    updated = true;
                }

                var mapped = string.IsNullOrWhiteSpace(item.MappedStatus)
                    ? appt.Status
                    : AppointmentStatuses.Normalize(item.MappedStatus);
                if (!string.Equals(AppointmentStatuses.Normalize(appt.Status), mapped, StringComparison.OrdinalIgnoreCase))
                {
                    appt.Status = mapped;
                    updated = true;
                }

                if (updated)
                {
                    appt.UpdatedAt = DateTime.UtcNow;
                    externalRef.UpdatedAt = DateTime.UtcNow;
                    externalRef.SyncDirection = "Inbound";
                    changed++;
                }
            }
            else if (externalRef == null)
            {
                var now = DateTime.UtcNow;
                var appointment = new Appointment
                {
                    DoctorId = connection.DoctorId,
                    PatientName = string.IsNullOrWhiteSpace(item.PatientName) ? "Patient" : item.PatientName.Trim(),
                    PatientPhone = item.PatientPhone,
                    PatientEmail = item.PatientEmail,
                    VisitReason = string.IsNullOrWhiteSpace(item.VisitReason) ? "PMS appointment" : item.VisitReason.Trim(),
                    StartsAt = ToWallClock(item.StartsAt),
                    Status = string.IsNullOrWhiteSpace(item.MappedStatus)
                        ? AppointmentStatuses.Unconfirmed
                        : AppointmentStatuses.Normalize(item.MappedStatus),
                    Source = AppointmentSources.PmsInbound,
                    CreatedAt = now,
                    UpdatedAt = now
                };
                _db.Appointments.Add(appointment);
                await _db.SaveChangesAsync(cancellationToken);

                _db.PmsExternalRefs.Add(new PmsExternalRef
                {
                    DoctorId = connection.DoctorId,
                    AppointmentId = appointment.Id,
                    Provider = connection.Provider,
                    ExternalAppointmentId = item.ExternalAppointmentId,
                    ExternalPatientId = item.ExternalPatientId,
                    ExternalLocationId = connection.LocationExternalId,
                    SyncDirection = "Inbound",
                    CreatedAt = now,
                    UpdatedAt = now
                });
                changed++;
            }
        }

        connection.LastSyncAt = DateTime.UtcNow;
        connection.LastError = null;
        connection.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);
        return changed;
    }

    private async Task<PmsConnection?> GetEnabledConnectionEntityAsync(int doctorId, CancellationToken cancellationToken)
    {
        return await _db.PmsConnections
            .Where(c => c.DoctorId == doctorId && c.IsEnabled)
            .OrderByDescending(c => c.UpdatedAt)
            .FirstOrDefaultAsync(cancellationToken);
    }

    private IPmsProvider? ResolveProvider(string providerId) =>
        _providers.FirstOrDefault(p => string.Equals(p.ProviderId, providerId, StringComparison.OrdinalIgnoreCase));

    private async Task MarkConnectionErrorAsync(int connectionId, string error, CancellationToken cancellationToken)
    {
        var row = await _db.PmsConnections.FirstOrDefaultAsync(c => c.Id == connectionId, cancellationToken);
        if (row == null)
            return;
        row.LastError = Truncate(error, 500);
        row.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);
    }

    private static PmsConnectionCredentials ToCredentials(PmsConnection row) => new()
    {
        ProviderId = row.Provider,
        DeveloperApiKey = row.DeveloperApiKey,
        CustomerApiKey = row.CustomerApiKey,
        ApiKey = row.ApiKey,
        InstitutionId = row.InstitutionId,
        LocationId = row.LocationExternalId,
        ProviderExternalId = row.ProviderExternalId,
        OperatoryId = row.OperatoryId,
        ClinicNum = row.ClinicNum,
        BaseUrl = row.BaseUrl
    };

    private PmsConnectionSettingsDto ToDto(PmsConnection row) => new()
    {
        Id = row.Id,
        Provider = row.Provider,
        IsEnabled = row.IsEnabled,
        HasCustomerKey = !string.IsNullOrWhiteSpace(row.CustomerApiKey),
        HasApiKey = !string.IsNullOrWhiteSpace(row.ApiKey)
            || (row.Provider == PmsProviders.NexHealth && HasGlobalNexHealthApiKey),
        InstitutionId = row.InstitutionId,
        LocationExternalId = row.LocationExternalId,
        ProviderExternalId = row.ProviderExternalId,
        OperatoryId = row.OperatoryId,
        ClinicNum = row.ClinicNum,
        LastError = row.LastError,
        LastSyncAt = row.LastSyncAt,
        LastTestAt = row.LastTestAt
    };

    private static string NormalizeProvider(string? provider) =>
        (provider ?? "").Trim().ToLowerInvariant() switch
        {
            "open dental" or "open-dental" or "od" => PmsProviders.OpenDental,
            "nex health" or "nex-health" or "nh" => PmsProviders.NexHealth,
            var p => p
        };

    private static string? NullIfEmpty(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max];

    private static DateTime ToWallClock(DateTime value)
    {
        var local = value.Kind switch
        {
            DateTimeKind.Utc => value.ToLocalTime(),
            DateTimeKind.Local => value,
            _ => value
        };
        return DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
    }
}
