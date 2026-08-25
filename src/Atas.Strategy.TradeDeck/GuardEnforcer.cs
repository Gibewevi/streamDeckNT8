using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

using ATAS.DataFeedsCore;
using ATAS.Indicators;

namespace TradeDeck.Atas.Strategy;

/// <summary>
/// Applies the bridge's safety rules to orders the bridge never sees.
///
/// THE HOLE THIS CLOSES. The safety macro lives in the bridge, which only knows about commands
/// coming from the deck. An order typed into the DOM or the Chart Trader reaches the market without
/// ever crossing it — so a trader who has just been refused an entry can simply place it himself,
/// and every limit becomes decorative at the exact moment it matters. This strategy runs INSIDE the
/// platform and is subscribed to the connector, so it sees every order on the account whatever its
/// origin. It is the only place the hole can be closed.
///
/// WHAT IT NEVER DOES. It never cancels an order that reduces exposure — a manual stop, a target, a
/// scale-out, a panic exit. Trapping the trader inside a position is the one outcome none of these
/// rules may ever produce, and it is why the test below is about GROWTH and not about "is this an
/// entry". It also never submits an order of its own: cancelling cannot create a position, so the
/// worst case of a false positive is a cancelled order, not a trade.
///
/// WHAT IT CANNOT DO. A market order on a liquid future often fills before the platform reports it,
/// and a filled order cannot be cancelled. That case is detected and reported, not prevented. And
/// anyone willing to stop this strategy, or trade from the broker's own platform, is past what any
/// of this can reach. The point is to turn a two-click impulse into a deliberate act of dismantling
/// one's own safety net.
///
/// IT KNOWS NO REASON BY NAME. `reason` travels through untouched — dailyLoss, tradeLimit,
/// beforeSessionStart or anything the bridge adds later. The clock, the arming and the countdown are
/// the bridge's business alone; this class never reads the time.
///
/// THE HOLE THIS DOES NOT CLOSE. This class only ever sees an order through a connector event
/// (OrderChanged / NewOrders — see OrderMonitor). In degraded mode (task 3: host registry
/// unreachable) OrderMonitor.Refresh receives an empty connector list, subscribes to nothing, and
/// Inspect is therefore never called at all: an order placed outside the deck goes completely
/// unwatched by this class while degraded, with no warning specific to this file (OrderMonitor logs
/// its own "no connector watched" line once per episode, but that line does not say guard
/// enforcement is part of what stopped). A periodic poll of ITradingManager.Orders to compensate was
/// deliberately left out of this plan (task 9) — it would be a distinct, unmeasured mechanism. The
/// only safety net left in degraded mode is the macro on the BRIDGE side, which keeps refusing
/// deck-originated commands regardless of this class's mode; it cannot reach a hand-typed DOM order
/// either way, degraded or not, because such an order never crosses the bridge in the first place.
/// The SAME statement holds — and is made explicit at the call site — for the state before the
/// bridge has confirmed a tracked account: see the account-scope check in InspectCore below.
/// </summary>
internal sealed class GuardEnforcer
{
    private readonly ContextResolver _resolver;
    private readonly BridgeClient _bridge;
    private readonly OrderEngine _engine;
    private readonly IOrderGateway _gateway;

    private readonly object _lock = new();

    private bool _blocked;
    private string _reason = string.Empty;
    private int _maxContracts;
    private string _trackedAccount = string.Empty;

    /// <summary>Orders already acted upon, so a second update does not cancel twice.</summary>
    private readonly HashSet<string> _handled = new(StringComparer.Ordinal);

    /// <summary>
    /// Same purpose as <see cref="_handled"/>, for the order that has no id to key on (R1 review,
    /// finding I2: an empty `order.Id` used to skip dedup entirely, firing a fresh Cancel and a
    /// fresh EventWarn on EVERY subsequent OrderChanged for that same order — a cancel-request
    /// storm against the broker for the length of one order's life, right when the log needs to
    /// stay readable). A ConditionalWeakTable so an order that never resolves cannot leak for the
    /// life of the session, matching the pattern OrderEngine's own `_ours` table already uses for
    /// exactly the same "no reliable key, fall back to identity" situation.
    ///
    /// TWO LIMITS TO THIS, BOTH FROM R2 REVIEW.
    ///
    /// (I2b) An order commonly starts life with an empty id and is later mutated, on the SAME
    /// instance, to carry the real one once the platform acknowledges it — the ordinary path, not
    /// an edge case. InspectCore checks this table BEFORE testing a non-empty id as "new" and
    /// migrates the entry to <see cref="_handled"/> instead, so the transition from "no id yet" to
    /// "id assigned" produces exactly one Cancel/EventWarn/_pending entry, not two.
    ///
    /// (I2a) When ATAS both omits the id AND hands back a DIFFERENT Order instance per event for
    /// the same logical order (OrderEngine's own doc: "observed to happen"), no key — reference or
    /// otherwise — can tell that this is the same order again, and `TryAdd` below succeeds every
    /// time. This is NOT fixed, deliberately: no dedup key can be correct with no stable identity at
    /// all, so the repeated Cancel is accepted as harmless (cancelling is idempotent at the broker
    /// and can never trap the trader in a position — this class's whole bound) and only the LOGGING
    /// is bounded, in <see cref="CancelAndReport"/>, so the one log that must stay readable during
    /// exactly this kind of episode cannot be flooded by it.
    /// </summary>
    private readonly ConditionalWeakTable<Order, object> _handledByReference = new();

    /// <summary>
    /// One order this class asked to cancel, waiting to find out how the story ends. Keyed by
    /// order id — see <see cref="_handledByReference"/> for the id-less case, which is never
    /// tracked to a conclusion here for lack of a key to track it by.
    /// </summary>
    private sealed class PendingGuardAction
    {
        public readonly string Violation;

        /// <summary>
        /// Set by <see cref="NotifyCancelFailed"/> the moment the platform confirms THIS class's
        /// own cancel request was refused. Read by <see cref="ResolveOutcome"/> regardless of when
        /// it happens to run relative to that notification (R2 review, N2 — see both methods'
        /// docs): the fact "our cancel failed" is recorded on the pending entry itself rather than
        /// inferred from whether the entry still exists at some instant, so it cannot be lost to a
        /// race between the two.
        /// </summary>
        public bool CancelFailed;

        public PendingGuardAction(string violation) => Violation = violation;
    }

    private readonly Dictionary<string, PendingGuardAction> _pending = new(StringComparer.Ordinal);

    /// <summary>
    /// Rate-limits the "no stable id" cancel log — see <see cref="_handledByReference"/>'s I2a
    /// note. Read and written only under <see cref="_lock"/>.
    /// </summary>
    private int _noIdCancelCount;

    /// <summary>The order got through: it filled despite the refusal.</summary>
    public const string OutcomeBypassed = "bypassed";

    /// <summary>The order was really stopped — cancelled or rejected before filling.</summary>
    public const string OutcomeStopped = "stopped";

    public int MaxContracts { get { lock (_lock) return _maxContracts; } }
    public bool IsBlocking { get { lock (_lock) return _blocked; } }

    public GuardEnforcer(ContextResolver resolver, BridgeClient bridge, OrderEngine engine,
                         IOrderGateway gateway)
    {
        _resolver = resolver;
        _bridge = bridge;
        _engine = engine;
        _gateway = gateway;
    }

    public void SetTrackedAccount(string account)
    {
        lock (_lock) _trackedAccount = account ?? string.Empty;
    }

    /// <summary>Adopts the policy the bridge publishes whenever it changes.</summary>
    public void UpdatePolicy(bool blocked, string? reason, int maxContracts)
    {
        lock (_lock)
        {
            var changed = _blocked != blocked
                       || !string.Equals(_reason, reason ?? string.Empty, StringComparison.Ordinal)
                       || _maxContracts != maxContracts;

            _blocked = blocked;
            _reason = reason ?? string.Empty;
            _maxContracts = maxContracts;

            // Logged only on change: the bridge pushes this on connection and then only when it
            // moves, but the discipline belongs on both sides of the wire.
            if (!changed) return;
        }

        SdLog.Event("Safety", "Guard policy adopted — blocked={0} reason={1} maxContracts={2}",
            blocked, string.IsNullOrEmpty(reason) ? "-" : reason, maxContracts);
    }

    /// <summary>
    /// Called for every order update on the connector. Returns true when a cancel was requested, so
    /// the caller can refresh the deck.
    ///
    /// GUARDED AT THE TOP LEVEL, NOT JUST INSIDE ITS OWN HELPERS. This runs on a connector event
    /// thread (OrderChanged, or a foreach over a NewOrders batch) with no wrapper of its own beneath
    /// it in the platform's dispatch loop — an exception escaping here does not just skip this one
    /// order, it can abort every order still left in the same NewOrders batch (a bracket's entry,
    /// stop and target can all arrive together) and, worse, propagate into ATAS's own event pipeline.
    /// Every risky call inside already has its own try/catch (Cancel, the report, the platform status
    /// read), but a plain property read (order.Portfolio, a connector's Position row) is not
    /// guaranteed never to throw, and the one thing this class must never do on an error it cannot
    /// explain is decide FOR ITSELF whether to cancel: failing to "did nothing" is the only wrong
    /// outcome (Inspect returning false) that stays inside this class's own bound of never causing
    /// harm.
    /// </summary>
    public bool Inspect(Order? order)
    {
        try
        {
            return InspectCore(order);
        }
        catch (Exception ex)
        {
            SdLog.Fail("Safety", ex, "Guard inspection faulted — order left untouched");
            return false;
        }
    }

    private bool InspectCore(Order? order)
    {
        if (order == null) return false;

        bool blocked;
        string reason;
        int cap;
        string account;
        lock (_lock)
        {
            blocked = _blocked;
            reason = _reason;
            cap = _maxContracts;
            account = _trackedAccount;
        }

        // BEFORE every early return below: an order we already acted on must be followed to its
        // conclusion whatever the policy has become since. Clearing the block, or the order ceasing
        // to be cancellable — which is precisely what happens when it fills — would otherwise lose
        // the only update that says how the story ended.
        ResolveOutcome(order);

        if (!blocked && cap <= 0) return false;

        // SCOPED TO THE TRACKED ACCOUNT, AND FAILS CLOSED WITHOUT ONE. Every safety rule of this
        // protocol is scoped to one account, and enforcing on a portfolio nobody designated is how,
        // on 2026-08-21, a locked macro coexisted with an hour of hand-placed orders. An EMPTY
        // tracked account is routine, not exotic — a fresh OnStarted, before the bridge's first
        // setAccount arrives, or between chart start and a connector attaching (see the Rithmic case
        // elsewhere in this file). With no confirmed account, this class inspects and cancels
        // NOTHING: guessing at "every account any connector reports" (the first cut of this class,
        // R1 review finding I1) is the SAME failure as 2026-08-21 wearing a different hat —
        // enforcement acting on an account nobody confirmed — only this time by cancelling a
        // trader's order on an account the deck was never even pointed at, instead of missing one.
        // See TradeDeckStrategy.HandleGuardPolicy's SECURITY warning for the trader-facing half of
        // this: with no account confirmed, enforcement is OFF, and the bridge-side macro is the
        // only remaining net — exactly the same statement already made for degraded mode above.
        if (account.Length == 0) return false;
        if (!string.Equals(order.Portfolio?.AccountID, account, StringComparison.OrdinalIgnoreCase))
            return false;

        // Best-effort recognition of our own order, no more than that — see OrderEngine.IsOurs's own
        // doc for the exact limit: a DOM ticket a trader happens to comment "StreamDeck" by hand is
        // classified as ours too, and slips through here unlogged, same as everywhere else IsOurs is
        // trusted. This is NOT "the bridge already vetted this order" (R1 review, minor finding) —
        // the bridge never sees a hand-typed order at all, which is the entire reason this class
        // exists.
        if (_engine.IsOurs(order)) return false;

        if (!ContextResolver.IsActive(order)) return false;
        if (!GrowsExposure(order)) return false;

        string violation;
        if (blocked)
        {
            violation = string.IsNullOrEmpty(reason) ? "guardBlocked" : reason;
        }
        else
        {
            var held = HeldQuantity(order);
            var resulting = held + (int)order.Unfilled;
            if (resulting <= cap) return false;
            violation = "maxContracts";
        }

        var id = order.Id ?? string.Empty;
        lock (_lock)
        {
            if (id.Length > 0)
            {
                // MIGRATION (R2 review, I2b): an order first seen with an empty id — dedup'd below
                // by reference — commonly presents a real id LATER on the very same instance, once
                // the platform acknowledges it. That is the ordinary path for a fresh order, not an
                // edge case. Without this check, `_handled.Add(id)` would see a brand new id and
                // treat the SAME order as new, firing a SECOND Cancel/EventWarn/_pending entry for
                // it. Recognise the instance and move it to the id-keyed table instead.
                if (_handledByReference.TryGetValue(order, out _))
                {
                    _handledByReference.Remove(order);
                    _handled.Add(id);
                    return false;
                }

                if (!_handled.Add(id)) return false;
            }
            else
            {
                // No id to key on. TryAdd still dedupes the common case — the SAME instance seen
                // again before ever gaining an id — correctly; only true instance churn with a
                // persistently empty id (I2a, see _handledByReference's own doc) defeats it, and
                // that is accepted rather than chased: see CancelAndReport for the log-side bound
                // that keeps that case from flooding the log.
                if (!_handledByReference.TryAdd(order, id)) return false;
            }
        }

        return CancelAndReport(order, violation, id);
    }

    private bool CancelAndReport(Order order, string violation, string id)
    {
        var instrument = ContextResolver.InstrumentName(order.Security);

        try
        {
            _gateway.Cancel(order);
            LogCancelRequested(order, instrument, violation, id);
        }
        catch (Exception ex)
        {
            // A cancel that throws is the one case settled on the spot: the order is alive and the
            // macro did not stop it.
            SdLog.Fail("Safety", ex, "Could not cancel external order on {0}", instrument);
            Report(order, violation, OutcomeBypassed, ex.Message);
            return false;
        }

        // DELIBERATELY NOT REPORTED AS CANCELLED HERE. Cancelling is asynchronous: returning without
        // throwing says only that the request left. On 2026-08-12, thirty of thirty-five such
        // requests came back "order is complete" because the market order had already filled — and
        // every one of them had been journalled as cancelled:true, so Bitlearn read the day as 100%
        // rule compliance while every trade carried a bypass. The verdict is whatever state the
        // order reaches NEXT, and ResolveOutcome sends it.
        //
        // ONLY WHEN AN ID EXISTS TO LOOK IT BACK UP BY (R1 review, I2). Without one, ResolveOutcome
        // itself refuses to act (see its own early return), so there is no key left to track this
        // particular cancel to a conclusion by — the reference-based dedup above already stops the
        // retry storm; the eventual bypassed/stopped verdict is simply unavailable for an order ATAS
        // never gave an id to.
        if (id.Length > 0)
        {
            lock (_lock) _pending[id] = new PendingGuardAction(violation);
        }

        return true;
    }

    /// <summary>
    /// Logs the cancel request — rate-limited ONLY on the no-stable-id path (R2 review, I2a). An
    /// order with a real id logs every time, exactly as before: that path is already deduplicated by
    /// <see cref="_handled"/>, so it can only fire once per genuine order. The id-less path has no
    /// such guarantee when ATAS churns the Order instance (see _handledByReference's own doc), so it
    /// uses the same "log the first few, then throttle" idiom StatePublisher already uses for
    /// _consecutiveSkips: the repeated Cancel itself is accepted as harmless — idempotent at the
    /// broker, incapable of trapping the trader in a position — but the log that must stay readable
    /// during exactly this kind of episode must not be flooded by it.
    /// </summary>
    private void LogCancelRequested(Order order, string instrument, string violation, string id)
    {
        if (id.Length > 0)
        {
            SdLog.EventWarn("Safety",
                "EXTERNAL ORDER CANCEL REQUESTED — {0} {1} qty={2} on {3} (comment='{4}', state={5}) refused by the safety macro: {6}",
                order.Direction, order.Type, order.QuantityToFill, instrument,
                order.Comment ?? string.Empty, order.State, violation);
            return;
        }

        int count;
        lock (_lock) count = ++_noIdCancelCount;

        if (count <= 3 || count % 50 == 0)
        {
            SdLog.EventWarn("Safety",
                "EXTERNAL ORDER CANCEL REQUESTED (no stable order id, occurrence #{7}) — {0} {1} qty={2} on {3} (comment='{4}', state={5}) refused by the safety macro: {6}",
                order.Direction, order.Type, order.QuantityToFill, instrument,
                order.Comment ?? string.Empty, order.State, violation, count);
        }
        else
        {
            SdLog.TraceEvent("Safety",
                "EXTERNAL ORDER CANCEL REQUESTED (no stable order id, occurrence #{0}) — throttled",
                count);
        }
    }

    /// <summary>
    /// The platform confirms this class's OWN cancel request did not take — a broker-side rejection
    /// of the cancel, reported asynchronously via OrdersCancelFailed. Distinct from the synchronous
    /// exception path in <see cref="CancelAndReport"/>: that one is a throw out of Cancel() itself,
    /// this one is a connector event that can arrive well after Cancel() already returned normally.
    ///
    /// RECORDS THE FACT, DOES NOT REPORT IT (R2 review, N2 — this changed from R1's version, which
    /// reported immediately and raced ResolveOutcome's own pop of the same entry: whichever ran
    /// first could leave the other looking at an empty _pending, and a later hand-cancel of the SAME
    /// order — the natural next move after a broker-rejected cancel — could then resolve through
    /// ResolveOutcome as `outcome: "stopped"`, crediting THIS class with a cancel that was actually
    /// the trader's own; Bitlearn reads "stopped" as the rule having worked). Flagging the STILL-
    /// PRESENT pending entry instead of popping-and-reporting removes the race outright: the entry is
    /// never removed by anyone until <see cref="ResolveOutcome"/> has an actual terminal status in
    /// hand, so this method can always find it (barring the id-less case, where there was never an
    /// entry to find), and ResolveOutcome — whichever OrderChanged eventually reaches it — is
    /// guaranteed to see the flag and report `bypassed`, never `stopped`, regardless of timing.
    ///
    /// Called for EVERY OrdersCancelFailed from OrderMonitor, unconditionally: cheap and correct to
    /// be a no-op whenever this class is not the one who asked for that particular cancel.
    /// </summary>
    public void NotifyCancelFailed(Order? order, string? reason)
    {
        if (order == null) return;
        var id = order.Id ?? string.Empty;
        if (id.Length == 0) return;

        bool alreadyFlagged;
        lock (_lock)
        {
            if (!_pending.TryGetValue(id, out var entry)) return;
            alreadyFlagged = entry.CancelFailed;
            entry.CancelFailed = true;
        }

        // Logged once per order, not once per retry: a broker can reject a cancel repeatedly while
        // the platform keeps re-trying it, and the fact this class cares about — "do not credit this
        // order as stopped" — only needs recording once.
        if (alreadyFlagged) return;

        var instrument = ContextResolver.InstrumentName(order.Security);
        SdLog.EventWarn("Safety",
            "EXTERNAL ORDER CANCEL FAILED — {0} {1} qty={2} on {3}: the guard's own cancel was refused ({4}); this order will NOT be reported as stopped",
            order.Direction, order.Type, order.QuantityToFill, instrument, string.IsNullOrEmpty(reason) ? "?" : reason);
    }

    /// <summary>
    /// Reports how a cancelled-at order actually ended, once the platform says so.
    ///
    /// ATAS only has four order states, so the conclusion is read from what actually traded rather
    /// than from a state name: anything filled means the trader got through — the bypass worked.
    ///
    /// THE ENTRY IS NEVER REMOVED UNTIL A TERMINAL STATUS IS ACTUALLY IN HAND (R2 review, N1 and
    /// N2). The R1 version popped the entry BEFORE reading order.Status() and put it back only if
    /// the status was not yet terminal — a throwing Status() then lost the entry permanently (N1),
    /// and the empty window between pop and put-back let NotifyCancelFailed race it (N2). Checking
    /// for presence without removing, reading Status() outside the lock, and only THEN taking the
    /// lock again to pop closes both: a throw leaves the entry exactly where it was, for the next
    /// OrderChanged to retry, and NotifyCancelFailed can always find the entry because nothing ever
    /// removes it early.
    /// </summary>
    private void ResolveOutcome(Order order)
    {
        var id = order.Id ?? string.Empty;
        if (id.Length == 0) return;

        lock (_lock)
        {
            if (!_pending.ContainsKey(id)) return;
        }

        string? terminal;
        try
        {
            // order.Status() is a platform read, made OUTSIDE the lock (R1 review, minor finding):
            // the R1 version made this call while holding _lock, which UpdatePolicy,
            // SetTrackedAccount and MaxContracts also take — a slow platform read here, on a
            // connector event thread, could stall the bridge thread trying to adopt a brand-new
            // policy at the exact moment it matters most.
            terminal = TerminalOutcome(order);
        }
        catch (Exception ex)
        {
            // Nothing has been removed yet — the entry is simply left in place for the next
            // OrderChanged on this id to try again (R2 review, N1: the pop-then-put-back version
            // could lose the entry forever here).
            SdLog.Fail("Safety", ex, "Could not read order status while resolving a pending guard outcome — left pending");
            return;
        }
        if (terminal == null) return;

        string violation;
        bool cancelFailed;
        lock (_lock)
        {
            // Re-check under lock: another thread could have already resolved (and removed) this
            // same entry between the ContainsKey check above and here.
            if (!_pending.TryGetValue(id, out var entry)) return;
            violation = entry.Violation;
            cancelFailed = entry.CancelFailed;
            _pending.Remove(id);
        }

        // A cancel THIS CLASS already knows failed can never be reported as "stopped" (R2 review,
        // N2): that would credit the guard with an order some OTHER cause actually terminated — the
        // trader's own hand-cancel, most likely, right after seeing the broker refuse this class's
        // attempt. Bitlearn reads "stopped" as the rule having worked; here it did not.
        var outcome = cancelFailed ? OutcomeBypassed : terminal;

        var instrument = ContextResolver.InstrumentName(order.Security);
        if (outcome == OutcomeStopped)
        {
            SdLog.EventWarn("Safety", "EXTERNAL ORDER STOPPED — {0} {1} qty={2} on {3}: {4} ({5})",
                order.Direction, order.Type, order.QuantityToFill, instrument, violation, order.State);
        }
        else
        {
            // The line that matters on a bad day: the rule refused AND the order got through (or
            // this class's own cancel of it had already failed, which is the same story for
            // Bitlearn's purposes: the rule did not stop the order).
            SdLog.EventWarn("Safety", "GUARD BYPASSED — {0} {1} qty={2} on {3}: {4}{5}",
                order.Direction, order.Type, order.QuantityToFill, instrument, violation,
                cancelFailed ? " (guard's own cancel had already failed)" : " FILLED despite the refusal");
        }

        Report(order, violation, outcome, string.Empty);
    }

    /// <summary>Null while the order can still change its mind.</summary>
    private static string? TerminalOutcome(Order order) => order.Status() switch
    {
        // Anything that traded is a bypass, even partially and even if the rest was then cancelled:
        // the trader took exposure the rule was refusing, which is the whole point of the report.
        OrderStatus.Filled or OrderStatus.PartlyFilled => OutcomeBypassed,
        OrderStatus.Canceled => OutcomeStopped,
        // Placed and None are not a conclusion yet.
        _ => null,
    };

    private void Report(Order order, string violation, string outcome, string error)
    {
        try
        {
            var payload = new Dictionary<string, object?>
            {
                ["violation"] = violation,
                // Kept for readers that predate `outcome`, and now honest: true only when the order
                // was really stopped, never merely asked to stop.
                ["cancelled"] = outcome == OutcomeStopped,
                ["outcome"] = outcome,
                ["orderId"] = order.Id ?? string.Empty,
                ["orderAction"] = order.Direction.ToString(),
                ["orderType"] = order.Type.ToString(),
                ["quantity"] = (int)order.QuantityToFill,
                ["name"] = order.Comment ?? string.Empty,
                ["instrument"] = ContextResolver.InstrumentName(order.Security),
            };
            if (!string.IsNullOrEmpty(error)) payload["error"] = error;

            // Fire-and-forget: this runs inside the connector's event pipeline and cannot wait.
            _ = _bridge.SendAsync(BridgeMessage.Notify("guardViolation", payload));
        }
        catch (Exception ex)
        {
            SdLog.Fail("Safety", ex, "Could not report guard violation");
        }
    }

    /// <summary>
    /// Would this order make the position BIGGER?
    ///
    /// The whole safety of this class rests on this test. A manual stop on a long is a Sell and a
    /// manual stop on a short is a Buy: both reduce, both must go through untouched even while every
    /// entry is refused.
    /// </summary>
    private bool GrowsExposure(Order order)
    {
        var portfolio = order.Portfolio;
        var position = _resolver.FindPosition(portfolio, order.Security);

        if (position == null) return true;               // flat: any order opens
        if (position.Volume > 0m) return order.Direction == OrderDirections.Buy;
        return order.Direction == OrderDirections.Sell;
    }

    private int HeldQuantity(Order order)
    {
        var position = _resolver.FindPosition(order.Portfolio, order.Security);
        return position == null ? 0 : (int)Math.Abs(position.Volume);
    }
}
