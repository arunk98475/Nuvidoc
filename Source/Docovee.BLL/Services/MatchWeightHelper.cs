using Docovee.BLL.Models;
using Docovee.DS.Entities;

namespace Docovee.BLL.Services;

public static class MatchWeightHelper
{
    public const int DefaultWeight = 5;

    public static int ParseLabel(string? label)
    {
        if (string.IsNullOrWhiteSpace(label))
            return DefaultWeight;

        var normalized = label.Trim().ToLowerInvariant();
        return normalized switch
        {
            "critical" => 10,
            var s when s.StartsWith("high") => 8,
            "medium" => 5,
            "low-medium" => 3,
            var s when s.StartsWith("low") => 1,
            "variable" => 5,
            _ when int.TryParse(label, out var numeric) => Math.Clamp(numeric, 1, 10),
            _ => DefaultWeight
        };
    }

    public static string ToLabel(int weight) => weight switch
    {
        >= 10 => "Critical",
        >= 8 => "High",
        >= 5 => "Medium",
        >= 3 => "Low-Medium",
        _ => "Low"
    };

    public static string FormatWeightedPreferences(IEnumerable<PollingAnswerEntry> answers)
    {
        var lines = answers
            .Where(a => !string.IsNullOrWhiteSpace(a.Answer))
            .OrderByDescending(a => a.MatchWeight > 0 ? a.MatchWeight : DefaultWeight)
            .Select(a =>
            {
                var weight = a.MatchWeight > 0 ? a.MatchWeight : DefaultWeight;
                return $"- [Weight {weight}/{ToLabel(weight)}] {a.Question}: {a.Answer}";
            });

        return string.Join("\n", lines);
    }

    public static int ComputeDoctorPreferenceBoost(
        Doctor doctor,
        IReadOnlyList<PollingAnswerEntry> answers,
        double? distanceMiles)
    {
        if (answers.Count == 0)
            return 0;

        var boost = 0;
        foreach (var answer in answers)
        {
            var weight = answer.MatchWeight > 0 ? answer.MatchWeight : DefaultWeight;
            var factor = weight / 10.0;
            var question = answer.Question.ToLowerInvariant();
            var value = answer.Answer.ToLowerInvariant();

            if (question.Contains("close to home") || question.Contains("close to home or work"))
            {
                if (value.Contains("very important") && distanceMiles is <= 10)
                    boost += (int)Math.Round(8 * factor);
                else if (value.Contains("somewhat") && distanceMiles is <= 20)
                    boost += (int)Math.Round(4 * factor);
            }
            else if (question.Contains("travel 30"))
            {
                if (value.Contains("yes") && distanceMiles is > 15 and <= 45)
                    boost += (int)Math.Round(4 * factor);
            }
            else if (question.Contains("experience level") || question.Contains("been practicing"))
            {
                if ((value.Contains("yes") || value.Contains("many")) && doctor.YearsOfPractice is >= 15)
                    boost += (int)Math.Round(10 * factor);
            }
            else if (question.Contains("top-ranked") || question.Contains("medical school"))
            {
                if (value.Contains("yes") && doctor.GraduationYear is < 2010)
                    boost += (int)Math.Round(5 * factor);
            }
            else if (question.Contains("online reviews") || question.Contains("healthgrades"))
            {
                if (value.Contains("yes") && doctor.GoogleRating >= 4.5m)
                    boost += (int)Math.Round(8 * factor);
            }
            else if (question.Contains("newer doctor") || question.Contains("fewer reviews"))
            {
                if (value.Contains("yes") && doctor.GoogleReviewCount is < 50 and > 0)
                    boost += (int)Math.Round(4 * factor);
            }
            else if (question.Contains("bedside manner") || question.Contains("personality"))
            {
                if (int.TryParse(new string(value.Where(char.IsDigit).ToArray()), out var scale) && scale >= 4)
                    boost += (int)Math.Round(3 * factor);
            }
            else if (question.Contains("holistic") || question.Contains("integrative"))
            {
                if (value.Contains("holistic") && !string.IsNullOrWhiteSpace(doctor.Niche)
                    && doctor.Niche.Contains("holistic", StringComparison.OrdinalIgnoreCase))
                    boost += (int)Math.Round(6 * factor);
            }
            else if (question.Contains("age group"))
            {
                if (value.Contains("30") && doctor.Age is >= 28 and <= 39)
                    boost += (int)Math.Round(3 * factor);
                else if ((value.Contains("40") || value.Contains("50")) && doctor.Age is >= 40 and <= 59)
                    boost += (int)Math.Round(3 * factor);
                else if (value.Contains("60") && doctor.Age is >= 60)
                    boost += (int)Math.Round(3 * factor);
            }
        }

        return Math.Min(boost, 25);
    }
}
