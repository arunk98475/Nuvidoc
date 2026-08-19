using Docovee.BLL.Data;
using Docovee.DS.Models;
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
        session.Latitude = request.Latitude ?? session.Latitude;
        session.Longitude = request.Longitude ?? session.Longitude;
        session.InsurancePlanText = string.IsNullOrWhiteSpace(request.InsurancePlan)
            ? null
            : request.InsurancePlan.Trim();
        session.InsuranceCarrierId = request.InsuranceCarrierId;
        session.GenderPreference = GenderPreference.NoPreference;
        session.CommunicationStyle = request.CommunicationStyle;
        session.AvailabilityPreference = request.AvailabilityPreference;
        session.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);

        var specialty = session.Specialty ?? "General Dentist";
        var resultCount = await _appSettings.GetDoctorSearchResultCountAsync(cancellationToken);
        var locationQuery = NormalizeLocationInput(request.Location);
        var hasLocation = !string.IsNullOrWhiteSpace(locationQuery);

        // Match only against doctors already in the database (no web discovery).
        var filtered = await LoadSpecialtyMatchesAsync(specialty, cancellationToken);

        if (filtered.Count == 0)
        {
            _logger.LogWarning("No doctors in DB matched specialty {Specialty}. Returning empty result set.", specialty);
            return Array.Empty<DoctorDto>();
        }

        if (hasLocation)
        {
            var cityMatch = filtered.Where(d => LocationMatches(locationQuery, d)).ToList();
            if (cityMatch.Count > 0)
            {
                filtered = cityMatch;
            }
            else
            {
                // Never fall back to other cities when the patient gave a location —
                // that was returning Atlanta/Chicago for Houston ZIPs.
                _logger.LogWarning(
                    "No DB doctors matched location {Location} for specialty {Specialty}. Returning empty.",
                    request.Location, specialty);
                return Array.Empty<DoctorDto>();
            }
        }

        if (!string.IsNullOrWhiteSpace(request.PreferredLanguage))
        {
            var preferredLanguage = request.PreferredLanguage.Trim();
            var languageMatch = filtered.Where(d =>
                d.DoctorLanguages.Any(dl =>
                    dl.DoctorLanguage.Name.Equals(preferredLanguage, StringComparison.OrdinalIgnoreCase))).ToList();
            if (languageMatch.Count > 0)
                filtered = languageMatch;
        }

        if (!string.IsNullOrWhiteSpace(request.AdditionalPreference))
        {
            session.SearchNotes = (session.SearchNotes ?? "") + $" Additional matching preference: {request.AdditionalPreference.Trim()}.";
            await _db.SaveChangesAsync(cancellationToken);
        }

        // Link by phone (last 10 digits) so sponsored DB listings win over duplicates.
        filtered = await LinkDoctorsByPhoneAsync(filtered, cancellationToken);

        var rankings = await _matchingService.RankDoctorsAsync(session, filtered, cancellationToken);
        var rankingMap = rankings.ToDictionary(r => r.DoctorId, r => r);

        var userInsurance = session.InsurancePlanText;
        var pollingAnswers = SearchContextHelper.Load(session).PollingAnswers;
        var originLat = request.Latitude ?? session.Latitude;
        var originLng = request.Longitude ?? session.Longitude;

        var results = filtered
            .Select(d =>
            {
                rankingMap.TryGetValue(d.Id, out var rank);
                var distance = CalculateDistanceMiles(originLat, originLng, d.Latitude, d.Longitude);
                var baseScore = rank.DoctorId == d.Id && rank.MatchScore > 0
                    ? rank.MatchScore
                    : CalculateMatchScore(d, distance);

                var insuranceBoost = InsuranceMatchHelper.InsuranceRankBoost(userInsurance, d);
                var preferenceBoost = MatchWeightHelper.ComputeDoctorPreferenceBoost(d, pollingAnswers, distance);
                var score = Math.Min(baseScore + insuranceBoost + preferenceBoost, 99);

                var reason = rank.DoctorId == d.Id ? rank.Reason : null;
                if (insuranceBoost > 0)
                {
                    var insuranceNote = $"Accepts your insurance ({userInsurance})";
                    reason = string.IsNullOrWhiteSpace(reason) ? insuranceNote : $"{reason}; {insuranceNote}";
                }
                if (preferenceBoost > 0)
                {
                    var preferenceNote = "Strong fit for your weighted preferences";
                    reason = string.IsNullOrWhiteSpace(reason) ? preferenceNote : $"{reason}; {preferenceNote}";
                }
                if (d.IsSponsored)
                {
                    var sponsoredNote = "Sponsored";
                    reason = string.IsNullOrWhiteSpace(reason) ? sponsoredNote : $"{reason}; {sponsoredNote}";
                }

                return MapDoctor(d, originLat, originLng, score, reason);
            })
            .OrderByDescending(d => d.IsSponsored)
            .ThenByDescending(d => d.MatchScore)
            .ThenBy(d => d.DistanceMiles ?? double.MaxValue)
            .ThenByDescending(d => d.GoogleRating)
            .Take(resultCount)
            .ToList();

        if (results.Count > 0)
            results[0].Recommended = true;

        _logger.LogInformation("Doctor search returned {Count} results for session {SessionKey}", results.Count, request.SessionKey);
        return results;
    }

    /// <summary>
    /// Before listing, replace each candidate with the best existing DB doctor that shares
    /// the same phone (last 10 digits), preferring sponsored accounts. Dedupes by phone.
    /// </summary>
    private async Task<List<DS.Entities.Doctor>> LinkDoctorsByPhoneAsync(
        List<DS.Entities.Doctor> candidates,
        CancellationToken cancellationToken)
    {
        if (candidates.Count == 0)
            return candidates;

        var allActive = await _db.Doctors
            .AsNoTracking()
            .Include(d => d.DoctorInsurances)
            .ThenInclude(di => di.InsuranceCarrier)
            .Include(d => d.DoctorLanguages)
            .ThenInclude(dl => dl.DoctorLanguage)
            .Include(d => d.PatientReviews)
            .Include(d => d.Locations)
            .Where(d => d.IsActive)
            .ToListAsync(cancellationToken);

        var byPhone = new Dictionary<string, DS.Entities.Doctor>(StringComparer.Ordinal);
        foreach (var doctor in allActive)
        {
            foreach (var phoneKey in GetDoctorPhoneKeys(doctor))
            {
                if (!byPhone.TryGetValue(phoneKey, out var existing)
                    || PreferDoctor(doctor, existing))
                {
                    byPhone[phoneKey] = doctor;
                }
            }
        }

        var linked = new List<DS.Entities.Doctor>();
        var seenIds = new HashSet<int>();
        var seenPhones = new HashSet<string>(StringComparer.Ordinal);

        foreach (var candidate in candidates)
        {
            var phoneKeys = GetDoctorPhoneKeys(candidate).ToList();
            DS.Entities.Doctor? preferred = null;
            string? matchedPhone = null;

            foreach (var key in phoneKeys)
            {
                if (byPhone.TryGetValue(key, out var match))
                {
                    if (preferred == null || PreferDoctor(match, preferred))
                    {
                        preferred = match;
                        matchedPhone = key;
                    }
                }
            }

            var resolved = preferred ?? candidate;

            if (matchedPhone != null && !seenPhones.Add(matchedPhone))
                continue;
            if (!seenIds.Add(resolved.Id))
                continue;

            linked.Add(resolved);
        }

        // Ensure every sponsored doctor in the same specialty/area phones still appear if
        // they were displaced — already covered by replacement. Sort sponsored first for ranker input.
        return linked
            .OrderByDescending(d => d.IsSponsored)
            .ThenByDescending(d => d.GoogleRating)
            .ToList();
    }

    private static IEnumerable<string> GetDoctorPhoneKeys(DS.Entities.Doctor doctor)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        var office = PhoneNumberHelper.NormalizeLast10(doctor.OfficePhoneNumber);
        if (office != null)
            keys.Add(office);

        if (doctor.Locations != null)
        {
            foreach (var loc in doctor.Locations)
            {
                var locPhone = PhoneNumberHelper.NormalizeLast10(loc.PhoneNumber);
                if (locPhone != null)
                    keys.Add(locPhone);
            }
        }

        return keys;
    }

    /// <summary>Prefer sponsored, registered, real ZIP/state, then higher rating / newer id.</summary>
    private static bool PreferDoctor(DS.Entities.Doctor candidate, DS.Entities.Doctor current)
    {
        if (candidate.IsSponsored != current.IsSponsored)
            return candidate.IsSponsored;

        var candidateRegistered = !string.IsNullOrWhiteSpace(candidate.Username);
        var currentRegistered = !string.IsNullOrWhiteSpace(current.Username);
        if (candidateRegistered != currentRegistered)
            return candidateRegistered;

        var candidateZipQuality = HasRealZip(candidate);
        var currentZipQuality = HasRealZip(current);
        if (candidateZipQuality != currentZipQuality)
            return candidateZipQuality;

        var candidateStateQuality = HasRealState(candidate);
        var currentStateQuality = HasRealState(current);
        if (candidateStateQuality != currentStateQuality)
            return candidateStateQuality;

        var candidatePhoneQuality = HasCleanUsPhone(candidate.OfficePhoneNumber);
        var currentPhoneQuality = HasCleanUsPhone(current.OfficePhoneNumber);
        if (candidatePhoneQuality != currentPhoneQuality)
            return candidatePhoneQuality;

        if (candidate.GoogleRating != current.GoogleRating)
            return candidate.GoogleRating > current.GoogleRating;

        // Prefer newer CSV/admin imports over older web-discovery duplicates.
        return candidate.Id > current.Id;
    }

    private static bool HasRealZip(DS.Entities.Doctor doctor)
    {
        var zip = ExtractZip(doctor.ZipCode);
        if (zip != null)
            return true;
        // Address often embeds the real ZIP when ZipCode column is 00000.
        return ExtractZip(doctor.Address) != null || ExtractZip(doctor.Location) != null;
    }

    private static bool HasRealState(DS.Entities.Doctor doctor)
    {
        var state = (doctor.State ?? string.Empty).Trim();
        return !string.IsNullOrWhiteSpace(state)
            && !state.Equals("NA", StringComparison.OrdinalIgnoreCase)
            && !state.Equals("-", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasCleanUsPhone(string? phone)
    {
        var digits = new string((phone ?? string.Empty).Where(char.IsDigit).ToArray());
        if (digits.Length == 11 && digits.StartsWith('1'))
            digits = digits[1..];
        // Reject mangled values like "(134) 620 23432" (11+ digits kept without stripping country code cleanly).
        if (digits.Length != 10)
            return false;
        // US area codes don't start with 0 or 1.
        return digits[0] is >= '2' and <= '9';
    }

    private async Task<List<DS.Entities.Doctor>> LoadSpecialtyMatchesAsync(
        string specialty, CancellationToken cancellationToken)
    {
        var doctors = await _db.Doctors
            .AsNoTracking()
            .Include(d => d.DoctorInsurances)
            .ThenInclude(di => di.InsuranceCarrier)
            .Include(d => d.DoctorLanguages)
            .ThenInclude(dl => dl.DoctorLanguage)
            .Include(d => d.PatientReviews)
            .Include(d => d.Locations)
            .Where(d => d.IsActive)
            .ToListAsync(cancellationToken);

        return doctors
            .Where(d => MatchesSpecialty(d.SpecialtyCategory, specialty) || MatchesSpecialty(d.Specialty, specialty))
            .ToList();
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
            OfficePhoneNumber = PhoneNumberHelper.FormatUsDisplay(doctor.OfficePhoneNumber),
            YearsOfPractice = doctor.YearsOfPractice,
            IsSponsored = doctor.IsSponsored
        };
    }

    private static int CalculateMatchScore(DS.Entities.Doctor doctor, double? distanceMiles)
    {
        var score = 70;
        score += (int)((double)doctor.GoogleRating * 2);
        if (distanceMiles.HasValue && distanceMiles.Value <= 5)
            score += 10;
        else if (distanceMiles.HasValue && distanceMiles.Value <= 15)
            score += 6;
        else if (distanceMiles.HasValue && distanceMiles.Value <= 25)
            score += 3;
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

        // "Use last used (77006)" → prefer the ZIP inside parentheses when present.
        var parenZip = System.Text.RegularExpressions.Regex.Match(lower, @"\((\d{5})(?:-\d{4})?\)");
        if (parenZip.Success)
            return parenZip.Groups[1].Value;

        return lower;
    }

    private static bool LocationMatches(string locationQuery, DS.Entities.Doctor doctor)
    {
        var queryZip = ExtractZip(locationQuery);
        var doctorZip = ExtractZip(doctor.ZipCode);

        // Exact ZIP column match (ignore 00000 placeholders).
        if (queryZip != null && doctorZip != null && queryZip == doctorZip)
            return true;

        // Same as: Address LIKE '%77006%' (also Location / City / ZipCode).
        if (queryZip != null && DoctorTextContainsZip(doctor, queryZip))
            return true;

        // Houston-area ZIP → any Houston / greater-Houston doctor in DB.
        if (queryZip != null && IsHoustonAreaZip(queryZip) && IsHoustonAreaDoctor(doctor))
            return true;

        // ZIP prefix (same 3-digit sectional center) when doctor has a real ZIP.
        if (queryZip != null && doctorZip != null
            && queryZip.Length >= 3 && doctorZip.Length >= 3
            && queryZip[..3] == doctorZip[..3])
            return true;

        var city = (doctor.City ?? string.Empty).ToLowerInvariant();
        var locationLabel = (doctor.Location ?? string.Empty).ToLowerInvariant();
        var token = locationQuery.Split(',')[0].Trim();

        if (!string.IsNullOrWhiteSpace(city)
            && (locationQuery.Contains(city) || city.Contains(token) || (city.Contains("houston") && token.Contains("houston"))))
            return true;

        if (!string.IsNullOrWhiteSpace(locationLabel) && locationLabel.Contains(token))
            return true;

        // City field sometimes stores "11224 Wilcrest Houston" — treat as Houston.
        if (token is "houston" or "houston tx" || locationQuery.Contains("houston"))
        {
            if (IsHoustonAreaDoctor(doctor))
                return true;
        }

        if (!string.IsNullOrWhiteSpace(doctor.State))
        {
            var doctorState = doctor.State.Trim();
            if (locationQuery.Contains(doctorState.ToLowerInvariant())
                && FuzzyCityMatch(token, city))
                return true;

            var queryStateCode = UsStates.CodeFromNameOrCode(locationQuery)
                ?? UsStates.CodeFromNameOrCode(token);
            if (queryStateCode != null
                && doctorState.Equals(queryStateCode, StringComparison.OrdinalIgnoreCase)
                && queryZip == null)
                return true;

            var doctorStateName = UsStates.All
                .FirstOrDefault(s => s.Code.Equals(doctorState, StringComparison.OrdinalIgnoreCase)).Name;
            if (!string.IsNullOrWhiteSpace(doctorStateName)
                && locationQuery.Contains(doctorStateName.ToLowerInvariant())
                && queryZip == null)
                return true;
        }

        return FuzzyCityMatch(token, city);
    }

    /// <summary>
    /// Equivalent to SQL: Address LIKE '%77006%' (also checks Location, City, ZipCode).
    /// </summary>
    private static bool DoctorTextContainsZip(DS.Entities.Doctor doctor, string zip)
    {
        if (string.IsNullOrWhiteSpace(zip))
            return false;

        static bool ContainsZip(string? haystack, string zipCode) =>
            !string.IsNullOrWhiteSpace(haystack)
            && haystack.Contains(zipCode, StringComparison.OrdinalIgnoreCase);

        return ContainsZip(doctor.Address, zip)
            || ContainsZip(doctor.Location, zip)
            || ContainsZip(doctor.City, zip)
            || ContainsZip(doctor.ZipCode, zip);
    }

    private static string? ExtractZip(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var match = System.Text.RegularExpressions.Regex.Match(value, @"\b(\d{5})(?:-\d{4})?\b");
        if (!match.Success)
            return null;

        var zip = match.Groups[1].Value;
        return zip is "00000" ? null : zip;
    }

    /// <summary>Houston + common surrounding ZIPs (770–775).</summary>
    private static bool IsHoustonAreaZip(string zip) =>
        zip.StartsWith("770", StringComparison.Ordinal)
        || zip.StartsWith("772", StringComparison.Ordinal)
        || zip.StartsWith("773", StringComparison.Ordinal)
        || zip.StartsWith("774", StringComparison.Ordinal)
        || zip.StartsWith("775", StringComparison.Ordinal);

    private static bool IsHoustonAreaDoctor(DS.Entities.Doctor doctor)
    {
        var city = (doctor.City ?? string.Empty).ToLowerInvariant();
        var location = (doctor.Location ?? string.Empty).ToLowerInvariant();
        var address = (doctor.Address ?? string.Empty).ToLowerInvariant();

        if (city.Contains("houston") || location.Contains("houston") || address.Contains("houston"))
            return true;

        var zip = ExtractZip(doctor.ZipCode);
        if (zip != null && IsHoustonAreaZip(zip))
            return true;

        var state = (doctor.State ?? string.Empty).Trim();
        if (state.Equals("TX", StringComparison.OrdinalIgnoreCase)
            && (city.Contains("katy") || city.Contains("sugar land") || city.Contains("pasadena")
                || city.Contains("pearland") || city.Contains("baytown") || city.Contains("spring")
                || city.Contains("cypress") || city.Contains("humble") || city.Contains("missouri city")))
            return true;

        return false;
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
