namespace Docovee.BLL.Configuration;

/// <summary>
/// Voice / outbound agent PHI controls (ElevenLabs + Twilio).
/// </summary>
public class VoiceOptions
{
    public const string SectionName = "Voice";

    /// <summary>
    /// When false (default), outbound agent dynamic variables omit rich PHI
    /// (full name, email, chief complaint detail, insurance). Callback number and slot remain.
    /// </summary>
    public bool IncludePhi { get; set; }
}
