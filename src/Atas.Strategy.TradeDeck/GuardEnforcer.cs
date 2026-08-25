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
/// the bridge's business alone, and NO REFUSAL PATH HERE EVER READS THE CLOCK: not one branch of
/// InspectCore, of the cancel decision or of the outcome verdict depends on what time it is, so this
/// class can never arm, disarm or expire a rule of its own accord. The single clock read in this
/// file (DateTime.UtcNow in LogCancelRequested — see _noIdCancelCount) bounds LOG VOLUME and nothing
/// else: it can change how loudly an event is written, never whether an order is cancelled nor how
/// that order is reported.
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
    /// order id.
    ///
    /// AN ORDER CANCELLED WHILE STILL ID-LESS DOES GET AN ENTRY HERE (R3 review, I2b). The older
    /// wording of this doc — "the id-less case is never tracked to a conclusion" — stopped being
    /// true then: the migration branch in InspectCore creates the entry at the first moment a key
    /// exists, i.e. the first inspection that sees a real id on an instance this class had already
    /// cancelled. What stays genuinely untracked is narrower, and is stated in full at that
    /// migration site: an order whose id only ever surfaces on an event InspectCore no longer
    /// reaches — the TERMINAL one (an instant fill, or the cancel confirmation itself), or any event
    /// past the point where a filter above now stops it — never reaches the migration and therefore
    /// never produces a verdict at all.
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
        ///
        /// COVERS ONLY THE FAILURES REPORTED WHILE THIS ENTRY EXISTED. A cancel refused BEFORE the
        /// entry was ever built — the ordinary id-less sequence, where the entry only appears later
        /// at the migration in InspectCore — cannot be flagged here at all, and used to be lost
        /// outright. <see cref="_cancelFailedIds"/> is the companion record that catches that case;
        /// <see cref="ResolveOutcome"/> consults both (R5 review, finding 1).
        /// </summary>
        public bool CancelFailed;

        public PendingGuardAction(string violation) => Violation = violation;
    }

    private readonly Dictionary<string, PendingGuardAction> _pending = new(StringComparer.Ordinal);

    /// <summary>
    /// Ids whose cancellation the platform reported as FAILED, recorded independently of
    /// <see cref="_pending"/>. Read and written only under <see cref="_lock"/>.
    ///
    /// WHY A FLAG ON THE PENDING ENTRY IS NOT ENOUGH (R5 review, finding 1). R2 made "our cancel
    /// failed" a flag on the entry so it could not be lost to a race with ResolveOutcome, and that
    /// covers every case where the entry EXISTS. On the id-less path it does not exist yet, and not
    /// by accident: an order cancelled while still id-less only gets its entry at the migration in
    /// InspectCore, on the NEXT inspection. So the ORDINARY sequence — cancel, broker refuses it,
    /// migration — had NotifyCancelFailed look up an id nothing had keyed yet and return, after
    /// which the migration built a fresh entry with CancelFailed necessarily cleared; the trader's
    /// own hand-cancel then resolved as `outcome: "stopped"`, and Bitlearn reads "stopped" as the
    /// rule having worked. That is the R2 incident exactly, resurrected on the id-less path by the
    /// R3 migration. A refused cancel is a fact about an ORDER, not a property of a dictionary
    /// entry that may not have been created yet, so it is recorded here the moment it is known.
    ///
    /// A FACT STORE AND NOTHING MORE: nothing is cancelled from it, nothing is reported from it,
    /// nothing periodic reads it. Its only reader is ResolveOutcome, which removes an id as it
    /// consumes it — pruning every order this class actually resolves. What is never pruned is an
    /// id belonging to an order this class never cancelled, because NotifyCancelFailed is called
    /// for EVERY OrdersCancelFailed and at that instant cannot yet tell the difference — which is
    /// the entire point. That is one short string per broker-refused cancel in a session, the same
    /// session-scoped shape <see cref="_handled"/> already has, and it is deliberately preferred to
    /// any size cap: a cap drops facts, and a dropped fact here IS the false clean record this
    /// store exists to prevent.
    ///
    /// IT CANNOT TELL WHOSE CANCEL WAS REFUSED, and errs on purpose. If the refused cancel was the
    /// TRADER's rather than this class's, an order that really does end up cancelled is reported
    /// `bypassed` instead of `stopped`. That is the pessimistic direction — a bypass recorded where
    /// the rule may in fact have held — never the reverse, and never a false clean record.
    /// </summary>
    private readonly HashSet<string> _cancelFailedIds = new(StringComparer.Ordinal);

    /// <summary>
    /// Rate-limits the "no stable id" cancel log — see <see cref="_handledByReference"/>'s I2a
    /// note. Read and written only under <see cref="_lock"/>.
    ///
    /// RESETS WHEN THE STORM SETTLES (R3 review, I2a): a session-global count that never cleared
    /// meant that after three id-less cancels ANYWHERE in a session, every later one — a wholly
    /// unrelated order, hours apart — dropped to TRACE except every fiftieth. Combined with I2b's
    /// original gap (no _pending entry on this path either) that TRACE line was the ONLY record
    /// anywhere that a trader's order had been cancelled, at a level the log does not show by
    /// default. <see cref="_lastNoIdCancelUtc"/> tracks how long ago the last one of these fired;
    /// a gap past <see cref="NoIdCancelResetWindow"/> means the previous storm has settled, so the
    /// NEXT id-less cancel — whether truly new or the same churn resuming — is treated as the
    /// start of a fresh episode and logs at WARN again. Same idiom as this project's own
    /// StatePublisher._consecutiveSkips (reset on the condition clearing) and the NinjaTrader
    /// add-on's _lastPnlWarning (time-gated, not count-gated) — chosen over a per-Order-reference
    /// counter because a churning instance is, by definition, a NEW reference every time (see
    /// _handledByReference's own doc): keying the throttle by reference would never throttle
    /// anything, since every occurrence would read as its own "first" one.
    /// </summary>
    private int _noIdCancelCount;

    private DateTime _lastNoIdCancelUtc = DateTime.MinValue;

    /// <summary>
    /// How long a gap with no id-less cancel counts as "the storm settled" for
    /// <see cref="_noIdCancelCount"/>. One minute, matching the NinjaTrader add-on's
    /// _lastPnlWarning gate — long enough that a genuinely continuous churn episode (events
    /// typically seconds or less apart) never resets mid-storm, short enough that a later,
    /// unrelated id-less order is never silently folded into a storm that ended minutes earlier.
    /// </summary>
    private static readonly TimeSpan NoIdCancelResetWindow = TimeSpan.FromMinutes(1);

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

        // PARKED GAP — THE MIGRATION BELOW ONLY EVER RUNS ON AN ORDER STILL BEING INSPECTED (R4
        // review; recorded deliberately, NOT fixed in this round). Every filter above has already
        // run by the time an id-less order can hand its new id to that migration: it must still be
        // ContextResolver.IsActive and must still violate the CURRENT policy. So an order this class
        // cancelled while it was id-less, whose id first appears on its TERMINAL event — an instant
        // fill on a liquid future, or the cancel confirmation itself — or only after the block
        // cleared, never reaches the migration, never gets a _pending entry, and produces NO verdict
        // at all. On the instant-fill case the missing line is `GUARD BYPASSED`, the one line that
        // matters most. Closing this needs a pending store keyed by REFERENCE while no id exists,
        // which is a design change to this class's whole bookkeeping rather than a patch; it was
        // parked rather than half-done, because every partial fix in this area so far has traded a
        // noisy record for a silent one.
        //
        // WHAT IS AND IS NOT LOST, PRECISELY (R5 review). The order was still cancelled — the
        // guard's primary duty. No WRONG verdict is produced either: with no _pending entry,
        // ResolveOutcome returns before deciding anything, so the failure mode here is a MISSING
        // verdict and never a false `stopped`. What IS lost is the journal line to Bitlearn, and
        // the local consolation is thinner than it first looks: LogCancelRequested does log the
        // cancel request, but on this very path — id-less, with the Order instance churning — that
        // line is throttled down to TRACE past the third occurrence of an episode (see
        // _noIdCancelCount), and TRACE is off at the default level. In exactly the churn episode
        // this gap is about, the trader may therefore see nothing about it at all.
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

                    // BUILD THE PENDING ENTRY HERE (R3 review, I2b): this order was already
                    // cancelled while it was still id-less, and CancelAndReport could not create
                    // a _pending entry for it back then — no key existed yet. This migration is
                    // the FIRST moment a key exists. Skipping this step (the R2 fix) moved the
                    // dedup key but left NEITHER a bypassed NOR a stopped verdict ever reaching
                    // Bitlearn on the NORMAL "id assigned at acknowledgement" path — worse than
                    // the duplicate verdict the R1 fix had originally replaced. `violation` here
                    // is the CURRENT policy's verdict for this order, computed just above; the
                    // original id-less cancel is not expected to have used a different one, since
                    // id assignment follows acknowledgement almost immediately.
                    //
                    // CREATE ONLY IF ABSENT — AN INDEXER ASSIGNMENT IS A DEFECT HERE (R4 review).
                    // This migration is one-shot per (instance, id) PAIR, not per id, and instance
                    // churn is real on this platform (see _handledByReference's I2a note: ATAS can
                    // hand back a different Order object per event for the same logical order). Two
                    // id-less instances of one logical order can therefore both sit in the weak
                    // table and both reach this line carrying the same id. `_pending[id] = new ...`
                    // would let the second REPLACE the entry the first created — discarding a
                    // CancelFailed flag an OrdersCancelFailed had set in between, after which the
                    // trader's own hand-cancel resolves as outcome:"stopped" and Bitlearn records a
                    // clean day on an order the guard never stopped. That is precisely the failure
                    // the R2 flag exists to prevent, so this write must never clobber: a duplicate
                    // entry is impossible anyway (the first one is kept), a LOST FLAG is what has to
                    // be impossible.
                    _pending.TryAdd(id, new PendingGuardAction(violation));
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

        // THE PENDING ENTRY IS CREATED BEFORE THE CANCEL REQUEST, NOT AFTER (R3 review, N2). The
        // R2 version inserted it AFTER Cancel() returned; OrdersCancelFailed can be raised inline
        // by Cancel() itself, or on a connector thread inside the window between the request and
        // that insert, and NotifyCancelFailed's own lookup found nothing there — silently dropping
        // the "our cancel failed" fact. A later hand-cancel of the SAME order would then resolve
        // through ResolveOutcome as `outcome: "stopped"`: the exact false clean record this class
        // exists to prevent, and Bitlearn reads "stopped" as the rule having worked. Inserting
        // first closes the window outright: by the time Cancel() can raise anything, the entry
        // already exists for NotifyCancelFailed to find. Only done when an id exists to key it by
        // (R1 review, I2); an order cancelled while still id-less gets its entry LATER, at the
        // migration branch in InspectCore, the first moment a key exists (R3 review, I2b — this
        // comment used to say the id-less case could never be tracked to a conclusion, which
        // stopped being true then). The residue that genuinely never gets an entry is the narrower
        // one stated at that migration site: an id that only ever surfaces on an event InspectCore
        // no longer reaches.
        //
        // CREATE-IF-ABSENT HERE TOO (R5 review, finding 2 — the R4 fix corrected the migration site
        // and left this one). This site is reachable with an entry already present: the
        // `_handled.Add(id)` gate in InspectCore is released before this method re-takes the lock,
        // and a platform read (InstrumentName, just above) sits inside that window. Thread A can
        // win `_handled.Add(X)` and leave the lock; thread B can migrate a churned id-less instance
        // of the SAME logical order and create entry E for X; an OrdersCancelFailed can flag E; and
        // A would then arrive here and REPLACE E, discarding the flag — the same lost flag, the
        // same false `stopped`, the same false clean record as the defect fixed one line up in the
        // migration branch. Keep whichever entry already exists: its Violation describes the cancel
        // that actually happened, and its flag is the one that must never be dropped.
        if (id.Length > 0)
        {
            lock (_lock) _pending.TryAdd(id, new PendingGuardAction(violation));
        }

        try
        {
            _gateway.Cancel(order);
            LogCancelRequested(order, instrument, violation, id);
        }
        catch (Exception ex)
        {
            // A cancel that throws is the one case settled on the spot: the order is alive and the
            // macro did not stop it — drop the pending entry for this id, since ResolveOutcome has
            // nothing left to wait for; this call already knows, and reports, the outcome. Said
            // exactly (R5): what is removed is the entry inserted above, OR an equivalent one a
            // concurrent migration had already created for the same order. Removing that one costs
            // nothing that matters — the verdict for this order is being reported as bypassed on
            // this very line, so the outcome is recorded, not silenced, and any cancel-failure fact
            // survives in _cancelFailedIds regardless of what happens to the entry.
            if (id.Length > 0)
            {
                lock (_lock) _pending.Remove(id);
            }
            SdLog.Fail("Safety", ex, "Could not cancel external order on {0}", instrument);
            Report(order, violation, OutcomeBypassed, ex.Message);
            return false;
        }

        // DELIBERATELY NOT REPORTED AS CANCELLED HERE. Cancelling is asynchronous: returning without
        // throwing says only that the request left. On 2026-08-12, thirty of thirty-five such
        // requests came back "order is complete" because the market order had already filled — and
        // every one of them had been journalled as cancelled:true, so Bitlearn read the day as 100%
        // rule compliance while every trade carried a bypass. The verdict is whatever state the
        // order reaches NEXT, and ResolveOutcome sends it — the pending entry inserted above is what
        // lets it, and NotifyCancelFailed, find their way back to this order.
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
        lock (_lock)
        {
            var now = DateTime.UtcNow;
            if (now - _lastNoIdCancelUtc > NoIdCancelResetWindow) _noIdCancelCount = 0;
            _lastNoIdCancelUtc = now;
            count = ++_noIdCancelCount;
        }

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
    /// PRESENT pending entry instead of popping-and-reporting removes the race outright. The exact
    /// guarantee, stated as it actually stands (R4 review: this used to claim the entry "is never
    /// removed by anyone" before ResolveOutcome holds a terminal status, and there has been a second
    /// remover since R3). <see cref="ResolveOutcome"/> never removes an entry before it has an
    /// actual terminal status in hand, so an entry still waiting for its verdict is always here to
    /// be flagged, and ResolveOutcome — whichever OrderChanged eventually reaches it — is then
    /// guaranteed to see the flag and report `bypassed`, never `stopped`, regardless of timing. The
    /// ONE other place that removes an entry is <see cref="CancelAndReport"/>'s catch, when Cancel()
    /// itself throws; that path has already settled the order and REPORTED it as bypassed on the
    /// spot, so an entry gone that way is a verdict recorded, not one lost. Finding nothing here can
    /// therefore only mean one of: not an order this class cancelled, an entry already resolved and
    /// reported, or an order not keyed YET (cancelled while still id-less and not yet migrated — see
    /// <see cref="_pending"/>'s own doc).
    ///
    /// AND FINDING NOTHING NO LONGER LOSES ANYTHING (R5 review, finding 1). That last case is not a
    /// harmless empty lookup: it is the ORDINARY id-less sequence, and it used to end in a false
    /// `outcome: "stopped"`. The failure is now recorded in <see cref="_cancelFailedIds"/> before
    /// this lookup happens at all, whether or not an entry exists, and ResolveOutcome consults that
    /// record alongside the flag. So such an order either resolves as `bypassed`, or — if its id
    /// never reaches the migration, the gap parked in InspectCore — produces no verdict at all.
    /// Neither of those can be a false `stopped`.
    ///
    /// Called for EVERY OrdersCancelFailed from OrderMonitor, unconditionally: cheap and correct to
    /// be a no-op whenever this class is not the one who asked for that particular cancel.
    /// </summary>
    public void NotifyCancelFailed(Order? order, string? reason)
    {
        if (order == null) return;
        var id = order.Id ?? string.Empty;
        if (id.Length == 0) return;

        bool pendingKnown;
        bool alreadyFlagged;
        lock (_lock)
        {
            // RECORDED BEFORE ANYTHING IS LOOKED UP, AND WHETHER OR NOT AN ENTRY EXISTS (R5 review,
            // finding 1). This used to return right here when _pending had nothing for this id, and
            // on the id-less path it ordinarily has nothing yet — the migration that creates the
            // entry only runs on the NEXT inspection. The fact was dropped and the order later
            // resolved as `stopped`. See _cancelFailedIds for the full sequence.
            _cancelFailedIds.Add(id);

            if (_pending.TryGetValue(id, out var entry))
            {
                pendingKnown = true;
                alreadyFlagged = entry.CancelFailed;
                entry.CancelFailed = true;
            }
            else
            {
                pendingKnown = false;
                alreadyFlagged = false;
            }
        }

        // Logged once per order, not once per retry: a broker can reject a cancel repeatedly while
        // the platform keeps re-trying it, and the fact this class cares about — "do not credit this
        // order as stopped" — only needs recording once.
        //
        // Still gated on a pending entry, unchanged from before R5: this method is called for EVERY
        // OrdersCancelFailed the platform raises, including cancels no part of the deck ever asked
        // for, and the line below asserts that the guard's OWN cancel was refused. The record above
        // needs no such proof; this log line does. Nothing is lost by the gate that was not lost
        // before it: if the order is one of ours, the fact reaches the log at resolution instead, in
        // ResolveOutcome's GUARD BYPASSED line — whenever that order reaches a verdict at all (for
        // when it does not, see the parked gap in InspectCore).
        if (!pendingKnown || alreadyFlagged) return;

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
    /// OrderChanged to retry. (The rest of that R2 sentence — "and NotifyCancelFailed can always
    /// find the entry because nothing ever removes it early" — was dropped in R5, on two counts:
    /// CancelAndReport's catch is a second remover, and NotifyCancelFailed no longer depends on
    /// finding an entry at all, since it records the failure in <see cref="_cancelFailedIds"/>
    /// whether one exists or not.)
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

            // BOTH SOURCES OF THE SAME FACT (R5 review, finding 1). The flag carries a cancel
            // failure reported while this entry existed; _cancelFailedIds carries one reported
            // BEFORE it existed, which on the id-less path is the ordinary order of events — the
            // entry is only built later, by the migration in InspectCore, with the flag necessarily
            // cleared. Remove() is on its own line rather than folded into the || below because ||
            // short-circuits: skipping the removal whenever the flag happened to be set would leak
            // that id for the rest of the session.
            var recordedFailure = _cancelFailedIds.Remove(id);
            cancelFailed = entry.CancelFailed || recordedFailure;
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
