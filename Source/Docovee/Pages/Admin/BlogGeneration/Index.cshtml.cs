using Docovee.BLL.Services;
using Docovee.DS.Entities;
using Docovee.DS.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Docovee.Pages.Admin.BlogGeneration;

public class IndexModel : PageModel
{
    private readonly IBlogGenerationService _blogGen;
    private readonly IAppSettingsService _appSettings;

    public IndexModel(IBlogGenerationService blogGen, IAppSettingsService appSettings)
    {
        _blogGen = blogGen;
        _appSettings = appSettings;
    }

    public IReadOnlyList<BlogGenerationTopic> Topics { get; private set; } = Array.Empty<BlogGenerationTopic>();
    public BlogGenerationSettings Settings { get; private set; } = new();
    public DateTime? NextDueUtc { get; private set; }
    public string? SuccessMessage { get; private set; }
    public string? ErrorMessage { get; private set; }

    [BindProperty]
    public string NewTopic { get; set; } = string.Empty;

    [BindProperty]
    public bool Enabled { get; set; }

    [BindProperty]
    public int IntervalDays { get; set; } = 7;

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        await LoadAsync(cancellationToken);
    }

    public async Task<IActionResult> OnPostAddTopicAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _blogGen.AddTopicAsync(NewTopic, cancellationToken);
            SuccessMessage = "Topic added to the queue.";
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }

        await LoadAsync(cancellationToken);
        return Page();
    }

    public async Task<IActionResult> OnPostSaveSettingsAsync(CancellationToken cancellationToken)
    {
        var (ok, error) = await _appSettings.SaveBlogGenerationSettingsAsync(
            new BlogGenerationSettings { Enabled = Enabled, IntervalDays = IntervalDays },
            cancellationToken);
        if (!ok)
            ErrorMessage = error;
        else
            SuccessMessage = "Schedule settings saved.";

        await LoadAsync(cancellationToken);
        return Page();
    }

    public async Task<IActionResult> OnPostDeleteAsync(int id, CancellationToken cancellationToken)
    {
        var deleted = await _blogGen.DeleteTopicAsync(id, cancellationToken);
        if (!deleted)
            ErrorMessage = "Could not delete topic (not found, or already generated).";
        else
            SuccessMessage = "Topic removed.";

        await LoadAsync(cancellationToken);
        return Page();
    }

    public async Task<IActionResult> OnPostGenerateNowAsync(CancellationToken cancellationToken)
    {
        var result = await _blogGen.ProcessDueGenerationAsync(force: true, cancellationToken);
        if (result.Success)
            SuccessMessage = $"Draft generated: {result.Page?.Title}. Check email / dashboard notification to review.";
        else
            ErrorMessage = result.Error ?? "Generation failed.";

        await LoadAsync(cancellationToken);
        return Page();
    }

    public async Task<IActionResult> OnPostRetryAsync(int id, CancellationToken cancellationToken)
    {
        var result = await _blogGen.GenerateTopicAsync(id, notify: true, cancellationToken);
        if (result.Success)
            SuccessMessage = $"Draft ready: {result.Page?.Title}.";
        else
            ErrorMessage = result.Error ?? "Retry failed.";

        await LoadAsync(cancellationToken);
        return Page();
    }

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        Topics = await _blogGen.GetTopicsAsync(cancellationToken);
        Settings = await _appSettings.GetBlogGenerationSettingsAsync(cancellationToken);
        Enabled = Settings.Enabled;
        IntervalDays = Settings.IntervalDays;

        if (Settings.LastRunUtc.HasValue)
            NextDueUtc = Settings.LastRunUtc.Value.AddDays(Settings.IntervalDays);
        else if (Settings.Enabled && Topics.Any(t => t.Status == BlogGenerationTopicStatuses.Pending))
            NextDueUtc = DateTime.UtcNow;
        else
            NextDueUtc = null;
    }
}
