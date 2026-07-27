namespace Docovee.DS.Entities;

/// <summary>
/// A user-editable marketing/SEO page (blog post or service/landing page).
/// Managed by admins via /Admin/Content.
/// Published pages are accessible at /blog/{Slug} or /services/{Slug}.
/// </summary>
public class ContentPage
{
    public int Id { get; set; }

    /// <summary>blog | service</summary>
    public string PageType { get; set; } = "blog";

    /// <summary>URL-safe slug, e.g. "emergency-dentist-houston"</summary>
    public string Slug { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    /// <summary>Meta description for SEO (&lt;meta name="description"&gt;)</summary>
    public string? MetaDescription { get; set; }

    /// <summary>Rich HTML body (stored as-is, rendered on public page)</summary>
    public string? BodyHtml { get; set; }

    /// <summary>Optional hero/feature image URL</summary>
    public string? ImageUrl { get; set; }

    /// <summary>Short excerpt shown on list pages</summary>
    public string? Excerpt { get; set; }

    /// <summary>YouTube or Vimeo embed URL (optional)</summary>
    public string? VideoEmbedUrl { get; set; }

    public bool IsPublished { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}
