using Docovee.DS;
using Docovee.DS.Entities;
using Docovee.DS.Models;
using Microsoft.EntityFrameworkCore;

namespace Docovee.BLL.Services;

public interface IPatientInsuranceProfileService
{
    Task<PatientInsuranceProfileDto> GetAsync(int patientId, CancellationToken cancellationToken = default);
    Task<(bool Success, string? Error)> SaveAsync(
        int patientId,
        PatientInsuranceSaveModel model,
        CancellationToken cancellationToken = default);
}

public sealed class PatientInsuranceProfileService : IPatientInsuranceProfileService
{
    private readonly DocoveeDbContext _db;

    public PatientInsuranceProfileService(DocoveeDbContext db)
    {
        _db = db;
    }

    public async Task<PatientInsuranceProfileDto> GetAsync(int patientId, CancellationToken cancellationToken = default)
    {
        var patient = await _db.Patients.AsNoTracking()
            .Include(p => p.InsuranceCoverages)
            .ThenInclude(c => c.InsuranceCarrier)
            .Include(p => p.InsuranceCoverages)
            .ThenInclude(c => c.InsurancePlan)
            .FirstOrDefaultAsync(p => p.Id == patientId, cancellationToken);

        var dental = patient?.InsuranceCoverages.FirstOrDefault(c => c.InsuranceType == PatientInsuranceTypes.Dental);
        var rows = new List<PatientInsuranceRowDto>();
        if (dental != null)
        {
            rows.Add(new PatientInsuranceRowDto
            {
                Type = PatientInsuranceTypes.Dental,
                InsuranceCarrierId = dental.InsuranceCarrierId,
                InsuranceCarrierName = dental.InsuranceCarrier?.Name ?? dental.CustomCarrierName,
                InsurancePlanId = dental.InsurancePlanId,
                InsurancePlanName = dental.InsurancePlan?.Name ?? dental.CustomPlanName,
                CustomCarrierName = dental.CustomCarrierName,
                CustomPlanName = dental.CustomPlanName
            });
        }

        return new PatientInsuranceProfileDto
        {
            Coverages = rows
        };
    }

    public async Task<(bool Success, string? Error)> SaveAsync(
        int patientId,
        PatientInsuranceSaveModel model,
        CancellationToken cancellationToken = default)
    {
        var patient = await _db.Patients
            .Include(p => p.InsuranceCoverages)
            .FirstOrDefaultAsync(p => p.Id == patientId, cancellationToken);

        if (patient == null)
            return (false, "Patient not found.");

        if (model.DentalCarrierId.HasValue)
        {
            var valid = await _db.InsuranceCarriers.AsNoTracking()
                .AnyAsync(c => c.IsActive && c.Id == model.DentalCarrierId.Value, cancellationToken);
            if (!valid)
                return (false, "Selected insurance carrier is invalid.");
        }

        if (model.DentalPlanId.HasValue)
        {
            var valid = await _db.InsurancePlans.AsNoTracking()
                .AnyAsync(p => p.IsActive && p.Id == model.DentalPlanId.Value, cancellationToken);
            if (!valid)
                return (false, "Selected insurance plan is invalid.");
        }

        UpsertDentalCoverage(patient, model.DentalCarrierId, model.DentalPlanId);

        await _db.SaveChangesAsync(cancellationToken);
        return (true, null);
    }

    private static void UpsertDentalCoverage(Patient patient, int? carrierId, int? planId)
    {
        var row = patient.InsuranceCoverages.FirstOrDefault(c => c.InsuranceType == PatientInsuranceTypes.Dental);
        if (row == null)
        {
            row = new PatientInsuranceCoverage { InsuranceType = PatientInsuranceTypes.Dental };
            patient.InsuranceCoverages.Add(row);
        }

        row.InsuranceCarrierId = carrierId;
        row.InsurancePlanId = planId;
        row.CustomCarrierName = null;
        row.CustomPlanName = null;
        row.MemberId = null;
        row.CardPhotoUrl = null;
        row.UpdatedAt = DateTime.UtcNow;
    }
}
