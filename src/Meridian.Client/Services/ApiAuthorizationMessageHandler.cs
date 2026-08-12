using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.WebAssembly.Authentication;

namespace Meridian.Client.Services;

public sealed class ApiAuthorizationMessageHandler : AuthorizationMessageHandler
{
    public ApiAuthorizationMessageHandler(
        IAccessTokenProvider provider,
        NavigationManager navigation,
        IConfiguration configuration)
        : base(provider, navigation)
    {
        var apiBaseUrl = configuration["Api:BaseUrl"]
            ?? throw new InvalidOperationException("Configuration value 'Api:BaseUrl' is missing.");
        var apiScope = configuration["Api:Scope"]
            ?? throw new InvalidOperationException("Configuration value 'Api:Scope' is missing.");

        ConfigureHandler(
            authorizedUrls: [apiBaseUrl.TrimEnd('/')],
            scopes: [apiScope]);
    }
}
