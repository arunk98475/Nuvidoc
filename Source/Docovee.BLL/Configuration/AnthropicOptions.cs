namespace Docovee.BLL.Configuration;

public class AnthropicOptions
{
    public const string SectionName = "Anthropic";
    public string ApiKey { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public bool EnableWebSearch { get; set; }
    public int WebSearchMaxUses { get; set; } = 5;

    /// <summary>
    /// When false (default), treat prompts as non-PHI for vendors: de-identify when
    /// <see cref="DeidentifyPrompts"/> is enabled. Does not control web search —
    /// use <see cref="EnableWebSearch"/> for that.
    /// Set true only with an Anthropic BAA and explicit product approval.
    /// </summary>
    public bool AllowPhi { get; set; }

    /// <summary>
    /// When true (default), strip email/phone/DOB/member-id/street patterns from system + messages
    /// before calling the Messages API.
    /// </summary>
    public bool DeidentifyPrompts { get; set; } = true;

    /// <summary>
    /// Skip live Claude Google-review fetch when last successful fetch is within this many days.
    /// Reviews are served from the saved file instead. Default: 7.
    /// </summary>
    public int GoogleReviewCacheDays { get; set; } = 7;
}
