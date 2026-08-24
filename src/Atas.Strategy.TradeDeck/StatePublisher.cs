using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Threading;

using ATAS.DataFeedsCore;
using ATAS.Indicators;
using ATAS.Strategies.Chart;
// DEVIATION FROM THE BRIEF, FOUND ONLY AT BUILD TIME: Portfolio.ConnectionState is
// Nullable<Utils.Common.Network.Client.ConnectionStates>, not StockSharp.Messages.ConnectionStates
// as ANNEXE B stated (confirmed by reflecting on the live property: reflection on the type's own
// name lied by coincidence — StockSharp.Messages happens to declare an unrelated enum with the same
// short name, which is why the mismatch surfaced as a CS0019 on the equality operator rather than
// a missing-reference error). Utils.Common is already referenced by the csproj for another reason,
// so no new reference was needed — only this using.
using Utils.Common.Network.Client;

namespace TradeDeck.Atas.Strategy;

/// <summary>
/// Publishes the deck's view of the world every 500 ms, and on demand when something actually
/// moved.
///
/// EVERYTHING HERE IS TRACE. This loop runs twice a second: one INFO line in it produces hundreds
/// of thousands of lines a day, and the file that matters on a bad day becomes unopenable.
/// </summary>
internal sealed class StatePublisher : IDisposable
{
    private readonly ContextResolver _resolver;
    private readonly BridgeClient _bridge;
    private readonly Func<ChartStrategy?> _chartStrategy;
    private readonly Func<ITradingManager?> _tradingManager;
    private readonly int _intervalMs;

    private Timer? _timer;
    private bool _disposed;

    /// <summary>
    /// Guards against two publishes overlapping. The deck showing stale data is bad; two frames
    /// interleaved on one socket is worse — that is the send lock's problem, and this keeps it from
    /// ever being tested.
    /// </summary>
    private int _publishing;

    private int _consecutiveSkips;

    // Transition-tracking for ReportConnectorDiagnostics — see that method for why. -2 for the
    // security count specifically because -1 is a real, meaningful value below (count failed), and
    // the very first tick must never be mistaken for "unchanged since a -1 we never actually saw".
    private bool? _lastConnectorNull;
    private bool? _lastPortfoliosNull;
    private int _lastPortfolioCount = -1;
    private int _lastSecurityCount = -2;
    private int _lastResolvedAccountCount = -1;

    // Transition-tracking for ReportClassicSurfaceDiagnostics. Null until the first tick runs, which
    // guarantees the first observation is always reported at a visible level instead of being
    // mistaken for "unchanged since a default we never actually saw" — same reasoning as the -1/-2
    // sentinels above, but a nullable key does it without picking a fake default per field.
    private ClassicSurfaceKey? _lastClassicSurfaceKey;

    // ONE-SHOT, not transition-based like the two flags above — see ReportServiceLocatorDiagnostics.
    private bool _serviceLocatorProbed;

    private readonly object _trackingLock = new();
    private string _account = string.Empty;
    private string _instrument = string.Empty;

    public string TrackedAccount { get { lock (_trackingLock) return _account; } }
    public string TrackedInstrument { get { lock (_trackingLock) return _instrument; } }

    public StatePublisher(ContextResolver resolver, BridgeClient bridge,
        Func<ChartStrategy?> chartStrategy, Func<ITradingManager?> tradingManager, int intervalMs)
    {
        _resolver = resolver;
        _bridge = bridge;
        _chartStrategy = chartStrategy;
        _tradingManager = tradingManager;
        _intervalMs = intervalMs;
    }

    public void Start(string account, string instrument)
    {
        lock (_trackingLock)
        {
            _account = account ?? string.Empty;
            _instrument = instrument ?? string.Empty;
        }

        _timer = new Timer(_ => Publish(false), null, 0, _intervalMs);
        SdLog.Event("State", "State publishing started every {0}ms — account='{1}' instrument='{2}'",
            _intervalMs, account, instrument);
    }

    public void UpdateTracking(string? account, string? instrument)
    {
        lock (_trackingLock)
        {
            if (!string.IsNullOrWhiteSpace(account)) _account = account!;
            if (!string.IsNullOrWhiteSpace(instrument)) _instrument = instrument!;
        }

        SdLog.Event("State", "Tracking changed — account='{0}' instrument='{1}'", TrackedAccount, TrackedInstrument);
        PublishNow();
    }

    /// <summary>Refreshes the deck on the spot instead of waiting for the next tick.</summary>
    public void PublishNow()
    {
        if (_disposed) return;
        try { Publish(true); }
        catch (Exception ex) { SdLog.Fail("StatePublish", ex, "Immediate state publish failed"); }
    }

    private void Publish(bool immediate)
    {
        if (_disposed || !_bridge.IsConnected) return;

        if (Interlocked.CompareExchange(ref _publishing, 1, 0) != 0)
        {
            // An immediate publish losing the race is not a stall: the publish already in flight is
            // milliseconds old and the timer republishes right after. Counting it would make the
            // stall detector fire on healthy bursts of fills.
            if (immediate)
            {
                SdLog.TraceEvent("StatePublish", "Immediate publish skipped — a publish is already in flight");
                return;
            }

            _consecutiveSkips++;
            // The first skip is routine; a sustained run means the deck is frozen on stale data,
            // which the trader reads as "the buttons stopped updating".
            if (_consecutiveSkips == 5 || _consecutiveSkips % 50 == 0)
            {
                SdLog.EventWarn("StatePublish",
                    "State publish stalled — {0} consecutive ticks skipped ({1}ms interval); the deck is showing stale data",
                    _consecutiveSkips, _intervalMs);
            }
            else
            {
                SdLog.TraceEvent("StatePublish", "Tick skipped — previous publish still in flight");
            }
            return;
        }

        if (_consecutiveSkips > 0)
        {
            SdLog.Event("StatePublish", "State publishing resumed after {0} skipped tick(s)", _consecutiveSkips);
            _consecutiveSkips = 0;
        }

        var sendStarted = false;
        try
        {
            var accounts = _resolver.GetAccountNames(out var accountStats);
            var securityCount = CountSecurities(_resolver.Connector, out var securitiesNull);
            ReportConnectorDiagnostics(accountStats, securitiesNull, securityCount, accounts.Count);
            ReportClassicSurfaceDiagnostics();
            ReportServiceLocatorDiagnostics();

            AdoptDefaultAccountIfNeeded(accounts);

            var portfolio = _resolver.FindPortfolio(TrackedAccount);
            var security = _resolver.FindSecurity(TrackedInstrument);

            var state = BuildState(portfolio, security, accounts);
            var task = _bridge.SendAsync(BridgeMessage.Notify("stateUpdate", state));
            sendStarted = true;
            task.ContinueWith(_ => Interlocked.Exchange(ref _publishing, 0));
        }
        catch (Exception ex)
        {
            SdLog.Fail("StatePublish", ex, "Failed to publish state for {0} / {1}", TrackedAccount, TrackedInstrument);
        }
        finally
        {
            if (!sendStarted) Interlocked.Exchange(ref _publishing, 0);
        }
    }

    /// <summary>
    /// Number of securities the connector currently knows about, or -1 when the count itself
    /// couldn't be taken.
    ///
    /// Enumerable.Count() rather than a manual foreach or ToList().Count: on a live feed
    /// Connector.Securities can run into the thousands, and this is a diagnostic running twice a
    /// second — walking the whole collection just to print its size would be the cost of the
    /// diagnosis exceeding the cost of the problem. LINQ's Count() takes the O(1) path when the
    /// runtime collection implements ICollection&lt;T&gt; and only falls back to a single full
    /// enumeration otherwise — never more than the one pass, and never materialised into a list.
    /// </summary>
    private static int CountSecurities(IDataFeedConnector? connector, out bool securitiesNull)
    {
        var securities = connector?.Securities;
        securitiesNull = securities == null;
        if (securities == null) return 0;

        try
        {
            return securities.Count();
        }
        catch (Exception ex)
        {
            // Same reasoning as the Portfolios enumeration in ContextResolver: a fault here is
            // connector-side, not a reason to lose this tick's stateUpdate.
            SdLog.TraceEvent("Security", "Securities count failed ({0}) — reporting as unavailable this tick", ex.GetType().Name);
            return -1;
        }
    }

    /// <summary>
    /// TRANSITION-BASED, NOT PER-TICK. Added while field-testing against classic ATAS: the deck's
    /// Account key stayed empty (availableAccounts=[]) while a demo account sat connected in the
    /// platform, and nothing in the log said whether Connector was null, Portfolios was empty, or
    /// portfolios existed but GetAccountNames() was silently rejecting every one of them.
    ///
    /// This runs on the 500ms publish loop, so logging the snapshot on every tick would be exactly
    /// the INFO-in-a-loop hazard CLAUDE.md calls out — hundreds of thousands of identical lines a
    /// day. But a connector that never binds must still be visible without turning TRACE on, since
    /// that is the actual failure this diagnostic exists to catch. So: log once, at a level a normal
    /// run shows, when the picture changes — connector appears/disappears, portfolio or security
    /// counts move, resolved-account count moves — and otherwise say nothing above TRACE. Same shape
    /// as _lastAmbiguousSecurityWarning in ContextResolver and _consecutiveSkips just above.
    /// </summary>
    private void ReportConnectorDiagnostics(AccountResolutionStats accounts, bool securitiesNull, int securityCount, int resolvedAccountCount)
    {
        var changed = accounts.ConnectorNull != _lastConnectorNull
            || accounts.PortfoliosNull != _lastPortfoliosNull
            || accounts.PortfolioCount != _lastPortfolioCount
            || securityCount != _lastSecurityCount
            || resolvedAccountCount != _lastResolvedAccountCount;

        if (!changed)
        {
            SdLog.TraceEvent("Connector",
                "connectorNull={0} portfolios={1}(null={2}) securities={3}(null={4}) resolvedAccounts={5}",
                accounts.ConnectorNull, accounts.PortfolioCount, accounts.PortfoliosNull, securityCount, securitiesNull, resolvedAccountCount);
            return;
        }

        _lastConnectorNull = accounts.ConnectorNull;
        _lastPortfoliosNull = accounts.PortfoliosNull;
        _lastPortfolioCount = accounts.PortfolioCount;
        _lastSecurityCount = securityCount;
        _lastResolvedAccountCount = resolvedAccountCount;

        if (accounts.ConnectorNull)
        {
            SdLog.EventWarn("Connector",
                "Connector is null — ATAS has not handed this strategy a trading context yet; no accounts, securities, positions or orders are reachable until it does");
            return;
        }

        if (accounts.PortfolioCount == 0)
        {
            SdLog.EventWarn("Connector",
                "Connector present but Portfolios reports 0 account(s) (collection null={0}), {1} security(ies) known — no account is connected on this platform, or it is not populating Portfolios yet",
                accounts.PortfoliosNull, securityCount);
            return;
        }

        if (resolvedAccountCount == 0)
        {
            SdLog.EventWarn("Connector",
                "Connector reports {0} portfolio(s) but 0 resolved into a usable account — dropped: blank AccountID={1}, IsReplay={2}, duplicate merged={3}",
                accounts.PortfolioCount, accounts.DroppedBlankAccountId, accounts.DroppedReplay, accounts.DuplicatesMerged);
            return;
        }

        SdLog.Event("Connector",
            "Connector healthy — {0} portfolio(s), {1} resolved account(s), {2} security(ies) (securities null={3})",
            accounts.PortfolioCount, resolvedAccountCount, securityCount, securitiesNull);
    }

    /// <summary>
    /// The structural facts ReportClassicSurfaceDiagnostics gates a visible log line on.
    ///
    /// DELIBERATELY EXCLUDES CONTINUOUSLY MOVING FIGURES — unrealized P&amp;L, bid/ask price,
    /// average price against a position that is not changing size. Those change every tick a market
    /// moves; if they were part of this key, "transition-based" logging would degrade into logging
    /// every tick, which is exactly the hazard this design avoids (see ReportConnectorDiagnostics
    /// just above, same reasoning). What IS in the key: nullness, identity (AccountID/Code),
    /// reference equality between ChartStrategy's and TradingManager's objects, collection counts,
    /// and realized figures (ClosedPnL, position volume) that only move on a fill.
    /// </summary>
    private readonly record struct ClassicSurfaceKey(
        bool TmNull,
        bool CsPortfolioNull, string? CsPortfolioAccountId, bool? CsPortfolioIsReal, string? CsPortfolioConnState, bool? CsPortfolioIsReplay,
        bool CsSecurityNull, string? CsSecurityCode,
        bool TmPortfolioNull, string? TmPortfolioAccountId, bool TmPortfolioSameRef,
        bool TmSecurityNull, string? TmSecurityCode, bool TmSecuritySameRef,
        int CsOrderCount, int TmOrderCount, int CsTradeCount, int TmTradeCount,
        bool TmPositionNull, string? TmPositionAccountId, string? TmPositionSecurityCode, decimal? TmPositionVolume,
        decimal CsCurrentPosition, decimal CsAveragePrice, decimal CsClosedPnL, decimal? PortfolioClosedPnL,
        bool BestBidHasPrice, bool BestAskHasPrice);

    /// <summary>
    /// Probes exactly what classic ATAS hands this strategy WITHOUT a connector — ChartStrategy.
    /// Connector is confirmed permanently null on classic ATAS (see the previous diagnostic round);
    /// this answers what is reachable instead: ChartStrategy's own Portfolio/Security/Orders/
    /// MyTrades/position figures, and — via the delegates TradeDeckStrategy passed in, because
    /// TradingManager's getter is protected on Indicator and unreachable from this class directly —
    /// the same facts from ITradingManager.
    ///
    /// THE SCOPE QUESTION IS THE POINT. If ChartStrategy.Orders / TradingManager.Orders only ever
    /// show orders this strategy itself placed, the deck can never see (or protect against) an order
    /// placed by hand in the DOM or Chart Trader, on this account or another. That determines whether
    /// the safety macro can even see what it is supposed to guard.
    ///
    /// TRANSITION-BASED, NOT PER-TICK, same as ReportConnectorDiagnostics: this runs on the 500ms
    /// publish loop, and CLAUDE.md is explicit that one INFO/WARN line in a 500ms loop is hundreds of
    /// thousands of lines a day. A visible line is logged only when ClassicSurfaceKey changes;
    /// otherwise a single condensed TraceEvent line covers the tick.
    ///
    /// EVERYTHING BELOW IS DEFENSIVE ON PURPOSE. Connector/TradingManager events do not arrive on the
    /// chart thread, and per CLAUDE.md's constraint for this probe, nothing here may cost the tick's
    /// real stateUpdate: every collection is walked at most once, capped at 5 sample entries, and the
    /// whole method is wrapped so a fault anywhere becomes a TRACE line, never a lost publish.
    /// </summary>
    private void ReportClassicSurfaceDiagnostics()
    {
        try
        {
            var strategy = _chartStrategy();
            var tm = _tradingManager();

            var csPortfolio = strategy?.Portfolio;
            var csSecurity = strategy?.Security;
            var csPortfolioIsReplay = SafeIsReplay(csPortfolio);

            var tmPortfolio = tm?.Portfolio;
            var tmSecurity = tm?.Security;
            var tmPosition = tm?.Position;

            // THE FACT THAT MATTERS: is TradingManager a second, independent context, or just a
            // mirror of the chart's own Portfolio/Security? Guarded against the null-null case, where
            // ReferenceEquals(null, null) is trivially true but would misreport "same object".
            var tmPortfolioSameRef = tmPortfolio != null && csPortfolio != null && ReferenceEquals(tmPortfolio, csPortfolio);
            var tmSecuritySameRef = tmSecurity != null && csSecurity != null && ReferenceEquals(tmSecurity, csSecurity);

            // Four independent collections, one pass each — the whole point of this probe is to see
            // whether they even agree with one another.
            var csOrders = SampleAndCount(strategy?.Orders, 5, out var csOrderCount);
            var tmOrders = SampleAndCount(tm?.Orders, 5, out var tmOrderCount);
            var csTrades = SampleAndCount(strategy?.MyTrades, 5, out var csTradeCount);
            var tmTrades = SampleAndCount(tm?.MyTrades, 5, out var tmTradeCount);

            var bestBid = strategy?.BestBid;
            var bestAsk = strategy?.BestAsk;
            var bestBidHasPrice = bestBid != null && bestBid.Price > 0m;
            var bestAskHasPrice = bestAsk != null && bestAsk.Price > 0m;

            var key = new ClassicSurfaceKey(
                TmNull: tm == null,
                CsPortfolioNull: csPortfolio == null, CsPortfolioAccountId: csPortfolio?.AccountID,
                CsPortfolioIsReal: csPortfolio?.IsRealAccount, CsPortfolioConnState: csPortfolio?.ConnectionState?.ToString(),
                CsPortfolioIsReplay: csPortfolioIsReplay,
                CsSecurityNull: csSecurity == null, CsSecurityCode: csSecurity?.Code,
                TmPortfolioNull: tmPortfolio == null, TmPortfolioAccountId: tmPortfolio?.AccountID, TmPortfolioSameRef: tmPortfolioSameRef,
                TmSecurityNull: tmSecurity == null, TmSecurityCode: tmSecurity?.Code, TmSecuritySameRef: tmSecuritySameRef,
                CsOrderCount: csOrderCount, TmOrderCount: tmOrderCount, CsTradeCount: csTradeCount, TmTradeCount: tmTradeCount,
                TmPositionNull: tmPosition == null, TmPositionAccountId: tmPosition?.Portfolio?.AccountID,
                TmPositionSecurityCode: tmPosition?.Security?.Code, TmPositionVolume: tmPosition?.Volume,
                CsCurrentPosition: strategy?.CurrentPosition ?? 0m, CsAveragePrice: strategy?.AveragePrice ?? 0m,
                CsClosedPnL: strategy?.ClosedPnL ?? 0m, PortfolioClosedPnL: csPortfolio?.ClosedPnL,
                BestBidHasPrice: bestBidHasPrice, BestAskHasPrice: bestAskHasPrice);

            if (key == _lastClassicSurfaceKey)
            {
                SdLog.TraceEvent("ClassicSurface",
                    "tmNull={0} csPortfolio={1} csSecurity={2} tmPortfolio={3}(sameRef={4}) tmSecurity={5}(sameRef={6}) " +
                    "orders(cs={7},tm={8}) trades(cs={9},tm={10}) tmPosition={11} strategy(pos={12},avgPx={13},openPnl={14},closedPnl={15}) " +
                    "portfolioPnl(open={16},closed={17}) bestBid={18} bestAsk={19}",
                    key.TmNull, PortfolioSummary(csPortfolio), SecuritySummary(csSecurity),
                    PortfolioSummary(tmPortfolio), tmPortfolioSameRef, SecuritySummary(tmSecurity), tmSecuritySameRef,
                    csOrderCount, tmOrderCount, csTradeCount, tmTradeCount, PositionSummary(tmPosition),
                    strategy?.CurrentPosition, strategy?.AveragePrice, strategy?.OpenPnL, strategy?.ClosedPnL,
                    csPortfolio?.OpenPnL, csPortfolio?.ClosedPnL, MarketDataSummary(bestBid), MarketDataSummary(bestAsk));
                return;
            }

            _lastClassicSurfaceKey = key;

            // 1. Is TradingManager reachable at all.
            if (tm == null)
            {
                SdLog.EventWarn("ClassicSurface",
                    "TradingManager is null — Position/Orders/MyTrades/GetSecurityTradingOptions on the trading manager are unreachable this tick");
            }
            else
            {
                SdLog.Event("ClassicSurface", "TradingManager is non-null — reachable via the protected Indicator.TradingManager getter");
            }

            // 2. ChartStrategy.Portfolio / .Security.
            if (csPortfolio == null || csSecurity == null)
            {
                SdLog.EventWarn("ClassicSurface",
                    "ChartStrategy.Portfolio={0} ChartStrategy.Security={1}", PortfolioSummary(csPortfolio), SecuritySummary(csSecurity));
            }
            else
            {
                SdLog.Event("ClassicSurface",
                    "ChartStrategy.Portfolio={0} ChartStrategy.Security={1}", PortfolioSummary(csPortfolio), SecuritySummary(csSecurity));
            }

            // 3. TradingManager.Portfolio / .Security — and whether they are the SAME OBJECT as the
            // chart's. That reference-equality is the answer to "second context, or just a mirror?".
            if (tm != null)
            {
                SdLog.Event("ClassicSurface",
                    "TradingManager.Portfolio={0} (sameRefAsChartPortfolio={1}) TradingManager.Security={2} (sameRefAsChartSecurity={3})",
                    PortfolioSummary(tmPortfolio), tmPortfolioSameRef, SecuritySummary(tmSecurity), tmSecuritySameRef);
            }

            // 4./5. Orders / MyTrades scope — THE SINGLE MOST IMPORTANT QUESTION THIS PROBE ANSWERS:
            // does either surface show orders/trades placed outside this strategy (DOM, Chart Trader,
            // another instrument or account)?
            SdLog.Event("ClassicSurface",
                "orders: chart={0} tradingManager={1} | trades: chart={2} tradingManager={3}",
                csOrderCount, tmOrderCount, csTradeCount, tmTradeCount);

            LogOrderSamples("chart.Orders", csOrders, csOrderCount);
            LogOrderSamples("tradingManager.Orders", tmOrders, tmOrderCount);
            LogTradeSamples("chart.MyTrades", csTrades, csTradeCount);
            LogTradeSamples("tradingManager.MyTrades", tmTrades, tmTradeCount);

            // 6. TradingManager.Position.
            SdLog.Event("ClassicSurface", "TradingManager.Position={0}", PositionSummary(tmPosition));

            // 7. Strategy-internal figures vs the account ones — do they ever disagree?
            SdLog.Event("ClassicSurface",
                "strategy: CurrentPosition={0} AveragePrice={1} OpenPnL={2} ClosedPnL={3} | ChartStrategy.Portfolio: OpenPnL={4} ClosedPnL={5}",
                strategy?.CurrentPosition, strategy?.AveragePrice, strategy?.OpenPnL, strategy?.ClosedPnL,
                csPortfolio?.OpenPnL, csPortfolio?.ClosedPnL);

            // 8. BestBid / BestAsk — the NO_MARKET_DATA source.
            if (!bestBidHasPrice || !bestAskHasPrice)
            {
                SdLog.EventWarn("ClassicSurface",
                    "BestBid={0} BestAsk={1} — at least one side has no usable price (NO_MARKET_DATA)",
                    MarketDataSummary(bestBid), MarketDataSummary(bestAsk));
            }
            else
            {
                SdLog.Event("ClassicSurface", "BestBid={0} BestAsk={1}", MarketDataSummary(bestBid), MarketDataSummary(bestAsk));
            }
        }
        catch (Exception ex)
        {
            // A probe that throws must not cost the tick's real stateUpdate — CLAUDE.md's rule and
            // this method's own doc comment. Everything above already guards its own risky spots
            // (SampleAndCount, SafeIsReplay); this is the last-resort net around the rest.
            SdLog.TraceEvent("ClassicSurface", "Diagnostic probe faulted ({0}) — skipped this tick", ex.GetType().Name);
        }
    }

    /// <summary>
    /// Walks an ATAS collection exactly once, counting every item while keeping only the first
    /// <paramref name="maxSamples"/> for detail logging. Capped and single-pass per CLAUDE.md's rule
    /// for this probe: never enumerate a potentially large collection twice, never hold more than a
    /// handful of entries in memory just to describe it.
    /// </summary>
    private static List<T> SampleAndCount<T>(IEnumerable<T>? source, int maxSamples, out int count)
    {
        var samples = new List<T>(maxSamples);
        count = 0;
        if (source == null) return samples;

        try
        {
            foreach (var item in source)
            {
                count++;
                if (samples.Count < maxSamples) samples.Add(item);
            }
        }
        catch (Exception ex)
        {
            // Partial count kept rather than discarded — it is still more than "unknown", and the
            // scope question this probe exists to answer is better served by "at least N" than by
            // silence.
            SdLog.TraceEvent("ClassicSurface", "Enumeration faulted after {0} item(s) seen ({1}) — reporting partial", count, ex.GetType().Name);
        }

        return samples;
    }

    private static void LogOrderSamples(string label, List<Order> samples, int totalCount)
    {
        if (totalCount == 0) return;
        for (var i = 0; i < samples.Count; i++)
        {
            var order = samples[i];
            if (order == null) continue;
            SdLog.Event("ClassicSurface", "{0}[{1}/{2}]: AccountID={3} Code={4} Comment={5}",
                label, i + 1, totalCount,
                order.Portfolio?.AccountID ?? order.AccountID ?? "?",
                order.Security?.Code ?? order.SecurityId ?? "?",
                order.Comment ?? string.Empty);
        }
    }

    private static void LogTradeSamples(string label, List<MyTrade> samples, int totalCount)
    {
        if (totalCount == 0) return;
        for (var i = 0; i < samples.Count; i++)
        {
            var trade = samples[i];
            if (trade == null) continue;
            SdLog.Event("ClassicSurface", "{0}[{1}/{2}]: AccountID={3} Code={4}",
                label, i + 1, totalCount,
                trade.Portfolio?.AccountID ?? trade.AccountID ?? "?",
                trade.Security?.Code ?? trade.SecurityId ?? "?");
        }
    }

    private static string PortfolioSummary(Portfolio? p)
    {
        if (p == null) return "null";
        return string.Format(CultureInfo.InvariantCulture,
            "AccountID={0} IsRealAccount={1} ConnectionState={2} IsReplay={3}",
            p.AccountID ?? "?", p.IsRealAccount, p.ConnectionState?.ToString() ?? "null",
            SafeIsReplay(p) is { } replay ? replay.ToString() : "?");
    }

    private static string SecuritySummary(Security? s)
    {
        if (s == null) return "null";
        return string.Format(CultureInfo.InvariantCulture,
            "Code={0} Instrument={1} TickSize={2} TickCost={3} Expiration={4:yyyy-MM-dd}",
            s.Code ?? "?", s.Instrument ?? "?", s.TickSize, s.TickCost, s.Expiration);
    }

    private static string PositionSummary(Position? p)
    {
        if (p == null) return "null";
        return string.Format(CultureInfo.InvariantCulture,
            "Volume={0} AveragePrice={1} AccountID={2} Code={3}",
            p.Volume, p.AveragePrice, p.Portfolio?.AccountID ?? "?", p.Security?.Code ?? "?");
    }

    private static string MarketDataSummary(MarketDataArg? m)
    {
        if (m == null) return "null";
        return string.Format(CultureInfo.InvariantCulture, "Price={0} Time={1:HH:mm:ss.fff}", m.Price, m.Time);
    }

    /// <summary>
    /// IsReplay() is an ATAS extension method, called defensively: everything on this probe path
    /// must survive a broker's Portfolio implementation throwing out of a property it owns, per
    /// CLAUDE.md's constraint that nothing here may cost the tick.
    /// </summary>
    private static bool? SafeIsReplay(Portfolio? p)
    {
        if (p == null) return null;
        try { return p.IsReplay(); }
        catch (Exception ex)
        {
            SdLog.TraceEvent("ClassicSurface", "Portfolio.IsReplay() faulted ({0})", ex.GetType().Name);
            return null;
        }
    }

    /// <summary>
    /// ONE-SHOT, run once on the first tick after start, never again — see the field comment next to
    /// <see cref="_serviceLocatorProbed"/>. Everything below reflects over already-loaded assemblies
    /// to answer one question: can a classic ChartStrategy, whose Connector is confirmed permanently
    /// null (see ReportClassicSurfaceDiagnostics), reach the SAME process-wide connector registry a
    /// native ATAS panel uses, through the host's own CommonServiceLocator?
    ///
    /// WHY REFLECTION AND NEVER A &lt;Reference&gt;: OFT.Platform.Core's ITradingCore,
    /// IConnectorsManager, CoreProvider, and CommonServiceLocator itself are host internals, not
    /// ATAS's documented plugin surface. A compile-time reference to any of them means the next ATAS
    /// build that renames or removes one stops THIS STRATEGY FROM LOADING AT ALL — a dead cockpit
    /// over a diagnostic the trader never asked for. Resolved by assembly simple name and full type
    /// name instead: the worst case degrades to a logged failure and the chart-scoped behaviour this
    /// strategy already has, exactly as CLAUDE.md requires of every reflection probe in this file.
    ///
    /// WHAT A SUCCESSFUL STEP 6 MEANS: if GetConnector(portfolio) returns a connector exposing more
    /// than the one portfolio the chart is scoped to, multi-account enumeration, visibility into
    /// orders/trades placed on other accounts, and account-wide safety enforcement are all back on
    /// the table for the ATAS port — the whole reason this probe exists. A failure anywhere just
    /// means the port stays chart-scoped by design, with this log as the record of why.
    ///
    /// EVERY STEP OWNS ITS OWN TRY/CATCH. Per CLAUDE.md's constraint on this probe, nothing here may
    /// block or throw out of the publication path — a probe that costs the tick's real stateUpdate
    /// would be worse than no probe. Logged at Event/EventWarn because this runs once, not per tick.
    /// </summary>
    private void ReportServiceLocatorDiagnostics()
    {
        if (_serviceLocatorProbed) return;
        _serviceLocatorProbed = true;

        try
        {
            var assemblies = AppDomain.CurrentDomain.GetAssemblies();

            // Reported once regardless of what follows: this is what is actually in the process,
            // independent of whether the service locator turns out to be usable.
            var matching = assemblies
                .Select(SafeAssemblyName)
                .Where(n => n != null && (n!.StartsWith("OFT.Platform", StringComparison.OrdinalIgnoreCase)
                                           || string.Equals(n, "CommonServiceLocator", StringComparison.OrdinalIgnoreCase)))
                .ToList();
            SdLog.Event("ServiceLocator", "Loaded assemblies matching OFT.Platform*/CommonServiceLocator: {0}",
                matching.Count == 0 ? "(none)" : string.Join(", ", matching));

            // Step 1: is CommonServiceLocator loaded, and has something initialised it?
            var serviceLocatorType = FindType(assemblies, "CommonServiceLocator", "CommonServiceLocator.ServiceLocator");
            if (serviceLocatorType == null)
            {
                SdLog.EventWarn("ServiceLocator", "Step 1: CommonServiceLocator.ServiceLocator type not found — assembly not loaded in this process");
                return;
            }

            bool? isSet = null;
            try
            {
                var isSetProp = serviceLocatorType.GetProperty("IsLocationProviderSet", BindingFlags.Public | BindingFlags.Static);
                isSet = isSetProp?.GetValue(null) as bool?;
                SdLog.Event("ServiceLocator", "Step 1: CommonServiceLocator loaded — IsLocationProviderSet={0}", isSet);
            }
            catch (Exception ex)
            {
                SdLog.EventWarn("ServiceLocator", "Step 1: reading IsLocationProviderSet faulted: {0} — {1}", ex.GetType().Name, ex.Message);
            }

            if (isSet != true)
            {
                SdLog.EventWarn("ServiceLocator", "Stopping here: location provider is not set, ServiceLocator.Current would throw on any host that hasn't initialised it");
                return;
            }

            object? current;
            try
            {
                var currentProp = serviceLocatorType.GetProperty("Current", BindingFlags.Public | BindingFlags.Static);
                current = currentProp?.GetValue(null);
                if (current == null)
                {
                    SdLog.EventWarn("ServiceLocator", "ServiceLocator.Current returned null despite IsLocationProviderSet=true");
                    return;
                }
                SdLog.Event("ServiceLocator", "ServiceLocator.Current resolved to {0}", current.GetType().FullName);
            }
            catch (Exception ex)
            {
                SdLog.EventWarn("ServiceLocator", "ServiceLocator.Current threw: {0} — {1}", ex.GetType().Name, ex.Message);
                return;
            }

            var getInstance = current.GetType().GetMethod("GetInstance", new[] { typeof(Type) });
            if (getInstance == null)
            {
                SdLog.EventWarn("ServiceLocator", "IServiceLocator.GetInstance(Type) not found by reflection on {0}", current.GetType().FullName);
                return;
            }

            // Step 2: ITradingCore.
            var tradingCore = TryGetInstance(assemblies, current, getInstance, "OFT.Platform.Core",
                "OFT.Platform.Core.Providers.ITradingCore", 2, "ITradingCore");

            // Step 3: IConnectorsManager.
            var connectorsManager = TryGetInstance(assemblies, current, getInstance, "OFT.Platform.Core",
                "OFT.Platform.Core.Managers.Connections.IConnectorsManager", 3, "IConnectorsManager");

            // Step 4: CoreProvider, then its TradingCore property — used to fill in step 2 only if
            // that came back empty. A locator can register a concrete CoreProvider without also
            // registering every interface it exposes, so this is a second path to the same object,
            // not a redundant one.
            var coreProvider = TryGetInstance(assemblies, current, getInstance, "OFT.Platform.Core",
                "OFT.Platform.Core.Providers.CoreProvider", 4, "CoreProvider");
            if (coreProvider != null)
            {
                try
                {
                    var tradingCoreProp = coreProvider.GetType().GetProperty("TradingCore", BindingFlags.Public | BindingFlags.Instance);
                    var fromProvider = tradingCoreProp?.GetValue(coreProvider);
                    SdLog.Event("ServiceLocator", "Step 4: CoreProvider.TradingCore = {0}",
                        fromProvider == null ? "null" : fromProvider.GetType().FullName);
                    tradingCore ??= fromProvider;
                }
                catch (Exception ex)
                {
                    SdLog.EventWarn("ServiceLocator", "Step 4: reading CoreProvider.TradingCore faulted: {0} — {1}", ex.GetType().Name, ex.Message);
                }
            }

            // Step 5/6: GetConnector(Portfolio), called with the SAME live Portfolio the rest of this
            // class already tracks (TradingManager.Portfolio) rather than a freshly-constructed one —
            // the question is whether the host recognises THIS chart's account, not any portfolio.
            if (tradingCore == null)
            {
                SdLog.EventWarn("ServiceLocator", "Step 5: no ITradingCore instance obtained by any path — cannot call GetConnector(Portfolio)");
            }
            else
            {
                var portfolio = _tradingManager()?.Portfolio;
                if (portfolio == null)
                {
                    SdLog.EventWarn("ServiceLocator", "Step 5: TradingManager.Portfolio is null — cannot call GetConnector(Portfolio) yet");
                }
                else
                {
                    try
                    {
                        var getConnector = tradingCore.GetType().GetMethod("GetConnector", new[] { typeof(Portfolio) });
                        if (getConnector == null)
                        {
                            SdLog.EventWarn("ServiceLocator", "Step 5: ITradingCore.GetConnector(Portfolio) not found by reflection on {0}", tradingCore.GetType().FullName);
                        }
                        else
                        {
                            var connectorObj = getConnector.Invoke(tradingCore, new object?[] { portfolio });
                            if (connectorObj == null)
                            {
                                SdLog.EventWarn("ServiceLocator", "Step 5: ITradingCore.GetConnector(portfolio={0}) returned null", portfolio.AccountID);
                            }
                            else
                            {
                                SdLog.Event("ServiceLocator", "Step 5: ITradingCore.GetConnector(portfolio={0}) returned {1}",
                                    portfolio.AccountID, connectorObj.GetType().FullName);
                                ReportConnectorFromServiceLocator(connectorObj, "ITradingCore.GetConnector");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        var real = ex is TargetInvocationException { InnerException: { } inner } ? inner : ex;
                        SdLog.EventWarn("ServiceLocator", "Step 5: GetConnector(Portfolio) threw: {0} — {1}", real.GetType().Name, real.Message);
                    }
                }
            }

            // Step 7: IConnectorsManager.Connectors.
            if (connectorsManager == null)
            {
                SdLog.EventWarn("ServiceLocator", "Step 7: no IConnectorsManager instance obtained — cannot enumerate Connectors");
                return;
            }

            try
            {
                var connectorsProp = connectorsManager.GetType().GetProperty("Connectors", BindingFlags.Public | BindingFlags.Instance);
                if (connectorsProp?.GetValue(connectorsManager) is not IEnumerable connectorsValue)
                {
                    SdLog.EventWarn("ServiceLocator", "Step 7: IConnectorsManager.Connectors is null or not enumerable");
                    return;
                }

                var count = 0;
                foreach (var entry in connectorsValue)
                {
                    count++;
                    if (entry == null) { SdLog.Event("ServiceLocator", "Step 7: Connectors[{0}] = null", count); continue; }
                    SdLog.Event("ServiceLocator", "Step 7: Connectors[{0}] = {1} (portfolios={2})",
                        count, entry.GetType().FullName, TryCountPortfolios(entry));
                }
                SdLog.Event("ServiceLocator", "Step 7: IConnectorsManager.Connectors has {0} entr{1}", count, count == 1 ? "y" : "ies");
            }
            catch (Exception ex)
            {
                SdLog.EventWarn("ServiceLocator", "Step 7: enumerating Connectors faulted: {0} — {1}", ex.GetType().Name, ex.Message);
            }
        }
        catch (Exception ex)
        {
            // Last-resort net around the whole probe, same reasoning as ReportClassicSurfaceDiagnostics'
            // own outer catch: a diagnostic must never be the reason a real stateUpdate is lost.
            SdLog.EventWarn("ServiceLocator", "Probe faulted outside any single step: {0} — {1}", ex.GetType().Name, ex.Message);
        }
    }

    /// <summary>
    /// Resolves one type by assembly simple name + full type name, then calls
    /// IServiceLocator.GetInstance(Type) on it — logging step-numbered success or failure either way,
    /// per the "report each step" requirement this probe exists to satisfy.
    /// </summary>
    private static object? TryGetInstance(Assembly[] assemblies, object locator, MethodInfo getInstance,
        string assemblySimpleName, string fullTypeName, int stepNumber, string label)
    {
        var type = FindType(assemblies, assemblySimpleName, fullTypeName);
        if (type == null)
        {
            SdLog.EventWarn("ServiceLocator", "Step {0}: {1} type ({2}) not found in loaded assemblies", stepNumber, label, fullTypeName);
            return null;
        }

        try
        {
            var instance = getInstance.Invoke(locator, new object?[] { type });
            SdLog.Event("ServiceLocator", "Step {0}: GetInstance({1}) = {2}", stepNumber, label,
                instance == null ? "null" : instance.GetType().FullName);
            return instance;
        }
        catch (Exception ex)
        {
            // TargetInvocationException wraps whatever the locator itself threw — typically
            // ActivationException when nothing is registered for the type. Unwrapped so the log shows
            // the real cause instead of "reflection failed".
            var real = ex is TargetInvocationException { InnerException: { } inner } ? inner : ex;
            SdLog.EventWarn("ServiceLocator", "Step {0}: GetInstance({1}) threw {2} — {3}", stepNumber, label, real.GetType().Name, real.Message);
            return null;
        }
    }

    private static Type? FindType(Assembly[] assemblies, string assemblySimpleName, string fullTypeName)
    {
        foreach (var assembly in assemblies)
        {
            if (!string.Equals(SafeAssemblyName(assembly), assemblySimpleName, StringComparison.OrdinalIgnoreCase))
                continue;

            try
            {
                var type = assembly.GetType(fullTypeName, throwOnError: false);
                if (type != null) return type;
            }
            catch (Exception ex)
            {
                SdLog.TraceEvent("ServiceLocator", "Assembly.GetType({0}) on {1} faulted: {2}", fullTypeName, assemblySimpleName, ex.GetType().Name);
            }
        }

        return null;
    }

    private static string? SafeAssemblyName(Assembly assembly)
    {
        try { return assembly.GetName().Name; }
        catch { return null; }
    }

    /// <summary>
    /// Step 6 — the single most important output of this probe (see the method doc comment on
    /// ReportServiceLocatorDiagnostics). Casts first, reflects only as a fallback: ATAS.DataFeedsCore.
    /// IDataFeedConnector is already an ordinary compile-time reference in this project (see
    /// ContextResolver.Connector), so if the host hands back that exact interface — which the probe's
    /// premise says it should — the direct cast is exact and avoids a second layer of reflection on
    /// top of the one that got us here.
    /// </summary>
    private static void ReportConnectorFromServiceLocator(object connectorObj, string source)
    {
        if (connectorObj is IDataFeedConnector connector)
        {
            var portfolioCount = SafeCountPortfolios(connector.Portfolios, out var accountIds);
            var securityCount = SafeCount(connector.Securities);
            var orderCount = SafeCount(connector.Orders);
            var positionCount = SafeCount(connector.Positions);
            var tradeCount = SafeCount(connector.MyTrades);

            SdLog.Event("ServiceLocator",
                "Step 6: {0} — Portfolios={1} Securities={2} Orders={3} Positions={4} MyTrades={5}",
                source, portfolioCount, securityCount, orderCount, positionCount, tradeCount);

            SdLog.Event("ServiceLocator", "Step 6: portfolio AccountIDs on this connector: {0}",
                accountIds.Count == 0 ? "(none)" : string.Join(", ", accountIds));
            return;
        }

        SdLog.EventWarn("ServiceLocator",
            "Step 6: {0} returned {1}, which does not cast to ATAS.DataFeedsCore.IDataFeedConnector — falling back to reflection",
            source, connectorObj.GetType().FullName);
        ReportConnectorFromServiceLocatorByReflection(connectorObj);
    }

    private static void ReportConnectorFromServiceLocatorByReflection(object connectorObj)
    {
        try
        {
            var type = connectorObj.GetType();
            var portfoliosValue = type.GetProperty("Portfolios", BindingFlags.Public | BindingFlags.Instance)?.GetValue(connectorObj) as IEnumerable;

            var portfolioCount = 0;
            var accountIds = new List<string>();
            if (portfoliosValue != null)
            {
                foreach (var p in portfoliosValue)
                {
                    portfolioCount++;
                    if (p?.GetType().GetProperty("AccountID")?.GetValue(p) is string id && !string.IsNullOrWhiteSpace(id))
                        accountIds.Add(id);
                }
            }

            SdLog.Event("ServiceLocator",
                "Step 6 (reflection fallback): Portfolios={0} Securities={1} Orders={2} Positions={3} MyTrades={4}",
                portfolioCount,
                ReflectCount(type, connectorObj, "Securities"), ReflectCount(type, connectorObj, "Orders"),
                ReflectCount(type, connectorObj, "Positions"), ReflectCount(type, connectorObj, "MyTrades"));
            SdLog.Event("ServiceLocator", "Step 6 (reflection fallback): portfolio AccountIDs: {0}",
                accountIds.Count == 0 ? "(none)" : string.Join(", ", accountIds));
        }
        catch (Exception ex)
        {
            SdLog.EventWarn("ServiceLocator", "Step 6: reflection fallback faulted: {0} — {1}", ex.GetType().Name, ex.Message);
        }
    }

    private static int ReflectCount(Type type, object target, string propertyName)
    {
        try
        {
            var value = type.GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance)?.GetValue(target) as IEnumerable;
            if (value == null) return -1;
            var count = 0;
            foreach (var _ in value) count++;
            return count;
        }
        catch (Exception ex)
        {
            SdLog.TraceEvent("ServiceLocator", "Reflected count of {0} faulted: {1}", propertyName, ex.GetType().Name);
            return -1;
        }
    }

    /// <summary>Step 7 per-connector portfolio count — same cast-first-reflect-as-fallback shape as step 6.</summary>
    private static int TryCountPortfolios(object connectorEntry)
    {
        if (connectorEntry is IDataFeedConnector connector)
            return SafeCountPortfolios(connector.Portfolios, out _);

        try
        {
            return ReflectCount(connectorEntry.GetType(), connectorEntry, "Portfolios");
        }
        catch (Exception ex)
        {
            SdLog.TraceEvent("ServiceLocator", "Portfolio count on connector entry faulted: {0}", ex.GetType().Name);
            return -1;
        }
    }

    private static int SafeCount<T>(IEnumerable<T>? source)
    {
        if (source == null) return -1;
        try { return source.Count(); }
        catch (Exception ex)
        {
            SdLog.TraceEvent("ServiceLocator", "Count() faulted on IEnumerable<{0}>: {1}", typeof(T).Name, ex.GetType().Name);
            return -1;
        }
    }

    private static int SafeCountPortfolios(IEnumerable<Portfolio>? source, out List<string> accountIds)
    {
        accountIds = new List<string>();
        if (source == null) return -1;

        var count = 0;
        try
        {
            foreach (var p in source)
            {
                count++;
                var id = p?.AccountID;
                if (!string.IsNullOrWhiteSpace(id)) accountIds.Add(id!);
            }
        }
        catch (Exception ex)
        {
            SdLog.TraceEvent("ServiceLocator", "Portfolios enumeration faulted after {0} seen: {1}", count, ex.GetType().Name);
        }

        return count;
    }

    /// <summary>
    /// Falls back to the first connected account when the tracked one is gone.
    ///
    /// A default, never a choice. The bridge pushes setAccount on every connection precisely so this
    /// path is not the one that decides: on 2026-08-21 an unpersisted account left the NinjaTrader
    /// add-on enforcing on an account nobody was trading, while the deck showed the macro armed.
    /// </summary>
    private void AdoptDefaultAccountIfNeeded(List<string> accounts)
    {
        lock (_trackingLock)
        {
            if (accounts.Count == 0)
            {
                if (_account.Length == 0) return;
                _account = string.Empty;
                SdLog.Event("State", "Tracked account cleared because no portfolio is connected.");
                return;
            }

            if (_account.Length > 0 && accounts.Any(a => string.Equals(a, _account, StringComparison.OrdinalIgnoreCase)))
                return;

            _account = accounts[0];
            SdLog.EventWarn("State",
                "Account auto-selected from the connected list: {0} — nobody chose it; the bridge should push setAccount",
                _account);
        }
    }

    private Dictionary<string, object?> BuildState(Portfolio? portfolio, Security? security, List<string> accounts)
    {
        var account = new Dictionary<string, object?>
        {
            ["name"] = portfolio?.AccountID ?? TrackedAccount,
            ["connected"] = portfolio != null && portfolio.ConnectionState == ConnectionStates.Connected,
        };

        AddAccountPnl(account, portfolio);
        AddAccountBalance(account, portfolio);

        Dictionary<string, object?>? instrument = null;
        Dictionary<string, object?>? position = null;

        if (security != null)
        {
            var pointValue = ContextResolver.PointValue(security);
            instrument = new Dictionary<string, object?>
            {
                ["name"] = ContextResolver.InstrumentName(security),
                ["lastPrice"] = ContextResolver.MarketPrice(security),
                ["tickSize"] = security.TickSize,
                // 1.0 rather than 0 for the DISPLAY value only, matching the NinjaTrader add-on: the
                // deck divides by it to show a P&L per point. The journal's own pointValue is a
                // different field and is omitted rather than faked — see ExecutionRecorder.
                ["pointValue"] = pointValue > 0m ? pointValue : 1.0m,
            };

            if (portfolio != null) position = BuildPosition(portfolio, security);
        }

        return new Dictionary<string, object?>
        {
            ["connected"] = true,
            ["account"] = account,
            ["instrument"] = instrument,
            ["position"] = position,
            ["availableAccounts"] = accounts,
            // No `trend` block and no `copier` block: both are out of scope. The protocol says an
            // absent trend leaves the bridge's last value in place and the key shows NO DATA, which
            // is exactly true here.
        };
    }

    private Dictionary<string, object?> BuildPosition(Portfolio portfolio, Security security)
    {
        var position = _resolver.FindPosition(portfolio, security);
        var stops = _resolver.FindStopOrders(portfolio, security);
        var targets = _resolver.FindTargetOrders(portfolio, security);
        var active = _resolver.FindActiveOrders(portfolio, security);

        if (position == null)
        {
            // EVERY FIELD IS PRESENT ON BOTH BRANCHES. The protocol declares them required, and
            // omitting the counts made the protection signature flip between "-" and a number on
            // every open and close, logging a transition that had not happened.
            return new Dictionary<string, object?>
            {
                ["exists"] = false,
                ["direction"] = "Flat",
                ["quantity"] = 0,
                ["averagePrice"] = 0m,
                ["unrealizedPnl"] = 0m,
                ["hasStopOrder"] = false,
                ["stopPrice"] = 0m,
                ["stopOrderCount"] = 0,
                ["hasTargetOrder"] = false,
                ["targetPrice"] = 0m,
                ["targetOrderCount"] = 0,
                ["activeOrderCount"] = active.Count,
            };
        }

        var isLong = position.Volume > 0m;

        // The stop that actually protects the position is the TIGHTEST one — the highest under a
        // long, the lowest over a short — not whichever the connector listed first. Showing an
        // arbitrary one tells the trader he has more room than he has.
        var tightestStop = stops.Count == 0 ? null : isLong ? stops[^1] : stops[0];
        var nearestTarget = targets.Count == 0 ? null : isLong ? targets[0] : targets[^1];

        return new Dictionary<string, object?>
        {
            ["exists"] = true,
            ["direction"] = isLong ? "Long" : "Short",
            ["quantity"] = (int)Math.Abs(position.Volume),
            ["averagePrice"] = position.AveragePrice,
            ["unrealizedPnl"] = position.UnrealizedPnL,
            ["hasStopOrder"] = stops.Count > 0,
            ["stopPrice"] = tightestStop?.TriggerPrice ?? 0m,
            ["stopOrderCount"] = stops.Count,
            ["hasTargetOrder"] = targets.Count > 0,
            ["targetPrice"] = nearestTarget?.Price ?? 0m,
            ["targetOrderCount"] = targets.Count,
            ["activeOrderCount"] = active.Count,
        };
    }

    /// <summary>
    /// Account P&amp;L, which the bridge's safety macro measures the daily loss from.
    ///
    /// ClosedPnL, NEVER TotalClosedPnL. The second one accumulates across sessions; the first
    /// carries a date (ClosedPnlDate). The daily loss limit is measured "since the start-of-day
    /// P&amp;L", so feeding it a lifetime figure would trip the cap at the open on any account with
    /// a losing history, and the macro would then refuse everything, for ever.
    /// </summary>
    private static void AddAccountPnl(Dictionary<string, object?> account, Portfolio? portfolio)
    {
        if (portfolio == null)
        {
            account["realizedPnl"] = 0m;
            account["unrealizedPnl"] = 0m;
            // pnlAvailable=false tells the bridge the P&L rules have no data to work with, which
            // surfaces on the deck instead of failing silently.
            account["pnlAvailable"] = false;
            return;
        }

        account["realizedPnl"] = portfolio.ClosedPnL;
        account["unrealizedPnl"] = portfolio.OpenPnL;
        account["pnlAvailable"] = true;
    }

    /// <summary>
    /// Account cash value, forwarded so the Bitlearn journal can start from the real capital.
    ///
    /// Without it the journal opens at zero and every percentage it computes is meaningless — a
    /// gain of 200 on nothing is not a percentage. Kept separate from the P&amp;L because it fails
    /// independently: losing the balance must never cost the P&amp;L the loss limits depend on.
    ///
    /// The field is OMITTED rather than sent as 0 when no source has a usable value. The bridge only
    /// overwrites its cash value when the field is a number, so an omission leaves the journal on
    /// the last real capital it knew instead of resetting it to nothing.
    /// </summary>
    private static void AddAccountBalance(Dictionary<string, object?> account, Portfolio? portfolio)
    {
        if (portfolio == null) return;

        if (portfolio.Balance != 0m) { account["cashValue"] = portfolio.Balance; return; }
        if (portfolio.BalanceAvailable is { } available && available != 0m) { account["cashValue"] = available; return; }

        SdLog.TraceEvent("Account", "No usable cash value for {0} — the Bitlearn journal keeps its current capital",
            portfolio.AccountID ?? "?");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer?.Dispose();
        _timer = null;
    }
}
