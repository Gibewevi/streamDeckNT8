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
/// </summary>
public sealed class SafetyMacro
{
    public const int MaxTradeLimit = 999;
    public const double MaxDailyLossLimit = 1_000_000;
    public const double MinLockHours = 0.05;   // 3 minutes — keeps manual testing practical
    public const double MaxLockHours = 24;

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
    /// </summary>
    public (bool Ok, string? ErrorCode, string? ErrorMessage, SafetyStatus Status) Configure(
        int? maxTradesWhenLosing, double? dailyLossLimit, double? lockDurationHours)
    {
        lock (_lock)
        {
            Refresh();

            if (_state.Armed)
            {
                return (false, "SAFETY_MACRO_LOCKED",
                    $"Safety macro settings are locked for another {FormatDuration(RemainingLock())}.",
                    BuildStatus());
            }

            var settings = _state.Settings;

            if (maxTradesWhenLosing.HasValue)
                settings.MaxTradesWhenLosing = Math.Clamp(maxTradesWhenLosing.Value, 0, MaxTradeLimit);

            if (dailyLossLimit.HasValue)
                settings.DailyLossLimit = Math.Clamp(Math.Abs(dailyLossLimit.Value), 0, MaxDailyLossLimit);

            if (lockDurationHours.HasValue)
                settings.LockDurationHours = Math.Clamp(lockDurationHours.Value, MinLockHours, MaxLockHours);

            Persist();

            _logger.LogInformation(
                "Safety macro configured — maxTradesWhenLosing={MaxTrades}, dailyLossLimit={Loss}, lockDuration={Hours}h",
                settings.MaxTradesWhenLosing, settings.DailyLossLimit, settings.LockDurationHours);

            return (true, null, null, BuildStatus());
        }
    }

    /// <summary>
    /// Decides whether an incoming command must be refused.
    /// Called by the router before any forwarding happens.
    /// </summary>
    public (bool Blocked, string? ErrorCode, string? ErrorMessage) Evaluate(string action)
    {
        lock (_lock)
        {
            Refresh();

            if (!_state.Armed || !EntryActions.Contains(action))
                return (false, null, null);

            var breach = FindBreach();
            return breach == null
                ? (false, null, null)
                : (true, breach.Value.Code, breach.Value.Message);
        }
    }

    /// <summary>
    /// Feeds the account-wide P&amp;L (realized + unrealized) that the loss rules are based on.
    /// The first sample of a trading day becomes that day's baseline.
    /// </summary>
    public void UpdatePnl(double accountPnl)
    {
        lock (_lock)
        {
            Refresh();
            _accountPnl = accountPnl;
            _state.LastAccountPnl = accountPnl;

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
    public void RecordTradeOpened()
    {
        lock (_lock)
        {
            Refresh();
            _state.TradeCount++;
            Persist();

            _logger.LogInformation("Safety macro: trade #{Count} of {Day} recorded (session P&L {Pnl:0.##})",
                _state.TradeCount, _state.TradingDay, SessionPnl());
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

    private LimitBreach? FindBreach()
    {
        // Without account P&L from NinjaTrader both rules are meaningless — say so
        // through PnlAvailable rather than silently blocking or silently allowing.
        if (!HasPnl()) return null;

        var settings = _state.Settings;
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

        return null;
    }

    private SafetyStatus BuildStatus()
    {
        LimitBreach? breach = _state.Armed ? FindBreach() : null;
        var remaining = RemainingLock();

        return new SafetyStatus
        {
            Armed = _state.Armed,
            Locked = remaining > TimeSpan.Zero,
            LockSecondsRemaining = (int)Math.Ceiling(remaining.TotalSeconds),
            LockDurationHours = _state.Settings.LockDurationHours,
            MaxTradesWhenLosing = _state.Settings.MaxTradesWhenLosing,
            DailyLossLimit = _state.Settings.DailyLossLimit,
            TradeCount = _state.TradeCount,
            SessionPnl = SessionPnl(),
            PnlAvailable = HasPnl(),
            EntriesBlocked = breach != null,
            BlockReason = breach?.Reason ?? string.Empty,
            TradingDay = _state.TradingDay
        };
    }

    private void Refresh()
    {
        RollTradingDay();
        ExpireLockIfDue();
    }

    private void RollTradingDay()
    {
        var today = DateTime.Now.ToString("yyyy-MM-dd");
        if (_state.TradingDay == today) return;

        _state.TradingDay = today;
        _state.TradeCount = 0;
        _state.BaselinePnl = _accountPnl;
        Persist();

        _logger.LogInformation("Safety macro: trading day is now {Day} — trade count and P&L baseline reset", today);
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
