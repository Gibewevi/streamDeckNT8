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

        /// <summary>
        /// Closes EVERY position on the account and cancels every working order on it.
        ///
        /// Distinct from <see cref="Flatten"/>, which only touches the selected instrument. This
        /// one backs the automatic liquidation on daily loss, and there the account is the unit
        /// that matters: the limit is computed on account P&amp;L, so announcing "the day is over"
        /// while a position stays open on an instrument the trader was not watching would be worse
        /// than doing nothing — he would believe he was flat.
        ///
        /// Orders are cancelled FIRST and account-wide, including on instruments carrying no
        /// position: a resting entry that fills afterwards would reopen exactly the exposure that
        /// was just closed.
        ///
        /// Never reachable from a key press. Its only caller is the safety macro, via the bridge.
        /// </summary>
        public BridgeMessage FlattenAccount(BridgeMessage cmd)
        {
            var accountName = cmd.GetPayloadString("account");
            if (string.IsNullOrWhiteSpace(accountName))
                return BridgeMessage.CreateError(cmd.RequestId, cmd.Action, "CONTEXT_MISSING", "Account name is required.");

            var account = _resolver.FindAccount(accountName);
            if (account == null)
                return BridgeMessage.CreateError(cmd.RequestId, cmd.Action, "ACCOUNT_NOT_FOUND", $"Account '{accountName}' not found.");

            try
            {
                var orders = _resolver.FindAllActiveOrders(account);
                if (orders.Count > 0) account.Cancel(orders);

                var instruments = new List<Instrument>();
                lock (account.Positions)
                {
                    foreach (Position position in account.Positions)
                    {
                        if (position.MarketPosition != MarketPosition.Flat && position.Instrument != null)
                            instruments.Add(position.Instrument);
                    }
                }

                if (instruments.Count > 0) account.Flatten(instruments);

                SdLogger.Warn("[REQ:{0}] ACCOUNT LIQUIDATION on {1} — {2} order(s) cancelled, {3} position(s) flattened",
                    cmd.RequestId, account.Name, orders.Count, instruments.Count);

                return BridgeMessage.CreateResponse(cmd.RequestId, cmd.Action, true, new
                {
                    ordersCancelled = orders.Count,
                    positionsFlattened = instruments.Count,
                    message = $"Account {account.Name} liquidated: {instruments.Count} position(s), {orders.Count} order(s)"
                });
            }
            catch (Exception ex)
            {
                // The worst outcome this whole feature can produce: the trader is told the day is
                // closed while his position is still live. Logged as an error and reported as a
                // failure so the deck can turn red instead of pretending it worked.
                SdLogger.Error(ex, $"[REQ:{cmd.RequestId}] ACCOUNT LIQUIDATION FAILED on {accountName}");
                return BridgeMessage.CreateError(cmd.RequestId, cmd.Action, "ORDER_REJECTED", ex.Message);
            }
        }

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

            var stopOrders = _resolver.FindStopOrders(ctx.Account, ctx.Instrument);

            try
            {
                // Aucun stop attaché : on le CRÉE plutôt que de refuser. Exiger une stratégie ATM
                // pour poser un break-even priverait de protection toute entrée passée depuis le
                // deck, alors que l'add-on a tout ce qu'il faut pour soumettre l'ordre lui-même.
                if (stopOrders.Count == 0)
                {
                    var protectQty = (int)Math.Abs(position.Quantity);
                    if (protectQty < 1)
                        return BridgeMessage.CreateError(cmd.RequestId, cmd.Action, "NO_POSITION",
                            "Position quantity is zero — nothing to protect.");

                    // Le stop sort de la position : il va donc dans le sens inverse de celle-ci.
                    var protectAction = position.MarketPosition == MarketPosition.Long
                        ? OrderAction.Sell
                        : OrderAction.Buy;

                    var stopOrder = ctx.Account.CreateOrder(
                        ctx.Instrument,
                        protectAction,
                        OrderType.StopMarket,
                        TimeInForce.Day,
                        protectQty,
                        0,          // limitPrice
                        bePrice,    // stopPrice
                        string.Empty,
                        "StreamDeck_BE",
                        null);

                    ctx.Account.Submit(new[] { stopOrder });

                    SdLogger.Info("[REQ:{0}] BE{1}: CREATED protective {2} stop x{3} at {4} (avg entry: {5}, OrderId: {6})",
                        cmd.RequestId,
                        offsetTicks != 0 ? $"+{offsetTicks}" : "",
                        protectAction, protectQty, bePrice, avgPrice, stopOrder.OrderId);

                    // Submit est asynchrone : un refus (marge, marché fermé) arrivera plus tard
                    // via OrderMonitor. Ce retour signale l'envoi, pas l'acceptation.
                    return BridgeMessage.CreateResponse(cmd.RequestId, cmd.Action, true, new
                    {
                        bePrice,
                        avgPrice,
                        offsetTicks,
                        stopsCreated = 1,
                        stopsModified = 0,
                        message = $"Break-even: protective stop created at {bePrice}"
                    });
                }

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
                    stopsCreated = 0,
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

        #region Bracket — Take Profit / Stop Loss

        /// <summary>
        /// Names carried by the two protective orders this macro owns.
        ///
        /// They are what separates "the stop this macro placed for this position" from "a stop the
        /// trader put there himself" — an ATM strategy, a stop dragged on the chart, a break-even.
        /// Only the first kind is ever repriced or resized here: silently moving a stop the trader
        /// placed with his own hands would take away the protection he chose.
        /// </summary>
        private const string BracketStopName = "StreamDeck_SL";
        private const string BracketTargetName = "StreamDeck_TP";

        /// <summary>
        /// Places the take profit and/or the stop loss of the open position, computed in ticks from
        /// its AVERAGE PRICE and in the direction of the position.
        ///
        /// Sent by the host's Auto TP/SL automatism once the position actually exists, never
        /// alongside the entry order: Account.Submit is asynchronous and returns long before the
        /// fill, so an entry price read at submit time would be a guess. The average price of the
        /// position is the only value that is true — and it is what makes a scale-in work, since
        /// resending the same command is enough for both legs to follow the new average.
        ///
        /// 0 disables a leg, on either side. It means "do not place this one", never "cancel what
        /// is already there": a trader who sets his take profit back to 0 is saying the macro
        /// should stop managing it, not that the protection currently working should vanish.
        ///
        /// Both legs go out under the same OCO id. Without it, a filled take profit leaves the stop
        /// working on a flat position — and a stop on a flat position is an ENTRY the moment it
        /// triggers, opening the reverse of the trade that was just closed.
        /// </summary>
        public BridgeMessage AttachBracket(BridgeMessage cmd)
        {
            var ctx = ResolveContext(cmd);
            if (ctx.Error != null) return ctx.Error;

            var position = _resolver.FindPosition(ctx.Account, ctx.Instrument);
            if (position == null)
                return BridgeMessage.CreateError(cmd.RequestId, cmd.Action, "NO_POSITION",
                    "No open position to attach a bracket to.");

            var stopTicks = cmd.GetPayloadInt("stopLossTicks") ?? 0;
            var targetTicks = cmd.GetPayloadInt("takeProfitTicks") ?? 0;

            // A negative distance would put the stop on the profit side and the target on the loss
            // side: two instantly marketable orders, both closing the trade by surprise.
            if (stopTicks < 0 || targetTicks < 0)
                return BridgeMessage.CreateError(cmd.RequestId, cmd.Action, "INVALID_PAYLOAD",
                    "stopLossTicks and takeProfitTicks must be zero (leg disabled) or positive.");

            if (stopTicks == 0 && targetTicks == 0)
            {
                return BridgeMessage.CreateResponse(cmd.RequestId, cmd.Action, true, new
                {
                    legsPlaced = 0,
                    message = "Both legs disabled (0) — nothing to place."
                });
            }

            double tickSize = ctx.Instrument.MasterInstrument.TickSize;
            if (tickSize <= 0)
                return BridgeMessage.CreateError(cmd.RequestId, cmd.Action, "NO_MARKET_DATA",
                    $"Invalid tick size for {ctx.Instrument.FullName} — cannot compute a bracket.");

            int qty = (int)Math.Abs(position.Quantity);
            if (qty < 1)
                return BridgeMessage.CreateError(cmd.RequestId, cmd.Action, "NO_POSITION",
                    "Position quantity is zero — nothing to protect.");

            bool isLong = position.MarketPosition == MarketPosition.Long;
            double avgPrice = position.AveragePrice;

            // Both legs LEAVE the position, so both go the opposite way to it. Same convention as
            // the break-even stop above: a long is protected by a Sell, a short by a Buy.
            var exitAction = isLong ? OrderAction.Sell : OrderAction.Buy;

            // 0 when there is no market data subscription. Every side test below is then skipped
            // rather than guessed — same posture as break-even: not knowing the market is not a
            // reason to leave a position unprotected.
            double marketPrice = GetLastPrice(ctx.Instrument);

            double stopPrice = RoundToTick(isLong ? avgPrice - (stopTicks * tickSize)
                                                  : avgPrice + (stopTicks * tickSize), tickSize);
            double targetPrice = RoundToTick(isLong ? avgPrice + (targetTicks * tickSize)
                                                    : avgPrice - (targetTicks * tickSize), tickSize);

            // Une jambe laissée par une position de sens OPPOSÉ est le seul ordre que cette macro
            // puisse produire capable d'OUVRIR un trade : le stop d'un long est un Sell, et sur le
            // short qui a suivi il ajoute à la position au lieu d'en sortir. Le cas arrive dès
            // qu'on retourne au marché sans passer par la touche Inverser, qui annule d'abord.
            // Seuls nos propres ordres sont annulés — rien de ce que le trader a posé lui-même.
            CancelStaleBracketOrders(ctx.Account, ctx.Instrument, position.MarketPosition, cmd.RequestId);

            var existingStops = FindExitOrders(ctx.Account, ctx.Instrument, position.MarketPosition, true);
            var existingTargets = FindExitOrders(ctx.Account, ctx.Instrument, position.MarketPosition, false);
            var ourStop = FindOrderNamed(existingStops, BracketStopName);
            var ourTarget = FindOrderNamed(existingTargets, BracketTargetName);

            // Reuse the OCO group of a leg this macro already owns, so the pair survives a
            // scale-in: Account.Change cannot rewrite an OCO id, and creating the second leg in a
            // group of its own would leave the two unlinked. A fresh group when we own neither.
            string oco;
            if (ourStop != null && !string.IsNullOrEmpty(ourStop.Oco)) oco = ourStop.Oco;
            else if (ourTarget != null && !string.IsNullOrEmpty(ourTarget.Oco)) oco = ourTarget.Oco;
            else oco = Guid.NewGuid().ToString("N");

            var created = new List<Order>();
            var changed = new List<Order>();
            string stopOutcome = "disabled";
            string targetOutcome = "disabled";
            int placed = 0;
            int refused = 0;

            try
            {
                if (stopTicks > 0)
                {
                    // A stop for a long sits BELOW the market, a stop for a short ABOVE it. Past
                    // that line the order is marketable: it would not protect the trade, it would
                    // close it on the spot at whatever price is there. Refusing the leg and saying
                    // so beats closing a position nobody asked to close.
                    bool wrongSide = marketPrice > 0 && (isLong ? stopPrice >= marketPrice : stopPrice <= marketPrice);
                    if (wrongSide)
                    {
                        stopOutcome = "refused:pastMarket";
                        refused++;
                        SdLogger.EventWarn("Bracket",
                            "[REQ:{0}] Stop loss NOT placed on {1}: {2} is already past the market ({3}) — the trade is beyond its stop",
                            cmd.RequestId, ctx.Instrument.FullName, stopPrice, marketPrice);
                    }
                    else if (ourStop != null)
                    {
                        ourStop.StopPriceChanged = stopPrice;
                        // Set explicitly rather than left alone: a scale-in grew the position, and
                        // a stop still sized for the first entry protects only part of it.
                        ourStop.QuantityChanged = qty;
                        if (ourStop.OrderType == OrderType.StopLimit)
                        {
                            // Keep the original distance between trigger and limit, as break-even does.
                            double limitOffset = ourStop.LimitPrice - ourStop.StopPrice;
                            ourStop.LimitPriceChanged = stopPrice + limitOffset;
                        }
                        changed.Add(ourStop);
                        stopOutcome = "modified";
                        placed++;
                    }
                    else if (existingStops.Count > 0)
                    {
                        // Someone else already protects this position — an ATM strategy, a manual
                        // stop, a break-even. That it is protected is the whole point: adding a
                        // second stop would exit twice the size and open the reverse trade.
                        stopOutcome = "kept:foreign";
                    }
                    else
                    {
                        created.Add(ctx.Account.CreateOrder(
                            ctx.Instrument, exitAction, OrderType.StopMarket, TimeInForce.Day,
                            qty, 0, stopPrice, oco, BracketStopName, null));
                        stopOutcome = "created";
                        placed++;
                    }
                }

                if (targetTicks > 0)
                {
                    // Mirror of the stop test. A take profit the market has already crossed is a
                    // marketable limit: submitting it would close the position immediately, which
                    // is exactly the surprise this macro must never produce.
                    bool wrongSide = marketPrice > 0 && (isLong ? targetPrice <= marketPrice : targetPrice >= marketPrice);
                    if (wrongSide)
                    {
                        targetOutcome = "refused:pastMarket";
                        refused++;
                        SdLogger.EventWarn("Bracket",
                            "[REQ:{0}] Take profit NOT placed on {1}: {2} is already past the market ({3}) — it would fill at once",
                            cmd.RequestId, ctx.Instrument.FullName, targetPrice, marketPrice);
                    }
                    else if (ourTarget != null)
                    {
                        ourTarget.LimitPriceChanged = targetPrice;
                        ourTarget.QuantityChanged = qty;
                        changed.Add(ourTarget);
                        targetOutcome = "modified";
                        placed++;
                    }
                    else if (existingTargets.Count > 0)
                    {
                        targetOutcome = "kept:foreign";
                    }
                    else
                    {
                        created.Add(ctx.Account.CreateOrder(
                            ctx.Instrument, exitAction, OrderType.Limit, TimeInForce.Day,
                            qty, targetPrice, 0, oco, BracketTargetName, null));
                        targetOutcome = "created";
                        placed++;
                    }
                }

                if (changed.Count > 0) ctx.Account.Change(changed);
                // Submitted in ONE call so NinjaTrader forms the OCO group from the batch. Two
                // separate submits would race the fill of the first leg.
                if (created.Count > 0) ctx.Account.Submit(created);
            }
            catch (Exception ex)
            {
                SdLogger.Fail("Bracket", ex, "[REQ:{0}] Could not attach the bracket on {1}",
                    cmd.RequestId, ctx.Instrument.FullName);
                return BridgeMessage.CreateError(cmd.RequestId, cmd.Action, "ORDER_REJECTED", ex.Message);
            }

            // Nothing placed and the market is the reason: a real refusal, and the deck must show
            // it. A leg left to a protection the trader already had is NOT a failure — the position
            // is covered, which is all the macro was ever asked for.
            if (placed == 0 && refused > 0)
            {
                return BridgeMessage.CreateError(cmd.RequestId, cmd.Action, "INVALID_STOP_PRICE",
                    $"Bracket not placed on {ctx.Instrument.FullName}: the market ({marketPrice}) is already past "
                    + $"the levels computed from the average price ({avgPrice}).");
            }

            SdLogger.Event("Bracket",
                "[REQ:{0}] Bracket on {1} {2} x{3} @ {4} — stop={5} ({6}), target={7} ({8}), oco={9}",
                cmd.RequestId, ctx.Instrument.FullName, position.MarketPosition, qty, avgPrice,
                stopTicks > 0 ? stopPrice.ToString() : "-", stopOutcome,
                targetTicks > 0 ? targetPrice.ToString() : "-", targetOutcome,
                oco);

            // Submit is asynchronous: a rejection (margin, closed market) arrives later through
            // OrderMonitor. This reply says the orders left, not that they were accepted.
            return BridgeMessage.CreateResponse(cmd.RequestId, cmd.Action, true, new
            {
                avgPrice,
                quantity = qty,
                direction = position.MarketPosition.ToString(),
                stopPrice = stopTicks > 0 ? stopPrice : 0,
                targetPrice = targetTicks > 0 ? targetPrice : 0,
                stopLossTicks = stopTicks,
                takeProfitTicks = targetTicks,
                stopOutcome,
                targetOutcome,
                legsPlaced = placed,
                oco,
                message = $"Bracket on {ctx.Instrument.FullName}: stop {stopOutcome}, target {targetOutcome}"
            });
        }

        /// <summary>
        /// Working orders of the given type that would CLOSE the position — entries excluded.
        ///
        /// The direction test is not decoration. <see cref="ContextResolver.FindTargetOrders"/>
        /// returns every working limit order on the instrument, and a resting buy limit under a
        /// long position is an ENTRY, not a target. Counting it as one would convince the macro
        /// that the trade already had a take profit, and leave it with none.
        /// </summary>
        private List<Order> FindExitOrders(Account account, Instrument instrument, MarketPosition side, bool stops)
        {
            var candidates = stops
                ? _resolver.FindStopOrders(account, instrument)
                : _resolver.FindTargetOrders(account, instrument);

            var exits = new List<Order>();
            foreach (var order in candidates)
            {
                bool closes = side == MarketPosition.Long
                    ? order.OrderAction == OrderAction.Sell || order.OrderAction == OrderAction.SellShort
                    : order.OrderAction == OrderAction.Buy || order.OrderAction == OrderAction.BuyToCover;
                if (closes) exits.Add(order);
            }
            return exits;
        }

        /// <summary>
        /// Cancels the bracket legs THIS macro left behind that would now grow the position instead
        /// of closing it — what remains of a bracket after the position flipped side.
        ///
        /// Scoped to our own two order names on purpose. The macro is allowed to clean up after
        /// itself; it is never allowed to cancel a protection the trader placed, and a failure here
        /// must not stop the new bracket from going out — an orphan order is bad, an unprotected
        /// position is worse.
        /// </summary>
        private void CancelStaleBracketOrders(Account account, Instrument instrument, MarketPosition side, string requestId)
        {
            try
            {
                var stale = new List<Order>();
                foreach (var order in _resolver.FindActiveOrders(account, instrument))
                {
                    bool ours = string.Equals(order.Name, BracketStopName, StringComparison.OrdinalIgnoreCase)
                             || string.Equals(order.Name, BracketTargetName, StringComparison.OrdinalIgnoreCase);
                    if (!ours) continue;

                    bool grows = side == MarketPosition.Long
                        ? order.OrderAction == OrderAction.Buy
                        : order.OrderAction == OrderAction.SellShort || order.OrderAction == OrderAction.Sell;
                    if (grows) stale.Add(order);
                }

                if (stale.Count == 0) return;

                account.Cancel(stale);
                SdLogger.EventWarn("Bracket",
                    "[REQ:{0}] {1} stale bracket leg(s) cancelled on {2}: they were left by a position of the opposite side and would have ADDED to the current {3}",
                    requestId, stale.Count, instrument.FullName, side);
            }
            catch (Exception ex)
            {
                SdLogger.Fail("Bracket", ex, "[REQ:{0}] Could not cancel the stale bracket legs on {1}",
                    requestId, instrument.FullName);
            }
        }

        private static Order FindOrderNamed(List<Order> orders, string name)
        {
            foreach (var order in orders)
            {
                if (string.Equals(order.Name, name, StringComparison.OrdinalIgnoreCase)) return order;
            }
            return null;
        }

        private static double RoundToTick(double price, double tickSize)
        {
            return Math.Round(price / tickSize) * tickSize;
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
