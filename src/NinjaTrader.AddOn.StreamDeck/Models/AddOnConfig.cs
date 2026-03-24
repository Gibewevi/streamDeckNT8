namespace NinjaTrader.NinjaScript.AddOns.StreamDeck.Models
{
    public class AddOnConfig
    {
        public string BridgeUrl { get; set; } = "ws://127.0.0.1:8219";
        public int ReconnectDelayMs { get; set; } = 3000;
        public int StateUpdateIntervalMs { get; set; } = 500;
        public int MaxReconnectAttempts { get; set; } = 0; // 0 = unlimited
    }
}
