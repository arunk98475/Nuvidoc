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

    /// <summary>Conversational agent ID for branded Nuvi voice agent.</summary>
    public string AgentId { get; set; } = string.Empty;

    /// <summary>
    /// Phone number ID from ElevenLabs after importing the Twilio number
    /// (agent_phone_number_id for POST /v1/convai/twilio/outbound-call).
    /// </summary>
    public string? AgentPhoneNumberId { get; set; }

    /// <summary>API base URL (override only if needed).</summary>
    public string BaseUrl { get; set; } = "https://api.elevenlabs.io";
}
