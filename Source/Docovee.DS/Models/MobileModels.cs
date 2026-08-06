namespace Docovee.DS.Models;

/// <summary>Initial payload for the native mobile home screen.</summary>
public class MobileBootstrapDto
{
    public string SiteName { get; set; } = "NuviDoc";
    public string ChatBotName { get; set; } = "Nuvi";
    public string Tagline { get; set; } = string.Empty;
    public string WelcomeMessage { get; set; } = string.Empty;
    public IReadOnlyList<string> QuickConcerns { get; set; } = Array.Empty<string>();
    public string ApiStatus { get; set; } = "ok";
}

/// <summary>Patient registration from the native app (email is the login username).</summary>
public class MobilePatientRegisterRequest
{
    public string FullName { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string Phone { get; set; } = string.Empty;
    public DateOnly DateOfBirth { get; set; }
    public string Password { get; set; } = string.Empty;
    public string ConfirmPassword { get; set; } = string.Empty;
}

public class MobileEmailAvailableResponse
{
    public bool Available { get; set; }
    public string? Message { get; set; }
}

public class MobilePatientLoginRequest
{
    public string Email { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
}

public class MobilePatientLoginResponse
{
    public bool Success { get; set; }
    public string? Message { get; set; }
    public string? FullName { get; set; }
    public string? Email { get; set; }
}
