using System;
using System.Collections.Generic;

using ATAS.DataFeedsCore;
using ATAS.Indicators;

namespace TradeDeck.Atas.Strategy;

/// <summary>
/// Applies the bridge's safety rules to orders the bridge never sees.
///
/// THE HOLE THIS CLOSES. The safety macro lives in the bridge, which only knows about commands
/// coming from the deck. An order typed into the DOM or the Chart Trader reaches the market without
/// ever crossing it — so a trader who has just been refused an entry can simply place it himself,
/// and every limit becomes decorative at the exact moment it matters. This strategy runs INSIDE the
/// platform and is subscribed to the connector, so it sees every order on the account whatever its
/// origin. It is the only place the hole can be closed.
///
/// WHAT IT NEVER DOES. It never cancels an order that reduces exposure — a manual stop, a target, a
/// scale-out, a panic exit. Trapping the trader inside a position is the one outcome none of these
/// rules may ever produce, and it is why the test below is about GROWTH and not about "is this an
/// entry". It also never submits an order of its own: cancelling cannot create a position, so the
/// worst case of a false positive is a cancelled order, not a trade.
///
/// WHAT IT CANNOT DO. A market order on a liquid future often fills before the platform reports it,
/// and a filled order cannot be cancelled. That case is detected and reported, not prevented. And
/// anyone willing to stop this strategy, or trade from the broker's own platform, is past what any
/// of this can reach. The point is to turn a two-click impulse into a deliberate act of dismantling
/// one's own safety net.
///
/// IT KNOWS NO REASON BY NAME. `reason` travels through untouched — dailyLoss, tradeLimit,
/// beforeSessionStart or anything the bridge adds later. The clock, the arming and the countdown are
/// the bridge's business alone; this class never reads the time.
///
/// THE HOLE THIS DOES NOT CLOSE. This class only ever sees an order through a connector event
/// (OrderChanged / NewOrders — see OrderMonitor). In degraded mode (task 3: host registry
/// unreachable) OrderMonitor.Refresh receives an empty connector list, subscribes to nothing, and
/// Inspect is therefore never called at all: an order placed outside the deck goes completely
/// unwatched by this class while degraded, with no warning specific to this file (OrderMonitor logs
/// its own "no connector watched" line once per episode, but that line does not say guard
/// enforcement is part of what stopped). A periodic poll of ITradingManager.Orders to compensate was
/// deliberately left out of this plan (task 9) — it would be a distinct, unmeasured mechanism. The
/// only safety net left in degraded mode is the macro on the BRIDGE side, which keeps refusing
/// deck-originated commands regardless of this class's mode; it cannot reach a hand-typed DOM order
/// either way, degraded or not, because such an order never crosses the bridge in the first place.
/// </summary>
internal sealed class GuardEnforcer
{
    private readonly ContextResolver _resolver;
    private readonly BridgeClient _bridge;
    private readonly OrderEngine _engine;
    private readonly IOrderGateway _gateway;

    private readonly object _lock = new();

    private bool _blocked;
    private string _reason = string.Empty;
    private int _maxContracts;
    private string _trackedAccount = string.Empty;

    /// <summary>Orders already acted upon, so a second update does not cancel twice.</summary>
    private readonly HashSet<string> _handled = new(StringComparer.Ordinal);

    /// <summary>
    /// Orders a cancel was requested for, waiting to find out whether it worked.
    /// Order id → violation reason.
    /// </summary>
    private readonly Dictionary<string, string> _pending = new(StringComparer.Ordinal);

    /// <summary>The order got through: it filled despite the refusal.</summary>
    public const string OutcomeBypassed = "bypassed";

    /// <summary>The order was really stopped — cancelled or rejected before filling.</summary>
    public const string OutcomeStopped = "stopped";

    public int MaxContracts { get { lock (_lock) return _maxContracts; } }
    public bool IsBlocking { get { lock (_lock) return _blocked; } }

    public GuardEnforcer(ContextResolver resolver, BridgeClient bridge, OrderEngine engine,
                         IOrderGateway gateway)
    {
        _resolver = resolver;
        _bridge = bridge;
        _engine = engine;
        _gateway = gateway;
    }

    public void SetTrackedAccount(string account)
    {
        lock (_lock) _trackedAccount = account ?? string.Empty;
    }

    /// <summary>Adopts the policy the bridge publishes whenever it changes.</summary>
    public void UpdatePolicy(bool blocked, string? reason, int maxContracts)
    {
        lock (_lock)
        {
            var changed = _blocked != blocked
                       || !string.Equals(_reason, reason ?? string.Empty, StringComparison.Ordinal)
                       || _maxContracts != maxContracts;

            _blocked = blocked;
            _reason = reason ?? string.Empty;
            _maxContracts = maxContracts;

            // Logged only on change: the bridge pushes this on connection and then only when it
            // moves, but the discipline belongs on both sides of the wire.
            if (!changed) return;
        }

        SdLog.Event("Safety", "Guard policy adopted — blocked={0} reason={1} maxContracts={2}",
            blocked, string.IsNullOrEmpty(reason) ? "-" : reason, maxContracts);
    }

    /// <summary>
    /// Called for every order update on the connector. Returns true when a cancel was requested, so
    /// the caller can refresh the deck.
    ///
    /// GUARDED AT THE TOP LEVEL, NOT JUST INSIDE ITS OWN HELPERS. This runs on a connector event
    /// thread (OrderChanged, or a foreach over a NewOrders batch) with no wrapper of its own beneath
    /// it in the platform's dispatch loop — an exception escaping here does not just skip this one
    /// order, it can abort every order still left in the same NewOrders batch (a bracket's entry,
    /// stop and target can all arrive together) and, worse, propagate into ATAS's own event pipeline.
    /// Every risky call inside already has its own try/catch (Cancel, the report), but a plain
    /// property read (order.Portfolio, a connector's Position row) is not guaranteed never to throw,
    /// and the one thing this class must never do on an error it cannot explain is decide FOR ITSELF
    /// whether to cancel: failing to "did nothing" is the only wrong outcome (Inspect returning false)
    /// that stays inside this class's own bound of never causing harm.
    /// </summary>
    public bool Inspect(Order? order)
    {
        try
        {
            return InspectCore(order);
        }
        catch (Exception ex)
        {
            SdLog.Fail("Safety", ex, "Guard inspection faulted — order left untouched");
            return false;
        }
    }

    private bool InspectCore(Order? order)
    {
        if (order == null) return false;

        bool blocked;
        string reason;
        int cap;
        string account;
        lock (_lock)
        {
            blocked = _blocked;
            reason = _reason;
            cap = _maxContracts;
            account = _trackedAccount;
        }

        // BEFORE every early return below: an order we already acted on must be followed to its
        // conclusion whatever the policy has become since. Clearing the block, or the order ceasing
        // to be cancellable — which is precisely what happens when it fills — would otherwise lose
        // the only update that says how the story ended.
        ResolveOutcome(order);

        if (!blocked && cap <= 0) return false;

        // SCOPED TO THE TRACKED ACCOUNT. Every safety rule of this protocol is scoped to one
        // account, and enforcing on a portfolio nobody designated is how, on 2026-08-21, a locked
        // macro coexisted with an hour of hand-placed orders.
        if (account.Length > 0 &&
            !string.Equals(order.Portfolio?.AccountID, account, StringComparison.OrdinalIgnoreCase))
            return false;

        // The bridge already ran every rule against this one before it was sent.
        if (_engine.IsOurs(order)) return false;

        if (!ContextResolver.IsActive(order)) return false;
        if (!GrowsExposure(order)) return false;

        string violation;
        if (blocked)
        {
            violation = string.IsNullOrEmpty(reason) ? "guardBlocked" : reason;
        }
        else
        {
            var held = HeldQuantity(order);
            var resulting = held + (int)order.Unfilled;
            if (resulting <= cap) return false;
            violation = "maxContracts";
        }

        var id = order.Id ?? string.Empty;
        lock (_lock)
        {
            if (id.Length > 0 && !_handled.Add(id)) return false;
        }

        return CancelAndReport(order, violation);
    }

    private bool CancelAndReport(Order order, string violation)
    {
        var instrument = ContextResolver.InstrumentName(order.Security);

        try
        {
            _gateway.Cancel(order);

            SdLog.EventWarn("Safety",
                "EXTERNAL ORDER CANCEL REQUESTED — {0} {1} qty={2} on {3} (comment='{4}', state={5}) refused by the safety macro: {6}",
                order.Direction, order.Type, order.QuantityToFill, instrument,
                order.Comment ?? string.Empty, order.State, violation);
        }
        catch (Exception ex)
        {
            // A cancel that throws is the one case settled on the spot: the order is alive and the
            // macro did not stop it.
            SdLog.Fail("Safety", ex, "Could not cancel external order on {0}", instrument);
            Report(order, violation, OutcomeBypassed, ex.Message);
            return false;
        }

        // DELIBERATELY NOT REPORTED AS CANCELLED HERE. Cancelling is asynchronous: returning without
        // throwing says only that the request left. On 2026-08-12, thirty of thirty-five such
        // requests came back "order is complete" because the market order had already filled — and
        // every one of them had been journalled as cancelled:true, so Bitlearn read the day as 100%
        // rule compliance while every trade carried a bypass. The verdict is whatever state the
        // order reaches NEXT, and ResolveOutcome sends it.
        lock (_lock) _pending[order.Id ?? string.Empty] = violation;
        return true;
    }

    /// <summary>
    /// Reports how a cancelled-at order actually ended, once the platform says so.
    ///
    /// ATAS only has four order states, so the conclusion is read from what actually traded rather
    /// than from a state name: anything filled means the trader got through — the bypass worked.
    /// </summary>
    private void ResolveOutcome(Order order)
    {
        var id = order.Id ?? string.Empty;
        if (id.Length == 0) return;

        string violation;
        string outcome;
        lock (_lock)
        {
            if (!_pending.TryGetValue(id, out violation!)) return;

            var terminal = TerminalOutcome(order);
            if (terminal == null) return;

            outcome = terminal;
            _pending.Remove(id);
        }

        var instrument = ContextResolver.InstrumentName(order.Security);
        if (outcome == OutcomeStopped)
        {
            SdLog.EventWarn("Safety", "EXTERNAL ORDER STOPPED — {0} {1} qty={2} on {3}: {4} ({5})",
                order.Direction, order.Type, order.QuantityToFill, instrument, violation, order.State);
        }
        else
        {
            // The line that matters on a bad day: the rule refused AND the order got through.
            SdLog.EventWarn("Safety", "GUARD BYPASSED — {0} {1} qty={2} on {3} FILLED despite {4}",
                order.Direction, order.Type, order.QuantityToFill, instrument, violation);
        }

        Report(order, violation, outcome, string.Empty);
    }

    /// <summary>Null while the order can still change its mind.</summary>
    private static string? TerminalOutcome(Order order) => order.Status() switch
    {
        // Anything that traded is a bypass, even partially and even if the rest was then cancelled:
        // the trader took exposure the rule was refusing, which is the whole point of the report.
        OrderStatus.Filled or OrderStatus.PartlyFilled => OutcomeBypassed,
        OrderStatus.Canceled => OutcomeStopped,
        // Placed and None are not a conclusion yet.
        _ => null,
    };

    private void Report(Order order, string violation, string outcome, string error)
    {
        try
        {
            var payload = new Dictionary<string, object?>
            {
                ["violation"] = violation,
                // Kept for readers that predate `outcome`, and now honest: true only when the order
                // was really stopped, never merely asked to stop.
                ["cancelled"] = outcome == OutcomeStopped,
                ["outcome"] = outcome,
                ["orderId"] = order.Id ?? string.Empty,
                ["orderAction"] = order.Direction.ToString(),
                ["orderType"] = order.Type.ToString(),
                ["quantity"] = (int)order.QuantityToFill,
                ["name"] = order.Comment ?? string.Empty,
                ["instrument"] = ContextResolver.InstrumentName(order.Security),
            };
            if (!string.IsNullOrEmpty(error)) payload["error"] = error;

            // Fire-and-forget: this runs inside the connector's event pipeline and cannot wait.
            _ = _bridge.SendAsync(BridgeMessage.Notify("guardViolation", payload));
        }
        catch (Exception ex)
        {
            SdLog.Fail("Safety", ex, "Could not report guard violation");
        }
    }

    /// <summary>
    /// Would this order make the position BIGGER?
    ///
    /// The whole safety of this class rests on this test. A manual stop on a long is a Sell and a
    /// manual stop on a short is a Buy: both reduce, both must go through untouched even while every
    /// entry is refused.
    /// </summary>
    private bool GrowsExposure(Order order)
    {
        var portfolio = order.Portfolio;
        var position = _resolver.FindPosition(portfolio, order.Security);

        if (position == null) return true;               // flat: any order opens
        if (position.Volume > 0m) return order.Direction == OrderDirections.Buy;
        return order.Direction == OrderDirections.Sell;
    }

    private int HeldQuantity(Order order)
    {
        var position = _resolver.FindPosition(order.Portfolio, order.Security);
        return position == null ? 0 : (int)Math.Abs(position.Volume);
    }
}
