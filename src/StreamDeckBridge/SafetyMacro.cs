using System.Text.Json;
using Microsoft.Extensions.Logging;
using StreamDeckBridge.Models;

namespace StreamDeckBridge;

/// <summary>
/// Lockable trading safety macro.
///
/// The trader arms it before a session. Once armed it cannot be disarmed until the
/// lock expires (6h by default) — there is no manual unlock, by design. While armed
/// it refuses every position-opening action as soon as a configured limit is reached:
///
///   - <see cref="SafetyMacroSettings.MaxTradesWhenLosing"/>: max trades allowed while the session P&amp;L is negative
///   - <see cref="SafetyMacroSettings.DailyLossLimit"/>: max session loss
///
/// Evaluation happens in the bridge, before anything is forwarded to NinjaTrader, so a
/// blocked key press never produces an order. Protective actions (flatten, cancel,
/// break-even, move stop/target) are never blocked — the trader must always be able to exit.
///
/// Settings and lock state are persisted so that restarting the bridge, the plugin or
/// Stream Deck cannot be used to bypass an active lock.
///
/// The macro also hosts the ANTI-TILT rules, which work on a different principle and must not be
/// confused with the limits above:
///
///   - the limits are accounting facts (loss, trade count) and they REFUSE orders, for hours;
///   - the anti-tilt rules are behavioural and they refuse NOTHING. They only report that entries
///     should require a long, deliberate press. That friction is applied by the host.
///
/// Consequences worth stating, because the whole design rests on them: an anti-tilt trigger never
/// touches <see cref="SafetyMacroPersistedState.LockedUntilUtc"/>, never arms the macro, and never
/// reaches <see cref="Evaluate"/>. It cannot lock the trader out of the deck. That was the explicit
/// requirement — a tilt pause lasts minutes and hands control back on its own.
/// </summary>
public sealed class SafetyMacro
{
    public const int MaxTradeLimit = 999;
    public const double MaxDailyLossLimit = 1_000_000;
    public const double MinLockHours = 0.05;   // 3 minutes — keeps manual testing practical
    public const double MaxLockHours = 24;

    public const int MaxContractLimit = 1_000;

    // --- Anti-tilt thresholds: fixed or derived, never asked of the trader ---
    //
    // Filling in eleven fields to protect yourself from your own impulses is a contradiction: the
    // moment you most need the rule is the moment you would have loosened it. So these come from
    // established risk practice instead, and the two that need a scale are derived from the limits
    // the trader already sets for Guard — which is what makes them fit a 25k account and a much
    // larger one without a second form.

    /// <summary>
    /// Size increase after a LOSING trade that counts as escalation. Fixed, because the principle
    /// behind it is not a preference: anti-martingale sizing says never add size to recover a loss,
    /// and the break-even effect (Thaler &amp; Johnson, 1990) is precisely the reflex to do it. 50%
    /// leaves room for an ordinary step while catching a doubling.
    /// </summary>
    private const double TiltSizeEscalationPct = 50;

    /// <summary>
    /// Share of the trader's own daily loss limit that, once handed back from the session's peak,
    /// opens an episode. Give-back rules are standard in proprietary trading risk desks; expressing
    /// the threshold as a fraction of the loss the trader already accepts scales it to the account
    /// instead of inventing a currency amount that fits nobody.
    /// </summary>
    private const double TiltGiveBackShareOfDailyLoss = 0.5;

    /// <summary>Consecutive losses as a share of the trader's own trade budget — a third of it.
    /// A fixed "stop after 3" is the classic desk rule, but at a scalper's trade count it fires on
    /// ordinary variance; tying it to the budget keeps the intent without the false positives.</summary>
    private const double TiltLossStreakShareOfBudget = 1.0 / 3.0;
    private const int MinTiltConsecutiveLosses = 3;
    private const int DefaultTiltConsecutiveLosses = 5;

    /// <summary>Cooling-off period. Long enough to break the loop, short enough that a session is
    /// never written off — which is exactly the requirement that separates this from Guard's lock.</summary>
    private const double DefaultTiltEpisodeMinutes = 15;

    /// <summary>Hold required on an entry while under friction. Set by the trader's own brief:
    /// 15 to 30 seconds, "not less" — below that the gesture is pushed through without thinking.</summary>
    private const int DefaultTiltHoldSeconds = 20;

    // Bounds for the two durations the advanced section may override.
    public const int MinTiltHoldSeconds = 15;
    public const int MaxTiltHoldSeconds = 30;
    public const double MinTiltEpisodeMinutes = 1;
    public const double MaxTiltEpisodeMinutes = 60;

    // --- Mandatory break bounds ---
    // The floor is 5 minutes rather than 1: below that the rule stops being a break and becomes a
    // rate limiter, which is not what it is for. 8 hours is a full session at the top end.
    public const double MinPauseAfterMinutes = 5;
    public const double MaxPauseAfterMinutes = 480;
    public const double MinPauseDurationMinutes = 1;
    public const double MaxPauseDurationMinutes = 120;

    // --- Automatic liquidation ---
    // The floor is 1 s and not 0: a zero delay would fire on the very tick that crosses the
    // threshold, which is exactly the wick this delay exists to ride out. 60 s at the top — beyond
    // that the limit stops being a circuit breaker and becomes a suggestion.
    public const double MinAutoFlattenGraceSeconds = 1;
    public const double MaxAutoFlattenGraceSeconds = 60;

    /// <summary>Actions that open or flip a position. Everything else stays available while blocked.</summary>
    private static readonly HashSet<string> EntryActions = new(StringComparer.OrdinalIgnoreCase)
    {
        "buyMarket", "sellMarket", "buyLimit", "sellLimit", "reverse"
    };

    private readonly ILogger<SafetyMacro> _logger;
    private readonly string _statePath;
    private readonly object _lock = new();

    private SafetyMacroPersistedState _state = new();
    private double? _accountPnl;
    private DateTime _lastPnlPersistedAt = DateTime.MinValue;
    private static readonly TimeSpan PnlPersistInterval = TimeSpan.FromSeconds(30);

    // --- Live position context, for the contextual anti-tilt conditions ---
    //
    // Runtime only, never persisted: these are recomputed from the position on every state update,
    // so there is nothing for them to survive across a restart.
    /// <summary>
    /// When the daily loss limit started being breached without interruption. Reset the moment
    /// session P&amp;L climbs back above the threshold, which is what makes a wick harmless.
    ///
    /// In memory only, deliberately: after a restart the bridge should watch the breach itself
    /// rather than inherit a countdown it never saw run.
    /// </summary>
    private DateTime? _lossBreachSince;

    private double? _realizedPnl;
    private int _positionQuantity;
    private string _positionDirection = "Flat";
    private int _lastSeenPositionQuantity;
    private bool _averagingLatched;

    private static readonly JsonSerializerOptions FileJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public SafetyMacro(BridgeConfig config, ILogger<SafetyMacro> logger)
    {
        _logger = logger;
        _statePath = ResolveStatePath(config);

        Load(config);

        lock (_lock)
        {
            // Resume from the last known P&L so the loss rules are live immediately,
            // instead of staying inert until NinjaTrader publishes again.
            _accountPnl = _state.LastAccountPnl;

            RollTradingDay();
            ExpireLockIfDue();

            if (_state.Armed)
            {
                _logger.LogWarning(
                    "Safety macro is ARMED and locked for another {Remaining} (restored from {Path})",
                    FormatDuration(RemainingLock()), _statePath);
            }
            else
            {
                _logger.LogInformation(
                    "Safety macro DISARMED — maxTradesWhenLosing={MaxTrades}, dailyLossLimit={Loss}, lockDuration={Hours}h",
                    _state.Settings.MaxTradesWhenLosing, _state.Settings.DailyLossLimit, _state.Settings.LockDurationHours);
            }

            if (RemainingTilt() > TimeSpan.Zero)
            {
                _logger.LogWarning("Anti-tilt episode restored ({Reason}) — {Remaining} left",
                    _state.TiltReason, FormatDuration(RemainingTilt()));
            }
        }
    }

    /// <summary>
    /// Arms the macro and starts the lock window. Idempotent while already armed
    /// (so a double key press never extends an existing lock).
    /// </summary>
    public (bool Ok, string? ErrorCode, string? ErrorMessage, SafetyStatus Status) Arm()
    {
        lock (_lock)
        {
            Refresh();

            if (_state.Armed)
                return (true, null, null, BuildStatus());

            _state.Armed = true;
            _state.ArmedAtUtc = DateTime.UtcNow;
            _state.LockedUntilUtc = DateTime.UtcNow.AddHours(_state.Settings.LockDurationHours);
            _state.BaselinePnl ??= _accountPnl;
            Persist();

            _logger.LogWarning(
                "Safety macro ARMED until {Until:u} ({Hours}h) — maxTradesWhenLosing={MaxTrades}, dailyLossLimit={Loss}, trades today={Trades}, session P&L={Pnl:0.##}",
                _state.LockedUntilUtc, _state.Settings.LockDurationHours, _state.Settings.MaxTradesWhenLosing,
                _state.Settings.DailyLossLimit, _state.TradeCount, SessionPnl());

            return (true, null, null, BuildStatus());
        }
    }

    /// <summary>
    /// Disarms the macro. Refused while the lock is still running — that refusal is the whole
    /// point of the feature.
    /// </summary>
    /// <param name="force">
    /// Development escape hatch, opt-in per key in the host ("mode développement" on the Safety
    /// key). It exists only because the macro is untestable otherwise: every trial locks the
    /// trader out for the whole lock duration, six hours by default.
    ///
    /// It does NOT weaken the macro while armed — entries stay blocked exactly as before. It
    /// only lifts the refusal to disarm, and the trader still has to press the key deliberately.
    /// Every use is logged at warning level so a forced unlock is always visible afterwards.
    /// </param>
    public (bool Ok, string? ErrorCode, string? ErrorMessage, SafetyStatus Status) Disarm(bool force = false)
    {
        lock (_lock)
        {
            Refresh();

            if (!_state.Armed)
                return (true, null, null, BuildStatus());

            var remaining = RemainingLock();
            if (remaining > TimeSpan.Zero)
            {
                if (!force)
                {
                    _logger.LogWarning("Safety macro disarm REFUSED — locked for another {Remaining}", FormatDuration(remaining));
                    return (false, "SAFETY_MACRO_LOCKED",
                        $"Safety macro is locked for another {FormatDuration(remaining)}. It cannot be disabled before the lock expires.",
                        BuildStatus());
                }

                _logger.LogWarning(
                    "Safety macro FORCE-DISARMED with {Remaining} of lock left — development mode is enabled on the Safety key",
                    FormatDuration(remaining));
            }

            DisarmInternal(force && remaining > TimeSpan.Zero ? "forced (dev mode)" : "manual");
            Persist();
            return (true, null, null, BuildStatus());
        }
    }

    /// <summary>Arms when disarmed, attempts to disarm when armed. Backs the single Stream Deck key.</summary>
    public (bool Ok, string? ErrorCode, string? ErrorMessage, SafetyStatus Status) Toggle(bool force = false)
    {
        // Monitor is reentrant, so delegating under the same lock is safe and keeps
        // the arm/disarm decision atomic with respect to the lock deadline.
        lock (_lock)
        {
            Refresh();
            return _state.Armed ? Disarm(force) : Arm();
        }
    }

    /// <summary>
    /// Updates the rules. Refused while armed, so the trader cannot loosen the limits
    /// mid-session. Only the supplied fields are changed.
    ///
    /// The mandatory break is the exception, because it is no longer a Guard rule: it has its own
    /// macro, applies whether or not Guard is armed, and its settings have therefore no business
    /// being held by Guard's lock. Someone who never arms Guard must still be able to set his
    /// rhythm; someone who arms it must still be able to change that rhythm.
    ///
    /// It has its own guard instead, and a narrower one: not while a break is actually running.
    /// Editing the rhythm mid-break would turn the break into a suggestion, which is the one thing
    /// it cannot be.
    /// </summary>
    public (bool Ok, string? ErrorCode, string? ErrorMessage, SafetyStatus Status) Configure(SafetyConfigUpdate update)
    {
        lock (_lock)
        {
            Refresh();

            var toucheLaPause = update.PauseAfterMinutes.HasValue || update.PauseDurationMinutes.HasValue;
            var pauseSeule = toucheLaPause && update.OnlyPauseFields;

            if (_state.Armed && !pauseSeule)
            {
                return (false, "SAFETY_MACRO_LOCKED",
                    $"Safety macro settings are locked for another {FormatDuration(RemainingLock())}.",
                    BuildStatus());
            }

            if (toucheLaPause && FindPauseBreach(null) != null)
            {
                return (false, "PAUSE_IN_PROGRESS",
                    "A mandatory break is running: its settings cannot be changed until it ends.",
                    BuildStatus());
            }

            var settings = _state.Settings;

            if (update.MaxTradesWhenLosing.HasValue)
                settings.MaxTradesWhenLosing = Math.Clamp(update.MaxTradesWhenLosing.Value, 0, MaxTradeLimit);

            if (update.DailyLossLimit.HasValue)
                settings.DailyLossLimit = Math.Clamp(Math.Abs(update.DailyLossLimit.Value), 0, MaxDailyLossLimit);

            if (update.LockDurationHours.HasValue)
                settings.LockDurationHours = Math.Clamp(update.LockDurationHours.Value, MinLockHours, MaxLockHours);

            if (update.AntiTiltEnabled.HasValue)
                settings.AntiTiltEnabled = update.AntiTiltEnabled.Value;

            if (update.TiltAveragingAllowed.HasValue)
                settings.TiltAveragingAllowed = update.TiltAveragingAllowed.Value;

            if (update.MaxContracts.HasValue)
                settings.MaxContracts = Math.Clamp(update.MaxContracts.Value, 0, MaxContractLimit);

            if (update.TiltAdvanced.HasValue)
                settings.TiltAdvanced = update.TiltAdvanced.Value;

            if (update.TiltHoldSeconds.HasValue)
                settings.TiltHoldSeconds = Math.Clamp(update.TiltHoldSeconds.Value, MinTiltHoldSeconds, MaxTiltHoldSeconds);

            if (update.TiltEpisodeMinutes.HasValue)
                settings.TiltEpisodeMinutes = Math.Clamp(update.TiltEpisodeMinutes.Value, MinTiltEpisodeMinutes, MaxTiltEpisodeMinutes);

            // 0 stays 0 — it is how the rule is switched off, like DailyLossLimit and MaxContracts.
            // Clamping it up to the floor would silently turn the rule ON for someone who typed 0.
            if (update.PauseAfterMinutes.HasValue)
            {
                var demande = update.PauseAfterMinutes.Value;
                settings.PauseAfterMinutes = demande <= 0
                    ? 0
                    : Math.Clamp(demande, MinPauseAfterMinutes, MaxPauseAfterMinutes);
            }

            if (update.PauseDurationMinutes.HasValue)
                settings.PauseDurationMinutes = Math.Clamp(update.PauseDurationMinutes.Value, MinPauseDurationMinutes, MaxPauseDurationMinutes);

            if (update.AutoFlattenOnDailyLoss.HasValue)
                settings.AutoFlattenOnDailyLoss = update.AutoFlattenOnDailyLoss.Value;

            if (update.AutoFlattenGraceSeconds.HasValue)
                settings.AutoFlattenGraceSeconds = Math.Clamp(update.AutoFlattenGraceSeconds.Value, MinAutoFlattenGraceSeconds, MaxAutoFlattenGraceSeconds);

            Persist();

            // The derived thresholds are logged alongside the settings, because they are the only
            // place the trader can see what the rules actually resolved to for their limits.
            _logger.LogInformation(
                "Safety macro configured — maxTradesWhenLosing={MaxTrades}, dailyLossLimit={Loss}, maxContracts={Contracts}, lockDuration={Hours}h, "
                + "antiTilt={Tilt} (averaging={Averaging}, advanced={Advanced}), break={Break} "
                + "— effective: escalation={Escalation}%, giveBack={GiveBack:0.##}, lossStreak={Losses}, episode={Episode}min, hold={Hold}s",
                settings.MaxTradesWhenLosing, settings.DailyLossLimit, settings.MaxContracts, settings.LockDurationHours,
                settings.AntiTiltEnabled ? "ON" : "OFF",
                settings.TiltAveragingAllowed ? "allowed" : "forbidden",
                settings.TiltAdvanced ? "ON" : "OFF",
                settings.PauseAfterMinutes > 0
                    ? $"{settings.PauseDurationMinutes:0.##}min every {settings.PauseAfterMinutes:0.##}min of trading"
                    : "OFF",
                TiltSizeEscalationPct, DerivedGiveBackLimit(), DerivedLossStreak(),
                EffectiveEpisodeMinutes(), EffectiveHoldSeconds());

            return (true, null, null, BuildStatus());
        }
    }

    /// <summary>
    /// Decides whether an incoming command must be refused.
    /// Called by the router before any forwarding happens.
    /// </summary>
    /// <param name="quantity">
    /// Contracts the command would send. Needed by the contract cap, which is the only rule that
    /// depends on the size of the order rather than on the state of the day.
    /// </param>
    public (bool Blocked, string? ErrorCode, string? ErrorMessage) Evaluate(string action, int quantity = 0)
    {
        lock (_lock)
        {
            Refresh();

            if (!EntryActions.Contains(action))
                return (false, null, null);

            // The session rules come first: they describe the state of the day and are the reason
            // the trader armed the macro, so they must be the message he reads when both apply.
            if (_state.Armed)
            {
                var breach = FindBreach(action);
                if (breach != null)
                    return (true, breach.Value.Code, breach.Value.Message);
            }

            // The mandatory break is NOT gated on Armed either, and for its own reason: it has its
            // own macro on the deck. A rule whose key sits there, configured, but that only takes
            // effect when a DIFFERENT macro happens to be armed is a trap — the trader would set
            // it, see nothing happen, and conclude it does not work.
            //
            // It costs nothing to keep it standing: unlike the daily loss and the trade budget, a
            // break is owed for time spent trading, which is true whether or not a session is
            // formally under Guard.
            var pause = FindPauseBreach(action);
            if (pause != null)
                return (true, pause.Value.Code, pause.Value.Message);

            // The contract cap is deliberately NOT gated on Armed. It is a standing risk limit on
            // the account, not a session rule: unlike the daily loss and the trade budget it needs
            // no session to mean something, and a cap that quietly does nothing because the macro
            // was not armed is exactly the kind of limit that gets discovered after the damage.
            var overCap = FindContractBreach(action, quantity);
            return overCap == null
                ? (false, null, null)
                : (true, overCap.Value.Code, overCap.Value.Message);
        }
    }

    /// <summary>
    /// Contract cap, evaluated against the position this order would actually produce.
    ///
    /// Only orders that GROW the exposure are refused. An entry in the opposite direction reduces
    /// it, and refusing that would trap the trader inside an oversized position — the one thing
    /// none of these rules may ever do.
    /// </summary>
    private LimitBreach? FindContractBreach(string action, int quantity)
    {
        var cap = _state.Settings.MaxContracts;
        if (cap <= 0 || quantity <= 0) return null;

        var buying = action.StartsWith("buy", StringComparison.OrdinalIgnoreCase);
        var selling = action.StartsWith("sell", StringComparison.OrdinalIgnoreCase);

        // A reverse ends up the same size the other way round, so it never grows the exposure.
        if (!buying && !selling) return null;

        var held = _positionDirection is "Long" or "Short" ? _positionQuantity : 0;
        var sameWay = (_positionDirection == "Long" && buying) || (_positionDirection == "Short" && selling);

        var resulting = sameWay ? held + quantity : quantity;
        if (resulting <= cap) return null;

        return new LimitBreach("maxContracts", "SAFETY_MAX_CONTRACTS",
            $"Contract cap reached ({resulting} would exceed {cap}). The safety macro blocks orders that grow the position.");
    }

    /// <summary>
    /// Feeds the account-wide P&amp;L (realized + unrealized) that the loss rules are based on.
    /// The first sample of a trading day becomes that day's baseline.
    /// </summary>
    /// <param name="realizedPnl">
    /// Account realized P&amp;L on its own, when NinjaTrader publishes it. The give-back rule needs it
    /// separately from the sum above: measuring a high-water mark on realized+unrealized would score
    /// every trade that runs up and retraces as a give-back.
    /// </param>
    public void UpdatePnl(double accountPnl, double? realizedPnl = null)
    {
        lock (_lock)
        {
            Refresh();
            _accountPnl = accountPnl;
            _state.LastAccountPnl = accountPnl;
            TrackDailyLossBreach();

            if (realizedPnl.HasValue)
            {
                _realizedPnl = realizedPnl.Value;
                _state.BaselineRealizedPnl ??= realizedPnl.Value;
                TrackGiveBack();
            }

            if (_state.BaselinePnl == null)
            {
                _state.BaselinePnl = accountPnl;
                _lastPnlPersistedAt = DateTime.UtcNow;
                Persist();
                _logger.LogInformation("Safety macro P&L baseline for {Day} set to {Pnl:0.##}", _state.TradingDay, accountPnl);
                return;
            }

            // Throttled: state updates arrive every couple of seconds, and this value only
            // needs to be roughly current to survive a restart.
            if (DateTime.UtcNow - _lastPnlPersistedAt >= PnlPersistInterval)
            {
                _lastPnlPersistedAt = DateTime.UtcNow;
                Persist();
            }
        }
    }

    /// <summary>Called when NinjaTrader reports a position going from flat to open — one trade.</summary>
    /// <param name="quantity">Contracts actually opened, as published by NinjaTrader. Used as the
    /// reference for the next size-escalation check, and compared against the previous trade's.</param>
    public void RecordTradeOpened(int quantity)
    {
        lock (_lock)
        {
            Refresh();
            _state.TradeCount++;

            // Mandatory-break anchors. WorkStartedAtUtc is only set when no stretch is running:
            // the clock must measure the whole stretch, not restart at every trade inside it.
            var maintenant = DateTime.UtcNow;
            _state.WorkStartedAtUtc ??= maintenant;
            _state.LastTradeAtUtc = maintenant;

            // Anti-tilt C1 — size escalation after a loss. Compared BEFORE the reference is moved
            // on, and only after a losing trade: a scalper's size should be constant, so a jump
            // right after a loss has no technical explanation left. It is recovery.
            var reference = _state.LastTradeQuantity;
            if (_state.LastTradeWasLoss && reference > 0 &&
                quantity > reference * (1 + TiltSizeEscalationPct / 100))
            {
                OpenEpisode("sizeEscalation",
                    $"size went from {reference} to {quantity} after a losing trade");
            }

            _state.LastTradeQuantity = quantity;
            Persist();

            _logger.LogInformation("Safety macro: trade #{Count} of {Day} recorded, {Qty} contract(s) (session P&L {Pnl:0.##})",
                _state.TradeCount, _state.TradingDay, quantity, SessionPnl());
        }
    }

    /// <summary>
    /// Called when NinjaTrader reports a position going from open to flat — one trade closed.
    /// </summary>
    /// <param name="lastUnrealizedPnl">
    /// Last unrealized P&amp;L seen before the position vanished. It is an approximation of the
    /// trade's result — the same one the cooldown has always used — because the state stream has no
    /// per-trade P&amp;L. Good enough to tell a winner from a loser, which is all these rules ask.
    /// </param>
    public void RecordTradeClosed(double lastUnrealizedPnl)
    {
        lock (_lock)
        {
            Refresh();

            var wasLoss = lastUnrealizedPnl < 0;
            _state.LastTradeWasLoss = wasLoss;
            _state.ConsecutiveLosses = wasLoss ? _state.ConsecutiveLosses + 1 : 0;

            // The position is gone, so there is nothing left to be averaging into.
            _averagingLatched = false;
            _lastSeenPositionQuantity = 0;
            _positionQuantity = 0;

            // Anti-tilt C3 — consecutive losses, threshold derived from the trader's trade budget.
            var streak = DerivedLossStreak();
            if (_state.ConsecutiveLosses >= streak)
                OpenEpisode("consecutiveLosses", $"{_state.ConsecutiveLosses} losing trades in a row (limit {streak})");

            Persist();

            _logger.LogInformation("Safety macro: trade closed {Result} (approx. {Pnl:0.##}), {Streak} loss(es) in a row",
                wasLoss ? "at a LOSS" : "at a profit", lastUnrealizedPnl, _state.ConsecutiveLosses);
        }
    }

    /// <summary>
    /// Feeds the live position to the contextual anti-tilt conditions. Called on every state update,
    /// so it must stay cheap and silent: it only logs when a latch actually flips.
    /// </summary>
    public void UpdatePositionContext(bool exists, int quantity, string direction, double unrealizedPnl)
    {
        lock (_lock)
        {
            _positionDirection = exists ? direction : "Flat";

            if (!exists)
            {
                _averagingLatched = false;
                _lastSeenPositionQuantity = 0;
                _positionQuantity = 0;
                return;
            }

            // Anti-tilt C4 — adding to a losing position. Latched rather than momentary: the risk
            // is carried for as long as the reinforced position stays open, so the friction has to
            // last as long as the position does. Going back into profit clears it, because the
            // situation the rule guards against is over.
            if (unrealizedPnl >= 0)
            {
                if (_averagingLatched)
                    _logger.LogInformation("Anti-tilt: averaging condition cleared — position is back in profit");
                _averagingLatched = false;
            }
            else if (_lastSeenPositionQuantity > 0 && quantity > _lastSeenPositionQuantity && !_averagingLatched)
            {
                _averagingLatched = true;
                _logger.LogWarning(
                    "Anti-tilt: position increased from {Before} to {After} while losing {Pnl:0.##} — entries under friction until it closes or recovers",
                    _lastSeenPositionQuantity, quantity, unrealizedPnl);
            }

            _lastSeenPositionQuantity = quantity;
            _positionQuantity = quantity;
        }
    }

    public SafetyStatus GetStatus()
    {
        lock (_lock)
        {
            Refresh();
            return BuildStatus();
        }
    }

    // --- internals (all callers hold _lock) ---

    private readonly record struct LimitBreach(string Reason, string Code, string Message);

    /// <param name="action">
    /// The order being evaluated, or null when building the published status — where there is no
    /// particular order and the question is simply "are entries refused right now".
    /// </param>
    private LimitBreach? FindBreach(string? action)
    {
        var settings = _state.Settings;

        // The P&L rules come first: they are the more severe of the two families, so theirs is the
        // message the trader must read when a break and a loss limit apply at the same time.
        // Without account P&L from NinjaTrader they are meaningless — say so through PnlAvailable
        // rather than silently blocking or silently allowing.
        if (HasPnl())
        {
            var pnl = SessionPnl();

            if (settings.DailyLossLimit > 0 && pnl <= -settings.DailyLossLimit)
            {
                return new LimitBreach("dailyLoss", "SAFETY_DAILY_LOSS_REACHED",
                    $"Daily loss limit reached ({pnl:0.##} / -{settings.DailyLossLimit:0.##}). The safety macro blocks new positions.");
            }

            if (settings.MaxTradesWhenLosing > 0 && pnl < 0 && _state.TradeCount >= settings.MaxTradesWhenLosing)
            {
                return new LimitBreach("tradeLimit", "SAFETY_TRADE_LIMIT_REACHED",
                    $"Trade limit reached while losing ({_state.TradeCount}/{settings.MaxTradesWhenLosing}, session P&L {pnl:0.##}). The safety macro blocks new positions.");
            }
        }

        // The mandatory break is NOT here: it has its own macro, is evaluated outside the Armed
        // gate, and is checked by its callers alongside this one. See `Evaluate` and `BuildStatus`.
        return null;
    }

    // --- Mandatory break ---
    //
    // Three properties make this rule bearable, and each one is a deliberate choice:
    //
    //   1. The clock starts at the FIRST TRADE, not at session start. Watching the market costs
    //      nothing; only trading does.
    //   2. It is evaluated when an entry is attempted, never on a background timer. A break can
    //      therefore not elapse unnoticed while the trader is away from the desk — it exists only
    //      at the moment it would actually restrain someone.
    //   3. Time already spent away from the market counts towards it. Someone who stopped on his
    //      own is not made to serve the break twice, and someone who returns from lunch is not
    //      pushed into a break he has already, in substance, taken.
    //
    // Together these mean the rule is silent for anyone who is not trading, and firm for anyone
    // who is trading continuously — which is the only population it was written for.

    // --- Automatic liquidation on daily loss ---
    //
    // The only rule here that ACTS instead of refusing. Three safeguards, each answering a way it
    // could do more harm than the loss it prevents:
    //
    //   1. A grace delay, because session P&L includes unrealized. A wick that crosses the limit
    //      and comes back costs nothing when the rule merely refuses an entry; liquidating on it
    //      realises a loss that was still recoverable, at the worst price of the move.
    //   2. Once per trading day, persisted. A liquidated session must not be liquidated twice by a
    //      bridge restart.
    //   3. The latch closes only once the order has actually LEFT the machine. A send that failed
    //      because the add-on was disconnected sent nothing, so there is nothing to protect
    //      against repeating — it is retried. A send that left and was then rejected by
    //      NinjaTrader is NOT retried: firing market orders again after an unexplained refusal
    //      turns one problem into two. It surfaces as a failure instead, in red.

    /// <summary>The order the bridge must send. Null while nothing is due.</summary>
    public sealed record AutoFlattenRequest(double SessionPnl, double Limit);

    /// <summary>
    /// Follows how long the daily loss limit has been continuously breached. Called on every P&amp;L
    /// publish — a couple of times a second — so the reset is immediate the moment price comes back.
    /// </summary>
    private void TrackDailyLossBreach()
    {
        var settings = _state.Settings;

        if (!settings.AutoFlattenOnDailyLoss || settings.DailyLossLimit <= 0 || !HasPnl())
        {
            _lossBreachSince = null;
            return;
        }

        if (SessionPnl() > -settings.DailyLossLimit)
        {
            _lossBreachSince = null;
            return;
        }

        _lossBreachSince ??= DateTime.UtcNow;
    }

    /// <summary>
    /// The liquidation the bridge should send right now, or null.
    ///
    /// Pure: it latches nothing. The caller confirms with <see cref="MarkAutoFlattenSent"/> or
    /// <see cref="MarkAutoFlattenFailed"/> once it knows whether the order left the machine.
    /// </summary>
    public AutoFlattenRequest? PendingAutoFlatten()
    {
        lock (_lock)
        {
            Refresh();

            if (!_state.Armed) return null;
            var settings = _state.Settings;
            if (!settings.AutoFlattenOnDailyLoss) return null;
            if (_state.AutoFlattenDay == _state.TradingDay) return null;
            if (_lossBreachSince is not { } since) return null;
            if (DateTime.UtcNow - since < TimeSpan.FromSeconds(settings.AutoFlattenGraceSeconds)) return null;

            return new AutoFlattenRequest(SessionPnl(), settings.DailyLossLimit);
        }
    }

    /// <summary>Called once the liquidation has actually been written to the add-on socket.</summary>
    public void MarkAutoFlattenSent()
    {
        lock (_lock)
        {
            _state.AutoFlattenDay = _state.TradingDay;
            _state.AutoFlattenFailed = false;
            Persist();
            _logger.LogWarning(
                "Safety macro: ACCOUNT LIQUIDATION sent — daily loss limit {Limit:0.##} breached for {Grace:0.##}s (session P&L {Pnl:0.##})",
                _state.Settings.DailyLossLimit, _state.Settings.AutoFlattenGraceSeconds, SessionPnl());
        }
    }

    /// <summary>
    /// Called when the liquidation left the machine but did not go through. Latches like a success
    /// — see safeguard 3 above — and raises the flag the deck turns red on.
    /// </summary>
    public void MarkAutoFlattenFailed(string reason)
    {
        lock (_lock)
        {
            _state.AutoFlattenDay = _state.TradingDay;
            _state.AutoFlattenFailed = true;
            Persist();
            _logger.LogError(
                "Safety macro: ACCOUNT LIQUIDATION FAILED ({Reason}) — positions may still be open, manual action required",
                reason);
        }
    }

    /// <summary>Moment the current break ends: one break duration after the last trade opened.</summary>
    private DateTime? PauseEndsAt() =>
        _state.LastTradeAtUtc?.AddMinutes(_state.Settings.PauseDurationMinutes);

    /// <summary>Moment the break falls due: one work stretch after the first trade of the stretch.</summary>
    private DateTime? PauseDueAt() =>
        _state.WorkStartedAtUtc?.AddMinutes(_state.Settings.PauseAfterMinutes);

    /// <summary>
    /// Would this order CREATE exposure — open from flat, add to a position, or flip it?
    ///
    /// This is the line every blocking rule has to respect. An order in the reducing direction is a
    /// way out, and a rule that refuses a way out locks the trader inside a position he was trying
    /// to leave — the one outcome none of these rules may ever produce.
    ///
    /// The test is direction-based and deliberately never size-based: a sell while long goes
    /// through whatever its quantity, even the quantity that would flip the position. Someone
    /// deliberately over-sizing an exit to flip during a break is circumventing on purpose, it is
    /// journalled as such, and that costs far less than trapping everyone else in a losing trade.
    ///
    /// `flatten` and `cancelOrders` never reach here — they are not entry actions at all.
    /// </summary>
    private bool CreatesExposure(string action)
    {
        // A reverse closes and immediately re-opens the same size the other way round. It is an
        // entry, not an exit, and it is exactly the gesture a break exists to interrupt.
        if (action.Equals("reverse", StringComparison.OrdinalIgnoreCase)) return true;

        var buying = action.StartsWith("buy", StringComparison.OrdinalIgnoreCase);
        var selling = action.StartsWith("sell", StringComparison.OrdinalIgnoreCase);
        if (!buying && !selling) return false;

        // Flat: anything opens a position.
        if (_positionDirection is not ("Long" or "Short") || _positionQuantity <= 0) return true;

        // Holding: only the same direction grows it. The other way reduces or closes.
        return (_positionDirection == "Long" && buying) || (_positionDirection == "Short" && selling);
    }

    private LimitBreach? FindPauseBreach(string? action)
    {
        var settings = _state.Settings;
        if (settings.PauseAfterMinutes <= 0) return null;
        if (PauseDueAt() is not { } dueAt || PauseEndsAt() is not { } endsAt) return null;

        var now = DateTime.UtcNow;
        if (now < dueAt) return null;

        // Due, but the trader has already been away long enough — ExpirePauseIfServed will have
        // cleared the anchor. Checked here too so the rule stays correct on its own rather than
        // depending on the order two private methods happen to run in.
        if (now >= endsAt) return null;

        // The whole point: a break stops you from taking a NEW position, never from leaving the one
        // you are in. Getting out must stay available at all times — that is not a concession to
        // convenience, it is the condition that makes a forced break acceptable at all.
        //
        // A null action is the status query, not an order: the break is reported as running,
        // because opening IS refused. The exit stays open per key, which is what the deck renders.
        if (action != null && !CreatesExposure(action)) return null;

        return new LimitBreach("mandatoryPause", "SAFETY_MANDATORY_PAUSE",
            $"Mandatory break in progress: {FormatDuration(endsAt - now)} left "
            + $"(after {settings.PauseAfterMinutes:0.##} min of trading). Closing and reducing stay available.");
    }

    /// <summary>
    /// Clears the work anchor once the trader has been away from the market for a full break, so
    /// the next trade opens a fresh stretch.
    ///
    /// Deliberately NOT conditioned on the break having fallen due. The rule this enforces is
    /// "never trade more than PauseAfterMinutes without a PauseDurationMinutes break", so someone
    /// who already breaks up his own session has nothing left to enforce — and a voluntary break
    /// that did not reset the clock would be a break he is asked to take twice.
    ///
    /// It also removes the need to special-case anything: overnight, a weekend and lunch are all
    /// simply gaps longer than a break, and the trading-day roll has no business resetting a
    /// stretch that runs through local midnight.
    ///
    /// Run from <see cref="Refresh"/> and not from the block check, which must stay free of side
    /// effects — it is also called to build the published status.
    /// </summary>
    private void ExpirePauseIfServed()
    {
        if (_state.Settings.PauseAfterMinutes <= 0) return;
        if (_state.WorkStartedAtUtc == null) return;
        if (PauseEndsAt() is not { } finPause || DateTime.UtcNow < finPause) return;

        _state.WorkStartedAtUtc = null;
        Persist();
        _logger.LogInformation(
            "Safety macro: {Duration:0.##} min away from the market — mandatory-break clock reset",
            _state.Settings.PauseDurationMinutes);
    }

    // --- Anti-tilt ---

    /// <summary>
    /// Follows the session's realized high-water mark and opens an episode when too much of the
    /// day's gain has been handed back. This is Guard's blind spot: a trader who climbs to +600 and
    /// slides back to +100 is still POSITIVE on the day, so neither the loss limit nor the trade
    /// limit sees anything — while one of the most expensive tilt patterns runs its course.
    /// </summary>
    private void TrackGiveBack()
    {
        var session = SessionRealized();
        if (session > _state.HighWaterRealizedPnl)
        {
            _state.HighWaterRealizedPnl = session;
            return;
        }

        // Derived from the daily loss limit, so a trader who switched that limit off has given the
        // rule no scale to work with. Inert is the honest answer there — inventing a currency
        // amount would be a threshold nobody chose and nobody could predict.
        var limit = DerivedGiveBackLimit();
        if (limit <= 0) return;

        // The high-water must be worth at least the limit before a give-back can be measured
        // against it. Without this, a day that only ever went down would trip the rule on its way
        // through — but that is a plain loss, and plain losses are Guard's job, not this one's.
        if (_state.HighWaterRealizedPnl < limit) return;

        var givenBack = _state.HighWaterRealizedPnl - session;
        if (givenBack >= limit)
        {
            OpenEpisode("giveBack",
                $"gave back {givenBack:0.##} of a {_state.HighWaterRealizedPnl:0.##} session peak (limit {limit:0.##})");
        }
    }

    /// <summary>Give-back threshold in account currency, as a share of the trader's daily loss
    /// limit. 0 when that limit is off, which leaves the rule inert.</summary>
    private double DerivedGiveBackLimit() => _state.Settings.DailyLossLimit * TiltGiveBackShareOfDailyLoss;

    /// <summary>Losing streak that opens an episode, as a share of the trader's trade budget,
    /// floored so the rule never becomes hair-trigger and never disappears entirely.</summary>
    private int DerivedLossStreak()
    {
        var budget = _state.Settings.MaxTradesWhenLosing;
        if (budget <= 0) return DefaultTiltConsecutiveLosses;
        return Math.Max(MinTiltConsecutiveLosses, (int)Math.Ceiling(budget * TiltLossStreakShareOfBudget));
    }

    // The two durations the advanced section may override. Reading them through the flag rather
    // than clearing the stored values means switching the section off restores the defaults
    // instantly, and switching it back on finds what had been typed.
    private int EffectiveHoldSeconds() =>
        _state.Settings.TiltAdvanced ? _state.Settings.TiltHoldSeconds : DefaultTiltHoldSeconds;

    private double EffectiveEpisodeMinutes() =>
        _state.Settings.TiltAdvanced ? _state.Settings.TiltEpisodeMinutes : DefaultTiltEpisodeMinutes;

    /// <summary>
    /// Opens a tilt episode, or leaves the running one alone. Deliberately never extends and never
    /// escalates: each episode lasts the same short while. A ladder would end up freezing the
    /// session, which is exactly what this feature must not do.
    /// </summary>
    private void OpenEpisode(string reason, string detail)
    {
        if (RemainingTilt() > TimeSpan.Zero)
        {
            _logger.LogDebug("Anti-tilt: {Reason} met during a running episode ({Detail}) — deadline left alone", reason, detail);
            return;
        }

        _state.TiltUntilUtc = DateTime.UtcNow.AddMinutes(EffectiveEpisodeMinutes());
        _state.TiltReason = reason;

        // Persisted here rather than left to the caller: the give-back rule fires from the P&L
        // path, whose own writes are throttled to 30s. Without this, restarting inside that window
        // would drop a pause the trader had already been handed.
        Persist();

        // Logged as a warning even when the friction is switched off. That is what makes the
        // disabled state a usable observation mode: the log says what the rules would have done.
        _logger.LogWarning(
            "Anti-tilt episode OPENED ({Reason}): {Detail} — {Minutes} min, friction {State}",
            reason, detail, EffectiveEpisodeMinutes(),
            _state.Armed && _state.Settings.AntiTiltEnabled ? "APPLIED" : "not applied (rules disabled)");
    }

    private TimeSpan RemainingTilt()
    {
        if (_state.TiltUntilUtc == null) return TimeSpan.Zero;
        var remaining = _state.TiltUntilUtc.Value - DateTime.UtcNow;
        return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
    }

    private void ExpireTiltIfDue()
    {
        if (_state.TiltUntilUtc == null || RemainingTilt() > TimeSpan.Zero) return;

        _logger.LogInformation("Anti-tilt episode ENDED ({Reason}) — entries back to normal", _state.TiltReason);
        _state.TiltUntilUtc = null;
        _state.TiltReason = string.Empty;
        Persist();
    }

    /// <summary>
    /// What the anti-tilt rules currently say. Two families, and they behave differently on purpose:
    /// an episode describes the TRADER, so it gates every entry; a contextual condition describes
    /// the POSITION, so it only gates orders that would make that position bigger.
    /// </summary>
    private (bool Active, string Reason, string Scope) TiltCondition()
    {
        if (RemainingTilt() > TimeSpan.Zero)
            return (true, _state.TiltReason, "all");

        var settings = _state.Settings;

        if (!settings.TiltAveragingAllowed && _averagingLatched)
            return (true, "averaging", "increaseOnly");

        return (false, string.Empty, string.Empty);
    }

    private double SessionRealized() =>
        _realizedPnl.HasValue && _state.BaselineRealizedPnl.HasValue
            ? _realizedPnl.Value - _state.BaselineRealizedPnl.Value
            : 0;

    private SafetyStatus BuildStatus()
    {
        // Session rules only count while armed; the mandatory break stands on its own, so its
        // status must be published even when the macro is disarmed — otherwise the Pause key would
        // sit dark while the break is refusing entries.
        LimitBreach? breach = (_state.Armed ? FindBreach(null) : null) ?? FindPauseBreach(null);

        // Sitting at the cap is published separately from EntriesBlocked, and on purpose: holding
        // your normal size is not an incident. Turning the Safety key red every time you reach it
        // would make red mean nothing, and hide a real block when one finally happens. Only the
        // keys that would grow the position react to this one.
        var atCap = _state.Settings.MaxContracts > 0 &&
                    _positionDirection is "Long" or "Short" &&
                    _positionQuantity >= _state.Settings.MaxContracts;

        var remaining = RemainingLock();
        var tilt = TiltCondition();
        var pause = PauseCountdown();
        var liquidation = AutoFlattenCountdown();

        // The friction is the only part gated on the switches. Detection, the reason and the log
        // all keep running while they are off, so the trader can calibrate the thresholds against
        // a real session before handing them any authority over the deck.
        var frictionOn = _state.Armed && _state.Settings.AntiTiltEnabled;

        return new SafetyStatus
        {
            TiltEnabled = _state.Settings.AntiTiltEnabled,
            TiltActive = frictionOn && tilt.Active,
            TiltSecondsRemaining = (int)Math.Ceiling(RemainingTilt().TotalSeconds),
            TiltReason = tilt.Reason,
            TiltScope = tilt.Scope,
            TiltHoldSeconds = EffectiveHoldSeconds(),
            Armed = _state.Armed,
            Locked = remaining > TimeSpan.Zero,
            LockSecondsRemaining = (int)Math.Ceiling(remaining.TotalSeconds),
            LockDurationHours = _state.Settings.LockDurationHours,
            MaxTradesWhenLosing = _state.Settings.MaxTradesWhenLosing,
            DailyLossLimit = _state.Settings.DailyLossLimit,
            MaxContracts = _state.Settings.MaxContracts,
            AtContractCap = atCap,
            TradeCount = _state.TradeCount,
            SessionPnl = SessionPnl(),
            PnlAvailable = HasPnl(),
            EntriesBlocked = breach != null,
            BlockReason = breach?.Reason ?? string.Empty,
            TradingDay = _state.TradingDay,
            PauseActive = breach?.Reason == "mandatoryPause",
            PauseSecondsRemaining = pause.Remaining,
            PauseDueInSeconds = pause.DueIn,
            AutoFlattenEnabled = _state.Settings.AutoFlattenOnDailyLoss,
            AutoFlattenPending = liquidation > 0,
            AutoFlattenSecondsRemaining = liquidation,
            AutoFlattenDone = _state.AutoFlattenDay == _state.TradingDay,
            AutoFlattenFailed = _state.AutoFlattenFailed
        };
    }

    /// <summary>
    /// Seconds left before the account is liquidated, 0 when nothing is pending.
    ///
    /// Published so the deck can count down out loud. A trader who sees "LIQUIDATION 4s" can still
    /// close on his own terms — which is a strictly better outcome than being flattened by
    /// surprise, and costs the rule nothing since the delay elapses either way.
    /// </summary>
    private int AutoFlattenCountdown()
    {
        if (!_state.Armed) return 0;
        if (!_state.Settings.AutoFlattenOnDailyLoss) return 0;
        if (_state.AutoFlattenDay == _state.TradingDay) return 0;
        if (_lossBreachSince is not { } since) return 0;

        var left = TimeSpan.FromSeconds(_state.Settings.AutoFlattenGraceSeconds) - (DateTime.UtcNow - since);
        return left > TimeSpan.Zero ? (int)Math.Ceiling(left.TotalSeconds) : 0;
    }

    /// <summary>
    /// The two countdowns the deck shows for the mandatory break: how long a running break still
    /// has to run, and how much trading time is left before one falls due.
    ///
    /// Both are published even when the macro is disarmed. The clock runs regardless — exactly like
    /// the trade count and the P&amp;L baseline — and only enforcement is gated on Armed; seeing the
    /// countdown before arming is what makes the rule predictable rather than a surprise.
    /// </summary>
    private (int Remaining, int DueIn) PauseCountdown()
    {
        // No Armed check: the break has its own macro and applies on its own.
        if (_state.Settings.PauseAfterMinutes <= 0) return (0, 0);
        if (PauseDueAt() is not { } echeance || PauseEndsAt() is not { } finPause) return (0, 0);

        var now = DateTime.UtcNow;
        var due = echeance - now;

        // Not due yet: nothing is running, and what matters is the warning.
        if (due > TimeSpan.Zero) return (0, (int)Math.Ceiling(due.TotalSeconds));

        var reste = finPause - now;
        return (reste > TimeSpan.Zero ? (int)Math.Ceiling(reste.TotalSeconds) : 0, 0);
    }

    private void Refresh()
    {
        RollTradingDay();
        ExpireLockIfDue();
        ExpireTiltIfDue();
        ExpirePauseIfServed();
    }

    private void RollTradingDay()
    {
        var today = DateTime.Now.ToString("yyyy-MM-dd");
        if (_state.TradingDay == today) return;

        _state.TradingDay = today;
        _state.TradeCount = 0;
        _state.BaselinePnl = _accountPnl;

        // The mandatory-break anchors are NOT reset here either, and for a stronger reason than the
        // episode: a trader working through local midnight is in one continuous stretch, and
        // handing him a fresh clock at 00:00 would be a free extension of exactly the kind the rule
        // exists to prevent. They need no reset — any gap longer than a break clears them, and a
        // night is always longer than a break.
        //
        // Anti-tilt counters are all about the day's behaviour, so they start over with it.
        // A running episode is NOT cleared here: it is a short pause the trader is serving, and a
        // day boundary falling inside it is no reason to hand back the keys early.
        _state.BaselineRealizedPnl = _realizedPnl;
        _state.HighWaterRealizedPnl = 0;
        _state.ConsecutiveLosses = 0;
        _state.LastTradeQuantity = 0;
        _state.LastTradeWasLoss = false;

        // A new day, a new liquidation allowance. The failure flag goes with it: it described
        // yesterday's positions, and leaving it up would turn a real alert into wallpaper.
        _state.AutoFlattenFailed = false;
        _lossBreachSince = null;
        Persist();

        _logger.LogInformation("Safety macro: trading day is now {Day} — trade count, P&L baseline and anti-tilt counters reset", today);
    }

    private void ExpireLockIfDue()
    {
        if (!_state.Armed || RemainingLock() > TimeSpan.Zero) return;

        DisarmInternal("lock expired");
        Persist();
    }

    private void DisarmInternal(string reason)
    {
        _state.Armed = false;
        _state.ArmedAtUtc = null;
        _state.LockedUntilUtc = null;
        _logger.LogWarning("Safety macro DISARMED ({Reason})", reason);
    }

    private TimeSpan RemainingLock()
    {
        if (!_state.Armed || _state.LockedUntilUtc == null) return TimeSpan.Zero;
        var remaining = _state.LockedUntilUtc.Value - DateTime.UtcNow;
        return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
    }

    private bool HasPnl() => _accountPnl.HasValue && _state.BaselinePnl.HasValue;

    private double SessionPnl() => HasPnl() ? _accountPnl!.Value - _state.BaselinePnl!.Value : 0;

    private static string FormatDuration(TimeSpan span)
    {
        if (span <= TimeSpan.Zero) return "0m";
        if (span.TotalHours >= 1) return $"{(int)span.TotalHours}h{span.Minutes:00}";
        if (span.TotalMinutes >= 1) return $"{(int)span.TotalMinutes}m";
        return $"{(int)span.TotalSeconds}s";
    }

    private static string ResolveStatePath(BridgeConfig config)
    {
        if (!string.IsNullOrWhiteSpace(config.SafetyStatePath))
            return config.SafetyStatePath;

        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "StreamDeckTrader");

        return Path.Combine(dir, "safety-macro.json");
    }

    private void Load(BridgeConfig config)
    {
        try
        {
            if (File.Exists(_statePath))
            {
                var loaded = JsonSerializer.Deserialize<SafetyMacroPersistedState>(File.ReadAllText(_statePath), FileJson);
                if (loaded?.Settings != null)
                {
                    _state = loaded;
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            // A corrupt file must not prevent the bridge from starting; fall back to defaults.
            _logger.LogError(ex, "Could not read safety macro state from {Path} — falling back to defaults", _statePath);
        }

        _state = new SafetyMacroPersistedState
        {
            Settings = new SafetyMacroSettings
            {
                MaxTradesWhenLosing = config.DefaultMaxTradesWhenLosing,
                DailyLossLimit = config.DefaultDailyLossLimit,
                LockDurationHours = config.DefaultSafetyLockHours
            }
        };
    }

    private void Persist()
    {
        try
        {
            var dir = Path.GetDirectoryName(_statePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(_statePath, JsonSerializer.Serialize(_state, FileJson));
        }
        catch (Exception ex)
        {
            // In-memory state stays authoritative for this process, so a failed write
            // degrades durability but never unlocks the macro.
            _logger.LogError(ex, "Could not persist safety macro state to {Path}", _statePath);
        }
    }
}
