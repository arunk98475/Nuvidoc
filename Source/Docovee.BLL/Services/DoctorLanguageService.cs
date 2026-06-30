using Docovee.BLL.Models;
using Docovee.DS;
using Docovee.DS.Entities;
using Docovee.logging;
using Microsoft.EntityFrameworkCore;

namespace Docovee.BLL.Services;

public interface IDoctorLanguageService
{
    Task<IReadOnlyList<string>> GetActiveNamesAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DoctorLanguageDto>> GetAllAsync(CancellationToken cancellationToken = default);
    Task<DoctorLanguageEditModel?> GetForEditAsync(int id, CancellationToken cancellationToken = default);
    Task<(bool Success, string? Error)> CreateAsync(DoctorLanguageEditModel model, CancellationToken cancellationToken = default);
    Task<(bool Success, string? Error)> UpdateAsync(DoctorLanguageEditModel model, CancellationToken cancellationToken = default);
    Task<bool> DeleteAsync(int id, CancellationToken cancellationToken = default);
}

public class DoctorLanguageService : IDoctorLanguageService
{
    private readonly DocoveeDbContext _db;
    private readonly IDocoveeLogger _logger;

    public DoctorLanguageService(DocoveeDbContext db, IDocoveeLogger logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<IReadOnlyList<string>> GetActiveNamesAsync(CancellationToken cancellationToken = default) =>
        await _db.DoctorLanguages.AsNoTracking()
            .Where(l => l.IsActive)
            .OrderBy(l => l.SortOrder)
            .ThenBy(l => l.Name)
            .Select(l => l.Name)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<DoctorLanguageDto>> GetAllAsync(CancellationToken cancellationToken = default) =>
        await _db.DoctorLanguages.AsNoTracking()
            .OrderBy(l => l.SortOrder)
            .ThenBy(l => l.Name)
            .Select(l => new DoctorLanguageDto
            {
                Id = l.Id,
                Name = l.Name,
                SortOrder = l.SortOrder,
                IsActive = l.IsActive
            })
            .ToListAsync(cancellationToken);

    public async Task<DoctorLanguageEditModel?> GetForEditAsync(int id, CancellationToken cancellationToken = default)
    {
        var row = await _db.DoctorLanguages.AsNoTracking().FirstOrDefaultAsync(l => l.Id == id, cancellationToken);
        if (row == null) return null;
        return new DoctorLanguageEditModel
        {
            Id = row.Id,
            Name = row.Name,
            SortOrder = row.SortOrder,
            IsActive = row.IsActive
        };
    }

    public async Task<(bool Success, string? Error)> CreateAsync(DoctorLanguageEditModel model, CancellationToken cancellationToken = default)
    {
        var name = model.Name?.Trim();
        if (string.IsNullOrWhiteSpace(name))
            return (false, "Language name is required.");

        if (await _db.DoctorLanguages.AnyAsync(l => l.Name == name, cancellationToken))
            return (false, "That language already exists.");

        _db.DoctorLanguages.Add(new DoctorLanguage
        {
            Name = name,
            SortOrder = model.SortOrder,
            IsActive = model.IsActive
        });
        await _db.SaveChangesAsync(cancellationToken);
        _logger.LogInformation("Admin created doctor language {Name}", name);
        return (true, null);
    }

    public async Task<(bool Success, string? Error)> UpdateAsync(DoctorLanguageEditModel model, CancellationToken cancellationToken = default)
    {
        var row = await _db.DoctorLanguages.FirstOrDefaultAsync(l => l.Id == model.Id, cancellationToken);
        if (row == null) return (false, "Language not found.");

        var name = model.Name?.Trim();
        if (string.IsNullOrWhiteSpace(name))
            return (false, "Language name is required.");

        if (await _db.DoctorLanguages.AnyAsync(l => l.Name == name && l.Id != model.Id, cancellationToken))
            return (false, "That language already exists.");

        row.Name = name;
        row.SortOrder = model.SortOrder;
        row.IsActive = model.IsActive;
        await _db.SaveChangesAsync(cancellationToken);
        return (true, null);
    }

    public async Task<bool> DeleteAsync(int id, CancellationToken cancellationToken = default)
    {
        var row = await _db.DoctorLanguages.FirstOrDefaultAsync(l => l.Id == id, cancellationToken);
        if (row == null) return false;
        _db.DoctorLanguages.Remove(row);
        await _db.SaveChangesAsync(cancellationToken);
        return true;
    }
}
