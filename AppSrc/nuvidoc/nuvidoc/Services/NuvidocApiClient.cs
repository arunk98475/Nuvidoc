using System.Net.Http.Json;
using System.Text.Json;
using Docovee.DS.Models;

namespace nuvidoc.Services;

public sealed class NuvidocApiClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _http;

    public NuvidocApiClient(HttpClient http)
    {
        _http = http;
    }

    public async Task<MobileBootstrapDto?> GetBootstrapAsync(CancellationToken cancellationToken = default)
    {
        using var response = await _http.GetAsync("api/mobile/bootstrap", cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<MobileBootstrapDto>(JsonOptions, cancellationToken);
    }

    public async Task<MobileEmailAvailableResponse> CheckEmailAvailableAsync(
        string email,
        CancellationToken cancellationToken = default)
    {
        var url = $"api/mobile/email-available?email={Uri.EscapeDataString(email)}";
        using var response = await _http.GetAsync(url, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<MobileEmailAvailableResponse>(JsonOptions, cancellationToken)
               ?? new MobileEmailAvailableResponse { Available = false, Message = "Unable to check email." };
    }

    public async Task<AccountRegisterResponse> RegisterPatientAsync(
        MobilePatientRegisterRequest request,
        CancellationToken cancellationToken = default)
    {
        using var response = await _http.PostAsJsonAsync("api/mobile/register", request, cancellationToken);
        var payload = await response.Content.ReadFromJsonAsync<AccountRegisterResponse>(JsonOptions, cancellationToken);
        if (payload is null)
        {
            return new AccountRegisterResponse
            {
                Success = false,
                Message = $"Registration failed ({(int)response.StatusCode}).",
                AccountType = AccountType.Patient
            };
        }

        return payload;
    }
}
