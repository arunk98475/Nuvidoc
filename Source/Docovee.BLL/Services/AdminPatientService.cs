using Docovee.DS;
using Docovee.DS.Entities;
using Docovee.DS.Models;
using Docovee.logging;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Docovee.BLL.Services;

public interface IAdminPatientService
{
    Task<PagedResult<PatientAdminDto>> SearchAsync(PatientSearchRequest request, CancellationToken cancellationToken = default);
    Task<PatientAdminEditModel?> GetForEditAsync(int id, CancellationToken cancellationToken = default);
    Task<(bool Success, string? Error)> CreateAsync(PatientAdminEditModel model, CancellationToken cancellationToken = default);
    Task<(bool Success, string? Error)> UpdateAsync(PatientAdminEditModel model, CancellationToken cancellationToken = default);
    Task<bool> DeleteAsync(int id, CancellationToken cancellationToken = default);
}

public class AdminPatientService : IAdminPatientService
{
    private readonly DocoveeDbContext _db;
    private readonly IDocoveeLogger _logger;
    private readonly PasswordHasher<Patient> _passwordHasher = new();

    public AdminPatientService(DocoveeDbContext db, IDocoveeLogger logger)
    {
        _db = db;
        _logger = logger;
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
                LatestSession = p.SearchSessions
                    .OrderByDescending(s => s.UpdatedAt)
                    .Select(s => new { s.Specialty, s.MedicalIssuesSummary })
                    .FirstOrDefault()
            })
            .ToListAsync(cancellationToken);

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
                MedicalIssuesSummary = p.LatestSession?.MedicalIssuesSummary
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

        return new PatientAdminEditModel
        {
            Id = patient.Id,
            Username = patient.Username,
            FullName = patient.FullName,
            DateOfBirth = patient.DateOfBirth,
            Phone = patient.Phone
        };
    }

    public async Task<(bool Success, string? Error)> CreateAsync(PatientAdminEditModel model, CancellationToken cancellationToken = default)
    {
        if (await _db.Patients.AnyAsync(p => p.Username == model.Username, cancellationToken))
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
        _logger.LogInformation("Admin created patient {Username}", patient.Username);
        return (true, null);
    }

    public async Task<(bool Success, string? Error)> UpdateAsync(PatientAdminEditModel model, CancellationToken cancellationToken = default)
    {
        var patient = await _db.Patients.FirstOrDefaultAsync(p => p.Id == model.Id, cancellationToken);
        if (patient == null)
            return (false, "Patient not found.");

        if (await _db.Patients.AnyAsync(p => p.Username == model.Username && p.Id != model.Id, cancellationToken))
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

    public async Task<bool> DeleteAsync(int id, CancellationToken cancellationToken = default)
    {
        var patient = await _db.Patients
            .Include(p => p.SearchSessions)
            .FirstOrDefaultAsync(p => p.Id == id, cancellationToken);

        if (patient == null)
            return false;

        foreach (var session in patient.SearchSessions)
            session.PatientId = null;

        _db.Patients.Remove(patient);
        await _db.SaveChangesAsync(cancellationToken);
        _logger.LogInformation("Admin deleted patient {Id}", id);
        return true;
    }
}
