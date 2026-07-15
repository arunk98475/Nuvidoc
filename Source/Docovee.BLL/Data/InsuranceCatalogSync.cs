using Docovee.DS;
using Docovee.DS.Entities;
using Microsoft.EntityFrameworkCore;

namespace Docovee.BLL.Data;

/// <summary>
/// Keeps insurance carriers/plans catalog current and backfills doctor network links.
/// </summary>
public static class InsuranceCatalogSync
{
    private static readonly (string Code, string Name, string[] Plans)[] CoreCatalog =
    [
        ("AETNA", "Aetna", ["Aetna Dental PPO", "Aetna DMO", "Aetna PPO", "Aetna Dental DHMO"]),
        ("BCBS", "BlueCross BlueShield", ["BlueDental PPO", "FEP BlueDental", "Blue Cross Dental PPO", "Dental Blue"]),
        ("CIGNA", "Cigna", ["Cigna Dental PPO", "Cigna DPPO", "Cigna Total DPPO", "Cigna Dental HMO"]),
        ("UNITED", "UnitedHealthcare", ["UnitedHealthcare Dental PPO", "UnitedHealthcare DHMO", "UnitedHealthcare Dental"]),
        ("DELTA", "Delta Dental", ["Delta Dental PPO", "Delta Dental Premier", "DeltaCare USA"]),
        ("METLIFE", "MetLife", ["MetLife PDP Plus", "MetLife Dental PPO", "MetLife DHMO"]),
        ("GUARDIAN", "Guardian", ["Guardian DentalGuard Preferred", "Guardian Dental PPO"]),
        ("HUMANA", "Humana", ["Humana Dental PPO", "Humana Dental DHMO", "Humana Dental"]),
        ("MEDICARE", "Medicare", ["Medicare Advantage (dental rider)", "Medicare (verify dental coverage)"]),
        ("PRINCIPAL", "Principal", ["Principal Dental PPO", "Principal Dental"])
    ];

    public static async Task SyncAsync(DocoveeDbContext db, CancellationToken cancellationToken = default)
    {
        foreach (var (code, name, plans) in CoreCatalog)
            await UpsertCarrierAsync(db, code, name, plans, cancellationToken);

        var pendingCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in InsuranceCarrierCatalog.Names)
        {
            var baseCode = InsuranceCarrierCatalog.CodeFor(name);
            if (string.IsNullOrWhiteSpace(baseCode))
                continue;

            var existsByName = await db.InsuranceCarriers.AnyAsync(
                c => c.Name == name,
                cancellationToken);
            if (existsByName)
                continue;

            var code = baseCode;
            var suffix = 2;
            while (pendingCodes.Contains(code)
                   || await db.InsuranceCarriers.AnyAsync(c => c.Code == code, cancellationToken))
            {
                var trimmed = baseCode.Length > 36 ? baseCode[..36] : baseCode;
                code = $"{trimmed}_{suffix}";
                suffix++;
            }

            pendingCodes.Add(code);
            db.InsuranceCarriers.Add(new InsuranceCarrier
            {
                Code = code,
                Name = name,
                IsActive = true
            });
        }

        await db.SaveChangesAsync(cancellationToken);

        // Only seed a small core set for doctors missing insurance — not the full catalog.
        var coreCodes = CoreCatalog.Select(c => c.Code).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var coreCarrierIds = await db.InsuranceCarriers.AsNoTracking()
            .Where(c => c.IsActive && coreCodes.Contains(c.Code))
            .Select(c => c.Id)
            .ToListAsync(cancellationToken);

        if (coreCarrierIds.Count == 0)
            return;

        var doctorIdsMissing = await db.Doctors.AsNoTracking()
            .Where(d => d.IsActive && !d.DoctorInsurances.Any())
            .Select(d => d.Id)
            .ToListAsync(cancellationToken);

        foreach (var doctorId in doctorIdsMissing)
        {
            foreach (var carrierId in coreCarrierIds)
            {
                db.DoctorInsurances.Add(new DoctorInsurance
                {
                    DoctorId = doctorId,
                    InsuranceCarrierId = carrierId
                });
            }
        }

        if (doctorIdsMissing.Count > 0)
            await db.SaveChangesAsync(cancellationToken);
    }

    private static async Task UpsertCarrierAsync(
        DocoveeDbContext db,
        string code,
        string name,
        string[] plans,
        CancellationToken cancellationToken)
    {
        var carrier = await db.InsuranceCarriers
            .Include(c => c.Plans)
            .FirstOrDefaultAsync(c => c.Code == code, cancellationToken);

        if (carrier == null)
        {
            carrier = new InsuranceCarrier
            {
                Code = code,
                Name = name,
                IsActive = true
            };
            db.InsuranceCarriers.Add(carrier);
            await db.SaveChangesAsync(cancellationToken);
        }
        else
        {
            if (!string.Equals(carrier.Name, name, StringComparison.Ordinal))
                carrier.Name = name;
            carrier.IsActive = true;
        }

        var sort = 0;
        foreach (var planName in plans)
        {
            sort++;
            var existing = carrier.Plans.FirstOrDefault(p =>
                string.Equals(p.Name, planName, StringComparison.OrdinalIgnoreCase));
            if (existing == null)
            {
                db.InsurancePlans.Add(new InsurancePlan
                {
                    InsuranceCarrierId = carrier.Id,
                    Name = planName,
                    IsActive = true,
                    SortOrder = sort
                });
            }
            else
            {
                existing.IsActive = true;
                existing.SortOrder = sort;
            }
        }
    }
}
