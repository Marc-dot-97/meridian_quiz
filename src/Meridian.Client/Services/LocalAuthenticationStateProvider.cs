using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using Meridian.Shared.DTOs;
using Microsoft.AspNetCore.Components.Authorization;
namespace Meridian.Client.Services;
// Name retained so existing component injections continue to work; authentication is now API-backed.
public sealed class LocalAuthenticationStateProvider(HttpClient http) : AuthenticationStateProvider
{
    private ClaimsPrincipal _user = new(new ClaimsIdentity());
    private bool _loaded;
    public override async Task<AuthenticationState> GetAuthenticationStateAsync()
    {
        if (!_loaded)
        {
            using var response = await http.GetAsync("api/account/me");
            if (response.IsSuccessStatusCode) Set(await response.Content.ReadFromJsonAsync<AccountDto>());
            else if (response.StatusCode == HttpStatusCode.Unauthorized) { _loaded = true; }
            else throw await ApiRequestException.FromResponseAsync(response, default);
        }
        return new(_user);
    }
    private void Set(AccountDto? user)
    {
        _loaded = true;
        _user = user is null ? new(new ClaimsIdentity()) : new(new ClaimsIdentity(new[] {
            new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()), new Claim(ClaimTypes.Email, user.Email),
            new Claim(ClaimTypes.Name, user.DisplayName), new Claim(ClaimTypes.Role, user.Role)
        }, "MySqlSession"));
    }
    public void SignedIn(AccountDto user) { Set(user); NotifyAuthenticationStateChanged(Task.FromResult(new AuthenticationState(_user))); }
    public async Task SignOutAsync()
    {
        using var response = await http.PostAsync("api/account/logout", null);
        if (!response.IsSuccessStatusCode && response.StatusCode != HttpStatusCode.Unauthorized)
            throw await ApiRequestException.FromResponseAsync(response, default);
        Set(null); NotifyAuthenticationStateChanged(Task.FromResult(new AuthenticationState(_user)));
    }
}
