using Docovee.DS;
using Docovee.DS.Entities;
using Docovee.DS.Models;
using Microsoft.EntityFrameworkCore;

namespace Docovee.BLL.Services;

public interface IDoctorPracticeFeeService
{
    Task<IReadOnlyList<DoctorPracticeFeeDto>> ListAsync(int doctorId, CancellationToken cancellationToken = default);
    Task<(bool Success, string? Error)> AddAsync(int doctorId, string procedureName, decimal feeUsd, CancellationToken cancellationToken = default);
    Task<(bool Success, string? Error)> UpdateAsync(int doctorId, int feeId, string procedureName, decimal feeUsd, CancellationToken cancellationToken = default);
    Task<(bool Success, string? Error)> DeleteAsync(int doctorId, int feeId, CancellationToken cancellationToken = default);
    /// <summary>Minimum procedure fee cents per doctor id. Doctors with no fees are omitted.</summary>
    Task<IReadOnlyDictionary<int, int>> GetMinFeeCentsByDoctorIdsAsync(IEnumerable<int> doctorIds, CancellationToken cancellationToken = default);
}

public class DoctorPracticeFeeService : IDoctorPracticeFeeService
{
    private readonly DocoveeDbContext _db;

    public DoctorPracticeFeeService(DocoveeDbContext db) => _db = db;

    public async Task<IReadOnlyList<DoctorPracticeFeeDto>> ListAsync(int doctorId, CancellationToken cancellationToken = default)
    {
        var rows = await _db.DoctorPracticeFees.AsNoTracking()
            .Where(f => f.DoctorId == doctorId)
            .OrderBy(f => f.ProcedureName)
            .ThenBy(f => f.Id)
            .ToListAsync(cancellationToken);

        return rows.Select(MapToDto).ToList();
    }

    public async Task<(bool Success, string? Error)> AddAsync(
        int doctorId,
        string procedureName,
        decimal feeUsd,
        CancellationToken cancellationToken = default)
    {
        var (name, cents, error) = Validate(procedureName, feeUsd);
        if (error != null)
            return (false, error);

        var exists = await _db.Doctors.AnyAsync(d => d.Id == doctorId && !d.IsDeleted, cancellationToken);
        if (!exists)
            return (false, "Doctor not found.");

        var now = DateTime.UtcNow;
        _db.DoctorPracticeFees.Add(new DoctorPracticeFee
        {
            DoctorId = doctorId,
            ProcedureName = name!,
            ProcedureFeeCents = cents,
            CreatedAt = now,
            UpdatedAt = now
        });
        await _db.SaveChangesAsync(cancellationToken);
        return (true, null);
    }

    public async Task<(bool Success, string? Error)> UpdateAsync(
        int doctorId,
        int feeId,
        string procedureName,
        decimal feeUsd,
        CancellationToken cancellationToken = default)
    {
        var (name, cents, error) = Validate(procedureName, feeUsd);
        if (error != null)
            return (false, error);

        var row = await _db.DoctorPracticeFees
            .FirstOrDefaultAsync(f => f.Id == feeId && f.DoctorId == doctorId, cancellationToken);
        if (row == null)
            return (false, "Procedure fee not found.");

        row.ProcedureName = name!;
        row.ProcedureFeeCents = cents;
        row.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);
        return (true, null);
    }

    public async Task<(bool Success, string? Error)> DeleteAsync(
        int doctorId,
        int feeId,
        CancellationToken cancellationToken = default)
    {
        var row = await _db.DoctorPracticeFees
            .FirstOrDefaultAsync(f => f.Id == feeId && f.DoctorId == doctorId, cancellationToken);
        if (row == null)
            return (false, "Procedure fee not found.");

        _db.DoctorPracticeFees.Remove(row);
        await _db.SaveChangesAsync(cancellationToken);
        return (true, null);
    }

    public async Task<IReadOnlyDictionary<int, int>> GetMinFeeCentsByDoctorIdsAsync(
        IEnumerable<int> doctorIds,
        CancellationToken cancellationToken = default)
    {
        var ids = doctorIds.Distinct().ToList();
        if (ids.Count == 0)
            return new Dictionary<int, int>();

        var rows = await _db.DoctorPracticeFees.AsNoTracking()
            .Where(f => ids.Contains(f.DoctorId))
            .GroupBy(f => f.DoctorId)
            .Select(g => new { DoctorId = g.Key, MinCents = g.Min(x => x.ProcedureFeeCents) })
            .ToListAsync(cancellationToken);

        return rows.ToDictionary(x => x.DoctorId, x => x.MinCents);
    }

    private static (string? Name, int Cents, string? Error) Validate(string procedureName, decimal feeUsd)
    {
        var name = procedureName?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(name))
            return (null, 0, "Procedure name is required.");
        if (name.Length > 200)
            return (null, 0, "Procedure name must be 200 characters or fewer.");
        if (feeUsd < 0)
            return (null, 0, "Fee must be zero or greater.");

        var cents = (int)Math.Round(feeUsd * 100m, MidpointRounding.AwayFromZero);
        return (name, cents, null);
    }

    private static DoctorPracticeFeeDto MapToDto(DoctorPracticeFee row) => new()
    {
        Id = row.Id,
        DoctorId = row.DoctorId,
        ProcedureName = row.ProcedureName,
        ProcedureFeeCents = row.ProcedureFeeCents,
        UpdatedAt = row.UpdatedAt
    };
}
