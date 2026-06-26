using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Docovee.BLL.Configuration;
using Docovee.BLL.Models;
using Docovee.DS.Entities;
using Docovee.logging;
using Microsoft.Extensions.Options;

namespace Docovee.BLL.Services;

public interface IAnthropicMatchingService
{
    Task<IReadOnlyList<(int DoctorId, int MatchScore, string Reason)>> RankDoctorsAsync(
        SearchSession session,
        IReadOnlyList<Doctor> candidates,
        CancellationToken cancellationToken = default);
}

public class AnthropicMatchingService : IAnthropicMatchingService
{
    private readonly HttpClient _httpClient;
    private readonly AnthropicOptions _options;
    private readonly IDocoveeLogger _logger;

    private static readonly Regex IdScoreRegex = new(
        @"(\d+)\s*[|:]\s*(\d+)",
        RegexOptions.Compiled);

    public AnthropicMatchingService(
        HttpClient httpClient,
        IOptions<AnthropicOptions> options,
        IDocoveeLogger logger)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<IReadOnlyList<(int DoctorId, int MatchScore, string Reason)>> RankDoctorsAsync(
        SearchSession session,
        IReadOnlyList<Doctor> candidates,
        CancellationToken cancellationToken = default)
    {
        if (candidates.Count == 0)
            return Array.Empty<(int, int, string)>();

        if (string.IsNullOrWhiteSpace(_options.ApiKey) || string.IsNullOrWhiteSpace(_options.Model))
            return FallbackRank(session, candidates);

        var patientContext = BuildPatientContext(session);
        var doctorProfiles = candidates.Select(BuildDoctorProfile).ToList();

        var systemPrompt = """
            You are a healthcare matching expert. Rank doctors for a patient based on specialty fit, location, reviews, niche expertise, personal preferences, and insurance coverage.
            When the patient lists an insurance plan, give significantly higher scores to doctors whose acceptedInsurances include a match (exact or close, e.g. "Aetna" matches "Aetna PPO").
            Doctors without insurance data should rank lower when the patient specified insurance, but still include them.
            Respond with ONLY a JSON array: [{"doctorId": 1, "score": 95, "reason": "brief reason"}]
            Scores are 1-99. Include all doctor IDs provided. Best match gets highest score.
            """;

        var userPrompt = $"""
            Patient context:
            {patientContext}

            Doctors (JSON):
            {JsonSerializer.Serialize(doctorProfiles)}
            """;

        try
        {
            var payload = new
            {
                model = _options.Model.Trim(),
                max_tokens = 1500,
                system = systemPrompt,
                messages = new[] { new { role = "user", content = userPrompt } }
            };

            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages");
            httpRequest.Headers.Add("x-api-key", _options.ApiKey);
            httpRequest.Headers.Add("anthropic-version", "2023-06-01");
            httpRequest.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

            var response = await _httpClient.SendAsync(httpRequest, cancellationToken);
            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Anthropic matching failed: {Body}", responseBody);
                return FallbackRank(session, candidates);
            }

            using var doc = JsonDocument.Parse(responseBody);
            var text = doc.RootElement.GetProperty("content")[0].GetProperty("text").GetString() ?? "[]";
            var jsonStart = text.IndexOf('[');
            var jsonEnd = text.LastIndexOf(']');
            if (jsonStart >= 0 && jsonEnd > jsonStart)
                text = text[jsonStart..(jsonEnd + 1)];

            var rankings = JsonSerializer.Deserialize<List<AiDoctorRank>>(text, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (rankings == null || rankings.Count == 0)
                return FallbackRank(session, candidates);

            var validIds = candidates.Select(c => c.Id).ToHashSet();
            return rankings
                .Where(r => validIds.Contains(r.DoctorId))
                .Select(r => (r.DoctorId, Math.Clamp(r.Score, 1, 99), r.Reason ?? "Good match for your needs"))
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during AI doctor matching");
            return FallbackRank(session, candidates);
        }
    }

    private static IReadOnlyList<(int DoctorId, int MatchScore, string Reason)> FallbackRank(
        SearchSession session,
        IReadOnlyList<Doctor> candidates)
    {
        return candidates
            .Select(d =>
            {
                var score = 70;
                if (session.Specialty != null && d.SpecialtyCategory.Contains(session.Specialty.Split(' ')[0], StringComparison.OrdinalIgnoreCase))
                    score += 15;
                score += (int)((double)d.GoogleRating * 2);
                score += InsuranceMatchHelper.InsuranceRankBoost(session.InsurancePlanText, d);
                return (d.Id, Math.Min(score, 99), d.TagLine ?? d.Specialty);
            })
            .OrderByDescending(x => x.Item2)
            .ToList();
    }

    private static string BuildPatientContext(SearchSession session)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(session.MedicalIssuesSummary))
            parts.Add($"Medical issues: {session.MedicalIssuesSummary}");
        if (!string.IsNullOrWhiteSpace(session.Specialty))
            parts.Add($"Specialty needed: {session.Specialty}");
        if (!string.IsNullOrWhiteSpace(session.Location))
            parts.Add($"Location: {session.Location}");
        if (!string.IsNullOrWhiteSpace(session.InsurancePlanText))
            parts.Add($"Patient insurance plan: {session.InsurancePlanText}");
        if (!string.IsNullOrWhiteSpace(session.SearchNotes))
            parts.Add($"Notes: {session.SearchNotes}");
        if (!string.IsNullOrWhiteSpace(session.SearchContextJson))
            parts.Add($"Preferences: {session.SearchContextJson}");
        return string.Join("\n", parts);
    }

    private static object BuildDoctorProfile(Doctor d)
    {
        var acceptedInsurances = InsuranceMatchHelper.GetCarrierNames(d);
        return new
        {
            doctorId = d.Id,
            name = d.Name,
            specialty = d.Specialty,
            specialtyCategory = d.SpecialtyCategory,
            practiceName = d.PracticeName,
            location = d.Location ?? $"{d.City}, {d.State}",
            address = d.Address,
            acceptedInsurances,
            rating = d.GoogleRating,
            reviewCount = d.GoogleReviewCount,
            summaryOfReviews = d.SummaryOfReviews,
            top3Procedures = d.Top3Procedures,
            niche = d.Niche,
            dentalImplants = d.OffersDentalImplants,
            tmj = d.OffersTmj,
            botox = d.OffersBotox,
            age = d.Age,
            yearsOfPractice = d.YearsOfPractice,
            procedureCount = d.ProcedureCount,
            graduationYear = d.GraduationYear,
            practiceCount = d.PracticeCount,
            gender = d.Gender.ToString()
        };
    }

    private class AiDoctorRank
    {
        public int DoctorId { get; set; }
        public int Score { get; set; }
        public string? Reason { get; set; }
    }
}
