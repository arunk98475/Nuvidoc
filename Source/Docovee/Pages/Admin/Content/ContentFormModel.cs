using Docovee.BLL.Configuration;
using Docovee.BLL.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;

namespace Docovee.Pages.Admin.Content;

/// <summary>Shared base for Create and Edit page models.</summary>
public abstract class ContentFormModel : PageModel
{
    protected readonly IContentPageService _content;
    private readonly UploadOptions _uploadOptions;

    protected ContentFormModel(IContentPageService content, IOptions<UploadOptions> uploadOptions)
    {
        _content = content;
        _uploadOptions = uploadOptions.Value;
    }

    [BindProperty]
    public ContentPageInput Input { get; set; } = new();

    public string ErrorMessage { get; set; } = string.Empty;
    public string PageHeading { get; set; } = string.Empty;
    public string SubmitLabel { get; set; } = "Save";

    protected bool TryValidate(out string error)
    {
        if (string.IsNullOrWhiteSpace(Input.Title))  { error = "Title is required.";   return false; }
        if (string.IsNullOrWhiteSpace(Input.Slug))   { error = "Slug is required.";    return false; }

        var slug = Input.Slug.Trim().ToLowerInvariant();
        if (!System.Text.RegularExpressions.Regex.IsMatch(slug, @"^[a-z0-9][a-z0-9\-]*[a-z0-9]$") && slug.Length > 1)
        {
            error = "Slug may only contain lowercase letters, numbers, and hyphens, and must not start or end with a hyphen.";
            return false;
        }

        error = string.Empty;
        return true;
    }

    protected async Task<string?> UploadImageAsync(IFormFile? file)
    {
        if (file is null || file.Length == 0) return null;

        const long maxBytes = 5 * 1024 * 1024;
        if (file.Length > maxBytes)
            return null;

        var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
        if (ext is not (".jpg" or ".jpeg" or ".png" or ".gif" or ".webp"))
            return null;

        var physicalPath = _uploadOptions.ContentImagesPhysicalPath;
        if (string.IsNullOrWhiteSpace(physicalPath))
            return null;

        Directory.CreateDirectory(physicalPath);
        var filename = $"{Guid.NewGuid():N}{ext}";
        var dest = Path.Combine(physicalPath, filename);

        await using var stream = System.IO.File.Create(dest);
        await file.CopyToAsync(stream);

        return $"{_uploadOptions.ContentImagesPublicPath}/{filename}";
    }
}

public class ContentPageInput
{
    public string PageType        { get; set; } = "blog";
    public string Slug            { get; set; } = string.Empty;
    public string Title           { get; set; } = string.Empty;
    public string? MetaDescription { get; set; }
    public string? Excerpt        { get; set; }
    public string? BodyHtml       { get; set; }
    public string? VideoEmbedUrl  { get; set; }
    public string? ImageUrl       { get; set; }
    public bool   IsPublished     { get; set; }
}
