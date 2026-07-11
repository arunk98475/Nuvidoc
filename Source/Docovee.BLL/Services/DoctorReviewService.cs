using Docovee.DS.Models;
using Docovee.DS;
using Docovee.DS.Entities;
using Docovee.logging;
using Microsoft.EntityFrameworkCore;

namespace Docovee.BLL.Services;

public interface IDoctorReviewService
{
    Task<IReadOnlyList<DoctorReviewDto>> GetByDoctorAsync(int doctorId, CancellationToken cancellationToken = default);
    Task<(bool Success, string? Error)> AddReviewAsync(DoctorReviewRequest request, CancellationToken cancellationToken = default);
    Task<(bool Success, string? Error)> AddReviewForPatientAsync(
        int patientId,
        int doctorId,
        int rating,
        string reviewText,
        string? waitingTime,
        string? recommendation,
        CancellationToken cancellationToken = default);
}

public static class PatientReviewOptions
{
    public static readonly string[] WaitingTimes = ["Excellent", "Good", "Average", "Bad"];
    public static readonly string[] Recommendations = ["Highly Recommended", "Neutral", "Not Recommended"];

    public static string? NormalizeWaitingTime(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        return WaitingTimes.FirstOrDefault(o =>
            string.Equals(o, value.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    public static string? NormalizeRecommendation(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        return Recommendations.FirstOrDefault(o =>
            string.Equals(o, value.Trim(), StringComparison.OrdinalIgnoreCase));
    }
}

public class DoctorReviewService : IDoctorReviewService
{
    private readonly DocoveeDbContext _db;
    private readonly IDocoveeLogger _logger;

    public DoctorReviewService(DocoveeDbContext db, IDocoveeLogger logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<IReadOnlyList<DoctorReviewDto>> GetByDoctorAsync(int doctorId, CancellationToken cancellationToken = default) =>
        await _db.DoctorPatientReviews.AsNoTracking()
            .Where(r => r.DoctorId == doctorId)
            .OrderByDescending(r => r.CreatedAt)
            .Select(r => new DoctorReviewDto
            {
                Id = r.Id,
                ReviewerName = r.ReviewerName,
                Rating = r.Rating,
                ReviewText = r.ReviewText,
                WaitingTime = r.WaitingTime,
                Recommendation = r.Recommendation,
                CreatedAt = r.CreatedAt
            })
            .ToListAsync(cancellationToken);

    public async Task<(bool Success, string? Error)> AddReviewAsync(DoctorReviewRequest request, CancellationToken cancellationToken = default)
    {
        if (!await _db.Doctors.AnyAsync(d => d.Id == request.DoctorId, cancellationToken))
            return (false, "Doctor not found.");
        if (string.IsNullOrWhiteSpace(request.ReviewerName))
            return (false, "Your name is required.");
        if (string.IsNullOrWhiteSpace(request.ReviewText))
            return (false, "Review text is required.");
        if (request.Rating < 1 || request.Rating > 5)
            return (false, "Rating must be between 1 and 5.");

        var waitingTime = PatientReviewOptions.NormalizeWaitingTime(request.WaitingTime);
        if (string.IsNullOrWhiteSpace(waitingTime))
            return (false, "Please rate the waiting time.");

        var recommendation = PatientReviewOptions.NormalizeRecommendation(request.Recommendation);
        if (string.IsNullOrWhiteSpace(recommendation))
            return (false, "Please tell us how you would recommend this doctor.");

        if (request.PatientId.HasValue)
        {
            if (await _db.DoctorPatientReviews.AnyAsync(
                    r => r.PatientId == request.PatientId && r.DoctorId == request.DoctorId, cancellationToken))
                return (false, "You have already reviewed this doctor.");
        }

        _db.DoctorPatientReviews.Add(new DoctorPatientReview
        {
            DoctorId = request.DoctorId,
            PatientId = request.PatientId,
            ReviewerName = request.ReviewerName.Trim(),
            Rating = request.Rating,
            ReviewText = request.ReviewText.Trim(),
            WaitingTime = waitingTime,
            Recommendation = recommendation
        });
        await _db.SaveChangesAsync(cancellationToken);
        _logger.LogInformation("Patient review added for doctor {DoctorId}", request.DoctorId);
        return (true, null);
    }

    public async Task<(bool Success, string? Error)> AddReviewForPatientAsync(
        int patientId,
        int doctorId,
        int rating,
        string reviewText,
        string? waitingTime,
        string? recommendation,
        CancellationToken cancellationToken = default)
    {
        var patient = await _db.Patients.AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == patientId, cancellationToken);
        if (patient == null)
            return (false, "Patient not found.");

        var hasViewed = await _db.PatientDoctorContactViews
            .AnyAsync(v => v.PatientId == patientId && v.DoctorId == doctorId, cancellationToken);
        var hasAppointment = await _db.Appointments
            .AnyAsync(a => a.DoctorId == doctorId
                && (a.PatientId == patientId
                    || (a.PatientId == null && a.PatientEmail == patient.Username)),
                cancellationToken);
        if (!hasViewed && !hasAppointment)
            return (false, "You can only review doctors you've contacted or booked with.");

        if (await _db.DoctorPatientReviews.AnyAsync(
                r => r.PatientId == patientId && r.DoctorId == doctorId, cancellationToken))
            return (false, "You have already reviewed this doctor.");

        return await AddReviewAsync(new DoctorReviewRequest
        {
            DoctorId = doctorId,
            ReviewerName = patient.FullName,
            Rating = rating,
            ReviewText = reviewText,
            WaitingTime = waitingTime,
            Recommendation = recommendation,
            PatientId = patientId
        }, cancellationToken);
    }
}
