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
                    SdLogger.Warn("Bridge connection lost: {0}", ex.Message);
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
                            SdLogger.Debug("Received: {0} / {1}", msg.Type, msg.Action);
                            if (OnMessageReceived != null) OnMessageReceived(msg);
                        }
                    }
                    catch (Exception ex)
                    {
                        SdLogger.Warn("Invalid JSON from bridge: {0}", ex.Message);
                    }
                }
            }
        }

        public async Task SendAsync(BridgeMessage message)
        {
            if (_ws == null || _ws.State != WebSocketState.Open)
            {
                SdLogger.Warn("Cannot send — not connected to bridge");
                return;
            }

            try
            {
                var json = message.ToJson();
                var bytes = Encoding.UTF8.GetBytes(json);
                await _ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
                SdLogger.Debug("Sent: {0} / {1} [REQ:{2}]", message.Type, message.Action, message.RequestId ?? "n/a");
            }
            catch (Exception ex)
            {
                SdLogger.Error(ex, "Failed to send message to bridge");
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
