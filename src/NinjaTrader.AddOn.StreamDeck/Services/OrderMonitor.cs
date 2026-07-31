using System;
using NinjaTrader.Cbi;
using NinjaTrader.NinjaScript.AddOns.StreamDeck.Models;
using NinjaTrader.NinjaScript.AddOns.StreamDeck.Utilities;

namespace NinjaTrader.NinjaScript.AddOns.StreamDeck.Services
{
    /// <summary>
    /// Watches order outcomes on the tracked account and reports them to the bridge.
    ///
    /// Account.Submit is asynchronous: it returns without throwing even when the order is
    /// later refused (margin, closed market, invalid price). Without this monitor the
    /// Stream Deck key flashes OK for an order that never reached the market.
    /// </summary>
    public class OrderMonitor : IDisposable
    {
        private readonly BridgeClient _bridgeClient;
        private readonly object _lock = new object();
        private Account _subscribed;
        private bool _disposed;

        /// <summary>
        /// Raised when the tracked account reports something the deck displays: a position change
        /// or an order reaching a state that alters the protective orders. Wired to
        /// StatePublisher.PublishNow so the deck stops waiting for the next 500 ms tick.
        ///
        /// Deliberately driven by PositionUpdate rather than by the command that sent the order:
        /// Account.Submit returns before the position moves, so publishing on the command would
        /// broadcast the *previous* position and make the deck flicker to the right value later.
        /// </summary>
        public Action StateChanged { get; set; }

        public OrderMonitor(BridgeClient bridgeClient)
        {
            _bridgeClient = bridgeClient;
        }

        /// <summary>
        /// Subscribes to the given account, releasing any previous subscription.
        /// Safe to call on every state publish — re-subscribing to the same account is a no-op.
        /// </summary>
        public void Track(Account account)
        {
            lock (_lock)
            {
                if (_disposed || ReferenceEquals(_subscribed, account)) return;

                if (_subscribed != null)
                {
                    try
                    {
                        _subscribed.OrderUpdate -= OnOrderUpdate;
                        _subscribed.PositionUpdate -= OnPositionUpdate;
                    }
                    catch (Exception ex) { SdLogger.Warn("Could not unsubscribe order updates: {0}", ex.Message); }
                    _subscribed = null;
                }

                if (account == null) return;

                try
                {
                    account.OrderUpdate += OnOrderUpdate;
                    account.PositionUpdate += OnPositionUpdate;
                    _subscribed = account;
                    SdLogger.Info("Order monitor tracking account {0}", account.Name);
                }
                catch (Exception ex)
                {
                    SdLogger.Error(ex, "Could not subscribe to order updates");
                }
            }
        }

        /// <summary>
        /// The position actually moved — this is the moment the deck must be refreshed.
        /// </summary>
        private void OnPositionUpdate(object sender, PositionEventArgs e)
        {
            try
            {
                SdLogger.TraceEvent("Position", "PositionUpdate received — publishing state immediately");
                NotifyStateChanged();
            }
            catch (Exception ex)
            {
                // Never let an exception escape into NinjaTrader's event pipeline
                SdLogger.Fail("Position", ex, "Error handling position update");
            }
        }

        private void NotifyStateChanged()
        {
            var handler = StateChanged;
            if (handler == null) return;

            try { handler(); }
            catch (Exception ex) { SdLogger.Fail("StatePublish", ex, "Immediate state publish handler failed"); }
        }

        private void OnOrderUpdate(object sender, OrderEventArgs e)
        {
            try
            {
                if (e == null) return;

                // Only outcomes the trader cannot see from the position alone
                bool isRejection = e.OrderState == OrderState.Rejected || e.Error != ErrorCode.NoError;

                if (!isRejection)
                {
                    // Every other transition (submitted, accepted, working, filled, cancelled) is
                    // still recorded: reconstructing why a position ended up where it did needs
                    // the whole life of the order, not only its failures.
                    SdLogger.Event("Order", "Order {0} {1} — {2} {3} qty={4} filled={5} avgFill={6} instrument={7}",
                        e.OrderId ?? "?", e.OrderState,
                        e.Order != null ? e.Order.OrderAction.ToString() : "?",
                        e.Order != null ? e.Order.OrderType.ToString() : "?",
                        e.Quantity,
                        e.Order != null ? e.Order.Filled : 0,
                        e.Order != null ? e.Order.AverageFillPrice : 0,
                        e.Order != null && e.Order.Instrument != null ? e.Order.Instrument.FullName : "?");

                    // These change the stop/target picture the deck draws even when the net
                    // position does not move — a target cancelled, a stop replaced after a BE.
                    if (e.OrderState == OrderState.Filled || e.OrderState == OrderState.PartFilled ||
                        e.OrderState == OrderState.Cancelled)
                    {
                        NotifyStateChanged();
                    }
                    return;
                }

                var reason = BuildReason(e);
                SdLogger.EventWarn("Order", "ORDER REJECTED {0} {1} — {2} {3} qty={4} instrument={5}: {6} (error={7})",
                    e.OrderId ?? "?", e.OrderState,
                    e.Order != null ? e.Order.OrderAction.ToString() : "?",
                    e.Order != null ? e.Order.OrderType.ToString() : "?",
                    e.Quantity,
                    e.Order != null && e.Order.Instrument != null ? e.Order.Instrument.FullName : "?",
                    reason, e.Error);

                var payload = new System.Collections.Generic.Dictionary<string, object>();
                payload["orderId"] = e.OrderId ?? string.Empty;
                payload["orderState"] = e.OrderState.ToString();
                payload["rejected"] = true;
                payload["error"] = e.Error.ToString();
                payload["reason"] = reason;
                payload["quantity"] = e.Quantity;
                if (e.Order != null)
                {
                    payload["orderType"] = e.Order.OrderType.ToString();
                    payload["orderAction"] = e.Order.OrderAction.ToString();
                    payload["name"] = e.Order.Name ?? string.Empty;
                    if (e.Order.Instrument != null)
                        payload["instrument"] = e.Order.Instrument.FullName;
                }

                var msg = BridgeMessage.CreateEvent("orderUpdate", payload);
                _bridgeClient.SendAsync(msg).ConfigureAwait(false);

                // A rejected order leaves the working-orders list: refresh the deck rather than
                // leaving it showing a protection that no longer exists.
                NotifyStateChanged();
            }
            catch (Exception ex)
            {
                // Never let an exception escape into NinjaTrader's event pipeline
                SdLogger.Fail("Order", ex, "Error handling order update for {0}",
                    e != null ? (e.OrderId ?? "?") : "(null event)");
            }
        }

        private static string BuildReason(OrderEventArgs e)
        {
            if (!string.IsNullOrWhiteSpace(e.Comment)) return e.Comment;
            if (e.Error != ErrorCode.NoError) return e.Error.ToString();
            return e.OrderState.ToString();
        }

        public void Dispose()
        {
            lock (_lock)
            {
                if (_disposed) return;
                _disposed = true;

                if (_subscribed != null)
                {
                    try
                    {
                        _subscribed.OrderUpdate -= OnOrderUpdate;
                        _subscribed.PositionUpdate -= OnPositionUpdate;
                    }
                    catch { }
                    _subscribed = null;
                }

                StateChanged = null;
            }
        }
    }
}
