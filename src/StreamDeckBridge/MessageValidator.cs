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
        "flatten", "cancelOrders", "reverse",
        "breakeven", "moveStop", "moveTarget",
        "qtySet", "qtyAdjust", "qtyReset",
        "setInstrument", "setAccount", "getState", "toggleCooldown"
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

        if (message.Action is "getState" or "setInstrument" or "setAccount" or "toggleCooldown")
            return (true, null, null);

        // All trading actions need instrument context at minimum
        return ValidateTradingAction(message);
    }

    private (bool, string?, string?) ValidateQuantityAction(BridgeMessage message)
    {
        if (message.Action == "qtySet")
        {
            var qty = GetPayloadInt(message, "quantity");
            if (qty == null || qty < _config.MinQuantity || qty > _config.MaxQuantity)
                return (false, "INVALID_QUANTITY", $"Quantity must be between {_config.MinQuantity} and {_config.MaxQuantity}.");
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

    public bool RequiresPosition(string action) => PositionRequiredActions.Contains(action);

    private static string? GetPayloadString(BridgeMessage msg, string key)
    {
        if (msg.Payload is not JsonElement el) return null;
        if (el.TryGetProperty(key, out var prop) && prop.ValueKind == JsonValueKind.String)
            return prop.GetString();
        return null;
    }

    private static int? GetPayloadInt(BridgeMessage msg, string key)
    {
        if (msg.Payload is not JsonElement el) return null;
        if (el.TryGetProperty(key, out var prop) && prop.ValueKind == JsonValueKind.Number)
            return prop.GetInt32();
        return null;
    }
}
