namespace Docovee.BLL.Services;

public static class DeletedAccountHelper
{
    public static string DeletedUsername(int id) => $"deleted-{id}@deleted.invalid";

    public static bool IsDeletedUsername(string? username)
    {
        if (string.IsNullOrEmpty(username))
            return false;

        return username.StartsWith("deleted-", StringComparison.OrdinalIgnoreCase)
            && username.EndsWith("@deleted.invalid", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Soft-closed accounts become eligible for permanent remove after <paramref name="waitDays"/>.
    /// Wait of 0 means eligible immediately after closure.
    /// </summary>
    public static bool CanPermanentlyRemove(DateTime? deletedAtUtc, int waitDays, DateTime? utcNow = null)
    {
        if (!deletedAtUtc.HasValue)
            return false;

        var now = utcNow ?? DateTime.UtcNow;
        var days = Math.Max(0, waitDays);
        if (days == 0)
            return true;

        return now >= deletedAtUtc.Value.AddDays(days);
    }

    public static DateTime? PermanentRemoveAvailableAtUtc(DateTime? deletedAtUtc, int waitDays)
    {
        if (!deletedAtUtc.HasValue)
            return null;

        var days = Math.Max(0, waitDays);
        return days == 0 ? deletedAtUtc : deletedAtUtc.Value.AddDays(days);
    }
}
