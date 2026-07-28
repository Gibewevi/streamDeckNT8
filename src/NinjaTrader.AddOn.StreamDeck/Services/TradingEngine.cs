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

            try
            {
                double lastPrice = GetLastPrice(ctx.Instrument);
                double tickSize = ctx.Instrument.MasterInstrument.TickSize;
                double limitPrice = lastPrice + (offsetTicks * tickSize);

                // Round to tick size
                limitPrice = Math.Round(limitPrice / tickSize) * tickSize;

                var order = ctx.Account.CreateOrder(
                    ctx.Instrument,
                    orderAction,
                    OrderType.Limit,
                    TimeInForce.Gtc,
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
                    message = $"Reverse from {currentDirection} {currentQty} → {reverseAction} {reverseQty}"
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
            double avgPrice = position.AveragePrice;

            double bePrice;
            if (position.MarketPosition == MarketPosition.Long)
                bePrice = avgPrice + (offsetTicks * tickSize);
            else
                bePrice = avgPrice - (offsetTicks * tickSize);

            // Round to tick size
            bePrice = Math.Round(bePrice / tickSize) * tickSize;

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

            try
            {
                // Move the closest stop order (most conservative)
                var stop = stopOrders[0]; // Already sorted by proximity

                double currentPrice = stop.StopPrice;
                double newPrice;

                // Positive deltaTicks = move stop away from price (give more room)
                // Negative deltaTicks = move stop closer to price (tighten)
                if (position.MarketPosition == MarketPosition.Long)
                    newPrice = currentPrice + (deltaTicks * tickSize);
                else
                    newPrice = currentPrice - (deltaTicks * tickSize);

                newPrice = Math.Round(newPrice / tickSize) * tickSize;

                stop.StopPriceChanged = newPrice;
                // For StopLimit orders, maintain the original offset between stop and limit
                if (stop.OrderType == OrderType.StopLimit)
                {
                    double limitOffset = stop.LimitPrice - stop.StopPrice;
                    stop.LimitPriceChanged = newPrice + limitOffset;
                }
                ctx.Account.Change(new[] { stop });

                SdLogger.Info("[REQ:{0}] Stop moved {1} tick(s): {2} → {3}",
                    cmd.RequestId, deltaTicks, currentPrice, newPrice);

                return BridgeMessage.CreateResponse(cmd.RequestId, cmd.Action, true, new
                {
                    oldPrice = currentPrice,
                    newPrice,
                    deltaTicks,
                    message = $"Stop moved from {currentPrice} to {newPrice}"
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

            try
            {
                var target = targetOrders[0];
                double currentPrice = target.LimitPrice;
                double newPrice;

                // Positive deltaTicks = move target further from entry (increase profit target)
                if (position.MarketPosition == MarketPosition.Long)
                    newPrice = currentPrice + (deltaTicks * tickSize);
                else
                    newPrice = currentPrice - (deltaTicks * tickSize);

                newPrice = Math.Round(newPrice / tickSize) * tickSize;

                target.LimitPriceChanged = newPrice;
                ctx.Account.Change(new[] { target });

                SdLogger.Info("[REQ:{0}] Target moved {1} tick(s): {2} → {3}",
                    cmd.RequestId, deltaTicks, currentPrice, newPrice);

                return BridgeMessage.CreateResponse(cmd.RequestId, cmd.Action, true, new
                {
                    oldPrice = currentPrice,
                    newPrice,
                    deltaTicks,
                    message = $"Target moved from {currentPrice} to {newPrice}"
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
