using System.Text.Json;
using System.Text.RegularExpressions;
using Docovee.BLL.Configuration;
using Docovee.BLL.Security;
using Docovee.DS.Entities;
using Docovee.logging;
using Microsoft.Extensions.Options;

namespace Docovee.BLL.Services;

public sealed class SmsReplyExtraction
{
    public const string IntentUnclear = "unclear";
    public const string IntentYes = "yes";
    public const string IntentNo = "no";
    public const string IntentUpcoming = "upcoming";
    public const string IntentPassed = "passed";
    public const string IntentOptOut = "opt_out";
    public const string IntentNoShow = "no_show";
    public const string IntentRating = "rating";
    public const string IntentWindow = "window";
    public const string IntentWaiting = "waiting";
    public const string IntentRecommend = "recommend";
    public const string IntentExperience = "experience";

    public string Intent { get; init; } = IntentUnclear;
    public int? Rating { get; init; }
    public int? WindowDays { get; init; }
    public string? WaitingTime { get; init; }
    public string? Recommendation { get; init; }
    public string? ExperienceText { get; init; }
    public double Confidence { get; init; }

    public bool IsUnclear =>
        string.Equals(Intent, IntentUnclear, StringComparison.OrdinalIgnoreCase);
    public bool IsYes => string.Equals(Intent, IntentYes, StringComparison.OrdinalIgnoreCase);
    public bool IsNo => string.Equals(Intent, IntentNo, StringComparison.OrdinalIgnoreCase);
    public bool IsUpcoming => string.Equals(Intent, IntentUpcoming, StringComparison.OrdinalIgnoreCase);
    public bool IsPassed => string.Equals(Intent, IntentPassed, StringComparison.OrdinalIgnoreCase);
    public bool IsOptOut => string.Equals(Intent, IntentOptOut, StringComparison.OrdinalIgnoreCase);
    public bool IsNoShow => string.Equals(Intent, IntentNoShow, StringComparison.OrdinalIgnoreCase);
}

public interface ISmsReplyExtractionService
{
    Task<SmsReplyExtraction> ExtractAsync(
        string stage,
        string questionHint,
        string reply,
        CancellationToken cancellationToken = default);

    string ClarifyPrompt(string stage);
}

public sealed class SmsReplyExtractionService : ISmsReplyExtractionService
{
    private static readonly Regex JsonFenceRegex = new(
        @"```(?:json)?\s*(\{[\s\S]*?\})\s*```",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly HashSet<string> StopKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "stop", "stopall", "unsubscribe", "cancel", "end", "quit"
    };

    private readonly HttpClient _httpClient;
    private readonly AnthropicOptions _options;
    private readonly IDocoveeLogger _logger;

    public SmsReplyExtractionService(
        HttpClient httpClient,
        IOptions<AnthropicOptions> options,
        IDocoveeLogger logger)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _logger = logger;
    }

    public static bool IsStopKeyword(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return false;
        var t = body.Trim();
        if (StopKeywords.Contains(t))
            return true;
        var first = t.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();
        return first != null && StopKeywords.Contains(first);
    }

    public async Task<SmsReplyExtraction> ExtractAsync(
        string stage,
        string questionHint,
        string reply,
        CancellationToken cancellationToken = default)
    {
        var allowed = AllowedIntents(stage);
        if (IsStopKeyword(reply))
            return new SmsReplyExtraction { Intent = SmsReplyExtraction.IntentOptOut, Confidence = 1 };

        if (!string.IsNullOrWhiteSpace(_options.ApiKey) && !string.IsNullOrWhiteSpace(_options.Model))
        {
            var ai = await ExtractWithClaudeAsync(stage, questionHint, reply, allowed, cancellationToken);
            if (ai != null && !ai.IsUnclear && allowed.Contains(ai.Intent))
                return ai;
        }

        var fallback = FallbackExtract(stage, reply);
        if (!fallback.IsUnclear && allowed.Contains(fallback.Intent))
            return fallback;

        return new SmsReplyExtraction { Intent = SmsReplyExtraction.IntentUnclear, Confidence = 0 };
    }

    public string ClarifyPrompt(string stage) => stage switch
    {
        PatientWhatsAppNurtureStages.AskBooked
            or PatientWhatsAppNurtureStages.AskContactPracticeNotBooked
            or PatientWhatsAppNurtureStages.AskAttended
            or PatientWhatsAppNurtureStages.AskContactPracticeMissed
            => "Please reply YES or NO.",
        PatientWhatsAppNurtureStages.AskUpcomingOrPassed
            => "Please reply UPCOMING or PASSED.",
        PatientWhatsAppNurtureStages.AskUpcomingWindow
            => "Please reply 10 DAYS, 20 DAYS, 30 DAYS, or 2 MONTHS.",
        PatientWhatsAppNurtureStages.AskRating
            or AppointmentFeedbackStages.RatingSent
            => "Please reply with a number from 1 to 5 (5 is best), or DID NOT ATTEND.",
        PatientWhatsAppNurtureStages.AskExperience
            or AppointmentFeedbackStages.ReviewTextAwaiting
            => "Please describe your experience in a few words.",
        AppointmentFeedbackStages.WaitingSent
            => "Please reply Excellent, Good, Average, or Bad.",
        AppointmentFeedbackStages.RecommendSent
            => "Please reply Highly Recommended, Neutral, or Not Recommended.",
        _ => "Sorry, I didn't catch that. Please reply again with a short answer."
    };

    private async Task<SmsReplyExtraction?> ExtractWithClaudeAsync(
        string stage,
        string questionHint,
        string reply,
        HashSet<string> allowed,
        CancellationToken cancellationToken)
    {
        var allowedList = string.Join(", ", allowed);
        var systemPrompt = """
            You classify an SMS reply to a single survey/check-in question.

            Rules:
            - Use only the stage name, the generic question text, and the reply.
            - Do not expect or use names, phone numbers, emails, dates of birth, addresses, or appointment IDs.
            - Map informal language (yeah, nope, five stars, didn't go) to the allowed intents.
            - If the reply is unrelated, empty, or you are not sure, use intent "unclear".

            Respond with ONLY JSON:
            {"intent":"...","rating":null,"windowDays":null,"waitingTime":null,"recommendation":null,"experienceText":null,"confidence":0.0}
            """;

        // Never send identifiers to Claude: generic stage/question only, reply de-identified.
        var sanitizedReply = PhiPromptSanitizer.Deidentify(reply);
        var userPrompt =
            $"Stage: {stage}\nAllowed intents: {allowedList}\nQuestion: {questionHint}\nReply: {sanitizedReply}";

        try
        {
            var options = new AnthropicOptions
            {
                ApiKey = _options.ApiKey,
                Model = _options.Model,
                AllowPhi = false,
                DeidentifyPrompts = true,
                EnableWebSearch = false
            };
            var payload = AnthropicApiHelper.BuildPayload(
                options,
                maxTokens: 200,
                system: systemPrompt,
                messages: new[] { new { role = "user", content = userPrompt } },
                includeWebSearch: false);

            using var httpRequest = AnthropicApiHelper.CreateMessageRequest(options, payload);
            var response = await _httpClient.SendAsync(httpRequest, cancellationToken);
            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("SMS reply extraction failed with status {Status}", (int)response.StatusCode);
                return null;
            }

            var text = AnthropicApiHelper.ExtractTextContent(responseBody);
            return ParseExtractionJson(text, allowed);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("SMS reply extraction with Claude failed: {Error}", ex.Message);
            return null;
        }
    }

    private static SmsReplyExtraction? ParseExtractionJson(string? text, HashSet<string> allowed)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var json = text.Trim();
        var fence = JsonFenceRegex.Match(json);
        if (fence.Success)
            json = fence.Groups[1].Value;

        var start = json.IndexOf('{');
        var end = json.LastIndexOf('}');
        if (start < 0 || end <= start)
            return null;

        json = json[start..(end + 1)];
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var intent = root.TryGetProperty("intent", out var intentEl)
                ? intentEl.GetString()?.Trim().ToLowerInvariant()
                : null;
            if (string.IsNullOrWhiteSpace(intent) || !allowed.Contains(intent))
                intent = SmsReplyExtraction.IntentUnclear;

            int? rating = TryGetInt(root, "rating");
            if (rating is < 1 or > 5)
                rating = null;

            int? windowDays = TryGetInt(root, "windowDays");
            if (windowDays is not (10 or 20 or 30 or 60))
                windowDays = null;

            var waiting = root.TryGetProperty("waitingTime", out var waitEl) ? waitEl.GetString() : null;
            waiting = PatientReviewOptions.NormalizeWaitingTime(waiting);

            var recommend = root.TryGetProperty("recommendation", out var recEl) ? recEl.GetString() : null;
            recommend = PatientReviewOptions.NormalizeRecommendation(recommend);

            var experience = root.TryGetProperty("experienceText", out var expEl) ? expEl.GetString() : null;
            var confidence = 0.0;
            if (root.TryGetProperty("confidence", out var confEl) && confEl.TryGetDouble(out var conf))
                confidence = conf;

            if (intent == SmsReplyExtraction.IntentRating && rating is null)
                intent = SmsReplyExtraction.IntentUnclear;
            if (intent == SmsReplyExtraction.IntentWindow && windowDays is null)
                intent = SmsReplyExtraction.IntentUnclear;
            if (intent == SmsReplyExtraction.IntentWaiting && string.IsNullOrWhiteSpace(waiting))
                intent = SmsReplyExtraction.IntentUnclear;
            if (intent == SmsReplyExtraction.IntentRecommend && string.IsNullOrWhiteSpace(recommend))
                intent = SmsReplyExtraction.IntentUnclear;
            if (intent == SmsReplyExtraction.IntentExperience && string.IsNullOrWhiteSpace(experience))
                intent = SmsReplyExtraction.IntentUnclear;

            return new SmsReplyExtraction
            {
                Intent = intent!,
                Rating = rating,
                WindowDays = windowDays,
                WaitingTime = waiting,
                Recommendation = recommend,
                ExperienceText = string.IsNullOrWhiteSpace(experience) ? null : experience.Trim(),
                Confidence = confidence
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static int? TryGetInt(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var el) || el.ValueKind == JsonValueKind.Null)
            return null;
        if (el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out var n))
            return n;
        if (el.ValueKind == JsonValueKind.String && int.TryParse(el.GetString(), out var parsed))
            return parsed;
        return null;
    }

    private static SmsReplyExtraction FallbackExtract(string stage, string reply)
    {
        var raw = reply.Trim();
        if (raw.Length == 0)
            return new SmsReplyExtraction { Intent = SmsReplyExtraction.IntentUnclear };

        if (IsYes(raw))
            return new SmsReplyExtraction { Intent = SmsReplyExtraction.IntentYes, Confidence = 0.6 };
        if (IsNo(raw))
            return new SmsReplyExtraction { Intent = SmsReplyExtraction.IntentNo, Confidence = 0.6 };

        if (IsUpcoming(raw))
            return new SmsReplyExtraction { Intent = SmsReplyExtraction.IntentUpcoming, Confidence = 0.6 };
        if (IsPassed(raw) || IsNoShowText(raw))
        {
            if (stage is AppointmentFeedbackStages.RatingSent or PatientWhatsAppNurtureStages.AskRating)
                return new SmsReplyExtraction { Intent = SmsReplyExtraction.IntentNoShow, Confidence = 0.6 };
            return new SmsReplyExtraction { Intent = SmsReplyExtraction.IntentPassed, Confidence = 0.6 };
        }

        var window = ParseUpcomingWindowDays(raw);
        if (window is not null)
            return new SmsReplyExtraction { Intent = SmsReplyExtraction.IntentWindow, WindowDays = window, Confidence = 0.6 };

        var rating = ParseRating(raw);
        if (rating is not null)
            return new SmsReplyExtraction { Intent = SmsReplyExtraction.IntentRating, Rating = rating, Confidence = 0.6 };

        var waiting = AppointmentFeedbackItemIds.ParseWaitingTime(raw)
                      ?? PatientReviewOptions.NormalizeWaitingTime(raw);
        if (!string.IsNullOrWhiteSpace(waiting))
            return new SmsReplyExtraction { Intent = SmsReplyExtraction.IntentWaiting, WaitingTime = waiting, Confidence = 0.6 };

        var recommend = AppointmentFeedbackItemIds.ParseRecommendation(raw)
                        ?? PatientReviewOptions.NormalizeRecommendation(raw);
        if (!string.IsNullOrWhiteSpace(recommend))
            return new SmsReplyExtraction
            {
                Intent = SmsReplyExtraction.IntentRecommend,
                Recommendation = recommend,
                Confidence = 0.6
            };

        if (stage is PatientWhatsAppNurtureStages.AskExperience
                or AppointmentFeedbackStages.ReviewTextAwaiting
            && raw.Length >= 3)
        {
            return new SmsReplyExtraction
            {
                Intent = SmsReplyExtraction.IntentExperience,
                ExperienceText = raw,
                Confidence = 0.5
            };
        }

        return new SmsReplyExtraction { Intent = SmsReplyExtraction.IntentUnclear };
    }

    private static HashSet<string> AllowedIntents(string stage)
    {
        string[] allowed = stage switch
        {
            PatientWhatsAppNurtureStages.AskBooked
                or PatientWhatsAppNurtureStages.AskContactPracticeNotBooked
                or PatientWhatsAppNurtureStages.AskAttended
                or PatientWhatsAppNurtureStages.AskContactPracticeMissed
                => [SmsReplyExtraction.IntentYes, SmsReplyExtraction.IntentNo, SmsReplyExtraction.IntentOptOut, SmsReplyExtraction.IntentUnclear],
            PatientWhatsAppNurtureStages.AskUpcomingOrPassed
                => [SmsReplyExtraction.IntentUpcoming, SmsReplyExtraction.IntentPassed, SmsReplyExtraction.IntentOptOut, SmsReplyExtraction.IntentUnclear],
            PatientWhatsAppNurtureStages.AskUpcomingWindow
                => [SmsReplyExtraction.IntentWindow, SmsReplyExtraction.IntentOptOut, SmsReplyExtraction.IntentUnclear],
            PatientWhatsAppNurtureStages.AskRating
                => [SmsReplyExtraction.IntentRating, SmsReplyExtraction.IntentNoShow, SmsReplyExtraction.IntentOptOut, SmsReplyExtraction.IntentUnclear],
            PatientWhatsAppNurtureStages.AskExperience
                or AppointmentFeedbackStages.ReviewTextAwaiting
                => [SmsReplyExtraction.IntentExperience, SmsReplyExtraction.IntentOptOut, SmsReplyExtraction.IntentUnclear],
            AppointmentFeedbackStages.RatingSent
                => [SmsReplyExtraction.IntentRating, SmsReplyExtraction.IntentNoShow, SmsReplyExtraction.IntentOptOut, SmsReplyExtraction.IntentUnclear],
            AppointmentFeedbackStages.WaitingSent
                => [SmsReplyExtraction.IntentWaiting, SmsReplyExtraction.IntentOptOut, SmsReplyExtraction.IntentUnclear],
            AppointmentFeedbackStages.RecommendSent
                => [SmsReplyExtraction.IntentRecommend, SmsReplyExtraction.IntentOptOut, SmsReplyExtraction.IntentUnclear],
            _ => [SmsReplyExtraction.IntentUnclear, SmsReplyExtraction.IntentOptOut]
        };
        return new HashSet<string>(allowed, StringComparer.OrdinalIgnoreCase);
    }

    private static int? ParseRating(string selection)
    {
        var star = AppointmentFeedbackItemIds.ParseStarRating(selection);
        if (star is >= 1 and <= 5)
            return star;

        var first = selection.Trim().Split(' ', '-')[0];
        if (int.TryParse(first, out var n) && n is >= 1 and <= 5)
            return n;

        return null;
    }

    private static int? ParseUpcomingWindowDays(string selection)
    {
        var s = selection.Trim().ToLowerInvariant();
        if (s.Contains("10") || s.Contains("ten"))
            return 10;
        if (s.Contains("20"))
            return 20;
        if (s.Contains("30"))
            return 30;
        if (s.Contains("2 month") || s.Contains("two month") || s.Contains("60"))
            return 60;
        return null;
    }

    private static bool IsYes(string s)
    {
        var t = s.Trim().ToLowerInvariant();
        return t is "yes" or "y" or "yeah" or "yep" or "sure" or "ok" or "okay" or "booked";
    }

    private static bool IsNo(string s)
    {
        var t = s.Trim().ToLowerInvariant();
        return t is "no" or "n" or "nope" or "not" or "not yet" or "haven't" or "havent";
    }

    private static bool IsUpcoming(string s)
    {
        var t = s.Trim().ToLowerInvariant();
        return t.Contains("upcoming") || t.Contains("future") || t.Contains("ahead") || t.Contains("not yet");
    }

    private static bool IsPassed(string s)
    {
        var t = s.Trim().ToLowerInvariant();
        return t.Contains("passed") || t.Contains("missed") || t.Contains("didn't go") || t.Contains("didnt go");
    }

    private static bool IsNoShowText(string s)
    {
        var t = s.Trim().ToLowerInvariant();
        return AppointmentFeedbackItemIds.IsNoShow(s)
               || t.Contains("did not attend")
               || t.Contains("didn't attend")
               || t.Contains("didnt attend")
               || t.Contains("no show")
               || t.Contains("noshow");
    }
}
