using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Docovee.BLL.Configuration;
using Docovee.logging;
using Microsoft.Extensions.Options;

namespace Docovee.BLL.Services;

public interface IAnthropicValidationService
{
    Task<PollingValidationResult> ValidateAnswerAsync(
        string question,
        string answer,
        string? validationHint,
        string? conversationContext = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Extracts a standardized spoken-language name from free-form text
    /// (e.g. "espanol" → "Spanish"). Prefer catalog matches when clear.
    /// </summary>
    Task<PollingValidationResult> ExtractLanguageNameAsync(
        string answer,
        IReadOnlyList<string>? knownLanguages = null,
        CancellationToken cancellationToken = default);
}

public class PollingValidationResult
{
    public bool IsValid { get; init; }
    public string? RepromptMessage { get; init; }
    public string? NormalizedAnswer { get; init; }
}

public class AnthropicValidationService : IAnthropicValidationService
{
    public static bool LooksLikeGibberish(string input) => IsGibberish(input);

    private static readonly Regex JsonFenceRegex = new(
        @"```(?:json)?\s*(\{[\s\S]*?\})\s*```",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly HttpClient _httpClient;
    private readonly AnthropicOptions _options;
    private readonly IDocoveeLogger _logger;

    public AnthropicValidationService(
        HttpClient httpClient,
        IOptions<AnthropicOptions> options,
        IDocoveeLogger logger)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<PollingValidationResult> ValidateAnswerAsync(
        string question,
        string answer,
        string? validationHint,
        string? conversationContext = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(answer))
        {
            return Invalid($"Could you share a bit more? {question}");
        }

        if (!string.IsNullOrWhiteSpace(_options.ApiKey) && !string.IsNullOrWhiteSpace(_options.Model))
        {
            var aiResult = await ValidateWithAiAsync(question, answer, validationHint, conversationContext, cancellationToken);
            if (aiResult != null)
                return aiResult;
        }

        return FallbackValidate(question, answer, validationHint);
    }

    public async Task<PollingValidationResult> ExtractLanguageNameAsync(
        string answer,
        IReadOnlyList<string>? knownLanguages = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(answer))
            return Invalid("Please type a language name.");

        var trimmed = answer.Trim();
        if (IsGibberish(trimmed))
            return Invalid("I didn't catch a language name — try something like Spanish or Mandarin.");

        var catalogMatch = TryMatchKnownLanguage(trimmed, knownLanguages);
        if (catalogMatch != null)
        {
            return new PollingValidationResult
            {
                IsValid = true,
                NormalizedAnswer = catalogMatch
            };
        }

        if (!string.IsNullOrWhiteSpace(_options.ApiKey) && !string.IsNullOrWhiteSpace(_options.Model))
        {
            var aiResult = await ExtractLanguageWithAiAsync(trimmed, knownLanguages, cancellationToken);
            if (aiResult != null)
                return aiResult;
        }

        return FallbackExtractLanguage(trimmed, knownLanguages);
    }

    private async Task<PollingValidationResult?> ValidateWithAiAsync(
        string question,
        string answer,
        string? validationHint,
        string? conversationContext,
        CancellationToken cancellationToken)
    {
        var systemPrompt = """
            You validate patient answers to intake questions for a healthcare doctor-matching service.

            Rules:
            - Accept reasonable informal answers, including words instead of digits (e.g. "twenty five" for age, "about 10 miles", "either is fine").
            - If the assistant message asked multiple questions, accept the answer when it clearly addresses AT LEAST ONE of them — even if brief.
            - Reject gibberish (e.g. "bla bla", "yyy", random letters, keyboard mash) or answers unrelated to every question asked.
            - Reject answers that do not meaningfully respond to what was asked (e.g. nonsense filler when a location or preference was requested).
            - When valid, extract the useful answer into normalizedAnswer (short, plain text).

            Respond with ONLY JSON:
            {"valid": true, "normalizedAnswer": "25"}
            or
            {"valid": false, "reprompt": "friendly one-sentence message asking again"}
            """;

        var userPrompt = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(conversationContext))
        {
            userPrompt.AppendLine("Recent assistant message shown to patient:");
            userPrompt.AppendLine(conversationContext);
            userPrompt.AppendLine();
        }

        userPrompt.AppendLine($"Target question to validate: {question}");
        userPrompt.AppendLine($"Expected type/hint: {validationHint ?? "a sensible answer to the question"}");
        userPrompt.AppendLine($"Patient answer: {answer}");

        try
        {
            var payload = AnthropicApiHelper.BuildPayload(
                _options,
                maxTokens: 250,
                system: systemPrompt,
                messages: new[] { new { role = "user", content = userPrompt.ToString() } });

            using var httpRequest = AnthropicApiHelper.CreateMessageRequest(_options, payload);
            var response = await _httpClient.SendAsync(httpRequest, cancellationToken);
            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Anthropic validation failed with status {Status}", (int)response.StatusCode);
                return null;
            }

            var text = AnthropicApiHelper.ExtractTextContent(responseBody);
            if (string.IsNullOrWhiteSpace(text))
                text = "{}";
            return ParseAiValidationJson(text, question);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error validating polling answer with AI");
            return null;
        }
    }

    private async Task<PollingValidationResult?> ExtractLanguageWithAiAsync(
        string answer,
        IReadOnlyList<string>? knownLanguages,
        CancellationToken cancellationToken)
    {
        var catalog = knownLanguages is { Count: > 0 }
            ? string.Join(", ", knownLanguages)
            : "(none provided)";

        var systemPrompt = """
            You extract a spoken language name for a healthcare doctor profile.

            Rules:
            - Accept informal answers, misspellings, native-language names, and short phrases
              (e.g. "espanol", "I speak Mandarin", "français" → French).
            - Return the standard English language name when possible (Spanish, Mandarin, Korean, etc.).
            - If the answer clearly matches a catalog language, use the catalog form exactly.
            - Accept real languages even if they are not in the catalog.
            - Reject gibberish, unrelated answers, or answers that do not name a language.

            Respond with ONLY JSON:
            {"valid": true, "normalizedAnswer": "Spanish"}
            or
            {"valid": false, "reprompt": "friendly one-sentence message asking for a language name"}
            """;

        var userPrompt = new StringBuilder();
        userPrompt.AppendLine($"Catalog languages (prefer exact match when clearly the same): {catalog}");
        userPrompt.AppendLine($"Input: {answer}");

        try
        {
            var payload = AnthropicApiHelper.BuildPayload(
                _options,
                maxTokens: 200,
                system: systemPrompt,
                messages: new[] { new { role = "user", content = userPrompt.ToString() } });

            using var httpRequest = AnthropicApiHelper.CreateMessageRequest(_options, payload);
            var response = await _httpClient.SendAsync(httpRequest, cancellationToken);
            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Anthropic language extraction failed with status {Status}", (int)response.StatusCode);
                return null;
            }

            var text = AnthropicApiHelper.ExtractTextContent(responseBody);
            if (string.IsNullOrWhiteSpace(text))
                text = "{}";

            var parsed = ParseAiValidationJson(text, "Which language do you speak?");
            if (parsed == null)
                return null;

            if (!parsed.IsValid || string.IsNullOrWhiteSpace(parsed.NormalizedAnswer))
                return parsed;

            var catalogMatch = TryMatchKnownLanguage(parsed.NormalizedAnswer, knownLanguages);
            return new PollingValidationResult
            {
                IsValid = true,
                NormalizedAnswer = catalogMatch ?? TitleCaseLanguage(parsed.NormalizedAnswer)
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error extracting language name with AI");
            return null;
        }
    }

    private static PollingValidationResult FallbackExtractLanguage(
        string answer,
        IReadOnlyList<string>? knownLanguages)
    {
        var catalogMatch = TryMatchKnownLanguage(answer, knownLanguages);
        if (catalogMatch != null)
        {
            return new PollingValidationResult
            {
                IsValid = true,
                NormalizedAnswer = catalogMatch
            };
        }

        var cleaned = Regex.Replace(answer, @"[^\p{L}\s\-']", " ").Trim();
        cleaned = Regex.Replace(cleaned, @"\s+", " ");
        if (cleaned.Length is < 2 or > 40)
            return Invalid("Please type a language name, for example Spanish or Mandarin.");

        var words = cleaned.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length > 4)
            return Invalid("Please type just the language name, for example Spanish or Mandarin.");

        return new PollingValidationResult
        {
            IsValid = true,
            NormalizedAnswer = TitleCaseLanguage(cleaned)
        };
    }

    private static string? TryMatchKnownLanguage(string input, IReadOnlyList<string>? knownLanguages)
    {
        if (knownLanguages == null || knownLanguages.Count == 0 || string.IsNullOrWhiteSpace(input))
            return null;

        var trimmed = input.Trim();
        var exact = knownLanguages.FirstOrDefault(l =>
            l.Equals(trimmed, StringComparison.OrdinalIgnoreCase));
        if (exact != null)
            return exact;

        foreach (var lang in knownLanguages.OrderByDescending(l => l.Length))
        {
            if (trimmed.Contains(lang, StringComparison.OrdinalIgnoreCase))
                return lang;
        }

        return null;
    }

    private static string TitleCaseLanguage(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.Length == 0)
            return trimmed;

        return string.Join(' ', trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(word =>
            {
                if (word.Length == 1)
                    return word.ToUpperInvariant();
                return char.ToUpperInvariant(word[0]) + word[1..].ToLowerInvariant();
            }));
    }

    private static PollingValidationResult? ParseAiValidationJson(string text, string question)
    {
        var json = ExtractJsonObject(text);
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            using var resultDoc = JsonDocument.Parse(json);
            var root = resultDoc.RootElement;
            var valid = root.TryGetProperty("valid", out var validProp) && validProp.GetBoolean();
            if (valid)
            {
                var normalized = root.TryGetProperty("normalizedAnswer", out var normProp)
                    ? normProp.GetString()
                    : null;
                return new PollingValidationResult
                {
                    IsValid = true,
                    NormalizedAnswer = string.IsNullOrWhiteSpace(normalized) ? null : normalized.Trim()
                };
            }

            var reprompt = root.TryGetProperty("reprompt", out var repromptProp)
                ? repromptProp.GetString()
                : $"Could you please answer again: {question}";
            return Invalid(reprompt ?? $"Could you please answer again: {question}");
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? ExtractJsonObject(string text)
    {
        var trimmed = text.Trim();
        var fenceMatch = JsonFenceRegex.Match(trimmed);
        if (fenceMatch.Success)
            return fenceMatch.Groups[1].Value.Trim();

        var start = trimmed.IndexOf('{');
        var end = trimmed.LastIndexOf('}');
        if (start >= 0 && end > start)
            return trimmed[start..(end + 1)];

        return trimmed.StartsWith('{') ? trimmed : null;
    }

    private static PollingValidationResult FallbackValidate(string question, string answer, string? hint)
    {
        var trimmed = answer.Trim();
        if (trimmed.Length < 1)
            return Invalid($"I didn't quite get that. {question}");

        if (IsGibberish(trimmed))
            return Invalid($"Could you give a clearer answer? {question}");

        if (hint != null && hint.Contains("age", StringComparison.OrdinalIgnoreCase))
        {
            if (TryParseAge(trimmed, out var age))
            {
                return new PollingValidationResult
                {
                    IsValid = true,
                    NormalizedAnswer = age.ToString()
                };
            }

            return Invalid("Please share your age — a number or words are fine, for example 35 or twenty five.");
        }

        return new PollingValidationResult
        {
            IsValid = true,
            NormalizedAnswer = trimmed
        };
    }

    private static bool IsGibberish(string input)
    {
        var trimmed = input.Trim().ToLowerInvariant();
        var letters = trimmed.Where(char.IsLetter).ToArray();
        if (letters.Length == 0)
            return false;

        if (letters.Length <= 2 && letters.Distinct().Count() == 1)
            return true;

        if (trimmed.Length >= 3 && letters.Distinct().Count() == 1)
            return true;

        if (letters.Length >= 6)
        {
            for (var patLen = 1; patLen <= 3; patLen++)
            {
                if (letters.Length % patLen != 0)
                    continue;

                var pattern = new string(letters, 0, patLen);
                var repeats = letters.Length / patLen;
                if (Enumerable.Range(0, repeats).All(i =>
                        new string(letters, i * patLen, patLen)
                            .Equals(pattern, StringComparison.OrdinalIgnoreCase)))
                {
                    return true;
                }
            }

            var vowelCount = letters.Count(c => "aeiou".Contains(char.ToLowerInvariant(c)));
            if (vowelCount == 0 && letters.Distinct().Count() <= 3)
                return true;
        }

        var words = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (words.Length >= 2 && words.All(w => w.Length <= 5) && IsNonsenseFillerWord(words[0]))
        {
            var distinct = words.Distinct(StringComparer.OrdinalIgnoreCase).Count();
            if (distinct <= 2 && words.All(IsNonsenseFillerWord))
                return true;
        }

        return false;
    }

    private static bool IsNonsenseFillerWord(string word)
    {
        if (word.Length < 2)
            return true;

        string[] fillers =
        [
            "bla", "blah", "bal", "foo", "bar", "baz", "test", "asdf", "qwer",
            "yyy", "xxx", "zzz", "hmm", "uhh", "umm", "idk", "meh"
        ];
        return fillers.Contains(word, StringComparer.OrdinalIgnoreCase);
    }

    private static bool TryParseAge(string input, out int age)
    {
        age = 0;
        var trimmed = input.Trim().ToLowerInvariant();

        var digitsOnly = new string(trimmed.Where(char.IsDigit).ToArray());
        if (digitsOnly.Length > 0 && int.TryParse(digitsOnly, out age))
            return age is >= 1 and <= 120;

        if (int.TryParse(trimmed, out age))
            return age is >= 1 and <= 120;

        if (TryParseSpelledNumber(trimmed, out age))
            return age is >= 1 and <= 120;

        if (trimmed.Contains("thirt", StringComparison.Ordinal))
        {
            age = trimmed.Contains("early", StringComparison.Ordinal) ? 32
                : trimmed.Contains("late", StringComparison.Ordinal) ? 38
                : trimmed.Contains("mid", StringComparison.Ordinal) ? 35
                : 35;
            return true;
        }

        if (trimmed.Contains("fort", StringComparison.Ordinal))
        {
            age = trimmed.Contains("early", StringComparison.Ordinal) ? 42
                : trimmed.Contains("late", StringComparison.Ordinal) ? 48
                : 45;
            return true;
        }

        if (trimmed.Contains("twent", StringComparison.Ordinal))
        {
            age = trimmed.Contains("early", StringComparison.Ordinal) ? 23
                : trimmed.Contains("late", StringComparison.Ordinal) ? 28
                : 25;
            return true;
        }

        if (trimmed.Contains("fift", StringComparison.Ordinal))
        {
            age = 55;
            return true;
        }

        if (trimmed.Contains("sixt", StringComparison.Ordinal))
        {
            age = 65;
            return true;
        }

        return false;
    }

    private static bool TryParseSpelledNumber(string input, out int result)
    {
        result = 0;
        var words = input
            .Replace("-", " ", StringComparison.Ordinal)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (words.Length == 0)
            return false;

        var total = 0;
        var current = 0;
        var found = false;

        foreach (var word in words)
        {
            if (!WordNumbers.TryGetValue(word, out var value))
                continue;

            found = true;
            if (value == 100)
            {
                current = current == 0 ? 100 : current * 100;
            }
            else if (value >= 20)
            {
                current += value;
            }
            else
            {
                current += value;
            }
        }

        total += current;
        if (!found)
            return false;

        result = total;
        return result > 0;
    }

    private static readonly Dictionary<string, int> WordNumbers = new(StringComparer.OrdinalIgnoreCase)
    {
        ["zero"] = 0, ["one"] = 1, ["two"] = 2, ["three"] = 3, ["four"] = 4,
        ["five"] = 5, ["six"] = 6, ["seven"] = 7, ["eight"] = 8, ["nine"] = 9,
        ["ten"] = 10, ["eleven"] = 11, ["twelve"] = 12, ["thirteen"] = 13,
        ["fourteen"] = 14, ["fifteen"] = 15, ["sixteen"] = 16, ["seventeen"] = 17,
        ["eighteen"] = 18, ["nineteen"] = 19,
        ["twenty"] = 20, ["thirty"] = 30, ["forty"] = 40, ["fifty"] = 50,
        ["sixty"] = 60, ["seventy"] = 70, ["eighty"] = 80, ["ninety"] = 90,
        ["hundred"] = 100
    };

    private static PollingValidationResult Invalid(string message) => new()
    {
        IsValid = false,
        RepromptMessage = message
    };
}
