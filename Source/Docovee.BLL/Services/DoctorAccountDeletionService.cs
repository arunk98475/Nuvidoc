using Docovee.BLL.Audit;
using Docovee.BLL.Configuration;
using Docovee.DS;
using Docovee.DS.Entities;
using Docovee.logging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Docovee.BLL.Services;

public interface IDoctorAccountDeletionService
{
    Task<(bool Success, string? Error)> SoftDeleteAsync(
        int doctorId,
        string reason,
        CancellationToken cancellationToken = default);

    Task<(bool Success, string? Error)> ActivateAsync(int doctorId, CancellationToken cancellationToken = default);

    Task<(bool Success, string? Error)> HardDeleteAsync(int doctorId, CancellationToken cancellationToken = default);
}

public sealed class DoctorAccountDeletionService : IDoctorAccountDeletionService
{
    private readonly DocoveeDbContext _db;
    private readonly IAuditTrailService _audit;
    private readonly AccountOptions _account;
    private readonly IDocoveeLogger _logger;

    public DoctorAccountDeletionService(
        DocoveeDbContext db,
        IAuditTrailService audit,
        IOptions<AccountOptions> account,
        IDocoveeLogger logger)
    {
        _db = db;
        _audit = audit;
        _account = account.Value;
        _logger = logger;
    }

    public async Task<(bool Success, string? Error)> SoftDeleteAsync(
        int doctorId,
        string reason,
        CancellationToken cancellationToken = default)
    {
        var doctor = await _db.Doctors.FirstOrDefaultAsync(d => d.Id == doctorId, cancellationToken);
        if (doctor == null)
            return (false, "Doctor not found.");

        if (doctor.IsDeleted)
            return (false, "This doctor account is already closed.");

        doctor.IsDeleted = true;
        doctor.DeletedAtUtc = DateTime.UtcNow;
        doctor.IsActive = false;
        doctor.IsSponsored = false;

        await _db.SaveChangesAsync(cancellationToken);

        await _audit.LogAsync(_db, new AuditLogRequest
        {
            Action = AuditActions.Delete,
            EntityType = AuditEntityTypes.Doctor,
            EntityId = doctorId.ToString(),
            Summary = string.IsNullOrWhiteSpace(reason)
                ? "Doctor account closed (soft delete)"
                : reason
        }, cancellationToken);

        _logger.LogInformation("Doctor account soft-deleted {DoctorId}", doctorId);
        return (true, null);
    }

    public async Task<(bool Success, string? Error)> ActivateAsync(
        int doctorId,
        CancellationToken cancellationToken = default)
    {
        var doctor = await _db.Doctors.FirstOrDefaultAsync(d => d.Id == doctorId, cancellationToken);
        if (doctor == null)
            return (false, "Doctor not found.");

        if (!doctor.IsDeleted)
            return (false, "This doctor account is already active.");

        doctor.IsDeleted = false;
        doctor.DeletedAtUtc = null;
        doctor.IsActive = true;

        await _db.SaveChangesAsync(cancellationToken);

        await _audit.LogAsync(_db, new AuditLogRequest
        {
            Action = AuditActions.Update,
            EntityType = AuditEntityTypes.Doctor,
            EntityId = doctorId.ToString(),
            Summary = "Doctor account reactivated after soft delete"
        }, cancellationToken);

        _logger.LogInformation("Doctor account activated {DoctorId}", doctorId);
        return (true, null);
    }

    public async Task<(bool Success, string? Error)> HardDeleteAsync(int doctorId, CancellationToken cancellationToken = default)
    {
        var doctor = await _db.Doctors.FirstOrDefaultAsync(d => d.Id == doctorId, cancellationToken);
        if (doctor == null)
            return (false, "Doctor not found.");

        if (!doctor.IsDeleted)
            return (false, "Close the account before permanently removing it.");

        var waitDays = Math.Max(0, _account.HardDeleteWaitDays);
        if (!DeletedAccountHelper.CanPermanentlyRemove(doctor.DeletedAtUtc, waitDays))
        {
            var availableAt = DeletedAccountHelper.PermanentRemoveAvailableAtUtc(doctor.DeletedAtUtc, waitDays);
            return (false, availableAt.HasValue
                ? $"Permanent remove is available after {availableAt.Value:u} UTC ({waitDays} day(s) after closure)."
                : "Permanent remove is not available yet.");
        }

        _db.Doctors.Remove(doctor);
        await _db.SaveChangesAsync(cancellationToken);

        await _audit.LogAsync(_db, new AuditLogRequest
        {
            Action = AuditActions.Delete,
            EntityType = AuditEntityTypes.Doctor,
            EntityId = doctorId.ToString(),
            Summary = "Doctor permanently deleted (hard delete)"
        }, cancellationToken);

        _logger.LogInformation("Doctor account hard-deleted {DoctorId}", doctorId);
        return (true, null);
    }
}
