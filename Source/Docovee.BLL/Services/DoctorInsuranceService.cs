using Docovee.DS;
using Docovee.DS.Entities;
using Docovee.DS.Models;
using Microsoft.EntityFrameworkCore;

namespace Docovee.BLL.Services;

public interface IDoctorInsuranceService
{
    Task<IReadOnlyList<DoctorInsuranceRowDto>> GetDoctorInsurancesAsync(int doctorId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<InsuranceCarrierDto>> GetAvailableCarriersAsync(int doctorId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<SelectableInsuranceCarrierDto>> GetSelectableCarriersAsync(int doctorId, CancellationToken cancellationToken = default);
    Task<(bool Success, string? Error)> AddCarriersAsync(int doctorId, IReadOnlyList<int> carrierIds, CancellationToken cancellationToken = default);
    Task<(bool Success, string? Error)> SetCarriersAsync(int doctorId, IReadOnlyList<int> carrierIds, CancellationToken cancellationToken = default);
    Task<(bool Success, string? Error)> RemoveCarrierAsync(int doctorId, int carrierId, CancellationToken cancellationToken = default);
}

public class DoctorInsuranceService : IDoctorInsuranceService
{
    private readonly DocoveeDbContext _db;

    public DoctorInsuranceService(DocoveeDbContext db) => _db = db;

    public async Task<IReadOnlyList<DoctorInsuranceRowDto>> GetDoctorInsurancesAsync(
        int doctorId,
        CancellationToken cancellationToken = default)
    {
        var rows = await _db.DoctorInsurances.AsNoTracking()
            .Where(di => di.DoctorId == doctorId)
            .Select(di => new
            {
                di.InsuranceCarrierId,
                CarrierName = di.InsuranceCarrier.Name,
                Plans = di.InsuranceCarrier.Plans
                    .Where(p => p.IsActive)
                    .OrderBy(p => p.SortOrder)
                    .Select(p => p.Name)
                    .ToList()
            })
            .OrderBy(x => x.CarrierName)
            .ToListAsync(cancellationToken);

        return rows.Select(r =>
        {
            var programs = InferPrograms(r.CarrierName, r.Plans);
            var planTypes = InferPlanTypes(r.Plans);
            return new DoctorInsuranceRowDto
            {
                CarrierId = r.InsuranceCarrierId,
                CarrierName = r.CarrierName,
                Programs = programs,
                PlanTypes = planTypes,
                PlanNames = r.Plans,
                AcceptedPrograms = programs.Count == 0 ? "—" : string.Join(", ", programs),
                AcceptedPlanTypes = planTypes.Count == 0 ? "—" : string.Join(", ", planTypes)
            };
        }).ToList();
    }

    public async Task<IReadOnlyList<InsuranceCarrierDto>> GetAvailableCarriersAsync(
        int doctorId,
        CancellationToken cancellationToken = default)
    {
        var acceptedIds = await _db.DoctorInsurances.AsNoTracking()
            .Where(di => di.DoctorId == doctorId)
            .Select(di => di.InsuranceCarrierId)
            .ToListAsync(cancellationToken);

        return await _db.InsuranceCarriers.AsNoTracking()
            .Where(c => c.IsActive && !acceptedIds.Contains(c.Id))
            .OrderBy(c => c.Name)
            .Select(c => new InsuranceCarrierDto
            {
                Id = c.Id,
                Name = c.Name,
                Code = c.Code
            })
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<SelectableInsuranceCarrierDto>> GetSelectableCarriersAsync(
        int doctorId,
        CancellationToken cancellationToken = default)
    {
        var acceptedIds = await _db.DoctorInsurances.AsNoTracking()
            .Where(di => di.DoctorId == doctorId)
            .Select(di => di.InsuranceCarrierId)
            .ToListAsync(cancellationToken);
        var accepted = acceptedIds.ToHashSet();

        var carriers = await _db.InsuranceCarriers.AsNoTracking()
            .Where(c => c.IsActive)
            .OrderBy(c => c.Name)
            .Select(c => new { c.Id, c.Name, c.Code })
            .ToListAsync(cancellationToken);

        return carriers.Select(c => new SelectableInsuranceCarrierDto
        {
            Id = c.Id,
            Name = c.Name,
            Code = c.Code,
            Selected = accepted.Contains(c.Id)
        }).ToList();
    }

    public async Task<(bool Success, string? Error)> AddCarriersAsync(
        int doctorId,
        IReadOnlyList<int> carrierIds,
        CancellationToken cancellationToken = default)
    {
        if (carrierIds.Count == 0)
            return (false, "Select at least one carrier.");

        var doctorExists = await _db.Doctors.AnyAsync(d => d.Id == doctorId, cancellationToken);
        if (!doctorExists)
            return (false, "Doctor not found.");

        var existing = await _db.DoctorInsurances
            .Where(di => di.DoctorId == doctorId)
            .Select(di => di.InsuranceCarrierId)
            .ToListAsync(cancellationToken);
        var existingSet = existing.ToHashSet();

        var validIds = await _db.InsuranceCarriers.AsNoTracking()
            .Where(c => c.IsActive && carrierIds.Contains(c.Id))
            .Select(c => c.Id)
            .ToListAsync(cancellationToken);

        var toAdd = validIds.Where(id => !existingSet.Contains(id)).Distinct().ToList();
        if (toAdd.Count == 0)
            return (false, "Those carriers are already added.");

        foreach (var id in toAdd)
        {
            _db.DoctorInsurances.Add(new DoctorInsurance
            {
                DoctorId = doctorId,
                InsuranceCarrierId = id
            });
        }

        await _db.SaveChangesAsync(cancellationToken);
        return (true, null);
    }

    public async Task<(bool Success, string? Error)> SetCarriersAsync(
        int doctorId,
        IReadOnlyList<int> carrierIds,
        CancellationToken cancellationToken = default)
    {
        var doctor = await _db.Doctors
            .Include(d => d.DoctorInsurances)
            .FirstOrDefaultAsync(d => d.Id == doctorId, cancellationToken);
        if (doctor == null)
            return (false, "Doctor not found.");

        var validIds = await _db.InsuranceCarriers.AsNoTracking()
            .Where(c => c.IsActive && carrierIds.Contains(c.Id))
            .Select(c => c.Id)
            .Distinct()
            .ToListAsync(cancellationToken);

        _db.DoctorInsurances.RemoveRange(doctor.DoctorInsurances);
        foreach (var id in validIds)
        {
            _db.DoctorInsurances.Add(new DoctorInsurance
            {
                DoctorId = doctorId,
                InsuranceCarrierId = id
            });
        }

        await _db.SaveChangesAsync(cancellationToken);
        return (true, null);
    }

    public async Task<(bool Success, string? Error)> RemoveCarrierAsync(
        int doctorId,
        int carrierId,
        CancellationToken cancellationToken = default)
    {
        var row = await _db.DoctorInsurances
            .FirstOrDefaultAsync(di => di.DoctorId == doctorId && di.InsuranceCarrierId == carrierId, cancellationToken);
        if (row == null)
            return (false, "Carrier not found.");

        _db.DoctorInsurances.Remove(row);
        await _db.SaveChangesAsync(cancellationToken);
        return (true, null);
    }

    private static List<string> InferPrograms(string carrierName, IReadOnlyList<string> plans)
    {
        var programs = new List<string>();
        var blob = string.Join(" ", plans.Prepend(carrierName));

        if (ContainsAny(blob, "medicare", "medicaid", "advantage"))
        {
            if (ContainsAny(blob, "medicare", "advantage"))
                programs.Add("Medicare");
            if (ContainsAny(blob, "medicaid"))
                programs.Add("Medicaid");
        }

        if (programs.Count == 0 || ContainsAny(blob, "ppo", "hmo", "dhmo", "premier", "dental"))
        {
            if (!programs.Contains("Commercial"))
                programs.Insert(0, "Commercial");
        }

        return programs;
    }

    private static List<string> InferPlanTypes(IReadOnlyList<string> plans)
    {
        var types = new List<string>();
        void Add(string type)
        {
            if (!types.Contains(type, StringComparer.OrdinalIgnoreCase))
                types.Add(type);
        }

        foreach (var plan in plans)
        {
            var lower = plan.ToLowerInvariant();
            if (lower.Contains("dppo") || lower.Contains("ppo"))
                Add("PPO");
            if (lower.Contains("dhmo") || (lower.Contains("hmo") && !lower.Contains("dhmo")))
            {
                if (lower.Contains("dhmo"))
                    Add("DHMO");
                else
                    Add("HMO");
            }
            if (lower.Contains("premier"))
                Add("Premier");
            if (lower.Contains("indemnity"))
                Add("Indemnity");
        }

        if (types.Count == 0 && plans.Count > 0)
            Add("PPO");

        return types;
    }

    private static bool ContainsAny(string text, params string[] needles)
    {
        var lower = text.ToLowerInvariant();
        return needles.Any(n => lower.Contains(n));
    }
}
