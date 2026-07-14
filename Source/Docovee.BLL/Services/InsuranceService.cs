using Docovee.DS.Models;
using Docovee.DS;
using Microsoft.EntityFrameworkCore;

namespace Docovee.BLL.Services;

public interface IInsuranceService
{
    Task<IReadOnlyList<InsuranceCarrierDto>> GetCarriersAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<InsuranceCarrierDto>> GetCarriersWithPlansAsync(CancellationToken cancellationToken = default);
}

public class InsuranceService : IInsuranceService
{
    private readonly DocoveeDbContext _db;

    public InsuranceService(DocoveeDbContext db) => _db = db;

    public async Task<IReadOnlyList<InsuranceCarrierDto>> GetCarriersAsync(CancellationToken cancellationToken = default)
    {
        return await _db.InsuranceCarriers
            .AsNoTracking()
            .Where(c => c.IsActive)
            .OrderBy(c => c.Name)
            .Select(c => new InsuranceCarrierDto
            {
                Id = c.Id,
                Name = c.Name,
                Code = c.Code
            })
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<InsuranceCarrierDto>> GetCarriersWithPlansAsync(CancellationToken cancellationToken = default)
    {
        return await _db.InsuranceCarriers
            .AsNoTracking()
            .Where(c => c.IsActive)
            .OrderBy(c => c.Name)
            .Select(c => new InsuranceCarrierDto
            {
                Id = c.Id,
                Name = c.Name,
                Code = c.Code,
                Plans = c.Plans
                    .Where(p => p.IsActive)
                    .OrderBy(p => p.SortOrder)
                    .ThenBy(p => p.Name)
                    .Select(p => new InsurancePlanDto
                    {
                        Id = p.Id,
                        Name = p.Name
                    })
                    .ToList()
            })
            .ToListAsync(cancellationToken);
    }
}
