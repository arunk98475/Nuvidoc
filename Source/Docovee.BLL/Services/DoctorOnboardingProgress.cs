using System.Text.Json;
using Docovee.BLL.Data;
using Docovee.DS.Entities;

namespace Docovee.BLL.Services;

public static class DoctorOnboardingProgress
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static int TotalQuestions => DoctorOnboardingQuestions.All.Count;

    public static int CalculatePercent(int answeredQuestionCount, bool complete = false)
    {
        if (complete || TotalQuestions == 0)
            return 100;

        return Math.Min(100, (int)Math.Round((double)answeredQuestionCount / TotalQuestions * 100));
    }

    public static int CountAnsweredQuestions(Dictionary<int, string> answers) =>
        answers.Count(kvp => !string.IsNullOrWhiteSpace(kvp.Value));

    public static Dictionary<int, string> LoadAnswers(Doctor doctor)
    {
        if (string.IsNullOrWhiteSpace(doctor.OnboardingProfileJson))
            return new Dictionary<int, string>();

        try
        {
            var raw = JsonSerializer.Deserialize<Dictionary<string, string>>(doctor.OnboardingProfileJson, JsonOptions)
                      ?? new Dictionary<string, string>();
            return raw
                .Where(kvp => int.TryParse(kvp.Key, out _))
                .ToDictionary(kvp => int.Parse(kvp.Key), kvp => kvp.Value);
        }
        catch
        {
            return new Dictionary<int, string>();
        }
    }

    public static string SerializeAnswers(Dictionary<int, string> answers) =>
        JsonSerializer.Serialize(
            answers.ToDictionary(kvp => kvp.Key.ToString(), kvp => kvp.Value),
            JsonOptions);

    public static bool IsOnboardingComplete(Doctor doctor) =>
        doctor.ProfileCompletionPercent >= 100;

    public static int ResumeQuestionIndex(Doctor doctor)
    {
        if (IsOnboardingComplete(doctor))
            return TotalQuestions;

        return Math.Clamp(doctor.OnboardingQuestionIndex, 0, TotalQuestions);
    }
}
