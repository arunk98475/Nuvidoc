namespace nuvidoc.Services;

/// <summary>
/// API base URL for the Docovee backend (no trailing slash).
/// Change this value when switching hosts.
/// LAN (this PC): http://192.168.1.13:37788
/// Android emulator → host: http://10.0.2.2:37788 (same port as lan profile)
/// Local only: http://localhost:5274
/// </summary>
public static class ApiConfig
{
#if DEBUG
    public const string BaseUrl = "http://192.168.1.13:37788";
#else
    public const string BaseUrl = "https://houston.nuvidoc.com";
#endif
}
