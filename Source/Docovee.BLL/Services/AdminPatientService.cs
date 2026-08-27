using Docovee.BLL.Audit;
using Docovee.BLL.Configuration;
using Docovee.DS;
using Docovee.DS.Entities;
using Docovee.DS.Models;
using Docovee.logging;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Docovee.BLL.Services;

public interface IAdminPatientService
{
    Task<PagedResult<PatientAdminDto>> SearchAsync(PatientSearchRequest request, CancellationToken cancellationToken = default);
    Task<PatientAdminEditModel?> GetForEditAsync(int id, CancellationToken cancellationToken = default);
    Task<(bool Success, string? Error)> CreateAsync(PatientAdminEditModel model, CancellationToken cancellationToken = default);
    Task<(bool Success, string? Error)> UpdateAsync(PatientAdminEditModel model, CancellationToken cancellationToken = default);
    Task<(bool Success, string? Error)> SoftDeleteAsync(int id, CancellationToken cancellationToken = default);
    Task<(bool Success, string? Error)> ActivateAsync(int id, CancellationToken cancellationToken = default);
    Task<(bool Success, string? Error)> HardDeleteAsync(int id, CancellationToken cancellationToken = default);
}

public class AdminPatientService : IAdminPatientService
{
    private readonly DocoveeDbContext _db;
    private readonly IDocoveeLogger _logger;
    private readonly IAuditTrailService _audit;
    private readonly IPatientPrivacyRightsService _privacyRights;
    private readonly AccountOptions _account;
    private readonly PasswordHasher<Patient> _passwordHasher = new();

    public AdminPatientService(
        DocoveeDbContext db,
        IDocoveeLogger logger,
        IAuditTrailService audit,
        IPatientPrivacyRightsService privacyRights,
        IOptions<AccountOptions> account)
    {
        _db = db;
        _logger = logger;
        _audit = audit;
        _privacyRights = privacyRights;
        _account = account.Value;
    }

    public async Task<PagedResult<PatientAdminDto>> SearchAsync(PatientSearchRequest request, CancellationToken cancellationToken = default)
    {
        var page = Math.Max(1, request.Page);
        var pageSize = Math.Clamp(request.PageSize, 1, 100);

        var query = _db.Patients.AsNoTracking().AsQueryable();

        if (!string.IsNullOrWhiteSpace(request.Name))
        {
            var name = request.Name.Trim().ToLowerInvariant();
            query = query.Where(p => p.FullName.ToLower().Contains(name) || p.Username.ToLower().Contains(name));
        }

        if (!string.IsNullOrWhiteSpace(request.Phone))
        {
            var phone = request.Phone.Trim();
            query = query.Where(p => p.Phone.Contains(phone));
        }

        if (request.DateOfBirth.HasValue)
            query = query.Where(p => p.DateOfBirth == request.DateOfBirth.Value);

        if (!string.IsNullOrWhiteSpace(request.IssueKeyword))
        {
            var keyword = request.IssueKeyword.Trim().ToLowerInvariant();
            query = query.Where(p => p.SearchSessions.Any(s =>
                (s.MedicalIssuesSummary != null && s.MedicalIssuesSummary.ToLower().Contains(keyword)) ||
                (s.Specialty != null && s.Specialty.ToLower().Contains(keyword)) ||
                (s.SearchNotes != null && s.SearchNotes.ToLower().Contains(keyword)) ||
                s.ChatMessages.Any(m => m.Role == "user" && m.Content.ToLower().Contains(keyword))));
        }

        var total = await query.CountAsync(cancellationToken);

        var patients = await query
            .OrderByDescending(p => p.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(p => new
            {
                p.Id,
                p.Username,
                p.FullName,
                p.DateOfBirth,
                p.Phone,
                p.CreatedAt,
                p.IsDeleted,
                LatestSession = p.SearchSessions
                    .OrderByDescending(s => s.UpdatedAt)
                    .Select(s => new { s.Specialty, s.MedicalIssuesSummary })
                    .FirstOrDefault()
            })
            .ToListAsync(cancellationToken);

        await _audit.LogSearchAsync(
            _db,
            AuditEntityTypes.Patient,
            $"Admin patient search page={page} results={patients.Count}",
            cancellationToken);

        return new PagedResult<PatientAdminDto>
        {
            Items = patients.Select(p => new PatientAdminDto
            {
                Id = p.Id,
                Username = p.Username,
                FullName = p.FullName,
                DateOfBirth = p.DateOfBirth,
                Phone = p.Phone,
                CreatedAt = p.CreatedAt,
                LatestSpecialty = p.LatestSession?.Specialty,
                MedicalIssuesSummary = p.LatestSession?.MedicalIssuesSummary,
                IsAccountClosed = p.IsDeleted
            }).ToList(),
            TotalCount = total,
            Page = page,
            PageSize = pageSize
        };
    }

    public async Task<PatientAdminEditModel?> GetForEditAsync(int id, CancellationToken cancellationToken = default)
    {
        var patient = await _db.Patients.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, cancellationToken);
        if (patient == null) return null;

        await _audit.LogReadAsync(
            _db,
            AuditEntityTypes.Patient,
            id.ToString(),
            "Admin viewed patient detail",
            cancellationToken);

        return new PatientAdminEditModel
        {
            Id = patient.Id,
            Username = patient.Username,
            FullName = patient.FullName,
            DateOfBirth = patient.DateOfBirth,
            Phone = patient.Phone,
            IsDeleted = patient.IsDeleted,
            DeletedAtUtc = patient.DeletedAtUtc
        };
    }

    public async Task<(bool Success, string? Error)> CreateAsync(PatientAdminEditModel model, CancellationToken cancellationToken = default)
    {
        if (await _db.Patients.AnyAsync(p => p.Username == model.Username && !p.IsDeleted, cancellationToken))
            return (false, "Username is already taken.");

        if (string.IsNullOrWhiteSpace(model.Password))
            return (false, "Password is required for new patients.");

        var patient = new Patient
        {
            Username = model.Username.Trim(),
            FullName = model.FullName.Trim(),
            DateOfBirth = model.DateOfBirth,
            Phone = model.Phone.Trim()
        };
        patient.PasswordHash = _passwordHasher.HashPassword(patient, model.Password);

        _db.Patients.Add(patient);
        await _db.SaveChangesAsync(cancellationToken);
        _logger.LogInformation("Admin created patient");
        return (true, null);
    }

    public async Task<(bool Success, string? Error)> UpdateAsync(PatientAdminEditModel model, CancellationToken cancellationToken = default)
    {
        var patient = await _db.Patients.FirstOrDefaultAsync(p => p.Id == model.Id, cancellationToken);
        if (patient == null)
            return (false, "Patient not found.");

        if (await _db.Patients.AnyAsync(p => p.Username == model.Username && p.Id != model.Id && !p.IsDeleted, cancellationToken))
            return (false, "Username is already taken.");

        patient.Username = model.Username.Trim();
        patient.FullName = model.FullName.Trim();
        patient.DateOfBirth = model.DateOfBirth;
        patient.Phone = model.Phone.Trim();

        if (!string.IsNullOrWhiteSpace(model.Password))
            patient.PasswordHash = _passwordHasher.HashPassword(patient, model.Password);

        await _db.SaveChangesAsync(cancellationToken);
        _logger.LogInformation("Admin updated patient {Id}", model.Id);
        return (true, null);
    }

    public Task<(bool Success, string? Error)> SoftDeleteAsync(int id, CancellationToken cancellationToken = default) =>
        _privacyRights.SoftClosePatientAccountAsync(
            id,
            $"Admin closed patient account {id} (soft delete)",
            cancellationToken);

    public async Task<(bool Success, string? Error)> ActivateAsync(int id, CancellationToken cancellationToken = default)
    {
        var patient = await _db.Patients.FirstOrDefaultAsync(p => p.Id == id, cancellationToken);
        if (patient == null)
            return (false, "Patient not found.");

        var isClosed = patient.IsDeleted;
        if (!isClosed)
            return (false, "This patient account is already active.");

        patient.IsDeleted = false;
        patient.DeletedAtUtc = null;

        await _db.SaveChangesAsync(cancellationToken);

        await _audit.LogAsync(_db, new AuditLogRequest
        {
            Action = AuditActions.Update,
            EntityType = AuditEntityTypes.Patient,
            EntityId = id.ToString(),
            Summary = "Admin reactivated patient account after soft delete"
        }, cancellationToken);

        _logger.LogInformation("Admin activated patient {Id}", id);
        return (true, null);
    }

    public async Task<(bool Success, string? Error)> HardDeleteAsync(int id, CancellationToken cancellationToken = default)
    {
        var patient = await _db.Patients
            .Include(p => p.SearchSessions)
            .FirstOrDefaultAsync(p => p.Id == id, cancellationToken);

        if (patient == null)
            return (false, "Patient not found.");

        if (!patient.IsDeleted)
            return (false, "Close the account before permanently removing it.");

        var waitDays = Math.Max(0, _account.HardDeleteWaitDays);
        if (!DeletedAccountHelper.CanPermanentlyRemove(patient.DeletedAtUtc, waitDays))
        {
            var availableAt = DeletedAccountHelper.PermanentRemoveAvailableAtUtc(patient.DeletedAtUtc, waitDays);
            return (false, availableAt.HasValue
                ? $"Permanent remove is available after {availableAt.Value:u} UTC ({waitDays} day(s) after closure)."
                : "Permanent remove is not available yet.");
        }

        foreach (var session in patient.SearchSessions)
            session.PatientId = null;

        _db.Patients.Remove(patient);
        await _db.SaveChangesAsync(cancellationToken);

        await _audit.LogAsync(_db, new AuditLogRequest
        {
            Action = AuditActions.Delete,
            EntityType = AuditEntityTypes.Patient,
            EntityId = id.ToString(),
            Summary = "Admin permanently deleted patient (hard delete)"
        }, cancellationToken);

        _logger.LogInformation("Admin hard-deleted patient {Id}", id);
        return (true, null);
    }
}
