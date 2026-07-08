using System.Text.Json;
using System.Text.RegularExpressions;

namespace Docovee.BLL.Services;

public static class DoctorProfileHelper
{
    private static readonly Regex UrlRegex = new(
        @"https?://[^\s""'<>]+",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly string[] VideoHostHints =
    [
        "youtube.com", "youtu.be", "vimeo.com", "dailymotion.com",
        ".mp4", ".webm", ".ogg", "loom.com"
    ];

    public static string? ExtractVideoUrl(string? onboardingProfileJson)
    {
        if (string.IsNullOrWhiteSpace(onboardingProfileJson))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(onboardingProfileJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return null;

            // Question 52: educational content (videos, blog, podcast)
            if (doc.RootElement.TryGetProperty("52", out var q52))
            {
                var fromQ52 = FindFirstVideoUrl(q52.GetString());
                if (!string.IsNullOrWhiteSpace(fromQ52))
                    return fromQ52;
            }

            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                var url = FindFirstVideoUrl(prop.Value.GetString());
                if (!string.IsNullOrWhiteSpace(url))
                    return url;
            }
        }
        catch (JsonException)
        {
            return FindFirstVideoUrl(onboardingProfileJson);
        }

        return null;
    }

    private static string? FindFirstVideoUrl(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var lower = text.ToLowerInvariant();
        if (lower is "no" or "n" or "none" or "n/a")
            return null;

        foreach (Match match in UrlRegex.Matches(text))
        {
            var url = match.Value.TrimEnd('.', ',', ';', ')');
            if (LooksLikeVideoUrl(url))
                return url;
        }

        return LooksLikeVideoUrl(text.Trim()) ? text.Trim() : null;
    }

    private static bool LooksLikeVideoUrl(string url)
    {
        var lower = url.ToLowerInvariant();
        return VideoHostHints.Any(lower.Contains);
    }
}
