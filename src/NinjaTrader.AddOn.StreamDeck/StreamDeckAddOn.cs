using System;
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
                _statePublisher.Start(_config.BridgeUrl.Contains("Sim") ? "Sim101" : "Sim101", "ES 06-25");

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

            // Handle instrument change tracking
            if (message.Action == "setInstrument")
            {
                var newInstrument = message.GetPayloadString("instrument");
                if (!string.IsNullOrEmpty(newInstrument))
                {
                    var account = message.GetPayloadString("account") ?? "Sim101";
                    _statePublisher.UpdateTracking(account, newInstrument);
                }
            }

            // Dispatch to trading engine
            var response = _dispatcher.Dispatch(message);

            // Send response back to bridge
            _bridgeClient.SendAsync(response).ConfigureAwait(false);
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
