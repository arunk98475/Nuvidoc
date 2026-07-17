using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Docovee.Integrations.Configuration;
using Docovee.Integrations.Contracts;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Docovee.Integrations.OpenDental;

public sealed class OpenDentalProvider : IPmsProvider
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly OpenDentalOptions _options;
    private readonly ILogger<OpenDentalProvider> _logger;

    public OpenDentalProvider(
        IHttpClientFactory httpClientFactory,
        IOptions<OpenDentalOptions> options,
        ILogger<OpenDentalProvider> logger)
    {
        _httpClientFactory = httpClientFactory;
        _options = options.Value;
        _logger = logger;
    }

    public string ProviderId => PmsProviders.OpenDental;

    public async Task<PmsConnectionResult> TestConnectionAsync(
        PmsConnectionCredentials credentials,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await SendAsync(credentials, HttpMethod.Get, "preferences", null, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                return new PmsConnectionResult
                {
                    Success = false,
                    Message = $"Open Dental connection failed ({(int)response.StatusCode}): {Truncate(body)}"
                };
            }

            return new PmsConnectionResult
            {
                Success = true,
                Message = "Connected to Open Dental API.",
                ExternalPracticeName = "Open Dental practice"
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Open Dental TestConnection failed");
            return new PmsConnectionResult { Success = false, Message = ex.Message };
        }
    }

    public async Task<IReadOnlyList<PmsSlot>> GetAvailabilityAsync(
        PmsAvailabilityRequest request,
        CancellationToken cancellationToken = default)
    {
        var dateStart = request.From.ToString("yyyy-MM-dd");
        var dateEnd = request.To.ToString("yyyy-MM-dd");
        var query = $"appointments/Slots?dateStart={dateStart}&dateEnd={dateEnd}";
        if (!string.IsNullOrWhiteSpace(request.Credentials.ProviderExternalId))
            query += $"&ProvNum={Uri.EscapeDataString(request.Credentials.ProviderExternalId)}";
        if (!string.IsNullOrWhiteSpace(request.Credentials.OperatoryId))
            query += $"&OpNum={Uri.EscapeDataString(request.Credentials.OperatoryId)}";
        if (!string.IsNullOrWhiteSpace(request.Credentials.ClinicNum))
            query += $"&ClinicNum={Uri.EscapeDataString(request.Credentials.ClinicNum)}";

        using var response = await SendAsync(request.Credentials, HttpMethod.Get, query, null, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("Open Dental GetAvailability failed: {Status}", response.StatusCode);
            return Array.Empty<PmsSlot>();
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var rows = await JsonSerializer.DeserializeAsync<List<OdSlotDto>>(stream, JsonOptions, cancellationToken)
                   ?? new List<OdSlotDto>();

        var slots = new List<PmsSlot>();
        foreach (var row in rows)
        {
            if (!TryParseOdDateTime(row.DateTimeStart ?? row.AptDateTime, out var start))
                continue;
            var end = start.AddMinutes(request.SlotMinutes);
            if (TryParseOdDateTime(row.DateTimeEnd, out var parsedEnd))
                end = parsedEnd;

            slots.Add(new PmsSlot
            {
                StartsAt = start,
                EndsAt = end,
                OperatoryId = row.OpNum?.ToString(),
                ProviderExternalId = row.ProvNum?.ToString(),
                TimeLabel = start.ToString("h:mm tt")
            });
        }

        return slots
            .OrderBy(s => s.StartsAt)
            .ToList();
    }

    public async Task<PmsAppointmentResult> CreateAppointmentAsync(
        PmsCreateAppointmentRequest request,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var patientId = request.Patient.ExternalPatientId;
            if (string.IsNullOrWhiteSpace(patientId))
            {
                var createdPatient = await CreateOrFindPatientAsync(request.Credentials, request.Patient, cancellationToken);
                if (!createdPatient.Success)
                    return createdPatient;
                patientId = createdPatient.ExternalPatientId;
            }

            var pattern = BuildPattern(request.DurationMinutes);
            var payload = new Dictionary<string, object?>
            {
                ["PatNum"] = long.Parse(patientId!),
                ["Op"] = long.Parse(string.IsNullOrWhiteSpace(request.Credentials.OperatoryId)
                    ? "1"
                    : request.Credentials.OperatoryId),
                ["AptDateTime"] = request.StartsAt.ToString("yyyy-MM-dd HH:mm:ss"),
                ["AptStatus"] = "Scheduled",
                ["Pattern"] = pattern,
                ["Note"] = string.IsNullOrWhiteSpace(request.Note)
                    ? $"NuviDoc: {request.VisitReason}"
                    : request.Note,
                ["IsNewPatient"] = "false"
            };

            if (!string.IsNullOrWhiteSpace(request.Credentials.ProviderExternalId)
                && long.TryParse(request.Credentials.ProviderExternalId, out var provNum))
                payload["ProvNum"] = provNum;

            if (!string.IsNullOrWhiteSpace(request.Credentials.ClinicNum)
                && long.TryParse(request.Credentials.ClinicNum, out var clinicNum))
                payload["ClinicNum"] = clinicNum;

            using var response = await SendAsync(
                request.Credentials,
                HttpMethod.Post,
                "appointments",
                payload,
                cancellationToken);

            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return new PmsAppointmentResult
                {
                    Success = false,
                    Error = $"Open Dental create appointment failed ({(int)response.StatusCode}): {Truncate(body)}"
                };
            }

            var apt = JsonSerializer.Deserialize<OdAppointmentDto>(body, JsonOptions);
            return new PmsAppointmentResult
            {
                Success = true,
                ExternalAppointmentId = apt?.AptNum?.ToString() ?? ExtractLocationId(response),
                ExternalPatientId = patientId,
                RawStatus = apt?.AptStatus ?? "Scheduled"
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Open Dental CreateAppointment failed");
            return new PmsAppointmentResult { Success = false, Error = ex.Message };
        }
    }

    public async Task<PmsAppointmentResult> UpdateAppointmentAsync(
        PmsUpdateAppointmentRequest request,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(request.ExternalAppointmentId))
                return new PmsAppointmentResult { Success = false, Error = "External appointment id is required." };

            var payload = new Dictionary<string, object?>();
            var mapped = MapOutboundStatus(request.Status);
            if (!string.IsNullOrWhiteSpace(mapped))
                payload["AptStatus"] = mapped;

            if (request.NewStartsAt.HasValue)
                payload["AptDateTime"] = request.NewStartsAt.Value.ToString("yyyy-MM-dd HH:mm:ss");

            if (!string.IsNullOrWhiteSpace(request.Note))
                payload["Note"] = request.Note;

            if (payload.Count == 0)
                return new PmsAppointmentResult { Success = true, ExternalAppointmentId = request.ExternalAppointmentId };

            using var response = await SendAsync(
                request.Credentials,
                HttpMethod.Put,
                $"appointments/{Uri.EscapeDataString(request.ExternalAppointmentId)}",
                payload,
                cancellationToken);

            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return new PmsAppointmentResult
                {
                    Success = false,
                    Error = $"Open Dental update failed ({(int)response.StatusCode}): {Truncate(body)}"
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
            _logger.LogWarning(ex, "Open Dental UpdateAppointment failed");
            return new PmsAppointmentResult { Success = false, Error = ex.Message };
        }
    }

    public async Task<IReadOnlyList<PmsExternalAppointment>> PullRecentAppointmentsAsync(
        PmsPullChangesRequest request,
        CancellationToken cancellationToken = default)
    {
        var from = request.FromDate ?? DateOnly.FromDateTime(DateTime.Today.AddDays(-7));
        var to = request.ToDate ?? DateOnly.FromDateTime(DateTime.Today.AddDays(60));
        var query = $"appointments?dateStart={from:yyyy-MM-dd}&dateEnd={to:yyyy-MM-dd}";

        using var response = await SendAsync(request.Credentials, HttpMethod.Get, query, null, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("Open Dental PullRecentAppointments failed: {Status}", response.StatusCode);
            return Array.Empty<PmsExternalAppointment>();
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var rows = await JsonSerializer.DeserializeAsync<List<OdAppointmentDto>>(stream, JsonOptions, cancellationToken)
                   ?? new List<OdAppointmentDto>();

        var list = new List<PmsExternalAppointment>();
        foreach (var row in rows)
        {
            if (!TryParseOdDateTime(row.AptDateTime, out var startsAt))
                continue;

            list.Add(new PmsExternalAppointment
            {
                ExternalAppointmentId = row.AptNum?.ToString() ?? "",
                ExternalPatientId = row.PatNum?.ToString(),
                PatientName = row.PatientName ?? $"Patient {row.PatNum}",
                StartsAt = startsAt,
                VisitReason = row.ProcDescript ?? row.Note,
                RawStatus = row.AptStatus ?? "",
                MappedStatus = MapInboundStatus(row.AptStatus),
                UpdatedAt = TryParseOdDateTime(row.DateTStamp, out var updated) ? updated : null
            });
        }

        return list
            .Where(a => !string.IsNullOrWhiteSpace(a.ExternalAppointmentId))
            .Where(a => a.UpdatedAt == null || a.UpdatedAt >= request.SinceUtc.AddHours(-1))
            .ToList();
    }

    public Task<PmsProviderEnsureResult> EnsureProviderAsync(
        PmsEnsureProviderRequest request,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new PmsProviderEnsureResult
        {
            Success = false,
            Error = "Creating providers via API is not supported for Open Dental in this integration."
        });
    }

    public Task<PmsProviderEnsureResult> FindProviderByNpiAsync(
        PmsFindProviderByNpiRequest request,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new PmsProviderEnsureResult
        {
            Success = false,
            Error = "NPI provider lookup is not supported for Open Dental in this integration."
        });
    }

    private async Task<PmsAppointmentResult> CreateOrFindPatientAsync(
        PmsConnectionCredentials credentials,
        PmsPatientInfo patient,
        CancellationToken cancellationToken)
    {
        SplitName(patient, out var first, out var last);

        if (!string.IsNullOrWhiteSpace(patient.Phone) || !string.IsNullOrWhiteSpace(patient.Email))
        {
            var search = !string.IsNullOrWhiteSpace(patient.Phone)
                ? $"patients?Phone={Uri.EscapeDataString(DigitsOnly(patient.Phone))}"
                : $"patients?Email={Uri.EscapeDataString(patient.Email!)}";

            using var findResponse = await SendAsync(credentials, HttpMethod.Get, search, null, cancellationToken);
            if (findResponse.IsSuccessStatusCode)
            {
                await using var stream = await findResponse.Content.ReadAsStreamAsync(cancellationToken);
                var found = await JsonSerializer.DeserializeAsync<List<OdPatientDto>>(stream, JsonOptions, cancellationToken);
                var match = found?.FirstOrDefault();
                if (match?.PatNum != null)
                {
                    return new PmsAppointmentResult
                    {
                        Success = true,
                        ExternalPatientId = match.PatNum.ToString()
                    };
                }
            }
        }

        var payload = new Dictionary<string, object?>
        {
            ["LName"] = last,
            ["FName"] = first,
            ["Birthdate"] = patient.DateOfBirth?.ToString("yyyy-MM-dd") ?? "0001-01-01",
            ["WirelessPhone"] = DigitsOnly(patient.Phone),
            ["Email"] = patient.Email ?? ""
        };

        using var createResponse = await SendAsync(credentials, HttpMethod.Post, "patients", payload, cancellationToken);
        var body = await createResponse.Content.ReadAsStringAsync(cancellationToken);
        if (!createResponse.IsSuccessStatusCode)
        {
            return new PmsAppointmentResult
            {
                Success = false,
                Error = $"Open Dental create patient failed ({(int)createResponse.StatusCode}): {Truncate(body)}"
            };
        }

        var created = JsonSerializer.Deserialize<OdPatientDto>(body, JsonOptions);
        return new PmsAppointmentResult
        {
            Success = true,
            ExternalPatientId = created?.PatNum?.ToString() ?? ExtractLocationId(createResponse)
        };
    }

    private async Task<HttpResponseMessage> SendAsync(
        PmsConnectionCredentials credentials,
        HttpMethod method,
        string relativeUrl,
        object? payload,
        CancellationToken cancellationToken)
    {
        var developerKey = credentials.DeveloperApiKey ?? _options.DeveloperApiKey;
        var customerKey = credentials.CustomerApiKey;
        if (string.IsNullOrWhiteSpace(developerKey) || string.IsNullOrWhiteSpace(customerKey))
            throw new InvalidOperationException("Open Dental developer and customer API keys are required.");

        var client = _httpClientFactory.CreateClient("OpenDental");
        var baseUrl = (credentials.BaseUrl ?? _options.BaseUrl).TrimEnd('/');
        var request = new HttpRequestMessage(method, $"{baseUrl}/{relativeUrl.TrimStart('/')}");
        request.Headers.TryAddWithoutValidation("Authorization", $"ODFHIR {developerKey}/{customerKey}");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        if (payload != null)
        {
            var json = JsonSerializer.Serialize(payload, JsonOptions);
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }

        return await client.SendAsync(request, cancellationToken);
    }

    private static string BuildPattern(int durationMinutes)
    {
        var blocks = Math.Max(1, (int)Math.Ceiling(durationMinutes / 5.0));
        return new string('X', blocks);
    }

    private static string MapOutboundStatus(string status) => status switch
    {
        "Confirmed" => "Scheduled",
        "PracticeCanceled" or "PatientCanceled" or "Cancelled" => "Broken",
        "PatientNoShow" => "Broken",
        "Completed" => "Complete",
        _ => "Scheduled"
    };

    private static string MapInboundStatus(string? aptStatus) => (aptStatus ?? "").ToLowerInvariant() switch
    {
        "scheduled" or "asap" => "Unconfirmed",
        "complete" => "Completed",
        "broken" or "unschedlist" => "PatientCanceled",
        _ => "Unconfirmed"
    };

    private static bool TryParseOdDateTime(string? value, out DateTime result)
    {
        result = default;
        if (string.IsNullOrWhiteSpace(value))
            return false;
        return DateTime.TryParse(value, out result);
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

    private static string? ExtractLocationId(HttpResponseMessage response)
    {
        if (response.Headers.Location == null)
            return null;
        var path = response.Headers.Location.OriginalString.TrimEnd('/');
        var slash = path.LastIndexOf('/');
        return slash >= 0 ? path[(slash + 1)..] : path;
    }

    private static string Truncate(string value) =>
        value.Length <= 300 ? value : value[..300] + "...";

    private sealed class OdSlotDto
    {
        public string? DateTimeStart { get; set; }
        public string? DateTimeEnd { get; set; }
        public string? AptDateTime { get; set; }
        public long? OpNum { get; set; }
        public long? ProvNum { get; set; }
    }

    private sealed class OdAppointmentDto
    {
        public long? AptNum { get; set; }
        public long? PatNum { get; set; }
        public string? AptDateTime { get; set; }
        public string? AptStatus { get; set; }
        public string? Note { get; set; }
        public string? ProcDescript { get; set; }
        public string? PatientName { get; set; }
        public string? DateTStamp { get; set; }
    }

    private sealed class OdPatientDto
    {
        public long? PatNum { get; set; }
        public string? FName { get; set; }
        public string? LName { get; set; }
    }
}
