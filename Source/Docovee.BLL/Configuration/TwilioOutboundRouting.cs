using Docovee.BLL.Services;

namespace Docovee.BLL.Configuration;

/// <summary>
/// Resolves the actual Twilio destination for outbound SMS, WhatsApp, and calls.
/// When <see cref="TwilioOptions.OutboundOverrideToNumber"/> is set, every outbound
/// message/call is redirected there instead of the patient/doctor number from the database.
/// Leave the override empty for production (real destinations).
/// </summary>
public static class TwilioOutboundRouting
{
    /// <summary>True when a non-empty E.164 override is configured.</summary>
    public static bool IsOverrideActive(TwilioOptions options) =>
        !string.IsNullOrWhiteSpace(GetOverrideE164(options));

    /// <summary>Normalized override number, or null when inactive.</summary>
    public static string? GetOverrideE164(TwilioOptions options) =>
        ElevenLabsTwilioCallingService.ToE164(options.OutboundOverrideToNumber);

    /// <summary>
    /// Destination for SMS / voice PSTN.
    /// Override wins when set; otherwise the intended patient/doctor number.
    /// </summary>
    public static string? ResolveToNumber(TwilioOptions options, string? intendedPhone)
    {
        var overrideTo = GetOverrideE164(options);
        if (!string.IsNullOrWhiteSpace(overrideTo))
            return overrideTo;

        return ElevenLabsTwilioCallingService.ToE164(StripWhatsAppPrefix(intendedPhone));
    }

    /// <summary>
    /// Destination for WhatsApp (<c>whatsapp:+E164</c>).
    /// Override wins when set; otherwise the intended number with WhatsApp prefix.
    /// </summary>
    public static string? ResolveWhatsAppTo(TwilioOptions options, string? intendedPhone)
    {
        var e164 = ResolveToNumber(options, intendedPhone);
        if (string.IsNullOrWhiteSpace(e164))
            return null;

        return e164.StartsWith("whatsapp:", StringComparison.OrdinalIgnoreCase)
            ? e164
            : "whatsapp:" + e164;
    }

    private static string? StripWhatsAppPrefix(string? phone)
    {
        if (string.IsNullOrWhiteSpace(phone))
            return phone;

        var trimmed = phone.Trim();
        if (trimmed.StartsWith("whatsapp:", StringComparison.OrdinalIgnoreCase))
            return trimmed["whatsapp:".Length..].Trim();

        return trimmed;
    }
}
