using System;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace TradeDeck.Atas.Strategy;

/// <summary>
/// WebSocket client to the bridge, with its own reconnection loop. The bridge is the only server on
/// the trading path; this side is a client that comes back on its own, exactly like the NinjaTrader
/// add-on it replaces. No handshake: connecting to the add-on port IS the registration.
/// </summary>
internal sealed class BridgeClient : IDisposable
{
    private readonly string _url;
    private readonly int _reconnectDelayMs;

    /// <summary>
    /// THE SEND LOCK IS NOT OPTIONAL, AND IT SURVIVED THE PORT.
    ///
    /// ClientWebSocket allows exactly ONE outstanding SendAsync on .NET 10, as it did on .NET
    /// Framework 4.8. What changed is the punishment: 4.8 aborted the socket, .NET 10 throws. The
    /// reason the guard exists has not changed at all — the state publisher's timer and the receive
    /// loop both send, and a lost frame is a lost order confirmation.
    ///
    /// EVERY DISPOSAL OF _ws TAKES THIS LOCK TOO, not just SendAsync. SendAsync captures its own
    /// local reference to the socket before awaiting the wire call; disposing that same object out
    /// from under an in-flight await — from Stop() or from the reconnect loop replacing a dead
    /// socket — throws ObjectDisposedException on the send path, silently swallowed by SendAsync's
    /// own catch, at exactly the moment (reconnect or shutdown) a lost frame matters most. See
    /// CloseSocket and DisposeSocketAsync.
    /// </summary>
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    /// <summary>
    /// Bound on how long a disposal path waits for the send lock. SendAsync's wire call uses
    /// CancellationToken.None (a WebSocket send cannot be safely cancelled mid-frame), so a send
    /// stuck against a dead TCP peer could hold the lock far longer than the caller expects.
    /// Waiting on it without a bound here would turn Stop() — called synchronously from
    /// OnStopping/OnSuspended — into a hung strategy shutdown, or stall the reconnect loop
    /// indefinitely. A bounded wait that expires is a diagnosable log line; an unbounded one is
    /// silence followed by "the deck stopped responding."
    /// </summary>
    private const int TeardownLockTimeoutMs = 2000;

    private ClientWebSocket? _ws;
    private CancellationTokenSource? _cts;
    private bool _running;
    private bool _disposed;

    public event Action<BridgeMessage>? MessageReceived;
    public event Action<bool>? ConnectionChanged;

    public bool IsConnected => _ws is { State: WebSocketState.Open };

    public BridgeClient(string url, int reconnectDelayMs)
    {
        _url = url;
        _reconnectDelayMs = reconnectDelayMs;
    }

    public void Start()
    {
        if (_running) return;
        _running = true;
        _cts = new CancellationTokenSource();
        _ = Task.Run(() => ConnectLoop(_cts.Token));
        SdLog.Info("Bridge client started, connecting to {0}", _url);
    }

    public void Stop()
    {
        _running = false;
        _cts?.Cancel();
        CloseSocket();
        SdLog.Info("Bridge client stopped");
    }

    private async Task ConnectLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                // Dispose the previous socket before replacing it, otherwise every reconnect leaks
                // a ClientWebSocket for the life of the session. Locked: this socket may have been
                // Open a moment ago, and a SendAsync started against it can still be awaiting the
                // wire call here.
                var previous = _ws;
                if (previous != null)
                {
                    _ws = null;
                    await DisposeSocketAsync(previous, "reconnect").ConfigureAwait(false);
                }

                var next = new ClientWebSocket();
                _ws = next;
                try
                {
                    await next.ConnectAsync(new Uri(_url), ct);
                }
                catch
                {
                    // Failed or was cancelled before the socket ever reached Open — SendAsync checks
                    // State before touching the socket, so nothing could have started sending on it.
                    // Safe to dispose directly, no send lock needed. Clear the field first so a
                    // concurrent Stop() does not try to close the same object a second time.
                    if (ReferenceEquals(_ws, next)) _ws = null;
                    try { next.Dispose(); } catch { /* already gone */ }
                    throw;
                }

                SdLog.Event("Connection", "Connected to bridge at {0}", _url);
                ConnectionChanged?.Invoke(true);

                await ReceiveLoop(ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                // Type included: a SocketException (bridge not started) and a WebSocketException
                // (bridge died mid-session) call for different fixes.
                SdLog.EventWarn("Connection", "Bridge connection lost: {0} — {1}", ex.GetType().Name, ex.Message);
                ConnectionChanged?.Invoke(false);
            }

            if (ct.IsCancellationRequested) break;

            SdLog.Info("Reconnecting in {0}ms...", _reconnectDelayMs);
            try { await Task.Delay(_reconnectDelayMs, ct); }
            catch (OperationCanceledException) { break; }
        }

        ConnectionChanged?.Invoke(false);
    }

    private async Task ReceiveLoop(CancellationToken ct)
    {
        var buffer = new byte[8192];
        var frame = new StringBuilder();

        while (_ws is { State: WebSocketState.Open } && !ct.IsCancellationRequested)
        {
            var result = await _ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct);

            if (result.MessageType == WebSocketMessageType.Close)
            {
                SdLog.Event("Connection", "Bridge closed the connection");
                break;
            }

            frame.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
            if (!result.EndOfMessage) continue;

            var json = frame.ToString();
            frame.Clear();

            try
            {
                var message = BridgeMessage.FromJson(json);
                if (message == null)
                {
                    SdLog.EventWarn("Wire", "Bridge frame ignored — not a JSON object: {0}", json);
                    continue;
                }

                SdLog.Debug("Received: {0} / {1} {2}", message.Type, message.Action, json);
                MessageReceived?.Invoke(message);
            }
            catch (Exception ex)
            {
                SdLog.Fail("Wire", ex, "Invalid frame from bridge: {0}", json);
            }
        }
    }

    public async Task SendAsync(BridgeMessage message)
    {
        var socket = _ws;
        if (socket is not { State: WebSocketState.Open })
        {
            SdLog.Warn("Cannot send — not connected to bridge");
            return;
        }

        await _sendLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (socket.State != WebSocketState.Open)
            {
                SdLog.Warn("Cannot send — socket closed while waiting for the send lock");
                return;
            }

            var json = message.ToJson();
            var bytes = Encoding.UTF8.GetBytes(json);
            await socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None)
                .ConfigureAwait(false);

            // State goes out twice a second and would bury everything else; it stays available one
            // level down, with the serialized frame for replay.
            if (message.Action == "stateUpdate")
                SdLog.TraceEvent("Wire", "Sent stateUpdate: {0}", json);
            else
                SdLog.Debug("Sent: {0} / {1} [REQ:{2}] {3}", message.Type, message.Action, message.RequestId ?? "n/a", json);
        }
        catch (Exception ex)
        {
            // Swallowed on purpose: every caller is either an event handler of the connector or a
            // fire-and-forget publish, and neither can do anything with the failure.
            SdLog.Fail("Wire", ex, "Failed to send {0}/{1} to bridge", message.Type, message.Action);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    /// <summary>
    /// Disposes a socket that may have been Open a moment ago, once no send is in flight on it —
    /// bounded, see TeardownLockTimeoutMs. Used by the reconnect loop, which runs on its own async
    /// task and can afford to await the lock.
    /// </summary>
    private async Task DisposeSocketAsync(ClientWebSocket socket, string reason)
    {
        var acquired = await _sendLock.WaitAsync(TeardownLockTimeoutMs).ConfigureAwait(false);
        if (!acquired)
        {
            SdLog.EventWarn("Connection",
                "Timed out after {0}ms waiting for an in-flight send before disposing the socket ({1}) — disposing anyway",
                TeardownLockTimeoutMs, reason);
        }

        try { socket.Dispose(); }
        catch { /* already gone */ }
        finally { if (acquired) _sendLock.Release(); }
    }

    /// <summary>
    /// Synchronous twin of DisposeSocketAsync for Stop(), which is called synchronously from
    /// OnStopping/OnSuspended and cannot await. Same bound, same reasoning.
    /// </summary>
    private void CloseSocket()
    {
        var socket = _ws;
        _ws = null;
        if (socket == null) return;

        var acquired = _sendLock.Wait(TeardownLockTimeoutMs);
        if (!acquired)
        {
            SdLog.EventWarn("Connection",
                "Timed out after {0}ms waiting for an in-flight send before closing the socket — closing anyway",
                TeardownLockTimeoutMs);
        }

        try
        {
            if (socket.State == WebSocketState.Open)
            {
                socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Shutting down", CancellationToken.None)
                    .GetAwaiter().GetResult();
            }
        }
        catch { /* the socket is going away either way */ }
        finally
        {
            try { socket.Dispose(); } catch { /* already gone */ }
            if (acquired) _sendLock.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        _cts?.Dispose();
        _sendLock.Dispose();
    }
}
