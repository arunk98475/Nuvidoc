using System.Net;
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

    public async Task<MobilePatientLoginResponse> LoginPatientAsync(
        MobilePatientLoginRequest request,
        CancellationToken cancellationToken = default)
    {
        using var response = await _http.PostAsJsonAsync("api/mobile/login", request, cancellationToken);
        var payload = await response.Content.ReadFromJsonAsync<MobilePatientLoginResponse>(JsonOptions, cancellationToken);
        if (payload is null)
        {
            return new MobilePatientLoginResponse
            {
                Success = false,
                Message = $"Sign-in failed ({(int)response.StatusCode})."
            };
        }

        return payload;
    }

    /// <summary>Same endpoint / contract as the web Nuvi chat (docovee.js).</summary>
    public async Task<(bool Ok, int Status, ChatMessageResponse Data)> SendChatMessageAsync(
        ChatMessageRequest request,
        CancellationToken cancellationToken = default)
    {
        using var response = await _http.PostAsJsonAsync("api/chat/message", request, cancellationToken);
        var data = await response.Content.ReadFromJsonAsync<ChatMessageResponse>(JsonOptions, cancellationToken)
                   ?? new ChatMessageResponse();
        return (response.IsSuccessStatusCode, (int)response.StatusCode, data);
    }

    public async Task<IReadOnlyList<DoctorDto>> GetFeaturedDoctorsAsync(
        int count = 6,
        CancellationToken cancellationToken = default)
    {
        using var response = await _http.GetAsync($"api/doctors/featured?count={count}", cancellationToken);
        response.EnsureSuccessStatusCode();
        var cards = await response.Content.ReadFromJsonAsync<List<FeaturedDoctorCardDto>>(JsonOptions, cancellationToken)
                    ?? new List<FeaturedDoctorCardDto>();

        return cards.Select((c, i) => new DoctorDto
        {
            Id = c.Id,
            Name = c.Name,
            Specialty = c.Specialty,
            Location = string.Join(", ", new[] { c.City, c.State }.Where(s => !string.IsNullOrWhiteSpace(s))),
            PhotoUrl = c.PhotoUrl,
            AvatarInitials = c.AvatarInitials,
            GoogleRating = c.GoogleRating,
            GoogleReviewCount = c.GoogleReviewCount,
            Niche = c.Niche,
            IsSponsored = c.IsFeatured || i < 2,
            Tag = c.HighlightText ?? ""
        }).ToList();
    }
}

/// <summary>Shared cookie jar so mobile login auth is sent on chat API calls.</summary>
public sealed class ApiCookieContainer
{
    public CookieContainer Cookies { get; } = new();
}
