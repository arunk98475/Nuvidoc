using Docovee.DS;
using AuditEntry = Docovee.DS.Entities.AuditTrail;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Docovee.Pages.Admin.AuditTrail;

public class IndexModel : PageModel
{
    private readonly DocoveeDbContext _db;

    public IndexModel(DocoveeDbContext db) => _db = db;

    public IReadOnlyList<AuditEntry> Rows { get; private set; } = Array.Empty<AuditEntry>();
    public int PageNum { get; private set; } = 1;
    public int TotalCount { get; private set; }
    public int PageSize { get; } = 50;

    [BindProperty(SupportsGet = true)]
    public string? EntityType { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Action { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Actor { get; set; }

    public async Task OnGetAsync(int page = 1, CancellationToken cancellationToken = default)
    {
        PageNum = Math.Max(1, page);

        var query = _db.AuditTrails.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(EntityType))
            query = query.Where(a => a.EntityType == EntityType.Trim());

        if (!string.IsNullOrWhiteSpace(Action))
            query = query.Where(a => a.Action == Action.Trim());

        if (!string.IsNullOrWhiteSpace(Actor))
        {
            var term = Actor.Trim();
            query = query.Where(a =>
                (a.ActorUsername != null && a.ActorUsername.Contains(term))
                || (a.ActorUserId != null && a.ActorUserId.Contains(term)));
        }

        TotalCount = await query.CountAsync(cancellationToken);

        Rows = await query
            .OrderByDescending(a => a.OccurredAtUtc)
            .Skip((PageNum - 1) * PageSize)
            .Take(PageSize)
            .ToListAsync(cancellationToken);
    }
}
