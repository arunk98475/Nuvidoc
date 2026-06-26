using Docovee.BLL.Models;
using Docovee.DS;
using Docovee.DS.Entities;
using Docovee.logging;
using Microsoft.EntityFrameworkCore;

namespace Docovee.BLL.Services;

public interface IDoctorReviewService
{
    Task<IReadOnlyList<DoctorReviewDto>> GetByDoctorAsync(int doctorId, CancellationToken cancellationToken = default);
    Task<(bool Success, string? Error)> AddReviewAsync(DoctorReviewRequest request, CancellationToken cancellationToken = default);
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

        _db.DoctorPatientReviews.Add(new DoctorPatientReview
        {
            DoctorId = request.DoctorId,
            ReviewerName = request.ReviewerName.Trim(),
            Rating = request.Rating,
            ReviewText = request.ReviewText.Trim()
        });
        await _db.SaveChangesAsync(cancellationToken);
        _logger.LogInformation("Patient review added for doctor {DoctorId}", request.DoctorId);
        return (true, null);
    }
}
