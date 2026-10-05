using System.Net.Http.Json;
using Meridian.Shared.DTOs;
namespace Meridian.Client.Services;
public sealed class LocalAccountService(HttpClient http, LocalAuthenticationStateProvider auth)
{
    public async Task LoginAsync(string email, string password)
    {
        using var response = await http.PostAsJsonAsync("api/account/login", new LoginRequest { Email = email, Password = password });
        await Accept(response);
    }
    public async Task RegisterAsync(RegisterAccountRequest request)
    {
        using var response = await http.PostAsJsonAsync("api/account/register", request);
        await Accept(response);
    }
    // DEV BYPASS: the API answers 404 here unless the bypass is enabled on that server.
    public async Task<bool> DevBypassAvailableAsync()
    {
        try { using var response = await http.GetAsync("api/dev/status"); return response.IsSuccessStatusCode; }
        catch { return false; }
    }
    public async Task DevLoginAsync(string? email)
    {
        using var response = await http.PostAsJsonAsync("api/dev/login", new { Email = string.IsNullOrWhiteSpace(email) ? null : email.Trim() });
        await Accept(response);
    }
    // DEV SEED: returns the server's summary message.
    public async Task<string> SeedAsync(bool reset, bool clearOnly = false)
    {
        var url = clearOnly ? "api/dev/seed/clear" : $"api/dev/seed?reset={(reset ? "true" : "false")}";
        using var response = await http.PostAsync(url, null);
        if (!response.IsSuccessStatusCode) throw await ApiRequestException.FromResponseAsync(response, default);
        var body = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        return body.TryGetProperty("message", out var m) ? m.GetString() ?? "Done." : "Done.";
    }
    private async Task Accept(HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode) throw await ApiRequestException.FromResponseAsync(response, default);
        auth.SignedIn(await response.Content.ReadFromJsonAsync<AccountDto>() ?? throw new InvalidOperationException("Empty login response."));
    }
}
