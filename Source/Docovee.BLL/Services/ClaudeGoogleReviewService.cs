using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Docovee.BLL.Configuration;
using Docovee.DS;
using Docovee.DS.Entities;
using Docovee.DS.Models;
using Docovee.logging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Docovee.BLL.Services;

public sealed class GoogleReviewLookupResult
{
    public decimal GoogleRating { get; init; }
    public int GoogleReviewCount { get; init; }
    public string? SummaryOfReviews { get; init; }
    public IReadOnlyList<PublicDoctorReviewDto> Reviews { get; init; } = Array.Empty<PublicDoctorReviewDto>();
    public bool Found { get; init; }
    public bool FromCache { get; init; }
    public DateTime? FetchedAt { get; init; }
}

public interface IClaudeGoogleReviewService
{
    /// <summary>
    /// Returns Google reviews for a doctor. Uses the on-disk cache when the last fetch
    /// is within <see cref="AnthropicOptions.GoogleReviewCacheDays"/>; otherwise fetches
    /// via Claude, updates DB ratings, and writes the review file.
    /// </summary>
    Task<GoogleReviewLookupResult?> LookupAsync(int doctorId, CancellationToken cancellationToken = default);

    Task<GoogleReviewLookupResult?> LookupAsync(Doctor doctor, CancellationToken cancellationToken = default);

    /// <summary>
    /// Loads the last saved on-disk Google review file only (no Claude call), even if the cache is stale.
    /// </summary>
    Task<GoogleReviewLookupResult?> GetCachedAsync(int doctorId, CancellationToken cancellationToken = default);

    Task<GoogleReviewLookupResult?> GetCachedAsync(Doctor doctor, CancellationToken cancellationToken = default);
}

public class ClaudeGoogleReviewService : IClaudeGoogleReviewService
{
    private const string ReviewFileName = "google-reviews.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static readonly Regex JsonObjectFenceRegex = new(
        @"```(?:json)?\s*(\{[\s\S]*?\})\s*```",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex JsonObjectLooseRegex = new(
        @"\{[\s\S]*""googleRating""[\s\S]*\}",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly HttpClient _httpClient;
    private readonly DocoveeDbContext _db;
    private readonly AnthropicOptions _options;
    private readonly UploadOptions _uploadOptions;
    private readonly IDocoveeLogger _logger;
    private readonly IDoctorQualityScoreService _qualityScore;

    public ClaudeGoogleReviewService(
        HttpClient httpClient,
        DocoveeDbContext db,
        IOptions<AnthropicOptions> options,
        IOptions<UploadOptions> uploadOptions,
        IDocoveeLogger logger,
        IDoctorQualityScoreService qualityScore)
    {
        _httpClient = httpClient;
        _db = db;
        _options = options.Value;
        _uploadOptions = uploadOptions.Value;
        _logger = logger;
        _qualityScore = qualityScore;
    }

    public async Task<GoogleReviewLookupResult?> LookupAsync(int doctorId, CancellationToken cancellationToken = default)
    {
        var doctor = await _db.Doctors.AsNoTracking()
            .FirstOrDefaultAsync(d => d.Id == doctorId && d.IsActive, cancellationToken);

        if (doctor == null)
            return null;

        return await LookupAsync(doctor, cancellationToken);
    }

    public async Task<GoogleReviewLookupResult?> GetCachedAsync(int doctorId, CancellationToken cancellationToken = default)
    {
        var doctor = await _db.Doctors.AsNoTracking()
            .FirstOrDefaultAsync(d => d.Id == doctorId && d.IsActive, cancellationToken);

        if (doctor == null)
            return null;

        return await GetCachedAsync(doctor, cancellationToken);
    }

    public Task<GoogleReviewLookupResult?> GetCachedAsync(Doctor doctor, CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        if (TryLoadFile(doctor, out var cached) && cached.Found && cached.Reviews.Count > 0)
            return Task.FromResult<GoogleReviewLookupResult?>(cached);

        return Task.FromResult<GoogleReviewLookupResult?>(null);
    }

    public async Task<GoogleReviewLookupResult?> LookupAsync(Doctor doctor, CancellationToken cancellationToken = default)
    {
        if (TryLoadFreshCache(doctor, out var cached))
        {
            _logger.LogInformation(
                "Using cached Google reviews for doctor {DoctorId} (fetched {FetchedAt})",
                doctor.Id, doctor.GoogleReviewsFetchedAt!.Value.ToString("u"));
            return cached;
        }

        if (string.IsNullOrWhiteSpace(_options.ApiKey) || string.IsNullOrWhiteSpace(_options.Model))
            return FallbackFromDoctor(doctor);

        var fetched = await FetchFromClaudeAsync(doctor, cancellationToken);
        var fetchedAt = DateTime.UtcNow;

        // API hard failure (null): keep any prior positive cache if present; otherwise write empty file
        // so we do not keep calling Claude until the cache window expires.
        if (fetched == null)
        {
            if (TryLoadFile(doctor, out var stale) && stale.Found)
            {
                _logger.LogWarning(
                    "Live Google review fetch failed for doctor {DoctorId}; returning stale file cache.",
                    doctor.Id);
                return new GoogleReviewLookupResult
                {
                    Found = stale.Found,
                    GoogleRating = stale.GoogleRating,
                    GoogleReviewCount = stale.GoogleReviewCount,
                    SummaryOfReviews = stale.SummaryOfReviews,
                    Reviews = stale.Reviews,
                    FromCache = true,
                    FetchedAt = stale.FetchedAt
                };
            }

            var emptyMiss = new GoogleReviewLookupResult
            {
                Found = false,
                GoogleRating = doctor.GoogleRating,
                GoogleReviewCount = doctor.GoogleReviewCount,
                SummaryOfReviews = doctor.SummaryOfReviews,
                Reviews = Array.Empty<PublicDoctorReviewDto>(),
                FromCache = false,
                FetchedAt = fetchedAt
            };
            await PersistAsync(doctor.Id, emptyMiss, fetchedAt, cancellationToken);
            _logger.LogInformation(
                "No Google reviews retrieved for doctor {DoctorId}; wrote empty cache file until {Until:u}.",
                doctor.Id, fetchedAt.AddDays(Math.Max(1, _options.GoogleReviewCacheDays)));
            return emptyMiss;
        }

        var withMeta = new GoogleReviewLookupResult
        {
            Found = fetched.Found,
            GoogleRating = fetched.Found && fetched.GoogleRating > 0 ? fetched.GoogleRating : doctor.GoogleRating,
            GoogleReviewCount = fetched.Found && fetched.GoogleReviewCount > 0 ? fetched.GoogleReviewCount : doctor.GoogleReviewCount,
            SummaryOfReviews = fetched.Found && !string.IsNullOrWhiteSpace(fetched.SummaryOfReviews)
                ? fetched.SummaryOfReviews
                : doctor.SummaryOfReviews,
            Reviews = fetched.Found ? fetched.Reviews : Array.Empty<PublicDoctorReviewDto>(),
            FromCache = false,
            FetchedAt = fetchedAt
        };

        // Always persist — including found=false / empty reviews — so the next click uses the file cache.
        await PersistAsync(doctor.Id, withMeta, fetchedAt, cancellationToken);
        if (!withMeta.Found)
        {
            _logger.LogInformation(
                "Google reviews not found for doctor {DoctorId}; wrote empty cache file until {Until:u}.",
                doctor.Id, fetchedAt.AddDays(Math.Max(1, _options.GoogleReviewCacheDays)));
        }

        return withMeta;
    }

    private bool TryLoadFreshCache(Doctor doctor, out GoogleReviewLookupResult result)
    {
        result = null!;
        if (!doctor.GoogleReviewsFetchedAt.HasValue)
            return false;

        var cacheDays = Math.Max(1, _options.GoogleReviewCacheDays);
        var age = DateTime.UtcNow - doctor.GoogleReviewsFetchedAt.Value.ToUniversalTime();
        if (age > TimeSpan.FromDays(cacheDays))
            return false;

        return TryLoadFile(doctor, out result);
    }

    private bool TryLoadFile(Doctor doctor, out GoogleReviewLookupResult result)
    {
        result = null!;
        var path = ResolveReviewFilePath(doctor);
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return false;

        try
        {
            var json = File.ReadAllText(path);
            var file = JsonSerializer.Deserialize<GoogleReviewFileDto>(json, JsonOptions);
            if (file == null)
                return false;

            var reviews = (file.Reviews ?? [])
                .Where(r => !string.IsNullOrWhiteSpace(r.ReviewText))
                .Select(r => new PublicDoctorReviewDto
                {
                    ReviewerName = string.IsNullOrWhiteSpace(r.ReviewerName) ? "Google reviewer" : r.ReviewerName.Trim(),
                    Rating = Math.Clamp(r.Rating, 0, 5),
                    ReviewText = r.ReviewText!.Trim(),
                    Recommendation = string.IsNullOrWhiteSpace(r.Recommendation)
                        ? "Google review"
                        : r.Recommendation.Trim(),
                    Source = "Google"
                })
                .ToList();

            result = new GoogleReviewLookupResult
            {
                Found = file.Found || file.GoogleRating > 0 || reviews.Count > 0,
                GoogleRating = file.GoogleRating > 0 ? file.GoogleRating : doctor.GoogleRating,
                GoogleReviewCount = file.GoogleReviewCount > 0 ? file.GoogleReviewCount : doctor.GoogleReviewCount,
                SummaryOfReviews = !string.IsNullOrWhiteSpace(file.SummaryOfReviews)
                    ? file.SummaryOfReviews
                    : doctor.SummaryOfReviews,
                Reviews = reviews,
                FromCache = true,
                FetchedAt = file.FetchedAt ?? doctor.GoogleReviewsFetchedAt
            };
            // Empty / not-found files are still valid cache hits (prevents re-fetching).
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Could not read Google review file for doctor {DoctorId}: {Message}",
                doctor.Id, ex.Message);
            return false;
        }
    }

    private async Task<GoogleReviewLookupResult?> FetchFromClaudeAsync(Doctor doctor, CancellationToken cancellationToken)
    {
        var addressLine = BuildAddressLine(doctor);
        var searchHint = string.IsNullOrWhiteSpace(addressLine)
            ? $"{doctor.Name} {doctor.City} {doctor.State} dentist Google reviews"
            : $"{doctor.Name} {addressLine} Google reviews rating";

        var systemPrompt = """
            You look up real Google reviews for a specific dental or medical practice using web search
            (Google Maps, Google Business Profile, practice listing pages).

            Rules:
            - Identify the correct practice using the exact doctor/practice name and address provided.
            - Prefer Google Maps / Google Business Profile ratings and reviews.
            - Do not invent reviews, ratings, or review counts. If you cannot verify, return found=false.
            - Include up to 5 recent or highly relevant review snippets with star ratings when available.
            - Respond with ONLY a JSON object (no markdown prose outside JSON):
              {
                "found": true,
                "googleRating": 4.8,
                "googleReviewCount": 214,
                "summaryOfReviews": "1-2 sentence summary of what patients say",
                "reviews": [
                  {
                    "reviewerName": "First name or initials",
                    "rating": 5,
                    "reviewText": "Short review snippet",
                    "relativeTime": "2 months ago"
                  }
                ]
              }
            """;

        var userPrompt = $"""
            Look up Google reviews for this doctor/practice:
            Name: {doctor.Name}
            Practice: {doctor.PracticeName ?? "(unknown)"}
            Specialty: {doctor.Specialty}
            Address: {(string.IsNullOrWhiteSpace(addressLine) ? "(not on file)" : addressLine)}
            Phone: {doctor.OfficePhoneNumber ?? "(unknown)"}

            Search query hint: {searchHint}
            Return verified Google rating, review count, a short summary, and up to 5 review snippets.
            """;

        try
        {
            var payload = AnthropicApiHelper.BuildPayload(
                _options,
                maxTokens: 2000,
                system: systemPrompt,
                messages: new[] { new { role = "user", content = userPrompt } },
                includeWebSearch: true,
                webSearchMaxUses: 3);

            using var httpRequest = AnthropicApiHelper.CreateMessageRequest(_options, payload);
            var response = await _httpClient.SendAsync(httpRequest, cancellationToken);
            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Claude Google review lookup failed for doctor {DoctorId}: {Body}",
                    doctor.Id, responseBody);
                return null;
            }

            var text = AnthropicApiHelper.ExtractTextContent(responseBody);
            var json = ExtractJsonObject(text);
            if (string.IsNullOrWhiteSpace(json))
            {
                _logger.LogWarning("Claude Google review lookup returned no JSON for doctor {DoctorId}", doctor.Id);
                return null;
            }

            var record = JsonSerializer.Deserialize<ClaudeReviewPayload>(json, JsonOptions);
            if (record == null || !record.Found)
                return new GoogleReviewLookupResult { Found = false };

            var reviews = (record.Reviews ?? [])
                .Where(r => !string.IsNullOrWhiteSpace(r.ReviewText))
                .Take(5)
                .Select(r => new PublicDoctorReviewDto
                {
                    ReviewerName = string.IsNullOrWhiteSpace(r.ReviewerName) ? "Google reviewer" : r.ReviewerName.Trim(),
                    Rating = Math.Clamp(r.Rating ?? 0, 0, 5),
                    ReviewText = r.ReviewText!.Trim(),
                    Recommendation = string.IsNullOrWhiteSpace(r.RelativeTime) ? "Google review" : $"Google · {r.RelativeTime.Trim()}",
                    Source = "Google"
                })
                .ToList();

            return new GoogleReviewLookupResult
            {
                Found = true,
                GoogleRating = ClampRating(record.GoogleRating),
                GoogleReviewCount = Math.Max(0, record.GoogleReviewCount ?? 0),
                SummaryOfReviews = Truncate(record.SummaryOfReviews, 4000),
                Reviews = reviews
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error looking up Google reviews via Claude for doctor {DoctorId}", doctor.Id);
            return null;
        }
    }

    private async Task PersistAsync(
        int doctorId,
        GoogleReviewLookupResult result,
        DateTime fetchedAtUtc,
        CancellationToken cancellationToken)
    {
        try
        {
            var relativePath = $"{doctorId}/{ReviewFileName}";
            var absolutePath = GetAbsoluteReviewPath(relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(absolutePath)!);

            var fileDto = new GoogleReviewFileDto
            {
                DoctorId = doctorId,
                Found = result.Found,
                FetchedAt = fetchedAtUtc,
                GoogleRating = result.GoogleRating,
                GoogleReviewCount = result.GoogleReviewCount,
                SummaryOfReviews = result.SummaryOfReviews,
                Reviews = result.Reviews.Select(r => new GoogleReviewFileSnippet
                {
                    ReviewerName = r.ReviewerName,
                    Rating = r.Rating,
                    ReviewText = r.ReviewText,
                    Recommendation = r.Recommendation
                }).ToList()
            };

            await File.WriteAllTextAsync(
                absolutePath,
                JsonSerializer.Serialize(fileDto, JsonOptions),
                cancellationToken);

            var doctor = await _db.Doctors.FirstOrDefaultAsync(d => d.Id == doctorId, cancellationToken);
            if (doctor == null)
                return;

            if (result.GoogleRating > 0)
                doctor.GoogleRating = result.GoogleRating;
            if (result.GoogleReviewCount > 0)
                doctor.GoogleReviewCount = result.GoogleReviewCount;
            if (!string.IsNullOrWhiteSpace(result.SummaryOfReviews))
                doctor.SummaryOfReviews = result.SummaryOfReviews;

            doctor.GoogleReviewsFetchedAt = fetchedAtUtc;
            doctor.GoogleReviewsFilePath = relativePath;

            await _db.SaveChangesAsync(cancellationToken);
            await _qualityScore.RecomputeAndPersistAsync(doctorId, cancellationToken);
            _logger.LogInformation(
                "Saved Google reviews for doctor {DoctorId} to {Path} (rating {Rating}, count {Count})",
                doctorId, relativePath, doctor.GoogleRating, doctor.GoogleReviewCount);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Could not persist Google review lookup for doctor {DoctorId}: {Message}",
                doctorId, ex.Message);
            _db.ChangeTracker.Clear();
        }
    }

    private string? ResolveReviewFilePath(Doctor doctor)
    {
        if (!string.IsNullOrWhiteSpace(doctor.GoogleReviewsFilePath))
            return GetAbsoluteReviewPath(doctor.GoogleReviewsFilePath);

        if (string.IsNullOrWhiteSpace(_uploadOptions.DoctorsPhysicalPath))
            return null;

        var fallback = Path.Combine(_uploadOptions.DoctorsPhysicalPath, doctor.Id.ToString(), ReviewFileName);
        return File.Exists(fallback) ? fallback : null;
    }

    private string GetAbsoluteReviewPath(string relativeOrAbsolute)
    {
        if (Path.IsPathRooted(relativeOrAbsolute))
            return relativeOrAbsolute;

        var root = _uploadOptions.DoctorsPhysicalPath;
        if (string.IsNullOrWhiteSpace(root))
            throw new InvalidOperationException("UploadOptions.DoctorsPhysicalPath is not configured.");

        return Path.Combine(root, relativeOrAbsolute.Replace('/', Path.DirectorySeparatorChar));
    }

    private static GoogleReviewLookupResult FallbackFromDoctor(Doctor doctor) => new()
    {
        Found = doctor.GoogleRating > 0 || doctor.GoogleReviewCount > 0 || !string.IsNullOrWhiteSpace(doctor.SummaryOfReviews),
        GoogleRating = doctor.GoogleRating,
        GoogleReviewCount = doctor.GoogleReviewCount,
        SummaryOfReviews = doctor.SummaryOfReviews,
        Reviews = Array.Empty<PublicDoctorReviewDto>(),
        FromCache = true,
        FetchedAt = doctor.GoogleReviewsFetchedAt
    };

    private static string BuildAddressLine(Doctor doctor)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(doctor.Address))
            parts.Add(doctor.Address.Trim());
        if (!string.IsNullOrWhiteSpace(doctor.City))
            parts.Add(doctor.City.Trim());
        if (!string.IsNullOrWhiteSpace(doctor.State) && !doctor.State.Equals("NA", StringComparison.OrdinalIgnoreCase))
            parts.Add(doctor.State.Trim());
        if (!string.IsNullOrWhiteSpace(doctor.ZipCode) && doctor.ZipCode != "00000")
            parts.Add(doctor.ZipCode.Trim());
        return string.Join(", ", parts);
    }

    private static string? ExtractJsonObject(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var fence = JsonObjectFenceRegex.Match(text);
        if (fence.Success)
            return fence.Groups[1].Value.Trim();

        var loose = JsonObjectLooseRegex.Match(text);
        if (loose.Success)
            return loose.Value.Trim();

        var trimmed = text.Trim();
        if (trimmed.StartsWith('{') && trimmed.EndsWith('}'))
            return trimmed;

        return null;
    }

    private static decimal ClampRating(decimal? rating)
    {
        if (!rating.HasValue || rating.Value <= 0)
            return 0;
        return Math.Clamp(rating.Value, 0, 5);
    }

    private static string? Truncate(string? value, int max)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var trimmed = value.Trim();
        return trimmed.Length <= max ? trimmed : trimmed[..max];
    }

    private sealed class GoogleReviewFileDto
    {
        public int DoctorId { get; set; }
        public bool Found { get; set; }
        public DateTime? FetchedAt { get; set; }
        public decimal GoogleRating { get; set; }
        public int GoogleReviewCount { get; set; }
        public string? SummaryOfReviews { get; set; }
        public List<GoogleReviewFileSnippet>? Reviews { get; set; }
    }

    private sealed class GoogleReviewFileSnippet
    {
        public string ReviewerName { get; set; } = string.Empty;
        public int Rating { get; set; }
        public string? ReviewText { get; set; }
        public string? Recommendation { get; set; }
    }

    private sealed class ClaudeReviewPayload
    {
        public bool Found { get; set; }
        public decimal? GoogleRating { get; set; }
        public int? GoogleReviewCount { get; set; }
        public string? SummaryOfReviews { get; set; }
        public List<ClaudeReviewSnippet>? Reviews { get; set; }
    }

    private sealed class ClaudeReviewSnippet
    {
        public string? ReviewerName { get; set; }
        public int? Rating { get; set; }
        public string? ReviewText { get; set; }
        public string? RelativeTime { get; set; }
    }
}
