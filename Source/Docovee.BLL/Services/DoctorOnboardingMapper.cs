using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Docovee.BLL.Data;
using Docovee.DS.Models;
using Docovee.DS.Entities;

namespace Docovee.BLL.Services;

public sealed class DoctorOnboardingRegistrationData
{
    public AccountRegisterRequest RegisterRequest { get; init; } = new() { AccountType = AccountType.Doctor };
    public int? YearsOfPractice { get; init; }
    public int? GraduationYear { get; init; }
    public int? GoogleReviewCount { get; init; }
    public decimal? GoogleRating { get; init; }
    public string? Top3Procedures { get; init; }
    public string? Niche { get; init; }
    public string? TagLine { get; init; }
    public string? SummaryOfReviews { get; init; }
    public string? PhotoUrl { get; init; }
    public string? GmbPhotoLink { get; init; }
    public int? Age { get; init; }
    public string OnboardingProfileJson { get; init; } = "{}";
    public List<int> InsuranceCarrierIds { get; init; } = new();
}

public static class DoctorOnboardingMapper
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static DoctorOnboardingRegistrationData BuildRegistration(
        Dictionary<int, string> answers,
        string username,
        string password,
        IReadOnlyList<InsuranceCarrier> carriers)
    {
        var profile = answers.ToDictionary(
            kvp => kvp.Key.ToString(),
            kvp => kvp.Value);

        var address = ParseAddress(Get(answers, 12));
        var specialty = Get(answers, 2);
        if (string.IsNullOrWhiteSpace(specialty))
            specialty = "General Practice";

        var request = new AccountRegisterRequest
        {
            AccountType = AccountType.Doctor,
            Username = username,
            Password = password,
            ConfirmPassword = password,
            DoctorName = Get(answers, 1) ?? "Doctor",
            PracticeName = Get(answers, 11),
            Specialty = specialty,
            Address = address.Street,
            City = address.City,
            State = address.State,
            ZipCode = address.ZipCode,
            OfficePhoneNumber = null,
            GmbPhotoLink = GetUrl(answers, 50),
            Niche = Get(answers, 3) ?? Get(answers, 24),
            TagLine = Get(answers, 40) ?? Get(answers, 25),
            InsuranceCarrierIds = MatchInsuranceCarriers(Get(answers, 17), carriers)
        };

        return new DoctorOnboardingRegistrationData
        {
            RegisterRequest = request,
            YearsOfPractice = ParseInt(Get(answers, 9)),
            GraduationYear = ParseInt(Get(answers, 5)),
            GoogleReviewCount = ParseInt(Get(answers, 48)),
            GoogleRating = ParseDecimal(Get(answers, 47)),
            Top3Procedures = Get(answers, 23),
            Niche = request.Niche,
            TagLine = request.TagLine,
            SummaryOfReviews = Get(answers, 43),
            PhotoUrl = null,
            GmbPhotoLink = request.GmbPhotoLink,
            Age = ParseAge(Get(answers, 31)),
            OnboardingProfileJson = JsonSerializer.Serialize(profile, JsonOptions),
            InsuranceCarrierIds = request.InsuranceCarrierIds
        };
    }

    public static void ApplyProfileFields(Doctor doctor, DoctorOnboardingRegistrationData data)
    {
        if (data.YearsOfPractice.HasValue)
            doctor.YearsOfPractice = data.YearsOfPractice;
        if (data.GraduationYear.HasValue)
            doctor.GraduationYear = data.GraduationYear;
        if (data.GoogleReviewCount.HasValue)
            doctor.GoogleReviewCount = data.GoogleReviewCount.Value;
        if (data.GoogleRating.HasValue)
            doctor.GoogleRating = data.GoogleRating.Value;
        if (!string.IsNullOrWhiteSpace(data.Top3Procedures))
            doctor.Top3Procedures = data.Top3Procedures;
        if (!string.IsNullOrWhiteSpace(data.Niche))
            doctor.Niche = data.Niche;
        if (!string.IsNullOrWhiteSpace(data.TagLine))
            doctor.TagLine = data.TagLine;
        if (!string.IsNullOrWhiteSpace(data.SummaryOfReviews))
            doctor.SummaryOfReviews = data.SummaryOfReviews;
        if (!string.IsNullOrWhiteSpace(data.PhotoUrl))
            doctor.PhotoUrl = data.PhotoUrl;
        if (!string.IsNullOrWhiteSpace(data.GmbPhotoLink))
            doctor.GmbPhotoLink = DoctorPhotoHelper.NormalizeStoredLink(data.GmbPhotoLink);
        if (data.Age.HasValue)
            doctor.Age = data.Age;
        doctor.OnboardingProfileJson = data.OnboardingProfileJson;
        doctor.PhotoUrl = DoctorPhotoHelper.GetDisplayPhotoUrl(doctor.PhotoUrl, doctor.GmbPhotoLink);
    }

    private static string? Get(Dictionary<int, string> answers, int id) =>
        answers.TryGetValue(id, out var value) && !string.IsNullOrWhiteSpace(value) ? value.Trim() : null;

    private static string? GetUrl(Dictionary<int, string> answers, int id)
    {
        var value = Get(answers, id);
        if (string.IsNullOrWhiteSpace(value))
            return null;
        return value.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? value.Trim() : null;
    }

    private static int? ParseInt(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var match = Regex.Match(value, @"\d+");
        return match.Success && int.TryParse(match.Value, out var n) ? n : null;
    }

    private static decimal? ParseDecimal(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var match = Regex.Match(value, @"\d+(\.\d+)?");
        return match.Success && decimal.TryParse(match.Value, NumberStyles.Number, CultureInfo.InvariantCulture, out var n)
            ? n
            : null;
    }

    private static int? ParseAge(string? value)
    {
        var direct = ParseInt(value);
        if (direct.HasValue && direct.Value > 0 && direct.Value < 120)
            return direct;

        if (string.IsNullOrWhiteSpace(value))
            return null;

        var lower = value.ToLowerInvariant();
        if (lower.Contains("30")) return 35;
        if (lower.Contains("40")) return 45;
        if (lower.Contains("50")) return 55;
        if (lower.Contains("60")) return 65;
        return null;
    }

    private static List<int> MatchInsuranceCarriers(string? answer, IReadOnlyList<InsuranceCarrier> carriers)
    {
        if (string.IsNullOrWhiteSpace(answer) || carriers.Count == 0)
            return new List<int>();

        var lower = answer.ToLowerInvariant();
        return carriers
            .Where(c => lower.Contains(c.Name.ToLowerInvariant()) || lower.Contains(c.Code.ToLowerInvariant()))
            .Select(c => c.Id)
            .Distinct()
            .ToList();
    }

    private static (string? Street, string City, string State, string ZipCode) ParseAddress(string? answer)
    {
        if (string.IsNullOrWhiteSpace(answer))
            return (null, "Unknown", "CA", "00000");

        var parts = answer.Split(',')
            .Select(p => p.Trim())
            .Where(p => p.Length > 0)
            .ToList();

        var zip = "00000";
        var state = "";
        var city = "";
        string? street = null;

        if (parts.Count > 0 && Regex.IsMatch(parts[^1], @"^\d{5}(-\d{4})?$"))
        {
            zip = parts[^1];
            parts.RemoveAt(parts.Count - 1);
        }

        for (var i = parts.Count - 1; i >= 0 && string.IsNullOrEmpty(state); i--)
        {
            var parsed = TryParseState(parts[i]);
            if (parsed != null)
            {
                state = parsed;
                parts.RemoveAt(i);
            }
        }

        if (parts.Count > 0)
        {
            city = parts[^1];
            if (parts.Count > 1)
                street = string.Join(", ", parts.Take(parts.Count - 1));
            else
                street = parts[0];
        }

        if (string.IsNullOrEmpty(city))
            city = answer;
        if (string.IsNullOrEmpty(state))
            state = "CA";

        return (street, city, state, zip);
    }

    private static string? TryParseState(string segment)
    {
        segment = segment.Trim();
        var code = UsStates.Normalize(segment);
        if (code != null)
            return code;

        var byName = UsStates.All.FirstOrDefault(s =>
            segment.Equals(s.Name, StringComparison.OrdinalIgnoreCase));
        return byName.Code;
    }
}
