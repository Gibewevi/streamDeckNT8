using System;
using System.Collections.Generic;
using System.Linq;

using ATAS.DataFeedsCore;

namespace TradeDeck.Atas.Strategy;

/// <summary>
/// Turns the deck's selection — two strings, an account name and an instrument name — into the ATAS
/// objects an order needs.
///
/// RESOLVED AGAINST THE CONNECTOR, NEVER AGAINST THE CHART. The chart the strategy is attached to is
/// only a host; Order carries its own Portfolio and Security, so the deck can trade an instrument
/// the chart does not display. Every lookup below therefore goes through Connector.Portfolios /
/// Connector.Securities, and never through ChartStrategy.Portfolio / .Security.
/// </summary>
internal sealed class ContextResolver
{
    private readonly Func<IDataFeedConnector?> _connector;

    public ContextResolver(Func<IDataFeedConnector?> connector) => _connector = connector;

    public IDataFeedConnector? Connector => _connector();

    public Portfolio? FindPortfolio(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;

        var portfolios = Connector?.Portfolios;
        if (portfolios == null) return null;

        foreach (var portfolio in portfolios)
        {
            if (string.Equals(portfolio?.AccountID, name, StringComparison.OrdinalIgnoreCase))
                return portfolio;
        }

        SdLog.Warn("Account not found: {0}", name);
        return null;
    }

    /// <summary>
    /// Matches the deck's instrument string against what the connector knows.
    ///
    /// Three passes, narrowest first. The deck sends whatever the trader typed on the Instrument
    /// key — "MNQ", "MNQ 09-26", or the broker's own symbol — and each broker names its contracts
    /// differently. Matching Code exactly first means an unambiguous symbol never gets hijacked by
    /// a prefix match on another one.
    /// </summary>
    public Security? FindSecurity(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;

        var securities = Connector?.Securities;
        if (securities == null) return null;

        var wanted = name.Trim();
        Security? prefix = null;

        foreach (var security in securities)
        {
            if (security == null) continue;

            if (string.Equals(security.Code, wanted, StringComparison.OrdinalIgnoreCase)) return security;
            if (string.Equals(security.Instrument, wanted, StringComparison.OrdinalIgnoreCase)) return security;

            // Kept as a fallback rather than returned at once: an exact match found later in the
            // list must still win over a prefix match found earlier.
            if (prefix == null && !string.IsNullOrEmpty(security.Code) &&
                security.Code.StartsWith(wanted, StringComparison.OrdinalIgnoreCase))
            {
                prefix = security;
            }
        }

        if (prefix != null) return prefix;

        SdLog.Warn("Instrument not found: {0}", name);
        return null;
    }

    /// <summary>
    /// The open position, or null when flat.
    ///
    /// A position object with Volume == 0 is NOT a position: ATAS keeps the row after a close, and
    /// treating it as open would let flatten, break-even and reverse act on nothing while telling
    /// the deck they worked.
    /// </summary>
    public Position? FindPosition(Portfolio? portfolio, Security? security)
    {
        if (portfolio == null || security == null) return null;

        var positions = Connector?.Positions;
        if (positions == null) return null;

        foreach (var position in positions)
        {
            if (position == null) continue;
            if (!ReferenceEquals(position.Portfolio, portfolio) &&
                !string.Equals(position.Portfolio?.AccountID, portfolio.AccountID, StringComparison.OrdinalIgnoreCase))
                continue;
            if (!SameSecurity(position.Security, security)) continue;

            return position.Volume != 0 ? position : null;
        }

        return null;
    }

    /// <summary>
    /// Account names the deck may select.
    ///
    /// Real accounts first, then simulated ones — the same order the NinjaTrader add-on publishes,
    /// so the Account key's list does not reshuffle when a trader moves between platforms.
    /// IsRealAccount is a FACT ATAS gives us, not a naming convention: the NinjaTrader side had to
    /// infer it from the provider, and the bridge still infers it from a "Sim" prefix.
    /// </summary>
    public List<string> GetAccountNames()
    {
        var live = new List<string>();
        var sim = new List<string>();

        var portfolios = Connector?.Portfolios;
        if (portfolios != null)
        {
            foreach (var portfolio in portfolios)
            {
                var name = portfolio?.AccountID;
                if (string.IsNullOrWhiteSpace(name)) continue;

                // A REPLAY PORTFOLIO IS NOT A TRADABLE ACCOUNT. Replay walks historical data and
                // produces real Order and MyTrade objects: offering it on the Account key would let
                // the trader place "orders" that go nowhere while the journal files fabricated
                // trades to Bitlearn as if they had happened. The NinjaTrader add-on guarded the
                // same hazard by excluding the accounts named Backtest and Playback101 — ATAS states
                // it as a fact instead of a name.
                if (portfolio!.IsReplay()) continue;

                if (portfolio.IsRealAccount) live.Add(name); else sim.Add(name);
            }
        }

        return live.Concat(sim).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    public List<Order> FindActiveOrders(Portfolio? portfolio, Security? security)
    {
        var found = new List<Order>();
        if (portfolio == null || security == null) return found;

        var orders = Connector?.Orders;
        if (orders == null) return found;

        foreach (var order in orders)
        {
            if (!IsActive(order)) continue;
            if (!SamePortfolio(order!.Portfolio, portfolio)) continue;
            if (!SameSecurity(order.Security, security)) continue;
            found.Add(order);
        }

        return found;
    }

    public List<Order> FindAllActiveOrders(Portfolio? portfolio)
    {
        var found = new List<Order>();
        if (portfolio == null) return found;

        var orders = Connector?.Orders;
        if (orders == null) return found;

        foreach (var order in orders)
        {
            if (!IsActive(order)) continue;
            if (!SamePortfolio(order!.Portfolio, portfolio)) continue;
            found.Add(order);
        }

        return found;
    }

    /// <summary>Working stop orders, sorted by trigger price ascending.</summary>
    public List<Order> FindStopOrders(Portfolio? portfolio, Security? security) =>
        FindActiveOrders(portfolio, security)
            .Where(o => o.Type is OrderTypes.Stop or OrderTypes.StopLimit)
            .OrderBy(o => o.TriggerPrice)
            .ToList();

    /// <summary>Working limit orders, sorted by price ascending.</summary>
    public List<Order> FindTargetOrders(Portfolio? portfolio, Security? security) =>
        FindActiveOrders(portfolio, security)
            .Where(o => o.Type == OrderTypes.Limit)
            .OrderBy(o => o.Price)
            .ToList();

    /// <summary>
    /// An order still worth acting on.
    ///
    /// Order.Status() rather than Order.State. The raw enum has only four values — None, Active,
    /// Done, Failed — where NinjaTrader had a dozen, and it cannot tell a filled order from a
    /// cancelled one. The Status() extension is what the platform's own order-management
    /// documentation uses, and it answers the question directly.
    ///
    /// PartlyFilled counts as live: the remainder is still working, and it is still a protection.
    /// </summary>
    public static bool IsActive(Order? order) =>
        order != null && order.Status() is OrderStatus.Placed or OrderStatus.PartlyFilled;

    /// <summary>
    /// The name this instrument goes by, everywhere: the state block, the journal, the logs.
    ///
    /// ONE function on purpose. The server groups a journal by this string and the deck matches its
    /// selection against it; two spellings of the same contract would split a day's trades in two.
    /// </summary>
    public static string InstrumentName(Security? security)
    {
        if (security == null) return string.Empty;
        if (!string.IsNullOrWhiteSpace(security.Code)) return security.Code;
        return security.Instrument ?? string.Empty;
    }

    /// <summary>
    /// Currency value of one full point — TickCost per TickSize.
    ///
    /// THE ONLY DERIVED FIELD OF THE WHOLE PORT, AND THE MOST SENSITIVE. NinjaTrader hands it over
    /// ready-made (MasterInstrument.PointValue); ATAS does not. The server computes every journal
    /// P&amp;L from this number and has no instrument table of its own, so a wrong value corrupts
    /// the statistics of every trade without anything ever looking broken.
    ///
    /// Returns 0 when it cannot be derived. 0 is honest: a caller can omit the field, whereas a
    /// fabricated 1.0 would silently price the whole day at a fiftieth of its value.
    /// </summary>
    public static decimal PointValue(Security? security)
    {
        if (security == null) return 0m;
        var tickSize = security.TickSize;
        if (tickSize <= 0m) return 0m;
        return security.TickCost / tickSize;
    }

    /// <summary>
    /// The price a deck command computes against, or 0 when there is no feed.
    ///
    /// Mid of best bid and ask, falling back to the last trade. 0 IS A REFUSAL SIGNAL, never a
    /// price: a limit computed from 0 lands a few ticks from zero, and a sell limit under the market
    /// fills instantly AT market. Every caller must test it — see NO_MARKET_DATA.
    /// </summary>
    public static decimal MarketPrice(Security? security)
    {
        if (security == null) return 0m;

        var bid = security.BestBidPrice;
        var ask = security.BestAskPrice;
        if (bid > 0m && ask > 0m) return (bid + ask) / 2m;
        if (ask > 0m) return ask;
        if (bid > 0m) return bid;

        var last = security.LastTradePrice ?? 0m;
        return last > 0m ? last : 0m;
    }

    public static bool SamePortfolio(Portfolio? a, Portfolio? b)
    {
        if (a == null || b == null) return false;
        if (ReferenceEquals(a, b)) return true;
        return !string.IsNullOrEmpty(a.AccountID) &&
               string.Equals(a.AccountID, b.AccountID, StringComparison.OrdinalIgnoreCase);
    }

    public static bool SameSecurity(Security? a, Security? b)
    {
        if (a == null || b == null) return false;
        if (ReferenceEquals(a, b)) return true;
        if (a.Id != 0 && a.Id == b.Id) return true;
        return !string.IsNullOrEmpty(a.SecurityId) &&
               string.Equals(a.SecurityId, b.SecurityId, StringComparison.OrdinalIgnoreCase);
    }
}
