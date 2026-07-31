namespace StreamDeckBridge.Models;

public sealed class BridgeConfig
{
    public int PluginPort { get; set; } = 8218;
    public int AddonPort { get; set; } = 8219;
    public string DefaultAccount { get; set; } = "";
    public string DefaultInstrument { get; set; } = "";
    public int DefaultQuantity { get; set; } = 1;
    public int MinQuantity { get; set; } = 1;
    public int MaxQuantity { get; set; } = 100;
    public bool AllowLiveAccounts { get; set; } = true;
    public int MaxQueueSize { get; set; } = 50;
    public int StateUpdateIntervalMs { get; set; } = 2000;
    public int DuplicateRequestWindowSeconds { get; set; } = 60;

    // --- Safety macro (first-run defaults only; afterwards the persisted file wins) ---
    public int DefaultMaxTradesWhenLosing { get; set; } = 15;
    public double DefaultDailyLossLimit { get; set; } = 300;
    public double DefaultSafetyLockHours { get; set; } = 6;

    /// <summary>Override for the safety macro state file. Empty = %APPDATA%\StreamDeckTrader\safety-macro.json.</summary>
    public string SafetyStatePath { get; set; } = "";

    /// <summary>Override for the session file (selected instrument). Empty = %APPDATA%\StreamDeckTrader\session.json.</summary>
    public string SessionStatePath { get; set; } = "";

    // --- Logging (one file per day in the directory below) ---

    /// <summary>Override for the log directory. Empty = %APPDATA%\StreamDeckTrader\logs.</summary>
    public string LogDirectory { get; set; } = "";

    /// <summary>Daily log files older than this are deleted on rotation.</summary>
    public int LogRetentionDays { get; set; } = 30;

    /// <summary>Minimum level written to the file: Trace, Debug, Information, Warning, Error.</summary>
    public string FileLogLevel { get; set; } = "Debug";

    /// <summary>Safety valve: a day exceeding this size continues in file.1.log, file.2.log…</summary>
    public int MaxLogFileSizeMb { get; set; } = 25;
}
