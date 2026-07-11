using Docovee.DS;
using Docovee.DS.Entities;
using Docovee.DS.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System.Globalization;

namespace Docovee.BLL.Services;

public interface IAppointmentService
{
    Task<CreateAppointmentResponse> CreateAsync(
        CreateAppointmentRequest request,
        int? patientId = null,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<DoctorAppointmentDto>> GetForDoctorAsync(
        int doctorId,
        string? status = null,
        DateTime? fromUtc = null,
        DateTime? toUtc = null,
        CancellationToken cancellationToken = default);

    Task<int> CountActionRequiredAsync(int doctorId, CancellationToken cancellationToken = default);

    Task<HashSet<DateTime>> GetBookedStartsAsync(
        int doctorId,
        DateOnly from,
        DateOnly to,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<PatientAppointmentDto>> GetForPatientAsync(
        int patientId,
        CancellationToken cancellationToken = default);
}

public class AppointmentService : IAppointmentService
{
    private static readonly HashSet<string> ActiveStatuses = new(StringComparer.OrdinalIgnoreCase)
    {
        AppointmentStatuses.New,
        AppointmentStatuses.Confirmed,
        AppointmentStatuses.Reschedule
    };

    private readonly DocoveeDbContext _db;
    private readonly ILogger<AppointmentService> _logger;

    public AppointmentService(DocoveeDbContext db, ILogger<AppointmentService> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<CreateAppointmentResponse> CreateAsync(
        CreateAppointmentRequest request,
        int? patientId = null,
        CancellationToken cancellationToken = default)
    {
        if (request.DoctorId <= 0)
            return Fail("Doctor is required.");

        var patientName = request.PatientName?.Trim() ?? "";
        if (patientName.Length < 2)
            return Fail("Please enter your name.");

        var visitReason = string.IsNullOrWhiteSpace(request.VisitReason)
            ? "General checkup"
            : request.VisitReason.Trim();

        if (!DateOnly.TryParse(request.Date, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            return Fail("Invalid appointment date.");

        if (!TryParseTimeLabel(request.TimeLabel, out var time))
            return Fail("Invalid appointment time.");

        var startsAt = date.ToDateTime(time);

        var doctorExists = await _db.Doctors.AsNoTracking()
            .AnyAsync(d => d.Id == request.DoctorId && d.IsActive, cancellationToken);
        if (!doctorExists)
            return Fail("Doctor not found.");

        var slotTaken = await _db.Appointments.AsNoTracking().AnyAsync(a =>
            a.DoctorId == request.DoctorId
            && a.StartsAt == startsAt
            && ActiveStatuses.Contains(a.Status), cancellationToken);
        if (slotTaken)
            return Fail("That time slot was just booked. Please choose another time.");

        string? phone = request.PatientPhone?.Trim();
        string? email = request.PatientEmail?.Trim();

        if (patientId is > 0)
        {
            var patient = await _db.Patients.AsNoTracking()
                .FirstOrDefaultAsync(p => p.Id == patientId.Value, cancellationToken);
            if (patient != null)
            {
                if (string.IsNullOrWhiteSpace(patientName))
                    patientName = patient.FullName;
                if (string.IsNullOrWhiteSpace(phone))
                    phone = patient.Phone;
                if (string.IsNullOrWhiteSpace(email))
                    email = patient.Username;
            }
        }

        var now = DateTime.UtcNow;
        var appointment = new Appointment
        {
            DoctorId = request.DoctorId,
            PatientId = patientId is > 0 ? patientId : null,
            PatientName = patientName,
            PatientPhone = phone,
            PatientEmail = email,
            VisitReason = visitReason,
            StartsAt = startsAt,
            Status = AppointmentStatuses.New,
            Source = string.IsNullOrWhiteSpace(request.Source)
                ? AppointmentSources.PublicProfile
                : request.Source.Trim(),
            CreatedAt = now,
            UpdatedAt = now
        };

        _db.Appointments.Add(appointment);
        await _db.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "Appointment {Id} created for doctor {DoctorId} at {StartsAt} ({Patient})",
            appointment.Id, appointment.DoctorId, appointment.StartsAt, appointment.PatientName);

        return new CreateAppointmentResponse
        {
            Success = true,
            AppointmentId = appointment.Id,
            Message = "Your booking request was sent. The office will confirm your appointment."
        };
    }

    public async Task<IReadOnlyList<DoctorAppointmentDto>> GetForDoctorAsync(
        int doctorId,
        string? status = null,
        DateTime? from = null,
        DateTime? to = null,
        CancellationToken cancellationToken = default)
    {
        var query = _db.Appointments.AsNoTracking()
            .Where(a => a.DoctorId == doctorId);

        if (!string.IsNullOrWhiteSpace(status))
            query = query.Where(a => a.Status == status);

        if (from.HasValue)
            query = query.Where(a => a.StartsAt >= from.Value);

        if (to.HasValue)
            query = query.Where(a => a.StartsAt < to.Value);

        return await query
            .OrderByDescending(a => a.UpdatedAt)
            .Select(a => new DoctorAppointmentDto
            {
                Id = a.Id,
                PatientName = a.PatientName,
                PatientPhone = a.PatientPhone,
                VisitReason = a.VisitReason,
                StartsAt = a.StartsAt,
                Status = a.Status,
                Source = a.Source,
                CreatedAt = a.CreatedAt,
                UpdatedAt = a.UpdatedAt
            })
            .ToListAsync(cancellationToken);
    }

    public async Task<int> CountActionRequiredAsync(int doctorId, CancellationToken cancellationToken = default)
    {
        return await _db.Appointments.AsNoTracking()
            .CountAsync(a =>
                a.DoctorId == doctorId
                && (a.Status == AppointmentStatuses.New || a.Status == AppointmentStatuses.Reschedule),
                cancellationToken);
    }

    public async Task<HashSet<DateTime>> GetBookedStartsAsync(
        int doctorId,
        DateOnly from,
        DateOnly to,
        CancellationToken cancellationToken = default)
    {
        var fromDt = from.ToDateTime(TimeOnly.MinValue);
        var toDt = to.AddDays(1).ToDateTime(TimeOnly.MinValue);

        var starts = await _db.Appointments.AsNoTracking()
            .Where(a =>
                a.DoctorId == doctorId
                && a.StartsAt >= fromDt
                && a.StartsAt < toDt
                && ActiveStatuses.Contains(a.Status))
            .Select(a => a.StartsAt)
            .ToListAsync(cancellationToken);

        return starts.ToHashSet();
    }

    public async Task<IReadOnlyList<PatientAppointmentDto>> GetForPatientAsync(
        int patientId,
        CancellationToken cancellationToken = default)
    {
        var patient = await _db.Patients.AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == patientId, cancellationToken);
        if (patient == null)
            return Array.Empty<PatientAppointmentDto>();

        var email = patient.Username;

        var rows = await (
            from a in _db.Appointments.AsNoTracking()
            join d in _db.Doctors.AsNoTracking() on a.DoctorId equals d.Id
            where a.PatientId == patientId
                  || (a.PatientId == null && email != "" && a.PatientEmail == email)
            orderby a.StartsAt descending
            select new
            {
                a.Id,
                a.DoctorId,
                DoctorName = d.Name,
                DoctorSpecialty = d.Specialty,
                DoctorPhotoUrl = d.PhotoUrl,
                DoctorGmb = d.GmbPhotoLink,
                DoctorCity = d.City,
                DoctorState = d.State,
                a.VisitReason,
                a.StartsAt,
                a.Status
            }).ToListAsync(cancellationToken);

        var doctorIds = rows.Select(r => r.DoctorId).Distinct().ToList();
        var reviews = await _db.DoctorPatientReviews.AsNoTracking()
            .Where(r => r.PatientId == patientId && doctorIds.Contains(r.DoctorId))
            .ToListAsync(cancellationToken);
        var reviewByDoctor = reviews
            .GroupBy(r => r.DoctorId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(x => x.Id).First());

        return rows.Select(r =>
        {
            reviewByDoctor.TryGetValue(r.DoctorId, out var review);
            return new PatientAppointmentDto
            {
                Id = r.Id,
                DoctorId = r.DoctorId,
                DoctorName = r.DoctorName,
                DoctorSpecialty = r.DoctorSpecialty ?? "",
                DoctorPhotoUrl = DoctorPhotoHelper.GetDisplayPhotoUrl(r.DoctorPhotoUrl, r.DoctorGmb),
                DoctorLocation = string.Join(", ", new[] { r.DoctorCity, r.DoctorState }
                    .Where(p => !string.IsNullOrWhiteSpace(p) && p != "NA")),
                VisitReason = r.VisitReason,
                StartsAt = r.StartsAt,
                Status = r.Status,
                HasReview = review != null,
                ReviewRating = review?.Rating,
                ReviewText = review?.ReviewText,
                ReviewWaitingTime = review?.WaitingTime,
                ReviewRecommendation = review?.Recommendation,
                ReviewedAt = review?.CreatedAt
            };
        }).ToList();
    }

    private static CreateAppointmentResponse Fail(string message) =>
        new() { Success = false, Message = message };

    public static bool TryParseTimeLabel(string? timeLabel, out TimeOnly time)
    {
        time = default;
        if (string.IsNullOrWhiteSpace(timeLabel))
            return false;

        var formats = new[] { "h:mm tt", "h:mmtt", "htt", "h tt", "HH:mm" };
        foreach (var format in formats)
        {
            if (DateTime.TryParseExact(
                    timeLabel.Trim(),
                    format,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out var parsed))
            {
                time = TimeOnly.FromDateTime(parsed);
                return true;
            }
        }

        return TimeOnly.TryParse(timeLabel, CultureInfo.InvariantCulture, out time);
    }
}
