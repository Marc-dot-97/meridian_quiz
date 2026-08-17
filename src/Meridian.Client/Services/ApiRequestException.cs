using System.Net;

namespace Meridian.Client.Services;

public sealed class ApiRequestException(HttpStatusCode statusCode, string message) : Exception(message)
{
    public HttpStatusCode StatusCode { get; } = statusCode;

    public static async Task<ApiRequestException> FromResponseAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken = default)
    {
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        var message = string.IsNullOrWhiteSpace(body)
            ? $"The API returned {(int)response.StatusCode} {response.ReasonPhrase}."
            : body;
        return new ApiRequestException(response.StatusCode, message);
    }
}
