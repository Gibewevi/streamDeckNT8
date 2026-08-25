using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

using ATAS.DataFeedsCore;
using ATAS.Indicators;

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

        // WITHOUT A FEED THIS IS 0, AND 0 IS NOT A PRICE. A limit computed from it would land a few
        // ticks from zero: a sell limit there fills instantly AT MARKET, which is the opposite of
        // what the trader asked for. The order is refused instead of sent.
        var market = ContextResolver.MarketPrice(ctx.Security);
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

    /// <summary>Rounds to a whole number of ticks — a price off the grid is refused by the venue.</summary>
    public static decimal RoundToTick(decimal price, decimal tickSize) =>
        tickSize <= 0m ? price : Math.Round(price / tickSize, MidpointRounding.AwayFromZero) * tickSize;

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
