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

        /// <summary>Orders already acted upon, so a second OrderUpdate does not cancel twice.</summary>
        private readonly HashSet<string> _handled = new HashSet<string>();

        public GuardEnforcer(ContextResolver resolver, BridgeClient bridgeClient)
        {
            _resolver = resolver;
            _bridgeClient = bridgeClient;
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
            if (!blocked && cap <= 0) return false;

            // The bridge already ran every rule against this one before it was sent.
            if (string.Equals(order.Name, DeckOrderName, StringComparison.OrdinalIgnoreCase))
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
                    "EXTERNAL ORDER CANCELLED — {0} {1} qty={2} on {3} (name='{4}', state={5}) refused by the safety macro: {6}",
                    order.OrderAction, order.OrderType, order.Quantity, instrument,
                    order.Name ?? string.Empty, order.OrderState, violation);
            }
            catch (Exception ex)
            {
                // A cancel that fails is exactly the case worth shouting about: the order is alive
                // and the macro did not stop it. Report it as such rather than staying silent.
                SdLogger.Fail("Guard", ex, "Could not cancel external order on " + instrument);
                Report(order, violation, false, ex.Message);
                return false;
            }

            Report(order, violation, true, string.Empty);
            return true;
        }

        private void Report(Order order, string violation, bool cancelled, string error)
        {
            try
            {
                var payload = new Dictionary<string, object>();
                payload["violation"] = violation;
                payload["cancelled"] = cancelled;
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
