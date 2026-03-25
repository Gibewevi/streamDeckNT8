using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using StreamDeckBridge.Models;

namespace StreamDeckBridge;

/// <summary>
/// WebSocket server that listens for both plugin and NT8 add-on connections.
/// Runs two HTTP listeners on separate ports.
/// </summary>
public sealed class BridgeServer : BackgroundService
{
    private readonly BridgeConfig _config;
    private readonly MessageRouter _router;
    private readonly StateManager _stateManager;
    private readonly ILogger<BridgeServer> _logger;

    private WebSocket? _pluginSocket;
    private WebSocket? _addonSocket;
    private readonly SemaphoreSlim _pluginSendLock = new(1, 1);
    private readonly SemaphoreSlim _addonSendLock = new(1, 1);

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    public BridgeServer(
        BridgeConfig config,
        MessageRouter router,
        StateManager stateManager,
        ILogger<BridgeServer> logger)
    {
        _config = config;
        _router = router;
        _stateManager = stateManager;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var pluginListener = new HttpListener();
        pluginListener.Prefixes.Add($"http://127.0.0.1:{_config.PluginPort}/");

        var addonListener = new HttpListener();
        addonListener.Prefixes.Add($"http://127.0.0.1:{_config.AddonPort}/");

        try
        {
            pluginListener.Start();
            addonListener.Start();

            _logger.LogInformation("Bridge started — Plugin port: {PluginPort}, Add-On port: {AddonPort}",
                _config.PluginPort, _config.AddonPort);

            if (_config.AllowLiveAccounts)
                _logger.LogWarning("⚠️  LIVE ACCOUNTS ENABLED — Safe mode is OFF");
            else
                _logger.LogInformation("Safe mode ON — Only Sim accounts allowed");

            // Run both listeners + state broadcast in parallel
            // Each task is wrapped to auto-restart on crash
            await Task.WhenAll(
                RunWithRestart(() => AcceptPluginConnections(pluginListener, stoppingToken), "PluginListener", stoppingToken),
                RunWithRestart(() => AcceptAddonConnections(addonListener, stoppingToken), "AddonListener", stoppingToken),
                RunWithRestart(() => BroadcastStateLoop(stoppingToken), "BroadcastLoop", stoppingToken)
            );
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Bridge shutting down...");
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "Bridge fatal error");
        }
        finally
        {
            pluginListener.Stop();
            addonListener.Stop();
        }
    }

    /// <summary>
    /// Wraps a task so that if it crashes unexpectedly, it auto-restarts
    /// instead of killing the entire bridge process.
    /// </summary>
    private async Task RunWithRestart(Func<Task> taskFactory, string name, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await taskFactory();
                return; // Normal exit
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Task {Name} crashed — restarting in 2s", name);
                try { await Task.Delay(2000, ct); }
                catch (OperationCanceledException) { throw; }
            }
        }
    }

    private async Task AcceptPluginConnections(HttpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var context = await listener.GetContextAsync().WaitAsync(ct);
                if (!context.Request.IsWebSocketRequest)
                {
                    context.Response.StatusCode = 400;
                    context.Response.Close();
                    continue;
                }

                var wsContext = await context.AcceptWebSocketAsync(null);
                _pluginSocket = wsContext.WebSocket;
                _stateManager.SetPluginConnected(true);
                _logger.LogInformation("Stream Deck plugin connected");

                await HandlePluginSession(_pluginSocket, ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in plugin connection handler");
                _stateManager.SetPluginConnected(false);
                await Task.Delay(1000, ct);
            }
        }
    }

    private async Task AcceptAddonConnections(HttpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var context = await listener.GetContextAsync().WaitAsync(ct);
                if (!context.Request.IsWebSocketRequest)
                {
                    context.Response.StatusCode = 400;
                    context.Response.Close();
                    continue;
                }

                var wsContext = await context.AcceptWebSocketAsync(null);
                _addonSocket = wsContext.WebSocket;
                _stateManager.SetNtConnected(true);
                _logger.LogInformation("NinjaTrader Add-On connected");

                await HandleAddonSession(_addonSocket, ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in add-on connection handler");
                _stateManager.SetNtConnected(false);
                await Task.Delay(1000, ct);
            }
        }
    }

    private async Task HandlePluginSession(WebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[8192];
        try
        {
            while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                if (result.MessageType == WebSocketMessageType.Close)
                    break;

                var json = Encoding.UTF8.GetString(buffer, 0, result.Count);
                _logger.LogDebug("Plugin → Bridge: {Json}", json);

                BridgeMessage? msg;
                try
                {
                    msg = JsonSerializer.Deserialize<BridgeMessage>(json, JsonOpts);
                }
                catch (JsonException ex)
                {
                    _logger.LogWarning("Invalid JSON from plugin: {Error}", ex.Message);
                    var errMsg = BridgeMessage.CreateError(null, "unknown", "INVALID_PAYLOAD", "Malformed JSON.");
                    await SendToPlugin(errMsg, ct);
                    continue;
                }

                if (msg == null) continue;

                var (localResponse, shouldForward, enrichedMessage) = _router.ProcessPluginCommand(msg);

                if (localResponse != null)
                {
                    await SendToPlugin(localResponse, ct);

                    // For qty/instrument changes, also broadcast updated state
                    if (msg.Action is "qtySet" or "qtyAdjust" or "qtyReset" or "setInstrument" or "setAccount")
                    {
                        await BroadcastState(ct);
                    }
                }

                if (shouldForward && enrichedMessage != null)
                {
                    if (_addonSocket?.State == WebSocketState.Open)
                    {
                        await SendToAddon(enrichedMessage, ct);
                    }
                    else
                    {
                        var err = BridgeMessage.CreateError(msg.RequestId, msg.Action, "NT_DISCONNECTED", "NinjaTrader is not connected.");
                        await SendToPlugin(err, ct);
                    }
                }
            }
        }
        catch (WebSocketException ex)
        {
            _logger.LogWarning("Plugin WebSocket error: {Msg}", ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Plugin session unexpected error");
        }
        finally
        {
            _stateManager.SetPluginConnected(false);
            _logger.LogInformation("Stream Deck plugin disconnected");
        }
    }

    private async Task HandleAddonSession(WebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[8192];
        try
        {
            while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                if (result.MessageType == WebSocketMessageType.Close)
                    break;

                var json = Encoding.UTF8.GetString(buffer, 0, result.Count);
                _logger.LogDebug("AddOn → Bridge: {Json}", json);

                BridgeMessage? msg;
                try
                {
                    msg = JsonSerializer.Deserialize<BridgeMessage>(json, JsonOpts);
                }
                catch (JsonException ex)
                {
                    _logger.LogWarning("Invalid JSON from add-on: {Error}", ex.Message);
                    continue;
                }

                if (msg == null) continue;

                var toPlugin = _router.ProcessAddonMessage(msg);
                if (toPlugin != null)
                {
                    await SendToPlugin(toPlugin, ct);
                }
            }
        }
        catch (WebSocketException ex)
        {
            _logger.LogWarning("Add-On WebSocket error: {Msg}", ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Add-On session unexpected error");
        }
        finally
        {
            _stateManager.SetNtConnected(false);
            _logger.LogInformation("NinjaTrader Add-On disconnected");
        }
    }

    private async Task BroadcastStateLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(_config.StateUpdateIntervalMs, ct);
            try
            {
                await BroadcastState(ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger.LogWarning("BroadcastState error (non-fatal): {Msg}", ex.Message);
            }
        }
    }

    private async Task BroadcastState(CancellationToken ct)
    {
        var socket = _pluginSocket;
        if (socket?.State != WebSocketState.Open) return;

        var state = _stateManager.GetSnapshot();
        var evt = BridgeMessage.CreateEvent("stateUpdate", state);
        await SendToPlugin(evt, ct);
    }

    private async Task SendToPlugin(BridgeMessage msg, CancellationToken ct)
    {
        var socket = _pluginSocket;
        if (socket?.State != WebSocketState.Open) return;
        await _pluginSendLock.WaitAsync(ct);
        try
        {
            if (socket.State != WebSocketState.Open) return;
            var json = JsonSerializer.Serialize(msg, JsonOpts);
            var bytes = Encoding.UTF8.GetBytes(json);
            await socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, ct);
            _logger.LogDebug("Bridge → Plugin: {Json}", json);
        }
        catch (WebSocketException ex)
        {
            _logger.LogWarning("SendToPlugin failed (socket closed): {Msg}", ex.Message);
        }
        finally
        {
            _pluginSendLock.Release();
        }
    }

    private async Task SendToAddon(BridgeMessage msg, CancellationToken ct)
    {
        var socket = _addonSocket;
        if (socket?.State != WebSocketState.Open) return;
        await _addonSendLock.WaitAsync(ct);
        try
        {
            if (socket.State != WebSocketState.Open) return;
            var json = JsonSerializer.Serialize(msg, JsonOpts);
            var bytes = Encoding.UTF8.GetBytes(json);
            await socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, ct);
            _logger.LogDebug("Bridge → AddOn: {Json}", json);
        }
        catch (WebSocketException ex)
        {
            _logger.LogWarning("SendToAddon failed (socket closed): {Msg}", ex.Message);
        }
        finally
        {
            _addonSendLock.Release();
        }
    }
}
