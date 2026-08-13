using System.Net.Http.Json;
using Meridian.Client.Models;

namespace Meridian.Client.Services;

public sealed class UserApiClient
{
    private readonly HttpClient _httpClient;

    public UserApiClient(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<UserDto> CreateUserAsync(
        CreateUserRequest request)
    {
        using var response =
            await _httpClient.PostAsJsonAsync(
                "api/users",
                request);

        if (!response.IsSuccessStatusCode)
        {
            var error =
                await response.Content.ReadAsStringAsync();

            throw new InvalidOperationException(
                $"Unable to create user. " +
                $"{(int)response.StatusCode}: {error}");
        }

        var user =
            await response.Content
                .ReadFromJsonAsync<UserDto>();

        return user
            ?? throw new InvalidOperationException(
                "The API returned an empty user.");
    }
}