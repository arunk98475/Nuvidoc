using Docovee.BLL.Configuration;
using Docovee.BLL.Services;
using Docovee.DS.Entities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Docovee.Pages.Admin.Content;

public class CreateModel : ContentFormModel
{
    public CreateModel(IContentPageService content, IOptions<UploadOptions> uploadOptions)
        : base(content, uploadOptions) { }

    public void OnGet() { }

    public async Task<IActionResult> OnPostAsync(IFormFile? imageFile, bool removeImage = false)
    {
        if (!TryValidate(out var error))
        {
            ErrorMessage = error;
            return Page();
        }

        if (await _content.SlugExistsAsync(Input.Slug))
        {
            ErrorMessage = $"The slug \"{Input.Slug}\" is already in use. Choose a different slug.";
            return Page();
        }

        var imageUrl = await UploadImageAsync(imageFile);

        var page = new ContentPage
        {
            PageType   = Input.PageType,
            Slug       = Input.Slug.Trim().ToLowerInvariant(),
            Title      = Input.Title.Trim(),
            MetaDescription = Input.MetaDescription?.Trim(),
            Excerpt    = Input.Excerpt?.Trim(),
            BodyHtml   = Input.BodyHtml?.Trim(),
            VideoEmbedUrl = Input.VideoEmbedUrl?.Trim(),
            ImageUrl   = imageUrl,
            IsPublished = Input.IsPublished,
        };

        await _content.CreateAsync(page);
        return RedirectToPage("Index");
    }
}
