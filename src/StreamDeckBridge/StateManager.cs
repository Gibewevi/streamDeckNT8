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
    private readonly SafetyMacro _safety;
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
    private string _previousPositionInstrument = string.Empty;

    // False while NT8 could not resolve the account/instrument, which is NOT the same as "flat".
    // Conflating the two fabricated trades: a single tick with an unresolved context cleared the
    // position, and the next tick then read a flat→open transition on a position that had never
    // moved — inflating the count that gates SAFETY_TRADE_LIMIT_REACHED. Starts false so an
    // already-open position at startup is never counted as a new trade.
    private bool _previousPositionKnown;

    public StateManager(BridgeConfig config, SafetyMacro safety, ILogger<StateManager> logger)
    {
        _config = config;
        _safety = safety;
        _logger = logger;
        _state = new TradingState
        {
            Account = config.DefaultAccount,
            Instrument = config.DefaultInstrument,
            Quantity = config.DefaultQuantity,
            DefaultQuantity = config.DefaultQuantity
        };

        _sessionPath = ResolveSessionPath(config);

        var savedInstrument = LoadSavedInstrument();
        if (!string.IsNullOrWhiteSpace(savedInstrument))
        {
            _state.Instrument = savedInstrument;
            _logger.LogInformation("Restored selected instrument: {Instrument}", savedInstrument);
        }
    }

    private readonly string _sessionPath;

    private static string ResolveSessionPath(BridgeConfig config)
    {
        if (!string.IsNullOrWhiteSpace(config.SessionStatePath))
            return config.SessionStatePath;

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "StreamDeckTrader", "session.json");
    }

    private string? LoadSavedInstrument()
    {
        try
        {
            if (!File.Exists(_sessionPath)) return null;

            using var doc = JsonDocument.Parse(File.ReadAllText(_sessionPath));
            if (doc.RootElement.TryGetProperty("instrument", out var value) && value.ValueKind == JsonValueKind.String)
                return value.GetString();
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Could not read the session file {Path}: {Error}", _sessionPath, ex.Message);
        }

        return null;
    }

    /// <summary>
    /// Remembers the selected instrument so a restart does not silently fall back to
    /// another one. Called whenever the trader picks an instrument on the deck.
    /// </summary>
    private void SaveSelectedInstrument(string instrument)
    {
        try
        {
            var dir = Path.GetDirectoryName(_sessionPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(_sessionPath, JsonSerializer.Serialize(new { instrument }));
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Could not persist the selected instrument: {Error}", ex.Message);
        }
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
                CooldownSecondsRemaining = cooldownRemaining,
                Safety = _safety.GetStatus()
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
            SaveSelectedInstrument(instrument);
            _logger.LogInformation("Instrument set to {Instrument} (guarded for {Secs}s)", instrument, OverrideGuard.TotalSeconds);
            return _state.Instrument;
        }
    }

    public void SetNtConnected(bool connected)
    {
        lock (_lock)
        {
            _state.NtConnected = connected;
            if (!connected)
            {
                _state.Account = string.Empty;
                _state.AvailableAccounts.Clear();
                _state.Position = null;
                _state.InstrumentInfo = null;
            }

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
            var previousKnown = _previousPositionKnown;

            // Account-wide P&L feeds the safety macro's loss rules
            UpdateSafetyPnl(statePayload);

            if (!statePayload.TryGetProperty("position", out var pos) || pos.ValueKind == JsonValueKind.Null)
            {
                // NT8 omits "position" when the account or instrument cannot be resolved.
                // Keeping the last value would leave a phantom position on the deck whose
                // Close would fail with INSTRUMENT_NOT_FOUND — drop it instead.
                if (_state.Position != null)
                    _logger.LogInformation("Position cleared — NT8 published no position for the tracked context");

                _state.Position = null;
                _previousPositionInstrument = ReadInstrumentName(statePayload);
                _previousPositionKnown = false;
            }
            else
            {
                var previousInstrument = _previousPositionInstrument;
                _previousPositionInstrument = ReadInstrumentName(statePayload);

                _state.Position = JsonSerializer.Deserialize<PositionState>(pos.GetRawText(), CamelCase);
                var currentExists = _state.Position?.Exists ?? false;

                // Transitions are only meaningful when the PREVIOUS tick actually knew the
                // position. After a tick with an unresolved context we know nothing about what
                // happened in between, so we resynchronise silently rather than invent an
                // open or a close that never occurred.
                if (previousKnown)
                {
                    // Detect position closed with a loss → trigger cooldown
                    if (_cooldownEnabled && _previousPositionExists && !currentExists && _previousUnrealizedPnl < 0)
                    {
                        _cooldownUntil = DateTime.UtcNow.AddSeconds(60);
                        _logger.LogWarning("Cooldown activated for 60s after losing trade (PnL: {Pnl})", _previousUnrealizedPnl);
                    }

                    // Flat → open on the same instrument counts as one trade for the safety macro.
                    // Requiring the same instrument avoids counting a phantom trade when the
                    // tracked instrument switches to one that already has an open position.
                    if (!_previousPositionExists && currentExists &&
                        string.Equals(previousInstrument, _previousPositionInstrument, StringComparison.OrdinalIgnoreCase))
                    {
                        _safety.RecordTradeOpened();
                    }
                }

                _previousPositionKnown = true;
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

    /// <summary>
    /// Forwards the account-wide P&amp;L (realized + unrealized) published by NT8 to the safety macro.
    /// Silently skipped when NT8 could not read it, which the macro reports as pnlAvailable=false.
    /// </summary>
    private void UpdateSafetyPnl(JsonElement statePayload)
    {
        if (!statePayload.TryGetProperty("account", out var account) || account.ValueKind != JsonValueKind.Object)
            return;

        if (account.TryGetProperty("pnlAvailable", out var available) && available.ValueKind == JsonValueKind.False)
            return;

        var realized = GetDouble(account, "realizedPnl");
        var unrealized = GetDouble(account, "unrealizedPnl");
        if (realized == null && unrealized == null)
            return;

        _safety.UpdatePnl((realized ?? 0) + (unrealized ?? 0));
    }

    private static string ReadInstrumentName(JsonElement statePayload)
    {
        if (statePayload.TryGetProperty("instrument", out var instrument) &&
            instrument.ValueKind == JsonValueKind.Object &&
            instrument.TryGetProperty("name", out var name) &&
            name.ValueKind == JsonValueKind.String)
        {
            return name.GetString() ?? string.Empty;
        }

        return string.Empty;
    }

    private static double? GetDouble(JsonElement element, string key)
    {
        if (element.TryGetProperty(key, out var prop) && prop.ValueKind == JsonValueKind.Number)
            return prop.GetDouble();
        return null;
    }

    private static bool IsOrderAction(string action) =>
        action is "buyMarket" or "sellMarket" or "buyLimit" or "sellLimit";
}
