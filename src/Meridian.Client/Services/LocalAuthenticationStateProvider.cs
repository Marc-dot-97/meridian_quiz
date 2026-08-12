using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;

namespace Meridian.Client.Services;

public sealed class LocalAuthenticationStateProvider : AuthenticationStateProvider
{
    private static readonly ClaimsPrincipal Anonymous =
        new(new ClaimsIdentity());

    private ClaimsPrincipal _currentUser = Anonymous;

    public override Task<AuthenticationState> GetAuthenticationStateAsync()
        => Task.FromResult(new AuthenticationState(_currentUser));

    public void SignIn(string displayName, string email, string role = "Advisor")
    {
        var identity = new ClaimsIdentity(
            new[]
            {
                new Claim(ClaimTypes.NameIdentifier, email),
                new Claim(ClaimTypes.Name, displayName),
                new Claim(ClaimTypes.Email, email),
                new Claim(ClaimTypes.Role, role)
            },
            authenticationType: "LocalDevelopment");

        _currentUser = new ClaimsPrincipal(identity);
        NotifyAuthenticationStateChanged(GetAuthenticationStateAsync());
    }

    public void SignOut()
    {
        _currentUser = Anonymous;
        NotifyAuthenticationStateChanged(GetAuthenticationStateAsync());
    }
}
