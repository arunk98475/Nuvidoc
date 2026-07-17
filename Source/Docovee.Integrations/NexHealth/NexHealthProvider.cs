using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Docovee.Integrations.Configuration;
using Docovee.Integrations.Contracts;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Docovee.Integrations.NexHealth;

public sealed class NexHealthProvider : IPmsProvider
{
    private const string DefaultAuthVersion = "v3.0.0";
    private const string DefaultMediaType = "application/vnd.Nexhealth+json; version=2";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly NexHealthOptions _options;
    private readonly ILogger<NexHealthProvider> _logger;

    public NexHealthProvider(
        IHttpClientFactory httpClientFactory,
        IOptions<NexHealthOptions> options,
        ILogger<NexHealthProvider> logger)
    {
        _httpClientFactory = httpClientFactory;
        _options = options.Value;
        _logger = logger;
    }

    public string ProviderId => PmsProviders.NexHealth;

    public async Task<PmsConnectionResult> TestConnectionAsync(
        PmsConnectionCredentials credentials,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // Auth alone proves the API key works (v3: POST /authenticates).
            var token = await GetBearerTokenAsync(credentials, cancellationToken);
            if (string.IsNullOrWhiteSpace(token))
            {
                return new PmsConnectionResult
                {
                    Success = false,
                    Message = "NexHealth authentication failed. Check the API key."
                };
            }

            if (string.IsNullOrWhiteSpace(credentials.InstitutionId))
            {
                return new PmsConnectionResult
                {
                    Success = true,
                    Message = "Authenticated with NexHealth (API key OK). Add institution/subdomain and location id to finish setup."
                };
            }

            using var response = await SendAsync(
                credentials,
                HttpMethod.Get,
                "institutions",
                null,
                cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                return new PmsConnectionResult
                {
                    Success = false,
                    Message = $"NexHealth authenticated, but institutions failed ({(int)response.StatusCode}): {Truncate(body)}"
                };
            }

            return new PmsConnectionResult
            {
                Success = true,
                Message = "Connected to NexHealth API (v3).",
                ExternalPracticeName = credentials.InstitutionId
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "NexHealth TestConnection failed");
            return new PmsConnectionResult { Success = false, Message = ex.Message };
        }
    }

    public async Task<IReadOnlyList<PmsSlot>> GetAvailabilityAsync(
        PmsAvailabilityRequest request,
        CancellationToken cancellationToken = default)
    {
        EnsureCredentials(request.Credentials);
        var locationId = request.Credentials.LocationId!;
        var subdomain = request.Credentials.InstitutionId!;
        var days = Math.Max(1, (request.To.DayNumber - request.From.DayNumber) + 1);
        var query =
            $"available_slots?subdomain={Uri.EscapeDataString(subdomain)}" +
            $"&start_date={request.From:yyyy-MM-dd}" +
            $"&days={days}" +
            $"&overlapping_operatory_slots=false" +
            $"&appointments_per_timeslot=1" +
            $"&lids[]={Uri.EscapeDataString(locationId)}";

        if (!string.IsNullOrWhiteSpace(request.Credentials.ProviderExternalId))
            query += $"&pids[]={Uri.EscapeDataString(request.Credentials.ProviderExternalId)}";

        using var response = await SendAsync(request.Credentials, HttpMethod.Get, query, null, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("NexHealth GetAvailability failed: {Status}", response.StatusCode);
            return Array.Empty<PmsSlot>();
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var slots = new List<PmsSlot>();

        if (doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
        {
            foreach (var block in data.EnumerateArray())
            {
                if (!block.TryGetProperty("slots", out var slotArray) || slotArray.ValueKind != JsonValueKind.Array)
                    continue;

                foreach (var slot in slotArray.EnumerateArray())
                {
                    var startText = GetString(slot, "time") ?? GetString(slot, "start_time");
                    if (!DateTime.TryParse(startText, out var start))
                        continue;

                    var end = start.AddMinutes(request.SlotMinutes);
                    if (DateTime.TryParse(GetString(slot, "end_time"), out var parsedEnd))
                        end = parsedEnd.Kind == DateTimeKind.Utc ? parsedEnd.ToLocalTime() : parsedEnd;

                    var localStart = start.Kind == DateTimeKind.Utc ? start.ToLocalTime() : start;
                    slots.Add(new PmsSlot
                    {
                        StartsAt = localStart,
                        EndsAt = end,
                        OperatoryId = GetString(slot, "operatory_id"),
                        ProviderExternalId = GetString(block, "pid"),
                        TimeLabel = localStart.ToString("h:mm tt")
                    });
                }
            }
        }

        return slots.OrderBy(s => s.StartsAt).ToList();
    }

    public async Task<PmsAppointmentResult> CreateAppointmentAsync(
        PmsCreateAppointmentRequest request,
        CancellationToken cancellationToken = default)
    {
        try
        {
            EnsureCredentials(request.Credentials);
            if (string.IsNullOrWhiteSpace(request.Credentials.ProviderExternalId))
            {
                return new PmsAppointmentResult
                {
                    Success = false,
                    Error = "NexHealth provider id is required. Set it in Settings → Integrations from GET /providers (do not use sample IDs)."
                };
            }

            var patientId = request.Patient.ExternalPatientId;
            if (string.IsNullOrWhiteSpace(patientId))
            {
                var patientResult = await CreatePatientAsync(request.Credentials, request.Patient, cancellationToken);
                if (!patientResult.Success)
                    return patientResult;
                patientId = patientResult.ExternalPatientId;
            }

            var appt = new Dictionary<string, object?>
            {
                ["patient_id"] = long.TryParse(patientId, out var pid) ? pid : patientId,
                ["provider_id"] = long.TryParse(request.Credentials.ProviderExternalId, out var provId)
                    ? provId
                    : request.Credentials.ProviderExternalId,
                ["start_time"] = FormatNexHealthUtc(request.StartsAt)
            };

            // Prefer Settings operatory; when map_by_operatory is true the practice must supply one.
            if (!string.IsNullOrWhiteSpace(request.Credentials.OperatoryId))
            {
                appt["operatory_id"] = long.TryParse(request.Credentials.OperatoryId, out var opId)
                    ? opId
                    : request.Credentials.OperatoryId;
            }

            var payload = new Dictionary<string, object?>
            {
                ["appointments_per_timeslot"] = 1,
                ["appt"] = appt
            };

            using var response = await SendAsync(
                request.Credentials,
                HttpMethod.Post,
                $"appointments?subdomain={Uri.EscapeDataString(request.Credentials.InstitutionId!)}" +
                $"&location_id={Uri.EscapeDataString(request.Credentials.LocationId!)}" +
                "&notify_patient=false",
                payload,
                cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return new PmsAppointmentResult
                {
                    Success = false,
                    Error = $"NexHealth create appointment failed ({(int)response.StatusCode}): {Truncate(body)}"
                };
            }

            using var doc = JsonDocument.Parse(body);
            var id = TryGetAppointmentId(doc.RootElement);
            return new PmsAppointmentResult
            {
                Success = true,
                ExternalAppointmentId = id,
                ExternalPatientId = patientId,
                RawStatus = "booked"
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "NexHealth CreateAppointment failed");
            return new PmsAppointmentResult { Success = false, Error = ex.Message };
        }
    }

    public async Task<PmsAppointmentResult> UpdateAppointmentAsync(
        PmsUpdateAppointmentRequest request,
        CancellationToken cancellationToken = default)
    {
        try
        {
            EnsureCredentials(request.Credentials);
            if (string.IsNullOrWhiteSpace(request.ExternalAppointmentId))
                return new PmsAppointmentResult { Success = false, Error = "External appointment id is required." };

            var payload = new Dictionary<string, object?>();
            var mapped = MapOutboundStatus(request.Status);
            if (!string.IsNullOrWhiteSpace(mapped))
                payload["status"] = mapped;
            if (request.NewStartsAt.HasValue)
                payload["start_time"] = request.NewStartsAt.Value.ToUniversalTime().ToString("o");
            if (!string.IsNullOrWhiteSpace(request.Note))
                payload["note"] = request.Note;

            using var response = await SendAsync(
                request.Credentials,
                HttpMethod.Patch,
                $"appointments/{Uri.EscapeDataString(request.ExternalAppointmentId)}?subdomain={Uri.EscapeDataString(request.Credentials.InstitutionId!)}",
                payload,
                cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return new PmsAppointmentResult
                {
                    Success = false,
                    Error = $"NexHealth update failed ({(int)response.StatusCode}): {Truncate(body)}"
                };
            }

            return new PmsAppointmentResult
            {
                Success = true,
                ExternalAppointmentId = request.ExternalAppointmentId,
                RawStatus = mapped
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "NexHealth UpdateAppointment failed");
            return new PmsAppointmentResult { Success = false, Error = ex.Message };
        }
    }

    public async Task<IReadOnlyList<PmsExternalAppointment>> PullRecentAppointmentsAsync(
        PmsPullChangesRequest request,
        CancellationToken cancellationToken = default)
    {
        EnsureCredentials(request.Credentials);
        var from = request.FromDate ?? DateOnly.FromDateTime(DateTime.Today.AddDays(-14));
        var to = request.ToDate ?? DateOnly.FromDateTime(DateTime.Today.AddDays(60));
        var subdomain = request.Credentials.InstitutionId!;
        var query =
            $"appointments?subdomain={Uri.EscapeDataString(subdomain)}" +
            $"&location_id={Uri.EscapeDataString(request.Credentials.LocationId!)}" +
            $"&start={FormatNexHealthDateStart(from)}" +
            $"&end={FormatNexHealthDateEnd(to)}" +
            "&per_page=100";

        using var response = await SendAsync(request.Credentials, HttpMethod.Get, query, null, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("NexHealth PullRecentAppointments failed: {Status}", response.StatusCode);
            return Array.Empty<PmsExternalAppointment>();
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var list = new List<PmsExternalAppointment>();
        var root = doc.RootElement.TryGetProperty("data", out var data) ? data : doc.RootElement;
        if (root.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in root.EnumerateArray())
            {
                var startText = GetString(item, "start_time") ?? GetString(item, "start");
                if (!DateTime.TryParse(startText, out var startsAt))
                    continue;

                var mappedStatus = MapInboundFromAppointment(item);
                list.Add(new PmsExternalAppointment
                {
                    ExternalAppointmentId = TryGetId(item) ?? "",
                    ExternalPatientId = GetString(item, "patient_id"),
                    PatientName = GetNestedName(item) ?? "Patient",
                    PatientPhone = GetString(item, "phone_number"),
                    PatientEmail = GetString(item, "email"),
                    StartsAt = startsAt.ToLocalTime(),
                    VisitReason = GetString(item, "note") ?? GetString(item, "descriptor"),
                    RawStatus = BuildRawStatus(item),
                    MappedStatus = mappedStatus,
                    UpdatedAt = DateTime.TryParse(GetString(item, "updated_at"), out var updated) ? updated : null
                });
            }
        }

        return list.Where(a => !string.IsNullOrWhiteSpace(a.ExternalAppointmentId)).ToList();
    }

    public async Task<PmsProviderEnsureResult> EnsureProviderAsync(
        PmsEnsureProviderRequest request,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(request.Credentials.InstitutionId))
            {
                return new PmsProviderEnsureResult
                {
                    Success = false,
                    Error = "Subdomain is required before adding a NexHealth provider."
                };
            }

            var candidates = await ListProvidersAsync(request.Credentials, cancellationToken);
            var match = FindBestNameMatch(candidates, request.FullName);
            if (match != null)
            {
                return new PmsProviderEnsureResult
                {
                    Success = true,
                    Created = false,
                    ProviderExternalId = match.Id,
                    Message = $"Linked existing NexHealth provider “{match.Name}” (id {match.Id}).",
                    Candidates = candidates
                };
            }

            // Official Synchronizer docs say create is unsupported for synced PMS providers,
            // but sandbox / non-synced institutions may allow POST — try, then fall back to picker.
            var created = await TryCreateProviderAsync(request, cancellationToken);
            if (created.Success)
                return created;

            return new PmsProviderEnsureResult
            {
                Success = false,
                Error = created.Error
                    ?? "No matching NexHealth provider found, and create is not available for this practice. Select an existing provider below.",
                Candidates = candidates
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "NexHealth EnsureProvider failed");
            return new PmsProviderEnsureResult { Success = false, Error = ex.Message };
        }
    }

    private async Task<IReadOnlyList<PmsProviderOption>> ListProvidersAsync(
        PmsConnectionCredentials credentials,
        CancellationToken cancellationToken)
    {
        var subdomain = credentials.InstitutionId!;
        var query = $"providers?subdomain={Uri.EscapeDataString(subdomain)}&per_page=100&inactive=false";
        if (!string.IsNullOrWhiteSpace(credentials.LocationId))
            query += $"&location_id={Uri.EscapeDataString(credentials.LocationId)}";

        using var response = await SendAsync(credentials, HttpMethod.Get, query, null, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("NexHealth ListProviders failed: {Status}", response.StatusCode);
            return Array.Empty<PmsProviderOption>();
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var list = new List<PmsProviderOption>();
        if (!doc.RootElement.TryGetProperty("data", out var data))
            return list;

        var items = data.ValueKind == JsonValueKind.Array
            ? data.EnumerateArray()
            : (data.ValueKind == JsonValueKind.Object ? new[] { data }.AsEnumerable() : Enumerable.Empty<JsonElement>());

        foreach (var item in items)
        {
            var id = GetString(item, "id");
            if (string.IsNullOrWhiteSpace(id))
                continue;

            var name = GetString(item, "name")
                       ?? $"{GetString(item, "first_name")} {GetString(item, "last_name")}".Trim();
            list.Add(new PmsProviderOption
            {
                Id = id,
                Name = string.IsNullOrWhiteSpace(name) ? $"Provider {id}" : name,
                Email = GetString(item, "email")
            });
        }

        return list;
    }

    private async Task<PmsProviderEnsureResult> TryCreateProviderAsync(
        PmsEnsureProviderRequest request,
        CancellationToken cancellationToken)
    {
        SplitDoctorName(request.FullName, out var first, out var last);
        var providerBody = new Dictionary<string, object?>
        {
            ["first_name"] = first,
            ["last_name"] = last,
            ["email"] = string.IsNullOrWhiteSpace(request.Email)
                ? null
                : request.Email.Trim(),
            ["bio"] = new Dictionary<string, object?>
            {
                ["phone_number"] = DigitsOnly(request.Phone)
            }
        };

        var payload = new Dictionary<string, object?> { ["provider"] = providerBody };
        var path = $"providers?subdomain={Uri.EscapeDataString(request.Credentials.InstitutionId!)}";
        if (!string.IsNullOrWhiteSpace(request.Credentials.LocationId))
            path += $"&location_id={Uri.EscapeDataString(request.Credentials.LocationId)}";

        using var response = await SendAsync(
            request.Credentials,
            HttpMethod.Post,
            path,
            payload,
            cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return new PmsProviderEnsureResult
            {
                Success = false,
                Error = $"NexHealth create provider failed ({(int)response.StatusCode}): {Truncate(body)}"
            };
        }

        using var doc = JsonDocument.Parse(body);
        var id = TryGetCreatedProviderId(doc.RootElement);
        if (string.IsNullOrWhiteSpace(id))
        {
            return new PmsProviderEnsureResult
            {
                Success = false,
                Error = "NexHealth create provider succeeded but no provider id was returned."
            };
        }

        return new PmsProviderEnsureResult
        {
            Success = true,
            Created = true,
            ProviderExternalId = id,
            Message = $"Created NexHealth provider “{first} {last}” (id {id})."
        };
    }

    private static string? TryGetCreatedProviderId(JsonElement root)
    {
        if (root.TryGetProperty("data", out var data))
        {
            if (data.ValueKind == JsonValueKind.Object)
                return GetString(data, "id");
            if (data.ValueKind == JsonValueKind.Array && data.GetArrayLength() > 0)
                return GetString(data[0], "id");
        }

        return GetString(root, "id");
    }

    private static PmsProviderOption? FindBestNameMatch(IReadOnlyList<PmsProviderOption> candidates, string fullName)
    {
        var target = NormalizePersonName(fullName);
        if (string.IsNullOrWhiteSpace(target) || candidates.Count == 0)
            return null;

        var exact = candidates.FirstOrDefault(c => NormalizePersonName(c.Name) == target);
        if (exact != null)
            return exact;

        // Last-name match when unique.
        var last = target.Split(' ', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
        if (string.IsNullOrWhiteSpace(last))
            return null;

        var lastMatches = candidates
            .Where(c => NormalizePersonName(c.Name).Split(' ', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() == last)
            .ToList();
        return lastMatches.Count == 1 ? lastMatches[0] : null;
    }

    private static void SplitDoctorName(string? fullName, out string first, out string last)
    {
        var cleaned = (fullName ?? "")
            .Replace("Dr.", "", StringComparison.OrdinalIgnoreCase)
            .Replace("Dr ", "", StringComparison.OrdinalIgnoreCase)
            .Trim();
        var parts = cleaned.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            first = "Provider";
            last = "Unknown";
        }
        else if (parts.Length == 1)
        {
            first = parts[0];
            last = "Unknown";
        }
        else
        {
            first = parts[0];
            last = string.Join(' ', parts.Skip(1));
        }
    }

    private static string NormalizePersonName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "";
        var cleaned = name
            .Replace("Dr.", "", StringComparison.OrdinalIgnoreCase)
            .Replace(".", "")
            .Trim();
        return string.Join(' ', cleaned.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .ToLowerInvariant();
    }

    private async Task<PmsAppointmentResult> CreatePatientAsync(
        PmsConnectionCredentials credentials,
        PmsPatientInfo patient,
        CancellationToken cancellationToken)
    {
        SplitName(patient, out var first, out var last);
        var providerId = credentials.ProviderExternalId;
        if (string.IsNullOrWhiteSpace(providerId))
        {
            return new PmsAppointmentResult
            {
                Success = false,
                Error = "NexHealth provider id is required to create a patient. Set it in Settings → Integrations from GET /providers."
            };
        }

        var payload = new Dictionary<string, object?>
        {
            ["provider"] = new Dictionary<string, object?>
            {
                ["provider_id"] = long.TryParse(providerId, out var parsedProviderId) ? parsedProviderId : providerId
            },
            ["patient"] = new Dictionary<string, object?>
            {
                ["first_name"] = first,
                ["last_name"] = last,
                ["email"] = patient.Email,
                ["bio"] = new Dictionary<string, object?>
                {
                    ["date_of_birth"] = patient.DateOfBirth?.ToString("yyyy-MM-dd"),
                    ["phone_number"] = DigitsOnly(patient.Phone)
                }
            },
            ["return_existing_if_match"] = true
        };

        using var response = await SendAsync(
            credentials,
            HttpMethod.Post,
            $"patients?subdomain={Uri.EscapeDataString(credentials.InstitutionId!)}" +
            $"&location_id={Uri.EscapeDataString(credentials.LocationId!)}",
            payload,
            cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return new PmsAppointmentResult
            {
                Success = false,
                Error = $"NexHealth create patient failed ({(int)response.StatusCode}): {Truncate(body)}"
            };
        }

        using var doc = JsonDocument.Parse(body);
        return new PmsAppointmentResult
        {
            Success = true,
            ExternalPatientId = TryGetPatientId(doc.RootElement)
        };
    }

    private async Task<HttpResponseMessage> SendAsync(
        PmsConnectionCredentials credentials,
        HttpMethod method,
        string relativeUrl,
        object? payload,
        CancellationToken cancellationToken)
    {
        var apiKey = ResolveApiKey(credentials);
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException("NexHealth API key is required.");

        var client = _httpClientFactory.CreateClient("NexHealth");
        var baseUrl = ResolveBaseUrl(credentials);
        var path = relativeUrl.TrimStart('/');

        // Most v3 routes require subdomain on the query string.
        if (!string.IsNullOrWhiteSpace(credentials.InstitutionId)
            && !path.Contains("subdomain=", StringComparison.OrdinalIgnoreCase)
            && !path.StartsWith("authenticates", StringComparison.OrdinalIgnoreCase))
        {
            path += path.Contains('?') ? "&" : "?";
            path += $"subdomain={Uri.EscapeDataString(credentials.InstitutionId)}";
        }

        var request = new HttpRequestMessage(method, $"{baseUrl}/{path}");
        // Match the working v3 examples the user validated in Postman / RestSharp.
        request.Headers.TryAddWithoutValidation("Authorization", apiKey);
        request.Headers.TryAddWithoutValidation("Nex-Api-Version", AuthVersion());
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        if (payload != null)
        {
            var json = JsonSerializer.Serialize(payload, JsonOptions);
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }

        return await client.SendAsync(request, cancellationToken);
    }

    private string MediaType() =>
        string.IsNullOrWhiteSpace(_options.MediaType) ? DefaultMediaType : _options.MediaType.Trim();

    private string AuthVersion() =>
        string.IsNullOrWhiteSpace(_options.ApiVersion) ? DefaultAuthVersion : _options.ApiVersion.Trim();

    private async Task<string> GetBearerTokenAsync(
        PmsConnectionCredentials credentials,
        CancellationToken cancellationToken)
    {
        var apiKey = ResolveApiKey(credentials);
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException(
                "NexHealth API key is required. Use the key from Developer Portal → API Key (Test mode), not the Dentrix product key.");

        // Bearer JWTs are returned BY /authenticates — they are not the API key.
        if (apiKey.StartsWith("eyJ", StringComparison.Ordinal))
            throw new InvalidOperationException(
                "That value looks like a bearer token (JWT), not an API key. " +
                "Paste the same API key Postman puts in Authorization on /authenticates (not data.token).");

        var client = _httpClientFactory.CreateClient("NexHealth");
        var baseUrl = ResolveBaseUrl(credentials);
        var authUrl = $"{baseUrl}/authenticates";

        using var request = new HttpRequestMessage(HttpMethod.Post, authUrl);
        // Step 1 (Postman): raw API key + Nex-Api-Version v3.0.0 — no Bearer, no NexHealth media type.
        request.Headers.TryAddWithoutValidation("Authorization", apiKey);
        request.Headers.TryAddWithoutValidation("Nex-Api-Version", AuthVersion());
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var response = await client.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"NexHealth auth failed ({(int)response.StatusCode}) calling {authUrl} " +
                $"(Nex-Api-Version: {AuthVersion()}, key {Fingerprint(apiKey)}): {Truncate(body)}. " +
                "Use the portal API key that works in Postman/curl — not the bearer token.");
        }

        using var doc = JsonDocument.Parse(body);
        if (doc.RootElement.TryGetProperty("data", out var data)
            && data.TryGetProperty("token", out var token)
            && token.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(token.GetString()))
        {
            return token.GetString()!;
        }

        throw new InvalidOperationException("NexHealth auth response did not include a token.");
    }

    private string? ResolveApiKey(PmsConnectionCredentials credentials)
    {
        if (!string.IsNullOrWhiteSpace(credentials.ApiKey))
            return NormalizeApiKey(credentials.ApiKey);
        return NormalizeApiKey(_options.ApiKey);
    }

    private static string Fingerprint(string apiKey)
    {
        if (apiKey.Length <= 12)
            return $"(len={apiKey.Length})";
        return $"'{apiKey[..6]}…{apiKey[^4..]}' (len={apiKey.Length})";
    }

    private string ResolveBaseUrl(PmsConnectionCredentials credentials)
    {
        var raw = !string.IsNullOrWhiteSpace(credentials.BaseUrl)
            ? credentials.BaseUrl
            : _options.BaseUrl;

        if (string.IsNullOrWhiteSpace(raw))
            raw = "https://nexhealth.info";

        raw = raw.Trim().TrimEnd('/');

        // Guard against older config that used /api/v1 — auth lives at host root.
        if (raw.EndsWith("/api/v1", StringComparison.OrdinalIgnoreCase))
            raw = raw[..^"/api/v1".Length].TrimEnd('/');

        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            throw new InvalidOperationException(
                $"NexHealth BaseUrl is invalid ('{raw}'). Expected https://nexhealth.info");
        }

        return raw;
    }

    private static string? NormalizeApiKey(string? apiKey)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            return null;

        var key = apiKey.Trim();
        // Users sometimes paste a bearer token or "Bearer <key>" into the API key field.
        if (key.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            key = key["Bearer ".Length..].Trim();

        return key;
    }

    private static void EnsureCredentials(PmsConnectionCredentials credentials)
    {
        if (string.IsNullOrWhiteSpace(credentials.InstitutionId))
            throw new InvalidOperationException("NexHealth institution/subdomain is required.");
        if (string.IsNullOrWhiteSpace(credentials.LocationId))
            throw new InvalidOperationException("NexHealth location id is required.");
    }

    private static string FormatNexHealthUtc(DateTime value) =>
        value.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss'Z'");

    private static string FormatNexHealthDateStart(DateOnly date) =>
        $"{date:yyyy-MM-dd}T00:00:00Z";

    private static string FormatNexHealthDateEnd(DateOnly date) =>
        $"{date:yyyy-MM-dd}T23:59:59Z";

    private static string MapInboundFromAppointment(JsonElement item)
    {
        if (GetBool(item, "cancelled") == true)
            return "PatientCanceled";
        if (GetBool(item, "patient_missed") == true)
            return "PatientNoShow";
        if (GetBool(item, "confirmed") == true)
            return "Confirmed";
        return "Unconfirmed";
    }

    private static string BuildRawStatus(JsonElement item)
    {
        if (GetBool(item, "cancelled") == true) return "cancelled";
        if (GetBool(item, "patient_missed") == true) return "no_show";
        if (GetBool(item, "confirmed") == true) return "confirmed";
        return "unconfirmed";
    }

    private static bool? GetBool(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
            return null;
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null
        };
    }

    private static string? TryGetAppointmentId(JsonElement element)
    {
        if (element.TryGetProperty("data", out var data)
            && data.TryGetProperty("appt", out var appt))
        {
            return TryGetId(appt);
        }

        return TryGetId(element);
    }

    private static string? TryGetPatientId(JsonElement element)
    {
        if (element.TryGetProperty("data", out var data)
            && data.TryGetProperty("user", out var user))
        {
            return TryGetId(user);
        }

        return TryGetId(element);
    }

    private static string MapOutboundStatus(string status) => status switch
    {
        "Confirmed" => "confirmed",
        "PracticeCanceled" or "PatientCanceled" or "Cancelled" => "cancelled",
        "PatientNoShow" => "no_show",
        "Completed" => "completed",
        _ => "booked"
    };

    private static string MapInboundStatus(string? status) => (status ?? "").ToLowerInvariant() switch
    {
        "confirmed" => "Confirmed",
        "cancelled" or "canceled" => "PatientCanceled",
        "no_show" or "noshow" => "PatientNoShow",
        "completed" => "Completed",
        _ => "Unconfirmed"
    };

    private static string? TryGetId(JsonElement element)
    {
        if (element.TryGetProperty("data", out var data))
            return TryGetId(data);
        if (element.TryGetProperty("id", out var id))
            return id.ValueKind == JsonValueKind.Number ? id.GetRawText() : id.GetString();
        if (element.TryGetProperty("appointment", out var apt))
            return TryGetId(apt);
        if (element.TryGetProperty("patient", out var patient))
            return TryGetId(patient);
        return null;
    }

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null
            ? value.ToString()
            : null;

    private static string? GetNestedName(JsonElement element)
    {
        if (element.TryGetProperty("patient", out var patient))
        {
            var first = GetString(patient, "first_name");
            var last = GetString(patient, "last_name");
            var name = $"{first} {last}".Trim();
            if (!string.IsNullOrWhiteSpace(name))
                return name;
        }

        return GetString(element, "patient_name");
    }

    private static void SplitName(PmsPatientInfo patient, out string first, out string last)
    {
        if (!string.IsNullOrWhiteSpace(patient.FirstName) || !string.IsNullOrWhiteSpace(patient.LastName))
        {
            first = patient.FirstName?.Trim() ?? "Patient";
            last = patient.LastName?.Trim() ?? "Unknown";
            return;
        }

        var parts = (patient.FullName ?? "").Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            first = "Patient";
            last = "Unknown";
        }
        else if (parts.Length == 1)
        {
            first = parts[0];
            last = "Unknown";
        }
        else
        {
            first = parts[0];
            last = string.Join(' ', parts.Skip(1));
        }
    }

    private static string DigitsOnly(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "" : new string(value.Where(char.IsDigit).ToArray());

    private static string Truncate(string value) =>
        value.Length <= 300 ? value : value[..300] + "...";
}
