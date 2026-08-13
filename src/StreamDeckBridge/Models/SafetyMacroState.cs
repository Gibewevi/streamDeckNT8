namespace StreamDeckBridge.Models;

/// <summary>
/// User-configurable rules of the safety macro.
/// Only editable while the macro is disarmed.
/// </summary>
public sealed class SafetyMacroSettings
{
    /// <summary>Max number of trades allowed once the session P&amp;L is negative. 0 disables the rule.</summary>
    public int MaxTradesWhenLosing { get; set; } = 15;

    /// <summary>Max session loss in account currency, expressed as a positive number. 0 disables the rule.</summary>
    public double DailyLossLimit { get; set; } = 300;

    /// <summary>
    /// Maximum contracts the account may hold. An entry that would take the position past it is
    /// REFUSED, like the two limits above — this is a risk limit, not a behavioural heuristic, so
    /// it belongs with Guard's accounting rules and not with the anti-tilt friction.
    /// 0 disables the rule, which is the default: nobody should start refusing orders after an
    /// update they did not ask for.
    /// </summary>
    public int MaxContracts { get; set; }

    /// <summary>How long the macro stays locked (undisableable) once armed.</summary>
    public double LockDurationHours { get; set; } = 6;

    // --- Anti-tilt ---
    //
    // These rules never refuse an order and never touch the lock deadline. They only tell the host
    // to demand a long, deliberate press before an entry leaves. Detection runs at all times; only
    // the friction is gated on Armed && AntiTiltEnabled, which makes the disabled state a free
    // observation mode: the log says what would have happened before the rules get any authority.
    //
    // ONLY THREE SETTINGS LIVE HERE, on purpose. Everything else — how much of a size jump counts
    // as escalation, how much given-back profit is too much, how many losses in a row, how long an
    // episode lasts, how long the press must be held — is derived by SafetyMacro from established
    // risk-management practice and from the two limits the trader already sets for Guard. A rule
    // nobody fills in is a rule that actually protects; a form of eleven fields gets abandoned.

    /// <summary>Master switch for the friction. Off by default, which still runs detection and
    /// logging — that is the intended way to calibrate before handing the rules any authority.</summary>
    public bool AntiTiltEnabled { get; set; }

    /// <summary>
    /// When false, adding to a losing position puts entries under friction until that position is
    /// closed or back in profit. Permissive by default: averaging down runs out of control on a
    /// small account, but it is a legitimate management choice on a larger one — so it is the
    /// trader's call, never a rule imposed by the software.
    /// </summary>
    public bool TiltAveragingAllowed { get; set; } = true;

    /// <summary>
    /// Opt-in for the two comfort durations below. When false they are ignored and the built-in
    /// values apply, so switching this off restores the defaults without losing what was typed.
    /// </summary>
    public bool TiltAdvanced { get; set; }

    /// <summary>Hold required on a slowed entry. Only used when <see cref="TiltAdvanced"/>.</summary>
    public int TiltHoldSeconds { get; set; } = 20;

    /// <summary>Episode length. Only used when <see cref="TiltAdvanced"/>.</summary>
    public double TiltEpisodeMinutes { get; set; } = 15;

    // --- Mandatory break ---
    //
    // An endurance rule, not a risk rule: attention decays with continuous screen time, and the
    // trader who most needs to step away is the least likely to decide it himself.
    //
    // The clock is anchored on the FIRST TRADE, never on the session start. Someone who watches
    // the market for three hours without touching it has spent nothing, and a rule that pauses him
    // anyway is experienced as arbitrary — which is how rules end up switched off.

    /// <summary>
    /// Minutes of trading, counted from the first trade, before a break falls due. 0 disables the
    /// rule, which is the default: nobody should start being refused entries after an update they
    /// did not ask for.
    /// </summary>
    public double PauseAfterMinutes { get; set; }

    /// <summary>
    /// How long the mandatory break lasts, in full, counted from the moment it falls due.
    ///
    /// It used to be counted from the last trade, so the time spent trading right up to the due
    /// moment was deducted from the break itself and the trader served a fraction of what he had
    /// configured. Time away from the market still counts — but BEFORE the break falls due, where
    /// it cancels the break outright rather than shortening it
    /// (see <see cref="SafetyMacroPersistedState.LastTradeAtUtc"/>).
    /// </summary>
    public double PauseDurationMinutes { get; set; } = 10;

    // --- Automatic liquidation on daily loss ---
    //
    // The only rule in this file that SENDS an order instead of refusing one. Everything else here
    // can, at worst, stop the trader from doing something; this one acts on his account by itself.
    // That asymmetry is why it is opt-in and why its failure path is loud.

    /// <summary>
    /// Close every position and cancel every order on the account when the daily loss limit is
    /// reached. OFF by default, and not negotiable: software that starts sending market orders
    /// after an update nobody asked for has no business being trusted with an account.
    /// </summary>
    public bool AutoFlattenOnDailyLoss { get; set; }

    /// <summary>
    /// Seconds the limit must stay breached before the account is liquidated.
    ///
    /// Session P&amp;L includes UNREALIZED, so a single wick can cross the threshold and come back.
    /// Refusing an entry on that wick costs nothing; liquidating on it turns a recoverable open
    /// loss into a realised one, at the worst price of the move. This delay is what separates the
    /// two — the limit has to still be breached once the wick has passed.
    /// </summary>
    public double AutoFlattenGraceSeconds { get; set; } = 5;

    public SafetyMacroSettings Clone() => new()
    {
        MaxTradesWhenLosing = MaxTradesWhenLosing,
        DailyLossLimit = DailyLossLimit,
        MaxContracts = MaxContracts,
        LockDurationHours = LockDurationHours,
        AntiTiltEnabled = AntiTiltEnabled,
        TiltAveragingAllowed = TiltAveragingAllowed,
        TiltAdvanced = TiltAdvanced,
        TiltHoldSeconds = TiltHoldSeconds,
        TiltEpisodeMinutes = TiltEpisodeMinutes,
        PauseAfterMinutes = PauseAfterMinutes,
        PauseDurationMinutes = PauseDurationMinutes,
        AutoFlattenOnDailyLoss = AutoFlattenOnDailyLoss,
        AutoFlattenGraceSeconds = AutoFlattenGraceSeconds
    };
}

/// <summary>
/// Everything the safety macro persists to disk.
/// The armed flag and the lock deadline are part of it on purpose: restarting the
/// bridge, the plugin or Stream Deck must never be a way to unlock the macro.
/// </summary>
public sealed class SafetyMacroPersistedState
{
    public SafetyMacroSettings Settings { get; set; } = new();
    public bool Armed { get; set; }
    public DateTime? ArmedAtUtc { get; set; }
    public DateTime? LockedUntilUtc { get; set; }

    /// <summary>Local calendar day (yyyy-MM-dd) the counters below belong to.</summary>
    public string TradingDay { get; set; } = string.Empty;

    /// <summary>Trades opened during <see cref="TradingDay"/>.</summary>
    public int TradeCount { get; set; }

    /// <summary>Account P&amp;L observed at the start of <see cref="TradingDay"/>. Session P&amp;L is measured from it.</summary>
    public double? BaselinePnl { get; set; }

    /// <summary>
    /// Last account P&amp;L seen from NinjaTrader. Persisted (throttled) so that a bridge
    /// restart resumes with a known P&amp;L instead of an inert loss rule.
    /// </summary>
    public double? LastAccountPnl { get; set; }

    // --- Anti-tilt episode ---
    //
    // Persisted for the same reason the lock is: restarting the bridge must not be a way to skip a
    // pause. The contextual conditions (averaging, contract cap) are deliberately absent — they are
    // recomputed from the live position on every state update, so they have nothing to survive.

    /// <summary>End of the running episode. Null when no episode is open.</summary>
    public DateTime? TiltUntilUtc { get; set; }

    /// <summary>What opened the running episode: "sizeEscalation", "giveBack" or "consecutiveLosses".</summary>
    public string TiltReason { get; set; } = string.Empty;

    /// <summary>Account realized P&amp;L observed at the start of <see cref="TradingDay"/>.</summary>
    public double? BaselineRealizedPnl { get; set; }

    /// <summary>
    /// Highest session REALIZED P&amp;L reached during <see cref="TradingDay"/>, measured from
    /// <see cref="BaselineRealizedPnl"/>. Realized and not realized+unrealized on purpose: a single
    /// trade running to +200 and retracing would otherwise register as a 200 give-back, and the
    /// rule would fire on every trade that breathes.
    /// </summary>
    public double HighWaterRealizedPnl { get; set; }

    /// <summary>Losing trades closed back to back. Reset by any winner.</summary>
    public int ConsecutiveLosses { get; set; }

    /// <summary>Quantity of the last trade opened, used as the reference for size escalation.</summary>
    public int LastTradeQuantity { get; set; }

    /// <summary>Whether the last trade closed at a loss. Escalation only counts after a loss.</summary>
    public bool LastTradeWasLoss { get; set; }

    // --- Mandatory break ---
    //
    // Persisted for the same reason the lock is: restarting the bridge must not be a way to skip a
    // break. Both are UTC so that a machine changing time zone mid-session cannot shorten one.

    /// <summary>
    /// Start of the current work stretch — the first trade opened while no stretch was running.
    /// Null before the first trade of the day, and again once a break has been served, so the next
    /// trade opens a fresh stretch.
    /// </summary>
    public DateTime? WorkStartedAtUtc { get; set; }

    /// <summary>
    /// Last trade opened. Measures the VOLUNTARY break: a gap of a full break duration before the
    /// break falls due clears the work stretch, so a trader who stopped on his own is never made to
    /// serve a break he has already taken.
    ///
    /// It does NOT measure the enforced break — that one runs from the due moment. Using this field
    /// for both is what made a configured break expire almost as soon as it started.
    /// </summary>
    public DateTime? LastTradeAtUtc { get; set; }

    // --- Automatic liquidation ---

    /// <summary>
    /// Trading day on which the account was last liquidated. Empty while it has not happened.
    ///
    /// No longer a latch that spends the rule for the day — see the comment block above
    /// <c>PendingAutoFlatten</c> for why that was wrong. It now says only "this has already
    /// happened today", which is what the deck shows and what the day's reading needs.
    ///
    /// The grace countdown is deliberately NOT persisted — a restart should observe the breach
    /// afresh rather than act on a timer it did not watch elapse.
    /// </summary>
    public string AutoFlattenDay { get; set; } = string.Empty;

    /// <summary>
    /// When the last liquidation was sent. Persisted, unlike the grace countdown, and for the
    /// original reason the day latch existed: a bridge restarting in the middle of a liquidation
    /// must not fire a second wave of market orders before NinjaTrader has reported flat.
    /// </summary>
    public DateTime? LastAutoFlattenUtc { get; set; }

    /// <summary>
    /// Liquidations during <see cref="TradingDay"/>. Reset with the other daily counters.
    ///
    /// Worth its own field rather than being derived: "the account was liquidated four times
    /// today" is a different session from "once", and nothing else in the state distinguishes them.
    /// </summary>
    public int AutoFlattenCount { get; set; }

    /// <summary>
    /// Set when the liquidation was sent but did not go through. The trader has to be told: he was
    /// shown "day over" while his position is still live, which is the one state worse than having
    /// no rule at all.
    /// </summary>
    public bool AutoFlattenFailed { get; set; }
}

/// <summary>
/// Partial update of the safety settings: every field is optional and only the ones supplied are
/// applied. Grouped into an object rather than eleven positional parameters, which is where the
/// previous signature was heading once the anti-tilt rules arrived.
/// </summary>
public sealed class SafetyConfigUpdate
{
    public int? MaxTradesWhenLosing { get; set; }
    public double? DailyLossLimit { get; set; }
    public int? MaxContracts { get; set; }
    public double? LockDurationHours { get; set; }
    public bool? AntiTiltEnabled { get; set; }
    public bool? TiltAveragingAllowed { get; set; }
    public bool? TiltAdvanced { get; set; }
    public int? TiltHoldSeconds { get; set; }
    public double? TiltEpisodeMinutes { get; set; }
    public double? PauseAfterMinutes { get; set; }
    public double? PauseDurationMinutes { get; set; }
    public bool? AutoFlattenOnDailyLoss { get; set; }
    public double? AutoFlattenGraceSeconds { get; set; }

    /// <summary>
    /// True when the update carries ONLY mandatory-break fields.
    ///
    /// The break has its own macro on the deck, so its key pushes its own settings and nothing
    /// else. That is what lets `Configure` let it through while Guard is armed without opening a
    /// hole: a payload that also carries a risk limit is not a break update and stays locked.
    /// </summary>
    public bool OnlyPauseFields =>
        MaxTradesWhenLosing == null && DailyLossLimit == null && MaxContracts == null &&
        LockDurationHours == null && AntiTiltEnabled == null && TiltAveragingAllowed == null &&
        TiltAdvanced == null && TiltHoldSeconds == null && TiltEpisodeMinutes == null &&
        AutoFlattenOnDailyLoss == null && AutoFlattenGraceSeconds == null;

    /// <summary>True when the caller supplied nothing at all — the router rejects that outright.</summary>
    public bool IsEmpty =>
        MaxTradesWhenLosing == null && DailyLossLimit == null && MaxContracts == null &&
        LockDurationHours == null && AntiTiltEnabled == null && TiltAveragingAllowed == null &&
        TiltAdvanced == null && TiltHoldSeconds == null && TiltEpisodeMinutes == null &&
        PauseAfterMinutes == null && PauseDurationMinutes == null &&
        AutoFlattenOnDailyLoss == null && AutoFlattenGraceSeconds == null;
}

/// <summary>
/// Read-only view of the safety macro, published inside every state update.
/// </summary>
public sealed class SafetyStatus
{
    public bool Armed { get; set; }
    public bool Locked { get; set; }
    public int LockSecondsRemaining { get; set; }
    public double LockDurationHours { get; set; }
    public int MaxTradesWhenLosing { get; set; }
    public double DailyLossLimit { get; set; }

    /// <summary>Contracts the account may hold. 0 = rule off.</summary>
    public int MaxContracts { get; set; }

    /// <summary>
    /// The position is already at or above the cap, so anything that grows it is refused.
    /// Published apart from <see cref="EntriesBlocked"/> because it is a normal state, not an
    /// incident: only the keys that would add to the position react to it.
    /// </summary>
    public bool AtContractCap { get; set; }

    public int TradeCount { get; set; }
    public double SessionPnl { get; set; }

    /// <summary>False when NinjaTrader does not expose account P&amp;L — the P&amp;L-based rules are then inert.</summary>
    public bool PnlAvailable { get; set; }

    /// <summary>True when position-opening actions are currently refused.</summary>
    public bool EntriesBlocked { get; set; }

    /// <summary>"", "dailyLoss", "tradeLimit" or "mandatoryPause". The contract cap has its own flag above.</summary>
    public string BlockReason { get; set; } = string.Empty;

    public string TradingDay { get; set; } = string.Empty;

    // --- Mandatory break ---

    /// <summary>
    /// True while the break is being enforced. Redundant with <see cref="EntriesBlocked"/> and a
    /// <see cref="BlockReason"/> of "mandatoryPause", and published anyway: the deck needs to tell
    /// a break apart from a loss limit without parsing a string.
    /// </summary>
    public bool PauseActive { get; set; }

    /// <summary>Seconds left on the running break. 0 when none is running.</summary>
    public int PauseSecondsRemaining { get; set; }

    /// <summary>
    /// Seconds of trading left before the break falls due. 0 when the rule is off, when nothing
    /// has been traded yet, or when the break is already due. Published so the deck can warn
    /// beforehand — a break that lands without notice is the surest way to get the rule turned off.
    /// </summary>
    public int PauseDueInSeconds { get; set; }

    // --- Automatic liquidation ---

    /// <summary>Whether the account is liquidated when the daily loss limit is reached.</summary>
    public bool AutoFlattenEnabled { get; set; }

    /// <summary>
    /// The limit is currently breached and the grace delay is running. Published so the deck can
    /// show the countdown: a trader who sees "LIQUIDATION 4s" can still flatten on his own terms,
    /// which is a better outcome than being liquidated by surprise.
    /// </summary>
    public bool AutoFlattenPending { get; set; }

    /// <summary>Seconds left before the account is liquidated. 0 when nothing is pending.</summary>
    public int AutoFlattenSecondsRemaining { get; set; }

    /// <summary>The account was liquidated today. The day is over.</summary>
    public bool AutoFlattenDone { get; set; }

    /// <summary>The liquidation was attempted and did not go through. Positions may still be open.</summary>
    public bool AutoFlattenFailed { get; set; }

    // --- Anti-tilt ---

    /// <summary>Whether the anti-tilt rules are allowed to add friction at all.</summary>
    public bool TiltEnabled { get; set; }

    /// <summary>
    /// True when the host must demand a long press before an entry leaves. False whenever the
    /// friction is switched off, even if a criterion is currently met — the criterion still shows
    /// through <see cref="TiltReason"/> and the log, which is what makes the disabled state usable
    /// as an observation mode.
    /// </summary>
    public bool TiltActive { get; set; }

    /// <summary>Seconds left on the running episode. 0 for the contextual conditions, which end
    /// with the situation rather than with a clock.</summary>
    public int TiltSecondsRemaining { get; set; }

    /// <summary>"", "sizeEscalation", "giveBack", "consecutiveLosses", "averaging" or "maxContracts".</summary>
    public string TiltReason { get; set; } = string.Empty;

    /// <summary>
    /// "all" for episodes, which describe the trader and therefore gate every entry.
    /// "increaseOnly" for the contextual conditions, which describe the position: refusing to
    /// speed up an order that REDUCES an oversized position would be exactly backwards.
    /// </summary>
    public string TiltScope { get; set; } = string.Empty;

    /// <summary>How long an entry key must be held while the friction applies.</summary>
    public int TiltHoldSeconds { get; set; }
}
