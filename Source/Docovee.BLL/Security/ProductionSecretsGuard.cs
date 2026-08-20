using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace Docovee.BLL.Security;

/// <summary>
/// Fails Production startup when required secrets are missing or obviously unsafe.
/// Development is not gated so local work can run with partial config.
/// </summary>
public static class ProductionSecretsGuard
{
    private static readonly HashSet<string> WeakAdminPasswords = new(StringComparer.OrdinalIgnoreCase)
    {
        "admin",
        "password",
        "password1",
        "passw0rd",
        "changeme",
        "change-me",
        "Admin@123",
        "Admin123",
        "123456",
        "12345678",
        "qwerty",
        "letmein",
        "welcome",
        "default"
    };

    public static void Validate(IHostEnvironment environment, IConfiguration configuration)
    {
        if (environment.IsDevelopment())
            return;

        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(configuration.GetConnectionString("DefaultConnection")))
            errors.Add("ConnectionStrings:DefaultConnection is missing or empty.");

        var anthropicKey = configuration["Anthropic:ApiKey"];
        var anthropicModel = configuration["Anthropic:Model"];
        if (string.IsNullOrWhiteSpace(anthropicKey))
            errors.Add("Anthropic:ApiKey is required in Production.");
        if (string.IsNullOrWhiteSpace(anthropicModel))
            errors.Add("Anthropic:Model is required in Production.");

        var twilioPhoneConfigured =
            !string.IsNullOrWhiteSpace(configuration["Twilio:FromNumber"])
            || !string.IsNullOrWhiteSpace(configuration["Twilio:SmsFromNumber"])
            || !string.IsNullOrWhiteSpace(configuration["Twilio:WhatsAppFromNumber"]);
        if (twilioPhoneConfigured)
        {
            if (string.IsNullOrWhiteSpace(configuration["Twilio:AccountSid"]))
                errors.Add("Twilio:AccountSid is required when a Twilio From/SMS/WhatsApp number is configured.");
            if (string.IsNullOrWhiteSpace(configuration["Twilio:AuthToken"]))
                errors.Add("Twilio:AuthToken is required when a Twilio From/SMS/WhatsApp number is configured.");
        }

        if (!string.IsNullOrWhiteSpace(configuration["ElevenLabs:AgentId"])
            && string.IsNullOrWhiteSpace(configuration["ElevenLabs:ApiKey"]))
        {
            errors.Add("ElevenLabs:ApiKey is required when ElevenLabs:AgentId is set.");
        }

        var adminPassword = configuration["Admin:Password"] ?? string.Empty;
        if (string.IsNullOrWhiteSpace(adminPassword))
            errors.Add("Admin:Password is required in Production.");
        else if (WeakAdminPasswords.Contains(adminPassword.Trim()))
            errors.Add("Admin:Password is a known weak/placeholder value. Set a strong unique password in Production.");

        if (string.IsNullOrWhiteSpace(configuration["Admin:Username"]))
            errors.Add("Admin:Username is required in Production.");

        if (errors.Count == 0)
            return;

        throw new InvalidOperationException(
            "Production startup blocked — fix configuration secrets:" + Environment.NewLine
            + string.Join(Environment.NewLine, errors.Select(e => " - " + e)));
    }
}
