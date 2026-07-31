using System;
using System.Collections.Generic;
using System.Linq;
using NinjaTrader.Cbi;
using NinjaTrader.NinjaScript.AddOns.StreamDeck.Models;
using NinjaTrader.NinjaScript.AddOns.StreamDeck.Utilities;

namespace NinjaTrader.NinjaScript.AddOns.StreamDeck.Services
{
    /// <summary>
    /// Executes trading actions via NinjaTrader's API.
    /// Every method validates context before execution.
    /// </summary>
    public class TradingEngine
    {
        private readonly ContextResolver _resolver;

        public TradingEngine(ContextResolver resolver)
        {
            _resolver = resolver;
        }

        #region Entry Orders

        public BridgeMessage BuyMarket(BridgeMessage cmd)
        {
            return SubmitMarketOrder(cmd, OrderAction.Buy);
        }

        public BridgeMessage SellMarket(BridgeMessage cmd)
        {
            return SubmitMarketOrder(cmd, OrderAction.Sell);
        }

        public BridgeMessage BuyLimit(BridgeMessage cmd)
        {
            return SubmitLimitOrder(cmd, OrderAction.Buy);
        }

        public BridgeMessage SellLimit(BridgeMessage cmd)
        {
            return SubmitLimitOrder(cmd, OrderAction.Sell);
        }

        private BridgeMessage SubmitMarketOrder(BridgeMessage cmd, OrderAction orderAction)
        {
            var ctx = ResolveContext(cmd);
            if (ctx.Error != null) return ctx.Error;

            var qty = cmd.GetPayloadInt("quantity") ?? 1;
            if (qty < 1)
                return BridgeMessage.CreateError(cmd.RequestId, cmd.Action, "INVALID_QUANTITY", "Quantity must be >= 1");

            try
            {
                var order = ctx.Account.CreateOrder(
                    ctx.Instrument,
                    orderAction,
                    OrderType.Market,
                    TimeInForce.Day,
                    qty,
                    0,  // limitPrice
                    0,  // stopPrice
                    string.Empty,
                    "StreamDeck",
                    null);

                ctx.Account.Submit(new[] { order });

                SdLogger.Info("[REQ:{0}] {1} {2} {3} Market submitted on {4} (OrderId: {5})",
                    cmd.RequestId, orderAction, qty, ctx.Instrument.FullName, ctx.Account.Name, order.OrderId);

                return BridgeMessage.CreateResponse(cmd.RequestId, cmd.Action, true, new
                {
                    orderId = order.OrderId,
                    message = $"{orderAction} {qty} {ctx.Instrument.FullName} Market submitted"
                });
            }
            catch (Exception ex)
            {
                SdLogger.Error(ex, $"[REQ:{cmd.RequestId}] Failed to submit market order");
                return BridgeMessage.CreateError(cmd.RequestId, cmd.Action, "ORDER_REJECTED", ex.Message);
            }
        }

        private BridgeMessage SubmitLimitOrder(BridgeMessage cmd, OrderAction orderAction)
        {
            var ctx = ResolveContext(cmd);
            if (ctx.Error != null) return ctx.Error;

            var qty = cmd.GetPayloadInt("quantity") ?? 1;
            var offsetTicks = cmd.GetPayloadInt("offsetTicks") ?? 0;

            if (qty < 1)
                return BridgeMessage.CreateError(cmd.RequestId, cmd.Action, "INVALID_QUANTITY", "Quantity must be >= 1");

            double tickSize = ctx.Instrument.MasterInstrument.TickSize;
            if (tickSize <= 0)
                return BridgeMessage.CreateError(cmd.RequestId, cmd.Action, "NO_MARKET_DATA",
                    $"Invalid tick size for {ctx.Instrument.FullName} — cannot compute a limit price.");

            // Without a market data subscription GetLastPrice returns 0, which would place the
            // limit a few ticks from zero. A sell limit there fills instantly at market, so the
            // order must be refused instead of submitted.
            double lastPrice = GetLastPrice(ctx.Instrument);
            if (lastPrice <= 0)
                return BridgeMessage.CreateError(cmd.RequestId, cmd.Action, "NO_MARKET_DATA",
                    $"No market data for {ctx.Instrument.FullName} — cannot compute a limit price. Open a chart or data window for this instrument.");

            try
            {
                double limitPrice = lastPrice + (offsetTicks * tickSize);

                // Round to tick size
                limitPrice = Math.Round(limitPrice / tickSize) * tickSize;

                if (limitPrice <= 0)
                    return BridgeMessage.CreateError(cmd.RequestId, cmd.Action, "NO_MARKET_DATA",
                        $"Computed limit price {limitPrice} is not valid for {ctx.Instrument.FullName}.");

                var order = ctx.Account.CreateOrder(
                    ctx.Instrument,
                    orderAction,
                    OrderType.Limit,
                    // Day, like market orders: a forgotten GTC entry could fill unattended
                    // in a later session.
                    TimeInForce.Day,
                    qty,
                    limitPrice,
                    0,
                    string.Empty,
                    "StreamDeck",
                    null);

                ctx.Account.Submit(new[] { order });

                SdLogger.Info("[REQ:{0}] {1} {2} {3} Limit on {4} @ {5} submitted",
                    cmd.RequestId, orderAction, qty, ctx.Instrument.FullName, ctx.Account.Name, limitPrice);

                return BridgeMessage.CreateResponse(cmd.RequestId, cmd.Action, true, new
                {
                    orderId = order.OrderId,
                    limitPrice,
                    message = $"{orderAction} {qty} {ctx.Instrument.FullName} Limit @ {limitPrice} submitted"
                });
            }
            catch (Exception ex)
            {
                SdLogger.Error(ex, $"[REQ:{cmd.RequestId}] Failed to submit limit order");
                return BridgeMessage.CreateError(cmd.RequestId, cmd.Action, "ORDER_REJECTED", ex.Message);
            }
        }

        #endregion

        #region Position Management

        public BridgeMessage Flatten(BridgeMessage cmd)
        {
            var ctx = ResolveContext(cmd);
            if (ctx.Error != null) return ctx.Error;

            try
            {
                ctx.Account.Flatten(new[] { ctx.Instrument });
                SdLogger.Info("[REQ:{0}] Flatten {1} on {2}", cmd.RequestId, ctx.Instrument.FullName, ctx.Account.Name);

                return BridgeMessage.CreateResponse(cmd.RequestId, cmd.Action, true, new
                {
                    message = $"Flatten {ctx.Instrument.FullName} submitted"
                });
            }
            catch (Exception ex)
            {
                SdLogger.Error(ex, $"[REQ:{cmd.RequestId}] Flatten failed");
                return BridgeMessage.CreateError(cmd.RequestId, cmd.Action, "ORDER_REJECTED", ex.Message);
            }
        }

        /// <summary>
        /// Cancels the working orders for the instrument WITHOUT touching the position.
        /// This is what "cancel pending orders" means — use <see cref="CancelOrders"/> (Close All)
        /// to also close the position.
        /// </summary>
        public BridgeMessage CancelWorkingOrders(BridgeMessage cmd)
        {
            var ctx = ResolveContext(cmd);
            if (ctx.Error != null) return ctx.Error;

            try
            {
                var orders = _resolver.FindActiveOrders(ctx.Account, ctx.Instrument);
                if (orders.Count == 0)
                {
                    return BridgeMessage.CreateResponse(cmd.RequestId, cmd.Action, true, new
                    {
                        ordersCancelled = 0,
                        message = $"No working order for {ctx.Instrument.FullName}"
                    });
                }

                ctx.Account.Cancel(orders);

                SdLogger.Info("[REQ:{0}] Cancelled {1} working order(s) for {2} on {3} (position untouched)",
                    cmd.RequestId, orders.Count, ctx.Instrument.FullName, ctx.Account.Name);

                return BridgeMessage.CreateResponse(cmd.RequestId, cmd.Action, true, new
                {
                    ordersCancelled = orders.Count,
                    message = $"Cancelled {orders.Count} order(s) for {ctx.Instrument.FullName}"
                });
            }
            catch (Exception ex)
            {
                SdLogger.Error(ex, $"[REQ:{cmd.RequestId}] Cancel working orders failed");
                return BridgeMessage.CreateError(cmd.RequestId, cmd.Action, "ORDER_REJECTED", ex.Message);
            }
        }

        /// <summary>
        /// "Close All": cancels every pending order AND closes the position.
        /// Kept deliberately destructive — this backs the Close All key.
        /// </summary>
        public BridgeMessage CancelOrders(BridgeMessage cmd)
        {
            var ctx = ResolveContext(cmd);
            if (ctx.Error != null) return ctx.Error;

            try
            {
                // Flatten cancels ALL pending orders AND closes any open position
                ctx.Account.Flatten(new[] { ctx.Instrument });

                SdLogger.Info("[REQ:{0}] Flatten (cancel all) {1} on {2}",
                    cmd.RequestId, ctx.Instrument.FullName, ctx.Account.Name);

                return BridgeMessage.CreateResponse(cmd.RequestId, cmd.Action, true, new
                {
                    message = $"Cancelled all orders and closed position for {ctx.Instrument.FullName}"
                });
            }
            catch (Exception ex)
            {
                SdLogger.Error(ex, $"[REQ:{cmd.RequestId}] Cancel orders failed");
                return BridgeMessage.CreateError(cmd.RequestId, cmd.Action, "ORDER_REJECTED", ex.Message);
            }
        }

        public BridgeMessage Reverse(BridgeMessage cmd)
        {
            var ctx = ResolveContext(cmd);
            if (ctx.Error != null) return ctx.Error;

            var position = _resolver.FindPosition(ctx.Account, ctx.Instrument);
            if (position == null)
                return BridgeMessage.CreateError(cmd.RequestId, cmd.Action, "NO_POSITION", "No open position to reverse.");

            try
            {
                var currentQty = (int)Math.Abs(position.Quantity);
                var currentDirection = position.MarketPosition;
                var reverseAction = currentDirection == MarketPosition.Long ? OrderAction.Sell : OrderAction.Buy;
                var reverseQty = currentQty * 2; // Close current + open same size opposite

                // Cancel the protective orders of the position being closed first. Submitting
                // the reversal alone leaves them working: a stop from the old long is a sell
                // stop, which after the reversal would ADD to the new short instead of
                // protecting it.
                int cancelled = 0;
                var stale = _resolver.FindActiveOrders(ctx.Account, ctx.Instrument);
                if (stale.Count > 0)
                {
                    ctx.Account.Cancel(stale);
                    cancelled = stale.Count;
                    SdLogger.Info("[REQ:{0}] Reverse: cancelled {1} working order(s) before reversing",
                        cmd.RequestId, cancelled);
                }

                var order = ctx.Account.CreateOrder(
                    ctx.Instrument,
                    reverseAction,
                    OrderType.Market,
                    TimeInForce.Day,
                    reverseQty,
                    0, 0,
                    string.Empty,
                    "StreamDeck_Reverse",
                    null);

                ctx.Account.Submit(new[] { order });

                SdLogger.Info("[REQ:{0}] Reverse {1} → {2} x{3}",
                    cmd.RequestId, currentDirection, reverseAction, reverseQty);

                return BridgeMessage.CreateResponse(cmd.RequestId, cmd.Action, true, new
                {
                    ordersCancelled = cancelled,
                    message = cancelled > 0
                        ? $"Reverse from {currentDirection} {currentQty} → {reverseAction} {reverseQty} ({cancelled} order(s) cancelled)"
                        : $"Reverse from {currentDirection} {currentQty} → {reverseAction} {reverseQty}"
                });
            }
            catch (Exception ex)
            {
                SdLogger.Error(ex, $"[REQ:{cmd.RequestId}] Reverse failed");
                return BridgeMessage.CreateError(cmd.RequestId, cmd.Action, "ORDER_REJECTED", ex.Message);
            }
        }

        #endregion

        #region Break-Even

        public BridgeMessage BreakEven(BridgeMessage cmd)
        {
            var ctx = ResolveContext(cmd);
            if (ctx.Error != null) return ctx.Error;

            var position = _resolver.FindPosition(ctx.Account, ctx.Instrument);
            if (position == null)
                return BridgeMessage.CreateError(cmd.RequestId, cmd.Action, "NO_POSITION", "No open position for break-even.");

            var stopOrders = _resolver.FindStopOrders(ctx.Account, ctx.Instrument);
            if (stopOrders.Count == 0)
                return BridgeMessage.CreateError(cmd.RequestId, cmd.Action, "NO_STOP_ORDER", "No active stop order to move to break-even.");

            var offsetTicks = cmd.GetPayloadInt("offsetTicks") ?? 0;
            double tickSize = ctx.Instrument.MasterInstrument.TickSize;
            if (tickSize <= 0)
                return BridgeMessage.CreateError(cmd.RequestId, cmd.Action, "NO_MARKET_DATA",
                    $"Invalid tick size for {ctx.Instrument.FullName}.");

            double avgPrice = position.AveragePrice;

            double bePrice;
            if (position.MarketPosition == MarketPosition.Long)
                bePrice = avgPrice + (offsetTicks * tickSize);
            else
                bePrice = avgPrice - (offsetTicks * tickSize);

            // Round to tick size
            bePrice = Math.Round(bePrice / tickSize) * tickSize;

            // A stop must stay on the protective side of the market. When the trade is at a
            // loss, break-even would put it on the wrong side and NinjaTrader rejects the
            // change with an opaque message — say so explicitly instead.
            double marketPrice = GetLastPrice(ctx.Instrument);
            if (marketPrice > 0)
            {
                bool wrongSide = position.MarketPosition == MarketPosition.Long
                    ? bePrice >= marketPrice
                    : bePrice <= marketPrice;

                if (wrongSide)
                {
                    return BridgeMessage.CreateError(cmd.RequestId, cmd.Action, "INVALID_STOP_PRICE",
                        $"Break-even price {bePrice} is on the wrong side of the market ({marketPrice}) — the trade is not in profit yet.");
                }
            }

            try
            {
                int moved = 0;
                foreach (var stop in stopOrders)
                {
                    stop.StopPriceChanged = bePrice;
                    // For StopLimit orders, maintain the original offset between stop and limit
                    if (stop.OrderType == OrderType.StopLimit)
                    {
                        double limitOffset = stop.LimitPrice - stop.StopPrice;
                        stop.LimitPriceChanged = bePrice + limitOffset;
                    }
                    ctx.Account.Change(new[] { stop });
                    moved++;
                }

                SdLogger.Info("[REQ:{0}] BE{1}: moved {2} stop(s) to {3} (avg entry: {4})",
                    cmd.RequestId,
                    offsetTicks != 0 ? $"+{offsetTicks}" : "",
                    moved, bePrice, avgPrice);

                return BridgeMessage.CreateResponse(cmd.RequestId, cmd.Action, true, new
                {
                    bePrice,
                    avgPrice,
                    offsetTicks,
                    stopsModified = moved,
                    message = $"Break-even: {moved} stop(s) moved to {bePrice}"
                });
            }
            catch (Exception ex)
            {
                SdLogger.Error(ex, $"[REQ:{cmd.RequestId}] Break-even failed");
                return BridgeMessage.CreateError(cmd.RequestId, cmd.Action, "ORDER_REJECTED", ex.Message);
            }
        }

        #endregion

        #region Stop/Target Management

        public BridgeMessage MoveStop(BridgeMessage cmd)
        {
            var ctx = ResolveContext(cmd);
            if (ctx.Error != null) return ctx.Error;

            var position = _resolver.FindPosition(ctx.Account, ctx.Instrument);
            if (position == null)
                return BridgeMessage.CreateError(cmd.RequestId, cmd.Action, "NO_POSITION", "No open position.");

            var stopOrders = _resolver.FindStopOrders(ctx.Account, ctx.Instrument);
            if (stopOrders.Count == 0)
                return BridgeMessage.CreateError(cmd.RequestId, cmd.Action, "NO_STOP_ORDER", "No active stop order to move.");

            var deltaTicks = cmd.GetPayloadInt("deltaTicks") ?? 0;
            if (deltaTicks == 0)
                return BridgeMessage.CreateError(cmd.RequestId, cmd.Action, "INVALID_PAYLOAD", "deltaTicks must be non-zero.");

            double tickSize = ctx.Instrument.MasterInstrument.TickSize;
            if (tickSize <= 0)
                return BridgeMessage.CreateError(cmd.RequestId, cmd.Action, "NO_MARKET_DATA",
                    $"Invalid tick size for {ctx.Instrument.FullName}.");

            try
            {
                // Shift EVERY working stop by the same delta. Picking one of them was
                // arbitrary on a scaled position, and left part of the position unprotected
                // at the old level. This matches how break-even treats all stops.
                var moved = new List<Order>();
                double firstOld = 0, firstNew = 0;

                foreach (var stop in stopOrders)
                {
                    double currentPrice = stop.StopPrice;

                    // Positive deltaTicks tightens the stop (moves it toward the market),
                    // negative gives the trade more room — in both directions.
                    double newPrice = position.MarketPosition == MarketPosition.Long
                        ? currentPrice + (deltaTicks * tickSize)
                        : currentPrice - (deltaTicks * tickSize);

                    newPrice = Math.Round(newPrice / tickSize) * tickSize;
                    if (newPrice <= 0) continue;

                    stop.StopPriceChanged = newPrice;
                    // For StopLimit orders, maintain the original offset between stop and limit
                    if (stop.OrderType == OrderType.StopLimit)
                    {
                        double limitOffset = stop.LimitPrice - stop.StopPrice;
                        stop.LimitPriceChanged = newPrice + limitOffset;
                    }

                    if (moved.Count == 0) { firstOld = currentPrice; firstNew = newPrice; }
                    moved.Add(stop);
                }

                if (moved.Count == 0)
                    return BridgeMessage.CreateError(cmd.RequestId, cmd.Action, "INVALID_STOP_PRICE",
                        "The requested move would put the stop at an invalid price.");

                ctx.Account.Change(moved);

                SdLogger.Info("[REQ:{0}] {1} stop(s) moved {2} tick(s): {3} → {4}",
                    cmd.RequestId, moved.Count, deltaTicks, firstOld, firstNew);

                return BridgeMessage.CreateResponse(cmd.RequestId, cmd.Action, true, new
                {
                    oldPrice = firstOld,
                    newPrice = firstNew,
                    deltaTicks,
                    stopsModified = moved.Count,
                    message = moved.Count > 1
                        ? $"{moved.Count} stops moved by {deltaTicks} tick(s)"
                        : $"Stop moved from {firstOld} to {firstNew}"
                });
            }
            catch (Exception ex)
            {
                SdLogger.Error(ex, $"[REQ:{cmd.RequestId}] Move stop failed");
                return BridgeMessage.CreateError(cmd.RequestId, cmd.Action, "ORDER_REJECTED", ex.Message);
            }
        }

        public BridgeMessage MoveTarget(BridgeMessage cmd)
        {
            var ctx = ResolveContext(cmd);
            if (ctx.Error != null) return ctx.Error;

            var position = _resolver.FindPosition(ctx.Account, ctx.Instrument);
            if (position == null)
                return BridgeMessage.CreateError(cmd.RequestId, cmd.Action, "NO_POSITION", "No open position.");

            var targetOrders = _resolver.FindTargetOrders(ctx.Account, ctx.Instrument);
            if (targetOrders.Count == 0)
                return BridgeMessage.CreateError(cmd.RequestId, cmd.Action, "NO_TARGET_ORDER", "No active target order to move.");

            var deltaTicks = cmd.GetPayloadInt("deltaTicks") ?? 0;
            if (deltaTicks == 0)
                return BridgeMessage.CreateError(cmd.RequestId, cmd.Action, "INVALID_PAYLOAD", "deltaTicks must be non-zero.");

            double tickSize = ctx.Instrument.MasterInstrument.TickSize;
            if (tickSize <= 0)
                return BridgeMessage.CreateError(cmd.RequestId, cmd.Action, "NO_MARKET_DATA",
                    $"Invalid tick size for {ctx.Instrument.FullName}.");

            try
            {
                // Shift every working target, for the same reason as MoveStop
                var moved = new List<Order>();
                double firstOld = 0, firstNew = 0;

                foreach (var target in targetOrders)
                {
                    double currentPrice = target.LimitPrice;

                    // Positive deltaTicks = move target further from entry (increase profit target)
                    double newPrice = position.MarketPosition == MarketPosition.Long
                        ? currentPrice + (deltaTicks * tickSize)
                        : currentPrice - (deltaTicks * tickSize);

                    newPrice = Math.Round(newPrice / tickSize) * tickSize;
                    if (newPrice <= 0) continue;

                    target.LimitPriceChanged = newPrice;

                    if (moved.Count == 0) { firstOld = currentPrice; firstNew = newPrice; }
                    moved.Add(target);
                }

                if (moved.Count == 0)
                    return BridgeMessage.CreateError(cmd.RequestId, cmd.Action, "INVALID_STOP_PRICE",
                        "The requested move would put the target at an invalid price.");

                ctx.Account.Change(moved);

                SdLogger.Info("[REQ:{0}] {1} target(s) moved {2} tick(s): {3} → {4}",
                    cmd.RequestId, moved.Count, deltaTicks, firstOld, firstNew);

                return BridgeMessage.CreateResponse(cmd.RequestId, cmd.Action, true, new
                {
                    oldPrice = firstOld,
                    newPrice = firstNew,
                    deltaTicks,
                    targetsModified = moved.Count,
                    message = moved.Count > 1
                        ? $"{moved.Count} targets moved by {deltaTicks} tick(s)"
                        : $"Target moved from {firstOld} to {firstNew}"
                });
            }
            catch (Exception ex)
            {
                SdLogger.Error(ex, $"[REQ:{cmd.RequestId}] Move target failed");
                return BridgeMessage.CreateError(cmd.RequestId, cmd.Action, "ORDER_REJECTED", ex.Message);
            }
        }

        #endregion

        #region Context Resolution

        private struct TradingContext
        {
            public Account Account;
            public Instrument Instrument;
            public BridgeMessage Error;
        }

        private TradingContext ResolveContext(BridgeMessage cmd)
        {
            var accountName = cmd.GetPayloadString("account");
            var instrumentName = cmd.GetPayloadString("instrument");

            if (string.IsNullOrWhiteSpace(accountName))
                return new TradingContext
                {
                    Error = BridgeMessage.CreateError(cmd.RequestId, cmd.Action, "CONTEXT_MISSING", "Account name is required.")
                };

            if (string.IsNullOrWhiteSpace(instrumentName))
                return new TradingContext
                {
                    Error = BridgeMessage.CreateError(cmd.RequestId, cmd.Action, "CONTEXT_MISSING", "Instrument name is required.")
                };

            var account = _resolver.FindAccount(accountName);
            if (account == null)
                return new TradingContext
                {
                    Error = BridgeMessage.CreateError(cmd.RequestId, cmd.Action, "ACCOUNT_NOT_FOUND", $"Account '{accountName}' not found.")
                };

            var instrument = _resolver.FindInstrument(instrumentName);
            if (instrument == null)
                return new TradingContext
                {
                    Error = BridgeMessage.CreateError(cmd.RequestId, cmd.Action, "INSTRUMENT_NOT_FOUND", $"Instrument '{instrumentName}' not found.")
                };

            return new TradingContext { Account = account, Instrument = instrument };
        }

        private double GetLastPrice(Instrument instrument)
        {
            try
            {
                return instrument.MarketData.Last.Price;
            }
            catch
            {
                try
                {
                    return instrument.MarketData.Bid.Price;
                }
                catch
                {
                    return 0.0;
                }
            }
        }

        #endregion
    }
}
