namespace Docovee.Integrations.Configuration;

public class OpenDentalOptions
{
    public const string SectionName = "OpenDental";

    public string BaseUrl { get; set; } = "https://api.opendental.com/api/v1";
    public string? DeveloperApiKey { get; set; }
}

public class NexHealthOptions
{
    public const string SectionName = "NexHealth";

    /// <summary>NexHealth API host (no /api/v1 path for v3).</summary>
    public string BaseUrl { get; set; } = "https://nexhealth.info";
    public string? ApiKey { get; set; }
    /// <summary>Header value for Nex-Api-Version on POST /authenticates only.</summary>
    public string ApiVersion { get; set; } = "v3.0.0";
    /// <summary>Accept / Content-Type for authenticated API calls (matches Postman collection).</summary>
    public string MediaType { get; set; } = "application/vnd.Nexhealth+json; version=2";
}
