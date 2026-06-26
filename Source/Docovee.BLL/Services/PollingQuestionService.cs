using Docovee.BLL.Models;
using Docovee.DS;
using Docovee.DS.Entities;
using Docovee.logging;
using Microsoft.EntityFrameworkCore;

namespace Docovee.BLL.Services;

public interface IPollingQuestionService
{
    Task<IReadOnlyList<PollingQuestionDto>> GetActiveAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<PollingQuestionDto>> GetAllAsync(CancellationToken cancellationToken = default);
    Task<PollingQuestionEditModel?> GetForEditAsync(int id, CancellationToken cancellationToken = default);
    Task<(bool Success, string? Error)> CreateAsync(PollingQuestionEditModel model, CancellationToken cancellationToken = default);
    Task<(bool Success, string? Error)> UpdateAsync(PollingQuestionEditModel model, CancellationToken cancellationToken = default);
    Task<bool> DeleteAsync(int id, CancellationToken cancellationToken = default);
}

public class PollingQuestionService : IPollingQuestionService
{
    private readonly DocoveeDbContext _db;
    private readonly IDocoveeLogger _logger;

    public PollingQuestionService(DocoveeDbContext db, IDocoveeLogger logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<IReadOnlyList<PollingQuestionDto>> GetActiveAsync(CancellationToken cancellationToken = default) =>
        await _db.PollingQuestions.AsNoTracking()
            .Where(q => q.IsActive)
            .OrderBy(q => q.SortOrder)
            .Select(q => MapDto(q))
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<PollingQuestionDto>> GetAllAsync(CancellationToken cancellationToken = default) =>
        await _db.PollingQuestions.AsNoTracking()
            .OrderBy(q => q.SortOrder)
            .Select(q => MapDto(q))
            .ToListAsync(cancellationToken);

    public async Task<PollingQuestionEditModel?> GetForEditAsync(int id, CancellationToken cancellationToken = default)
    {
        var q = await _db.PollingQuestions.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (q == null) return null;
        return new PollingQuestionEditModel
        {
            Id = q.Id,
            Question = q.Question,
            ValidationHint = q.ValidationHint,
            SortOrder = q.SortOrder,
            IsActive = q.IsActive
        };
    }

    public async Task<(bool Success, string? Error)> CreateAsync(PollingQuestionEditModel model, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(model.Question))
            return (false, "Question is required.");

        _db.PollingQuestions.Add(new PollingQuestion
        {
            Question = model.Question.Trim(),
            ValidationHint = model.ValidationHint?.Trim(),
            SortOrder = model.SortOrder,
            IsActive = model.IsActive
        });
        await _db.SaveChangesAsync(cancellationToken);
        _logger.LogInformation("Admin created polling question");
        return (true, null);
    }

    public async Task<(bool Success, string? Error)> UpdateAsync(PollingQuestionEditModel model, CancellationToken cancellationToken = default)
    {
        var q = await _db.PollingQuestions.FirstOrDefaultAsync(x => x.Id == model.Id, cancellationToken);
        if (q == null) return (false, "Question not found.");
        if (string.IsNullOrWhiteSpace(model.Question))
            return (false, "Question is required.");

        q.Question = model.Question.Trim();
        q.ValidationHint = model.ValidationHint?.Trim();
        q.SortOrder = model.SortOrder;
        q.IsActive = model.IsActive;
        await _db.SaveChangesAsync(cancellationToken);
        return (true, null);
    }

    public async Task<bool> DeleteAsync(int id, CancellationToken cancellationToken = default)
    {
        var q = await _db.PollingQuestions.FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (q == null) return false;
        _db.PollingQuestions.Remove(q);
        await _db.SaveChangesAsync(cancellationToken);
        return true;
    }

    private static PollingQuestionDto MapDto(PollingQuestion q) => new()
    {
        Id = q.Id,
        Question = q.Question,
        ValidationHint = q.ValidationHint,
        SortOrder = q.SortOrder,
        IsActive = q.IsActive
    };
}
