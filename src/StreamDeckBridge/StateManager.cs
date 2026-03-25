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
    private DateTime _accountSetAt = DateTime.MinValue;
    private DateTime _instrumentSetAt = DateTime.MinValue;
    private static readonly TimeSpan OverrideGuard = TimeSpan.FromSeconds(5);

    private bool _cooldownEnabled;
    private DateTime? _cooldownUntil;
    private bool _previousPositionExists;
    private double _previousUnrealizedPnl;

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
            var cooldownActive = _cooldownUntil.HasValue && DateTime.UtcNow < _cooldownUntil.Value;
            var cooldownRemaining = cooldownActive
                ? (int)Math.Ceiling((_cooldownUntil!.Value - DateTime.UtcNow).TotalSeconds)
                : 0;

            return new TradingState
            {
                Account = _state.Account,
                Instrument = _state.Instrument,
                Quantity = _state.Quantity,
                DefaultQuantity = _state.DefaultQuantity,
                NtConnected = _state.NtConnected,
                PluginConnected = _state.PluginConnected,
                Position = _state.Position,
                InstrumentInfo = _state.InstrumentInfo,
                AvailableAccounts = new List<string>(_state.AvailableAccounts),
                CooldownEnabled = _cooldownEnabled,
                CooldownActive = cooldownActive,
                CooldownSecondsRemaining = cooldownRemaining
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

    public string SetAccount(string account)
    {
        lock (_lock)
        {
            _state.Account = account;
            _accountSetAt = DateTime.UtcNow;
            _logger.LogInformation("Account set to {Account} (guarded for {Secs}s)", account, OverrideGuard.TotalSeconds);
            return _state.Account;
        }
    }

    public string SetInstrument(string instrument)
    {
        lock (_lock)
        {
            _state.Instrument = instrument;
            _instrumentSetAt = DateTime.UtcNow;
            _logger.LogInformation("Instrument set to {Instrument} (guarded for {Secs}s)", instrument, OverrideGuard.TotalSeconds);
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
            // Save previous position state before updating
            _previousPositionExists = _state.Position?.Exists ?? false;
            _previousUnrealizedPnl = _state.Position?.UnrealizedPnl ?? 0;

            if (statePayload.TryGetProperty("position", out var pos))
            {
                _state.Position = JsonSerializer.Deserialize<PositionState>(pos.GetRawText(), CamelCase);

                // Detect position closed with a loss → trigger cooldown
                var currentExists = _state.Position?.Exists ?? false;
                if (_cooldownEnabled && _previousPositionExists && !currentExists && _previousUnrealizedPnl < 0)
                {
                    _cooldownUntil = DateTime.UtcNow.AddSeconds(60);
                    _logger.LogWarning("Cooldown activated for 60s after losing trade (PnL: {Pnl})", _previousUnrealizedPnl);
                }
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
            // Update available accounts list from NT8
            if (statePayload.TryGetProperty("availableAccounts", out var accts) && accts.ValueKind == JsonValueKind.Array)
            {
                _state.AvailableAccounts.Clear();
                foreach (var a in accts.EnumerateArray())
                {
                    var name = a.GetString();
                    if (!string.IsNullOrEmpty(name))
                        _state.AvailableAccounts.Add(name);
                }
            }
            // Update account name from NT8 — but not if user recently changed it via setAccount
            var accountGuarded = (DateTime.UtcNow - _accountSetAt) < OverrideGuard;
            if (!accountGuarded && statePayload.TryGetProperty("account", out var acctObj) && acctObj.ValueKind == JsonValueKind.Object)
            {
                if (acctObj.TryGetProperty("name", out var acctName) && acctName.ValueKind == JsonValueKind.String)
                {
                    var name = acctName.GetString();
                    if (!string.IsNullOrEmpty(name))
                        _state.Account = name;
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

    public bool ToggleCooldown()
    {
        lock (_lock)
        {
            _cooldownEnabled = !_cooldownEnabled;
            if (!_cooldownEnabled)
            {
                // Toggling OFF — cancel any active cooldown
                _cooldownUntil = null;
            }
            _logger.LogInformation("Cooldown {State}", _cooldownEnabled ? "ENABLED" : "DISABLED");
            return _cooldownEnabled;
        }
    }

    public bool IsOrderBlocked(string action)
    {
        lock (_lock)
        {
            var cooldownActive = _cooldownUntil.HasValue && DateTime.UtcNow < _cooldownUntil.Value;
            if (!cooldownActive) return false;

            // Allow protective/management actions even during cooldown
            return action is "buyMarket" or "sellMarket" or "buyLimit" or "sellLimit" or "reverse";
        }
    }

    private static bool IsOrderAction(string action) =>
        action is "buyMarket" or "sellMarket" or "buyLimit" or "sellLimit";
}
