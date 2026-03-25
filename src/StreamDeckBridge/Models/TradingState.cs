namespace StreamDeckBridge.Models;

/// <summary>
/// Tracks current trading state managed by the bridge.
/// </summary>
public sealed class TradingState
{
    public string Account { get; set; } = "Sim101";
    public string Instrument { get; set; } = "ES 06-25";
    public int Quantity { get; set; } = 1;
    public int DefaultQuantity { get; set; } = 1;
    public bool NtConnected { get; set; }
    public bool PluginConnected { get; set; }
    public PositionState? Position { get; set; }
    public InstrumentInfo? InstrumentInfo { get; set; }
    public List<string> AvailableAccounts { get; set; } = [];
}

public sealed class PositionState
{
    public bool Exists { get; set; }
    public string Direction { get; set; } = "Flat";
    public int Quantity { get; set; }
    public double AveragePrice { get; set; }
    public double UnrealizedPnl { get; set; }
    public bool HasStopOrder { get; set; }
    public double StopPrice { get; set; }
    public bool HasTargetOrder { get; set; }
    public double TargetPrice { get; set; }
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
