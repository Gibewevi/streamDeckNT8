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
    private readonly SafetyMacro _safety;
    private readonly MessageValidator _validator;
    private readonly DuplicateGuard _duplicateGuard;
    private readonly ILogger<MessageRouter> _logger;

    private static readonly HashSet<string> LocalActions = new(StringComparer.OrdinalIgnoreCase)
    {
        "qtySet", "qtyAdjust", "qtyReset", "setInstrument", "setAccount", "getState", "toggleCooldown",
        "armSafety", "disarmSafety", "toggleSafety", "configureSafety"
    };

    public MessageRouter(
        StateManager stateManager,
        SafetyMacro safety,
        MessageValidator validator,
        DuplicateGuard duplicateGuard,
        ILogger<MessageRouter> logger)
    {
        _stateManager = stateManager;
        _safety = safety;
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

        // Duplicate check — only for requests with requestId (null requestId skips duplicate detection)
        if (!string.IsNullOrEmpty(message.RequestId) && _duplicateGuard.IsDuplicate(message.RequestId))
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

            // Local actions never reach NT8, so their outcome (new quantity, new instrument,
            // cooldown on/off) exists nowhere else in the log.
            if (message.Action != "getState")
            {
                _logger.LogInformation("[REQ:{RequestId}] Local action {Action} → {Result}",
                    message.RequestId, message.Action,
                    resp.Error != null ? $"refused {resp.Error.Code}" : resp.Result?.ToString() ?? "ok");
            }

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

        // Safety macro — hard block. Evaluated before anything leaves the bridge, so a
        // refused key press never turns into an order on the market.
        var (safetyBlocked, safetyCode, safetyMessage) = _safety.Evaluate(message.Action);
        if (safetyBlocked)
        {
            _logger.LogWarning("[REQ:{RequestId}] SAFETY MACRO blocked {Action}: {Code} — {Msg}",
                message.RequestId, message.Action, safetyCode, safetyMessage);
            return (BridgeMessage.CreateError(message.RequestId, message.Action, safetyCode!, safetyMessage!), false, null);
        }

        // Check cooldown before forwarding entry orders to NT8
        if (_stateManager.IsOrderBlocked(message.Action))
        {
            _logger.LogWarning("[REQ:{RequestId}] Cooldown active, blocking {Action}", message.RequestId, message.Action);
            return (BridgeMessage.CreateError(message.RequestId, message.Action, "COOLDOWN_ACTIVE", "Cooldown is active after a losing trade. Entry orders are blocked."), false, null);
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

        LogAddonOutcome(message);

        // Forward everything else from NT8 to plugin as-is
        return message;
    }

    /// <summary>
    /// Records what NinjaTrader actually did with a command. Without this the log shows the
    /// order leaving the bridge and nothing else: NT8 accepts asynchronously, so a rejection
    /// (margin, closed market, bad price) arrives as a separate message later.
    /// </summary>
    private void LogAddonOutcome(BridgeMessage message)
    {
        if (message.Type == "response" || message.Type == "error")
        {
            var success = message.Result is JsonElement r
                          && r.TryGetProperty("success", out var s)
                          && s.ValueKind == JsonValueKind.True;

            if (message.Error != null)
            {
                _logger.LogWarning("[REQ:{RequestId}] NT8 refused {Action}: {Code} — {Msg}",
                    message.RequestId, message.Action, message.Error.Code, message.Error.Message);
            }
            else
            {
                _logger.LogInformation("[REQ:{RequestId}] NT8 completed {Action} (success={Success})",
                    message.RequestId, message.Action, success);
            }
            return;
        }

        if (message.Type == "event" && message.Action == "orderUpdate" && message.Payload is JsonElement p)
        {
            var rejected = p.TryGetProperty("rejected", out var rej) && rej.ValueKind == JsonValueKind.True;
            if (rejected)
            {
                _logger.LogError("ORDER REJECTED by NT8: {Action} {Type} {Instrument} id={OrderId} reason={Reason}",
                    GetElementString(p, "orderAction") ?? "?", GetElementString(p, "orderType") ?? "?",
                    GetElementString(p, "instrument") ?? "?", GetElementString(p, "orderId") ?? "?",
                    GetElementString(p, "reason") ?? GetElementString(p, "error") ?? "unspecified");
            }
            else
            {
                _logger.LogInformation("Order update: {Action} {Type} {Instrument} state={State} id={OrderId}",
                    GetElementString(p, "orderAction") ?? "?", GetElementString(p, "orderType") ?? "?",
                    GetElementString(p, "instrument") ?? "?", GetElementString(p, "orderState") ?? "?",
                    GetElementString(p, "orderId") ?? "?");
            }
        }
    }

    private static string? GetElementString(JsonElement el, string key) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(key, out var prop)
            ? prop.ValueKind == JsonValueKind.String ? prop.GetString() : prop.ToString()
            : null;

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
            case "toggleCooldown":
                {
                    var enabled = _stateManager.ToggleCooldown();
                    return new BridgeMessage
                    {
                        Type = "response",
                        RequestId = message.RequestId,
                        Source = "bridge",
                        Action = message.Action,
                        Timestamp = DateTimeOffset.UtcNow.ToString("o"),
                        Result = JsonSerializer.SerializeToElement(new { success = true, cooldownEnabled = enabled })
                    };
                }
            case "armSafety":
            case "disarmSafety":
            case "toggleSafety":
            case "configureSafety":
                return HandleSafetyAction(message);
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

    /// <summary>
    /// Runs an arm/disarm/toggle/configure request against the safety macro.
    /// Both success and refusal carry the resulting status so the plugin can refresh its
    /// buttons from a single round trip.
    /// </summary>
    private BridgeMessage HandleSafetyAction(BridgeMessage message)
    {
        // `force` n'est envoyé que si le mode développement est activé sur la touche Sécurité.
        // Absent ou mal formé, il vaut false : un contournement ne doit jamais être accordé
        // par accident.
        var force = GetPayloadBool(message, "force");

        var outcome = message.Action switch
        {
            "armSafety" => _safety.Arm(),
            "disarmSafety" => _safety.Disarm(force),
            "toggleSafety" => _safety.Toggle(force),
            _ => _safety.Configure(
                GetPayloadInt(message, "maxTradesWhenLosing"),
                GetPayloadDouble(message, "dailyLossLimit"),
                GetPayloadDouble(message, "lockDurationHours"))
        };

        var status = JsonSerializer.SerializeToElement(outcome.Status, BridgeMessage.CamelCaseOpts);

        if (!outcome.Ok)
        {
            _logger.LogWarning("[REQ:{RequestId}] {Action} refused: {Code}", message.RequestId, message.Action, outcome.ErrorCode);
            var error = BridgeMessage.CreateError(message.RequestId, message.Action, outcome.ErrorCode!, outcome.ErrorMessage!);
            error.Payload = status;
            return error;
        }

        return new BridgeMessage
        {
            Type = "response",
            RequestId = message.RequestId,
            Source = "bridge",
            Action = message.Action,
            Timestamp = DateTimeOffset.UtcNow.ToString("o"),
            Result = JsonSerializer.SerializeToElement(new { success = true }),
            Payload = status
        };
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

    /// <summary>
    /// TryGetInt32 and not GetInt32: the latter throws on any JSON number that is not an exact
    /// Int32 (2.5, but also 2.0). See the same guard in MessageValidator — an exception here
    /// used to tear down the plugin session instead of refusing the command.
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

    /// <summary>Absent or malformed reads as false: a bypass must never be granted by accident.</summary>
    private static bool GetPayloadBool(BridgeMessage msg, string key)
    {
        if (msg.Payload is not JsonElement el) return false;
        return el.TryGetProperty(key, out var prop) && prop.ValueKind == JsonValueKind.True;
    }
}
