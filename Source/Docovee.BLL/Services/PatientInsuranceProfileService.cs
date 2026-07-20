using Docovee.DS;
using Docovee.DS.Entities;
using Docovee.DS.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace Docovee.BLL.Services;

public interface IPatientInsuranceProfileService
{
    Task<PatientInsuranceProfileDto> GetAsync(int patientId, CancellationToken cancellationToken = default);
    Task<(bool Success, string? Error)> SaveAsync(
        int patientId,
        PatientInsuranceSaveModel model,
        IFormFile? medicalCardPhoto,
        IFormFile? idCardPhoto,
        CancellationToken cancellationToken = default);
}

public sealed class PatientInsuranceProfileService : IPatientInsuranceProfileService
{
    private readonly DocoveeDbContext _db;
    private readonly IPatientFileService _files;

    public PatientInsuranceProfileService(DocoveeDbContext db, IPatientFileService files)
    {
        _db = db;
        _files = files;
    }

    public async Task<PatientInsuranceProfileDto> GetAsync(int patientId, CancellationToken cancellationToken = default)
    {
        var patient = await _db.Patients.AsNoTracking()
            .Include(p => p.InsuranceCoverages)
            .ThenInclude(c => c.InsuranceCarrier)
            .Include(p => p.InsuranceCoverages)
            .ThenInclude(c => c.InsurancePlan)
            .FirstOrDefaultAsync(p => p.Id == patientId, cancellationToken);

        var rows = PatientInsuranceTypes.All.Select(type =>
        {
            var row = patient?.InsuranceCoverages.FirstOrDefault(c => c.InsuranceType == type);
            return new PatientInsuranceRowDto
            {
                Type = type,
                InsuranceCarrierId = row?.InsuranceCarrierId,
                InsuranceCarrierName = row?.InsuranceCarrier?.Name ?? row?.CustomCarrierName,
                InsurancePlanId = row?.InsurancePlanId,
                InsurancePlanName = row?.InsurancePlan?.Name ?? row?.CustomPlanName,
                CustomCarrierName = row?.CustomCarrierName,
                CustomPlanName = row?.CustomPlanName,
                MemberId = row?.MemberId,
                CardPhotoUrl = row?.CardPhotoUrl
            };
        }).ToList();

        return new PatientInsuranceProfileDto
        {
            Coverages = rows,
            IdCardPhotoUrl = patient?.IdCardPhotoUrl
        };
    }

    public async Task<(bool Success, string? Error)> SaveAsync(
        int patientId,
        PatientInsuranceSaveModel model,
        IFormFile? medicalCardPhoto,
        IFormFile? idCardPhoto,
        CancellationToken cancellationToken = default)
    {
        var patient = await _db.Patients
            .Include(p => p.InsuranceCoverages)
            .FirstOrDefaultAsync(p => p.Id == patientId, cancellationToken);

        if (patient == null)
            return (false, "Patient not found.");

        var carrierIds = new[]
            {
                model.MedicalCarrierId,
                model.DentalCarrierId,
                model.VisionCarrierId
            }
            .Where(id => id.HasValue)
            .Select(id => id!.Value)
            .Distinct()
            .ToList();

        var planIds = new[]
            {
                model.MedicalPlanId,
                model.DentalPlanId,
                model.VisionPlanId
            }
            .Where(id => id.HasValue)
            .Select(id => id!.Value)
            .Distinct()
            .ToList();

        if (carrierIds.Count > 0)
        {
            var validCarrierIds = await _db.InsuranceCarriers.AsNoTracking()
                .Where(c => c.IsActive && carrierIds.Contains(c.Id))
                .Select(c => c.Id)
                .ToListAsync(cancellationToken);

            if (validCarrierIds.Count != carrierIds.Count)
                return (false, "One or more selected insurance carriers are invalid.");
        }

        if (planIds.Count > 0)
        {
            var validPlanIds = await _db.InsurancePlans.AsNoTracking()
                .Where(p => p.IsActive && planIds.Contains(p.Id))
                .Select(p => p.Id)
                .ToListAsync(cancellationToken);

            if (validPlanIds.Count != planIds.Count)
                return (false, "One or more selected insurance plans are invalid.");
        }

        UpsertCoverage(patient, PatientInsuranceTypes.Medical, model.MedicalCarrierId, model.MedicalPlanId, null, null, model.MedicalMemberId);
        UpsertCoverage(patient, PatientInsuranceTypes.Dental, model.DentalCarrierId, model.DentalPlanId, null, null, model.DentalMemberId);
        UpsertCoverage(patient, PatientInsuranceTypes.Vision, model.VisionCarrierId, model.VisionPlanId, null, null, model.VisionMemberId);
        UpsertCoverage(
            patient,
            PatientInsuranceTypes.Secondary,
            null,
            null,
            Trim(model.SecondaryCarrierName),
            Trim(model.SecondaryPlanName),
            model.SecondaryMemberId);

        var medical = patient.InsuranceCoverages.First(c => c.InsuranceType == PatientInsuranceTypes.Medical);
        if (medicalCardPhoto != null && medicalCardPhoto.Length > 0)
        {
            var url = await _files.SaveInsuranceCardPhotoAsync(medicalCardPhoto, cancellationToken);
            if (url == null)
                return (false, "Medical insurance card photo must be JPG, PNG, WEBP, or GIF under 10 MB.");
            medical.CardPhotoUrl = url;
            medical.UpdatedAt = DateTime.UtcNow;
        }

        if (idCardPhoto != null && idCardPhoto.Length > 0)
        {
            var url = await _files.SaveIdCardPhotoAsync(idCardPhoto, cancellationToken);
            if (url == null)
                return (false, "ID card photo must be JPG, PNG, WEBP, or GIF under 10 MB.");
            patient.IdCardPhotoUrl = url;
        }

        await _db.SaveChangesAsync(cancellationToken);
        return (true, null);
    }

    private static void UpsertCoverage(
        Patient patient,
        string type,
        int? carrierId,
        int? planId,
        string? customCarrier,
        string? customPlan,
        string? memberId)
    {
        var row = patient.InsuranceCoverages.FirstOrDefault(c => c.InsuranceType == type);
        if (row == null)
        {
            row = new PatientInsuranceCoverage { InsuranceType = type };
            patient.InsuranceCoverages.Add(row);
        }

        if (type == PatientInsuranceTypes.Secondary)
        {
            row.InsuranceCarrierId = null;
            row.InsurancePlanId = null;
            row.CustomCarrierName = customCarrier;
            row.CustomPlanName = customPlan;
        }
        else
        {
            row.InsuranceCarrierId = carrierId;
            row.InsurancePlanId = planId;
            row.CustomCarrierName = null;
            row.CustomPlanName = null;
        }

        row.MemberId = Trim(memberId);
        row.UpdatedAt = DateTime.UtcNow;
    }

    private static string? Trim(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
