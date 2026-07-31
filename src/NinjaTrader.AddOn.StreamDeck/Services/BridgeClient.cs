using System;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NinjaTrader.NinjaScript.AddOns.StreamDeck.Models;
using NinjaTrader.NinjaScript.AddOns.StreamDeck.Utilities;

namespace NinjaTrader.NinjaScript.AddOns.StreamDeck.Services
{
    /// <summary>
    /// WebSocket client that connects to the bridge.
    /// Handles reconnection logic and message serialization.
    /// </summary>
    public class BridgeClient : IDisposable
    {
        private readonly AddOnConfig _config;

        // ClientWebSocket on .NET Framework 4.8 allows only ONE outstanding SendAsync:
        // a concurrent send throws InvalidOperationException and aborts the socket, which
        // silently loses order responses and forces a reconnect. The state publisher timer
        // thread and the receive thread both send, so every send must be serialized.
        private readonly SemaphoreSlim _sendLock = new SemaphoreSlim(1, 1);

        private ClientWebSocket _ws;
        private CancellationTokenSource _cts;
        private bool _disposed;
        private bool _running;

        public event Action<BridgeMessage> OnMessageReceived;
        public event Action<bool> OnConnectionChanged;

        public bool IsConnected
        {
            get { return _ws != null && _ws.State == WebSocketState.Open; }
        }

        public BridgeClient(AddOnConfig config)
        {
            _config = config;
        }

        public void Start()
        {
            if (_running) return;
            _running = true;
            _cts = new CancellationTokenSource();
            Task.Run(() => ConnectLoop(_cts.Token));
            SdLogger.Info("Bridge client started, connecting to {0}", _config.BridgeUrl);
        }

        public void Stop()
        {
            _running = false;
            _cts?.Cancel();
            CloseSocket();
            SdLogger.Info("Bridge client stopped");
        }

        private async Task ConnectLoop(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    // Dispose the previous socket before replacing it, otherwise every
                    // reconnect leaks a ClientWebSocket for the life of the session.
                    var previous = _ws;
                    if (previous != null)
                    {
                        _ws = null;
                        try { previous.Dispose(); } catch { }
                    }

                    _ws = new ClientWebSocket();
                    await _ws.ConnectAsync(new Uri(_config.BridgeUrl), ct);
                    SdLogger.Info("Connected to bridge at {0}", _config.BridgeUrl);
                    if (OnConnectionChanged != null) OnConnectionChanged(true);

                    await ReceiveLoop(ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    // Type included: a SocketException (bridge not started) and a
                    // WebSocketException (bridge died mid-session) call for different fixes.
                    SdLogger.EventWarn("Connection", "Bridge connection lost: {0} — {1}",
                        ex.GetType().Name, ex.Message);
                    if (OnConnectionChanged != null) OnConnectionChanged(false);
                }

                if (!ct.IsCancellationRequested)
                {
                    SdLogger.Info("Reconnecting in {0}ms...", _config.ReconnectDelayMs);
                    try { await Task.Delay(_config.ReconnectDelayMs, ct); }
                    catch (OperationCanceledException) { break; }
                }
            }

            if (OnConnectionChanged != null) OnConnectionChanged(false);
        }

        private async Task ReceiveLoop(CancellationToken ct)
        {
            var buffer = new byte[8192];
            var sb = new StringBuilder();

            while (_ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var result = await _ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    SdLogger.Info("Bridge closed the connection");
                    break;
                }

                sb.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));

                if (result.EndOfMessage)
                {
                    var json = sb.ToString();
                    sb.Clear();

                    try
                    {
                        var msg = BridgeMessage.FromJson(json);
                        if (msg != null)
                        {
                            SdLogger.Debug("Received: {0} / {1} {2}", msg.Type, msg.Action, json);
                            if (OnMessageReceived != null) OnMessageReceived(msg);
                        }
                        else
                        {
                            SdLogger.EventWarn("Wire", "Bridge frame ignored — not a JSON object: {0}", json);
                        }
                    }
                    catch (Exception ex)
                    {
                        SdLogger.Fail("Wire", ex, "Invalid JSON from bridge: {0}", json);
                    }
                }
            }
        }

        public async Task SendAsync(BridgeMessage message)
        {
            var socket = _ws;
            if (socket == null || socket.State != WebSocketState.Open)
            {
                SdLogger.Warn("Cannot send — not connected to bridge");
                return;
            }

            // Serialize every send: a second concurrent SendAsync would abort the socket.
            await _sendLock.WaitAsync().ConfigureAwait(false);
            try
            {
                if (socket.State != WebSocketState.Open)
                {
                    SdLogger.Warn("Cannot send — socket closed while waiting for the send lock");
                    return;
                }

                var json = message.ToJson();
                var bytes = Encoding.UTF8.GetBytes(json);
                await socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None)
                    .ConfigureAwait(false);

                // State is published several times a second and would bury everything else;
                // it stays available one level down, with the serialized frame for replay.
                if (message.Action == "stateUpdate")
                    SdLogger.TraceEvent("Wire", "Sent stateUpdate: {0}", json);
                else
                    SdLogger.Debug("Sent: {0} / {1} [REQ:{2}] {3}",
                        message.Type, message.Action, message.RequestId ?? "n/a", json);
            }
            catch (Exception ex)
            {
                SdLogger.Fail("Wire", ex, "Failed to send {0}/{1} to bridge",
                    message.Type, message.Action);
            }
            finally
            {
                _sendLock.Release();
            }
        }

        private void CloseSocket()
        {
            try
            {
                if (_ws != null && _ws.State == WebSocketState.Open)
                {
                    _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Shutting down", CancellationToken.None)
                        .GetAwaiter().GetResult();
                }
            }
            catch { }
            finally
            {
                if (_ws != null) _ws.Dispose();
                _ws = null;
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Stop();
            if (_cts != null) _cts.Dispose();
        }
    }
}
