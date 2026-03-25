using System.Text.Json;
using Microsoft.Extensions.Logging;
using StreamDeckBridge.Models;

namespace StreamDeckBridge;

/// <summary>
/// Routes messages between the plugin and the NT8 add-on.
/// Handles local actions (qty, instrument, state) directly.
/// Forwards trading actions to NT8 via the addon WebSocket.
/// </summary>
public sealed class MessageRouter
{
    private readonly StateManager _stateManager;
    private readonly MessageValidator _validator;
    private readonly DuplicateGuard _duplicateGuard;
    private readonly ILogger<MessageRouter> _logger;

    private static readonly HashSet<string> LocalActions = new(StringComparer.OrdinalIgnoreCase)
    {
        "qtySet", "qtyAdjust", "qtyReset", "setInstrument", "setAccount", "getState"
    };

    public MessageRouter(
        StateManager stateManager,
        MessageValidator validator,
        DuplicateGuard duplicateGuard,
        ILogger<MessageRouter> logger)
    {
        _stateManager = stateManager;
        _validator = validator;
        _duplicateGuard = duplicateGuard;
        _logger = logger;
    }

    /// <summary>
    /// Processes a command from the plugin.
    /// Returns (localResponse, shouldForwardToNt).
    /// If localResponse is not null, send it back to plugin.
    /// If shouldForwardToNt is true, the enriched message should be forwarded.
    /// </summary>
    public (BridgeMessage? LocalResponse, bool ShouldForward, BridgeMessage? EnrichedMessage) ProcessPluginCommand(BridgeMessage message)
    {
        _logger.LogInformation("[REQ:{RequestId}] Received command: {Action}", message.RequestId, message.Action);

        // Validate
        var (isValid, errorCode, errorMessage) = _validator.Validate(message);
        if (!isValid)
        {
            _logger.LogWarning("[REQ:{RequestId}] Validation failed: {Code} - {Msg}", message.RequestId, errorCode, errorMessage);
            return (BridgeMessage.CreateError(message.RequestId, message.Action, errorCode!, errorMessage!), false, null);
        }

        // Duplicate check
        if (message.RequestId != null && _duplicateGuard.IsDuplicate(message.RequestId))
        {
            return (BridgeMessage.CreateError(message.RequestId, message.Action, "DUPLICATE_REQUEST", "This requestId was already processed recently."), false, null);
        }

        // Check NT connection for trading actions
        if (!LocalActions.Contains(message.Action))
        {
            var state = _stateManager.GetSnapshot();
            if (!state.NtConnected)
            {
                _logger.LogWarning("[REQ:{RequestId}] NT8 not connected, rejecting {Action}", message.RequestId, message.Action);
                return (BridgeMessage.CreateError(message.RequestId, message.Action, "NT_DISCONNECTED", "NinjaTrader is not connected."), false, null);
            }
        }

        // Handle local actions
        if (LocalActions.Contains(message.Action))
        {
            var resp = HandleLocalAction(message);

            // setInstrument/setAccount must ALSO be forwarded to NT8 so the add-on
            // updates its tracked instrument/account and starts publishing data for it
            if (message.Action is "setInstrument" or "setAccount")
            {
                var fwdPayload = _stateManager.EnrichPayload(message);
                var fwdMsg = new BridgeMessage
                {
                    Type = "command",
                    Version = message.Version,
                    RequestId = message.RequestId,
                    Timestamp = message.Timestamp,
                    Source = "bridge",
                    Action = message.Action,
                    Payload = fwdPayload
                };
                return (resp, true, fwdMsg);
            }

            return (resp, false, null);
        }

        // Enrich and forward to NT8
        var enrichedPayload = _stateManager.EnrichPayload(message);
        var enriched = new BridgeMessage
        {
            Type = message.Type,
            Version = message.Version,
            RequestId = message.RequestId,
            Timestamp = message.Timestamp,
            Source = "bridge",
            Action = message.Action,
            Payload = enrichedPayload
        };

        _logger.LogInformation("[REQ:{RequestId}] Forwarding {Action} to NT8", message.RequestId, message.Action);
        return (null, true, enriched);
    }

    /// <summary>
    /// Processes a message from the NT8 add-on (response or event).
    /// </summary>
    public BridgeMessage? ProcessAddonMessage(BridgeMessage message)
    {
        if (message.Type == "event" && message.Action == "stateUpdate" && message.Payload is JsonElement payload)
        {
            _stateManager.UpdateFromNtState(payload);

            // Return merged state (NT8 position data + bridge-managed qty/instrument)
            // instead of raw NT8 payload which lacks quantity/defaultQuantity
            var snapshot = _stateManager.GetSnapshot();
            return BridgeMessage.CreateEvent("stateUpdate", snapshot);
        }

        // Forward everything else from NT8 to plugin as-is
        return message;
    }

    private BridgeMessage HandleLocalAction(BridgeMessage message)
    {
        switch (message.Action)
        {
            case "qtySet":
                {
                    var qty = GetPayloadInt(message, "quantity") ?? 1;
                    var newQty = _stateManager.SetQuantity(qty);
                    return CreateQtyResponse(message, newQty);
                }
            case "qtyAdjust":
                {
                    var delta = GetPayloadInt(message, "delta") ?? 0;
                    var newQty = _stateManager.AdjustQuantity(delta);
                    return CreateQtyResponse(message, newQty);
                }
            case "qtyReset":
                {
                    var newQty = _stateManager.ResetQuantity();
                    return CreateQtyResponse(message, newQty);
                }
            case "setAccount":
                {
                    var acct = GetPayloadString(message, "account") ?? "";
                    _stateManager.SetAccount(acct);
                    return new BridgeMessage
                    {
                        Type = "response",
                        RequestId = message.RequestId,
                        Source = "bridge",
                        Action = message.Action,
                        Timestamp = DateTimeOffset.UtcNow.ToString("o"),
                        Result = JsonSerializer.SerializeToElement(new { success = true, account = acct })
                    };
                }
            case "setInstrument":
                {
                    var inst = GetPayloadString(message, "instrument") ?? "";
                    _stateManager.SetInstrument(inst);
                    return new BridgeMessage
                    {
                        Type = "response",
                        RequestId = message.RequestId,
                        Source = "bridge",
                        Action = message.Action,
                        Timestamp = DateTimeOffset.UtcNow.ToString("o"),
                        Result = JsonSerializer.SerializeToElement(new { success = true, instrument = inst })
                    };
                }
            case "getState":
                {
                    var state = _stateManager.GetSnapshot();
                    return new BridgeMessage
                    {
                        Type = "response",
                        RequestId = message.RequestId,
                        Source = "bridge",
                        Action = "getState",
                        Timestamp = DateTimeOffset.UtcNow.ToString("o"),
                        Result = JsonSerializer.SerializeToElement(new { success = true }),
                        Payload = JsonSerializer.SerializeToElement(state, BridgeMessage.CamelCaseOpts)
                    };
                }
            default:
                return BridgeMessage.CreateError(message.RequestId, message.Action, "INTERNAL_ERROR", "Unhandled local action.");
        }
    }

    private static BridgeMessage CreateQtyResponse(BridgeMessage req, int qty)
    {
        return new BridgeMessage
        {
            Type = "response",
            RequestId = req.RequestId,
            Source = "bridge",
            Action = req.Action,
            Timestamp = DateTimeOffset.UtcNow.ToString("o"),
            Result = JsonSerializer.SerializeToElement(new { success = true, quantity = qty })
        };
    }

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
