namespace Docovee.DS.Entities;

/// <summary>
/// Queue item for scheduled Claude blog generation. One topic produces at most one ContentPage.
/// </summary>
public class BlogGenerationTopic
{
    public int Id { get; set; }

    public string Topic { get; set; } = string.Empty;

    /// <summary>Optional prompt override used on regenerate / retry.</summary>
    public string? CustomPrompt { get; set; }

    public int SortOrder { get; set; }

    /// <summary>Pending | Generated | Failed</summary>
    public string Status { get; set; } = BlogGenerationTopicStatuses.Pending;

    public int? ContentPageId { get; set; }

    public ContentPage? ContentPage { get; set; }

    public string? LastError { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public DateTime? GeneratedAtUtc { get; set; }
}

public static class BlogGenerationTopicStatuses
{
    public const string Pending = "Pending";
    public const string Generated = "Generated";
    public const string Failed = "Failed";
}
