namespace Docovee.BLL.Configuration;

public class AnthropicOptions
{
    public const string SectionName = "Anthropic";
    public string ApiKey { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public bool EnableWebSearch { get; set; }
    public int WebSearchMaxUses { get; set; } = 5;

    /// <summary>
    /// Skip live Claude Google-review fetch when last successful fetch is within this many days.
    /// Reviews are served from the saved file instead. Default: 7.
    /// </summary>
    public int GoogleReviewCacheDays { get; set; } = 7;
}
