using Docovee.DS.Models;
using Docovee.DS;
using Docovee.DS.Entities;
using Docovee.logging;
using Microsoft.EntityFrameworkCore;

namespace Docovee.BLL.Services;

public interface IDoctorLanguageService
{
    Task<IReadOnlyList<string>> GetActiveNamesAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DoctorLanguageDto>> GetAllAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DoctorLanguageDto>> GetActiveCatalogAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DoctorLanguageDto>> GetDoctorLanguagesAsync(
        int doctorId,
        CancellationToken cancellationToken = default);
    Task<(bool Success, string? Error, DoctorLanguageDto? Language)> AddDoctorLanguageFromTextAsync(
        int doctorId,
        string freeText,
        CancellationToken cancellationToken = default);
    Task<(bool Success, string? Error)> RemoveDoctorLanguageAsync(
        int doctorId,
        int languageId,
        CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DoctorLanguageSpeakerDto>> GetDoctorsSpeakingAsync(
        int languageId,
        CancellationToken cancellationToken = default);
    Task<DoctorLanguageEditModel?> GetForEditAsync(int id, CancellationToken cancellationToken = default);
    Task<(bool Success, string? Error)> CreateAsync(DoctorLanguageEditModel model, CancellationToken cancellationToken = default);
    Task<(bool Success, string? Error)> UpdateAsync(DoctorLanguageEditModel model, CancellationToken cancellationToken = default);
    Task<bool> DeleteAsync(int id, CancellationToken cancellationToken = default);
}

public class DoctorLanguageService : IDoctorLanguageService
{
    private readonly DocoveeDbContext _db;
    private readonly IDocoveeLogger _logger;
    private readonly IAnthropicValidationService _validation;

    public DoctorLanguageService(
        DocoveeDbContext db,
        IDocoveeLogger logger,
        IAnthropicValidationService validation)
    {
        _db = db;
        _logger = logger;
        _validation = validation;
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
                IsActive = l.IsActive,
                DoctorCount = l.Doctors.Count(d => !d.Doctor.IsDeleted)
            })
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<DoctorLanguageDto>> GetActiveCatalogAsync(CancellationToken cancellationToken = default) =>
        await _db.DoctorLanguages.AsNoTracking()
            .Where(l => l.IsActive)
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

    public async Task<IReadOnlyList<DoctorLanguageDto>> GetDoctorLanguagesAsync(
        int doctorId,
        CancellationToken cancellationToken = default) =>
        await _db.DoctorDoctorLanguages.AsNoTracking()
            .Where(ddl => ddl.DoctorId == doctorId
                && ddl.DoctorLanguage.Name.ToLower() != "english")
            .OrderBy(ddl => ddl.DoctorLanguage.SortOrder)
            .ThenBy(ddl => ddl.DoctorLanguage.Name)
            .Select(ddl => new DoctorLanguageDto
            {
                Id = ddl.DoctorLanguage.Id,
                Name = ddl.DoctorLanguage.Name,
                SortOrder = ddl.DoctorLanguage.SortOrder,
                IsActive = ddl.DoctorLanguage.IsActive
            })
            .ToListAsync(cancellationToken);

    public async Task<(bool Success, string? Error, DoctorLanguageDto? Language)> AddDoctorLanguageFromTextAsync(
        int doctorId,
        string freeText,
        CancellationToken cancellationToken = default)
    {
        var doctorExists = await _db.Doctors.AnyAsync(d => d.Id == doctorId && !d.IsDeleted, cancellationToken);
        if (!doctorExists)
            return (false, "Doctor not found.", null);

        if (string.IsNullOrWhiteSpace(freeText))
            return (false, "Please type a language name.", null);

        var knownNames = await GetActiveNamesAsync(cancellationToken);
        var extraction = await _validation.ExtractLanguageNameAsync(freeText, knownNames, cancellationToken);
        if (!extraction.IsValid || string.IsNullOrWhiteSpace(extraction.NormalizedAnswer))
            return (false, extraction.RepromptMessage ?? "Could not recognize that language. Try again.", null);

        var name = extraction.NormalizedAnswer.Trim();
        if (name.Equals("English", StringComparison.OrdinalIgnoreCase))
            return (false, "English is assumed — add other languages you speak.", null);

        var language = await _db.DoctorLanguages
            .FirstOrDefaultAsync(l => l.Name.ToLower() == name.ToLower(), cancellationToken);
        if (language == null)
        {
            var nextSort = await _db.DoctorLanguages.MaxAsync(l => (int?)l.SortOrder, cancellationToken) ?? 0;
            language = new DoctorLanguage
            {
                Name = name,
                SortOrder = nextSort + 1,
                IsActive = true
            };
            _db.DoctorLanguages.Add(language);
            await _db.SaveChangesAsync(cancellationToken);
            _logger.LogInformation("Doctor {DoctorId} created language catalog entry {Name}", doctorId, name);
        }
        else if (!language.IsActive)
        {
            language.IsActive = true;
            await _db.SaveChangesAsync(cancellationToken);
        }

        var alreadyLinked = await _db.DoctorDoctorLanguages.AnyAsync(
            ddl => ddl.DoctorId == doctorId && ddl.DoctorLanguageId == language.Id,
            cancellationToken);
        if (alreadyLinked)
        {
            return (true, null, new DoctorLanguageDto
            {
                Id = language.Id,
                Name = language.Name,
                SortOrder = language.SortOrder,
                IsActive = language.IsActive
            });
        }

        _db.DoctorDoctorLanguages.Add(new DoctorDoctorLanguage
        {
            DoctorId = doctorId,
            DoctorLanguageId = language.Id
        });
        await _db.SaveChangesAsync(cancellationToken);

        return (true, null, new DoctorLanguageDto
        {
            Id = language.Id,
            Name = language.Name,
            SortOrder = language.SortOrder,
            IsActive = language.IsActive
        });
    }

    public async Task<(bool Success, string? Error)> RemoveDoctorLanguageAsync(
        int doctorId,
        int languageId,
        CancellationToken cancellationToken = default)
    {
        var link = await _db.DoctorDoctorLanguages
            .Include(ddl => ddl.DoctorLanguage)
            .FirstOrDefaultAsync(
                ddl => ddl.DoctorId == doctorId && ddl.DoctorLanguageId == languageId,
                cancellationToken);
        if (link == null)
            return (false, "Language is not on your profile.");

        if (link.DoctorLanguage.Name.Equals("English", StringComparison.OrdinalIgnoreCase))
            return (false, "English cannot be removed.");

        _db.DoctorDoctorLanguages.Remove(link);
        await _db.SaveChangesAsync(cancellationToken);
        return (true, null);
    }

    public async Task<IReadOnlyList<DoctorLanguageSpeakerDto>> GetDoctorsSpeakingAsync(
        int languageId,
        CancellationToken cancellationToken = default) =>
        await _db.DoctorDoctorLanguages.AsNoTracking()
            .Where(ddl => ddl.DoctorLanguageId == languageId && !ddl.Doctor.IsDeleted)
            .OrderBy(ddl => ddl.Doctor.Name)
            .Select(ddl => new DoctorLanguageSpeakerDto
            {
                Id = ddl.Doctor.Id,
                Name = ddl.Doctor.Name,
                PracticeName = ddl.Doctor.PracticeName,
                Specialty = ddl.Doctor.Specialty,
                City = ddl.Doctor.City,
                State = ddl.Doctor.State
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
