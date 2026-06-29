using Docovee.BLL.Models;
using Docovee.DS;
using Docovee.DS.Enums;
using Docovee.logging;
using Microsoft.EntityFrameworkCore;

namespace Docovee.BLL.Services;

public interface IDoctorSearchService
{
    Task<IReadOnlyList<DoctorDto>> SearchAsync(DoctorSearchRequest request, CancellationToken cancellationToken = default);
}

public class DoctorSearchService : IDoctorSearchService
{
    private readonly DocoveeDbContext _db;
    private readonly IDocoveeLogger _logger;
    private readonly IAppSettingsService _appSettings;
    private readonly IAnthropicMatchingService _matchingService;

    public DoctorSearchService(
        DocoveeDbContext db,
        IDocoveeLogger logger,
        IAppSettingsService appSettings,
        IAnthropicMatchingService matchingService)
    {
        _db = db;
        _logger = logger;
        _appSettings = appSettings;
        _matchingService = matchingService;
    }

    public async Task<IReadOnlyList<DoctorDto>> SearchAsync(DoctorSearchRequest request, CancellationToken cancellationToken = default)
    {
        var session = await _db.SearchSessions
            .FirstOrDefaultAsync(s => s.SessionKey == request.SessionKey, cancellationToken);

        if (session == null)
        {
            _logger.LogWarning("Search session not found: {SessionKey}", request.SessionKey);
            return Array.Empty<DoctorDto>();
        }

        session.Location = request.Location;
        session.Latitude = request.Latitude;
        session.Longitude = request.Longitude;
        session.InsurancePlanText = string.IsNullOrWhiteSpace(request.InsurancePlan)
            ? null
            : request.InsurancePlan.Trim();
        session.InsuranceCarrierId = request.InsuranceCarrierId;
        session.GenderPreference = GenderPreference.NoPreference;
        session.CommunicationStyle = request.CommunicationStyle;
        session.AvailabilityPreference = request.AvailabilityPreference;
        session.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);

        var specialty = session.Specialty ?? "Family Medicine";

        var query = _db.Doctors
            .AsNoTracking()
            .Include(d => d.DoctorInsurances)
            .ThenInclude(di => di.InsuranceCarrier)
            .Include(d => d.PatientReviews)
            .Where(d => d.IsActive);

        // Do not hard-filter by insurance — rank doctors with matching plans higher instead.
        // Gender preference disabled for prototype — imported doctors may lack gender data.

        var doctors = await query.ToListAsync(cancellationToken);
        var filtered = doctors.Where(d => MatchesSpecialty(d.SpecialtyCategory, specialty) || MatchesSpecialty(d.Specialty, specialty)).ToList();

        if (filtered.Count == 0)
        {
            _logger.LogWarning("No doctors matched specialty {Specialty}. Returning empty result set.", specialty);
            return Array.Empty<DoctorDto>();
        }

        var locationQuery = NormalizeLocationInput(request.Location);
        if (!string.IsNullOrWhiteSpace(locationQuery))
        {
            var cityMatch = filtered.Where(d => LocationMatches(locationQuery, d)).ToList();
            if (cityMatch.Count > 0)
                filtered = cityMatch;
        }

        var rankings = await _matchingService.RankDoctorsAsync(session, filtered, cancellationToken);
        var rankingMap = rankings.ToDictionary(r => r.DoctorId, r => r);

        var userInsurance = session.InsurancePlanText;
        var resultCount = await _appSettings.GetDoctorSearchResultCountAsync(cancellationToken);

        var results = filtered
            .Select(d =>
            {
                rankingMap.TryGetValue(d.Id, out var rank);
                var baseScore = rank.DoctorId == d.Id && rank.MatchScore > 0
                    ? rank.MatchScore
                    : CalculateMatchScore(d, CalculateDistanceMiles(request.Latitude, request.Longitude, d.Latitude, d.Longitude));

                var insuranceBoost = InsuranceMatchHelper.InsuranceRankBoost(userInsurance, d);
                var score = Math.Min(baseScore + insuranceBoost, 99);

                var reason = rank.DoctorId == d.Id ? rank.Reason : null;
                if (insuranceBoost > 0)
                {
                    var insuranceNote = $"Accepts your insurance ({userInsurance})";
                    reason = string.IsNullOrWhiteSpace(reason) ? insuranceNote : $"{reason}; {insuranceNote}";
                }

                return MapDoctor(d, request.Latitude, request.Longitude, score, reason);
            })
            .OrderByDescending(d => d.MatchScore)
            .ThenByDescending(d => d.GoogleRating)
            .Take(resultCount)
            .ToList();

        if (results.Count > 0)
            results[0].Recommended = true;

        _logger.LogInformation("Doctor search returned {Count} results for session {SessionKey}", results.Count, request.SessionKey);
        return results;
    }

    private static DoctorDto MapDoctor(
        DS.Entities.Doctor doctor,
        double? lat,
        double? lng,
        int matchScore,
        string? matchReason)
    {
        var distance = CalculateDistanceMiles(lat, lng, doctor.Latitude, doctor.Longitude);
        var location = distance.HasValue
            ? $"{doctor.City}, {doctor.State} · {distance.Value:0.#} mi"
            : doctor.Location ?? $"{doctor.City}, {doctor.State}";

        var patientReviews = doctor.PatientReviews.ToList();
        var patientAvg = patientReviews.Count > 0
            ? (decimal?)patientReviews.Average(r => r.Rating)
            : null;

        return new DoctorDto
        {
            Id = doctor.Id,
            Name = doctor.Name,
            Specialty = doctor.Specialty,
            PracticeName = doctor.PracticeName,
            Location = location,
            PhotoUrl = DoctorPhotoHelper.GetDisplayPhotoUrl(doctor.PhotoUrl, doctor.GmbPhotoLink),
            AvatarInitials = doctor.AvatarInitials,
            MatchScore = matchScore > 0 ? matchScore : CalculateMatchScore(doctor, distance),
            GoogleRating = doctor.GoogleRating,
            GoogleReviewCount = doctor.GoogleReviewCount,
            Tag = doctor.TagLine ?? doctor.Niche ?? doctor.SpecialtyCategory,
            MatchReason = matchReason,
            DistanceMiles = distance,
            Niche = doctor.Niche,
            Top3Procedures = doctor.Top3Procedures,
            SummaryOfReviews = doctor.SummaryOfReviews,
            PatientReviewAverage = patientAvg,
            PatientReviewCount = patientReviews.Count,
            OfficePhoneNumber = doctor.OfficePhoneNumber,
            YearsOfPractice = doctor.YearsOfPractice
        };
    }

    private static int CalculateMatchScore(DS.Entities.Doctor doctor, double? distanceMiles)
    {
        var score = 70;
        score += (int)((double)doctor.GoogleRating * 2);
        if (distanceMiles.HasValue && distanceMiles.Value <= 5)
            score += 10;
        else if (distanceMiles.HasValue && distanceMiles.Value <= 15)
            score += 5;
        return Math.Min(score, 99);
    }

    private static double? CalculateDistanceMiles(double? lat1, double? lon1, double? lat2, double? lon2)
    {
        if (!lat1.HasValue || !lon1.HasValue || !lat2.HasValue || !lon2.HasValue)
            return null;

        const double R = 3958.8;
        var dLat = DegreesToRadians(lat2.Value - lat1.Value);
        var dLon = DegreesToRadians(lon2.Value - lon1.Value);
        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                Math.Cos(DegreesToRadians(lat1.Value)) * Math.Cos(DegreesToRadians(lat2.Value)) *
                Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        var c = 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
        return R * c;
    }

    private static double DegreesToRadians(double degrees) => degrees * Math.PI / 180.0;

    private static string NormalizeLocationInput(string location)
    {
        var lower = location.ToLowerInvariant().Trim();
        foreach (var (typo, fix) in LocationTypoCorrections)
            lower = lower.Replace(typo, fix, StringComparison.Ordinal);
        return lower;
    }

    private static bool LocationMatches(string locationQuery, DS.Entities.Doctor doctor)
    {
        var city = doctor.City.ToLowerInvariant();
        var token = locationQuery.Split(',')[0].Trim();

        if (locationQuery.Contains(city) || city.Contains(token))
            return true;

        if (doctor.Location != null && doctor.Location.ToLowerInvariant().Contains(token))
            return true;

        if (!string.IsNullOrWhiteSpace(doctor.State)
            && locationQuery.Contains(doctor.State.ToLowerInvariant())
            && FuzzyCityMatch(token, city))
            return true;

        return FuzzyCityMatch(token, city);
    }

    private static bool FuzzyCityMatch(string a, string b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b))
            return false;
        if (a == b || a.StartsWith(b, StringComparison.Ordinal) || b.StartsWith(a, StringComparison.Ordinal))
            return true;
        if (a.Length < 4 || b.Length < 4)
            return false;
        return LevenshteinDistance(a, b) <= 2;
    }

    private static int LevenshteinDistance(string a, string b)
    {
        var d = new int[a.Length + 1, b.Length + 1];
        for (var i = 0; i <= a.Length; i++) d[i, 0] = i;
        for (var j = 0; j <= b.Length; j++) d[0, j] = j;
        for (var i = 1; i <= a.Length; i++)
        {
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + cost);
            }
        }
        return d[a.Length, b.Length];
    }

    private static readonly (string Typo, string Fix)[] LocationTypoCorrections =
    [
        ("phonix", "phoenix"),
        ("pheonix", "phoenix"),
        ("los angelas", "los angeles"),
        ("seatle", "seattle"),
    ];

    private static GenderPreference ParseGenderPreference(string? value) =>
        value?.ToLowerInvariant() switch
        {
            "male" => GenderPreference.Male,
            "female" => GenderPreference.Female,
            _ => GenderPreference.NoPreference
        };

    private static bool MatchesSpecialty(string category, string specialty)
    {
        var cat = category.ToLowerInvariant();
        var spec = specialty.ToLowerInvariant();

        if (cat == spec || cat.Contains(spec) || spec.Contains(cat))
            return true;

        return spec switch
        {
            var s when s.Contains("dentist") || s.Contains("dental") || s.Contains("oral") =>
                cat.Contains("dentist") || cat.Contains("dental") || cat.Contains("oral"),
            var s when s.Contains("family") =>
                cat.Contains("family medicine"),
            var s when s.Contains("internal") =>
                cat.Contains("internal medicine"),
            var s when s.Contains("orthopedic") || s.Contains("ortho") =>
                cat.Contains("orthopedic"),
            var s when s.Contains("dermat") =>
                cat.Contains("dermat"),
            var s when s.Contains("cardio") =>
                cat.Contains("cardio"),
            var s when s.Contains("psych") || s.Contains("mental") =>
                cat.Contains("psych"),
            var s when s.Contains("neuro") =>
                cat.Contains("neuro"),
            var s when s.Contains("pediatric") =>
                cat.Contains("pediatric"),
            var s when s.Contains("urgent") =>
                cat.Contains("urgent") || cat.Contains("family"),
            _ => cat.Split(' ')[0] == spec.Split(' ')[0]
        };
    }
}
