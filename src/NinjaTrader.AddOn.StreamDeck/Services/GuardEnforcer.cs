using System;
using System.Collections.Generic;
using NinjaTrader.Cbi;
using NinjaTrader.NinjaScript.AddOns.StreamDeck.Models;
using NinjaTrader.NinjaScript.AddOns.StreamDeck.Utilities;

namespace NinjaTrader.NinjaScript.AddOns.StreamDeck.Services
{
    /// <summary>
    /// Applies the bridge's safety rules to orders the bridge never sees.
    ///
    /// THE HOLE THIS CLOSES. The safety macro lives in the bridge, which only knows about commands
    /// coming from the deck. An order typed into the SuperDOM, the Chart Trader or the DOM reaches
    /// the market without ever crossing it — so a trader who has just been refused an entry can
    /// simply place it himself, and every limit becomes decorative at the exact moment it matters.
    /// This add-on runs INSIDE NinjaTrader and is subscribed to Account.OrderUpdate, so it sees
    /// every order on the account whatever its origin. It is the only place the hole can be closed.
    ///
    /// WHAT IT DOES. While the macro is refusing entries, an external order that would OPEN or GROW
    /// a position is cancelled as soon as NinjaTrader reports it, and the deck is told. The same
    /// applies to the contract cap, evaluated here against the live position and the order size.
    ///
    /// WHAT IT NEVER DOES. It never cancels an order that reduces exposure — a manual stop, a
    /// target, a scale-out, a panic exit. Trapping the trader inside a position is the one outcome
    /// none of these rules may ever produce, and it is why the test below is about growth and not
    /// about "is this an entry". It also never submits an order of its own: cancelling cannot
    /// create a position, so the worst case of a false positive is a cancelled order, not a trade.
    ///
    /// WHAT IT CANNOT DO. A MARKET order on a liquid future often fills before the platform reports
    /// it, and a filled order cannot be cancelled. That case is detected and reported, not
    /// prevented. And anyone willing to disable this add-on, or trade from the broker's own
    /// platform, is past what any of this can reach. The point is to turn a two-click impulse into
    /// a deliberate act of dismantling one's own safety net — friction against impulse, not
    /// security against an adversary.
    /// </summary>
    public class GuardEnforcer
    {
        /// <summary>
        /// Name carried by every order the deck submits (see TradingEngine). Anything else on the
        /// account came from somewhere the bridge could not vet.
        /// </summary>
        public const string DeckOrderName = "StreamDeck";

        private readonly ContextResolver _resolver;
        private readonly BridgeClient _bridgeClient;
        private readonly object _lock = new object();

        private bool _blocked;
        private string _reason = string.Empty;
        private int _maxContracts;

        /// <summary>
        /// The macro's contract cap, readable from outside.
        ///
        /// <see cref="CopyEngine"/> needs it before it submits: a copied order is exempt from the
        /// cancellation below on purpose, so the cap has to be honoured at the submit or not at all.
        /// </summary>
        public int MaxContracts
        {
            get { lock (_lock) { return _maxContracts; } }
        }

        /// <summary>True while the macro is refusing entries.</summary>
        public bool IsBlocking
        {
            get { lock (_lock) { return _blocked; } }
        }

        /// <summary>Orders already acted upon, so a second OrderUpdate does not cancel twice.</summary>
        private readonly HashSet<string> _handled = new HashSet<string>();

        /// <summary>
        /// Beyond this many remembered copies the oldest are forgotten. A copied order that lived
        /// longer than two thousand later copies has long since reached a terminal state.
        /// </summary>
        private const int MaxTrackedCopies = 2000;

        /// <summary>
        /// Orders the copy engine created. Tracked BY ID rather than by <c>Order.Name</c>: the name
        /// is a free string that a trader can type into a SuperDOM ticket, so trusting it would
        /// hand anyone a one-word bypass of every rule this class enforces. An id is assigned by
        /// NinjaTrader and cannot be claimed.
        /// </summary>
        private readonly HashSet<long> _copiedOrders = new HashSet<long>();

        /// <summary>Insertion order for <see cref="_copiedOrders"/>, so the set stays bounded.</summary>
        private readonly Queue<long> _copiedOrderRing = new Queue<long>();

        /// <summary>
        /// Orders a cancel was requested for, waiting to find out whether it worked.
        /// Order id → violation reason. Emptied by <see cref="ResolveOutcome"/>.
        /// </summary>
        private readonly Dictionary<string, string> _pending = new Dictionary<string, string>();

        /// <summary>The order got through: it filled despite the refusal.</summary>
        public const string OutcomeBypassed = "bypassed";

        /// <summary>The order was really stopped — cancelled or rejected before filling.</summary>
        public const string OutcomeStopped = "stopped";

        public GuardEnforcer(ContextResolver resolver, BridgeClient bridgeClient)
        {
            _resolver = resolver;
            _bridgeClient = bridgeClient;
        }

        /// <summary>
        /// Declares an order created by <see cref="CopyEngine"/>, so this class does not treat it
        /// as an order placed outside the deck.
        ///
        /// Called BEFORE the submit, deliberately: registering afterwards leaves a window in which
        /// NinjaTrader has already reported the order and the enforcer would cancel it.
        /// </summary>
        public void RegisterCopiedOrder(Order order)
        {
            if (order == null) return;

            lock (_lock)
            {
                if (!_copiedOrders.Add(order.Id)) return;
                _copiedOrderRing.Enqueue(order.Id);

                while (_copiedOrderRing.Count > MaxTrackedCopies)
                    _copiedOrders.Remove(_copiedOrderRing.Dequeue());
            }
        }

        private bool IsCopiedOrder(Order order)
        {
            lock (_lock)
            {
                return _copiedOrders.Contains(order.Id);
            }
        }

        /// <summary>Adopts the policy the bridge publishes whenever it changes.</summary>
        public void UpdatePolicy(bool blocked, string reason, int maxContracts)
        {
            lock (_lock)
            {
                bool changed = _blocked != blocked || _reason != reason || _maxContracts != maxContracts;
                _blocked = blocked;
                _reason = reason ?? string.Empty;
                _maxContracts = maxContracts < 0 ? 0 : maxContracts;

                // Cleared policy means a new episode starts from a clean slate: an order id seen
                // during the previous block must not stay marked as handled forever.
                //
                // `_pending` is NOT cleared with it. Those orders are still in flight, and their
                // outcome is the whole point of the record — dropping them because the block has
                // since lifted would lose exactly the bypasses that happened at the worst moment.
                if (!_blocked && _maxContracts <= 0)
                    _handled.Clear();

                if (changed)
                {
                    SdLogger.EventWarn("Guard", "Enforcement policy updated — blocked={0} reason={1} maxContracts={2}",
                        _blocked, string.IsNullOrEmpty(_reason) ? "-" : _reason, _maxContracts);
                }
            }
        }

        /// <summary>
        /// Called for every order update on the tracked account. Returns true when the order was
        /// cancelled, so the caller can refresh the deck.
        /// </summary>
        public bool Inspect(Account account, Order order)
        {
            bool blocked;
            string reason;
            int cap;
            lock (_lock)
            {
                blocked = _blocked;
                reason = _reason;
                cap = _maxContracts;
            }

            if (account == null || order == null) return false;

            // BEFORE every early return below: an order we already acted on must be followed to its
            // conclusion whatever the policy has become since. Clearing the block, or the order
            // ceasing to be cancellable — which is precisely what happens when it fills — would
            // otherwise lose the only update that says how the story ended.
            ResolveOutcome(order);

            if (!blocked && cap <= 0) return false;

            // The bridge already ran every rule against this one before it was sent.
            if (string.Equals(order.Name, DeckOrderName, StringComparison.OrdinalIgnoreCase))
                return false;

            // A copy of a master order the bridge already vetted. Cancelling it here would be
            // enforcing the same rule twice — and worse, asymmetrically: the master's entry would
            // stand while its copies were killed, leaving the follower accounts out of step with
            // an account that did take the trade. The copy engine stops copying entries by itself
            // when the macro blocks; this branch is about the orders already in flight.
            if (IsCopiedOrder(order))
                return false;

            if (!IsCancellable(order)) return false;
            if (!GrowsExposure(account, order)) return false;

            string violation = null;
            if (blocked)
            {
                violation = string.IsNullOrEmpty(reason) ? "guardBlocked" : reason;
            }
            else if (cap > 0)
            {
                int held = HeldQuantity(account, order);
                int resulting = held + order.Quantity;
                if (resulting <= cap) return false;
                violation = "maxContracts";
            }

            if (violation == null) return false;

            var id = order.OrderId ?? string.Empty;
            lock (_lock)
            {
                if (id.Length > 0 && !_handled.Add(id)) return false;
            }

            return CancelAndReport(account, order, violation);
        }

        private bool CancelAndReport(Account account, Order order, string violation)
        {
            string instrument = order.Instrument != null ? order.Instrument.FullName : "?";

            try
            {
                account.Cancel(new[] { order });
                SdLogger.EventWarn("Guard",
                    "EXTERNAL ORDER CANCEL REQUESTED — {0} {1} qty={2} on {3} (name='{4}', state={5}) refused by the safety macro: {6}",
                    order.OrderAction, order.OrderType, order.Quantity, instrument,
                    order.Name ?? string.Empty, order.OrderState, violation);
            }
            catch (Exception ex)
            {
                // A cancel that throws is the one case settled on the spot: the order is alive and
                // the macro did not stop it.
                SdLogger.Fail("Guard", ex, "Could not cancel external order on " + instrument);
                Report(order, violation, OutcomeBypassed, ex.Message);
                return false;
            }

            // Deliberately NOT reported as cancelled here. `Account.Cancel` is asynchronous, exactly
            // like `Account.Submit`: returning without throwing says only that the request left, and
            // on 2026-08-12 thirty of thirty-five such requests came back "Cancellation rejected —
            // Order is complete" because the market order had already filled. Every one of them had
            // been journalled as `cancelled: true`, and Bitlearn read the day as 100% rule
            // compliance while every trade carried a bypass marker.
            //
            // The verdict is whatever state the order reaches next, and ResolveOutcome sends it.
            lock (_lock)
            {
                _pending[order.OrderId ?? string.Empty] = violation;
            }
            return true;
        }

        /// <summary>
        /// Reports how a cancelled-at order actually ended, once NinjaTrader says so.
        ///
        /// Filled or part-filled means the trader got through — the bypass worked. Cancelled or
        /// rejected means it did not. Anything else is not a conclusion yet and is left pending.
        /// </summary>
        private void ResolveOutcome(Order order)
        {
            var id = order.OrderId ?? string.Empty;
            if (id.Length == 0) return;

            string violation;
            lock (_lock)
            {
                if (!_pending.TryGetValue(id, out violation)) return;

                var outcome = TerminalOutcome(order.OrderState);
                if (outcome == null) return;

                _pending.Remove(id);
                ReportOutcome(order, violation, outcome);
            }
        }

        /// <summary>Null while the order can still change its mind.</summary>
        private static string TerminalOutcome(OrderState state)
        {
            if (state == OrderState.Filled || state == OrderState.PartFilled) return OutcomeBypassed;
            if (state == OrderState.Cancelled || state == OrderState.Rejected) return OutcomeStopped;
            return null;
        }

        private void ReportOutcome(Order order, string violation, string outcome)
        {
            if (outcome == OutcomeStopped)
            {
                SdLogger.EventWarn("Guard", "EXTERNAL ORDER STOPPED — {0} {1} qty={2} on {3}: {4} ({5})",
                    order.OrderAction, order.OrderType, order.Quantity,
                    order.Instrument != null ? order.Instrument.FullName : "?", violation, order.OrderState);
            }
            else
            {
                // The line that matters on a bad day: the rule was refused AND the order got through.
                SdLogger.EventWarn("Guard", "GUARD BYPASSED — {0} {1} qty={2} on {3} FILLED despite {4}",
                    order.OrderAction, order.OrderType, order.Quantity,
                    order.Instrument != null ? order.Instrument.FullName : "?", violation);
            }

            Report(order, violation, outcome, string.Empty);
        }

        private void Report(Order order, string violation, string outcome, string error)
        {
            try
            {
                var payload = new Dictionary<string, object>();
                payload["violation"] = violation;
                // Kept for readers that predate `outcome`, and now honest: true only when the order
                // was really stopped, never merely asked to stop.
                payload["cancelled"] = outcome == OutcomeStopped;
                payload["outcome"] = outcome;
                payload["orderId"] = order.OrderId ?? string.Empty;
                payload["orderAction"] = order.OrderAction.ToString();
                payload["orderType"] = order.OrderType.ToString();
                payload["quantity"] = order.Quantity;
                payload["name"] = order.Name ?? string.Empty;
                payload["instrument"] = order.Instrument != null ? order.Instrument.FullName : string.Empty;
                if (!string.IsNullOrEmpty(error)) payload["error"] = error;

                // Fire-and-forget: this runs inside NinjaTrader's event pipeline and cannot wait.
                _ = _bridgeClient.SendAsync(BridgeMessage.CreateEvent("guardViolation", payload));
            }
            catch (Exception ex)
            {
                SdLogger.Fail("Guard", ex, "Could not report guard violation");
            }
        }

        /// <summary>Only states where a cancel still means something. A filled order is past us.</summary>
        private static bool IsCancellable(Order order)
        {
            return order.OrderState == OrderState.Submitted ||
                   order.OrderState == OrderState.Accepted ||
                   order.OrderState == OrderState.Working ||
                   order.OrderState == OrderState.TriggerPending ||
                   order.OrderState == OrderState.ChangeSubmitted;
        }

        /// <summary>
        /// Would this order make the position BIGGER?
        ///
        /// The whole safety of this class rests on this test. A manual stop on a long is a Sell and
        /// a manual stop on a short is a BuyToCover: both reduce, both must go through untouched
        /// even while every entry is refused.
        /// </summary>
        private bool GrowsExposure(Account account, Order order)
        {
            var position = _resolver.FindPosition(account, order.Instrument);
            var side = position != null ? position.MarketPosition : MarketPosition.Flat;

            switch (side)
            {
                case MarketPosition.Long:
                    return order.OrderAction == OrderAction.Buy;
                case MarketPosition.Short:
                    return order.OrderAction == OrderAction.SellShort;
                default:
                    // Flat: only the two actions that open a position count.
                    return order.OrderAction == OrderAction.Buy || order.OrderAction == OrderAction.SellShort;
            }
        }

        private int HeldQuantity(Account account, Order order)
        {
            var position = _resolver.FindPosition(account, order.Instrument);
            return position != null ? Math.Abs(position.Quantity) : 0;
        }
    }
}
