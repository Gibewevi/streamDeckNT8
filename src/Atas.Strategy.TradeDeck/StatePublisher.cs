using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;

using ATAS.DataFeedsCore;
// ConnectionStates lives here, NOT in StockSharp.Messages (a same-named but unrelated type there —
// see the deviation note in the csproj) and not in ATAS.DataFeedsCore either.
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

    private readonly object _trackingLock = new();
    private string _account = string.Empty;
    private string _instrument = string.Empty;

    public string TrackedAccount { get { lock (_trackingLock) return _account; } }
    public string TrackedInstrument { get { lock (_trackingLock) return _instrument; } }

    public StatePublisher(ContextResolver resolver, BridgeClient bridge, int intervalMs)
    {
        _resolver = resolver;
        _bridge = bridge;
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
            var accounts = _resolver.GetAccountNames();
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
