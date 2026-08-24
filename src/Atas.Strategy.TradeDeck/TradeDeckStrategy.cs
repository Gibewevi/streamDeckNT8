using System;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;

using ATAS.Strategies.Chart;

namespace TradeDeck.Atas.Strategy;

/// <summary>
/// The deck's foothold inside ATAS. Replaces the NinjaTrader add-on: same protocol, same bridge,
/// same log file — a different platform underneath.
///
/// THE CHART IS ONLY A HOST. ATAS requires a strategy to be attached to a chart and started, but
/// nothing here reads that chart: the deck's selection is resolved against Connector.Portfolios and
/// Connector.Securities and set EXPLICITLY on every order. The chart may display anything at all.
///
/// A CLOSED CHART BREAKS NOTHING NEW. Stopped or removed, the strategy simply stops being a client
/// of the bridge, and the bridge reports "add-on disconnected" exactly as it does today when
/// NinjaTrader is closed. The deck already shows that state.
/// </summary>
[DisplayName("TradeDeck")]
[Display(Name = "TradeDeck",
    Description = "Surface de controle Stream Deck : execute les commandes du deck via le bridge local.")]
public class TradeDeckStrategy : ChartStrategy
{
    private const string BridgeUrl = "ws://127.0.0.1:8219";
    private const int ReconnectDelayMs = 3000;

    private BridgeClient? _bridge;

    public TradeDeckStrategy() : base(true)
    {
    }

    /// <summary>
    /// Nothing to compute. This strategy is driven by the deck and by the connector's events, not
    /// by bars — the override exists because the base class requires it.
    /// </summary>
    protected override void OnCalculate(int bar, decimal value)
    {
    }

    protected override void OnStarted()
    {
        try
        {
            SdLog.SessionHeader(BridgeUrl);

            _bridge = new BridgeClient(BridgeUrl, ReconnectDelayMs);
            _bridge.MessageReceived += OnBridgeMessage;
            _bridge.ConnectionChanged += OnConnectionChanged;
            _bridge.Start();

            SdLog.Event("Session", "Strategy started — bridge={0}", BridgeUrl);
        }
        catch (Exception ex)
        {
            SdLog.Fail("Session", ex, "Failed to start the TradeDeck strategy");
            // FAIL LOUDLY. A cockpit that is silently absent is worse than one that is plainly
            // broken: the trader would keep pressing keys and keep believing orders were leaving.
            StopWithNotification("TradeDeck n'a pas pu demarrer : " + ex.Message);
        }
    }

    /// <summary>
    /// The chart carrying the strategy was closed. ATAS suspends rather than stops.
    ///
    /// The socket MUST go down here. Left open, the bridge would keep reporting "add-on connected"
    /// while nothing behind it can act: the deck would show a live cockpit whose keys quietly do
    /// nothing, which is the one failure mode worse than showing it disconnected.
    /// </summary>
    protected override void OnSuspended()
    {
        SdLog.EventWarn("Session", "Strategy suspended (chart closed) — dropping the bridge connection");
        try { _bridge?.Stop(); }
        catch (Exception ex) { SdLog.Fail("Session", ex, "Error while stopping the bridge client on suspend"); }
    }

    /// <summary>
    /// WE DELIBERATELY DO NOT CLOSE POSITIONS HERE.
    ///
    /// ATAS's own guidance is the opposite — "it is extremely desirable to redefine the OnStopping
    /// method and realize the logic of closing internal positions and cancelling the posted orders"
    /// — and it is right for an automated strategy, whose positions are its own and meaningless once
    /// it stops. This is not one. The deck is a CONTROL SURFACE: the positions are the trader's, and
    /// liquidating them because a chart was closed, or because the strategy was restarted after a
    /// recompile, would be the single most destructive thing this code could do.
    /// </summary>
    protected override void OnStopping()
    {
        try { _bridge?.Stop(); }
        catch (Exception ex) { SdLog.Fail("Session", ex, "Error while stopping the bridge client"); }
    }

    protected override void OnStopped()
    {
        try
        {
            if (_bridge != null)
            {
                _bridge.MessageReceived -= OnBridgeMessage;
                _bridge.ConnectionChanged -= OnConnectionChanged;
                _bridge.Dispose();
                _bridge = null;
            }

            SdLog.Event("Session", "=== TradeDeck ATAS strategy session ended ===");
        }
        catch (Exception ex)
        {
            SdLog.Fail("Session", ex, "Error during shutdown");
        }
    }

    private void OnConnectionChanged(bool connected)
    {
        if (connected) SdLog.Event("Connection", "Connected to bridge at {0}", BridgeUrl);
        else SdLog.EventWarn("Connection", "Disconnected from bridge — will retry every {0}ms", ReconnectDelayMs);
    }

    private void OnBridgeMessage(BridgeMessage message)
    {
        if (message.Type != "command")
        {
            SdLog.Debug("Ignoring non-command message: {0}/{1}", message.Type, message.Action);
            return;
        }

        SdLog.Event("Command", "[REQ:{0}] Received {1} payload={2}",
            message.RequestId ?? "n/a", message.Action,
            message.Payload?.ToString(Newtonsoft.Json.Formatting.None) ?? "{}");

        var response = BridgeMessage.Failure(message.RequestId, message.Action,
            "UNSUPPORTED_ACTION", $"Action '{message.Action}' is not supported yet by the ATAS strategy.");

        // Fire-and-forget: this runs on the connector's thread and must not block. SendAsync
        // catches its own errors, so no faulted task is left unobserved.
        _ = _bridge?.SendAsync(response);
    }
}
