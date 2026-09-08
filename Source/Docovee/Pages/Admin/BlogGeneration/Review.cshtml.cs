using Docovee.BLL.Services;
using Docovee.DS.Entities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Docovee.Pages.Admin.BlogGeneration;

[BindProperties]
public class ReviewModel : PageModel
{
    private readonly IBlogGenerationService _blogGen;

    public ReviewModel(IBlogGenerationService blogGen) => _blogGen = blogGen;

    public BlogGenerationTopic? Topic { get; private set; }
    public string? SuccessMessage { get; private set; }
    public string? ErrorMessage { get; private set; }

    public string Title { get; set; } = string.Empty;
    public string Slug { get; set; } = string.Empty;
    public string? MetaDescription { get; set; }
    public string? Excerpt { get; set; }
    public string? BodyHtml { get; set; }
    public string? CustomPrompt { get; set; }

    public async Task<IActionResult> OnGetAsync(int id, CancellationToken cancellationToken)
    {
        if (!await LoadAsync(id, cancellationToken))
            return NotFound();
        await _blogGen.MarkBlogNotificationsReadForTopicAsync(id, cancellationToken);
        return Page();
    }

    public async Task<IActionResult> OnPostSaveAsync(int id, CancellationToken cancellationToken)
    {
        var result = await _blogGen.SaveDraftEditsAsync(
            id, Title, Slug, MetaDescription, Excerpt, BodyHtml, CustomPrompt, cancellationToken);
        if (!result.Success)
        {
            ErrorMessage = result.Error;
            if (!await LoadAsync(id, cancellationToken, preserveForm: true))
                return NotFound();
            return Page();
        }

        SuccessMessage = "Draft saved.";
        if (!await LoadAsync(id, cancellationToken))
            return NotFound();
        return Page();
    }

    public async Task<IActionResult> OnPostRegenerateAsync(int id, CancellationToken cancellationToken)
    {
        var result = await _blogGen.RegenerateAsync(id, CustomPrompt, cancellationToken);
        if (!result.Success)
        {
            ErrorMessage = result.Error;
            if (!await LoadAsync(id, cancellationToken, preserveForm: true))
                return NotFound();
            return Page();
        }

        SuccessMessage = "Draft regenerated. Review below and publish when ready (no email sent).";
        if (!await LoadAsync(id, cancellationToken))
            return NotFound();
        return Page();
    }

    public async Task<IActionResult> OnPostPublishAsync(int id, CancellationToken cancellationToken)
    {
        var save = await _blogGen.SaveDraftEditsAsync(
            id, Title, Slug, MetaDescription, Excerpt, BodyHtml, CustomPrompt, cancellationToken);
        if (!save.Success)
        {
            ErrorMessage = save.Error;
            if (!await LoadAsync(id, cancellationToken, preserveForm: true))
                return NotFound();
            return Page();
        }

        var result = await _blogGen.PublishAsync(id, cancellationToken);
        if (!result.Success)
        {
            ErrorMessage = result.Error;
            if (!await LoadAsync(id, cancellationToken))
                return NotFound();
            return Page();
        }

        SuccessMessage = "Published. Live at /blog/" + result.Page!.Slug;
        if (!await LoadAsync(id, cancellationToken))
            return NotFound();
        return Page();
    }

    private async Task<bool> LoadAsync(int id, CancellationToken cancellationToken, bool preserveForm = false)
    {
        Topic = await _blogGen.GetTopicByIdAsync(id, cancellationToken);
        if (Topic?.ContentPage is null)
            return false;

        if (!preserveForm)
        {
            Title = Topic.ContentPage.Title;
            Slug = Topic.ContentPage.Slug;
            MetaDescription = Topic.ContentPage.MetaDescription;
            Excerpt = Topic.ContentPage.Excerpt;
            BodyHtml = Topic.ContentPage.BodyHtml;
            CustomPrompt = Topic.CustomPrompt;
        }

        return true;
    }
}
