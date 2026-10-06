using System.Net;
using System.Text.Json;
namespace Meridian.Client.Services;
public sealed class ApiRequestException(HttpStatusCode statusCode, string message) : Exception(message)
{
    public HttpStatusCode StatusCode { get; } = statusCode;
    public static async Task<ApiRequestException> FromResponseAsync(HttpResponseMessage response, CancellationToken cancellationToken = default)
    {
        var message = response.StatusCode switch { HttpStatusCode.Unauthorized => "Email or password is incorrect, or your session has expired.", HttpStatusCode.TooManyRequests => "Too many attempts. Please wait a minute and try again.", _ => "The request could not be completed. Please try again." };
        try {
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            if (json.RootElement.TryGetProperty("message", out var text)) message = text.GetString() ?? message;
            else if (json.RootElement.TryGetProperty("errors", out var errors))
                message = string.Join(" ", errors.EnumerateObject().SelectMany(e => e.Value.EnumerateArray()).Select(e => e.GetString()));
        } catch (JsonException) { }
        return new(response.StatusCode, message);
    }
}
