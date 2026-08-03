using System.Text.Json;
using Microsoft.Extensions.Logging;
using StreamDeckBridge.Models;

namespace StreamDeckBridge;

/// <summary>
/// Validates incoming messages against the V1 protocol rules.
/// </summary>
public sealed class MessageValidator
{
    private readonly BridgeConfig _config;
    private readonly ILogger<MessageValidator> _logger;

    private static readonly HashSet<string> KnownActions = new(StringComparer.OrdinalIgnoreCase)
    {
        "buyMarket", "sellMarket", "buyLimit", "sellLimit",
        "flatten", "cancelOrders", "cancelWorkingOrders", "reverse",
        "breakeven", "moveStop", "moveTarget",
        "qtySet", "qtyAdjust", "qtyReset",
        "setInstrument", "setAccount", "getState", "toggleCooldown", "configureCooldown",
        "armSafety", "disarmSafety", "toggleSafety", "configureSafety"
    };

    private static readonly HashSet<string> PositionRequiredActions = new(StringComparer.OrdinalIgnoreCase)
    {
        "breakeven", "moveStop", "moveTarget", "reverse"
    };

    private static readonly HashSet<string> QuantityActions = new(StringComparer.OrdinalIgnoreCase)
    {
        "qtySet", "qtyAdjust", "qtyReset"
    };

    public MessageValidator(BridgeConfig config, ILogger<MessageValidator> logger)
    {
        _config = config;
        _logger = logger;
    }

    public (bool IsValid, string? ErrorCode, string? ErrorMessage) Validate(BridgeMessage message)
    {
        if (message.Version != "1.0")
            return (false, "UNSUPPORTED_VERSION", $"Version '{message.Version}' is not supported. Expected '1.0'.");

        if (string.IsNullOrWhiteSpace(message.Type))
            return (false, "INVALID_PAYLOAD", "Message type is required.");

        if (message.Type != "command")
            return (false, "INVALID_PAYLOAD", $"Bridge only accepts 'command' type messages from plugin. Got '{message.Type}'.");

        if (string.IsNullOrWhiteSpace(message.RequestId))
            return (false, "INVALID_PAYLOAD", "requestId is required for commands.");

        if (string.IsNullOrWhiteSpace(message.Action))
            return (false, "INVALID_PAYLOAD", "action is required.");

        if (!KnownActions.Contains(message.Action))
            return (false, "UNSUPPORTED_ACTION", $"Unknown action '{message.Action}'.");

        // Quantity-only actions don't need account/instrument validation
        if (QuantityActions.Contains(message.Action))
            return ValidateQuantityAction(message);

        if (message.Action == "setInstrument")
            return ValidateRequiredPayloadString(message, "instrument", "instrument is required for setInstrument.");

        if (message.Action == "setAccount")
            return ValidateRequiredPayloadString(message, "account", "account is required for setAccount.");

        if (message.Action is "getState" or "toggleCooldown" or "armSafety" or "disarmSafety" or "toggleSafety")
            return (true, null, null);

        if (message.Action == "configureSafety")
            return ValidateSafetyConfig(message);

        if (message.Action == "configureCooldown")
            return ValidateCooldownConfig(message);

        // All trading actions need instrument context at minimum
        return ValidateTradingAction(message);
    }

    private (bool, string?, string?) ValidateQuantityAction(BridgeMessage message)
    {
        if (message.Action == "qtySet")
        {
            var qty = GetPayloadInt(message, "quantity");
            if (qty == null || qty < _config.MinQuantity || qty > _config.MaxQuantity)
                return (false, "INVALID_QUANTITY", $"Quantity must be a whole number between {_config.MinQuantity} and {_config.MaxQuantity}.");
        }
        else if (message.Action == "qtyAdjust")
        {
            var delta = GetPayloadInt(message, "delta");
            if (delta == null)
                return (false, "INVALID_PAYLOAD", "delta is required for qtyAdjust.");
        }

        return (true, null, null);
    }

    private (bool, string?, string?) ValidateTradingAction(BridgeMessage message)
    {
        var account = GetPayloadString(message, "account");
        var instrument = GetPayloadString(message, "instrument");

        if (string.IsNullOrWhiteSpace(account))
            return (false, "INVALID_PAYLOAD", "account is required for trading actions.");

        if (string.IsNullOrWhiteSpace(instrument))
            return (false, "INVALID_PAYLOAD", "instrument is required for trading actions.");

        // Safe mode: block live accounts unless explicitly allowed
        if (!_config.AllowLiveAccounts && !account.StartsWith("Sim", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("[REQ:{RequestId}] BLOCKED: Live account '{Account}' not allowed in safe mode", message.RequestId, account);
            return (false, "LIVE_ACCOUNT_BLOCKED", $"Live account '{account}' is blocked. Safe mode only allows Sim accounts.");
        }

        // Validate quantity for order actions
        if (message.Action is "buyMarket" or "sellMarket" or "buyLimit" or "sellLimit")
        {
            var qty = GetPayloadInt(message, "quantity");
            if (qty == null || qty < 1)
                return (false, "INVALID_QUANTITY", "quantity must be a positive integer for order actions.");
        }

        return (true, null, null);
    }

    private static (bool, string?, string?) ValidateSafetyConfig(BridgeMessage message)
    {
        var maxTrades = GetPayloadInt(message, "maxTradesWhenLosing");
        var dailyLoss = GetPayloadDouble(message, "dailyLossLimit");
        var lockHours = GetPayloadDouble(message, "lockDurationHours");

        // Distinguish "not supplied" from "supplied but not a whole number". Both read as null
        // after the TryGetInt32 guard, and reporting a decimal as a missing field sent the trader
        // looking for the wrong problem.
        if (maxTrades == null && HasNumericProperty(message, "maxTradesWhenLosing"))
        {
            return (false, "INVALID_PAYLOAD",
                $"maxTradesWhenLosing must be a whole number between 0 and {SafetyMacro.MaxTradeLimit} (0 disables the rule).");
        }

        if (maxTrades == null && dailyLoss == null && lockHours == null)
        {
            return (false, "INVALID_PAYLOAD",
                "configureSafety requires at least one of maxTradesWhenLosing, dailyLossLimit, lockDurationHours.");
        }

        if (maxTrades is < 0 or > SafetyMacro.MaxTradeLimit)
        {
            return (false, "INVALID_PAYLOAD",
                $"maxTradesWhenLosing must be between 0 and {SafetyMacro.MaxTradeLimit} (0 disables the rule).");
        }

        if (dailyLoss is < 0 or > SafetyMacro.MaxDailyLossLimit)
        {
            return (false, "INVALID_PAYLOAD",
                $"dailyLossLimit must be between 0 and {SafetyMacro.MaxDailyLossLimit} (0 disables the rule).");
        }

        if (lockHours is < SafetyMacro.MinLockHours or > SafetyMacro.MaxLockHours)
        {
            return (false, "INVALID_PAYLOAD",
                $"lockDurationHours must be between {SafetyMacro.MinLockHours} and {SafetyMacro.MaxLockHours}.");
        }

        return (true, null, null);
    }

    private static (bool, string?, string?) ValidateCooldownConfig(BridgeMessage message)
    {
        var seconds = GetPayloadInt(message, "cooldownSeconds");

        // Same distinction as configureSafety: a decimal reads as null after the TryGetInt32
        // guard, and reporting it as missing would send the trader looking for the wrong problem.
        if (seconds == null && HasNumericProperty(message, "cooldownSeconds"))
        {
            return (false, "INVALID_PAYLOAD",
                $"cooldownSeconds must be a whole number between {StateManager.MinCooldownSeconds} and {StateManager.MaxCooldownSeconds}.");
        }

        if (seconds == null)
            return (false, "INVALID_PAYLOAD", "cooldownSeconds is required for configureCooldown.");

        if (seconds < StateManager.MinCooldownSeconds || seconds > StateManager.MaxCooldownSeconds)
        {
            return (false, "INVALID_PAYLOAD",
                $"cooldownSeconds must be between {StateManager.MinCooldownSeconds} and {StateManager.MaxCooldownSeconds}.");
        }

        return (true, null, null);
    }

    private (bool, string?, string?) ValidateRequiredPayloadString(BridgeMessage message, string key, string errorMessage)
    {
        var value = GetPayloadString(message, key);
        if (string.IsNullOrWhiteSpace(value))
            return (false, "INVALID_PAYLOAD", errorMessage);

        return (true, null, null);
    }

    public bool RequiresPosition(string action) => PositionRequiredActions.Contains(action);

    private static string? GetPayloadString(BridgeMessage msg, string key)
    {
        if (msg.Payload is not JsonElement el) return null;
        if (el.TryGetProperty(key, out var prop) && prop.ValueKind == JsonValueKind.String)
            return prop.GetString();
        return null;
    }

    /// <summary>
    /// TryGetInt32 and not GetInt32: the latter THROWS on any JSON number that is not an exact
    /// Int32 — 2.5, but also 2.0 or 2147483648. That exception escaped Validate, unwound past
    /// ProcessPluginCommand and tore down the whole plugin session, which the host then
    /// reconnected and re-triggered. A decimal typed in the config UI bricked the deck in a
    /// permanent reconnect loop. A malformed value must read as absent, never as an exception.
    /// </summary>
    private static int? GetPayloadInt(BridgeMessage msg, string key)
    {
        if (msg.Payload is not JsonElement el) return null;
        if (el.TryGetProperty(key, out var prop) && prop.ValueKind == JsonValueKind.Number
            && prop.TryGetInt32(out var value))
            return value;
        return null;
    }

    private static double? GetPayloadDouble(BridgeMessage msg, string key)
    {
        if (msg.Payload is not JsonElement el) return null;
        if (el.TryGetProperty(key, out var prop) && prop.ValueKind == JsonValueKind.Number)
            return prop.GetDouble();
        return null;
    }

    /// <summary>True when the key is present as a JSON number, whatever its precision or range.</summary>
    private static bool HasNumericProperty(BridgeMessage msg, string key)
    {
        if (msg.Payload is not JsonElement el) return false;
        return el.TryGetProperty(key, out var prop) && prop.ValueKind == JsonValueKind.Number;
    }
}
