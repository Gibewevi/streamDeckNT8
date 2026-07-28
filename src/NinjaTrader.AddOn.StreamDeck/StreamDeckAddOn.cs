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
                SdLogger.Info("=== StreamDeck Add-On V1.0 initializing ===");

                _config = new AddOnConfig();
                _resolver = new ContextResolver();
                _tradingEngine = new TradingEngine(_resolver);
                _dispatcher = new CommandDispatcher(_tradingEngine);

                _bridgeClient = new BridgeClient(_config);
                _bridgeClient.OnMessageReceived += OnBridgeMessage;
                _bridgeClient.OnConnectionChanged += OnConnectionChanged;

                _statePublisher = new StatePublisher(_resolver, _bridgeClient, _config);

                _bridgeClient.Start();

                // Start state publishing with default tracking
                _statePublisher.Start(GetInitialAccountName(), "ES 06-25");

                SdLogger.Info("StreamDeck Add-On initialized successfully");
            }
            catch (Exception ex)
            {
                SdLogger.Error(ex, "Failed to initialize StreamDeck Add-On");
            }
        }

        private void Shutdown()
        {
            try
            {
                SdLogger.Info("StreamDeck Add-On shutting down...");
                if (_statePublisher != null) _statePublisher.Dispose();
                if (_bridgeClient != null) _bridgeClient.Dispose();
                SdLogger.Info("StreamDeck Add-On shut down");
            }
            catch (Exception ex)
            {
                SdLogger.Error(ex, "Error during shutdown");
            }
        }

        private void OnBridgeMessage(BridgeMessage message)
        {
            if (message.Type != "command")
            {
                SdLogger.Debug("Ignoring non-command message: {0}/{1}", message.Type, message.Action);
                return;
            }

            if (message.Action == "setInstrument" || message.Action == "setAccount")
            {
                var trackingResponse = HandleTrackingCommand(message);
                _bridgeClient.SendAsync(trackingResponse).ConfigureAwait(false);
                return;
            }

            // Dispatch to trading engine
            var response = _dispatcher.Dispatch(message);

            // Send response back to bridge
            _bridgeClient.SendAsync(response).ConfigureAwait(false);
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
                SdLogger.Info("Connected to bridge");
            else
                SdLogger.Warn("Disconnected from bridge — will retry");
        }
    }
}
