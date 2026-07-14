using Docovee.DS;
using Docovee.DS.Entities;
using Microsoft.EntityFrameworkCore;

namespace Docovee.BLL.Data;

/// <summary>
/// Keeps insurance carriers/plans catalog current and backfills doctor network links.
/// </summary>
public static class InsuranceCatalogSync
{
    private static readonly (string Code, string Name, string[] Plans)[] Catalog =
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
        foreach (var (code, name, plans) in Catalog)
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

        await db.SaveChangesAsync(cancellationToken);

        // Doctors with no insurance rows get the full active catalog (Zocdoc-style coverage list).
        var carrierIds = await db.InsuranceCarriers.AsNoTracking()
            .Where(c => c.IsActive)
            .Select(c => c.Id)
            .ToListAsync(cancellationToken);

        if (carrierIds.Count == 0)
            return;

        var doctorIdsMissing = await db.Doctors.AsNoTracking()
            .Where(d => d.IsActive && !d.DoctorInsurances.Any())
            .Select(d => d.Id)
            .ToListAsync(cancellationToken);

        foreach (var doctorId in doctorIdsMissing)
        {
            foreach (var carrierId in carrierIds)
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
}
