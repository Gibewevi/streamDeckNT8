namespace NinjaTrader.NinjaScript.AddOns.StreamDeck.Models
{
    public class AddOnConfig
    {
        public string BridgeUrl { get; set; } = "ws://127.0.0.1:8219";

        /// <summary>
        /// Instrument tracked until Stream Deck selects one. Empty on purpose: a hardcoded
        /// contract goes stale (it used to be "ES 06-25") and would make the deck show — and
        /// trade — an instrument the trader never picked.
        /// </summary>
        public string DefaultInstrument { get; set; } = "";
        public int ReconnectDelayMs { get; set; } = 3000;
        public int StateUpdateIntervalMs { get; set; } = 500;
        public int MaxReconnectAttempts { get; set; } = 0; // 0 = unlimited
    }
}
