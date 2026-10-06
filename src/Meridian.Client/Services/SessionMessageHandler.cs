using Microsoft.AspNetCore.Components.WebAssembly.Http;
namespace Meridian.Client.Services;
public sealed class SessionMessageHandler : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        request.SetBrowserRequestCredentials(BrowserRequestCredentials.Include);
        request.Headers.TryAddWithoutValidation("X-Meridian-Client", "web");
        return base.SendAsync(request, ct);
    }
}
