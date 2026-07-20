namespace Docovee.BLL.Audit;

/// <summary>
/// Suppresses automatic audit rows during seed/migration (avoids noise and startup slowdown).
/// </summary>
public static class AuditTrailScope
{
    private static readonly AsyncLocal<int> SuppressDepth = new();

    public static bool IsSuppressed => SuppressDepth.Value > 0;

    public static IDisposable Suppress() => new SuppressToken();

    private sealed class SuppressToken : IDisposable
    {
        public SuppressToken() => SuppressDepth.Value++;

        public void Dispose() => SuppressDepth.Value = Math.Max(0, SuppressDepth.Value - 1);
    }
}
