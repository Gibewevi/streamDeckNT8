using System;
using System.Collections.Generic;
using System.Linq;

using ATAS.DataFeedsCore;
using ATAS.Indicators;

namespace TradeDeck.Atas.Strategy;

/// <summary>
/// Turns the deck's selection — two strings, an account name and an instrument name — into the ATAS
/// objects an order needs.
///
/// RESOLVED AGAINST THE AGGREGATED HOST REGISTRY, NEVER AGAINST THE CHART, AND NEVER AGAINST A SINGLE
/// CONNECTOR. There is no such thing as "the" connector: portfolios are spread across whichever of
/// the platform's connectors are actually connected (measured live: 2 of 12, one of them a Rithmic
/// connector carrying a portfolio invisible from the chart). Every account/instrument/position/order
/// lookup below therefore walks <see cref="ConnectorRegistry.GetConnectors"/>, never a single
/// connector — and Order carries its own Portfolio and Security, so the deck can trade an instrument
/// the chart does not display.
///
/// DEGRADED MODE. The fallback to the chart's own <c>ITradingManager</c> — one account, one
/// instrument, the chart's — is gated on "no connector could answer THIS question", never on
/// <see cref="IsDegraded"/> (whether the registry as a whole is reachable). Those are different
/// states, and account/position/order lookups below gate on the former: the registry can be fully
/// reachable — <see cref="Connectors"/> non-empty, the account and instrument both resolve — while
/// the one portfolio being asked about currently carries no connector (ATAS started before the
/// broker attached, or a mid-session reconnect). Gating on IsDegraded there used to publish
/// exists:false / Flat over a real position while TradingManager had the right answer the whole
/// time. IsDegraded itself stays meaningful for logging and for <see cref="GetAccountNames"/>
/// (which genuinely can only see one account with no registry at all) — it is just never the
/// switch a single lookup uses to decide whether to fall back. This is not a partial failure to
/// patch around: it is the delivered fallback (see Contraintes globales), entered and left
/// silently except for the one transition log line in <see cref="ConnectorRegistry.GetConnectors"/>
/// (via <c>IsAvailable</c> flipping) — this class adds no logging of its own for the transition, it
/// only reads the registry's current answer on each call.
/// </summary>
internal sealed class ContextResolver
{
    private readonly ConnectorRegistry _registry;
    private readonly Func<ITradingManager?> _tradingManager;

    public ContextResolver(ConnectorRegistry registry, Func<ITradingManager?> tradingManager)
    {
        _registry = registry;
        _tradingManager = tradingManager;
    }

    /// <summary>True once the host registry has proven unreachable — read for logging/diagnostics only.</summary>
    public bool IsDegraded => !_registry.IsAvailable;

    /// <summary>Every connector currently carrying a portfolio. Empty in degraded mode.</summary>
    public IReadOnlyList<IDataFeedConnector> Connectors => _registry.GetConnectors();

    public IDataFeedConnector? ConnectorFor(Portfolio? portfolio) => _registry.ConnectorFor(portfolio);

    public Portfolio? FindPortfolio(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;

        var connectors = _registry.GetConnectors();
        if (connectors.Count == 0) return DegradedPortfolio(name);

        foreach (var connector in connectors)
        {
            var portfolios = SafePortfolios(connector);
            foreach (var portfolio in portfolios)
            {
                if (string.Equals(portfolio?.AccountID, name, StringComparison.OrdinalIgnoreCase))
                    return portfolio;
            }
        }

        if (ShouldWarn("account-not-found:" + name))
            SdLog.Warn("Account not found: {0}", name);
        return null;
    }

    /// <summary>
    /// Chart-scoped fallback for FindPortfolio when the registry is unreachable. Only ONE portfolio
    /// exists in this mode — the chart's own, via TradingManager — so the lookup degrades to
    /// "is this the one account we have", rather than a search.
    /// </summary>
    private Portfolio? DegradedPortfolio(string name)
    {
        var portfolio = _tradingManager()?.Portfolio;
        if (portfolio == null) return null;
        if (string.Equals(portfolio.AccountID, name, StringComparison.OrdinalIgnoreCase)) return portfolio;

        if (ShouldWarn("account-not-found-degraded:" + name))
            SdLog.Warn("Account '{0}' not found — degraded mode only exposes the chart's own account ('{1}')",
                name, portfolio.AccountID ?? "?");
        return null;
    }

    /// <summary>
    /// Rate limit for every "could not resolve" warning below: the ambiguous-prefix warning in
    /// FindSecurity, the not-found warnings in FindPortfolio, FindSecurity and their degraded-mode
    /// counterparts, and the foreign-connector warning FindSecurity logs when a portfolio-scoped
    /// lookup fell back to the cross-connector search.
    ///
    /// UNGUARDED, THIS IS UNBOUNDED ON THE PUBLISH PATH. StatePublisher.Publish calls
    /// FindSecurity(TrackedInstrument) every 500ms. Unlike the account (auto-corrected to the
    /// first connected one — see StatePublisher.AdoptDefaultAccountIfNeeded), the instrument is
    /// NEVER auto-corrected: once the bridge names an instrument the registry cannot match — the
    /// migration case, NT8's "MNQ 12-26" spelling against ATAS's "MNQZ6" — every tick warns
    /// forever without this limiter. That is ~2 lines/second, ~172,800/day, through a locked
    /// AutoFlush disk write twice a second, until the day's log file rolls mid-incident and takes
    /// the real diagnostic window down with it.
    ///
    /// THREAD-SAFE ON PURPOSE, separately from the log-volume reason above: unlike the
    /// NinjaTrader-era version of this rate limit (a bare DateTime, safe only because a single
    /// serialised 500ms publisher was the only caller), FindSecurity and FindPortfolio are reached
    /// from task 4's OrderMonitor and task 8's GuardEnforcer too — both driven by connector
    /// events, which do not arrive on the chart thread. A torn read/write on a bare DateTime here
    /// would not crash, but could let two threads both pass the rate-limit check on the same
    /// condition and double-log it, or — worse — corrupt the field into a value that suppresses
    /// the warning forever. The lock is tiny and only taken on the rare warning path, never on a
    /// successful lookup.
    ///
    /// Keyed per condition (e.g. "instrument-not-found:MNQ 12-26") rather than one shared
    /// timestamp: a warning about a missing account must not silently swallow an unrelated warning
    /// about a missing instrument that happens to fire in the same minute.
    /// </summary>
    private readonly object _warnRateLimitLock = new();
    private readonly Dictionary<string, DateTime> _lastWarnByKey = new(StringComparer.Ordinal);

    private bool ShouldWarn(string key)
    {
        lock (_warnRateLimitLock)
        {
            if (_lastWarnByKey.TryGetValue(key, out var last) && (DateTime.UtcNow - last).TotalMinutes < 1)
                return false;
            _lastWarnByKey[key] = DateTime.UtcNow;
            return true;
        }
    }

    /// <summary>
    /// Matches the deck's instrument string against what the registry knows.
    ///
    /// Three passes, narrowest first, exact match always wins regardless of which connector or
    /// position in the enumeration it came from — see the ambiguity handling below. The deck sends
    /// whatever the trader typed on the Instrument key — "MNQ", "MNQ 09-26", or the broker's own
    /// symbol — and each broker names its contracts differently.
    ///
    /// PREFIX MATCHES ARE COLLECTED, NOT LATCHED. Taking the first prefix match in enumeration order
    /// would let the SAME deck keypress silently resolve to a DIFFERENT contract after a reconnect —
    /// whichever connector happens to enumerate first is not a stable choice, and nothing would be
    /// in the log to explain where an order went. Instead every prefix match, from every connector,
    /// is collected and the ambiguity is resolved deterministically below.
    ///
    /// SCOPED TO THE PORTFOLIO'S OWN CONNECTOR WHEN ONE IS GIVEN. Field trial, 2026-08-25: with no
    /// scoping this searched every connector and returned the first match, with no relation to the
    /// account the order was about to be submitted on — an order carried Portfolio from a
    /// RithmicConnector account (LDE050-SZ976P0C-TEST001) and Security from the SbeConnector purely
    /// because Sbe happened to enumerate first. It was correctly submitted on that account — "Buy 1
    /// MESU6 Market submitted on LDE050-SZ976P0C-TEST001" — and the broker answered "This order
    /// could not be submitted to the infrastructure. Are your ticker symbol and exchange values
    /// valid?", the signature of a ticker from a different broker. It also explains the earlier log
    /// line "Ambiguous instrument 'ES' matched 2 securities by prefix — chose ESU6, rejected: ESU6"
    /// — chosen and rejected shared the same Code because they were the SAME contract seen through
    /// two different connectors, not a real month ambiguity. Resolving within the portfolio's own
    /// connector first (see <c>ResolvePrefixAmbiguity</c> for the same-Code collapsing) is the only
    /// way to guarantee Security and Portfolio agree on which broker they belong to. The unscoped
    /// fallback below still exists — refusing outright would be a behaviour change beyond this fix,
    /// and StatePublisher relies on it before an account is even known — but it is now logged when
    /// it is what actually answers, because from that point on the security may be foreign to the
    /// account the caller is about to trade with.
    /// </summary>
    public Security? FindSecurity(string? name, Portfolio? portfolio = null)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;

        var connectors = _registry.GetConnectors();
        if (connectors.Count == 0) return DegradedSecurity(name);

        var wanted = name.Trim();

        if (portfolio != null)
        {
            var scoped = _registry.ConnectorFor(portfolio);
            if (scoped != null)
            {
                var scopedResult = SearchConnectors(new[] { scoped }, wanted, name);
                if (scopedResult != null) return scopedResult;
            }

            // NOT FOUND ON THE PORTFOLIO'S OWN CONNECTOR IS ROUTINE, NOT AN ERROR. Measured live:
            // IDataFeedConnector.Securities lists only what the platform currently has SUBSCRIBED,
            // not the broker's catalogue — MES resolved to nothing and three buyMarket were
            // correctly refused INSTRUMENT_NOT_FOUND until the trader opened a chart or DOM on MES,
            // after which the same name resolved and filled. Falling through to the cross-connector
            // search below is what makes that recover instead of refusing forever; the WARN after it
            // is what keeps the resulting foreign-security risk visible instead of silent.
        }

        var result = SearchConnectors(connectors, wanted, name);

        if (result != null)
        {
            // Only reached when a portfolio was given AND its own connector could not answer above —
            // this IS the fallback answering, so the security may belong to a different broker than
            // `portfolio`. Rate-limited per account+instrument like every other warning here.
            if (portfolio != null &&
                ShouldWarn("security-foreign-connector:" + wanted + ":" + portfolio.AccountID))
            {
                SdLog.EventWarn("Instrument",
                    "Instrument '{0}' not found on account '{1}''s own connector — using a cross-connector match instead; the order may carry a security from a different broker than this account",
                    name, portfolio.AccountID ?? "?");
            }
            return result;
        }

        if (ShouldWarn("instrument-not-found:" + wanted))
            SdLog.Warn("Instrument not found: {0}", name);
        return null;
    }

    /// <summary>
    /// The narrowest-first, three-pass search itself, run against whichever connector list the
    /// caller hands it — either one connector (the scoped attempt) or every connector (the
    /// fallback). Split out of FindSecurity so both attempts share one implementation instead of
    /// two copies drifting apart.
    /// </summary>
    private Security? SearchConnectors(IReadOnlyList<IDataFeedConnector> connectors, string wanted, string originalName)
    {
        List<Security>? prefixCandidates = null;

        foreach (var connector in connectors)
        {
            foreach (var security in SafeSecurities(connector))
            {
                if (security == null) continue;

                if (string.Equals(security.Code, wanted, StringComparison.OrdinalIgnoreCase)) return security;
                if (string.Equals(security.Instrument, wanted, StringComparison.OrdinalIgnoreCase)) return security;

                // Collected rather than returned at once: an exact match found later — on this
                // connector or another one — must still win over a prefix match found earlier, and
                // — when more than one security shares the prefix, possibly across connectors — the
                // choice below must not depend on which connector or which security the aggregation
                // happened to enumerate first.
                if (!string.IsNullOrEmpty(security.Code) && security.Code.StartsWith(wanted, StringComparison.OrdinalIgnoreCase))
                {
                    (prefixCandidates ??= new List<Security>()).Add(security);
                }
            }
        }

        if (prefixCandidates == null || prefixCandidates.Count == 0) return null;
        if (prefixCandidates.Count == 1) return prefixCandidates[0];

        return ResolvePrefixAmbiguity(prefixCandidates, wanted, originalName);
    }

    /// <summary>
    /// Resolves more than one prefix match, deterministically, and explains itself in the log — but
    /// only when there is a real ambiguity to explain.
    ///
    /// COLLAPSE SAME-CODE DUPLICATES BEFORE THE TIE-BREAK. Field trial, 2026-08-25: "Ambiguous
    /// instrument 'ES' matched 2 securities by prefix — chose ESU6 (front month), rejected: ESU6" —
    /// chosen and rejected carried the SAME Code. That is not a contract-month ambiguity at all: it
    /// is the same contract seen through two different connectors (one Sbe, one Rithmic), and the
    /// Expiration-then-Code tie-break cannot separate two objects that already agree on both, so the
    /// pick was once again "whichever connector the aggregation enumerated first" — the exact
    /// non-determinism this whole mechanism exists to eliminate. Grouping by Code first means the
    /// tie-break only ever compares genuinely different contract months, and the warning below fires
    /// only when there is one.
    /// </summary>
    private Security ResolvePrefixAmbiguity(List<Security> prefixCandidates, string wanted, string originalName)
    {
        var byCode = prefixCandidates
            .GroupBy(s => s.Code, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();

        if (byCode.Count == 1) return byCode[0];

        // AMBIGUOUS PREFIX, RESOLVED DETERMINISTICALLY — AND SAID OUT LOUD. Several contract
        // months can share a root ("MNQ" prefixes both a December and a March contract), and now
        // possibly across different connectors too. Whichever one the aggregation happened to
        // enumerate first is not a criterion: it can change between reconnects, which would silently
        // move the SAME deck command onto a DIFFERENT contract with nothing in the log to explain why
        // an order landed somewhere unexpected.
        //
        // Tie-break: soonest Expiration first (the front month is what a trader typing the bare
        // root almost always means), with securities that carry no real expiration — Expiration
        // defaults to DateTime.MinValue for those — pushed to the end rather than the front, since
        // a non-expiring symbol matching by prefix coincidence is the least likely intended match.
        // Code is the final tie-break so the pick is reproducible even if two candidates somehow
        // share an expiration.
        var chosen = byCode
            .OrderBy(s => s.Expiration == default ? DateTime.MaxValue : s.Expiration)
            .ThenBy(s => s.Code, StringComparer.OrdinalIgnoreCase)
            .First();

        if (ShouldWarn("ambiguous-instrument:" + wanted))
        {
            var rejected = string.Join(", ", byCode.Where(s => s != chosen).Select(s => s.Code));
            SdLog.EventWarn("Instrument",
                "Ambiguous instrument '{0}' matched {1} distinct contracts by prefix — chose {2} (front month), rejected: {3}",
                originalName, byCode.Count, chosen.Code, rejected);
        }

        return chosen;
    }

    private Security? DegradedSecurity(string name)
    {
        var security = _tradingManager()?.Security;
        if (security == null) return null;
        if (string.Equals(security.Code, name, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(security.Instrument, name, StringComparison.OrdinalIgnoreCase))
            return security;

        if (ShouldWarn("instrument-not-found-degraded:" + name))
            SdLog.Warn("Instrument '{0}' not found — degraded mode only exposes the chart's own instrument ('{1}')",
                name, security.Code ?? "?");
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

        // NO CONNECTOR COULD ANSWER — not "the registry is unreachable" — is the fallback gate.
        // The registry can be fully reachable (connectors.Count > 0, account and instrument both
        // resolve) while THIS portfolio currently carries no connector: ATAS started before the
        // broker attached, or a mid-session reconnect. IsDegraded would stay false there and this
        // used to return null outright, publishing exists:false / Flat over a real position while
        // TradingManager had the right answer the whole time — the host's Auto BE / Auto TP-SL
        // would then act on that lie. DegradedPosition re-checks portfolio and security match, so
        // falling back unconditionally cannot answer about the wrong account.
        var connector = _registry.ConnectorFor(portfolio);
        if (connector == null) return DegradedPosition(portfolio, security);

        foreach (var position in SafePositions(connector))
        {
            if (position == null) continue;
            if (!SamePortfolio(position.Portfolio, portfolio)) continue;
            if (!SameSecurity(position.Security, security)) continue;

            // KEEP SCANNING PAST A ZERO-VOLUME ROW. ATAS can carry more than one Position row for
            // the same portfolio+security pair (a closed-then-reopened position, a partial fill
            // still settling) — returning null on the FIRST match, flat or not, let an earlier
            // flat row shadow a later row that was actually open. Only conclude flat once every
            // matching row has been seen.
            if (position.Volume != 0) return position;
        }

        return null;
    }

    private Position? DegradedPosition(Portfolio portfolio, Security security)
    {
        var tm = _tradingManager();
        var position = tm?.Position;
        if (position == null || position.Volume == 0m) return null;
        if (!SamePortfolio(position.Portfolio, portfolio) || !SameSecurity(position.Security, security)) return null;
        return position;
    }

    /// <summary>
    /// Every open position on this portfolio, across whichever connector owns it — used by
    /// flattenAccount, which liquidates the whole account rather than one instrument.
    /// </summary>
    public List<Position> FindAllPositions(Portfolio? portfolio)
    {
        var found = new List<Position>();
        if (portfolio == null) return found;

        // Same gate as FindPosition above — no connector carries this portfolio right now, not
        // "the registry as a whole is unreachable".
        var connector = _registry.ConnectorFor(portfolio);
        if (connector == null)
        {
            var position = _tradingManager()?.Position;
            if (position != null && position.Volume != 0m && SamePortfolio(position.Portfolio, portfolio))
                found.Add(position);
            return found;
        }

        foreach (var position in SafePositions(connector))
        {
            if (position == null || position.Volume == 0m) continue;
            if (!SamePortfolio(position.Portfolio, portfolio)) continue;
            found.Add(position);
        }
        return found;
    }

    /// <summary>
    /// Account names the deck may select, aggregated across every connected connector.
    ///
    /// Real accounts first, then simulated ones — the same order the NinjaTrader add-on publishes,
    /// so the Account key's list does not reshuffle when a trader moves between platforms.
    /// IsRealAccount is a FACT ATAS gives us, not a naming convention: the NinjaTrader side had to
    /// infer it from the provider, and the bridge still infers it from a "Sim" prefix.
    ///
    /// DEGRADED MODE returns AT MOST ONE NAME — the chart's own account. That is not a bug to fix:
    /// it is the entire point of the fallback, and it is what tells the trader, honestly, that the
    /// deck can no longer see the rest of the platform's accounts.
    /// </summary>
    public List<string> GetAccountNames()
    {
        var connectors = _registry.GetConnectors();
        if (connectors.Count == 0) return DegradedAccountNames();

        var live = new List<string>();
        var sim = new List<string>();

        foreach (var connector in connectors)
        {
            foreach (var portfolio in SafePortfolios(connector))
            {
                var name = portfolio?.AccountID;
                if (string.IsNullOrWhiteSpace(name)) continue;

                // A REPLAY PORTFOLIO IS NOT A TRADABLE ACCOUNT. Replay walks historical data and
                // produces real Order and MyTrade objects: offering it on the Account key would let
                // the trader place "orders" that go nowhere while the journal files fabricated
                // trades to Bitlearn as if they had happened. The NinjaTrader add-on guarded the
                // same hazard by excluding the accounts named Backtest and Playback101 — ATAS states
                // it as a fact instead of a name.
                if (SafeIsReplay(portfolio!)) continue;

                if (portfolio.IsRealAccount) live.Add(name); else sim.Add(name);
            }
        }

        return live.Concat(sim).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private List<string> DegradedAccountNames()
    {
        var portfolio = _tradingManager()?.Portfolio;
        if (portfolio == null || string.IsNullOrWhiteSpace(portfolio.AccountID)) return new List<string>();
        if (SafeIsReplay(portfolio)) return new List<string>();
        return new List<string> { portfolio.AccountID };
    }

    public List<Order> FindActiveOrders(Portfolio? portfolio, Security? security)
    {
        var found = new List<Order>();
        if (portfolio == null || security == null) return found;

        // Same gate as FindPosition — no connector for THIS portfolio, not registry unreachable.
        var connector = _registry.ConnectorFor(portfolio);
        if (connector == null)
        {
            found.AddRange(DegradedOrders(portfolio, security, allInstruments: false));
            return found;
        }

        foreach (var order in SafeOrders(connector))
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

        // Same gate as FindPosition — no connector for THIS portfolio, not registry unreachable.
        var connector = _registry.ConnectorFor(portfolio);
        if (connector == null)
        {
            found.AddRange(DegradedOrders(portfolio, security: null, allInstruments: true));
            return found;
        }

        foreach (var order in SafeOrders(connector))
        {
            if (!IsActive(order)) continue;
            if (!SamePortfolio(order!.Portfolio, portfolio)) continue;
            found.Add(order);
        }

        return found;
    }

    /// <summary>
    /// Degraded-mode order lookup, via TradingManager.Orders — confirmed live to show every order on
    /// the chart's account+instrument pair REGARDLESS OF ORIGIN, including a manual ChartTrader order
    /// (Comment empty). That is the discriminator GuardEnforcer relies on even in degraded mode.
    /// </summary>
    private IEnumerable<Order> DegradedOrders(Portfolio portfolio, Security? security, bool allInstruments)
    {
        var tm = _tradingManager();
        var orders = tm?.Orders;
        if (orders == null) yield break;

        foreach (var order in orders)
        {
            if (!IsActive(order)) continue;
            if (!SamePortfolio(order!.Portfolio, portfolio)) continue;
            if (!allInstruments && !SameSecurity(order.Security, security)) continue;
            yield return order;
        }
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

    // --- Defensive accessors -----------------------------------------------------------------
    //
    // Every lookup above walks collections owned by a connector this class does not control, on the
    // 500ms publish tick. A broker's connector rebuilding its portfolio list mid-walk, or throwing
    // out of a property it owns, must cost this ONE lookup, never the tick's real stateUpdate — same
    // reasoning as ConnectorRegistry.SafePortfolioCount.
    //
    // THE ENUMERATION ITSELF, NOT JUST THE PROPERTY GETTER, MUST BE INSIDE THE TRY. A connector
    // mutating its collection WHILE this walks it — a burst of fills arriving mid-tick — throws
    // InvalidOperationException from the enumerator's MoveNext, not from the getter. Wrapping only
    // `connector.Orders` and returning the live IEnumerable let that exception escape into the
    // caller's foreach, past this class, into StatePublisher.Publish's catch: the WHOLE tick's
    // stateUpdate was dropped and an ERROR with a full stack trace got logged, repeatedly, at
    // exactly the moment orders were moving — the deck freezes on stale protection data at the
    // worst possible time. Materialising to a list INSIDE the try keeps whatever was read before
    // the throw instead of discarding it — the cost of a torn read is one missing row, not the
    // whole tick.

    private static IEnumerable<Portfolio> SafePortfolios(IDataFeedConnector connector)
    {
        var result = new List<Portfolio>();
        try
        {
            var portfolios = connector.Portfolios;
            if (portfolios != null) foreach (var p in portfolios) result.Add(p);
        }
        catch (Exception ex)
        {
            SdLog.TraceEvent("Context", "Portfolios read faulted after {0} entrie(s): {1}", result.Count, ex.GetType().Name);
        }
        return result;
    }

    private static IEnumerable<Security> SafeSecurities(IDataFeedConnector connector)
    {
        var result = new List<Security>();
        try
        {
            var securities = connector.Securities;
            if (securities != null) foreach (var s in securities) result.Add(s);
        }
        catch (Exception ex)
        {
            SdLog.TraceEvent("Context", "Securities read faulted after {0} entrie(s): {1}", result.Count, ex.GetType().Name);
        }
        return result;
    }

    private static IEnumerable<Position> SafePositions(IDataFeedConnector connector)
    {
        var result = new List<Position>();
        try
        {
            var positions = connector.Positions;
            if (positions != null) foreach (var p in positions) result.Add(p);
        }
        catch (Exception ex)
        {
            SdLog.TraceEvent("Context", "Positions read faulted after {0} entrie(s): {1}", result.Count, ex.GetType().Name);
        }
        return result;
    }

    private static IEnumerable<Order> SafeOrders(IDataFeedConnector connector)
    {
        var result = new List<Order>();
        try
        {
            var orders = connector.Orders;
            if (orders != null) foreach (var o in orders) result.Add(o);
        }
        catch (Exception ex)
        {
            SdLog.TraceEvent("Context", "Orders read faulted after {0} entrie(s): {1}", result.Count, ex.GetType().Name);
        }
        return result;
    }

    private static bool SafeIsReplay(Portfolio portfolio)
    {
        try { return portfolio.IsReplay(); }
        catch (Exception ex) { SdLog.TraceEvent("Context", "IsReplay() faulted: {0}", ex.GetType().Name); return false; }
    }
}
