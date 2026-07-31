using System;
using System.Linq;
using NinjaTrader.NinjaScript.AddOns.StreamDeck.Models;
using NinjaTrader.NinjaScript.AddOns.StreamDeck.Services;
using NinjaTrader.NinjaScript.AddOns.StreamDeck.Utilities;

namespace NinjaTrader.NinjaScript.AddOns
{
    /// <summary>
    /// NinjaTrader 8 Add-On entry point for Stream Deck integration.
    /// 
    /// Installation:
    ///   1. Build this project
    ///   2. Copy the DLL to: Documents\NinjaTrader 8\bin\Custom\AddOns\
    ///   3. Restart NinjaTrader 8
    ///   4. The add-on auto-starts and connects to the bridge
    /// </summary>
    public class StreamDeckAddOn : AddOnBase
    {
        private BridgeClient _bridgeClient;
        private TradingEngine _tradingEngine;
        private CommandDispatcher _dispatcher;
        private ContextResolver _resolver;
        private StatePublisher _statePublisher;
        private OrderMonitor _orderMonitor;
        private AddOnConfig _config;

        protected override void OnStateChange()
        {
            switch (State)
            {
                case NinjaTrader.NinjaScript.State.SetDefaults:
                    Description = "Stream Deck Trading Integration V1";
                    Name = "StreamDeckAddOn";
                    break;

                case NinjaTrader.NinjaScript.State.Configure:
                    break;

                case NinjaTrader.NinjaScript.State.Active:
                    Initialize();
                    break;

                case NinjaTrader.NinjaScript.State.Terminated:
                    Shutdown();
                    break;
            }
        }

        private void Initialize()
        {
            try
            {
                _config = new AddOnConfig();
                SdLogger.LogSessionHeader(_config.BridgeUrl);
                SdLogger.Event("Session", "Configuration: stateInterval={0}ms reconnectDelay={1}ms defaultInstrument='{2}'",
                    _config.StateUpdateIntervalMs, _config.ReconnectDelayMs, _config.DefaultInstrument);

                _resolver = new ContextResolver();
                _tradingEngine = new TradingEngine(_resolver);
                _dispatcher = new CommandDispatcher(_tradingEngine);

                _bridgeClient = new BridgeClient(_config);
                _bridgeClient.OnMessageReceived += OnBridgeMessage;
                _bridgeClient.OnConnectionChanged += OnConnectionChanged;

                _orderMonitor = new OrderMonitor(_bridgeClient);
                _statePublisher = new StatePublisher(_resolver, _bridgeClient, _config, _orderMonitor);

                _bridgeClient.Start();

                // Start state publishing with default tracking
                var initialAccount = GetInitialAccountName();
                _statePublisher.Start(initialAccount, _config.DefaultInstrument);

                SdLogger.Event("Session", "Add-On initialized — initialAccount='{0}'",
                    string.IsNullOrEmpty(initialAccount) ? "(none)" : initialAccount);
            }
            catch (Exception ex)
            {
                SdLogger.Fail("Session", ex, "Failed to initialize StreamDeck Add-On");
            }
        }

        private void Shutdown()
        {
            try
            {
                SdLogger.Event("Session", "Add-On shutting down (NinjaTrader closing or NinjaScript reload)");
                if (_statePublisher != null) _statePublisher.Dispose();
                if (_orderMonitor != null) _orderMonitor.Dispose();
                if (_bridgeClient != null) _bridgeClient.Dispose();
                SdLogger.Event("Session", "=== StreamDeck Add-On session ended ===");
            }
            catch (Exception ex)
            {
                SdLogger.Fail("Session", ex, "Error during shutdown");
            }
        }

        private void OnBridgeMessage(BridgeMessage message)
        {
            if (message.Type != "command")
            {
                SdLogger.Debug("Ignoring non-command message: {0}/{1}", message.Type, message.Action);
                return;
            }

            // Full payload on the way in: when an order goes wrong, the first question is always
            // "what exactly was asked for?" — quantity, offset and resolved context included.
            SdLogger.Event("Command", "[REQ:{0}] Received {1} payload={2}",
                message.RequestId ?? "n/a", message.Action, DescribePayload(message));

            var startedAt = DateTime.UtcNow;
            BridgeMessage response;

            if (message.Action == "setInstrument" || message.Action == "setAccount")
                response = HandleTrackingCommand(message);
            else
                response = _dispatcher.Dispatch(message);

            LogCommandOutcome(message, response, startedAt);

            // Send response back to bridge
            _bridgeClient.SendAsync(response).ConfigureAwait(false);
        }

        private static string DescribePayload(BridgeMessage message)
        {
            if (message.Payload == null || message.Payload.Count == 0) return "{}";
            try { return SimpleJson.Serialize(message.Payload); }
            catch (Exception ex) { return "(unserializable: " + ex.Message + ")"; }
        }

        /// <summary>
        /// Records what the add-on answered and how long NinjaTrader took. The duration matters:
        /// a command that normally answers in a few ms and suddenly takes seconds points at NT8
        /// itself (data feed, account lock) rather than at the deck.
        /// </summary>
        private static void LogCommandOutcome(BridgeMessage request, BridgeMessage response, DateTime startedAt)
        {
            var elapsedMs = (int)(DateTime.UtcNow - startedAt).TotalMilliseconds;

            if (response != null && response.Error != null)
            {
                SdLogger.EventWarn("Command", "[REQ:{0}] {1} refused: {2} — {3} ({4}ms)",
                    request.RequestId ?? "n/a", request.Action,
                    response.Error.Code, response.Error.Message, elapsedMs);
                return;
            }

            SdLogger.Event("Command", "[REQ:{0}] {1} completed ({2}ms)",
                request.RequestId ?? "n/a", request.Action, elapsedMs);
        }

        private string GetInitialAccountName()
        {
            var firstAccount = _resolver.GetAccountNames().FirstOrDefault();
            return !string.IsNullOrWhiteSpace(firstAccount) ? firstAccount : string.Empty;
        }

        private BridgeMessage HandleTrackingCommand(BridgeMessage message)
        {
            var account = message.GetPayloadString("account");
            var instrument = message.GetPayloadString("instrument");

            if (message.Action == "setAccount" && string.IsNullOrWhiteSpace(account))
                return BridgeMessage.CreateError(message.RequestId, message.Action, "CONTEXT_MISSING", "Account name is required.");

            if (message.Action == "setInstrument" && string.IsNullOrWhiteSpace(instrument))
                return BridgeMessage.CreateError(message.RequestId, message.Action, "CONTEXT_MISSING", "Instrument name is required.");

            var nextAccount = !string.IsNullOrWhiteSpace(account) ? account : _statePublisher.TrackedAccount;
            var nextInstrument = !string.IsNullOrWhiteSpace(instrument) ? instrument : _statePublisher.TrackedInstrument;

            _statePublisher.UpdateTracking(nextAccount, nextInstrument);

            return BridgeMessage.CreateResponse(message.RequestId, message.Action, true, new
            {
                account = nextAccount,
                instrument = nextInstrument
            });
        }

        private void OnConnectionChanged(bool connected)
        {
            if (connected)
                SdLogger.Event("Connection", "Connected to bridge at {0}", _config.BridgeUrl);
            else
                SdLogger.EventWarn("Connection", "Disconnected from bridge — will retry every {0}ms",
                    _config.ReconnectDelayMs);
        }
    }
}
