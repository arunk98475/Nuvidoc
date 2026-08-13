namespace Docovee.BLL.Configuration;

/// <summary>
/// ElevenLabs Agents / voice settings for Nuvi.
/// Optional when using Twilio ConversationRelay TTS alone; required for ElevenLabs agent outbound.
/// Dashboard: https://elevenlabs.io/app → Profile → API Key; Agents → agent + phone numbers.
/// </summary>
public class ElevenLabsOptions
{
    public const string SectionName = "ElevenLabs";

    /// <summary>API key (xi-api-key header).</summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>Conversational agent ID for Nuvi voice (booking and cancellation).</summary>
    public string AgentId { get; set; } = string.Empty;

    /// <summary>
    /// Phone number ID from ElevenLabs after importing the Twilio number
    /// (agent_phone_number_id for POST /v1/convai/twilio/outbound-call).
    /// </summary>
    public string? AgentPhoneNumberId { get; set; }

    /// <summary>API base URL (override only if needed).</summary>
    public string BaseUrl { get; set; } = "https://api.elevenlabs.io";

    /// <summary>
    /// Optional HMAC secret from ElevenLabs post-call webhook settings.
    /// When set, outbound-call polling is skipped and post-call webhooks are the source of truth.
    /// When empty, webhook signature verification is skipped (dev only) and polling is used.
    /// </summary>
    public string? WebhookSecret { get; set; }

    /// <summary>
    /// Seconds to wait after placing an outbound call before the first conversation poll.
    /// Used only when <see cref="WebhookSecret"/> is empty. Default: 45.
    /// </summary>
    public int ConversationPollingDelaySeconds { get; set; } = 45;

    /// <summary>
    /// How many times to retry calling the same doctor before moving on.
    /// 0 = dial once (no retries), 1 = dial up to 2 times, etc.
    /// Default: 1.
    /// </summary>
    public int MaxCallRetriesPerDoctor { get; set; } = 1;

    /// <summary>
    /// Seconds to wait between retry attempts for the same doctor.
    /// Displayed to patients in minutes. Default: 120 (2 minutes).
    /// </summary>
    public int CallRetryDelaySeconds { get; set; } = 120;
}
