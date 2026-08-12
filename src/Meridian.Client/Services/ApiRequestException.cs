using System.Net;
using System.Text.Json;

namespace Meridian.Client.Services;

public sealed class ApiRequestException : Exception
{
    public HttpStatusCode StatusCode { get; }

    public ApiRequestException(HttpStatusCode statusCode, string message)
        : base(message) => StatusCode = statusCode;

    public static async Task<ApiRequestException> FromResponseAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken = default)
    {
        var message = $"The server returned {(int)response.StatusCode} {response.ReasonPhrase}.";
        try
        {
            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!string.IsNullOrWhiteSpace(json))
            {
                using var document = JsonDocument.Parse(json);
                var root = document.RootElement;
                if (root.TryGetProperty("detail", out var detail) && detail.ValueKind == JsonValueKind.String)
                    message = detail.GetString() ?? message;
                else if (root.TryGetProperty("title", out var title) && title.ValueKind == JsonValueKind.String)
                    message = title.GetString() ?? message;
            }
        }
        catch (JsonException) { }

        return new ApiRequestException(response.StatusCode, message);
    }
}
