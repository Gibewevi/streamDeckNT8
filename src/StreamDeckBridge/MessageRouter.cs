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
    private readonly CopierPolicy _copier;
    private readonly MessageValidator _validator;
    private readonly DuplicateGuard _duplicateGuard;
    private readonly SecurityJournal _journal;
    private readonly ILogger<MessageRouter> _logger;

    private static readonly HashSet<string> LocalActions = new(StringComparer.OrdinalIgnoreCase)
    {
        "qtySet", "qtyAdjust", "qtyReset", "setInstrument", "setAccount", "getState",
        "toggleCooldown", "configureCooldown",
        "armSafety", "disarmSafety", "toggleSafety", "configureSafety",
        "configureTrend", "toggleTrend",
        // `configureCopier` only stores settings. What the add-on needs reaches it through the
        // broadcast loop's setCopierConfig push, on change and on every reconnection — the same
        // route as the guard policy, and for the same reason: the add-on is reloaded on every
        // NinjaScript recompile and must be told again without the host having to notice.
        "configureCopier"
    };

    /// <summary>
    /// Actions locales qui doivent AUSSI descendre dans NT8, parce que l'add-on a quelque chose à
    /// en faire : suivre un instrument, suivre un compte, recharger ses séries de barres.
    /// </summary>
    private static readonly HashSet<string> ForwardedLocalActions = new(StringComparer.OrdinalIgnoreCase)
    {
        "setInstrument", "setAccount", "configureTrend"
    };

    /// <summary>
    /// Les cinq actions qui ouvrent ou retournent une position. Miroir de
    /// <c>SafetyMacro.EntryActions</c> et de <c>ACTIONS_ENTREE</c> côté hôte.
    /// </summary>
    private static readonly HashSet<string> EntryActions = new(StringComparer.OrdinalIgnoreCase)
    {
        "buyMarket", "sellMarket", "buyLimit", "sellLimit", "reverse"
    };

    public MessageRouter(
        StateManager stateManager,
        SafetyMacro safety,
        CopierPolicy copier,
        MessageValidator validator,
        DuplicateGuard duplicateGuard,
        SecurityJournal journal,
        ILogger<MessageRouter> logger)
    {
        _stateManager = stateManager;
        _safety = safety;
        _copier = copier;
        _validator = validator;
        _duplicateGuard = duplicateGuard;
        _journal = journal;
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
            // updates its tracked instrument/account and starts publishing data for it.
            // configureTrend joins them: only the add-on can act on it — it owns the bars.
            if (ForwardedLocalActions.Contains(message.Action))
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
        //
        // The quantity is resolved here rather than after EnrichPayload, which runs later: the
        // contract cap has to judge the order that would actually be sent, and the host usually
        // omits the quantity precisely because the bridge owns it.
        var snapshot = _stateManager.GetSnapshot();
        var quantity = GetPayloadInt(message, "quantity") ?? snapshot.Quantity;
        var (safetyBlocked, safetyCode, safetyMessage) = _safety.Evaluate(message.Action, quantity);
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

        // Trend. Placed AFTER the safety macro and the cooldown, and that order is not arbitrary:
        // Guard's message must win. Reading TREND on a key while the daily loss limit is reached
        // would send the trader looking for the wrong problem.
        //
        // Armed, it refuses. Disarmed — or with blocking switched off on the key — it refuses
        // nothing and only writes down what it WOULD have refused, which is what lets the trader
        // calibrate the threshold against a real session before handing the macro any authority.
        var (trendBlocked, trendMessage) = _stateManager.IsTrendBlocked(message.Action);
        if (trendBlocked)
        {
            _logger.LogWarning("[REQ:{RequestId}] TREND blocked {Action}: {Msg}",
                message.RequestId, message.Action, trendMessage);
            return (BridgeMessage.CreateError(message.RequestId, message.Action, "TREND_AGAINST", trendMessage!), false, null);
        }

        LogTrendObservation(message, snapshot);

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
    /// Écrit ce que la macro Trend AURAIT refusé, sans rien refuser.
    ///
    /// Journalisé en INFO et non en TRACE : ce n'est pas une boucle périodique mais un appui de
    /// touche, donc quelques dizaines de lignes par séance — et c'est la mesure que la version
    /// suivante attend pour décider du seuil.
    /// </summary>
    private void LogTrendObservation(BridgeMessage message, TradingState state)
    {
        if (!EntryActions.Contains(message.Action)) return;

        var trend = state.Trend;
        if (!trend.Available) return;
        if (trend.Direction is not ("up" or "down")) return;

        // Un ordre qui RÉDUIT l'exposition n'est jamais concerné, même en observation : il ne sera
        // jamais refusé, donc le compter fausserait le seul chiffre que ce journal produit.
        // Enfermer le trader dans une position est le résultat qu'aucune règle ne peut produire.
        if (!CreatesExposure(message.Action, state)) return;

        var buying = message.Action.StartsWith("buy", StringComparison.OrdinalIgnoreCase);
        // `reverse` s'évalue sur le sens qu'il OUVRE, pas sur celui qu'il ferme.
        if (message.Action.Equals("reverse", StringComparison.OrdinalIgnoreCase))
            buying = state.Position?.Direction == "Short";

        var against = buying ? trend.Direction == "down" : trend.Direction == "up";
        if (!against) return;

        _logger.LogInformation(
            "[REQ:{RequestId}] TREND observation — {Action} WOULD HAVE BEEN REFUSED: trend is {Direction} "
            + "({RefMin}min={Reference}, {HigherLabel}) on {Instrument}",
            message.RequestId, message.Action, trend.Direction,
            trend.ReferenceMinutes, trend.Reference,
            trend.HigherMinutes > 0 ? $"{trend.HigherMinutes}min={trend.Higher}" : "higher timeframe off",
            state.Instrument);
    }

    /// <summary>
    /// Cet ordre créerait-il de l'exposition — ouvrir depuis flat, renforcer, ou retourner ?
    ///
    /// Reprend mot pour mot la logique de <c>SafetyMacro.CreatesExposure</c>, à laquelle Trend est
    /// soumis exactement comme les autres règles. Direction seule, jamais la taille : une vente en
    /// position longue passe quelle que soit sa quantité.
    /// </summary>
    private static bool CreatesExposure(string action, TradingState state)
    {
        if (action.Equals("reverse", StringComparison.OrdinalIgnoreCase)) return true;

        var buying = action.StartsWith("buy", StringComparison.OrdinalIgnoreCase);
        var selling = action.StartsWith("sell", StringComparison.OrdinalIgnoreCase);
        if (!buying && !selling) return false;

        var position = state.Position;
        if (position is not { Exists: true } || position.Quantity <= 0) return true;

        return (position.Direction == "Long" && buying) || (position.Direction == "Short" && selling);
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

        // An order the trader placed straight into NinjaTrader, refused by the add-on. This is the
        // one event that proves the bypass was attempted, so it is logged loudly here as well as
        // in the add-on — the two files are read separately when something goes wrong.
        if (message.Type == "event" && message.Action == "guardViolation")
        {
            var gv = message.Payload is JsonElement e ? e : default;
            _logger.LogWarning("GUARD VIOLATION reported by NT8: {Payload}",
                gv.ValueKind == JsonValueKind.Object ? gv.GetRawText() : "(no payload)");
            RecordViolation(gv);
            return message;
        }

        LogAddonOutcome(message);

        // Forward everything else from NT8 to plugin as-is
        return message;
    }

    /// <summary>
    /// Files the bypass attempt in the behavioural journal.
    ///
    /// Written here rather than by the host, which used to do it and did it well right up to the
    /// moment it mattered: on 2026-08-12 the deck was unplugged, the host process ended, and the
    /// nineteen manual orders that followed — past a breached daily-loss limit — reached no
    /// journal at all. An event that only survives while the trader keeps his deck plugged in is
    /// not evidence, it is a courtesy.
    ///
    /// `cancelled: false` is the line to read: the order was seen and survived, which means the
    /// bypass worked.
    /// </summary>
    private void RecordViolation(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object) return;

        string Text(string nom) =>
            payload.TryGetProperty(nom, out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString() ?? string.Empty
                : string.Empty;

        // TryGetInt32 and never GetInt32: a malformed quantity must read as absent, never throw.
        // An exception on this path used to cost the whole plugin session.
        int Number(string nom) =>
            payload.TryGetProperty(nom, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n)
                ? n
                : 0;

        var instrument = Text("instrument");

        _journal.Record("guard.violation", _stateManager.GetSnapshot().Account, instrument, new
        {
            reason = Text("violation"),
            cancelled = payload.TryGetProperty("cancelled", out var c) && c.ValueKind == JsonValueKind.True,
            // "stopped" or "bypassed", straight from the add-on, which now waits for the order to
            // reach a terminal state before deciding. Empty only for records written by an older
            // add-on, and a reader must treat that as UNKNOWN rather than as a success.
            outcome = Text("outcome"),
            // Without it, no violation could be traced back to the order it was about — which is
            // what made the 2026-08-12 discrepancy so slow to pin down.
            orderId = Text("orderId"),
            orderAction = Text("orderAction"),
            orderType = Text("orderType"),
            quantity = Number("quantity"),
            orderName = Text("name")
        });
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
            case "configureCooldown":
                {
                    // La validation a déjà borné la valeur ; le `?? 0` n'est qu'un garde-fou de
                    // compilation, StateManager reborne de toute façon.
                    var secs = _stateManager.SetCooldownSeconds(GetPayloadInt(message, "cooldownSeconds") ?? 0);
                    return new BridgeMessage
                    {
                        Type = "response",
                        RequestId = message.RequestId,
                        Source = "bridge",
                        Action = message.Action,
                        Timestamp = DateTimeOffset.UtcNow.ToString("o"),
                        Result = JsonSerializer.SerializeToElement(new { success = true, cooldownSeconds = secs })
                    };
                }
            case "armSafety":
            case "disarmSafety":
            case "toggleSafety":
            case "configureSafety":
                return HandleSafetyAction(message);
            case "configureCopier":
                {
                    var enabled = GetPayloadBoolOrNull(message, "enabled");
                    var followers = GetPayloadString(message, "followers");
                    var master = _stateManager.GetSnapshot().Account;

                    var (ok, code, reason) = _copier.Configure(enabled, followers, master);
                    if (!ok)
                        return BridgeMessage.CreateError(message.RequestId, message.Action, code!, reason!);

                    return new BridgeMessage
                    {
                        Type = "response",
                        RequestId = message.RequestId,
                        Source = "bridge",
                        Action = message.Action,
                        Timestamp = DateTimeOffset.UtcNow.ToString("o"),
                        Result = JsonSerializer.SerializeToElement(new
                        {
                            success = true,
                            enabled = _copier.IsEffectivelyEnabled,
                            followers = _copier.Followers.Count
                        })
                    };
                }
            case "configureTrend":
                {
                    // Les réglages de DÉTECTION vivent dans l'add-on, seul à détenir les barres, et
                    // lui parviennent par le chemin de transmission. Une seule clé est pour le
                    // bridge : l'autorisation de bloquer, puisque c'est lui qui refuse.
                    if (GetPayloadBoolOrNull(message, "blockingAllowed") is { } allowed)
                        _stateManager.SetTrendBlockingAllowed(allowed);

                    return new BridgeMessage
                    {
                        Type = "response",
                        RequestId = message.RequestId,
                        Source = "bridge",
                        Action = message.Action,
                        Timestamp = DateTimeOffset.UtcNow.ToString("o"),
                        Result = JsonSerializer.SerializeToElement(new { success = true })
                    };
                }
            case "toggleTrend":
                {
                    var (ok, code, reason, armed) = _stateManager.ToggleTrendArmed();
                    if (!ok)
                        return BridgeMessage.CreateError(message.RequestId, message.Action, code!, reason!);

                    return new BridgeMessage
                    {
                        Type = "response",
                        RequestId = message.RequestId,
                        Source = "bridge",
                        Action = message.Action,
                        Timestamp = DateTimeOffset.UtcNow.ToString("o"),
                        Result = JsonSerializer.SerializeToElement(new { success = true, trendArmed = armed })
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

    /// <summary>
    /// The account-wide liquidation to send, or null when nothing is due.
    ///
    /// Lives here rather than in <c>BridgeServer</c> because the router already owns both the
    /// safety macro and the state — widening the server's dependencies just to read an account
    /// name would spread the decision across two files.
    ///
    /// Nothing is latched by this call: the server confirms afterwards, through
    /// <see cref="ConfirmAutoFlatten"/>, once it knows whether the order actually left.
    /// </summary>
    public BridgeMessage? BuildAutoFlattenCommand()
    {
        if (_safety.PendingAutoFlatten() is not { } request) return null;

        var account = _stateManager.GetSnapshot().Account;
        if (string.IsNullOrWhiteSpace(account))
        {
            // No resolved account means nothing can be liquidated. Not latched — the next state
            // update will carry one, and silently marking the day "handled" would leave the trader
            // believing a rule had run when it never did.
            _logger.LogError("Auto-liquidation due (session P&L {Pnl:0.##} / limit {Limit:0.##}) "
                + "but no account is resolved — nothing sent", request.SessionPnl, request.Limit);
            return null;
        }

        var payload = JsonSerializer.SerializeToElement(new { account });

        return new BridgeMessage
        {
            Type = "command",
            Source = "bridge",
            RequestId = $"safety-flatten-{Guid.NewGuid():N}",
            // Deliberately not in MessageValidator.KnownActions: this action must never be
            // reachable from a key press, only from the rule that owns it.
            Action = "flattenAccount",
            Payload = payload,
        };
    }

    /// <summary>Records what became of the liquidation once the server knows.</summary>
    public void ConfirmAutoFlatten(bool sent, string reason = "")
    {
        if (sent) _safety.MarkAutoFlattenSent();
        else _safety.MarkAutoFlattenFailed(reason);

        // Journalled from the bridge for the same reason as the violations: this is the sanction
        // itself, and now that it can fire several times a day, how many times it fired is the
        // single most telling number of the session.
        var snapshot = _stateManager.GetSnapshot();
        _journal.Record(sent ? "guard.liquidation" : "guard.liquidationFailed",
            snapshot.Account, snapshot.Instrument, new
            {
                sessionPnl = snapshot.Safety.SessionPnl,
                limit = snapshot.Safety.DailyLossLimit,
                occurrence = _safety.AutoFlattenCount,
                reason
            });
    }

    /// <summary>
    /// Runs an arm/disarm/toggle/configure request against the safety macro.
    /// Both success and refusal carry the resulting status so the plugin can refresh its
    /// buttons from a single round trip.
    /// </summary>
    private BridgeMessage HandleSafetyAction(BridgeMessage message)
    {
        // Aucun champ du payload ne peut lever le verrou : `disarmSafety` et `toggleSafety` ne
        // prennent aucun argument. Un drapeau `force` a existé ici — il est retiré, et rien ne le
        // lit plus. Le refuser au routeur plutôt qu'à l'appelant est délibéré : c'est le seul
        // endroit par lequel toute commande passe, donc le seul où l'absence de contournement
        // se vérifie d'un coup d'œil.
        var outcome = message.Action switch
        {
            "armSafety" => _safety.Arm(),
            "disarmSafety" => _safety.Disarm(),
            "toggleSafety" => _safety.Toggle(),
            _ => _safety.Configure(new SafetyConfigUpdate
            {
                MaxTradesWhenLosing = GetPayloadInt(message, "maxTradesWhenLosing"),
                DailyLossLimit = GetPayloadDouble(message, "dailyLossLimit"),
                LockDurationHours = GetPayloadDouble(message, "lockDurationHours"),
                AntiTiltEnabled = GetPayloadBoolOrNull(message, "antiTiltEnabled"),
                MaxContracts = GetPayloadInt(message, "maxContracts"),
                TiltAveragingAllowed = GetPayloadBoolOrNull(message, "tiltAveragingAllowed"),
                TiltAdvanced = GetPayloadBoolOrNull(message, "tiltAdvanced"),
                TiltHoldSeconds = GetPayloadInt(message, "tiltHoldSeconds"),
                TiltEpisodeMinutes = GetPayloadDouble(message, "tiltEpisodeMinutes"),
                PauseAfterMinutes = GetPayloadDouble(message, "pauseAfterMinutes"),
                PauseDurationMinutes = GetPayloadDouble(message, "pauseDurationMinutes"),
                AutoFlattenOnDailyLoss = GetPayloadBoolOrNull(message, "autoFlattenOnDailyLoss"),
                AutoFlattenGraceSeconds = GetPayloadDouble(message, "autoFlattenGraceSeconds")
            })
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

    /// <summary>
    /// Three-state read for settings: null means "not supplied, leave it alone", which a plain
    /// bool cannot express.
    ///
    /// Only reader for booleans, and it must stay that way: a two-state reader existed alongside
    /// it for the safety-macro bypass flag, and it went out with the flag. Reintroducing one
    /// would mean some payload boolean can decide something other than a setting.
    /// </summary>
    private static bool? GetPayloadBoolOrNull(BridgeMessage msg, string key)
    {
        if (msg.Payload is not JsonElement el) return null;
        if (!el.TryGetProperty(key, out var prop)) return null;
        return prop.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null
        };
    }
}
