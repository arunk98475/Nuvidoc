using System.Text.Json;
using Docovee.BLL.Data;
using Docovee.DS;
using Docovee.DS.Models;
using Microsoft.EntityFrameworkCore;

namespace Docovee.BLL.Services;

public interface IPatientPreferenceService
{
    Task<PatientPreferencePageModel> GetForEditAsync(int patientId, CancellationToken cancellationToken = default);
    Task<(bool Success, string? Error)> SaveAsync(
        int patientId,
        IReadOnlyList<PatientPreferenceAnswerInput> answers,
        CancellationToken cancellationToken = default);
}

public class PatientPreferenceService : IPatientPreferenceService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private readonly DocoveeDbContext _db;
    private readonly IPollingQuestionService _pollingQuestions;
    private readonly IDoctorLanguageService _languages;

    public PatientPreferenceService(
        DocoveeDbContext db,
        IPollingQuestionService pollingQuestions,
        IDoctorLanguageService languages)
    {
        _db = db;
        _pollingQuestions = pollingQuestions;
        _languages = languages;
    }

    public async Task<PatientPreferencePageModel> GetForEditAsync(
        int patientId,
        CancellationToken cancellationToken = default)
    {
        var questions = await _pollingQuestions.GetActiveAsync(cancellationToken);
        var saved = await LoadProfileAsync(patientId, cancellationToken);
        var answers = saved?.DeepDiveAnswers ?? [];
        var languages = await _languages.GetActiveNamesAsync(cancellationToken);

        var items = questions
            .Where(q => !IsPatientAgeQuestion(q.Question))
            .Select(q =>
            {
                var existing = answers.FirstOrDefault(a => a.QuestionId == q.Id);
                var isLanguage = NuviFlowContent.IsLanguageDeepDiveQuestion(q.Question);
                var isWildcard = NuviFlowContent.IsWildcardDeepDiveQuestion(q.Question);
                var options = GetOptions(q);
                var isYesNo = isLanguage || isWildcard || IsYesNoOptions(options);
                var (answer, followUp) = SplitStoredAnswer(existing?.Answer, isLanguage, isWildcard, saved);

                return new PatientPreferenceQuestionVm
                {
                    QuestionId = q.Id,
                    Question = q.Question,
                    Options = options,
                    IsLanguage = isLanguage,
                    IsWildcard = isWildcard,
                    IsYesNo = isYesNo,
                    Answer = answer,
                    FollowUp = followUp
                };
            })
            .ToList();

        return new PatientPreferencePageModel
        {
            Questions = items,
            Languages = languages
        };
    }

    public async Task<(bool Success, string? Error)> SaveAsync(
        int patientId,
        IReadOnlyList<PatientPreferenceAnswerInput> answers,
        CancellationToken cancellationToken = default)
    {
        var patient = await _db.Patients.FirstOrDefaultAsync(p => p.Id == patientId, cancellationToken);
        if (patient == null)
            return (false, "Patient not found.");

        var questions = await _pollingQuestions.GetActiveAsync(cancellationToken);
        var byId = questions.ToDictionary(q => q.Id);
        var profile = await LoadProfileAsync(patientId, cancellationToken) ?? new PatientPreferenceProfile();
        var existing = profile.DeepDiveAnswers ?? [];

        var next = existing
            .Where(a => !byId.ContainsKey(a.QuestionId) || IsPatientAgeQuestion(a.Question))
            .ToList();

        string? languagePreference = profile.LanguagePreference;
        string? wildcardConcern = profile.WildcardConcern;

        foreach (var input in answers)
        {
            if (!byId.TryGetValue(input.QuestionId, out var question))
                continue;
            if (IsPatientAgeQuestion(question.Question))
                continue;

            var isLanguage = NuviFlowContent.IsLanguageDeepDiveQuestion(question.Question);
            var isWildcard = NuviFlowContent.IsWildcardDeepDiveQuestion(question.Question);
            var stored = NormalizeAnswer(input, isLanguage, isWildcard, out var lang, out var wildcard);
            if (string.IsNullOrWhiteSpace(stored))
                continue;

            if (isLanguage)
                languagePreference = lang;
            if (isWildcard)
                wildcardConcern = wildcard;

            next.RemoveAll(a => a.QuestionId == question.Id);
            next.Add(new PollingAnswerEntry
            {
                QuestionId = question.Id,
                Question = question.Question,
                Answer = stored,
                MatchWeight = question.MatchWeight
            });
        }

        profile.DeepDiveAnswers = next;
        profile.LanguagePreference = languagePreference;
        profile.WildcardConcern = wildcardConcern;
        profile.UpdatedAt = DateTime.UtcNow;

        patient.PreferenceProfileJson = JsonSerializer.Serialize(profile, JsonOptions);
        await _db.SaveChangesAsync(cancellationToken);
        return (true, null);
    }

    private async Task<PatientPreferenceProfile?> LoadProfileAsync(int patientId, CancellationToken cancellationToken)
    {
        var json = await _db.Patients.AsNoTracking()
            .Where(p => p.Id == patientId)
            .Select(p => p.PreferenceProfileJson)
            .FirstOrDefaultAsync(cancellationToken);

        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            return JsonSerializer.Deserialize<PatientPreferenceProfile>(json, JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    private static string NormalizeAnswer(
        PatientPreferenceAnswerInput input,
        bool isLanguage,
        bool isWildcard,
        out string? language,
        out string? wildcard)
    {
        language = null;
        wildcard = null;
        var answer = (input.Answer ?? string.Empty).Trim();
        var followUp = (input.FollowUp ?? string.Empty).Trim();

        if (isLanguage)
        {
            if (IsNo(answer) || string.IsNullOrWhiteSpace(answer))
                return "No";
            if (string.IsNullOrWhiteSpace(followUp))
                return string.Empty;
            language = followUp;
            return $"Yes — {followUp}";
        }

        if (isWildcard)
        {
            if (IsNo(answer))
                return "No";
            wildcard = !string.IsNullOrWhiteSpace(followUp) ? followUp : answer;
            if (wildcard.Equals("Yes", StringComparison.OrdinalIgnoreCase) || wildcard.Length < 2)
                return string.Empty;
            return wildcard;
        }

        return answer;
    }

    private static (string Answer, string? FollowUp) SplitStoredAnswer(
        string? stored,
        bool isLanguage,
        bool isWildcard,
        PatientPreferenceProfile? profile)
    {
        var value = (stored ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(value))
            return ("", null);

        if (isLanguage)
        {
            if (IsNo(value))
                return ("No", null);
            var dash = value.IndexOf('—');
            if (dash < 0)
                dash = value.IndexOf('-');
            var follow = dash >= 0 ? value[(dash + 1)..].Trim() : (profile?.LanguagePreference ?? value);
            if (follow.StartsWith("Yes", StringComparison.OrdinalIgnoreCase))
                follow = profile?.LanguagePreference ?? "";
            return ("Yes", string.IsNullOrWhiteSpace(follow) ? profile?.LanguagePreference : follow);
        }

        if (isWildcard)
        {
            if (IsNo(value))
                return ("No", null);
            return ("Yes", value);
        }

        return (value, null);
    }

    private static bool IsNo(string value) =>
        value.Equals("no", StringComparison.OrdinalIgnoreCase)
        || value.StartsWith("no ", StringComparison.OrdinalIgnoreCase);

    private static bool IsPatientAgeQuestion(string question)
    {
        if (question.Contains("doctor", StringComparison.OrdinalIgnoreCase))
            return false;
        return question.Contains("old are you", StringComparison.OrdinalIgnoreCase)
            || question.Contains("how old are you", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsYesNoOptions(IReadOnlyList<string>? options) =>
        options is { Count: 2 }
        && options.Any(o => o.Equals("Yes", StringComparison.OrdinalIgnoreCase))
        && options.Any(o => o.Equals("No", StringComparison.OrdinalIgnoreCase));

    private static IReadOnlyList<string>? GetOptions(PollingQuestionDto question)
    {
        if (NuviFlowContent.IsWildcardDeepDiveQuestion(question.Question)
            || NuviFlowContent.IsLanguageDeepDiveQuestion(question.Question))
            return ["Yes", "No"];

        var hint = question.ValidationHint;
        if (string.IsNullOrWhiteSpace(hint))
            return null;

        if (hint.StartsWith("Required", StringComparison.OrdinalIgnoreCase))
            return null;

        if (hint.Contains("1 through 5", StringComparison.OrdinalIgnoreCase))
            return ["1", "2", "3", "4", "5"];

        if (hint.Contains('/'))
            return hint.Split('/').Select(s => s.Trim()).Where(s => s.Length > 0).ToList();

        if (!hint.Contains(',') && hint.Contains(" or ", StringComparison.OrdinalIgnoreCase))
            return hint.Split(" or ", StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToList();

        if (!hint.Contains(','))
            return null;

        return hint.Split(',')
            .Select(s => s.Trim())
            .Select(s => s.StartsWith("or ", StringComparison.OrdinalIgnoreCase) ? s[3..].Trim() : s)
            .Where(s => s.Length > 0)
            .ToList();
    }
}
