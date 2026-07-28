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
}
