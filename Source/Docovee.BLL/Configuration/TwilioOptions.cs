namespace Docovee.BLL.Configuration;

/// <summary>
/// Twilio Voice / PSTN settings for Nuvi outbound office calls.
/// Console: Account → API keys & tokens; Phone Numbers → Active numbers.
/// </summary>
public class TwilioOptions
{
    public const string SectionName = "Twilio";

    /// <summary>Account SID (starts with AC…).</summary>
    public string AccountSid { get; set; } = string.Empty;

    /// <summary>Auth Token (keep secret; rotate if exposed).</summary>
    public string AuthToken { get; set; } = string.Empty;

    /// <summary>
    /// E.164 outbound caller ID Nuvi calls from (e.g. +12533083687).
    /// Must be a Voice-capable number owned by this Twilio account.
    /// </summary>
    public string FromNumber { get; set; } = string.Empty;

    /// <summary>
    /// Public HTTPS base URL of this app (no trailing slash), used for voice webhooks
    /// / status callbacks when ConversationRelay or TwiML is wired (e.g. https://yourdomain.com).
    /// </summary>
    public string? PublicBaseUrl { get; set; }

    /// <summary>
    /// When set (E.164, e.g. +1…), all Nuvi outbound office calls dial this number
    /// instead of the doctor's phone from the database. Leave empty for production behavior.
    /// </summary>
    public string? OutboundOverrideToNumber { get; set; }

    /// <summary>
    /// WhatsApp sandbox / business sender for Development phone verification
    /// (e.g. whatsapp:+14155238886).
    /// </summary>
    public string WhatsAppFromNumber { get; set; } = "whatsapp:+14155238886";

    /// <summary>Twilio Content SID for the Development WhatsApp template.</summary>
    public string WhatsAppContentSid { get; set; } = "HXb5b62575e6e4ff6129ad7c8efe1f983e";

    /// <summary>
    /// SMS caller ID for Production verification. Falls back to <see cref="FromNumber"/> when empty.
    /// </summary>
    public string? SmsFromNumber { get; set; }

    /// <summary>How long a phone verification code stays valid.</summary>
    public int VerifyCodeExpiryMinutes { get; set; } = 10;
}
