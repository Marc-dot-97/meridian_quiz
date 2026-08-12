using Meridian.Shared.DTOs;
using Microsoft.AspNetCore.Components.WebAssembly.Authentication;
using Microsoft.AspNetCore.SignalR.Client;

namespace Meridian.Client.Services;

public sealed class LeaderboardSignalRService(
    IConfiguration configuration,
    IServiceProvider services) : IAsyncDisposable
{
    private readonly bool _useLocalMode = configuration.GetValue<bool>("Development:UseLocalMode");
    private HubConnection? _connection;
    private bool _localConnected;

    public event Action<IReadOnlyList<LeaderboardEntryDto>>? LeaderboardUpdated;
    public bool IsConnected => _useLocalMode ? _localConnected : _connection?.State == HubConnectionState.Connected;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_useLocalMode)
        {
            _localConnected = true;
            return;
        }

        if (_connection is not null)
        {
            if (_connection.State == HubConnectionState.Disconnected)
                await _connection.StartAsync(cancellationToken);
            return;
        }

        var apiBaseUrl = configuration["Api:BaseUrl"]
            ?? throw new InvalidOperationException("Configuration value 'Api:BaseUrl' is missing.");
        var apiScope = configuration["Api:Scope"]
            ?? throw new InvalidOperationException("Configuration value 'Api:Scope' is missing.");
        var tokenProvider = services.GetRequiredService<IAccessTokenProvider>();
        var hubUrl = new Uri(new Uri(apiBaseUrl, UriKind.Absolute), "hubs/quiz");

        _connection = new HubConnectionBuilder()
            .WithUrl(hubUrl, options =>
            {
                options.AccessTokenProvider = async () =>
                {
                    var tokenResult = await tokenProvider.RequestAccessToken(
                        new AccessTokenRequestOptions { Scopes = [apiScope] });
                    return tokenResult.TryGetToken(out var token) ? token.Value : null;
                };
            })
            .WithAutomaticReconnect()
            .Build();

        _connection.On<IReadOnlyList<LeaderboardEntryDto>>(
            "LeaderboardUpdated",
            entries => LeaderboardUpdated?.Invoke(entries));

        await _connection.StartAsync(cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (_connection is not null)
            await _connection.DisposeAsync();
    }
}
