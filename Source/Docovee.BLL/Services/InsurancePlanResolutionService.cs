using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Docovee.BLL.Configuration;
using Docovee.BLL.Data;
using Docovee.logging;
using Microsoft.Extensions.Options;

namespace Docovee.BLL.Services;

public class InsurancePlanResolutionResult
{
    public bool IsSkip { get; init; }
    public bool IsResolved { get; init; }
    public string? ResolvedPlanName { get; init; }
    public string? RepromptMessage { get; init; }
}

public interface IInsurancePlanResolutionService
{
    Task<InsurancePlanResolutionResult> ResolveAsync(string userInput, CancellationToken cancellationToken = default);
}

/// <summary>Maps free-text patient insurance answers to canonical carrier/plan names.</summary>
public sealed class InsurancePlanResolutionService : IInsurancePlanResolutionService
{
    private static readonly Regex JsonFenceRegex = new(
        @"```(?:json)?\s*(\{[\s\S]*?\})\s*```",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["aetna"] = "Aetna PPO",
        ["bcbs"] = "Blue Cross Blue Shield",
        ["blue cross"] = "Blue Cross Blue Shield",
        ["blue shield"] = "Blue Cross Blue Shield",
        ["anthem"] = "Blue Cross Blue Shield",
        ["cigna"] = "Cigna",
        ["uhc"] = "United Healthcare",
        ["united"] = "United Healthcare",
        ["united health"] = "United Healthcare",
        ["united healthcare"] = "United Healthcare",
        ["unitedhealthcare"] = "United Healthcare",
        ["humana"] = "Humana",
        ["medicare"] = "Medicare",
        ["medicaid"] = "Medicaid"
    };

    private readonly HttpClient _httpClient;
    private readonly AnthropicOptions _options;
    private readonly IDocoveeLogger _logger;
    private readonly IInsuranceService _insuranceService;

    public InsurancePlanResolutionService(
        HttpClient httpClient,
        IOptions<AnthropicOptions> options,
        IDocoveeLogger logger,
        IInsuranceService insuranceService)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _logger = logger;
        _insuranceService = insuranceService;
    }

    public async Task<InsurancePlanResolutionResult> ResolveAsync(string userInput, CancellationToken cancellationToken = default)
    {
        var trimmed = (userInput ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(trimmed) || IsSkipAnswer(trimmed))
            return new InsurancePlanResolutionResult { IsSkip = true, IsResolved = true };

        if (TryMatchQuickPick(trimmed, out var quickPick))
            return Resolved(quickPick);

        if (TryLocalResolve(trimmed, out var local))
            return Resolved(local);

        if (!string.IsNullOrWhiteSpace(_options.ApiKey) && !string.IsNullOrWhiteSpace(_options.Model))
        {
            var aiResult = await ResolveWithAiAsync(trimmed, cancellationToken);
            if (aiResult != null)
                return aiResult;
        }

        if (AnthropicValidationService.LooksLikeGibberish(trimmed))
        {
            return new InsurancePlanResolutionResult
            {
                IsResolved = false,
                RepromptMessage = "Please type your insurance plan name, tap one of the options below, or skip for now."
            };
        }

        return Resolved(trimmed);
    }

    private async Task<InsurancePlanResolutionResult?> ResolveWithAiAsync(string userInput, CancellationToken cancellationToken)
    {
        var catalog = await BuildCatalogAsync(cancellationToken);
        var quickPicks = NuviFlowContent.LogisticsInsurancePlanOptions
            .Where(o => !o.Contains("skip", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var systemPrompt = """
            You normalize patient insurance answers for a US dental doctor-matching service.

            Rules:
            - If the patient wants to skip or does not know, respond with skip true.
            - Map informal answers, abbreviations, and employer plan names to the closest canonical carrier/plan.
            - Prefer an exact name from the provided catalog when possible.
            - If the answer is clearly not insurance-related or gibberish, set resolved false with a friendly reprompt.
            - Output ONLY JSON.

            Examples:
            {"skip": false, "resolved": true, "planName": "Blue Cross Blue Shield"}
            {"skip": true}
            {"skip": false, "resolved": false, "reprompt": "Could you share your dental insurance carrier?"}
            """;

        var userPrompt = new StringBuilder();
        userPrompt.AppendLine($"Patient answer: {userInput}");
        userPrompt.AppendLine();
        userPrompt.AppendLine("Preferred quick-pick plans:");
        foreach (var pick in quickPicks)
            userPrompt.AppendLine($"- {pick}");
        userPrompt.AppendLine();
        userPrompt.AppendLine("Known insurance carriers (choose closest match when possible):");
        foreach (var name in catalog.Take(120))
            userPrompt.AppendLine($"- {name}");

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
                _logger.LogWarning("Insurance plan resolution API call failed with status {Status}", (int)response.StatusCode);
                return null;
            }

            var text = AnthropicApiHelper.ExtractTextContent(responseBody);
            return ParseAiJson(text, catalog, quickPicks);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error resolving insurance plan with AI");
            return null;
        }
    }

    private InsurancePlanResolutionResult? ParseAiJson(
        string text,
        IReadOnlyList<string> catalog,
        IReadOnlyList<string> quickPicks)
    {
        var json = ExtractJsonObject(text);
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.TryGetProperty("skip", out var skipProp) && skipProp.GetBoolean())
                return new InsurancePlanResolutionResult { IsSkip = true, IsResolved = true };

            var resolved = root.TryGetProperty("resolved", out var resolvedProp) && resolvedProp.GetBoolean();
            if (!resolved)
            {
                var reprompt = root.TryGetProperty("reprompt", out var repromptProp)
                    ? repromptProp.GetString()
                    : null;
                return new InsurancePlanResolutionResult
                {
                    IsResolved = false,
                    RepromptMessage = string.IsNullOrWhiteSpace(reprompt)
                        ? "Please type your insurance plan name, tap one of the options below, or skip for now."
                        : reprompt.Trim()
                };
            }

            var planName = root.TryGetProperty("planName", out var planProp)
                ? planProp.GetString()?.Trim()
                : null;
            if (string.IsNullOrWhiteSpace(planName))
                return null;

            if (TryMatchCatalog(planName, catalog, quickPicks, out var canonical))
                return Resolved(canonical);

            return Resolved(planName);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task<List<string>> BuildCatalogAsync(CancellationToken cancellationToken)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var option in NuviFlowContent.LogisticsInsurancePlanOptions)
        {
            if (!option.Contains("skip", StringComparison.OrdinalIgnoreCase))
                names.Add(option);
        }

        foreach (var name in InsuranceCarrierCatalog.Names)
            names.Add(name);

        try
        {
            var carriers = await _insuranceService.GetCarriersAsync(cancellationToken);
            foreach (var carrier in carriers)
            {
                if (!string.IsNullOrWhiteSpace(carrier.Name))
                    names.Add(carrier.Name.Trim());
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Could not load insurance carriers for resolution: {Message}", ex.Message);
        }

        return names.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static bool IsSkipAnswer(string input)
    {
        var lower = input.Trim().ToLowerInvariant();
        return lower is "skip" or "skip for now" or "skip it" or "skip now" or "pass"
            || lower.Contains("not sure", StringComparison.Ordinal)
            || lower.Contains("don't know", StringComparison.Ordinal)
            || lower.Contains("dont know", StringComparison.Ordinal)
            || lower.Contains("no idea", StringComparison.Ordinal)
            || lower is "n/a" or "na" or "none";
    }

    private static bool TryMatchQuickPick(string input, out string planName)
    {
        planName = string.Empty;
        foreach (var option in NuviFlowContent.LogisticsInsurancePlanOptions)
        {
            if (option.Contains("skip", StringComparison.OrdinalIgnoreCase))
                continue;

            if (string.Equals(option, input, StringComparison.OrdinalIgnoreCase))
            {
                planName = option;
                return true;
            }
        }

        return false;
    }

    private static bool TryLocalResolve(string input, out string planName)
    {
        planName = string.Empty;
        var lower = input.Trim().ToLowerInvariant();

        foreach (var (alias, canonical) in Aliases)
        {
            if (lower.Equals(alias, StringComparison.Ordinal)
                || lower.Contains(alias, StringComparison.Ordinal))
            {
                planName = canonical;
                return true;
            }
        }

        foreach (var option in NuviFlowContent.LogisticsInsurancePlanOptions)
        {
            if (option.Contains("skip", StringComparison.OrdinalIgnoreCase))
                continue;

            if (lower.Contains(option.ToLowerInvariant(), StringComparison.Ordinal)
                || option.Contains(input, StringComparison.OrdinalIgnoreCase))
            {
                planName = option;
                return true;
            }
        }

        return false;
    }

    private static bool TryMatchCatalog(
        string candidate,
        IReadOnlyList<string> catalog,
        IReadOnlyList<string> quickPicks,
        out string planName)
    {
        planName = string.Empty;
        if (TryMatchQuickPick(candidate, out planName))
            return true;

        var exact = catalog.FirstOrDefault(n => string.Equals(n, candidate, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(exact))
        {
            planName = exact;
            return true;
        }

        var contains = catalog.FirstOrDefault(n =>
            candidate.Contains(n, StringComparison.OrdinalIgnoreCase)
            || n.Contains(candidate, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(contains))
        {
            planName = contains;
            return true;
        }

        var quick = quickPicks.FirstOrDefault(n =>
            candidate.Contains(n, StringComparison.OrdinalIgnoreCase)
            || n.Contains(candidate, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(quick))
        {
            planName = quick;
            return true;
        }

        return false;
    }

    private static InsurancePlanResolutionResult Resolved(string planName) =>
        new() { IsResolved = true, ResolvedPlanName = planName.Trim() };

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
}
