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
        private GuardEnforcer _guardEnforcer;
        private ExecutionRecorder _executionRecorder;
        private TrendMonitor _trendMonitor;
        private CopyEngine _copyEngine;
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

                // Closes the hole the bridge cannot reach: orders placed straight into NinjaTrader
                // never cross it, so the safety macro has to be applied from inside the platform.
                _guardEnforcer = new GuardEnforcer(_resolver, _bridgeClient);

                // The add-on's only market-data subscription. Created before the publisher, which
                // hands it the tracked instrument on every tick and reads its verdict — and before
                // the recorder, which stamps that verdict on every fill.
                _trendMonitor = new TrendMonitor();

                // Records every fill to a local spool. Passed to the monitor, which owns the
                // account subscription and is therefore the only place that can attach it.
                _executionRecorder = new ExecutionRecorder(_trendMonitor);
                _orderMonitor = new OrderMonitor(_bridgeClient, _guardEnforcer, _executionRecorder);

                // Mirrors the selected account onto follower accounts. Created before the publisher,
                // which drives its account resolution and its drift check on every tick — the
                // copier deliberately owns no timer of its own.
                _copyEngine = new CopyEngine(_resolver, _bridgeClient, _guardEnforcer, _executionRecorder);

                // Chaque exécution d'un compte lié part au journal annotée de son origine : quel
                // ordre maître elle copie, avec combien de retard et quel écart de prix. Branché
                // ici plutôt qu'injecté pour ne pas faire connaître le moteur de copie à
                // l'enregistreur — même motif que `_orderMonitor.StateChanged`.
                _executionRecorder.CopyContext = _copyEngine.DescribeCopiedExecution;

                _statePublisher = new StatePublisher(_resolver, _bridgeClient, _config, _orderMonitor, _trendMonitor, _copyEngine);

                // Fills and cancellations refresh the deck on the spot instead of waiting for the
                // next publish tick. Set here rather than injected so the monitor keeps no
                // reference back to the publisher that owns it.
                _orderMonitor.StateChanged = _statePublisher.PublishNow;

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
                // After the publisher, which is what drives it: stopping the copier first would
                // leave a publish in flight calling into a disposed engine. Its submit workers are
                // background threads, so an unclean stop would survive a NinjaScript reload.
                if (_copyEngine != null) _copyEngine.Dispose();
                // After the publisher, which is the only reader of its verdict: releasing the bars
                // requests while a publish is in flight would pull the series out from under it.
                if (_trendMonitor != null) _trendMonitor.Dispose();
                // After the monitor, which detaches the subscription that feeds it: closing the
                // spool first would leave fills arriving at a disposed writer.
                if (_orderMonitor != null) _orderMonitor.Dispose();
                if (_executionRecorder != null) _executionRecorder.Dispose();
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

            if (message.Action == "setGuardPolicy")
                response = HandleGuardPolicy(message);
            else if (message.Action == "setCopierConfig")
                response = HandleCopierConfig(message);
            else if (message.Action == "copierPanic")
                response = HandleCopierPanic(message);
            else if (message.Action == "configureTrend")
                response = HandleTrendConfig(message);
            else if (message.Action == "setInstrument" || message.Action == "setAccount")
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

        /// <summary>
        /// Adopts the safety policy the bridge publishes. Handled here rather than in the
        /// dispatcher because it sends no order and touches no position: it only tells the
        /// enforcer what to refuse when an order arrives from outside the deck.
        /// </summary>
        private BridgeMessage HandleGuardPolicy(BridgeMessage message)
        {
            if (_guardEnforcer == null)
                return BridgeMessage.CreateError(message.RequestId, message.Action, "CONTEXT_MISSING", "Guard enforcer is not initialized.");

            var blocked = message.GetPayloadBool("blocked");
            var reason = message.GetPayloadString("reason");
            var maxContracts = (int)message.GetPayloadDouble("maxContracts");

            _guardEnforcer.UpdatePolicy(blocked, reason, maxContracts);

            return BridgeMessage.CreateResponse(message.RequestId, message.Action, true, new
            {
                blocked = blocked,
                maxContracts = maxContracts
            });
        }

        /// <summary>
        /// Adopts the copier configuration the bridge publishes. Handled here rather than in the
        /// dispatcher for the same reason as the guard policy: it sends no order and touches no
        /// position, it only tells the copy engine what to mirror and where.
        ///
        /// The bridge owns and persists this configuration and republishes it on every add-on
        /// (re)connection — which is what makes a NinjaScript recompile mid-session harmless.
        /// </summary>
        private BridgeMessage HandleCopierConfig(BridgeMessage message)
        {
            if (_copyEngine == null)
                return BridgeMessage.CreateError(message.RequestId, message.Action, "CONTEXT_MISSING", "Copy engine is not initialized.");

            _copyEngine.Configure(message);

            return BridgeMessage.CreateResponse(message.RequestId, message.Action, true, new
            {
                enabled = message.GetPayloadBool("enabled"),
                master = message.GetPayloadString("master") ?? string.Empty
            });
        }

        /// <summary>
        /// Stops copying and flattens every follower account. The only place the copier sends
        /// orders of its own accord, and it takes a deliberate command to get here — never a
        /// measurement.
        /// </summary>
        private BridgeMessage HandleCopierPanic(BridgeMessage message)
        {
            if (_copyEngine == null)
                return BridgeMessage.CreateError(message.RequestId, message.Action, "CONTEXT_MISSING", "Copy engine is not initialized.");

            var flattened = _copyEngine.PanicFlatten();

            return BridgeMessage.CreateResponse(message.RequestId, message.Action, true, new
            {
                flattened = flattened
            });
        }

        /// <summary>
        /// Adopts the trend settings the deck publishes. Handled here rather than in the
        /// dispatcher for the same reason as the guard policy: it sends no order and touches no
        /// position, it only tells a monitor what to watch.
        ///
        /// Every field is optional and falls back to what the monitor already holds. The host
        /// replays its whole configuration on each layout edit and each reconnection, and a
        /// missing field must mean "leave it alone", never "reset it to zero".
        /// </summary>
        private BridgeMessage HandleTrendConfig(BridgeMessage message)
        {
            if (_trendMonitor == null)
                return BridgeMessage.CreateError(message.RequestId, message.Action, "CONTEXT_MISSING", "Trend monitor is not initialized.");

            var referenceMinutes = message.GetPayloadInt("referenceMinutes");
            var higherMinutes = message.GetPayloadInt("higherMinutes");
            var thresholdAtr = message.GetPayloadDouble("thresholdAtr");
            var higherEnabled = message.GetPayloadBool("higherEnabled");

            // 0 and null both mean "not supplied": TrendMonitor.Configure keeps its current value
            // for anything out of range, so passing them straight through is safe.
            _trendMonitor.Configure(
                referenceMinutes.HasValue ? referenceMinutes.Value : 0,
                higherEnabled,
                higherMinutes.HasValue ? higherMinutes.Value : 0,
                thresholdAtr.HasValue ? thresholdAtr.Value : 0);

            return BridgeMessage.CreateResponse(message.RequestId, message.Action, true, new
            {
                higherEnabled = higherEnabled
            });
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
