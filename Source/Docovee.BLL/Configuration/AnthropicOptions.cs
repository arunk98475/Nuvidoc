namespace Docovee.BLL.Configuration;

public class AnthropicOptions
{
    public const string SectionName = "Anthropic";
    public string ApiKey { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public bool EnableWebSearch { get; set; }
    public int WebSearchMaxUses { get; set; } = 5;
}
