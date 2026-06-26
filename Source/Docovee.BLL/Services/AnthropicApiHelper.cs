using System.Text;
using System.Text.Json;
using Docovee.BLL.Configuration;

namespace Docovee.BLL.Services;

public static class AnthropicApiHelper
{
    private const string MessagesUrl = "https://api.anthropic.com/v1/messages";
    private const string WebSearchToolType = "web_search_20250305";

    public static Dictionary<string, object> BuildPayload(
        AnthropicOptions options,
        int maxTokens,
        string system,
        IEnumerable<object> messages,
        bool includeWebSearch = false)
    {
        var payload = new Dictionary<string, object>
        {
            ["model"] = options.Model.Trim(),
            ["max_tokens"] = maxTokens,
            ["system"] = system,
            ["messages"] = messages.ToList()
        };

        if (includeWebSearch && options.EnableWebSearch)
        {
            payload["tools"] = new object[]
            {
                new Dictionary<string, object>
                {
                    ["type"] = WebSearchToolType,
                    ["name"] = "web_search",
                    ["max_uses"] = Math.Max(1, options.WebSearchMaxUses)
                }
            };
        }

        return payload;
    }

    public static HttpRequestMessage CreateMessageRequest(AnthropicOptions options, object payload)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, MessagesUrl);
        request.Headers.Add("x-api-key", options.ApiKey);
        request.Headers.Add("anthropic-version", "2023-06-01");
        request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        return request;
    }

    public static string ExtractTextContent(string responseBody)
    {
        using var doc = JsonDocument.Parse(responseBody);
        return ExtractTextContent(doc.RootElement);
    }

    public static string ExtractTextContent(JsonElement root)
    {
        if (!root.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
            return string.Empty;

        var text = new StringBuilder();
        foreach (var block in content.EnumerateArray())
        {
            if (block.TryGetProperty("type", out var type) && type.GetString() == "text"
                && block.TryGetProperty("text", out var textProp))
            {
                text.Append(textProp.GetString());
            }
        }

        return text.ToString();
    }
}
