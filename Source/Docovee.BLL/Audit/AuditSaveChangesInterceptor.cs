using Docovee.DS;
using Docovee.DS.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Docovee.BLL.Audit;

/// <summary>
/// Captures Create/Update/Delete operations before they are committed to the database.
/// </summary>
public sealed class AuditSaveChangesInterceptor : SaveChangesInterceptor
{
    private readonly IAuditTrailService _audit;

    public AuditSaveChangesInterceptor(IAuditTrailService audit) => _audit = audit;

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        AppendAuditEntries(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        AppendAuditEntries(eventData.Context);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    private void AppendAuditEntries(DbContext? context)
    {
        if (AuditTrailScope.IsSuppressed)
            return;

        if (context is not DocoveeDbContext db)
            return;

        var buffer = new List<AuditTrail>();
        _audit.AppendEntityChanges(db, buffer);
        if (buffer.Count == 0)
            return;

        db.AuditTrails.AddRange(buffer);
    }
}
