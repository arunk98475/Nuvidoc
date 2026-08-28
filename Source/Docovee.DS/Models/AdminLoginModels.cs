namespace Docovee.DS.Models;

public sealed class AdminLoginResult
{
    public bool Success { get; init; }
    public string? Error { get; init; }
    public bool RequiresOtp { get; init; }
    public string? OtpSessionToken { get; init; }
    public string? OtpMessage { get; init; }
}
