using System.Text.Json;
using System.Text.RegularExpressions;
using Docovee.DS.Models;

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

    public static (string? Website, bool AllowGoogleBookings) ExtractPracticeSettings(string? onboardingProfileJson)
    {
        if (string.IsNullOrWhiteSpace(onboardingProfileJson))
            return (null, true);

        try
        {
            using var doc = JsonDocument.Parse(onboardingProfileJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return (null, true);

            if (!doc.RootElement.TryGetProperty("practiceSettings", out var settings)
                || settings.ValueKind != JsonValueKind.Object)
                return (null, true);

            string? website = settings.TryGetProperty("website", out var w) ? w.GetString() : null;
            var allow = true;
            if (settings.TryGetProperty("allowGoogleBookings", out var g))
            {
                allow = g.ValueKind switch
                {
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    JsonValueKind.String => !string.Equals(g.GetString(), "no", StringComparison.OrdinalIgnoreCase),
                    _ => true
                };
            }

            return (website, allow);
        }
        catch (JsonException)
        {
            return (null, true);
        }
    }

    public static string MergePracticeSettings(string? onboardingProfileJson, string? website, bool allowGoogleBookings)
    {
        var root = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(onboardingProfileJson))
        {
            try
            {
                using var doc = JsonDocument.Parse(onboardingProfileJson);
                if (doc.RootElement.ValueKind == JsonValueKind.Object)
                {
                    foreach (var prop in doc.RootElement.EnumerateObject())
                    {
                        if (prop.NameEquals("practiceSettings"))
                            continue;
                        root[prop.Name] = prop.Value.Clone();
                    }
                }
            }
            catch (JsonException)
            {
                // start fresh
            }
        }

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            foreach (var kvp in root)
            {
                writer.WritePropertyName(kvp.Key);
                kvp.Value.WriteTo(writer);
            }
            writer.WritePropertyName("practiceSettings");
            writer.WriteStartObject();
            writer.WriteString("website", website?.Trim() ?? "");
            writer.WriteBoolean("allowGoogleBookings", allowGoogleBookings);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    public static IReadOnlyList<VisitReasonCategoryPreference> ExtractVisitReasonPreferences(string? onboardingProfileJson)
    {
        if (string.IsNullOrWhiteSpace(onboardingProfileJson))
            return Array.Empty<VisitReasonCategoryPreference>();

        try
        {
            using var doc = JsonDocument.Parse(onboardingProfileJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return Array.Empty<VisitReasonCategoryPreference>();

            if (!doc.RootElement.TryGetProperty("visitReasonPreferences", out var prefs)
                || prefs.ValueKind != JsonValueKind.Object)
                return Array.Empty<VisitReasonCategoryPreference>();

            if (!prefs.TryGetProperty("categories", out var cats) || cats.ValueKind != JsonValueKind.Array)
                return Array.Empty<VisitReasonCategoryPreference>();

            var list = new List<VisitReasonCategoryPreference>();
            foreach (var item in cats.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                    continue;

                var key = item.TryGetProperty("key", out var k) ? k.GetString() ?? "" : "";
                if (string.IsNullOrWhiteSpace(key))
                    continue;

                var enabled = item.TryGetProperty("enabled", out var en) && en.ValueKind == JsonValueKind.True;
                var newMins = item.TryGetProperty("newPatientMinutes", out var nm) && nm.TryGetInt32(out var nmi) ? nmi : 45;
                var existMins = item.TryGetProperty("existingPatientMinutes", out var em) && em.TryGetInt32(out var emi) ? emi : 45;
                var popular = new List<string>();
                if (item.TryGetProperty("popularSelectedKeys", out var pop) && pop.ValueKind == JsonValueKind.Array)
                {
                    foreach (var p in pop.EnumerateArray())
                    {
                        var s = p.GetString();
                        if (!string.IsNullOrWhiteSpace(s))
                            popular.Add(s);
                    }
                }

                list.Add(new VisitReasonCategoryPreference
                {
                    Key = key,
                    Enabled = enabled,
                    NewPatientMinutes = newMins,
                    ExistingPatientMinutes = existMins,
                    PopularSelectedKeys = popular
                });
            }

            return list;
        }
        catch (JsonException)
        {
            return Array.Empty<VisitReasonCategoryPreference>();
        }
    }

    public static string MergeVisitReasonPreferences(string? onboardingProfileJson, IEnumerable<VisitReasonCategoryPreference> categories)
    {
        var root = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(onboardingProfileJson))
        {
            try
            {
                using var doc = JsonDocument.Parse(onboardingProfileJson);
                if (doc.RootElement.ValueKind == JsonValueKind.Object)
                {
                    foreach (var prop in doc.RootElement.EnumerateObject())
                    {
                        if (prop.NameEquals("visitReasonPreferences"))
                            continue;
                        root[prop.Name] = prop.Value.Clone();
                    }
                }
            }
            catch (JsonException)
            {
                // start fresh
            }
        }

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            foreach (var kvp in root)
            {
                writer.WritePropertyName(kvp.Key);
                kvp.Value.WriteTo(writer);
            }

            writer.WritePropertyName("visitReasonPreferences");
            writer.WriteStartObject();
            writer.WritePropertyName("categories");
            writer.WriteStartArray();
            foreach (var cat in categories)
            {
                writer.WriteStartObject();
                writer.WriteString("key", cat.Key);
                writer.WriteBoolean("enabled", cat.Enabled);
                writer.WriteNumber("newPatientMinutes", cat.NewPatientMinutes);
                writer.WriteNumber("existingPatientMinutes", cat.ExistingPatientMinutes);
                writer.WritePropertyName("popularSelectedKeys");
                writer.WriteStartArray();
                foreach (var key in cat.PopularSelectedKeys.Distinct(StringComparer.OrdinalIgnoreCase))
                    writer.WriteStringValue(key);
                writer.WriteEndArray();
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    public static readonly string[] WorkingHourDays =
    [
        "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday"
    ];

    public static WorkingHoursInput DefaultWorkingHours()
    {
        return new WorkingHoursInput
        {
            Days = WorkingHourDays.Select(day =>
            {
                var weekday = day is not ("Saturday" or "Sunday");
                return new WorkingHoursDayInput
                {
                    Day = day,
                    Enabled = weekday,
                    Blocks =
                    [
                        new WorkingHoursBlockInput
                        {
                            StartTime = "09:00",
                            EndTime = "17:00",
                            LocationIds = new List<int>()
                        }
                    ]
                };
            }).ToList()
        };
    }

    public static WorkingHoursInput ExtractWorkingHours(string? onboardingProfileJson)
    {
        var defaults = DefaultWorkingHours();
        if (string.IsNullOrWhiteSpace(onboardingProfileJson))
            return defaults;

        try
        {
            using var doc = JsonDocument.Parse(onboardingProfileJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return defaults;

            if (!doc.RootElement.TryGetProperty("workingHours", out var hours)
                || hours.ValueKind != JsonValueKind.Object)
                return defaults;

            if (!hours.TryGetProperty("days", out var daysEl) || daysEl.ValueKind != JsonValueKind.Array)
                return defaults;

            var byDay = new Dictionary<string, WorkingHoursDayInput>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in daysEl.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                    continue;

                var day = item.TryGetProperty("day", out var d) ? d.GetString() ?? "" : "";
                if (string.IsNullOrWhiteSpace(day))
                    continue;

                var enabled = item.TryGetProperty("enabled", out var en) && en.ValueKind == JsonValueKind.True;
                var blocks = new List<WorkingHoursBlockInput>();
                if (item.TryGetProperty("blocks", out var blocksEl) && blocksEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (var b in blocksEl.EnumerateArray())
                    {
                        if (b.ValueKind != JsonValueKind.Object)
                            continue;
                        var start = b.TryGetProperty("startTime", out var s) ? s.GetString() ?? "09:00" : "09:00";
                        var end = b.TryGetProperty("endTime", out var e) ? e.GetString() ?? "17:00" : "17:00";
                        var locIds = new List<int>();
                        if (b.TryGetProperty("locationIds", out var locs) && locs.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var loc in locs.EnumerateArray())
                            {
                                if (loc.TryGetInt32(out var id) && id > 0)
                                    locIds.Add(id);
                            }
                        }

                        blocks.Add(new WorkingHoursBlockInput
                        {
                            StartTime = start,
                            EndTime = end,
                            LocationIds = locIds
                        });
                    }
                }

                if (blocks.Count == 0)
                {
                    blocks.Add(new WorkingHoursBlockInput
                    {
                        StartTime = "09:00",
                        EndTime = "17:00",
                        LocationIds = new List<int>()
                    });
                }

                byDay[day] = new WorkingHoursDayInput
                {
                    Day = day,
                    Enabled = enabled,
                    Blocks = blocks
                };
            }

            return new WorkingHoursInput
            {
                Days = WorkingHourDays.Select(day =>
                    byDay.TryGetValue(day, out var existing)
                        ? existing
                        : defaults.Days.First(d => d.Day == day)).ToList()
            };
        }
        catch (JsonException)
        {
            return defaults;
        }
    }

    public static string MergeWorkingHours(string? onboardingProfileJson, WorkingHoursInput hours)
    {
        var root = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(onboardingProfileJson))
        {
            try
            {
                using var doc = JsonDocument.Parse(onboardingProfileJson);
                if (doc.RootElement.ValueKind == JsonValueKind.Object)
                {
                    foreach (var prop in doc.RootElement.EnumerateObject())
                    {
                        if (prop.NameEquals("workingHours"))
                            continue;
                        root[prop.Name] = prop.Value.Clone();
                    }
                }
            }
            catch (JsonException)
            {
                // start fresh
            }
        }

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            foreach (var kvp in root)
            {
                writer.WritePropertyName(kvp.Key);
                kvp.Value.WriteTo(writer);
            }

            writer.WritePropertyName("workingHours");
            writer.WriteStartObject();
            writer.WritePropertyName("days");
            writer.WriteStartArray();
            foreach (var day in hours.Days)
            {
                writer.WriteStartObject();
                writer.WriteString("day", day.Day);
                writer.WriteBoolean("enabled", day.Enabled);
                writer.WritePropertyName("blocks");
                writer.WriteStartArray();
                foreach (var block in day.Blocks)
                {
                    writer.WriteStartObject();
                    writer.WriteString("startTime", block.StartTime);
                    writer.WriteString("endTime", block.EndTime);
                    writer.WritePropertyName("locationIds");
                    writer.WriteStartArray();
                    foreach (var id in block.LocationIds.Distinct())
                        writer.WriteNumberValue(id);
                    writer.WriteEndArray();
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }
}
