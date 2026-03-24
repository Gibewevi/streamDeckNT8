using System.Text.Json;
using Microsoft.Extensions.Logging;
using StreamDeckBridge.Models;

namespace StreamDeckBridge;

/// <summary>
/// Manages the shared trading state: quantity, instrument, account.
/// Thread-safe via locking.
/// </summary>
public sealed class StateManager
{
    private readonly BridgeConfig _config;
    private readonly ILogger<StateManager> _logger;
    private readonly object _lock = new();
    private readonly TradingState _state;

    public StateManager(BridgeConfig config, ILogger<StateManager> logger)
    {
        _config = config;
        _logger = logger;
        _state = new TradingState
        {
            Account = config.DefaultAccount,
            Instrument = config.DefaultInstrument,
            Quantity = config.DefaultQuantity,
            DefaultQuantity = config.DefaultQuantity
        };
    }

    public TradingState GetSnapshot()
    {
        lock (_lock)
        {
            return new TradingState
            {
                Account = _state.Account,
                Instrument = _state.Instrument,
                Quantity = _state.Quantity,
                DefaultQuantity = _state.DefaultQuantity,
                NtConnected = _state.NtConnected,
                PluginConnected = _state.PluginConnected,
                Position = _state.Position,
                InstrumentInfo = _state.InstrumentInfo
            };
        }
    }

    public int SetQuantity(int quantity)
    {
        lock (_lock)
        {
            _state.Quantity = Math.Clamp(quantity, _config.MinQuantity, _config.MaxQuantity);
            _logger.LogInformation("Quantity set to {Qty}", _state.Quantity);
            return _state.Quantity;
        }
    }

    public int AdjustQuantity(int delta)
    {
        lock (_lock)
        {
            var newQty = Math.Clamp(_state.Quantity + delta, _config.MinQuantity, _config.MaxQuantity);
            _state.Quantity = newQty;
            _logger.LogInformation("Quantity adjusted by {Delta}, now {Qty}", delta, _state.Quantity);
            return _state.Quantity;
        }
    }

    public int ResetQuantity()
    {
        lock (_lock)
        {
            _state.Quantity = _state.DefaultQuantity;
            _logger.LogInformation("Quantity reset to default {Qty}", _state.Quantity);
            return _state.Quantity;
        }
    }

    public string SetInstrument(string instrument)
    {
        lock (_lock)
        {
            _state.Instrument = instrument;
            _logger.LogInformation("Instrument set to {Instrument}", instrument);
            return _state.Instrument;
        }
    }

    public void SetNtConnected(bool connected)
    {
        lock (_lock)
        {
            _state.NtConnected = connected;
            _logger.LogInformation("NT8 connection: {Status}", connected ? "CONNECTED" : "DISCONNECTED");
        }
    }

    public void SetPluginConnected(bool connected)
    {
        lock (_lock)
        {
            _state.PluginConnected = connected;
            _logger.LogInformation("Plugin connection: {Status}", connected ? "CONNECTED" : "DISCONNECTED");
        }
    }

    private static readonly JsonSerializerOptions CamelCase = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public void UpdateFromNtState(JsonElement statePayload)
    {
        lock (_lock)
        {
            if (statePayload.TryGetProperty("position", out var pos))
            {
                _state.Position = JsonSerializer.Deserialize<PositionState>(pos.GetRawText(), CamelCase);
            }
            if (statePayload.TryGetProperty("instrument", out var inst))
            {
                _state.InstrumentInfo = JsonSerializer.Deserialize<InstrumentInfo>(inst.GetRawText(), CamelCase);
                // Also update the tracked instrument name from NT8 if bridge has none set
                if (_state.InstrumentInfo != null && !string.IsNullOrEmpty(_state.InstrumentInfo.Name)
                    && string.IsNullOrEmpty(_state.Instrument))
                {
                    _state.Instrument = _state.InstrumentInfo.Name;
                    _logger.LogInformation("Instrument auto-detected from NT8: {Instrument}", _state.Instrument);
                }
            }
            // Update NT connected status from addon heartbeat
            if (statePayload.TryGetProperty("connected", out var conn) && conn.ValueKind == JsonValueKind.True)
            {
                _state.NtConnected = true;
            }
        }
    }

    /// <summary>
    /// Enriches a command payload with current state defaults where fields are missing.
    /// Returns a new JsonElement with enriched data.
    /// </summary>
    public JsonElement EnrichPayload(BridgeMessage message)
    {
        lock (_lock)
        {
            var dict = new Dictionary<string, object>();

            if (message.Payload is JsonElement el && el.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in el.EnumerateObject())
                {
                    dict[prop.Name] = prop.Value.Clone();
                }
            }

            // Add defaults for missing fields
            if (!dict.ContainsKey("account"))
                dict["account"] = _state.Account;
            if (!dict.ContainsKey("instrument"))
                dict["instrument"] = _state.Instrument;
            if (!dict.ContainsKey("quantity") && IsOrderAction(message.Action))
                dict["quantity"] = _state.Quantity;

            return JsonSerializer.SerializeToElement(dict);
        }
    }

    private static bool IsOrderAction(string action) =>
        action is "buyMarket" or "sellMarket" or "buyLimit" or "sellLimit";
}
