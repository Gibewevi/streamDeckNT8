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
/// SUBSCRIBING TO EVERYTHING WIDENED THE SOURCE — THE OUTPUT HAD TO BE NARROWED BACK DOWN.
/// Aggregating across every connector is correct for GuardEnforcer's future purpose (task 8: nothing
/// the trader account does anywhere may go unseen) but wrong for the deck key today: a rejection on
/// an account the deck is not even tracking is not the trader's business at that key, and must not
/// flash REJECTED on it (review round 1, finding I1 — a hand-typed DOM order refused for margin on a
/// Rithmic account the deck was not watching used to do exactly that). <see cref="TrackedAccountName"/>
/// is how the output is narrowed back down; see its own doc for why it is a delegate rather than a
/// dependency, and why "account unknown" fails OPEN, not closed.
///
/// DEGRADED MODE — AND MORE GENERALLY, ZERO CONNECTORS CURRENTLY WATCHED — MEANS NO REJECTION EVER
/// REACHES THE DECK. <see cref="Refresh"/> can receive an empty connector list for two different
/// reasons: the host registry is unreachable (ContextResolver.IsDegraded), or it is perfectly
/// reachable but nothing is connected to it yet (ATAS just started, no broker attached). Either way
/// this class ends up subscribed to nothing, and OrdersRegisterFailed / OrderChanged simply never
/// fire — a trader could press Buy, get what looks like a silent nothing, and have no way to know a
/// rejection is not coming because nobody is listening for it. <see cref="Refresh"/> therefore logs
/// this loudly, once per episode (not once per 500ms tick — see the rate limiting below), naming
/// exactly what is lost. This is NOT a replacement event source: task 8/9's own design question
/// (polling TradingManager.Orders from the publish loop while degraded) is left alone here on
/// purpose. What this class offers instead is a shape an alternative source can slot into without
/// restructuring: Refresh already reconciles against whatever connector list it is handed, and
/// StateChanged is a plain callback a polling path could invoke exactly like a connector event does.
/// </summary>
internal sealed class OrderMonitor : IDisposable
{
    private readonly BridgeClient _bridge;

    /// <summary>
    /// Task 8's guard. Optional: a caller that never passes one (a unit test, or an earlier task's
    /// wiring) gets exactly the pre-task-8 behaviour — every event still reaches the deck, nothing
    /// extra is inspected or cancelled.
    /// </summary>
    private readonly GuardEnforcer? _enforcer;

    /// <summary>
    /// Task 9's execution journal. Optional for the same reason <see cref="_enforcer"/> is: a
    /// caller that never passes one gets the pre-task-9 behaviour, nothing subscribed beyond the
    /// order/position events this class already watched.
    /// </summary>
    private readonly ExecutionRecorder? _recorder;

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
    /// How many CONSECUTIVE Refresh ticks a previously-attached connector has been absent from the
    /// registry's report, keyed by reference. See <see cref="GraceTicks"/>.
    /// </summary>
    private readonly Dictionary<IDataFeedConnector, int> _missingTicks = new();

    /// <summary>
    /// Review round 1, finding M1: ConnectorRegistry re-evaluates whether a connector carries a
    /// portfolio every 500ms, and a connector mid-reconnect can read empty for exactly one tick
    /// before recovering on its own. Detaching and reattaching on that single missed tick used to
    /// produce a full detach plus no-connector-WARN plus attach cycle PER FLAP — up to about one WARN
    /// a second during a prolonged reconnect, roughly 86,000 lines a day, the same axis task 3's
    /// review had already flagged once. A connector must be absent GraceTicks consecutive times (one
    /// tick of tolerance, i.e. it has to miss twice in a row) before it is treated as actually gone —
    /// one missed tick is jitter, not a disconnect.
    /// </summary>
    private const int GraceTicks = 2;

    /// <summary>
    /// Read on every rejection/orderUpdate frame this class is about to forward, and on every order
    /// transition it is about to log at INFO — see the class-level remark on why the output had to be
    /// narrowed back to one account. TradeDeckStrategy wires this from StatePublisher.TrackedAccount
    /// as a LIVE delegate (invoked on every check) rather than a value captured once, so a later
    /// setAccount is honoured without this class needing to be told about it.
    ///
    /// A DELEGATE, NOT A ContextResolver/StatePublisher DEPENDENCY. The plan reserves OrderMonitor's
    /// two optional constructor parameters for tasks 8 and 9; taking either dependency early would
    /// both jump ahead of that reservation and make this class impossible to unit-test without
    /// standing up the whole publish pipeline. Defaults to null, which — see InScope — means "let
    /// everything through", so a caller that never sets it (a unit test, for instance) gets the
    /// pre-I1 behaviour rather than a monitor that discards every event.
    /// </summary>
    public Func<string?>? TrackedAccountName { get; set; }

    /// <summary>
    /// Refreshes the deck on the spot instead of waiting for the next publish tick. Assigned rather
    /// than injected so this class keeps no reference back to the publisher that owns it.
    /// </summary>
    public Action? StateChanged { get; set; }

    public OrderMonitor(BridgeClient bridge, GuardEnforcer? enforcer = null,
                        ExecutionRecorder? recorder = null)
    {
        _bridge = bridge;
        _enforcer = enforcer;
        _recorder = recorder;
    }

    /// <summary>
    /// Reconciles the connectors this monitor listens to against what the registry currently reports.
    /// Cheap to call every tick: a session with no connector churn does nothing beyond a set
    /// comparison, and one that gains or loses a connector attaches or detaches exactly that one —
    /// modulo the grace period on the detach side, see <see cref="GraceTicks"/>.
    /// </summary>
    public void Refresh(IReadOnlyList<IDataFeedConnector> connectors)
    {
        lock (_lock)
        {
            if (_disposed) return;

            var wanted = new HashSet<IDataFeedConnector>(connectors);

            // Detach side: grace-period the absence rather than acting on one missed tick — see
            // GraceTicks' doc. Walking _attached (not `connectors`) is deliberate: this loop only
            // ever concerns connectors we currently think we are subscribed to.
            foreach (var connector in _attached.ToList())
            {
                if (wanted.Contains(connector))
                {
                    _missingTicks.Remove(connector);
                    continue;
                }

                _missingTicks.TryGetValue(connector, out var missed);
                missed++;
                if (missed >= GraceTicks)
                {
                    _missingTicks.Remove(connector);
                    DetachOne(connector);
                }
                else
                {
                    _missingTicks[connector] = missed;
                }
            }

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
            // No connector should carry a grace-period count once nothing is attached at all — a
            // stale entry here would only ever matter if a connector reference were reused, which
            // ATAS never does, but clearing it costs nothing and removes the question.
            _missingTicks.Clear();
        }
    }

    /// <summary>
    /// Subscribes all seven events this monitor needs from one connector — symmetric with
    /// <see cref="DetachOne"/> ON PURPOSE (review round 1, finding I2). DetachOne already wraps its
    /// unsubscribes in a try/catch, which is an admission that these accessors CAN throw (a connector
    /// mid-disposal or mid-reconnect); the subscribe side used to not say so. Without the try/catch
    /// and the unwind below, a throw on (say) the fourth "+=" left handlers one-to-three subscribed
    /// while _attached.Add never ran — the connector then read as "not attached" on every subsequent
    /// Refresh, which resubscribed handlers one-to-three AGAIN every tick, forever: after an hour of
    /// 500ms ticks that is roughly 7200 duplicate registrations, so one broker rejection would fan out
    /// into thousands of orderUpdate frames at the bridge, on top of an ERROR line every 500ms on a
    /// periodic path. A connector must end this method either FULLY attached and recorded in
    /// _attached, or FULLY detached and absent from it — never the half-subscribed state in between.
    /// </summary>
    private void AttachOne(IDataFeedConnector connector)
    {
        var alreadySubscribed = new List<Action>(7);
        try
        {
            connector.OrderChanged += OnOrderChanged;
            alreadySubscribed.Add(() => connector.OrderChanged -= OnOrderChanged);

            connector.OrdersRegisterFailed += OnRegisterFailed;
            alreadySubscribed.Add(() => connector.OrdersRegisterFailed -= OnRegisterFailed);

            connector.OrdersCancelFailed += OnCancelFailed;
            alreadySubscribed.Add(() => connector.OrdersCancelFailed -= OnCancelFailed);

            connector.OrderModifyFailed += OnModifyFailed;
            alreadySubscribed.Add(() => connector.OrderModifyFailed -= OnModifyFailed);

            connector.PositionsChanged += OnPositionsChanged;
            alreadySubscribed.Add(() => connector.PositionsChanged -= OnPositionsChanged);

            // Task 8: the earliest sighting of an order placed outside the deck. Without this,
            // GuardEnforcer only ever learns about an external order through OrderChanged, which on
            // a fast market order can arrive a fill late.
            connector.NewOrders += OnNewOrders;
            alreadySubscribed.Add(() => connector.NewOrders -= OnNewOrders);

            // Task 9: NewMyTrades on EACH connector the registry aggregates, never
            // ChartStrategy.OnNewMyTrade and never a single connector. That difference is the reason
            // the journal exists: at connector level we see every fill on every account the platform
            // is connected to, including the ones placed at the DOM or the Chart Trader — the very
            // ones the guard rules cannot prevent, only report.
            connector.NewMyTrades += OnNewMyTrades;
            alreadySubscribed.Add(() => connector.NewMyTrades -= OnNewMyTrades);
        }
        catch (Exception ex)
        {
            foreach (var unsubscribe in alreadySubscribed)
            {
                // Best effort: the connector is already failing, and a second exception while
                // unwinding must not stop the rest of the unwind or escape to the caller.
                try { unsubscribe(); } catch { /* nothing useful left to do */ }
            }
            SdLog.Warn("Could not attach order monitor to a connector (unwound {0} partial subscription(s)): {1}",
                alreadySubscribed.Count, ex.Message);
            return;
        }

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
            connector.NewOrders -= OnNewOrders;
            connector.NewMyTrades -= OnNewMyTrades;
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

            // Enforcement first, and before any other consideration: this is the only moment an
            // order placed outside the deck can still be stopped. Every millisecond spent elsewhere
            // is a millisecond it may fill in. The verdict is held, not acted on immediately — see
            // below — so the standard transition line for THIS event still gets logged either way
            // (R1 review, minor finding: the two logs used to fall out of step for a cancelled
            // event, and lining them up by order id is how an incident gets reconstructed here).
            var cancelledByGuard = _enforcer != null && _enforcer.Inspect(order);

            var status = order.Status();
            var inScope = InScope(order);

            // Every transition on the TRACKED account is recorded at INFO, not only the failures:
            // reconstructing why a position ended up where it did needs the whole life of the order.
            // An order on a DIFFERENT account still gets a line — just at TRACE (raise TD_LOG_LEVEL to
            // see it) rather than INFO, so it stays findable for diagnosis without adding to the
            // default log's volume for accounts the deck is not even tracking (review round 1, finding
            // I1's log-volume side effect, called out under M1's "not to fix" list).
            if (inScope)
            {
                SdLog.Event("Order", "Order {0} {1}/{2} — {3} {4} qty={5} unfilled={6} instrument={7}",
                    order.Id ?? "?", status, order.State, order.Direction, order.Type,
                    order.QuantityToFill, order.Unfilled,
                    ContextResolver.InstrumentName(order.Security));
            }
            else
            {
                SdLog.TraceEvent("Order", "Order {0} {1}/{2} — {3} {4} qty={5} unfilled={6} instrument={7} (different account, not forwarded)",
                    order.Id ?? "?", status, order.State, order.Direction, order.Type,
                    order.QuantityToFill, order.Unfilled,
                    ContextResolver.InstrumentName(order.Security));
            }

            if (cancelledByGuard)
            {
                StateChanged?.Invoke();
                return;
            }

            if (order.State == OrderStates.Failed)
            {
                // A failed order with no message of its own still has to reach the deck: the key
                // must go from "sent" to "rejected", and silence would leave it lying. Read from
                // State rather than Status(), which has no value for "the venue refused it".
                // ReportRejection ALWAYS logs (any account); it only forwards to the bridge — and
                // this only invokes StateChanged — when the order is on the tracked account.
                ReportRejection(order, "OrderFailed", order.State.ToString());
                if (inScope) StateChanged?.Invoke();
                return;
            }

            // These change the stop/target picture the deck draws even when the net position does
            // not move — a target cancelled, a stop replaced after a break-even. Only worth an
            // immediate refresh when it is the tracked account's own picture that changed.
            if (inScope && status is OrderStatus.Filled or OrderStatus.PartlyFilled or OrderStatus.Canceled)
                StateChanged?.Invoke();
        });
    }

    private void OnNewOrders(IDataFeedConnector connector, IEnumerable<Order> orders) =>
        Safely("Safety", () =>
        {
            if (_enforcer == null) return;
            // The earliest sighting of an order placed outside the deck. OrderChanged follows, but
            // on a market order the two may be a fill apart. Inspect guards itself against throwing
            // (see its own doc), so one bad order in the batch cannot stop the rest — a bracket's
            // entry, stop and target can all arrive in the same NewOrders callback.
            foreach (var order in orders)
            {
                if (_enforcer.Inspect(order)) StateChanged?.Invoke();
            }
        });

    /// <summary>
    /// Task 9's execution journal. Fills reported by the CONNECTOR, never
    /// ChartStrategy.OnNewMyTrade — see the class-level remark on <see cref="AttachOne"/>. The
    /// journal owns its own guard against replay fills and duplicate ids (see
    /// <see cref="ExecutionRecorder.Record"/>), which is why <c>Record</c> itself cannot throw —
    /// but <paramref name="trades"/> and <c>trades</c> below is only borrowed from the connector:
    /// review round 1, finding I2, corrected a claim that a plain foreach was therefore safe. It
    /// is not: MoveNext() on a collection the connector is mutating concurrently can throw
    /// mid-enumeration regardless of what Record does, and an uncaught throw there would abandon
    /// every remaining fill in the batch silently. <see cref="SafeTrades"/> is what makes the loop
    /// keep what it already read instead.
    /// </summary>
    private void OnNewMyTrades(IDataFeedConnector connector, IEnumerable<MyTrade> trades) =>
        Safely("Journal", () =>
        {
            var batch = SafeTrades(trades);

            // Recording is unconditional on every account the platform reports — that breadth is
            // the whole reason this journal exists (see the class doc on ExecutionRecorder).
            // Refreshing the DECK is not: every other handler in this class gates StateChanged on
            // InScope (OnOrderChanged, OnRegisterFailed, OnPositionsChanged is the one deliberate
            // exception, unscoped because a position move on ANY account still means the picture
            // this class draws could be stale). A fill on an account the trader is not tracking
            // must not refresh a key that has nothing to do with it.
            var trackedFillSeen = false;
            foreach (var trade in batch)
            {
                _recorder?.Record(trade);
                if (InScopeAccount(trade?.AccountID ?? trade?.Portfolio?.AccountID)) trackedFillSeen = true;
            }

            // A fill moved the tracked position: refresh the deck rather than waiting for the next
            // tick.
            if (trackedFillSeen) StateChanged?.Invoke();
        });

    /// <summary>
    /// Enumerates a connector's own trade batch defensively.
    ///
    /// The connector hands over a live IEnumerable, not a snapshot; if it mutates the underlying
    /// collection while this class is still walking it, MoveNext() can throw partway through — an
    /// InvalidOperationException is the common shape, but nothing here assumes which one. Same
    /// pattern as ContextResolver.SafeOrders: keep whatever was read before the fault rather than
    /// discard it, and say exactly how many entries were lost so the gap is never silent. Losing an
    /// EXECUTION this way is worth an EventWarn, not a TraceEvent — unlike an order snapshot, a
    /// dropped fill is a permanent hole in the journal, not a value re-read on the next tick.
    /// </summary>
    private static List<MyTrade> SafeTrades(IEnumerable<MyTrade> trades)
    {
        var result = new List<MyTrade>();
        try
        {
            foreach (var trade in trades) result.Add(trade);
        }
        catch (Exception ex)
        {
            SdLog.EventWarn("Journal",
                "NewMyTrades batch enumeration faulted after {0} entrie(s) — the rest of this batch is lost: {1}",
                result.Count, ex.GetType().Name);
        }
        return result;
    }

    private void OnRegisterFailed(IDataFeedConnector connector, string message, Order order) =>
        Safely("Order", () =>
        {
            ReportRejection(order, "OrderRejected", message);
            if (InScope(order)) StateChanged?.Invoke();
        });

    private void OnCancelFailed(IDataFeedConnector connector, string message, Order order) =>
        Safely("Order", () =>
        {
            // Task 8, R1 minor: tells the guard its OWN cancel was refused, so a later hand-cancel
            // of the same order is never misreported as this class having stopped it — see
            // GuardEnforcer.NotifyCancelFailed's own doc for the 12/08-mirror-image incident this
            // prevents. A no-op whenever the guard was not the one waiting on this order.
            _enforcer?.NotifyCancelFailed(order, message);
            ReportRejection(order, "CancelRejected", message);
        });

    private void OnModifyFailed(IDataFeedConnector connector, Order order, Order newOrder, string error) =>
        Safely("Order", () => ReportRejection(order, "ModifyRejected", error));

    /// <summary>
    /// True when this order belongs to the account the deck is currently tracking, or when scope
    /// cannot be determined — see the doc on <see cref="TrackedAccountName"/> for why "unknown" means
    /// "let it through" rather than "hide it". Never throws: a scoping check must never be the reason
    /// a real rejection fails to reach the deck. Thin wrapper over <see cref="InScopeAccount"/>, which
    /// task 9 needed a second entry point for — a MyTrade carries its account directly rather than
    /// through an Order.
    /// </summary>
    private bool InScope(Order? order) => InScopeAccount(order?.Portfolio?.AccountID);

    /// <summary>
    /// The account-only core of <see cref="InScope"/> — same fail-open semantics, same "never
    /// throws" guarantee, generalised so a fill (which carries an account id but not necessarily an
    /// Order) can be scoped the same way an order rejection is.
    /// </summary>
    private bool InScopeAccount(string? accountId)
    {
        try
        {
            var tracked = TrackedAccountName?.Invoke();
            if (string.IsNullOrWhiteSpace(tracked)) return true; // not yet known — fail OPEN
            if (string.IsNullOrWhiteSpace(accountId)) return true; // cannot tell — fail OPEN
            return string.Equals(accountId, tracked, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            SdLog.TraceEvent("Order", "Account scope check faulted, letting the event through: {0}", ex.GetType().Name);
            return true; // fail OPEN — see the class-level remark on why a spurious flash beats a silent drop
        }
    }

    /// <summary>
    /// Sends the orderUpdate event the host turns into a red REJECTED on the key, SCOPED TO THE
    /// TRACKED ACCOUNT ONLY (review round 1, finding I1) — deliberately NOT also scoped to the
    /// tracked instrument: a flattenAccount failing on a different instrument of the SAME account is
    /// still the trader's own order failing, and instrument-scoping this would have swallowed it.
    ///
    /// The rejection is ALWAYS logged, on any account — see OnOrderChanged's INFO/TRACE split above
    /// for the same reasoning applied to logging — so an out-of-scope rejection stays visible to
    /// someone reading the log even though it never reaches the deck.
    ///
    /// The field names are the protocol's and are read by the host as they are: orderState, rejected,
    /// error, reason. `rejected: true` is what makes the key flash — the key's background stays
    /// normal, because a greyed background means "blocked", never "rejected".
    /// </summary>
    private void ReportRejection(Order? order, string error, string? reason)
    {
        var inScope = InScope(order);

        SdLog.EventWarn("Order", "ORDER REJECTED {0} — {1} {2} qty={3} instrument={4} account={5}: {6} (error={7}){8}",
            order?.Id ?? "?", order?.Direction, order?.Type, order?.QuantityToFill,
            ContextResolver.InstrumentName(order?.Security), order?.Portfolio?.AccountID ?? "?", reason, error,
            inScope ? string.Empty : " [different account — NOT forwarded to the deck]");

        if (!inScope) return;

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
            TrackedAccountName = null;
        }
    }
}
