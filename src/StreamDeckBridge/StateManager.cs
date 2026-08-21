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
    private readonly CopierPolicy _copier;
    private readonly ILogger<StateManager> _logger;
    private readonly object _lock = new();
    private readonly TradingState _state;
    private DateTime _accountSetAt = DateTime.MinValue;
    private DateTime _instrumentSetAt = DateTime.MinValue;
    private static readonly TimeSpan OverrideGuard = TimeSpan.FromSeconds(5);

    /// <summary>Bounds for the configurable cooldown. Below a second it is not a pause; above an
    /// hour the trader would be better served by disarming the deck than by waiting.</summary>
    public const int MinCooldownSeconds = 1;
    public const int MaxCooldownSeconds = 3600;

    private bool _cooldownEnabled;
    private DateTime? _cooldownUntil;
    private int _cooldownSeconds;

    // --- Macro Tendance ---
    //
    // Logée ici et non dans une classe à part, sur le modèle exact de la temporisation juste
    // au-dessus : même forme de règle — un interrupteur, pas de verrou, un refus qui ne vise que
    // les entrées — et le même besoin de lire la position et l'état publié pour trancher.
    //
    // Comme la temporisation, l'armement n'est PAS persisté. C'est un choix, pas un oubli : le
    // bridge ne redémarre qu'à une mise à jour ou à un plantage, la touche affiche son état en
    // permanence, et une aide à la discipline n'a pas à survivre à un redémarrage comme le fait le
    // verrou de Guard, qui lui protège d'un contournement délibéré.
    private bool _trendArmed;
    private bool _trendBlockingAllowed;
    private bool _previousPositionExists;
    private double _previousUnrealizedPnl;
    private string _previousPositionInstrument = string.Empty;

    // False while NT8 could not resolve the account/instrument, which is NOT the same as "flat".
    // Conflating the two fabricated trades: a single tick with an unresolved context cleared the
    // position, and the next tick then read a flat→open transition on a position that had never
    // moved — inflating the count that gates SAFETY_TRADE_LIMIT_REACHED. Starts false so an
    // already-open position at startup is never counted as a new trade.
    private bool _previousPositionKnown;

    public StateManager(BridgeConfig config, SafetyMacro safety, CopierPolicy copier, ILogger<StateManager> logger)
    {
        _config = config;
        _safety = safety;
        _copier = copier;
        _logger = logger;
        _state = new TradingState
        {
            Account = config.DefaultAccount,
            Instrument = config.DefaultInstrument,
            Quantity = config.DefaultQuantity,
            DefaultQuantity = config.DefaultQuantity
        };

        _cooldownSeconds = Math.Clamp(config.DefaultCooldownSeconds, MinCooldownSeconds, MaxCooldownSeconds);
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

            var safety = _safety.GetStatus();

            var snapshot = new TradingState
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

                // Le solde DOIT être recopié ici. Il a été omis, et comme ce snapshot est le seul
                // objet diffusé au client, la valeur lue de NT8 mourait dans `_state` : l'add-on
                // publiait bien `cashValue`, le bridge la parsait bien, et l'hôte journalisait
                // `solde=ABSENT` à chaque envoi. Le journal Bitlearn est resté sans capital de
                // départ, donc sans balance et sans pourcentage exploitable.
                CashValue = _state.CashValue,
                CooldownEnabled = _cooldownEnabled,
                CooldownActive = cooldownActive,
                CooldownSecondsRemaining = cooldownRemaining,
                CooldownSeconds = _cooldownSeconds,

                // Recopié ici comme tout le reste, et pour la raison écrite plus haut : ce snapshot
                // est le SEUL objet diffusé au client. Un champ lu de NT8, parsé correctement, mais
                // absent d'ici n'atteint jamais l'écran et rien ne le signale.
                // Copié plutôt que partagé : les deux derniers champs appartiennent au bridge et
                // sont estampillés ici. Renvoyer l'instance de `_state` reviendrait à les écrire
                // dans l'objet que la prochaine publication de NinjaTrader va remplacer.
                Trend = new TrendState
                {
                    Available = _state.Trend.Available,
                    Direction = _state.Trend.Direction,
                    Reference = _state.Trend.Reference,
                    Higher = _state.Trend.Higher,
                    ReferenceMinutes = _state.Trend.ReferenceMinutes,
                    HigherMinutes = _state.Trend.HigherMinutes,
                    StaleSeconds = _state.Trend.StaleSeconds,
                    BlockingAllowed = _trendBlockingAllowed,
                    Armed = _trendArmed,
                },

                // Copied for the reason written above the trend block: this snapshot is the ONLY
                // object broadcast to the client. Returning the live instance would let the
                // bridge-owned half be stamped into the object NinjaTrader's next publish
                // overwrites, and the follower list would flicker between configured and empty.
                Copier = new CopierStatus
                {
                    MasterResolved = _state.Copier.MasterResolved,
                    CopiedToday = _state.Copier.CopiedToday,
                    Followers = _state.Copier.Followers.Select(f => new CopierFollowerStatus
                    {
                        Name = f.Name,
                        Multiplier = f.Multiplier,
                        MaxContracts = f.MaxContracts,
                        Resolved = f.Resolved,
                        Drifted = f.Drifted,
                        Drift = f.Drift,
                        LastError = f.LastError,
                    }).ToList(),
                },
                Safety = safety
            };

            // The configuration half of the copier block is stamped on here, after the copy, for
            // the same reason the trend's two bridge-owned fields are: a NinjaTrader publish must
            // not be able to overwrite what the trader configured. `entriesBlocked` is handed over
            // rather than recomputed — the safety macro is the only arbiter of it.
            _copier.StampSnapshot(snapshot.Copier, snapshot.Account, safety.EntriesBlocked);

            return snapshot;
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

                // Sans NinjaTrader il n'y a plus de barres, donc plus de tendance. La garder
                // afficherait un sens figé au moment précis où plus rien ne le met à jour.
                _state.Trend = new TrendState();

                // Même raisonnement pour la SANTÉ du copieur : plus personne ne peut dire si un
                // suiveur est résolu ou en dérive. La configuration, elle, n'est pas touchée — elle
                // appartient à CopierPolicy et sera réestampillée au prochain instantané.
                _state.Copier = new CopierStatus();
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
                    if (_previousPositionExists && !currentExists)
                    {
                        // Detect position closed with a loss → trigger cooldown
                        if (_cooldownEnabled && _previousUnrealizedPnl < 0)
                        {
                            _cooldownUntil = DateTime.UtcNow.AddSeconds(_cooldownSeconds);
                            _logger.LogWarning("Cooldown activated for {Secs}s after losing trade (PnL: {Pnl})",
                                _cooldownSeconds, _previousUnrealizedPnl);
                        }

                        // The anti-tilt counters are told about EVERY close, winners included, and
                        // regardless of whether the cooldown is switched on: a winner is what
                        // resets the losing streak, so skipping it would leave the streak stuck.
                        _safety.RecordTradeClosed(_previousUnrealizedPnl);
                    }

                    // Flat → open on the same instrument counts as one trade for the safety macro.
                    // Requiring the same instrument avoids counting a phantom trade when the
                    // tracked instrument switches to one that already has an open position.
                    if (!_previousPositionExists && currentExists &&
                        string.Equals(previousInstrument, _previousPositionInstrument, StringComparison.OrdinalIgnoreCase))
                    {
                        _safety.RecordTradeOpened(_state.Position?.Quantity ?? 0);
                    }
                }

                // Feeds the contextual anti-tilt conditions (averaging down, contract cap). Kept
                // out of the previousKnown guard because it describes the position as it is now,
                // not a transition — but only called when NT8 actually published one, so an
                // unresolved context leaves the conditions untouched instead of clearing them.
                _safety.UpdatePositionContext(
                    currentExists,
                    _state.Position?.Quantity ?? 0,
                    _state.Position?.Direction ?? "Flat",
                    _state.Position?.UnrealizedPnl ?? 0);

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
            // The account object is read on every publish; only the NAME is subject to the override
            // guard, which exists so NT8 cannot overwrite an account the trader just picked by hand.
            //
            // The cash value used to sit inside that guard, and had no business being there: it is
            // the account BALANCE, not the selection, and nothing about it conflicts with a manual
            // pick. Every publish landing inside the guard window dropped it. The balance is what
            // gives the Bitlearn journal its starting capital — without it the journal opens at zero
            // and every percentage it shows is meaningless.
            if (statePayload.TryGetProperty("account", out var acctObj) && acctObj.ValueKind == JsonValueKind.Object)
            {
                var accountGuarded = (DateTime.UtcNow - _accountSetAt) < OverrideGuard;
                if (!accountGuarded && acctObj.TryGetProperty("name", out var acctName) && acctName.ValueKind == JsonValueKind.String)
                {
                    var name = acctName.GetString();
                    if (!string.IsNullOrEmpty(name))
                        _state.Account = name;
                }

                if (acctObj.TryGetProperty("cashValue", out var cash) && cash.ValueKind == JsonValueKind.Number)
                {
                    _state.CashValue = cash.GetDouble();
                }
            }
            // La tendance est calculée par l'add-on : le bridge ne fait que la transporter. Un bloc
            // absent laisse la valeur précédente en place plutôt que de la remettre à zéro — une
            // publication sans `trend` (add-on plus ancien, monitor non initialisé) ne doit pas
            // faire clignoter la touche entre un sens connu et NO DATA à chaque tic.
            if (statePayload.TryGetProperty("trend", out var trend) && trend.ValueKind == JsonValueKind.Object)
            {
                var parsed = JsonSerializer.Deserialize<TrendState>(trend.GetRawText(), CamelCase);
                if (parsed != null) _state.Trend = parsed;
            }
            // Only the add-on can see whether a follower account resolved or drifted. The
            // configuration half of this block is stamped back on in GetSnapshot, so what is
            // parsed here can never overwrite the trader's settings — same discipline as the
            // trend, and for the same reason.
            if (statePayload.TryGetProperty("copier", out var copier) && copier.ValueKind == JsonValueKind.Object)
            {
                var parsed = JsonSerializer.Deserialize<CopierStatus>(copier.GetRawText(), CamelCase);
                if (parsed != null) _state.Copier = parsed;
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

    /// <summary>
    /// Sets the cooldown duration applied after the NEXT losing trade. A cooldown already running
    /// keeps its original deadline: shortening it mid-run would hand the trader a way to lift the
    /// pause he had just asked for, by editing a setting.
    /// </summary>
    public int SetCooldownSeconds(int seconds)
    {
        lock (_lock)
        {
            _cooldownSeconds = Math.Clamp(seconds, MinCooldownSeconds, MaxCooldownSeconds);
            _logger.LogInformation("Cooldown duration set to {Secs}s", _cooldownSeconds);
            return _cooldownSeconds;
        }
    }

    /// <summary>
    /// Arme ou désarme la macro Tendance. Refusé tant que le blocage n'est pas autorisé par la
    /// configuration de la touche — sinon un maintien armerait une protection que le trader n'a
    /// jamais demandée, et il la découvrirait au premier ordre refusé.
    /// </summary>
    public (bool Ok, string? ErrorCode, string? ErrorMessage, bool Armed) ToggleTrendArmed()
    {
        lock (_lock)
        {
            if (!_trendBlockingAllowed)
            {
                _logger.LogWarning("Trend arming REFUSED — blocking is not enabled on the Trend key");
                return (false, "TREND_BLOCKING_DISABLED",
                    "Trend blocking is switched off on this key. Enable it in the key settings before arming.",
                    false);
            }

            _trendArmed = !_trendArmed;
            _logger.LogWarning("Trend macro {State} — direction is {Direction} ({Available})",
                _trendArmed ? "ARMED" : "DISARMED",
                _state.Trend.Direction,
                _state.Trend.Available ? "available" : "no data");

            return (true, null, null, _trendArmed);
        }
    }

    /// <summary>
    /// Adopte l'autorisation de blocage venue des réglages de la touche.
    ///
    /// Retirer l'autorisation DÉSARME. Masquer une règle sans la neutraliser est le pire des deux
    /// mondes — la même exigence que celle qui gouverne les champs `showIf` de l'éditeur : le
    /// trader qui décoche « Bloquer les trades » doit obtenir un deck qui ne refuse plus rien, pas
    /// une macro restée armée dont plus rien à l'écran ne dit qu'elle l'est.
    /// </summary>
    public void SetTrendBlockingAllowed(bool allowed)
    {
        lock (_lock)
        {
            if (_trendBlockingAllowed == allowed) return;
            _trendBlockingAllowed = allowed;

            if (!allowed && _trendArmed)
            {
                _trendArmed = false;
                _logger.LogWarning("Trend macro DISARMED — blocking was switched off in the key settings");
            }

            _logger.LogInformation("Trend blocking {State}", allowed ? "ENABLED" : "DISABLED");
        }
    }

    /// <summary>
    /// La Tendance refuse-t-elle cet ordre ?
    ///
    /// Trois portes avant d'en arriver à la direction, et chacune tient à une raison :
    ///
    ///   - non armée, ou blocage non autorisé — la macro est indicative, elle ne refuse rien ;
    ///   - tendance INCONNUE — « on ne sait pas » n'est pas « on interdit ». Même posture que
    ///     `pnlAvailable=false` sur les règles de perte : un roll de contrat ou un flux figé ne
    ///     doivent pas enfermer le trader hors de ses propres touches ;
    ///   - ordre qui RÉDUIT l'exposition — il passe toujours. En tendance haussière avec un short
    ///     ouvert, l'achat de clôture doit partir. C'est la règle qu'aucune macro de ce deck n'a le
    ///     droit d'enfreindre : enfermer le trader dans une position est le seul résultat interdit.
    /// </summary>
    public (bool Blocked, string? Message) IsTrendBlocked(string action)
    {
        lock (_lock)
        {
            if (!_trendArmed || !_trendBlockingAllowed) return (false, null);

            var trend = _state.Trend;
            if (!trend.Available) return (false, null);

            var sens = trend.Direction;
            if (sens is not ("up" or "down")) return (false, null);

            if (!CreatesExposure(action, out var buying)) return (false, null);

            var contreSens = buying ? sens == "down" : sens == "up";
            if (!contreSens) return (false, null);

            var libelle = buying ? "buying" : "selling";
            return (true,
                $"Trend is {sens.ToUpperInvariant()} ({trend.ReferenceMinutes}min={trend.Reference}"
                + (trend.HigherMinutes > 0 ? $", {trend.HigherMinutes}min={trend.Higher}" : string.Empty)
                + $"): {libelle} against it is refused while the Trend macro is armed. "
                + "Closing and reducing stay available.");
        }
    }

    /// <summary>
    /// Cet ordre créerait-il de l'exposition, et dans quel sens ?
    ///
    /// Reprend la logique de <c>SafetyMacro.CreatesExposure</c>. Direction seule, jamais la taille :
    /// une vente en position longue passe quelle que soit sa quantité. `reverse` s'évalue sur le
    /// sens qu'il OUVRE et non sur celui qu'il ferme — retourner ne réduit rien, cela remet la même
    /// taille en face.
    /// </summary>
    private bool CreatesExposure(string action, out bool buying)
    {
        var position = _state.Position;
        var direction = position is { Exists: true } && position.Quantity > 0 ? position.Direction : "Flat";

        if (action.Equals("reverse", StringComparison.OrdinalIgnoreCase))
        {
            buying = direction == "Short";
            return true;
        }

        buying = action.StartsWith("buy", StringComparison.OrdinalIgnoreCase);
        var selling = action.StartsWith("sell", StringComparison.OrdinalIgnoreCase);
        if (!buying && !selling) return false;

        if (direction is not ("Long" or "Short")) return true;

        return (direction == "Long" && buying) || (direction == "Short" && selling);
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

        // Realized is forwarded on its own as well as inside the sum. The loss rules want the sum
        // (an open loser counts against you), but the anti-tilt give-back rule needs realized
        // alone: a high-water mark taken on realized+unrealized would score every trade that runs
        // up and retraces as a give-back, and fire on ordinary price movement.
        _safety.UpdatePnl((realized ?? 0) + (unrealized ?? 0), realized);
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
