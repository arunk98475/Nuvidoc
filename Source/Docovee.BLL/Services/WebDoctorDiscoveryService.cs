using System.Text.Json;
using System.Text.RegularExpressions;
using Docovee.BLL.Configuration;
using Docovee.BLL.Data;
using Docovee.DS;
using Docovee.DS.Entities;
using Docovee.DS.Enums;
using Docovee.logging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Docovee.BLL.Services;

public interface IWebDoctorDiscoveryService
{
    Task<IReadOnlyList<Doctor>> DiscoverAndImportAsync(
        string location,
        string specialty,
        int maxResults,
        CancellationToken cancellationToken = default);
}

public class WebDoctorDiscoveryService : IWebDoctorDiscoveryService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private static readonly Regex JsonFenceRegex = new(
        @"```(?:json)?\s*(\[[\s\S]*?\])\s*```",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly HttpClient _httpClient;
    private readonly DocoveeDbContext _db;
    private readonly AnthropicOptions _options;
    private readonly IDocoveeLogger _logger;

    public WebDoctorDiscoveryService(
        HttpClient httpClient,
        DocoveeDbContext db,
        IOptions<AnthropicOptions> options,
        IDocoveeLogger logger)
    {
        _httpClient = httpClient;
        _db = db;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<IReadOnlyList<Doctor>> DiscoverAndImportAsync(
        string location,
        string specialty,
        int maxResults,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(location)
            || string.IsNullOrWhiteSpace(_options.ApiKey)
            || string.IsNullOrWhiteSpace(_options.Model))
        {
            return Array.Empty<Doctor>();
        }

        var count = Math.Clamp(maxResults, 1, 5);
        var discovered = await FetchDoctorsFromWebAsync(location.Trim(), specialty.Trim(), count, cancellationToken);
        if (discovered.Count == 0)
            return Array.Empty<Doctor>();

        var importedIds = new List<int>();
        foreach (var item in discovered)
        {
            var doctorId = await ImportDoctorIfNewAsync(item, location.Trim(), cancellationToken);
            if (doctorId.HasValue)
                importedIds.Add(doctorId.Value);
        }

        if (importedIds.Count == 0)
            return Array.Empty<Doctor>();

        return await LoadDoctorsWithIncludesAsync(importedIds, cancellationToken);
    }

    private async Task<List<DiscoveredDoctorRecord>> FetchDoctorsFromWebAsync(
        string location,
        string specialty,
        int count,
        CancellationToken cancellationToken)
    {
        var systemPrompt = """
            You find real doctors for a healthcare matching app using web search (Google Maps, Healthgrades, hospital sites, practice websites).

            Rules:
            - Only include real, practicing physicians or dentists in the requested city/area.
            - Match the requested medical specialty as closely as possible.
            - Use web search to verify names, practice, address, phone, and Google ratings when available.
            - Do not invent doctors. If you cannot find enough real matches, return fewer items.
            - Always include city and state (2-letter US code) for each doctor.
            - Respond with ONLY a JSON array (no markdown prose), each object:
              {
                "name": "Dr. Full Name",
                "specialty": "Family Medicine",
                "specialtyCategory": "Family Medicine",
                "practiceName": "Practice name or null",
                "address": "Street address or null",
                "city": "Miami",
                "state": "FL",
                "zipCode": "33101",
                "phone": "phone or null",
                "googleRating": 4.5,
                "googleReviewCount": 100,
                "summaryOfReviews": "1-2 sentence summary from reviews or null",
                "niche": "notable focus or null",
                "gmbPhotoLink": "Google Maps or practice photo URL or null"
              }
            """;

        var userPrompt = $"""
            Find up to {count} highly rated {specialty} doctors in or near: {location}.
            Prefer doctors with strong Google reviews and clear practice information.
            """;

        try
        {
            var payload = AnthropicApiHelper.BuildPayload(
                _options,
                maxTokens: 4000,
                system: systemPrompt,
                messages: new[] { new { role = "user", content = userPrompt } },
                includeWebSearch: true);

            using var httpRequest = AnthropicApiHelper.CreateMessageRequest(_options, payload);
            var response = await _httpClient.SendAsync(httpRequest, cancellationToken);
            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Web doctor discovery failed with status {Status}", (int)response.StatusCode);
                return [];
            }

            var text = AnthropicApiHelper.ExtractTextContent(responseBody);
            var json = ExtractJsonArray(text);
            if (string.IsNullOrWhiteSpace(json))
                return [];

            var records = JsonSerializer.Deserialize<List<DiscoveredDoctorRecord>>(json, JsonOptions) ?? [];
            return records
                .Where(r => !string.IsNullOrWhiteSpace(r.Name))
                .Take(count)
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error discovering doctors via web search for {Location}", location);
            return [];
        }
    }

    private async Task<int?> ImportDoctorIfNewAsync(
        DiscoveredDoctorRecord record,
        string searchLocation,
        CancellationToken cancellationToken)
    {
        var (city, state) = ResolveCityState(searchLocation, record);
        var name = TruncateRequired(record.Name, 200);
        var specialty = TruncateRequired(
            string.IsNullOrWhiteSpace(record.Specialty) ? "General Practice" : record.Specialty, 150);
        var specialtyCategory = TruncateRequired(
            string.IsNullOrWhiteSpace(record.SpecialtyCategory) ? specialty : record.SpecialtyCategory, 150);

        var normalizedName = name.ToLowerInvariant();
        var normalizedCity = city.ToLowerInvariant();
        var phone = TruncateOptional(FormatPhone(record.Phone), 30);
        var normalizedPhone = NormalizePhone(phone);

        if (!string.IsNullOrWhiteSpace(normalizedPhone))
        {
            var existingByPhoneId = await FindDoctorIdByPhoneAsync(normalizedPhone, cancellationToken);
            if (existingByPhoneId.HasValue)
            {
                _logger.LogInformation(
                    "Skipping insert for {Name} — phone match (last 10 digits) found existing doctor id {Id}",
                    name, existingByPhoneId.Value);
                return existingByPhoneId.Value;
            }
        }

        var existing = await _db.Doctors
            .AsNoTracking()
            .FirstOrDefaultAsync(d =>
                d.IsActive
                && d.Name.ToLower() == normalizedName
                && d.City.ToLower() == normalizedCity,
                cancellationToken);

        if (existing != null)
            return existing.Id;

        var doctor = new Doctor
        {
            Name = name,
            Specialty = specialty,
            SpecialtyCategory = specialtyCategory,
            PracticeName = TruncateOptional(record.PracticeName, 200),
            Address = TruncateOptional(record.Address, 500),
            OfficePhoneNumber = phone,
            City = city,
            State = state,
            ZipCode = TruncateRequired(
                string.IsNullOrWhiteSpace(record.ZipCode) ? "00000" : record.ZipCode, 20),
            Location = TruncateRequired($"{city}, {state}", 200),
            GoogleRating = ClampRating(record.GoogleRating),
            GoogleReviewCount = Math.Max(0, record.GoogleReviewCount ?? 0),
            SummaryOfReviews = TruncateOptional(record.SummaryOfReviews, 4000),
            Niche = TruncateOptional(record.Niche, 200),
            GmbPhotoLink = TruncateOptional(DoctorPhotoHelper.NormalizeStoredLink(record.GmbPhotoLink), 2000),
            PhotoUrl = TruncateOptional(DoctorPhotoHelper.GetDisplayPhotoUrl(null, record.GmbPhotoLink), 2000),
            AvatarInitials = TruncateRequired(BuildInitials(name), 5),
            TagLine = "Web discovery",
            Gender = Gender.Other,
            Username = null,
            IsActive = true
        };

        try
        {
            _db.Doctors.Add(doctor);
            await _db.SaveChangesAsync(cancellationToken);
            _logger.LogInformation("Imported doctor {Name} in {City}, {State} via web discovery (id {Id})",
                name, city, state, doctor.Id);
            return doctor.Id;
        }
        catch (DbUpdateException ex)
        {
            _db.ChangeTracker.Clear();
            _logger.LogError(ex,
                "Failed to import doctor {Name} in {City}, {State}: {Inner}",
                name, city, state, ex.InnerException?.Message ?? ex.Message);
            return null;
        }
    }

    private static (string City, string State) ResolveCityState(string searchLocation, DiscoveredDoctorRecord record)
    {
        var searchParts = ParseSearchLocation(searchLocation);
        var city = record.City?.Trim();
        var state = UsStates.CodeFromNameOrCode(record.State)
            ?? (record.State?.Trim().Length <= 2 ? record.State.Trim().ToUpperInvariant() : null);

        if (string.IsNullOrWhiteSpace(state))
            state = searchParts.State ?? UsStates.CodeFromNameOrCode(searchLocation) ?? "NA";

        if (string.IsNullOrWhiteSpace(city))
            city = ParseCityFromLocation(record.Address) ?? searchParts.City;

        if (string.IsNullOrWhiteSpace(city))
            city = searchParts.State != null ? searchLocation.Trim() : "Unknown";

        return (TruncateRequired(city, 100), TruncateRequired(state, 50));
    }

    private static (string? City, string? State) ParseSearchLocation(string location)
    {
        var trimmed = location.Trim();
        var parts = trimmed.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 2)
        {
            var city = parts[0];
            var state = UsStates.CodeFromNameOrCode(parts[^1]) ?? parts[^1].ToUpperInvariant();
            return (city, state);
        }

        var stateOnly = UsStates.CodeFromNameOrCode(trimmed);
        if (stateOnly != null)
            return (null, stateOnly);

        return (trimmed, null);
    }

    private async Task<IReadOnlyList<Doctor>> LoadDoctorsWithIncludesAsync(
        IReadOnlyList<int> ids,
        CancellationToken cancellationToken) =>
        await _db.Doctors
            .Include(d => d.DoctorInsurances)
            .ThenInclude(di => di.InsuranceCarrier)
            .Include(d => d.DoctorLanguages)
            .ThenInclude(dl => dl.DoctorLanguage)
            .Include(d => d.PatientReviews)
            .Include(d => d.Locations)
            .Where(d => ids.Contains(d.Id))
            .ToListAsync(cancellationToken);

    private async Task<int?> FindDoctorIdByPhoneAsync(string normalizedPhone, CancellationToken cancellationToken)
    {
        var doctorPhones = await _db.Doctors
            .AsNoTracking()
            .Where(d => d.IsActive && d.OfficePhoneNumber != null && d.OfficePhoneNumber != "")
            .Select(d => new { d.Id, Phone = d.OfficePhoneNumber, d.IsSponsored, d.QualityScore })
            .ToListAsync(cancellationToken);

        var matches = doctorPhones
            .Where(d => PhoneNumberHelper.NormalizeLast10(d.Phone) == normalizedPhone)
            .OrderByDescending(d => d.IsSponsored)
            .ThenByDescending(d => d.QualityScore)
            .ThenByDescending(d => d.Id)
            .ToList();
        if (matches.Count > 0)
            return matches[0].Id;

        var locationPhones = await _db.DoctorLocations
            .AsNoTracking()
            .Where(l => l.PhoneNumber != null && l.PhoneNumber != "")
            .Select(l => new { l.DoctorId, Phone = l.PhoneNumber })
            .ToListAsync(cancellationToken);

        var locationMatch = locationPhones.FirstOrDefault(l =>
            PhoneNumberHelper.NormalizeLast10(l.Phone) == normalizedPhone);
        return locationMatch?.DoctorId;
    }

    private static string? NormalizePhone(string? phone) => PhoneNumberHelper.NormalizeLast10(phone);

    private static string? FormatPhone(string? phone)
    {
        var normalized = NormalizePhone(phone);
        if (normalized == null)
            return TruncateOptional(phone, 30);

        if (normalized.Length == 10)
            return $"({normalized[..3]}) {normalized[3..6]}-{normalized[6..]}";

        return normalized;
    }

    private static string? ExtractJsonArray(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var fenceMatch = JsonFenceRegex.Match(text);
        if (fenceMatch.Success)
            return fenceMatch.Groups[1].Value.Trim();

        var start = text.IndexOf('[');
        var end = text.LastIndexOf(']');
        if (start >= 0 && end > start)
            return text[start..(end + 1)];

        return null;
    }

    private static string? ParseCityFromLocation(string? address)
    {
        if (string.IsNullOrWhiteSpace(address))
            return null;

        var parts = address.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 ? parts[^2] : parts[0];
    }

    private static decimal ClampRating(decimal? rating)
    {
        if (!rating.HasValue)
            return 0;
        return Math.Clamp(rating.Value, 0, 5);
    }

    private static string BuildInitials(string name)
    {
        var stripped = name
            .Replace("Dr.", "", StringComparison.OrdinalIgnoreCase)
            .Replace("Dr ", "", StringComparison.OrdinalIgnoreCase)
            .Trim();
        var parts = stripped.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
            return "DR";
        if (parts.Length == 1)
            return parts[0][..Math.Min(2, parts[0].Length)].ToUpperInvariant();
        return $"{parts[0][0]}{parts[^1][0]}".ToUpperInvariant();
    }

    private static string TruncateRequired(string? value, int maxLength)
    {
        var trimmed = (value ?? string.Empty).Trim();
        if (trimmed.Length == 0)
            return "-";
        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
    }

    private static string? TruncateOptional(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var trimmed = value.Trim();
        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
    }

    private sealed class DiscoveredDoctorRecord
    {
        public string Name { get; set; } = string.Empty;
        public string? Specialty { get; set; }
        public string? SpecialtyCategory { get; set; }
        public string? PracticeName { get; set; }
        public string? Address { get; set; }
        public string? City { get; set; }
        public string? State { get; set; }
        public string? ZipCode { get; set; }
        public string? Phone { get; set; }
        public decimal? GoogleRating { get; set; }
        public int? GoogleReviewCount { get; set; }
        public string? SummaryOfReviews { get; set; }
        public string? Niche { get; set; }
        public string? GmbPhotoLink { get; set; }
    }
}
