using Docovee.DS;
using Docovee.DS.Entities;
using Docovee.logging;
using Microsoft.EntityFrameworkCore;

namespace Docovee.BLL.Services;

public sealed class PatientAccountLifecycleResult
{
    public int ClosedCount { get; init; }
    public int DeletedCount { get; init; }
}

public interface IPatientAccountLifecycleService
{
    Task<PatientAccountLifecycleResult> ProcessDueAccountsAsync(CancellationToken cancellationToken = default);
}

public sealed class PatientAccountLifecycleService : IPatientAccountLifecycleService
{
    private const int BatchSize = 100;

    private readonly DocoveeDbContext _db;
    private readonly IAppSettingsService _appSettings;
    private readonly IPatientPrivacyRightsService _privacyRights;
    private readonly IAdminPatientService _adminPatients;
    private readonly IDocoveeLogger _logger;

    public PatientAccountLifecycleService(
        DocoveeDbContext db,
        IAppSettingsService appSettings,
        IPatientPrivacyRightsService privacyRights,
        IAdminPatientService adminPatients,
        IDocoveeLogger logger)
    {
        _db = db;
        _appSettings = appSettings;
        _privacyRights = privacyRights;
        _adminPatients = adminPatients;
        _logger = logger;
    }

    public async Task<PatientAccountLifecycleResult> ProcessDueAccountsAsync(CancellationToken cancellationToken = default)
    {
        var settings = await _appSettings.GetPatientAccountLifecycleSettingsAsync(cancellationToken);
        var closed = 0;
        var deleted = 0;

        if (settings.AutoCloseInactiveEnabled)
            closed = await ProcessAutoCloseInactiveAsync(settings.AutoCloseInactiveMonths, cancellationToken);

        if (settings.AutoDeleteClosedEnabled)
            deleted = await ProcessAutoDeleteClosedAsync(settings.AutoDeleteClosedMonths, cancellationToken);

        return new PatientAccountLifecycleResult
        {
            ClosedCount = closed,
            DeletedCount = deleted
        };
    }

    private async Task<int> ProcessAutoCloseInactiveAsync(int inactiveMonths, CancellationToken cancellationToken)
    {
        var cutoff = DateTime.UtcNow.AddMonths(-inactiveMonths);
        var total = 0;

        while (true)
        {
            var batch = await _db.Patients
                .AsNoTracking()
                .Where(p => !p.IsDeleted)
                .Where(p => (p.LastLoginAtUtc ?? p.CreatedAt) <= cutoff)
                .OrderBy(p => p.Id)
                .Select(p => p.Id)
                .Take(BatchSize)
                .ToListAsync(cancellationToken);

            if (batch.Count == 0)
                break;

            foreach (var patientId in batch)
            {
                var (success, error) = await _privacyRights.SoftClosePatientAccountAsync(
                    patientId,
                    $"Auto-closed due to inactivity ({inactiveMonths} months without login)",
                    cancellationToken);

                if (success)
                    total++;
                else
                    _logger.LogWarning("Auto-close skipped for patient {PatientId}: {Error}", patientId, error);
            }

            if (batch.Count < BatchSize)
                break;
        }

        return total;
    }

    private async Task<int> ProcessAutoDeleteClosedAsync(int closedMonths, CancellationToken cancellationToken)
    {
        var cutoff = DateTime.UtcNow.AddMonths(-closedMonths);
        var total = 0;

        while (true)
        {
            var batch = await _db.Patients
                .AsNoTracking()
                .Where(p => p.IsDeleted && p.DeletedAtUtc != null && p.DeletedAtUtc <= cutoff)
                .OrderBy(p => p.Id)
                .Select(p => p.Id)
                .Take(BatchSize)
                .ToListAsync(cancellationToken);

            if (batch.Count == 0)
                break;

            foreach (var patientId in batch)
            {
                var (success, error) = await _adminPatients.HardDeleteAsync(
                    patientId,
                    bypassWaitPeriod: true,
                    cancellationToken);

                if (success)
                    total++;
                else
                    _logger.LogWarning("Auto-delete skipped for patient {PatientId}: {Error}", patientId, error);
            }

            if (batch.Count < BatchSize)
                break;
        }

        return total;
    }
}
