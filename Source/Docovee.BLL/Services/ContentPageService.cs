using Docovee.DS;
using Docovee.DS.Entities;
using Microsoft.EntityFrameworkCore;

namespace Docovee.BLL.Services;

public interface IContentPageService
{
    Task<IReadOnlyList<ContentPage>> GetAllAsync(CancellationToken ct = default);
    Task<IReadOnlyList<ContentPage>> GetPublishedByTypeAsync(string pageType, CancellationToken ct = default);
    Task<ContentPage?> GetByIdAsync(int id, CancellationToken ct = default);
    Task<ContentPage?> GetBySlugAsync(string slug, CancellationToken ct = default);
    Task<ContentPage> CreateAsync(ContentPage page, CancellationToken ct = default);
    Task<ContentPage> UpdateAsync(ContentPage page, CancellationToken ct = default);
    Task DeleteAsync(int id, CancellationToken ct = default);
    Task<bool> SlugExistsAsync(string slug, int? excludeId = null, CancellationToken ct = default);
}

public class ContentPageService : IContentPageService
{
    private readonly DocoveeDbContext _db;

    public ContentPageService(DocoveeDbContext db) => _db = db;

    public async Task<IReadOnlyList<ContentPage>> GetAllAsync(CancellationToken ct = default) =>
        await _db.ContentPages
            .OrderByDescending(p => p.UpdatedAtUtc)
            .ToListAsync(ct);

    public async Task<IReadOnlyList<ContentPage>> GetPublishedByTypeAsync(string pageType, CancellationToken ct = default) =>
        await _db.ContentPages
            .Where(p => p.PageType == pageType && p.IsPublished)
            .OrderByDescending(p => p.UpdatedAtUtc)
            .ToListAsync(ct);

    public Task<ContentPage?> GetByIdAsync(int id, CancellationToken ct = default) =>
        _db.ContentPages.FirstOrDefaultAsync(p => p.Id == id, ct);

    public Task<ContentPage?> GetBySlugAsync(string slug, CancellationToken ct = default) =>
        _db.ContentPages.FirstOrDefaultAsync(p => p.Slug == slug, ct);

    public async Task<ContentPage> CreateAsync(ContentPage page, CancellationToken ct = default)
    {
        page.CreatedAtUtc = DateTime.UtcNow;
        page.UpdatedAtUtc = DateTime.UtcNow;
        _db.ContentPages.Add(page);
        await _db.SaveChangesAsync(ct);
        return page;
    }

    public async Task<ContentPage> UpdateAsync(ContentPage page, CancellationToken ct = default)
    {
        page.UpdatedAtUtc = DateTime.UtcNow;
        _db.ContentPages.Update(page);
        await _db.SaveChangesAsync(ct);
        return page;
    }

    public async Task DeleteAsync(int id, CancellationToken ct = default)
    {
        var page = await _db.ContentPages.FindAsync([id], ct);
        if (page is not null)
        {
            _db.ContentPages.Remove(page);
            await _db.SaveChangesAsync(ct);
        }
    }

    public async Task<bool> SlugExistsAsync(string slug, int? excludeId = null, CancellationToken ct = default) =>
        await _db.ContentPages.AnyAsync(
            p => p.Slug == slug && (excludeId == null || p.Id != excludeId.Value),
            ct);
}
