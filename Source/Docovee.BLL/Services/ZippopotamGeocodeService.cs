using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Docovee.logging;

namespace Docovee.BLL.Services;

public readonly record struct ZipGeoCoordinate(double Latitude, double Longitude);

public interface IZipGeocodeService
{
    Task<ZipGeoCoordinate?> TryGeocodeUsZipAsync(string zip, CancellationToken cancellationToken = default);
}

/// <summary>Resolves US ZIP codes to lat/lng via Zippopotam.us.</summary>
public sealed class ZippopotamGeocodeService : IZipGeocodeService
{
    private static readonly Regex UsZipRegex = new(@"^\d{5}$", RegexOptions.Compiled);
    private static readonly ConcurrentDictionary<string, ZipGeoCoordinate> Cache = new(StringComparer.Ordinal);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _httpClient;
    private readonly IDocoveeLogger _logger;

    public ZippopotamGeocodeService(HttpClient httpClient, IDocoveeLogger logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    public async Task<ZipGeoCoordinate?> TryGeocodeUsZipAsync(string zip, CancellationToken cancellationToken = default)
    {
        var normalized = NormalizeUsZip(zip);
        if (normalized == null)
            return null;

        if (Cache.TryGetValue(normalized, out var cached))
            return cached;

        try
        {
            using var response = await _httpClient.GetAsync($"us/{normalized}", cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogInformation("Zippopotam lookup failed for {Zip}: {Status}", normalized, (int)response.StatusCode);
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var parsed = await JsonSerializer.DeserializeAsync<ZippopotamResponse>(stream, JsonOptions, cancellationToken);
            var place = parsed?.Places?.FirstOrDefault();
            if (place == null)
                return null;

            if (!TryParseCoordinate(place.Latitude, out var lat)
                || !TryParseCoordinate(place.Longitude, out var lng))
                return null;

            var coords = new ZipGeoCoordinate(lat, lng);
            Cache[normalized] = coords;
            return coords;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or IOException)
        {
            _logger.LogWarning("Zippopotam lookup error for {Zip}: {Message}", normalized, ex.Message);
            return null;
        }
    }

    public static string? NormalizeUsZip(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var match = Regex.Match(value, @"\b(\d{5})(?:-\d{4})?\b");
        if (!match.Success)
            return null;

        var zip = match.Groups[1].Value;
        if (zip == "00000" || !UsZipRegex.IsMatch(zip))
            return null;

        return zip;
    }

    private static bool TryParseCoordinate(string? value, out double coordinate)
    {
        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out coordinate)
            && !double.IsNaN(coordinate)
            && !double.IsInfinity(coordinate);
    }

    private sealed class ZippopotamResponse
    {
        [JsonPropertyName("places")]
        public List<ZippopotamPlace>? Places { get; set; }
    }

    private sealed class ZippopotamPlace
    {
        [JsonPropertyName("latitude")]
        public string? Latitude { get; set; }

        [JsonPropertyName("longitude")]
        public string? Longitude { get; set; }
    }
}
