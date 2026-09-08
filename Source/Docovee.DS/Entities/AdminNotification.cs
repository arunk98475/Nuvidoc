namespace Docovee.DS.Entities;

/// <summary>In-app notification for the Admin dashboard (e.g. blog draft ready).</summary>
public class AdminNotification
{
    public int Id { get; set; }

    public string Type { get; set; } = AdminNotificationTypes.BlogDraftReady;

    public string Title { get; set; } = string.Empty;

    public string Message { get; set; } = string.Empty;

    public string? LinkUrl { get; set; }

    public bool IsRead { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

public static class AdminNotificationTypes
{
    public const string BlogDraftReady = "blog_draft_ready";
}
