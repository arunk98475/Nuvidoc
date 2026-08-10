using Microsoft.AspNetCore.SignalR.Client;
using Docovee.DS.Models;

namespace nuvidoc.Services;

/// <summary>
/// SignalR listener for booking updates. Later FCM receive can call the same <see cref="IBookingAlertHandler"/>.
/// </summary>
public sealed class SignalRBookingPushClient : IAsyncDisposable
{
    private readonly IBookingAlertHandler _handler;
    private HubConnection? _connection;
    private Guid? _joinedSession;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public SignalRBookingPushClient(IBookingAlertHandler handler) => _handler = handler;

    public bool IsConnected => _connection?.State == HubConnectionState.Connected;

    public async Task EnsureConnectedAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_connection is { State: HubConnectionState.Connected })
                return;

            await DisposeConnectionUnlockedAsync();

            var hubUrl = ApiConfig.BaseUrl.TrimEnd('/') + "/hubs/patient-notifications";
            var builder = new HubConnectionBuilder()
                .WithUrl(hubUrl, options =>
                {
                    options.AccessTokenProvider = () => Task.FromResult(AuthSession.AccessToken);
                })
                .WithAutomaticReconnect();

            _connection = builder.Build();
            _connection.On<PatientPushMessage>(PatientPushClientMethods.BookingUpdated, async message =>
            {
                try
                {
                    await _handler.HandleAsync(message);
                }
                catch
                {
                    // Swallow UI handler errors so the hub stays alive.
                }
            });

            _connection.Reconnected += async _ =>
            {
                try { await JoinGroupsAsync(CancellationToken.None); }
                catch { /* ignore */ }
            };

            await _connection.StartAsync(cancellationToken);
            await JoinGroupsAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task JoinSessionAsync(Guid sessionKey, CancellationToken cancellationToken = default)
    {
        _joinedSession = sessionKey;
        await EnsureConnectedAsync(cancellationToken);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_connection?.State == HubConnectionState.Connected)
                await _connection.InvokeAsync("JoinSession", sessionKey.ToString("D"), cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task DisconnectAsync()
    {
        await _gate.WaitAsync();
        try
        {
            _joinedSession = null;
            await DisposeConnectionUnlockedAsync();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync();
        _gate.Dispose();
    }

    private async Task JoinGroupsAsync(CancellationToken cancellationToken)
    {
        if (_connection?.State != HubConnectionState.Connected)
            return;

        if (AuthSession.IsSignedIn)
        {
            try { await _connection.InvokeAsync("JoinPatient", cancellationToken); }
            catch { /* guest / claim missing */ }
        }

        if (_joinedSession is Guid key)
        {
            try { await _connection.InvokeAsync("JoinSession", key.ToString("D"), cancellationToken); }
            catch { /* ignore */ }
        }
    }

    private async Task DisposeConnectionUnlockedAsync()
    {
        if (_connection == null)
            return;
        try
        {
            await _connection.DisposeAsync();
        }
        catch
        {
            // ignore
        }
        _connection = null;
    }
}
