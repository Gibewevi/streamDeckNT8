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
    public bool CooldownEnabled { get; set; }
    public bool CooldownActive { get; set; }
    public int CooldownSecondsRemaining { get; set; }
    public SafetyStatus Safety { get; set; } = new();
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
