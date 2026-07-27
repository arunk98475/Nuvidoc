using Docovee.BLL.Configuration;
using Docovee.BLL.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Docovee.Pages.Admin.Content;

public class EditModel : ContentFormModel
{
    private int _id;

    public EditModel(IContentPageService content, IOptions<UploadOptions> uploadOptions)
        : base(content, uploadOptions) { }

    public async Task<IActionResult> OnGetAsync(int id)
    {
        _id = id;
        var page = await _content.GetByIdAsync(id);
        if (page is null) return NotFound();

        Input = new ContentPageInput
        {
            PageType        = page.PageType,
            Slug            = page.Slug,
            Title           = page.Title,
            MetaDescription = page.MetaDescription,
            Excerpt         = page.Excerpt,
            BodyHtml        = page.BodyHtml,
            VideoEmbedUrl   = page.VideoEmbedUrl,
            ImageUrl        = page.ImageUrl,
            IsPublished     = page.IsPublished,
        };
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(int id, IFormFile? imageFile, bool removeImage = false)
    {
        _id = id;
        var page = await _content.GetByIdAsync(id);
        if (page is null) return NotFound();

        if (!TryValidate(out var error))
        {
            ErrorMessage = error;
            return Page();
        }

        if (await _content.SlugExistsAsync(Input.Slug, excludeId: id))
        {
            ErrorMessage = $"The slug \"{Input.Slug}\" is already used by another page.";
            return Page();
        }

        var imageUrl = removeImage ? null : await UploadImageAsync(imageFile) ?? page.ImageUrl;

        page.PageType        = Input.PageType;
        page.Slug            = Input.Slug.Trim().ToLowerInvariant();
        page.Title           = Input.Title.Trim();
        page.MetaDescription = Input.MetaDescription?.Trim();
        page.Excerpt         = Input.Excerpt?.Trim();
        page.BodyHtml        = Input.BodyHtml?.Trim();
        page.VideoEmbedUrl   = Input.VideoEmbedUrl?.Trim();
        page.ImageUrl        = imageUrl;
        page.IsPublished     = Input.IsPublished;

        await _content.UpdateAsync(page);
        return RedirectToPage("Index");
    }
}
