using System;
using System.Collections.Generic;
using System.Linq;

using ATAS.DataFeedsCore;

namespace TradeDeck.Atas.Strategy;

/// <summary>
/// Watches every order on every connector the deck can reach, and tells the deck what it cannot see
/// for itself.
///
/// THE REJECTION PATH IS THE REASON THIS CLASS EXISTS. Submission is asynchronous: OpenOrder returns
/// long before the venue answers, so without this the key would flash "OK" for an order that never
/// reached the market. This is exactly the pain Account.Submit caused on the NinjaTrader side.
///
/// SUBSCRIBED TO EVERY CONNECTOR THE REGISTRY REPORTS, NOT ONE. There is no single connector to
/// subscribe to (task 3): portfolios are spread across whichever connectors are actually connected,
/// and an Order/Position/MyTrade belongs to whichever one carries its portfolio. Missing a connector
/// here means missing every external order placed on the account it carries — exactly the hole
/// GuardEnforcer (task 8) exists to close, so <see cref="Refresh"/> is called every publish tick
/// (500 ms) rather than once at start-up: a connector can connect mid-session, as the Rithmic one did
/// in the field trial, carrying a portfolio invisible from the chart.
///
/// DEGRADED MODE — AND MORE GENERALLY, ZERO CONNECTORS CURRENTLY WATCHED — MEANS NO REJECTION EVER
/// REACHES THE DECK. <see cref="Refresh"/> can receive an empty connector list for two different
/// reasons: the host registry is unreachable (ContextResolver.IsDegraded), or it is perfectly
/// reachable but nothing is connected to it yet (ATAS just started, no broker attached). Either way
/// this class ends up subscribed to nothing, and OrdersRegisterFailed / OrderChanged / NewOrders
/// simply never fire — a trader could press Buy, get what looks like a silent nothing, and have no
/// way to know a rejection is not coming because nobody is listening for it. <see cref="Refresh"/>
/// therefore logs this loudly, once per episode (not once per 500ms tick — see the rate limiting
/// below), naming exactly what is lost. This is NOT a replacement event source: task 8/9's own
/// design question (polling TradingManager.Orders from the publish loop while degraded) is left
/// alone here on purpose. What this class offers instead is a shape an alternative source can slot
/// into without restructuring: Refresh already reconciles against whatever connector list it is
/// handed, and StateChanged is a plain callback a polling path could invoke exactly like a connector
/// event does.
/// </summary>
internal sealed class OrderMonitor : IDisposable
{
    private readonly BridgeClient _bridge;
    private readonly object _lock = new();

    /// <summary>Connectors currently subscribed, keyed by reference — a connector never compares by value.</summary>
    private readonly HashSet<IDataFeedConnector> _attached = new();
    private bool _disposed;

    /// <summary>
    /// Set once a "no connector watched" warning has fired, and cleared the moment a connector
    /// attaches again — so the warning repeats once per episode of blindness, not once per Refresh
    /// tick (500ms; unrated-limited here would be hundreds of thousands of lines a day) and not
    /// only once ever (a connector dropping again after a recovery is a NEW episode worth saying
    /// out loud again).
    /// </summary>
    private bool _warnedNoConnectors;

    /// <summary>
    /// Refreshes the deck on the spot instead of waiting for the next publish tick. Assigned rather
    /// than injected so this class keeps no reference back to the publisher that owns it.
    /// </summary>
    public Action? StateChanged { get; set; }

    public OrderMonitor(BridgeClient bridge) => _bridge = bridge;

    /// <summary>
    /// Reconciles the connectors this monitor listens to against what the registry currently reports.
    /// Cheap to call every tick: a session with no connector churn does nothing beyond a set
    /// comparison, and one that gains or loses a connector attaches or detaches exactly that one.
    /// </summary>
    public void Refresh(IReadOnlyList<IDataFeedConnector> connectors)
    {
        lock (_lock)
        {
            if (_disposed) return;

            var wanted = new HashSet<IDataFeedConnector>(connectors);

            foreach (var gone in _attached.Where(c => !wanted.Contains(c)).ToList())
                DetachOne(gone);

            foreach (var connector in connectors)
                if (!_attached.Contains(connector)) AttachOne(connector);

            // See the class-level remark above: this is the loud-once warning task 4's review
            // required. Checked after reconciling, so it reflects what is ACTUALLY watched right
            // now rather than what the registry merely reported this tick.
            if (_attached.Count == 0)
            {
                if (!_warnedNoConnectors)
                {
                    _warnedNoConnectors = true;
                    SdLog.EventWarn("Order",
                        "No connector is currently watched — order rejections will NOT reach the deck " +
                        "(a submit can look like it worked and never be corrected) until a connector becomes available again");
                }
            }
            else
            {
                _warnedNoConnectors = false;
            }
        }
    }

    /// <summary>Detaches every connector — used on shutdown and in degraded mode, where the registry reports none.</summary>
    public void DetachAll()
    {
        lock (_lock)
        {
            foreach (var connector in _attached.ToList()) DetachOne(connector);
        }
    }

    private void AttachOne(IDataFeedConnector connector)
    {
        connector.OrderChanged += OnOrderChanged;
        connector.OrdersRegisterFailed += OnRegisterFailed;
        connector.OrdersCancelFailed += OnCancelFailed;
        connector.OrderModifyFailed += OnModifyFailed;
        connector.PositionsChanged += OnPositionsChanged;

        _attached.Add(connector);
        SdLog.Event("Order", "Order monitor attached to connector {0} ({1} connector(s) now watched)",
            connector.GetType().Name, _attached.Count);
    }

    private void DetachOne(IDataFeedConnector connector)
    {
        try
        {
            connector.OrderChanged -= OnOrderChanged;
            connector.OrdersRegisterFailed -= OnRegisterFailed;
            connector.OrdersCancelFailed -= OnCancelFailed;
            connector.OrderModifyFailed -= OnModifyFailed;
            connector.PositionsChanged -= OnPositionsChanged;
        }
        catch (Exception ex) { SdLog.Warn("Could not unsubscribe order updates from a connector: {0}", ex.Message); }

        _attached.Remove(connector);
        SdLog.Event("Order", "Order monitor detached from connector {0} ({1} connector(s) now watched)",
            connector.GetType().Name, _attached.Count);
    }

    private void OnPositionsChanged(IDataFeedConnector connector, IEnumerable<Position> positions)
    {
        // The position actually moved — this is the moment the deck must be refreshed, rather than
        // up to 500 ms later.
        Safely("Position", () =>
        {
            SdLog.TraceEvent("Position", "PositionsChanged received — publishing state immediately");
            StateChanged?.Invoke();
        });
    }

    private void OnOrderChanged(IDataFeedConnector connector, Order order)
    {
        Safely("Order", () =>
        {
            if (order == null) return;

            var status = order.Status();

            // Every transition is recorded, not only the failures: reconstructing why a position
            // ended up where it did needs the whole life of the order. Both the raw state and the
            // Status() verdict go in — the second is what we act on, the first is what the platform
            // will quote back at you in a support thread.
            SdLog.Event("Order", "Order {0} {1}/{2} — {3} {4} qty={5} unfilled={6} instrument={7}",
                order.Id ?? "?", status, order.State, order.Direction, order.Type,
                order.QuantityToFill, order.Unfilled,
                ContextResolver.InstrumentName(order.Security));

            if (order.State == OrderStates.Failed)
            {
                // A failed order with no message of its own still has to reach the deck: the key
                // must go from "sent" to "rejected", and silence would leave it lying. Read from
                // State rather than Status(), which has no value for "the venue refused it".
                ReportRejection(order, "OrderFailed", order.State.ToString());
                StateChanged?.Invoke();
                return;
            }

            // These change the stop/target picture the deck draws even when the net position does
            // not move — a target cancelled, a stop replaced after a break-even.
            if (status is OrderStatus.Filled or OrderStatus.PartlyFilled or OrderStatus.Canceled)
                StateChanged?.Invoke();
        });
    }

    private void OnRegisterFailed(IDataFeedConnector connector, string message, Order order) =>
        Safely("Order", () => { ReportRejection(order, "OrderRejected", message); StateChanged?.Invoke(); });

    private void OnCancelFailed(IDataFeedConnector connector, string message, Order order) =>
        Safely("Order", () => ReportRejection(order, "CancelRejected", message));

    private void OnModifyFailed(IDataFeedConnector connector, Order order, Order newOrder, string error) =>
        Safely("Order", () => ReportRejection(order, "ModifyRejected", error));

    /// <summary>
    /// Sends the orderUpdate event the host turns into a red REJECTED on the key.
    ///
    /// The field names are the protocol's and are read by the host as they are: orderState,
    /// rejected, error, reason. `rejected: true` is what makes the key flash — the key's background
    /// stays normal, because a greyed background means "blocked", never "rejected".
    /// </summary>
    private void ReportRejection(Order? order, string error, string? reason)
    {
        var payload = new Dictionary<string, object?>
        {
            ["orderId"] = order?.Id ?? string.Empty,
            ["orderState"] = "Rejected",
            ["rejected"] = true,
            ["error"] = error,
            ["reason"] = string.IsNullOrWhiteSpace(reason) ? error : reason,
            ["quantity"] = order != null ? (int)order.QuantityToFill : 0,
            ["orderType"] = order?.Type.ToString() ?? "?",
            ["orderAction"] = order?.Direction.ToString() ?? "?",
            ["name"] = order?.Comment ?? string.Empty,
            ["instrument"] = ContextResolver.InstrumentName(order?.Security),
        };

        SdLog.EventWarn("Order", "ORDER REJECTED {0} — {1} {2} qty={3} instrument={4}: {5} (error={6})",
            order?.Id ?? "?", order?.Direction, order?.Type, order?.QuantityToFill,
            ContextResolver.InstrumentName(order?.Security), reason, error);

        // Fire-and-forget: this runs on the connector's event thread and cannot wait. SendAsync
        // catches its own errors, so no faulted task is left unobserved.
        _ = _bridge.SendAsync(BridgeMessage.Notify("orderUpdate", payload));
    }

    /// <summary>
    /// Runs a handler with no way for it to throw back into the connector's event pipeline. An
    /// exception escaping here would land in the platform's own dispatch loop, and a lost log line
    /// must never cost a trading session.
    /// </summary>
    private static void Safely(string category, Action action)
    {
        try { action(); }
        catch (Exception ex) { SdLog.Fail(category, ex, "Error handling a connector event"); }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            DetachAll();
            StateChanged = null;
        }
    }
}
