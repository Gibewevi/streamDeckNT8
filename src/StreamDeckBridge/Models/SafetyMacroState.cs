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

    /// <summary>How long the macro stays locked (undisableable) once armed.</summary>
    public double LockDurationHours { get; set; } = 6;

    public SafetyMacroSettings Clone() => new()
    {
        MaxTradesWhenLosing = MaxTradesWhenLosing,
        DailyLossLimit = DailyLossLimit,
        LockDurationHours = LockDurationHours
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
    public int TradeCount { get; set; }
    public double SessionPnl { get; set; }

    /// <summary>False when NinjaTrader does not expose account P&amp;L — the P&amp;L-based rules are then inert.</summary>
    public bool PnlAvailable { get; set; }

    /// <summary>True when position-opening actions are currently refused.</summary>
    public bool EntriesBlocked { get; set; }

    /// <summary>"", "dailyLoss" or "tradeLimit".</summary>
    public string BlockReason { get; set; } = string.Empty;

    public string TradingDay { get; set; } = string.Empty;
}
