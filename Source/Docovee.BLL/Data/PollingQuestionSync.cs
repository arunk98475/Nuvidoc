using Docovee.DS;
using Docovee.DS.Entities;
using Microsoft.EntityFrameworkCore;

namespace Docovee.BLL.Data;

public static class PollingQuestionSync
{
    private const int ActiveDeepDiveCount = 10;

    public static async Task SyncFromSpecAsync(DocoveeDbContext context, CancellationToken cancellationToken = default)
    {
        var spec = NuviFlowContent.DeepDiveQuestions;
        var existing = await context.PollingQuestions.ToListAsync(cancellationToken);

        for (var i = 0; i < spec.Count; i++)
        {
            var sortOrder = i + 1;
            var (question, hint, matchWeight, matchWeightLabel) = spec[i];
            var row = existing.FirstOrDefault(q => q.SortOrder == sortOrder);
            var isWildcard = sortOrder == spec.Count;
            var isActive = isWildcard || sortOrder <= ActiveDeepDiveCount;
            if (row == null)
            {
                context.PollingQuestions.Add(new PollingQuestion
                {
                    Question = question,
                    ValidationHint = hint,
                    SortOrder = sortOrder,
                    MatchWeight = matchWeight,
                    MatchWeightLabel = matchWeightLabel,
                    IsActive = isActive
                });
            }
            else
            {
                row.Question = question;
                row.ValidationHint = hint;
                row.MatchWeight = matchWeight;
                row.MatchWeightLabel = matchWeightLabel;
                row.IsActive = isActive;
            }
        }

        foreach (var extra in existing.Where(q => q.SortOrder > spec.Count))
            extra.IsActive = false;

        await context.SaveChangesAsync(cancellationToken);
    }
}
