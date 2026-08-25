using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

using ATAS.DataFeedsCore;
using ATAS.Indicators;
using Newtonsoft.Json.Linq;

namespace TradeDeck.Atas.Strategy;

/// <summary>
/// The three order verbs of ChartStrategy, plus the two classifications only ITradingManager can
/// make, behind one seam.
///
/// An interface rather than four delegates because these five members always come from the same
/// place — the running strategy — and because it keeps OrderEngine, GuardEnforcer and
/// ExecutionRecorder from each having to know how to reach it.
///
/// PUBLIC, NOT INTERNAL. TradeDeckStrategy is a public class (ATAS loads it by reflection); a
/// public class cannot implement an internal interface, so this one seam has to be public even
/// though everything else in this file stays internal.
/// </summary>
public interface IOrderGateway
{
    /// <summary>ChartStrategy.OpenOrder — sends the order, never confirms it was accepted.</summary>
    void Open(Order order);

    void Cancel(Order order);

    /// <summary>ChartStrategy.ModifyOrder — the SECOND argument is the repriced clone.</summary>
    void Modify(Order oldOrder, Order newOrder);

    bool IsStopLossOrder(Order order);
    bool IsTakeProfitOrder(Order order);
}

/// <summary>
/// Builds and submits the orders the deck asks for.
///
/// THE DECK DECIDES, NOT THE CHART. Portfolio and Security are set explicitly on every order from
/// what the deck selected. ITradingManager's own SetStopLoss / SetTakeProfit / SetBreakeven are
/// deliberately NOT used, despite the code they would save: they act on the CHART's selection, so
/// the moment the deck points at an instrument the chart does not display they would send the
/// break-even to the wrong position.
/// </summary>
internal sealed class OrderEngine
{
    /// <summary>
    /// The comment every order this deck submits carries. ATAS's Order has no Name — this is the
    /// closest equivalent, and it keeps the same vocabulary the server already reads in the
    /// journal ("StreamDeck", "StreamDeck_SL", …).
    /// </summary>
    public const string DeckOrderComment = "StreamDeck";

    private readonly ContextResolver _resolver;
    private readonly IOrderGateway _gateway;

    /// <summary>
    /// Orders this engine built and handed to IOrderGateway THIS SESSION.
    ///
    /// REVIEW ROUND 1, FINDING I3: this doc used to claim the table is "what the guard trusts
    /// first" against "a one-word bypass" — that overstates what the code actually does, and
    /// task 8's GuardEnforcer will read this sentence to decide how much to trust IsOurs. The
    /// honest version: this table is AUTHORITATIVE only for the exact Order instances this engine
    /// itself created — it says nothing about a manual DOM ticket. The Comment fallback in IsOurs
    /// below is best-effort RECOVERY for the case ATAS hands back a DIFFERENT Order instance for
    /// the same order (observed to happen), not a security boundary: Comment is a free string, and
    /// a trader who types "StreamDeck" into a manual order's comment field WILL be classified as a
    /// deck order by IsOurs — there is no defence against that in this class. One mitigating,
    /// measured fact: a real manual ChartTrader order was observed with Comment empty, so the false
    /// positive needs a trader to type the exact word; it does not happen by default.
    ///
    /// A ConditionalWeakTable so a forgotten order cannot leak: entries vanish with the order.
    /// </summary>
    private readonly ConditionalWeakTable<Order, object> _ours = new();

    public OrderEngine(ContextResolver resolver, IOrderGateway gateway)
    {
        _resolver = resolver;
        _gateway = gateway;
    }

    public bool IsOurs(Order? order)
    {
        if (order == null) return false;
        if (_ours.TryGetValue(order, out _)) return true;

        var comment = order.Comment;
        return !string.IsNullOrEmpty(comment) &&
               comment.StartsWith(DeckOrderComment, StringComparison.OrdinalIgnoreCase);
    }

    public BridgeMessage BuyMarket(BridgeMessage cmd) => SubmitMarket(cmd, OrderDirections.Buy);
    public BridgeMessage SellMarket(BridgeMessage cmd) => SubmitMarket(cmd, OrderDirections.Sell);

    private BridgeMessage SubmitMarket(BridgeMessage cmd, OrderDirections direction)
    {
        var ctx = Resolve(cmd);
        if (ctx.Error != null) return ctx.Error;

        var quantity = cmd.GetInt("quantity") ?? 1;
        if (quantity < 1)
            return BridgeMessage.Failure(cmd.RequestId, cmd.Action, "INVALID_QUANTITY", "Quantity must be >= 1");

        var order = NewOrder(ctx, direction, OrderTypes.Market, quantity, DeckOrderComment);

        var failure = Submit(cmd, order);
        if (failure != null) return failure;

        SdLog.Event("Order", "[REQ:{0}] {1} {2} {3} Market submitted on {4}",
            cmd.RequestId, direction, quantity, ContextResolver.InstrumentName(ctx.Security), ctx.Portfolio!.AccountID);

        // Submission is asynchronous, exactly as Account.Submit was: a rejection (margin, closed
        // market) arrives later through OrderMonitor. This reply says the order LEFT, never that it
        // was accepted.
        return BridgeMessage.Response(cmd.RequestId, cmd.Action, true, new
        {
            orderId = order.Id ?? string.Empty,
            message = $"{direction} {quantity} {ContextResolver.InstrumentName(ctx.Security)} Market submitted",
        });
    }

    public BridgeMessage BuyLimit(BridgeMessage cmd) => SubmitLimit(cmd, OrderDirections.Buy);
    public BridgeMessage SellLimit(BridgeMessage cmd) => SubmitLimit(cmd, OrderDirections.Sell);

    private BridgeMessage SubmitLimit(BridgeMessage cmd, OrderDirections direction)
    {
        var ctx = Resolve(cmd);
        if (ctx.Error != null) return ctx.Error;

        var quantity = cmd.GetInt("quantity") ?? 1;
        var offsetTicks = cmd.GetInt("offsetTicks") ?? 0;

        if (quantity < 1)
            return BridgeMessage.Failure(cmd.RequestId, cmd.Action, "INVALID_QUANTITY", "Quantity must be >= 1");

        var name = ContextResolver.InstrumentName(ctx.Security);

        var tickSize = ctx.Security!.TickSize;
        if (tickSize <= 0m)
        {
            return BridgeMessage.Failure(cmd.RequestId, cmd.Action, "NO_MARKET_DATA",
                $"Invalid tick size for {name} — cannot compute a limit price.");
        }

        // ContextResolver.OrderPrice, NOT MarketPrice: OrderPrice is last-trade-first, matching
        // NinjaTrader's TradingEngine.GetLastPrice digit for digit (REVIEW ROUND 1, FINDING I1) —
        // MarketPrice's (bid+ask)/2 is the state block's display price and lands off the tick grid
        // whenever the spread is an odd number of ticks, the ordinary case on ES/NQ. See both
        // methods' own notes for why they must stay two different functions.
        //
        // WITHOUT A FEED THIS IS 0, AND 0 IS NOT A PRICE. A limit computed from it would land a few
        // ticks from zero: a sell limit there fills instantly AT MARKET, which is the opposite of
        // what the trader asked for. The order is refused instead of sent.
        //
        // REVIEW ROUND 1, FINDING I3 — WHAT THIS GUARD DOES NOT COVER: it catches ABSENT data
        // (<= 0), not STALE data. A Security still listed over a weekend or on a halted contract
        // can keep last Friday's last-trade/bid indefinitely, and that price is > 0 — present,
        // just meaningless — so it passes this check. Whether ATAS actually zeroes
        // LastTradePrice/BestBidPrice on unsubscribe (making the stale case routine) or keeps the
        // last seen value forever (making it the common case on a closed market) is unmeasured
        // from here; inventing a staleness heuristic without that measurement risks refusing
        // legitimate orders on a real, if quiet, price. Left as is on purpose — carried to the
        // next live session as a check on a closed market, not fixed blind.
        var market = ContextResolver.OrderPrice(ctx.Security);
        if (market <= 0m)
        {
            return BridgeMessage.Failure(cmd.RequestId, cmd.Action, "NO_MARKET_DATA",
                $"No market data for {name} — cannot compute a limit price. Open a chart or a DOM for this instrument.");
        }

        var limitPrice = RoundToTick(market + offsetTicks * tickSize, tickSize);
        if (limitPrice <= 0m)
        {
            return BridgeMessage.Failure(cmd.RequestId, cmd.Action, "NO_MARKET_DATA",
                $"Computed limit price {limitPrice} is not valid for {name}.");
        }

        var order = NewOrder(ctx, direction, OrderTypes.Limit, quantity, DeckOrderComment);
        order.Price = limitPrice;

        var failure = Submit(cmd, order);
        if (failure != null) return failure;

        SdLog.Event("Order", "[REQ:{0}] {1} {2} {3} Limit @ {4} submitted on {5} (market={6}, offset={7} ticks)",
            cmd.RequestId, direction, quantity, name, limitPrice, ctx.Portfolio!.AccountID, market, offsetTicks);

        return BridgeMessage.Response(cmd.RequestId, cmd.Action, true, new
        {
            orderId = order.Id ?? string.Empty,
            limitPrice,
            message = $"{direction} {quantity} {name} Limit @ {limitPrice} submitted",
        });
    }

    /// <summary>
    /// Closes the position on the selected instrument with an opposite market order.
    ///
    /// An explicit order rather than Connector.ClosePositionAsync, and for the same reason the
    /// trading manager's SetStopLoss is avoided: what the deck aims at must never depend on what a
    /// helper decides to look at. The order carries the deck's portfolio and security, and its size
    /// is the position's own — nothing is inferred.
    ///
    /// Idempotent by design: flat means success with nothing sent. A trader pressing Flatten twice
    /// must not open a short.
    /// </summary>
    public BridgeMessage Flatten(BridgeMessage cmd)
    {
        var ctx = Resolve(cmd);
        if (ctx.Error != null) return ctx.Error;
        return FlattenPosition(cmd, ctx);
    }

    /// <summary>
    /// Flatten's actual work, taking an already-resolved context. Split out so CancelOrders
    /// (REVIEW ROUND 1, FINDING minor 2) can reuse the SAME resolution it already has instead of
    /// calling Resolve a second time — a second Resolve that failed used to report
    /// INSTRUMENT_NOT_FOUND to the deck AFTER the orders had already been cancelled: a red key
    /// reading "nothing happened" over a state that had, in fact, changed.
    /// </summary>
    private BridgeMessage FlattenPosition(BridgeMessage cmd, TradingContext ctx)
    {
        var name = ContextResolver.InstrumentName(ctx.Security);
        var position = _resolver.FindPosition(ctx.Portfolio, ctx.Security);

        if (position == null)
        {
            return BridgeMessage.Response(cmd.RequestId, cmd.Action, true, new
            {
                positionsFlattened = 0,
                message = $"No open position for {name}",
            });
        }

        // REVIEW ROUND 1, FINDING minor 4 — STALE SIZE, READ ONCE: this size is read here, before
        // any order goes out. A resting entry that fills between this read and Submit below
        // changes the real position without this method noticing, and the market order already
        // sized would then close the WRONG quantity. Inherent to a fire-and-forget submission path
        // with no synchronous wait available on this path (see Reverse's own note below, which
        // carries the sharper version of this same risk).
        var quantity = Math.Abs(position.Volume);
        var direction = position.Volume > 0m ? OrderDirections.Sell : OrderDirections.Buy;

        var order = NewOrder(ctx, direction, OrderTypes.Market, quantity, DeckOrderComment + "_Flatten");
        var failure = Submit(cmd, order);
        if (failure != null) return failure;

        SdLog.Event("Position", "[REQ:{0}] Flatten {1} on {2} — {3} {4}",
            cmd.RequestId, name, ctx.Portfolio!.AccountID, direction, quantity);

        return BridgeMessage.Response(cmd.RequestId, cmd.Action, true, new
        {
            positionsFlattened = 1,
            message = $"Flatten {name} submitted",
        });
    }

    /// <summary>
    /// Closes EVERY position on the account and cancels every working order on it.
    ///
    /// Distinct from Flatten, which only touches the selected instrument. This one backs the
    /// automatic liquidation on daily loss, and there the ACCOUNT is the unit that matters: the
    /// limit is computed on account P&amp;L, so announcing "the day is over" while a position stays
    /// open on an instrument the trader was not watching would be worse than doing nothing — he
    /// would believe he was flat.
    ///
    /// ORDERS ARE CANCELLED FIRST, and account-wide, including on instruments carrying no position:
    /// a resting entry that fills afterwards would reopen exactly the exposure just closed.
    ///
    /// Never reachable from a key press. Its only caller is the safety macro, via the bridge.
    /// </summary>
    public BridgeMessage FlattenAccount(BridgeMessage cmd)
    {
        var accountName = cmd.GetString("account");
        if (string.IsNullOrWhiteSpace(accountName))
            return BridgeMessage.Failure(cmd.RequestId, cmd.Action, "CONTEXT_MISSING", "Account name is required.");

        var portfolio = _resolver.FindPortfolio(accountName);
        if (portfolio == null)
            return BridgeMessage.Failure(cmd.RequestId, cmd.Action, "ACCOUNT_NOT_FOUND", $"Account '{accountName}' not found.");

        // REVIEW ROUND 1, FINDING C1 — BEST EFFORT, NOT ATOMIC. NinjaTrader liquidated an account
        // with two atomic broker calls, Account.Cancel(orders) then Account.Flatten(instruments):
        // no single sick order could disable the rest of the liquidation. ATAS's strategy API has
        // no equivalent, and the original port turned that into a loop that ABORTED on the first
        // throw — one stale order failing to cancel meant zero positions were even attempted. This
        // command's only caller is the daily-loss safety macro, i.e. the moment things are already
        // going wrong: a trader relying on it to end the day flat must see everything that COULD
        // be closed actually get closed, with the failures reported alongside, not a red key that
        // stopped after the first problem while the rest of the account kept bleeding. Every order
        // and every position is therefore attempted independently; failures are collected, not
        // fatal to the ones that come after.
        var orders = _resolver.FindAllActiveOrders(portfolio);
        var ordersCancelled = 0;
        var errors = new List<string>();
        foreach (var order in orders)
        {
            if (!ContextResolver.IsActive(order)) continue;
            try
            {
                _gateway.Cancel(order);
                ordersCancelled++;
            }
            catch (Exception ex)
            {
                SdLog.Fail("Safety", ex, "[REQ:{0}] ACCOUNT LIQUIDATION: failed to cancel one order on {1}",
                    cmd.RequestId, accountName);
                errors.Add($"cancel failed: {ex.Message}");
            }
        }

        var flattened = 0;
        // FindAllPositions, NOT a raw connector: the portfolio can live on any connector the registry
        // aggregates (task 3), and in degraded mode this call transparently falls back to the
        // chart's own TradingManager.Position — no branch needed here for that case.
        var positions = _resolver.FindAllPositions(portfolio);
        foreach (var position in positions)
        {
            if (position == null || position.Volume == 0m) continue;
            if (position.Security == null) continue;

            var ctx = new TradingContext { Portfolio = portfolio, Security = position.Security };
            var direction = position.Volume > 0m ? OrderDirections.Sell : OrderDirections.Buy;
            var order = NewOrder(ctx, direction, OrderTypes.Market, Math.Abs(position.Volume),
                                 DeckOrderComment + "_Liquidate");

            try
            {
                _gateway.Open(order);
                flattened++;
            }
            catch (Exception ex)
            {
                // The worst outcome this whole feature can produce: the trader is told the day
                // is closed while a position is still live. Logged and collected, never silently
                // swallowed — but NOT returned immediately: the remaining positions still get their
                // own attempt, per the best-effort ruling above.
                SdLog.Fail("Safety", ex, "[REQ:{0}] ACCOUNT LIQUIDATION FAILED on {1} for {2}",
                    cmd.RequestId, accountName, ContextResolver.InstrumentName(position.Security));
                errors.Add($"{ContextResolver.InstrumentName(position.Security)}: {ex.Message}");
            }
        }

        if (errors.Count > 0)
        {
            SdLog.Fail("Safety", null,
                "[REQ:{0}] ACCOUNT LIQUIDATION INCOMPLETE on {1} — {2}/{3} order(s) cancelled, {4} position(s) flattened, {5} error(s)",
                cmd.RequestId, accountName, ordersCancelled, orders.Count, flattened, errors.Count);

            // Honest refusal, not a plain failure: the deck and the log must agree on what
            // actually happened, so the real counts travel alongside the error rather than being
            // discarded the way a bare Failure(...) would discard them.
            return BridgeMessage.Failure(cmd.RequestId, cmd.Action, "ORDER_REJECTED",
                $"Account {accountName} liquidation incomplete: {string.Join("; ", errors)}",
                new { ordersCancelled, positionsFlattened = flattened });
        }

        SdLog.EventWarn("Safety", "[REQ:{0}] ACCOUNT LIQUIDATION on {1} — {2} order(s) cancelled, {3} position(s) flattened",
            cmd.RequestId, accountName, ordersCancelled, flattened);

        return BridgeMessage.Response(cmd.RequestId, cmd.Action, true, new
        {
            ordersCancelled,
            positionsFlattened = flattened,
            message = $"Account {accountName} liquidated: {flattened} position(s), {ordersCancelled} order(s)",
        });
    }

    /// <summary>
    /// Cancels the working orders of the instrument WITHOUT touching the position.
    /// Idempotent: success with ordersCancelled = 0 when there is nothing to cancel.
    /// </summary>
    public BridgeMessage CancelWorkingOrders(BridgeMessage cmd)
    {
        var ctx = Resolve(cmd);
        if (ctx.Error != null) return ctx.Error;

        var name = ContextResolver.InstrumentName(ctx.Security);
        var orders = _resolver.FindActiveOrders(ctx.Portfolio, ctx.Security);

        if (orders.Count == 0)
        {
            return BridgeMessage.Response(cmd.RequestId, cmd.Action, true, new
            {
                ordersCancelled = 0,
                message = $"No working order for {name}",
            });
        }

        var failure = Cancel(cmd, orders);
        if (failure != null) return failure;

        SdLog.Event("Order", "[REQ:{0}] Cancelled {1} working order(s) for {2} on {3} (position untouched)",
            cmd.RequestId, orders.Count, name, ctx.Portfolio!.AccountID);

        return BridgeMessage.Response(cmd.RequestId, cmd.Action, true, new
        {
            ordersCancelled = orders.Count,
            message = $"Cancelled {orders.Count} order(s) for {name}",
        });
    }

    /// <summary>
    /// "Close All": cancels every pending order AND closes the position. Kept deliberately
    /// destructive — this backs the Close All key.
    ///
    /// Cancel first, then flatten. The other order would leave a resting entry able to reopen the
    /// position the flatten had just closed.
    /// </summary>
    public BridgeMessage CancelOrders(BridgeMessage cmd)
    {
        var ctx = Resolve(cmd);
        if (ctx.Error != null) return ctx.Error;

        var name = ContextResolver.InstrumentName(ctx.Security);
        var orders = _resolver.FindActiveOrders(ctx.Portfolio, ctx.Security);

        if (orders.Count > 0)
        {
            var cancelFailure = Cancel(cmd, orders);
            if (cancelFailure != null) return cancelFailure;
        }

        // REVIEW ROUND 1, FINDING I2 — WHY NO WAIT HERE: Cancel is fire-and-forget, and a
        // synchronous wait is forbidden on this path (no blocking a connector event handler,
        // shared state under a lock, sends fire-and-forget — see the project constraints). Between
        // the cancel above and the flatten below, a resting ENTRY order that was already working
        // can still fill: the flatten then closes the position that fill just opened, and the
        // position this key leaves behind is whatever that fill added, not flat. NinjaTrader closed
        // this window with one atomic Account.Flatten call; ATAS's strategy API has no equivalent.
        // Whether this is a routine race or a theoretical one is unmeasured from here — carried
        // forward as a known residual risk, not silently fixed with a wait this path cannot afford.
        //
        // REVIEW ROUND 1, FINDING minor 2 — FlattenPosition(cmd, ctx), NOT Flatten(cmd): reuses the
        // SAME resolved context instead of resolving a second time. A second Resolve that failed
        // used to report INSTRUMENT_NOT_FOUND to the deck after the orders above had already been
        // cancelled — a red key claiming nothing happened over a state that had, in fact, changed.
        var flatten = FlattenPosition(cmd, ctx);
        if (flatten.Error != null) return flatten;

        // REVIEW ROUND 1, FINDING minor 1: the flatten above can legitimately find nothing to
        // close (success, positionsFlattened: 0) when Close All is pressed with only working
        // orders and no position — that used to be reported as "position closed" regardless. Read
        // the real count back out instead of assuming it matched what was requested.
        var positionsClosed = flatten.Result?["positionsFlattened"]?.Value<int>() ?? 0;

        SdLog.Event("Position", "[REQ:{0}] Close All on {1} — {2} order(s) cancelled, {3} position(s) closed",
            cmd.RequestId, name, orders.Count, positionsClosed);

        return BridgeMessage.Response(cmd.RequestId, cmd.Action, true, new
        {
            ordersCancelled = orders.Count,
            positionsFlattened = positionsClosed,
            message = positionsClosed > 0
                ? $"Cancelled all orders and closed position for {name}"
                : $"Cancelled {orders.Count} order(s) for {name} — no open position to close",
        });
    }

    /// <summary>
    /// Reverses the position: cancel its working orders, then ONE market order of twice the size.
    ///
    /// REVIEW ROUND 1, FINDING minor 3: "cancel the protections" undersold what actually happens
    /// here — FindActiveOrders is origin-blind by design (see its own doc), so this cancels EVERY
    /// working order on the pair, including a manual ChartTrader entry sitting on the same
    /// instrument, not just the stop/target this engine itself attached. That is the safer choice
    /// and stays as is; the point of this note is that the comment used to claim less than the
    /// code does, in front of a guard, which is exactly backwards.
    ///
    /// The reason to cancel first at all: a stop from the old long is a SELL, and after the
    /// reversal it would ADD to the new short instead of protecting it.
    ///
    /// One order of 2× rather than a close followed by an open: two orders leave a window in which
    /// the account is flat and the second one has not landed, and a partial fill in that window
    /// leaves a size nobody chose.
    /// </summary>
    public BridgeMessage Reverse(BridgeMessage cmd)
    {
        var ctx = Resolve(cmd);
        if (ctx.Error != null) return ctx.Error;

        var position = _resolver.FindPosition(ctx.Portfolio, ctx.Security);
        if (position == null)
            return BridgeMessage.Failure(cmd.RequestId, cmd.Action, "NO_POSITION", "No open position to reverse.");

        // REVIEW ROUND 1, FINDING minor 4 — STALE SIZE, READ ONCE, WORST CASE OF ALL FIVE VERBS:
        // this size is read here, before either order below goes out. A resting entry that fills
        // in the gap between this read and the reversal order changes the real position without
        // this method noticing — a long 2 that becomes a long 3 from a fill in that window still
        // submits a SELL 4 sized for a long 2, leaving a LONG 1 behind, not the short the trader
        // asked for. Reverse is the most destructive verb in this set — it doubles a position
        // rather than merely closing one — so a stale size here matters more than the same read in
        // Flatten above. Inherent to a fire-and-forget submission path with no synchronous wait
        // available (see the cancel-then-reverse note below); not something this method can close
        // on its own.
        var currentQuantity = Math.Abs(position.Volume);
        var wasLong = position.Volume > 0m;

        var stale = _resolver.FindActiveOrders(ctx.Portfolio, ctx.Security);
        if (stale.Count > 0)
        {
            var cancelFailure = Cancel(cmd, stale);
            if (cancelFailure != null) return cancelFailure;
            SdLog.Event("Order", "[REQ:{0}] Reverse: cancelled {1} working order(s) before reversing",
                cmd.RequestId, stale.Count);
        }

        // REVIEW ROUND 1, FINDING I2 — WHY NO WAIT HERE: same fire-and-forget Cancel as
        // CancelOrders, same reason a synchronous wait is not available on this path. An
        // un-acknowledged STOP from the position being reversed — a sell stop protecting the old
        // long — can still trigger after the reversal order below lands: it would ADD to the new
        // short instead of protecting it, doubling the short instead of the position it was meant
        // to guard. NinjaTrader closed this window with one atomic Account.Flatten/Change; ATAS's
        // strategy API has no equivalent, and this project has decided not to fake one with a
        // blocking wait. Carried forward as a known residual risk — neither live check can catch
        // it, both need a fast market and a slow cancel ack against a quiet Sim book.
        var direction = wasLong ? OrderDirections.Sell : OrderDirections.Buy;
        var order = NewOrder(ctx, direction, OrderTypes.Market, currentQuantity * 2m, DeckOrderComment + "_Reverse");

        var failure = Submit(cmd, order);
        if (failure != null) return failure;

        SdLog.Event("Position", "[REQ:{0}] Reverse {1} {2} → {3} {4}",
            cmd.RequestId, wasLong ? "Long" : "Short", currentQuantity, direction, currentQuantity * 2m);

        return BridgeMessage.Response(cmd.RequestId, cmd.Action, true, new
        {
            ordersCancelled = stale.Count,
            message = stale.Count > 0
                ? $"Reverse from {(wasLong ? "Long" : "Short")} {currentQuantity} → {direction} {currentQuantity * 2m} ({stale.Count} order(s) cancelled)"
                : $"Reverse from {(wasLong ? "Long" : "Short")} {currentQuantity} → {direction} {currentQuantity * 2m}",
        });
    }

    /// <summary>
    /// Builds an order carrying the DECK's portfolio and security, never the chart's.
    /// </summary>
    public Order NewOrder(TradingContext ctx, OrderDirections direction, OrderTypes type,
                          decimal quantity, string comment)
    {
        var order = new Order
        {
            Portfolio = ctx.Portfolio,
            Security = ctx.Security,
            Direction = direction,
            Type = type,
            QuantityToFill = quantity,
            Comment = comment,
            // Day, like the NinjaTrader add-on: a forgotten GTC entry could fill unattended in a
            // later session, on a market the trader is not watching.
            TimeInForce = TimeInForce.Day,
        };

        _ours.Add(order, this);
        return order;
    }

    /// <summary>
    /// Submits, or returns the refusal to send back to the deck.
    ///
    /// Goes through ChartStrategy.OpenOrder — the documented strategy API, which takes the order and
    /// nothing else. ITradingManager also carries an OpenOrder with four booleans
    /// (setDefaultQuantity, askConfirmation, checkOrderStates) and the design brief reached for it;
    /// it is NOT what the official documentation nor the SmaChartStrategy example use, and taking
    /// the documented path means there is no confirmation dialog to reason about at all. A modal
    /// window opening in the middle of a key press would turn the cockpit into a trap.
    ///
    /// "The OpenOrder method does not guarantee that an order will be placed, it only sends the
    /// order to the exchange" — the documentation's own words, and the reason OrderMonitor exists.
    /// </summary>
    public BridgeMessage? Submit(BridgeMessage cmd, Order order)
    {
        try
        {
            _gateway.Open(order);
            return null;
        }
        catch (Exception ex)
        {
            SdLog.Fail("Order", ex, "[REQ:{0}] Failed to submit {1} {2}",
                cmd.RequestId, order.Direction, order.Type);
            return BridgeMessage.Failure(cmd.RequestId, cmd.Action, "ORDER_REJECTED", ex.Message);
        }
    }

    public BridgeMessage? Cancel(BridgeMessage cmd, IReadOnlyList<Order> orders)
    {
        try
        {
            foreach (var order in orders)
            {
                // The documentation asks for this check before cancelling: an order that already
                // reached a terminal state only produces a "cancel rejected" that means nothing.
                if (!ContextResolver.IsActive(order)) continue;
                _gateway.Cancel(order);
            }
            return null;
        }
        catch (Exception ex)
        {
            SdLog.Fail("Order", ex, "[REQ:{0}] Failed to cancel {1} order(s)", cmd.RequestId, orders.Count);
            return BridgeMessage.Failure(cmd.RequestId, cmd.Action, "ORDER_REJECTED", ex.Message);
        }
    }

    /// <summary>
    /// Replaces an order with a repriced clone.
    ///
    /// ATAS modifies by handing over a NEW order, where NinjaTrader mutated the existing one and
    /// called Account.Change. Cloning keeps every field we are not changing — quantity, OCO group,
    /// time in force — instead of rebuilding them and getting one wrong.
    /// </summary>
    public BridgeMessage? Modify(BridgeMessage cmd, Order order, Action<Order> reprice)
    {
        try
        {
            var updated = order.Clone();
            reprice(updated);
            _ours.AddOrUpdate(updated, this);
            _gateway.Modify(order, updated);
            return null;
        }
        catch (Exception ex)
        {
            SdLog.Fail("Order", ex, "[REQ:{0}] Failed to modify order {1}", cmd.RequestId, order.Id ?? "?");
            return BridgeMessage.Failure(cmd.RequestId, cmd.Action, "ORDER_REJECTED", ex.Message);
        }
    }

    /// <summary>
    /// Rounds to a whole number of ticks — a price off the grid is refused by the venue.
    ///
    /// REVIEW ROUND 1, FINDING I1: MidpointRounding.ToEven (the decimal Math.Round default, so
    /// left unspecified), not AwayFromZero. NinjaTrader's TradingEngine rounds every price the
    /// same way — Math.Round(p / tick) * tick, no explicit mode, e.g. TradingEngine.cs:115 (limit
    /// price), :831, :878, :957 (stop/target moves) — and matching it here is what keeps a price
    /// computed from identical inputs landing on the identical tick on both platforms, for every
    /// caller of this shared primitive, not just BuyLimit/SellLimit.
    /// </summary>
    public static decimal RoundToTick(decimal price, decimal tickSize) =>
        tickSize <= 0m ? price : Math.Round(price / tickSize) * tickSize;

    public readonly struct TradingContext
    {
        public Portfolio? Portfolio { get; init; }
        public Security? Security { get; init; }
        public BridgeMessage? Error { get; init; }
    }

    public TradingContext Resolve(BridgeMessage cmd)
    {
        var accountName = cmd.GetString("account");
        var instrumentName = cmd.GetString("instrument");

        if (string.IsNullOrWhiteSpace(accountName))
            return Refuse(cmd, "CONTEXT_MISSING", "Account name is required.");

        if (string.IsNullOrWhiteSpace(instrumentName))
            return Refuse(cmd, "CONTEXT_MISSING", "Instrument name is required.");

        var portfolio = _resolver.FindPortfolio(accountName);
        if (portfolio == null)
            return Refuse(cmd, "ACCOUNT_NOT_FOUND", $"Account '{accountName}' not found.");

        // Portfolio resolved first, passed into FindSecurity so the lookup is scoped to the
        // connector that owns THIS account — see ContextResolver.FindSecurity. This is the call
        // site where a foreign security becomes a rejected order: the resolved Security is set
        // directly on the Order alongside this Portfolio before ChartStrategy.OpenOrder.
        var security = _resolver.FindSecurity(instrumentName, portfolio);
        if (security == null)
            return Refuse(cmd, "INSTRUMENT_NOT_FOUND", $"Instrument '{instrumentName}' not found.");

        return new TradingContext { Portfolio = portfolio, Security = security };
    }

    private static TradingContext Refuse(BridgeMessage cmd, string code, string message) =>
        new() { Error = BridgeMessage.Failure(cmd.RequestId, cmd.Action, code, message) };
}
