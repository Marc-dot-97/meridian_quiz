using Meridian.Client;
using Meridian.Client.Services;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Authentication;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;

var builder = WebAssemblyHostBuilder.CreateDefault(args);

builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

var apiBaseUrl = builder.Configuration["Api:BaseUrl"];
if (string.IsNullOrWhiteSpace(apiBaseUrl))
{
    throw new InvalidOperationException("Configuration value 'Api:BaseUrl' is missing.");
}

var useLocalMode = builder.Configuration.GetValue<bool>("Development:UseLocalMode");

// Always register the local-development services so local login/register pages can use them.
builder.Services.AddScoped<LocalAuthenticationStateProvider>();
builder.Services.AddScoped<LocalAccountService>();

if (useLocalMode)
{
    builder.Services.AddAuthorizationCore();
    builder.Services.AddScoped<AuthenticationStateProvider>(sp =>
        sp.GetRequiredService<LocalAuthenticationStateProvider>());

    builder.Services.AddScoped(_ => new HttpClient
    {
        BaseAddress = new Uri(apiBaseUrl, UriKind.Absolute)
    });
}
else
{
    var apiScope = builder.Configuration["Api:Scope"]
        ?? throw new InvalidOperationException("Configuration value 'Api:Scope' is missing.");

    builder.Services.AddMsalAuthentication(options =>
    {
        builder.Configuration.Bind("AzureAd", options.ProviderOptions.Authentication);
        options.ProviderOptions.DefaultAccessTokenScopes.Add(apiScope);
        options.ProviderOptions.LoginMode = "redirect";
    });

    builder.Services.AddScoped<ApiAuthorizationMessageHandler>();
    builder.Services.AddScoped(sp =>
    {
        var handler = sp.GetRequiredService<ApiAuthorizationMessageHandler>();
        handler.InnerHandler = new HttpClientHandler();

        return new HttpClient(handler)
        {
            BaseAddress = new Uri(apiBaseUrl, UriKind.Absolute)
        };
    });
}

builder.Services.AddScoped<MeridianApiClient>();
builder.Services.AddScoped<LeaderboardSignalRService>();

await builder.Build().RunAsync();
