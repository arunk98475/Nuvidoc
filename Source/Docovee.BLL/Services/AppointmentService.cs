using Docovee.BLL.Services.Billing;
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

    Task<DoctorAppointmentDto?> GetForDoctorByIdAsync(
        int doctorId,
        int appointmentId,
        CancellationToken cancellationToken = default);

    Task<(bool Success, string? Error, string? Status, string? StatusLabel, string? BillingMessage)> UpdateStatusAsync(
        int doctorId,
        int appointmentId,
        string status,
        CancellationToken cancellationToken = default);

    Task<(bool Success, string? Error, string? Status, string? StatusLabel)> UpdateStatusAsPatientAsync(
        int patientId,
        int appointmentId,
        string status,
        CancellationToken cancellationToken = default);

    /// <summary>Patient-confirmed reschedule: move StartsAt and set status to Confirmed.</summary>
    Task<(bool Success, string? Error)> RescheduleAsPatientAsync(
        int patientId,
        int appointmentId,
        DateTime newStartsAt,
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
    public const int DefaultSlotDurationMinutes = 40;

    private static readonly HashSet<string> ActiveStatuses = new(StringComparer.OrdinalIgnoreCase)
    {
        AppointmentStatuses.Unconfirmed,
        AppointmentStatuses.Confirmed,
        AppointmentStatuses.PracticeRescheduled,
        AppointmentStatuses.PatientRescheduled,
        // Legacy rows
        AppointmentStatuses.New,
        AppointmentStatuses.Reschedule
    };

    private static readonly HashSet<string> DoctorSettableStatuses = new(StringComparer.OrdinalIgnoreCase)
    {
        AppointmentStatuses.Confirmed,
        AppointmentStatuses.Unconfirmed,
        AppointmentStatuses.PracticeRescheduled,
        AppointmentStatuses.PracticeCanceled,
        AppointmentStatuses.PatientNoShow,
        AppointmentStatuses.Completed
    };

    private readonly DocoveeDbContext _db;
    private readonly IPmsCalendarService _pms;
    private readonly IVisitBillingService _visitBilling;
    private readonly ISponsorshipBillingService _sponsorshipBilling;
    private readonly ILogger<AppointmentService> _logger;

    public AppointmentService(
        DocoveeDbContext db,
        IPmsCalendarService pms,
        IVisitBillingService visitBilling,
        ISponsorshipBillingService sponsorshipBilling,
        ILogger<AppointmentService> logger)
    {
        _db = db;
        _pms = pms;
        _visitBilling = visitBilling;
        _sponsorshipBilling = sponsorshipBilling;
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

        var slotTaken = await IsSlotOccupiedAsync(request.DoctorId, startsAt, cancellationToken);
        if (slotTaken)
            return Fail("That time slot was just booked. Please choose another time.");

        string? phone = request.PatientPhone?.Trim();
        string? email = request.PatientEmail?.Trim();
        DateOnly? patientDob = null;

        if (!string.IsNullOrWhiteSpace(request.DateOfBirth))
        {
            if (!TryParseDateOfBirth(request.DateOfBirth, out var parsedDob))
                return Fail("Please enter a valid date of birth (MM/DD/YYYY).");

            var todayDob = DateOnly.FromDateTime(DateTime.Today);
            if (parsedDob > todayDob || parsedDob < todayDob.AddYears(-120))
                return Fail("Please enter a valid date of birth.");

            patientDob = parsedDob;
        }

        if (patientId is > 0)
        {
            var patient = await _db.Patients.FirstOrDefaultAsync(p => p.Id == patientId.Value, cancellationToken);
            if (patient != null)
            {
                if (string.IsNullOrWhiteSpace(patientName))
                    patientName = patient.FullName;
                if (string.IsNullOrWhiteSpace(phone))
                    phone = patient.Phone;
                if (string.IsNullOrWhiteSpace(email))
                    email = patient.Username;

                var hasRealPatientDob = patient.DateOfBirth != default
                    && patient.DateOfBirth != new DateOnly(1990, 1, 1);
                if (patientDob is DateOnly bookingDob)
                {
                    if (!hasRealPatientDob)
                        patient.DateOfBirth = bookingDob;
                }
                else if (hasRealPatientDob)
                {
                    patientDob = patient.DateOfBirth;
                }
            }
        }

        if (patientDob is null)
            return Fail("Date of birth is required.");

        var now = DateTime.UtcNow;
        var appointment = new Appointment
        {
            DoctorId = request.DoctorId,
            PatientId = patientId is > 0 ? patientId : null,
            PatientName = patientName,
            PatientPhone = phone,
            PatientEmail = email,
            PatientDateOfBirth = patientDob,
            VisitReason = visitReason,
            StartsAt = startsAt,
            Status = AppointmentStatuses.Unconfirmed,
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

        try
        {
            await _pms.PushAppointmentCreatedAsync(appointment, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "PMS outbound push failed after creating appointment {Id}", appointment.Id);
        }

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
                PatientId = a.PatientId,
                SearchSessionId = a.SearchSessionId,
                PatientName = a.PatientName,
                PatientPhone = a.PatientPhone,
                PatientEmail = a.PatientEmail,
                PatientDateOfBirth = a.PatientDateOfBirth,
                VisitReason = a.VisitReason,
                StartsAt = a.StartsAt,
                Status = a.Status,
                Source = a.Source,
                CreatedAt = a.CreatedAt,
                UpdatedAt = a.UpdatedAt
            })
            .ToListAsync(cancellationToken);
    }

    public async Task<DoctorAppointmentDto?> GetForDoctorByIdAsync(
        int doctorId,
        int appointmentId,
        CancellationToken cancellationToken = default)
    {
        return await _db.Appointments.AsNoTracking()
            .Where(a => a.DoctorId == doctorId && a.Id == appointmentId)
            .Select(a => new DoctorAppointmentDto
            {
                Id = a.Id,
                PatientId = a.PatientId,
                SearchSessionId = a.SearchSessionId,
                PatientName = a.PatientName,
                PatientPhone = a.PatientPhone,
                PatientEmail = a.PatientEmail,
                PatientDateOfBirth = a.PatientDateOfBirth,
                VisitReason = a.VisitReason,
                StartsAt = a.StartsAt,
                Status = a.Status,
                Source = a.Source,
                CreatedAt = a.CreatedAt,
                UpdatedAt = a.UpdatedAt
            })
            .FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<(bool Success, string? Error, string? Status, string? StatusLabel, string? BillingMessage)> UpdateStatusAsync(
        int doctorId,
        int appointmentId,
        string status,
        CancellationToken cancellationToken = default)
    {
        var normalized = AppointmentStatuses.Normalize(status);
        if (!DoctorSettableStatuses.Contains(normalized) && !DoctorSettableStatuses.Contains(status))
            return (false, "Unsupported appointment status.", null, null, null);

        var target = DoctorSettableStatuses.Contains(normalized) ? normalized : status;

        var appointment = await _db.Appointments
            .FirstOrDefaultAsync(a => a.DoctorId == doctorId && a.Id == appointmentId, cancellationToken);
        if (appointment == null)
            return (false, "Appointment not found.", null, null, null);

        if (AppointmentSources.IsPmsInbound(appointment.Source))
            return (false, "PMS appointments are managed in your practice software.", null, null, null);

        if (string.Equals(AppointmentStatuses.Normalize(appointment.Status), target, StringComparison.OrdinalIgnoreCase))
        {
            return (true, null, target, AppointmentStatuses.DisplayLabel(target), null);
        }

        if (target == AppointmentStatuses.Confirmed && !AppointmentStatuses.CanConfirm(appointment.Status))
            return (false, "This appointment cannot be confirmed.", null, null, null);

        if (target == AppointmentStatuses.PracticeCanceled && !AppointmentStatuses.CanPracticeCancel(appointment.Status))
            return (false, "This appointment cannot be canceled.", null, null, null);

        if (target == AppointmentStatuses.PatientNoShow && !AppointmentStatuses.CanMarkNoShow(appointment.Status))
            return (false, "This appointment cannot be marked as a no-show.", null, null, null);

        if (target == AppointmentStatuses.Completed && !AppointmentStatuses.CanMarkCompleted(appointment.Status))
            return (false, "This appointment cannot be marked as completed.", null, null, null);

        appointment.Status = target;
        appointment.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "Appointment {Id} status set to {Status} by doctor {DoctorId}",
            appointment.Id, appointment.Status, doctorId);

        string? billingMessage = null;
        if (target == AppointmentStatuses.Completed)
        {
            var charge = await _visitBilling.ChargeForCompletedVisitAsync(doctorId, appointmentId, cancellationToken);
            billingMessage = charge.Message;
            if (!charge.Success && charge.ChargeStatus != BillingChargeStatuses.Skipped)
            {
                _logger.LogWarning(
                    "Visit billing failed for appointment {AppointmentId}: {Message}",
                    appointmentId, charge.Message);
            }

            var sponsorshipCharge = await _sponsorshipBilling.TryChargeAsync(
                doctorId,
                SponsorshipBillingChargeTrigger.Booking,
                appointmentId,
                cancellationToken);
            if (!sponsorshipCharge.Success && sponsorshipCharge.ChargeStatus != BillingChargeStatuses.Skipped)
            {
                _logger.LogWarning(
                    "Sponsorship billing failed for appointment {AppointmentId}: {Message}",
                    appointmentId, sponsorshipCharge.Message);
            }
        }

        try
        {
            await _pms.PushAppointmentStatusAsync(appointment, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "PMS outbound status push failed for appointment {Id}", appointment.Id);
        }

        return (true, null, appointment.Status, AppointmentStatuses.DisplayLabel(appointment.Status), billingMessage);
    }

    public async Task<(bool Success, string? Error, string? Status, string? StatusLabel)> UpdateStatusAsPatientAsync(
        int patientId,
        int appointmentId,
        string status,
        CancellationToken cancellationToken = default)
    {
        var target = AppointmentStatuses.Normalize(status);
        if (target is not (AppointmentStatuses.PatientCanceled or AppointmentStatuses.PatientRescheduled))
            return (false, "Patients can only cancel or request a reschedule.", null, null);

        var patient = await _db.Patients.AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == patientId, cancellationToken);
        if (patient == null)
            return (false, "Patient not found.", null, null);

        var appointment = await _db.Appointments
            .FirstOrDefaultAsync(a =>
                a.Id == appointmentId
                && (a.PatientId == patientId
                    || (a.PatientId == null && !string.IsNullOrWhiteSpace(patient.Username)
                        && a.PatientEmail == patient.Username)),
                cancellationToken);

        if (appointment == null)
            return (false, "Appointment not found.", null, null);

        if (!AppointmentStatuses.IsActive(appointment.Status))
            return (false, "This appointment can no longer be changed.", null, null);

        appointment.Status = target;
        appointment.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "Appointment {Id} status set to {Status} by patient {PatientId}",
            appointment.Id, appointment.Status, patientId);

        try
        {
            await _pms.PushAppointmentStatusAsync(appointment, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "PMS outbound status push failed for appointment {Id}", appointment.Id);
        }

        return (true, null, appointment.Status, AppointmentStatuses.DisplayLabel(appointment.Status));
    }

    public async Task<(bool Success, string? Error)> RescheduleAsPatientAsync(
        int patientId,
        int appointmentId,
        DateTime newStartsAt,
        CancellationToken cancellationToken = default)
    {
        var patient = await _db.Patients.AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == patientId, cancellationToken);
        if (patient == null)
            return (false, "Patient not found.");

        var appointment = await _db.Appointments
            .FirstOrDefaultAsync(a =>
                a.Id == appointmentId
                && (a.PatientId == patientId
                    || (a.PatientId == null && !string.IsNullOrWhiteSpace(patient.Username)
                        && a.PatientEmail == patient.Username)),
                cancellationToken);

        if (appointment == null)
            return (false, "Appointment not found.");

        if (!AppointmentStatuses.IsActive(appointment.Status))
            return (false, "This appointment can no longer be rescheduled.");

        appointment.StartsAt = newStartsAt;
        appointment.Status = AppointmentStatuses.Confirmed;
        appointment.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "Appointment {Id} rescheduled to {StartsAt} by patient {PatientId}",
            appointment.Id, appointment.StartsAt, patientId);

        try
        {
            await _pms.PushAppointmentStatusAsync(appointment, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "PMS outbound status push failed for appointment {Id}", appointment.Id);
        }

        return (true, null);
    }

    public async Task<int> CountActionRequiredAsync(int doctorId, CancellationToken cancellationToken = default)
    {
        var rows = await _db.Appointments.AsNoTracking()
            .Where(a => a.DoctorId == doctorId && a.Source != AppointmentSources.PmsInbound)
            .Select(a => a.Status)
            .ToListAsync(cancellationToken);

        return rows.Count(AppointmentStatuses.NeedsDoctorAttention);
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

    public static bool SlotsOverlap(
        DateTime candidateStart,
        DateTime occupiedStart,
        int slotMinutes = DefaultSlotDurationMinutes)
    {
        var candidateEnd = candidateStart.AddMinutes(slotMinutes);
        var occupiedEnd = occupiedStart.AddMinutes(slotMinutes);
        return candidateStart < occupiedEnd && occupiedStart < candidateEnd;
    }

    public static bool IsSlotBlocked(
        DateTime candidateStart,
        IEnumerable<DateTime> occupiedStarts,
        int slotMinutes = DefaultSlotDurationMinutes)
    {
        foreach (var occupied in occupiedStarts)
        {
            if (SlotsOverlap(candidateStart, occupied, slotMinutes))
                return true;
        }

        return false;
    }

    private async Task<bool> IsSlotOccupiedAsync(
        int doctorId,
        DateTime startsAt,
        CancellationToken cancellationToken)
    {
        var dayStart = startsAt.Date;
        var dayEnd = dayStart.AddDays(1);
        var occupied = await _db.Appointments.AsNoTracking()
            .Where(a =>
                a.DoctorId == doctorId
                && a.StartsAt >= dayStart.AddDays(-1)
                && a.StartsAt < dayEnd.AddDays(1)
                && ActiveStatuses.Contains(a.Status))
            .Select(a => a.StartsAt)
            .ToListAsync(cancellationToken);

        return IsSlotBlocked(startsAt, occupied);
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
            where (a.PatientId == patientId
                  || (a.PatientId == null && email != "" && a.PatientEmail == email))
                  && a.Source != AppointmentSources.PmsInbound
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

    private static bool TryParseDateOfBirth(string value, out DateOnly dateOfBirth)
    {
        dateOfBirth = default;
        var trimmed = value.Trim();
        var formats = new[]
        {
            "M/d/yyyy", "MM/dd/yyyy", "M-d-yyyy", "MM-dd-yyyy", "yyyy-MM-dd"
        };

        if (DateOnly.TryParseExact(trimmed, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out dateOfBirth))
            return true;

        return DateOnly.TryParse(trimmed, CultureInfo.InvariantCulture, DateTimeStyles.None, out dateOfBirth);
    }

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
