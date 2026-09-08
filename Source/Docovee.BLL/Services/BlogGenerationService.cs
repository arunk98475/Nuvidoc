using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Docovee.BLL.Configuration;
using Docovee.DS;
using Docovee.DS.Entities;
using Docovee.logging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Docovee.BLL.Services;

public sealed class BlogGenerationResult
{
    public bool Success { get; init; }
    public string? Error { get; init; }
    public BlogGenerationTopic? Topic { get; init; }
    public ContentPage? Page { get; init; }
}

public interface IBlogGenerationService
{
    Task<IReadOnlyList<BlogGenerationTopic>> GetTopicsAsync(CancellationToken cancellationToken = default);
    Task<BlogGenerationTopic?> GetTopicByIdAsync(int id, CancellationToken cancellationToken = default);
    Task<BlogGenerationTopic> AddTopicAsync(string topic, CancellationToken cancellationToken = default);
    Task<bool> DeleteTopicAsync(int id, CancellationToken cancellationToken = default);

    /// <summary>
    /// If due (or force), generate the next Pending topic. Sends email + admin notification on success.
    /// </summary>
    Task<BlogGenerationResult> ProcessDueGenerationAsync(
        bool force = false,
        CancellationToken cancellationToken = default);

    /// <summary>Retry a Failed topic (or re-run Pending). Notifies on success.</summary>
    Task<BlogGenerationResult> GenerateTopicAsync(
        int topicId,
        bool notify = true,
        CancellationToken cancellationToken = default);

    /// <summary>Regenerate existing draft with optional custom prompt. No email/notification.</summary>
    Task<BlogGenerationResult> RegenerateAsync(
        int topicId,
        string? customPrompt,
        CancellationToken cancellationToken = default);

    Task<BlogGenerationResult> SaveDraftEditsAsync(
        int topicId,
        string title,
        string slug,
        string? metaDescription,
        string? excerpt,
        string? bodyHtml,
        string? customPrompt,
        CancellationToken cancellationToken = default);

    Task<BlogGenerationResult> PublishAsync(int topicId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AdminNotification>> GetUnreadAdminNotificationsAsync(
        CancellationToken cancellationToken = default);

    Task MarkAdminNotificationReadAsync(int id, CancellationToken cancellationToken = default);

    Task MarkBlogNotificationsReadForTopicAsync(int topicId, CancellationToken cancellationToken = default);
}

public sealed class BlogGenerationService : IBlogGenerationService
{
    private static readonly Regex JsonFenceRegex = new(
        @"```(?:json)?\s*(\{[\s\S]*?\})\s*```",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.Singleline);

    private static readonly Regex NonSlugChars = new(@"[^a-z0-9\-]+", RegexOptions.Compiled);
    private static readonly Regex MultiHyphen = new(@"-+", RegexOptions.Compiled);

    private static readonly Regex DelimitedFieldRegex = new(
        @"^TITLE:\s*(.*?)\s*^SLUG:\s*(.*?)\s*^META:\s*(.*?)\s*^EXCERPT:\s*(.*?)\s*^BODY_HTML:\s*\r?\n([\s\S]*?)\s*^END_BODY_HTML\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.Compiled);

    private readonly DocoveeDbContext _db;
    private readonly IContentPageService _content;
    private readonly IAppSettingsService _appSettings;
    private readonly IEmailSender _email;
    private readonly HttpClient _httpClient;
    private readonly AnthropicOptions _anthropic;
    private readonly AdminOptions _admin;
    private readonly EmailOptions _emailOptions;
    private readonly SiteOptions _site;
    private readonly IDocoveeLogger _logger;

    public BlogGenerationService(
        DocoveeDbContext db,
        IContentPageService content,
        IAppSettingsService appSettings,
        IEmailSender email,
        HttpClient httpClient,
        IOptions<AnthropicOptions> anthropic,
        IOptions<AdminOptions> admin,
        IOptions<EmailOptions> emailOptions,
        IOptions<SiteOptions> site,
        IDocoveeLogger logger)
    {
        _db = db;
        _content = content;
        _appSettings = appSettings;
        _email = email;
        _httpClient = httpClient;
        _anthropic = anthropic.Value;
        _admin = admin.Value;
        _emailOptions = emailOptions.Value;
        _site = site.Value;
        _logger = logger;
    }

    public async Task<IReadOnlyList<BlogGenerationTopic>> GetTopicsAsync(CancellationToken cancellationToken = default) =>
        await _db.BlogGenerationTopics
            .AsNoTracking()
            .Include(t => t.ContentPage)
            .OrderBy(t => t.SortOrder)
            .ThenBy(t => t.Id)
            .ToListAsync(cancellationToken);

    public Task<BlogGenerationTopic?> GetTopicByIdAsync(int id, CancellationToken cancellationToken = default) =>
        _db.BlogGenerationTopics
            .Include(t => t.ContentPage)
            .FirstOrDefaultAsync(t => t.Id == id, cancellationToken);

    public async Task<BlogGenerationTopic> AddTopicAsync(string topic, CancellationToken cancellationToken = default)
    {
        var text = (topic ?? string.Empty).Trim();
        if (text.Length == 0)
            throw new ArgumentException("Topic is required.", nameof(topic));
        if (text.Length > 500)
            text = text[..500];

        var maxOrder = await _db.BlogGenerationTopics.MaxAsync(t => (int?)t.SortOrder, cancellationToken) ?? 0;
        var row = new BlogGenerationTopic
        {
            Topic = text,
            SortOrder = maxOrder + 1,
            Status = BlogGenerationTopicStatuses.Pending,
            CreatedAtUtc = DateTime.UtcNow
        };
        _db.BlogGenerationTopics.Add(row);
        await _db.SaveChangesAsync(cancellationToken);
        return row;
    }

    public async Task<bool> DeleteTopicAsync(int id, CancellationToken cancellationToken = default)
    {
        var row = await _db.BlogGenerationTopics.FirstOrDefaultAsync(t => t.Id == id, cancellationToken);
        if (row is null) return false;
        if (row.Status == BlogGenerationTopicStatuses.Generated && row.ContentPageId.HasValue)
            return false;

        _db.BlogGenerationTopics.Remove(row);
        await _db.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<BlogGenerationResult> ProcessDueGenerationAsync(
        bool force = false,
        CancellationToken cancellationToken = default)
    {
        var settings = await _appSettings.GetBlogGenerationSettingsAsync(cancellationToken);
        if (!force && !settings.Enabled)
            return new BlogGenerationResult { Success = false, Error = "Blog generation is disabled." };

        if (!force)
        {
            if (settings.LastRunUtc.HasValue
                && DateTime.UtcNow < settings.LastRunUtc.Value.AddDays(settings.IntervalDays))
            {
                return new BlogGenerationResult { Success = false, Error = "Not due yet." };
            }
        }

        var next = await _db.BlogGenerationTopics
            .Where(t => t.Status == BlogGenerationTopicStatuses.Pending)
            .OrderBy(t => t.SortOrder)
            .ThenBy(t => t.Id)
            .FirstOrDefaultAsync(cancellationToken);

        if (next is null)
            return new BlogGenerationResult { Success = false, Error = "No pending topics." };

        return await GenerateTopicInternalAsync(next, notify: true, recordRun: true, cancellationToken);
    }

    public async Task<BlogGenerationResult> GenerateTopicAsync(
        int topicId,
        bool notify = true,
        CancellationToken cancellationToken = default)
    {
        var topic = await _db.BlogGenerationTopics
            .Include(t => t.ContentPage)
            .FirstOrDefaultAsync(t => t.Id == topicId, cancellationToken);
        if (topic is null)
            return new BlogGenerationResult { Success = false, Error = "Topic not found." };

        if (topic.Status == BlogGenerationTopicStatuses.Generated && topic.ContentPageId.HasValue)
            return await RegenerateAsync(topicId, topic.CustomPrompt, cancellationToken);

        return await GenerateTopicInternalAsync(topic, notify, recordRun: notify, cancellationToken);
    }

    public async Task<BlogGenerationResult> RegenerateAsync(
        int topicId,
        string? customPrompt,
        CancellationToken cancellationToken = default)
    {
        var topic = await _db.BlogGenerationTopics
            .Include(t => t.ContentPage)
            .FirstOrDefaultAsync(t => t.Id == topicId, cancellationToken);
        if (topic is null)
            return new BlogGenerationResult { Success = false, Error = "Topic not found." };
        if (topic.ContentPage is null)
            return new BlogGenerationResult { Success = false, Error = "No draft to regenerate. Generate first." };

        if (!string.IsNullOrWhiteSpace(customPrompt))
            topic.CustomPrompt = customPrompt.Trim();

        try
        {
            var generated = await CallClaudeAsync(topic.Topic, topic.CustomPrompt, cancellationToken);
            await ApplyGeneratedToPageAsync(topic.ContentPage, generated, excludePageId: topic.ContentPage.Id, cancellationToken);
            topic.Status = BlogGenerationTopicStatuses.Generated;
            topic.LastError = null;
            topic.GeneratedAtUtc = DateTime.UtcNow;
            await _db.SaveChangesAsync(cancellationToken);

            return new BlogGenerationResult { Success = true, Topic = topic, Page = topic.ContentPage };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Blog regenerate failed for topic {TopicId}", topicId);
            topic.Status = BlogGenerationTopicStatuses.Failed;
            topic.LastError = Truncate(ex.Message, 2000);
            await _db.SaveChangesAsync(cancellationToken);
            return new BlogGenerationResult { Success = false, Error = topic.LastError, Topic = topic };
        }
    }

    public async Task<BlogGenerationResult> SaveDraftEditsAsync(
        int topicId,
        string title,
        string slug,
        string? metaDescription,
        string? excerpt,
        string? bodyHtml,
        string? customPrompt,
        CancellationToken cancellationToken = default)
    {
        var topic = await _db.BlogGenerationTopics
            .Include(t => t.ContentPage)
            .FirstOrDefaultAsync(t => t.Id == topicId, cancellationToken);
        if (topic?.ContentPage is null)
            return new BlogGenerationResult { Success = false, Error = "Draft not found." };

        title = (title ?? string.Empty).Trim();
        slug = Slugify(slug);
        if (title.Length == 0)
            return new BlogGenerationResult { Success = false, Error = "Title is required." };
        if (slug.Length == 0)
            return new BlogGenerationResult { Success = false, Error = "Slug is required." };
        if (!string.IsNullOrEmpty(excerpt) && excerpt.Length > 155)
            return new BlogGenerationResult { Success = false, Error = "Excerpt must be 155 characters or fewer." };

        if (await _content.SlugExistsAsync(slug, topic.ContentPage.Id, cancellationToken))
            return new BlogGenerationResult { Success = false, Error = "That slug is already in use." };

        topic.ContentPage.Title = title.Length > 300 ? title[..300] : title;
        topic.ContentPage.Slug = slug.Length > 200 ? slug[..200] : slug;
        topic.ContentPage.MetaDescription = Truncate(metaDescription?.Trim(), 500);
        topic.ContentPage.Excerpt = Truncate(excerpt?.Trim(), 500);
        topic.ContentPage.BodyHtml = bodyHtml;
        topic.ContentPage.UpdatedAtUtc = DateTime.UtcNow;
        if (customPrompt is not null)
            topic.CustomPrompt = string.IsNullOrWhiteSpace(customPrompt) ? null : customPrompt.Trim();

        await _db.SaveChangesAsync(cancellationToken);
        return new BlogGenerationResult { Success = true, Topic = topic, Page = topic.ContentPage };
    }

    public async Task<BlogGenerationResult> PublishAsync(int topicId, CancellationToken cancellationToken = default)
    {
        var topic = await _db.BlogGenerationTopics
            .Include(t => t.ContentPage)
            .FirstOrDefaultAsync(t => t.Id == topicId, cancellationToken);
        if (topic?.ContentPage is null)
            return new BlogGenerationResult { Success = false, Error = "Draft not found." };

        topic.ContentPage.IsPublished = true;
        topic.ContentPage.UpdatedAtUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);
        await MarkBlogNotificationsReadForTopicAsync(topicId, cancellationToken);
        return new BlogGenerationResult { Success = true, Topic = topic, Page = topic.ContentPage };
    }

    public async Task<IReadOnlyList<AdminNotification>> GetUnreadAdminNotificationsAsync(
        CancellationToken cancellationToken = default) =>
        await _db.AdminNotifications
            .AsNoTracking()
            .Where(n => !n.IsRead)
            .OrderByDescending(n => n.CreatedAtUtc)
            .Take(20)
            .ToListAsync(cancellationToken);

    public async Task MarkAdminNotificationReadAsync(int id, CancellationToken cancellationToken = default)
    {
        var row = await _db.AdminNotifications.FirstOrDefaultAsync(n => n.Id == id, cancellationToken);
        if (row is null || row.IsRead) return;
        row.IsRead = true;
        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task MarkBlogNotificationsReadForTopicAsync(int topicId, CancellationToken cancellationToken = default)
    {
        var link = $"/Admin/BlogGeneration/Review/{topicId}";
        var rows = await _db.AdminNotifications
            .Where(n => !n.IsRead && n.Type == AdminNotificationTypes.BlogDraftReady && n.LinkUrl == link)
            .ToListAsync(cancellationToken);
        if (rows.Count == 0) return;
        foreach (var row in rows)
            row.IsRead = true;
        await _db.SaveChangesAsync(cancellationToken);
    }

    private async Task<BlogGenerationResult> GenerateTopicInternalAsync(
        BlogGenerationTopic topic,
        bool notify,
        bool recordRun,
        CancellationToken cancellationToken)
    {
        try
        {
            var generated = await CallClaudeAsync(topic.Topic, topic.CustomPrompt, cancellationToken);

            ContentPage page;
            if (topic.ContentPageId.HasValue)
            {
                page = await _content.GetByIdAsync(topic.ContentPageId.Value, cancellationToken)
                    ?? throw new InvalidOperationException("Linked content page missing.");
                await ApplyGeneratedToPageAsync(page, generated, excludePageId: page.Id, cancellationToken);
            }
            else
            {
                page = new ContentPage
                {
                    PageType = "blog",
                    IsPublished = false,
                    CreatedAtUtc = DateTime.UtcNow,
                    UpdatedAtUtc = DateTime.UtcNow
                };
                await ApplyGeneratedToPageAsync(page, generated, excludePageId: null, cancellationToken);
                page = await _content.CreateAsync(page, cancellationToken);
                topic.ContentPageId = page.Id;
            }

            topic.Status = BlogGenerationTopicStatuses.Generated;
            topic.LastError = null;
            topic.GeneratedAtUtc = DateTime.UtcNow;
            await _db.SaveChangesAsync(cancellationToken);

            if (recordRun)
                await _appSettings.RecordBlogGenerationRunAsync(DateTime.UtcNow, cancellationToken);

            if (notify)
                await NotifyAdminAsync(topic, page, cancellationToken);

            topic.ContentPage = page;
            return new BlogGenerationResult { Success = true, Topic = topic, Page = page };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Blog generation failed for topic {TopicId}", topic.Id);
            topic.Status = BlogGenerationTopicStatuses.Failed;
            topic.LastError = Truncate(ex.Message, 2000);
            await _db.SaveChangesAsync(cancellationToken);
            return new BlogGenerationResult { Success = false, Error = topic.LastError, Topic = topic };
        }
    }

    private async Task NotifyAdminAsync(BlogGenerationTopic topic, ContentPage page, CancellationToken cancellationToken)
    {
        var reviewPath = $"/Admin/BlogGeneration/Review/{topic.Id}";
        var notification = new AdminNotification
        {
            Type = AdminNotificationTypes.BlogDraftReady,
            Title = "New blog draft ready",
            Message = $"A draft for “{Truncate(topic.Topic, 120)}” is ready to review.",
            LinkUrl = reviewPath,
            IsRead = false,
            CreatedAtUtc = DateTime.UtcNow
        };
        _db.AdminNotifications.Add(notification);
        await _db.SaveChangesAsync(cancellationToken);

        var to = (_admin.Email ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(to) || !_email.IsConfigured)
        {
            _logger.LogWarning("Blog draft ready but admin email not sent (missing Admin:Email or Email config).");
            return;
        }

        var baseUrl = (_emailOptions.PublicBaseUrl ?? string.Empty).Trim().TrimEnd('/');
        var link = string.IsNullOrWhiteSpace(baseUrl) ? reviewPath : $"{baseUrl}{reviewPath}";
        var siteName = string.IsNullOrWhiteSpace(_site.Name) ? "NuviDoc" : _site.Name.Trim();
        var subject = $"{siteName}: Blog draft ready — {Truncate(page.Title, 80)}";
        var text = $"A new blog draft was generated for topic: {topic.Topic}\n\nTitle: {page.Title}\nReview: {link}\n";
        var html = $"""
            <p>A new blog draft was generated for topic: <strong>{System.Net.WebUtility.HtmlEncode(topic.Topic)}</strong></p>
            <p>Title: {System.Net.WebUtility.HtmlEncode(page.Title)}</p>
            <p><a href="{System.Net.WebUtility.HtmlEncode(link)}">Review and publish</a></p>
            <p>You must sign in as Admin to open the review page.</p>
            """;

        try
        {
            await _email.SendAsync(to, subject, text, html, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Failed to email admin about blog draft {TopicId}: {Error}", topic.Id, ex.Message);
        }
    }

    private async Task ApplyGeneratedToPageAsync(
        ContentPage page,
        GeneratedBlogContent generated,
        int? excludePageId,
        CancellationToken cancellationToken)
    {
        page.Title = Truncate(generated.Title, 300) ?? "Untitled";
        page.Slug = await EnsureUniqueSlugAsync(generated.Slug, excludePageId, cancellationToken);
        page.MetaDescription = Truncate(generated.MetaDescription, 500);
        page.Excerpt = Truncate(generated.Excerpt, 155);
        page.BodyHtml = generated.BodyHtml;
        page.UpdatedAtUtc = DateTime.UtcNow;
    }

    private async Task<GeneratedBlogContent> CallClaudeAsync(
        string topic,
        string? customPrompt,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_anthropic.ApiKey) || string.IsNullOrWhiteSpace(_anthropic.Model))
            throw new InvalidOperationException("Anthropic API key/model is not configured.");

        var system = """
            You are a marketing content writer for NuviDoc, a Houston-focused dental implant matching service.
            Patients chat with Nuvi (AI concierge), get matched to specialists, and NuviDoc can call offices to book consults.
            Matching is free for patients. Write clear, helpful, SEO-friendly blog posts in HTML.
            Do not invent medical claims. Do not include PHI. Prefer Houston / dental implant context when relevant.

            Return the post in EXACTLY this plain-text format (no JSON, no markdown fences):
            TITLE: <compelling blog title>
            SLUG: <lowercase-url-slug-with-hyphens>
            META: <meta description under 155 characters>
            EXCERPT: <listing excerpt under 155 characters>
            BODY_HTML:
            <p>...</p>
            END_BODY_HTML

            BODY_HTML rules: use only p, h2, h3, ul, li, strong, a tags. No html/body/script tags.
            Do not put TITLE/SLUG/META/EXCERPT/BODY_HTML/END_BODY_HTML labels inside the HTML body.
            """;

        var user = new StringBuilder();
        user.AppendLine($"Topic: {topic}");
        if (!string.IsNullOrWhiteSpace(customPrompt))
        {
            user.AppendLine();
            user.AppendLine("Additional instructions from the editor:");
            user.AppendLine(customPrompt.Trim());
        }
        user.AppendLine();
        user.AppendLine("Generate the blog in the exact TITLE/SLUG/META/EXCERPT/BODY_HTML format now.");

        var payload = AnthropicApiHelper.BuildPayload(
            _anthropic,
            maxTokens: 4096,
            system: system,
            messages: new[] { new { role = "user", content = user.ToString() } });

        using var httpRequest = AnthropicApiHelper.CreateMessageRequest(_anthropic, payload);
        var response = await _httpClient.SendAsync(httpRequest, cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Anthropic API returned {(int)response.StatusCode}: {Truncate(responseBody, 400)}");

        var text = AnthropicApiHelper.ExtractTextContent(responseBody);
        return ParseGeneratedContent(text);
    }

    private static GeneratedBlogContent ParseGeneratedContent(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            throw new InvalidOperationException("Empty response from Claude.");

        if (TryParseDelimited(text, out var delimited))
            return ValidateGenerated(delimited);

        if (TryParseJsonObject(text, out var fromJson))
            return ValidateGenerated(fromJson);

        if (TryParseJsonLenient(text, out var lenient))
            return ValidateGenerated(lenient);

        throw new InvalidOperationException(
            "Could not parse Claude blog response. Expected TITLE/SLUG/META/EXCERPT/BODY_HTML format.");
    }

    private static GeneratedBlogContent ValidateGenerated(GeneratedBlogContent content)
    {
        var title = (content.Title ?? string.Empty).Trim();
        var slug = Slugify(content.Slug);
        if (string.IsNullOrWhiteSpace(slug) && !string.IsNullOrWhiteSpace(title))
            slug = Slugify(title);
        var body = (content.BodyHtml ?? string.Empty).Trim();

        if (string.IsNullOrWhiteSpace(title))
            throw new InvalidOperationException("Claude response missing title.");
        if (string.IsNullOrWhiteSpace(slug))
            throw new InvalidOperationException("Claude response missing slug.");
        if (string.IsNullOrWhiteSpace(body))
            throw new InvalidOperationException("Claude response missing bodyHtml.");

        return new GeneratedBlogContent
        {
            Title = title,
            Slug = slug,
            MetaDescription = content.MetaDescription?.Trim(),
            Excerpt = content.Excerpt?.Trim(),
            BodyHtml = body
        };
    }

    private static bool TryParseDelimited(string text, out GeneratedBlogContent content)
    {
        content = new GeneratedBlogContent();
        var cleaned = text.Trim();
        if (cleaned.StartsWith("```"))
        {
            var firstNl = cleaned.IndexOf('\n');
            if (firstNl > 0)
                cleaned = cleaned[(firstNl + 1)..];
            if (cleaned.EndsWith("```", StringComparison.Ordinal))
                cleaned = cleaned[..^3].TrimEnd();
        }

        var match = DelimitedFieldRegex.Match(cleaned);
        if (!match.Success)
        {
            // Looser: find BODY_HTML: ... END_BODY_HTML even if field order varies slightly.
            var bodyStart = cleaned.IndexOf("BODY_HTML:", StringComparison.OrdinalIgnoreCase);
            var bodyEnd = cleaned.LastIndexOf("END_BODY_HTML", StringComparison.OrdinalIgnoreCase);
            if (bodyStart < 0 || bodyEnd <= bodyStart)
                return false;

            var header = cleaned[..bodyStart];
            var body = cleaned[(bodyStart + "BODY_HTML:".Length)..bodyEnd].Trim();
            content = new GeneratedBlogContent
            {
                Title = ExtractLabeledLine(header, "TITLE"),
                Slug = ExtractLabeledLine(header, "SLUG"),
                MetaDescription = ExtractLabeledLine(header, "META"),
                Excerpt = ExtractLabeledLine(header, "EXCERPT"),
                BodyHtml = body
            };
            return !string.IsNullOrWhiteSpace(content.Title) && !string.IsNullOrWhiteSpace(content.BodyHtml);
        }

        content = new GeneratedBlogContent
        {
            Title = match.Groups[1].Value.Trim(),
            Slug = match.Groups[2].Value.Trim(),
            MetaDescription = match.Groups[3].Value.Trim(),
            Excerpt = match.Groups[4].Value.Trim(),
            BodyHtml = match.Groups[5].Value.Trim()
        };
        return true;
    }

    private static string ExtractLabeledLine(string text, string label)
    {
        var regex = new Regex(@"^" + Regex.Escape(label) + @":\s*(.*)$",
            RegexOptions.IgnoreCase | RegexOptions.Multiline);
        var m = regex.Match(text);
        return m.Success ? m.Groups[1].Value.Trim() : string.Empty;
    }

    private static bool TryParseJsonObject(string text, out GeneratedBlogContent content)
    {
        content = new GeneratedBlogContent();
        try
        {
            var json = text.Trim();
            var fence = JsonFenceRegex.Match(json);
            if (fence.Success)
                json = fence.Groups[1].Value.Trim();
            else
            {
                var start = json.IndexOf('{');
                var end = json.LastIndexOf('}');
                if (start >= 0 && end > start)
                    json = json[start..(end + 1)];
            }

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            string Req(string name)
            {
                if (!root.TryGetProperty(name, out var p)) return string.Empty;
                return p.ValueKind == JsonValueKind.String
                    ? (p.GetString() ?? string.Empty).Trim()
                    : p.ToString().Trim();
            }

            content = new GeneratedBlogContent
            {
                Title = Req("title"),
                Slug = Req("slug"),
                MetaDescription = Req("metaDescription"),
                Excerpt = Req("excerpt"),
                BodyHtml = Req("bodyHtml")
            };
            return !string.IsNullOrWhiteSpace(content.Title) && !string.IsNullOrWhiteSpace(content.BodyHtml);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// Fallback when Claude returns almost-JSON but breaks bodyHtml escaping
    /// (e.g. raw quotes / closing tags like &lt;/p&gt;).
    /// </summary>
    private static bool TryParseJsonLenient(string text, out GeneratedBlogContent content)
    {
        content = new GeneratedBlogContent();
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        if (start < 0 || end <= start)
            return false;

        var json = text[start..(end + 1)];
        var title = ExtractJsonStringProperty(json, "title");
        var slug = ExtractJsonStringProperty(json, "slug");
        var meta = ExtractJsonStringProperty(json, "metaDescription");
        var excerpt = ExtractJsonStringProperty(json, "excerpt");
        var body = ExtractJsonStringProperty(json, "bodyHtml");
        if (string.IsNullOrWhiteSpace(body))
            body = ExtractJsonStringPropertyGreedy(json, "bodyHtml");

        if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(body))
            return false;

        content = new GeneratedBlogContent
        {
            Title = UnescapeJsonString(title),
            Slug = UnescapeJsonString(slug),
            MetaDescription = UnescapeJsonString(meta),
            Excerpt = UnescapeJsonString(excerpt),
            BodyHtml = UnescapeJsonString(body)
        };
        return true;
    }

    private static string ExtractJsonStringProperty(string json, string name)
    {
        var key = $"\"{name}\"";
        var keyIdx = json.IndexOf(key, StringComparison.OrdinalIgnoreCase);
        if (keyIdx < 0) return string.Empty;
        var colon = json.IndexOf(':', keyIdx + key.Length);
        if (colon < 0) return string.Empty;

        var i = colon + 1;
        while (i < json.Length && char.IsWhiteSpace(json[i])) i++;
        if (i >= json.Length || json[i] != '"') return string.Empty;
        i++;

        var sb = new StringBuilder();
        for (; i < json.Length; i++)
        {
            var c = json[i];
            if (c == '\\' && i + 1 < json.Length)
            {
                sb.Append(c);
                sb.Append(json[++i]);
                continue;
            }
            if (c == '"')
                break;
            sb.Append(c);
        }

        return sb.ToString();
    }

    /// <summary>
    /// For broken bodyHtml where inner quotes terminate early: take from after "bodyHtml": "
    /// through the last quote before the final closing brace.
    /// </summary>
    private static string ExtractJsonStringPropertyGreedy(string json, string name)
    {
        var key = $"\"{name}\"";
        var keyIdx = json.IndexOf(key, StringComparison.OrdinalIgnoreCase);
        if (keyIdx < 0) return string.Empty;
        var colon = json.IndexOf(':', keyIdx + key.Length);
        if (colon < 0) return string.Empty;

        var i = colon + 1;
        while (i < json.Length && char.IsWhiteSpace(json[i])) i++;
        if (i >= json.Length || json[i] != '"') return string.Empty;
        i++;

        var lastQuote = json.LastIndexOf('"');
        if (lastQuote <= i) return string.Empty;
        return json[i..lastQuote];
    }

    private static string UnescapeJsonString(string value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        return value
            .Replace("\\n", "\n", StringComparison.Ordinal)
            .Replace("\\r", "\r", StringComparison.Ordinal)
            .Replace("\\t", "\t", StringComparison.Ordinal)
            .Replace("\\\"", "\"", StringComparison.Ordinal)
            .Replace("\\/", "/", StringComparison.Ordinal)
            .Replace("\\\\", "\\", StringComparison.Ordinal);
    }

    private async Task<string> EnsureUniqueSlugAsync(string slug, int? excludeId, CancellationToken cancellationToken)
    {
        var baseSlug = Slugify(slug);
        if (baseSlug.Length == 0) baseSlug = "blog-post";
        if (baseSlug.Length > 180) baseSlug = baseSlug[..180].Trim('-');

        var candidate = baseSlug;
        var n = 2;
        while (await _content.SlugExistsAsync(candidate, excludeId, cancellationToken))
        {
            candidate = $"{baseSlug}-{n}";
            n++;
            if (n > 100)
                candidate = $"{baseSlug}-{Guid.NewGuid():N}"[..200];
        }

        return candidate.Length > 200 ? candidate[..200].Trim('-') : candidate;
    }

    private static string Slugify(string? input)
    {
        var s = (input ?? string.Empty).Trim().ToLowerInvariant();
        s = s.Replace(' ', '-');
        s = NonSlugChars.Replace(s, "-");
        s = MultiHyphen.Replace(s, "-").Trim('-');
        return s;
    }

    private static string? Truncate(string? value, int max)
    {
        if (value is null) return null;
        var t = value.Trim();
        if (t.Length == 0) return null;
        return t.Length <= max ? t : t[..max];
    }

    private sealed class GeneratedBlogContent
    {
        public string Title { get; init; } = string.Empty;
        public string Slug { get; init; } = string.Empty;
        public string? MetaDescription { get; init; }
        public string? Excerpt { get; init; }
        public string BodyHtml { get; init; } = string.Empty;
    }
}
