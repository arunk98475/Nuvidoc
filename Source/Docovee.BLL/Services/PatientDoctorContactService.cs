using Docovee.BLL.Models;
using Docovee.DS;
using Docovee.DS.Entities;
using Docovee.logging;
using Microsoft.EntityFrameworkCore;

namespace Docovee.BLL.Services;

public interface IPatientDoctorContactService
{
    Task RecordContactViewAsync(int patientId, int doctorId, int? searchSessionId = null, CancellationToken cancellationToken = default);
    Task TryRecordContactViewBySessionAsync(Guid sessionKey, int doctorId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<PatientViewedDoctorDto>> GetViewedDoctorsAsync(int patientId, CancellationToken cancellationToken = default);
    Task<int?> TryResolveSearchSessionIdAsync(Guid sessionKey, CancellationToken cancellationToken = default);
}

public class PatientDoctorContactService : IPatientDoctorContactService
{
    private readonly DocoveeDbContext _db;
    private readonly IDocoveeLogger _logger;

    public PatientDoctorContactService(DocoveeDbContext db, IDocoveeLogger logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task RecordContactViewAsync(
        int patientId,
        int doctorId,
        int? searchSessionId = null,
        CancellationToken cancellationToken = default)
    {
        if (!await _db.Doctors.AnyAsync(d => d.Id == doctorId && d.IsActive, cancellationToken))
            return;

        var existing = await _db.PatientDoctorContactViews
            .FirstOrDefaultAsync(v => v.PatientId == patientId && v.DoctorId == doctorId, cancellationToken);

        if (existing != null)
        {
            existing.ViewedAt = DateTime.UtcNow;
            if (searchSessionId.HasValue)
                existing.SearchSessionId = searchSessionId;
        }
        else
        {
            _db.PatientDoctorContactViews.Add(new PatientDoctorContactView
            {
                PatientId = patientId,
                DoctorId = doctorId,
                SearchSessionId = searchSessionId,
                ViewedAt = DateTime.UtcNow
            });
        }

        await _db.SaveChangesAsync(cancellationToken);
        _logger.LogInformation("Recorded doctor contact view for patient {PatientId}, doctor {DoctorId}", patientId, doctorId);
    }

    public async Task TryRecordContactViewBySessionAsync(
        Guid sessionKey,
        int doctorId,
        CancellationToken cancellationToken = default)
    {
        var session = await _db.SearchSessions.AsNoTracking()
            .FirstOrDefaultAsync(s => s.SessionKey == sessionKey, cancellationToken);
        if (session?.PatientId == null)
            return;

        await RecordContactViewAsync(session.PatientId.Value, doctorId, session.Id, cancellationToken);
    }

    public async Task<IReadOnlyList<PatientViewedDoctorDto>> GetViewedDoctorsAsync(
        int patientId,
        CancellationToken cancellationToken = default)
    {
        var views = await _db.PatientDoctorContactViews.AsNoTracking()
            .Where(v => v.PatientId == patientId)
            .Include(v => v.Doctor)
            .OrderByDescending(v => v.ViewedAt)
            .ToListAsync(cancellationToken);

        if (views.Count == 0)
            return Array.Empty<PatientViewedDoctorDto>();

        var doctorIds = views.Select(v => v.DoctorId).ToList();
        var reviews = await _db.DoctorPatientReviews.AsNoTracking()
            .Where(r => r.PatientId == patientId && doctorIds.Contains(r.DoctorId))
            .ToListAsync(cancellationToken);
        var reviewByDoctor = reviews.ToDictionary(r => r.DoctorId);

        return views.Select(v =>
        {
            reviewByDoctor.TryGetValue(v.DoctorId, out var review);
            var doctor = v.Doctor;
            return new PatientViewedDoctorDto
            {
                DoctorId = v.DoctorId,
                Name = doctor.Name,
                Specialty = doctor.Specialty,
                PracticeName = doctor.PracticeName,
                Location = doctor.Location ?? $"{doctor.City}, {doctor.State}",
                OfficePhoneNumber = doctor.OfficePhoneNumber,
                PhotoUrl = DoctorPhotoHelper.GetDisplayPhotoUrl(doctor.PhotoUrl, doctor.GmbPhotoLink),
                AvatarInitials = doctor.AvatarInitials,
                ViewedAt = v.ViewedAt,
                HasReview = review != null,
                ReviewRating = review?.Rating,
                ReviewText = review?.ReviewText,
                ReviewedAt = review?.CreatedAt
            };
        }).ToList();
    }

    public async Task<int?> TryResolveSearchSessionIdAsync(Guid sessionKey, CancellationToken cancellationToken = default)
    {
        return await _db.SearchSessions.AsNoTracking()
            .Where(s => s.SessionKey == sessionKey)
            .Select(s => (int?)s.Id)
            .FirstOrDefaultAsync(cancellationToken);
    }
}
