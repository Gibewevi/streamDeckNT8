namespace StreamDeckBridge.Models;

/// <summary>
/// Tracks current trading state managed by the bridge.
/// </summary>
public sealed class TradingState
{
    public string Account { get; set; } = string.Empty;
    public string Instrument { get; set; } = string.Empty;
    public int Quantity { get; set; } = 1;
    public int DefaultQuantity { get; set; } = 1;
    public bool NtConnected { get; set; }
    public bool PluginConnected { get; set; }
    public PositionState? Position { get; set; }
    public InstrumentInfo? InstrumentInfo { get; set; }
    public List<string> AvailableAccounts { get; set; } = [];

    /// <summary>
    /// Cash value du compte, telle que NinjaTrader la publie. Null quand le fournisseur ne l'expose
    /// pas — le journal Bitlearn garde alors son capital de départ actuel plutôt que d'en inventer un.
    ///
    /// Le bridge ne s'en sert pas : il ne fait que la transporter. Elle existe parce que le P&amp;L
    /// dit ce qui a changé, jamais à partir de QUOI — et sans ce point de départ, tout pourcentage
    /// affiché par le journal est faux.
    /// </summary>
    public double? CashValue { get; set; }
    public bool CooldownEnabled { get; set; }
    public bool CooldownActive { get; set; }
    public int CooldownSecondsRemaining { get; set; }

    /// <summary>Configured duration applied on the next losing trade — not the countdown.</summary>
    public int CooldownSeconds { get; set; }
    public SafetyStatus Safety { get; set; } = new();

    /// <summary>
    /// Market direction, computed by the add-on and merely carried by the bridge.
    ///
    /// Never null, so the host renders without a null check. A default instance means
    /// <see cref="TrendState.Available"/> is false, which is the honest answer before NinjaTrader
    /// has published anything — and which refuses nothing.
    /// </summary>
    public TrendState Trend { get; set; } = new();
}

/// <summary>
/// What the trend macro currently sees. Observation only in this version: nothing here refuses an
/// order, the bridge simply journals what it WOULD have refused so the thresholds can be calibrated
/// against a real session before being given any authority over the deck.
/// </summary>
public sealed class TrendState
{
    /// <summary>
    /// False while a series is missing, still loading, or stale. It means "we do not know", and the
    /// deck must read it as such: the same posture as <c>pnlAvailable</c> on the loss rules, and the
    /// reason a data hiccup can never lock the trader out of his own keys.
    /// </summary>
    public bool Available { get; set; }

    /// <summary>Combined verdict — <c>up</c>, <c>down</c> or <c>neutral</c>. The field that will decide.</summary>
    public string Direction { get; set; } = "neutral";

    /// <summary>Reference timeframe on its own. Display and diagnosis only.</summary>
    public string Reference { get; set; } = "neutral";

    /// <summary>Higher timeframe on its own, empty when that confirmation is switched off.</summary>
    public string Higher { get; set; } = string.Empty;

    /// <summary><c>structure</c> or <c>heikinAshi</c>.</summary>
    public string Method { get; set; } = "structure";

    /// <summary>
    /// Defaults mirror the catalog's, so the block published before NinjaTrader has ever spoken
    /// describes the macro as configured rather than as "0 minutes", which is not a timeframe.
    /// The add-on overwrites both as soon as it publishes.
    /// </summary>
    public int ReferenceMinutes { get; set; } = 1;

    /// <summary>0 when the higher-timeframe confirmation is off.</summary>
    public int HigherMinutes { get; set; } = 5;

    /// <summary>Seconds since the slowest series last gained a closed bar.</summary>
    public int StaleSeconds { get; set; }
}

public sealed class PositionState
{
    public bool Exists { get; set; }
    public string Direction { get; set; } = "Flat";
    public int Quantity { get; set; }
    public double AveragePrice { get; set; }
    public double UnrealizedPnl { get; set; }
    public bool HasStopOrder { get; set; }

    /// <summary>Price of the stop that protects the position most tightly.</summary>
    public double StopPrice { get; set; }

    /// <summary>Number of working stops — greater than 1 on a scaled position.</summary>
    public int StopOrderCount { get; set; }

    public bool HasTargetOrder { get; set; }

    /// <summary>Price of the nearest target in the position's direction.</summary>
    public double TargetPrice { get; set; }

    public int TargetOrderCount { get; set; }
    public int ActiveOrderCount { get; set; }
}

public sealed class InstrumentInfo
{
    public string Name { get; set; } = string.Empty;
    public double LastPrice { get; set; }
    public double OpenPrice { get; set; }
    public double SettlementPrice { get; set; }
    public double TickSize { get; set; }
    public double PointValue { get; set; }
}
