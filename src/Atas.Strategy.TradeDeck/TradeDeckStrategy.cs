using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Linq;

using ATAS.DataFeedsCore;
using ATAS.Indicators;
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
public class TradeDeckStrategy : ChartStrategy, IOrderGateway
{
    private const string BridgeUrl = "ws://127.0.0.1:8219";
    private const int ReconnectDelayMs = 3000;

    private BridgeClient? _bridge;

    private ConnectorRegistry? _registry;
    private ContextResolver? _resolver;
    private StatePublisher? _publisher;

    private OrderEngine? _engine;
    private CommandDispatcher? _dispatcher;
    private OrderMonitor? _monitor;

    /// <summary>
    /// Keeps the order monitor's connector subscriptions in step with the registry, at the same
    /// cadence as the state publisher. A connector can connect or disconnect mid-session — see
    /// OrderMonitor.Refresh — and the only way to notice is to ask again periodically; there is no
    /// "connector list changed" event to subscribe to instead.
    /// </summary>
    private System.Threading.Timer? _connectorRefreshTimer;

    private const int StateIntervalMs = 500;

    /// <summary>
    /// True once the bridge has named the account to track.
    ///
    /// Until then we are on whatever account the connector listed first, chosen by nobody. Every
    /// safety rule scoped to an account rides on that choice — which is how, on 2026-08-21, a locked
    /// macro coexisted with an hour of hand-placed orders on the NinjaTrader side. The bridge pushes
    /// the account on every connection; this flag is the tripwire that says so out loud when it
    /// does not.
    /// </summary>
    private volatile bool _accountConfirmedByBridge;

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

            // ChartStrategy.Connector is confirmed always null on classic ATAS — never used here.
            // The registry is the primary path; ContextResolver degrades to the protected
            // Indicator.TradingManager getter (reachable only from this class, passed down as a
            // delegate) when the registry cannot be reached.
            _registry = new ConnectorRegistry();
            _resolver = new ContextResolver(_registry, () => TradingManager);

            _engine = new OrderEngine(_resolver, this);
            _dispatcher = new CommandDispatcher(_engine);
            _monitor = new OrderMonitor(_bridge);

            _publisher = new StatePublisher(_resolver, _bridge, StateIntervalMs);
            // Empty instrument on purpose: a hardcoded contract goes stale and would make the deck
            // show — and trade — something the trader never picked. The bridge replays its
            // selection on every connection.
            _publisher.Start(_resolver.GetAccountNames().FirstOrDefault() ?? string.Empty, string.Empty);

            // Fills and cancellations refresh the deck on the spot instead of waiting for the next
            // publish tick. Set here rather than injected so the monitor keeps no reference back to
            // the publisher that owns it.
            _monitor.StateChanged = _publisher.PublishNow;

            // NEVER ChartStrategy.Connector (always null, confirmed by measurement — task 3). The
            // monitor's subscriptions are reconciled against the registry's CURRENT connector list on
            // the same cadence as the state publisher: a connector can appear mid-session (the
            // Rithmic connector in the 24/08 field trial connected after the chart had already
            // started), and there is no event to learn that from — only asking again.
            _connectorRefreshTimer = new System.Threading.Timer(_ =>
            {
                try { _monitor?.Refresh(_resolver?.Connectors ?? Array.Empty<IDataFeedConnector>()); }
                catch (Exception ex) { SdLog.Fail("Session", ex, "Order monitor connector refresh failed"); }
            }, null, 0, StateIntervalMs);

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

        // Same ordering and same reason as OnStopping: dispose the publisher — which sends through
        // the bridge client — before the client itself, so no in-flight publish writes to a
        // disposed socket. ATAS's suspend/resume contract does not document whether OnStarted can
        // follow a suspend without an intervening OnStopping, so this path stops the publisher too
        // rather than assume OnStopping is always the one that will; Dispose is idempotent, so
        // whichever path runs first does the real work and the other is a no-op — the alternative
        // is a live 500ms timer leaked per chart reopen that no trader could ever diagnose.
        //
        // The connector refresh timer and the order monitor follow the same defensive logic, for
        // the same reason: left running past a suspend, the timer would keep firing every 500ms
        // forever (one more leaked timer per chart reopen), calling StateChanged — which points at
        // the just-disposed publisher's PublishNow — on every connector event the monitor is still
        // subscribed to.
        try { _connectorRefreshTimer?.Dispose(); _connectorRefreshTimer = null; }
        catch (Exception ex) { SdLog.Fail("Session", ex, "Error while stopping the connector refresh timer on suspend"); }

        try { _monitor?.Dispose(); }
        catch (Exception ex) { SdLog.Fail("Session", ex, "Error while detaching the order monitor on suspend"); }

        try { _publisher?.Dispose(); }
        catch (Exception ex) { SdLog.Fail("Session", ex, "Error while stopping the state publisher on suspend"); }

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
        try { _connectorRefreshTimer?.Dispose(); _connectorRefreshTimer = null; }
        catch (Exception ex) { SdLog.Fail("Session", ex, "Error while stopping the connector refresh timer"); }
        try { _monitor?.Dispose(); }
        catch (Exception ex) { SdLog.Fail("Session", ex, "Error while detaching the order monitor"); }

        // Before the bridge client, which it publishes through: stopping the socket first would
        // leave a publish in flight writing to a disposed client.
        try { _publisher?.Dispose(); }
        catch (Exception ex) { SdLog.Fail("Session", ex, "Error while stopping the state publisher"); }

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

            // Defensive, not redundant: OnStopping already disposes the publisher on the normal
            // path, but nothing here can prove ATAS always calls OnStopping before OnStopped on
            // every path (e.g. after OnSuspended). Dispose is idempotent, so calling it again here
            // costs nothing on the normal path and closes the gap on any path that skips OnStopping —
            // nulling the field without disposing would otherwise leak the 500ms timer.
            try { _connectorRefreshTimer?.Dispose(); }
            catch (Exception ex) { SdLog.Fail("Session", ex, "Error while stopping the connector refresh timer during shutdown"); }
            try { _monitor?.Dispose(); }
            catch (Exception ex) { SdLog.Fail("Session", ex, "Error while detaching the order monitor during shutdown"); }
            try { _publisher?.Dispose(); }
            catch (Exception ex) { SdLog.Fail("Session", ex, "Error while stopping the state publisher during shutdown"); }

            _connectorRefreshTimer = null;
            _monitor = null;
            _dispatcher = null;
            _engine = null;
            _publisher = null;
            _resolver = null;
            _registry = null;

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

        var startedAt = DateTime.UtcNow;

        var response = message.Action switch
        {
            _ when CommandDispatcher.Handles(message.Action) && _dispatcher != null => _dispatcher.Dispatch(message),
            "setAccount" or "setInstrument" => HandleTracking(message),
            // Out of scope, and answered honestly rather than with a comfortable lie. The bridge
            // FORCES both on every add-on connection; replying "success" would show the trader a
            // copier that mirrors nothing and a trend rule that judges nothing.
            "setCopierConfig" or "copierPanic" or "configureTrend" => Unsupported(message),
            _ => BridgeMessage.Failure(message.RequestId, message.Action,
                     "UNSUPPORTED_ACTION", $"Action '{message.Action}' is not supported by the ATAS strategy."),
        };

        LogOutcome(message, response, startedAt);
        _ = _bridge?.SendAsync(response);
    }

    private BridgeMessage HandleTracking(BridgeMessage message)
    {
        if (_publisher == null)
            return BridgeMessage.Failure(message.RequestId, message.Action, "CONTEXT_MISSING", "State publisher is not initialized.");

        var account = message.GetString("account");
        var instrument = message.GetString("instrument");

        if (message.Action == "setAccount" && string.IsNullOrWhiteSpace(account))
            return BridgeMessage.Failure(message.RequestId, message.Action, "CONTEXT_MISSING", "Account name is required.");

        if (message.Action == "setInstrument" && string.IsNullOrWhiteSpace(instrument))
            return BridgeMessage.Failure(message.RequestId, message.Action, "CONTEXT_MISSING", "Instrument name is required.");

        // The account now comes from the bridge, not from this side's startup guess.
        if (!string.IsNullOrWhiteSpace(account)) _accountConfirmedByBridge = true;

        _publisher.UpdateTracking(account, instrument);

        return BridgeMessage.Response(message.RequestId, message.Action, true, new
        {
            account = _publisher.TrackedAccount,
            instrument = _publisher.TrackedInstrument,
        });
    }

    private static readonly HashSet<string> UnsupportedReported = new(StringComparer.Ordinal);

    /// <summary>
    /// Refuses an out-of-scope action, and says so ONCE per action.
    ///
    /// The bridge forces setCopierConfig on every connection and the host replays configureTrend on
    /// every layout edit: warning on each one would train the reader to skip the category on the day
    /// it carries something else.
    /// </summary>
    private static BridgeMessage Unsupported(BridgeMessage message)
    {
        lock (UnsupportedReported)
        {
            if (UnsupportedReported.Add(message.Action))
            {
                SdLog.EventWarn("Command",
                    "'{0}' is out of scope for the ATAS port (trend macro and account copier ship later) — refused, and this is the only warning",
                    message.Action);
            }
        }

        return BridgeMessage.Failure(message.RequestId, message.Action, "UNSUPPORTED_ACTION",
            $"'{message.Action}' is not ported to ATAS yet.");
    }

    /// <summary>
    /// Records what the strategy answered and how long it took. The duration matters: a command
    /// that normally answers in a few ms and suddenly takes seconds points at the platform — data
    /// feed, account lock — rather than at the deck.
    /// </summary>
    private static void LogOutcome(BridgeMessage request, BridgeMessage response, DateTime startedAt)
    {
        var elapsedMs = (int)(DateTime.UtcNow - startedAt).TotalMilliseconds;

        if (response.Error != null)
        {
            SdLog.EventWarn("Command", "[REQ:{0}] {1} refused: {2} — {3} ({4}ms)",
                request.RequestId ?? "n/a", request.Action, response.Error.Code, response.Error.Message, elapsedMs);
            return;
        }

        SdLog.Event("Command", "[REQ:{0}] {1} completed ({2}ms)", request.RequestId ?? "n/a", request.Action, elapsedMs);
    }

    // --- IOrderGateway ---------------------------------------------------------------
    //
    // The three verbs come from ChartStrategy itself, which is the documented strategy API and takes
    // the order and nothing else. The two classifications only ITradingManager can make, and it is a
    // direct property of Indicator — ATAS's own indicators write TradingManager.Portfolio just like
    // this, with no detour through DataProvider.

    void IOrderGateway.Open(Order order) => OpenOrder(order);

    void IOrderGateway.Cancel(Order order) => CancelOrder(order);

    void IOrderGateway.Modify(Order oldOrder, Order newOrder) => ModifyOrder(oldOrder, newOrder);

    /// <summary>
    /// Classifies an order the deck did not place. Never throws: the caller is the journal, and a
    /// classification it could not make must cost a field, never the line.
    /// </summary>
    bool IOrderGateway.IsStopLossOrder(Order order)
    {
        try { return TradingManager?.IsStopLossOrder(order) == true; }
        catch { return false; }
    }

    bool IOrderGateway.IsTakeProfitOrder(Order order)
    {
        try { return TradingManager?.IsTakeProfitOrder(order) == true; }
        catch { return false; }
    }
}
