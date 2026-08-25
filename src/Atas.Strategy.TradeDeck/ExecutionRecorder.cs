using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

using ATAS.DataFeedsCore;
using ATAS.Indicators;

using Newtonsoft.Json;

using TradeDeck.Journal;

namespace TradeDeck.Atas.Strategy;

/// <summary>
/// Records every fill to a local append-only spool, one JSON object per line.
///
/// This strategy is the only component that sees the truth: fills reported by the platform itself,
/// including orders placed outside the deck. The bridge and the host only ever see what passes
/// through them, which is a subset.
///
/// Three rules govern this class, and they outrank every other consideration:
///
///   1. It must never be able to break trading. It runs inside the connector's event pipeline: no
///      exception escapes, no network call, no blocking wait. Append a line, nothing else.
///   2. Point value and tick size are captured AT FILL TIME. The server can then compute P&amp;L
///      without an instrument table of its own — a fact that only exists at the instant of the
///      fill, which no amount of later work reconstructs.
///   3. The path, the file name and the field list are an INTERFACE, not a choice. The deck-host
///      reads this folder and seals nothing of its own here; changing any of the three silently
///      stops the journal from being uploaded.
///
/// Recording is local and unconditional; *uploading* is what the user opts into, per account, and
/// the host filters on that. Writing a line to the trader's own disk is not transmission, and a
/// spool that only started once the toggle was flipped would lose the session that made the trader
/// want the journal in the first place.
///
/// FIX ROUND 1: NewMyTrades is now subscribed on EVERY connector the registry aggregates
/// (OrderMonitor.AttachOne) — that fan-out is task 9's entire point, and it is also what makes two
/// connector threads able to call <see cref="Record"/> concurrently, which the single-account,
/// single-connector NinjaTrader reference never had to survive. Every lock in this file exists
/// because of that fan-out; see <see cref="Write"/> for the one that used to be missing.
/// </summary>
internal sealed class ExecutionRecorder : IDisposable
{
    /// <summary>Kept beside the logs: one place to look, one place to clean up.</summary>
    private const string FolderName = "journal";

    /// <summary>
    /// Beyond this, a spool that was never uploaded is a disk leak rather than a safety net. The
    /// host removes what it has sent; this is the backstop for a host that never runs.
    /// </summary>
    private const int RetentionDays = 30;

    /// <summary>
    /// Beyond this many remembered ids the oldest are forgotten. A fill that arrived more than four
    /// thousand fills ago is not about to be re-raised.
    /// </summary>
    private const int MaxSeenExecutions = 4000;

    /// <summary>
    /// How often a given repeating-warning key is allowed to fire — matches the cadence the
    /// NinjaTrader reference uses for its own analogous commission warning (WarnIfNoCommission,
    /// once an hour). See <see cref="ShouldWarn"/>.
    /// </summary>
    private static readonly TimeSpan WarnInterval = TimeSpan.FromHours(1);

    private readonly object _sync = new();
    private readonly string _directory;
    private readonly IOrderGateway _gateway;
    private readonly JournalSeal _seal;

    /// <summary>
    /// Executions already written, so the same fill is never journalled twice. A duplicated fill
    /// does not merely clutter the file: the Bitlearn journal would count the trade twice, and the
    /// P&amp;L it publishes would be twice what the trader made.
    /// </summary>
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
    private readonly Queue<string> _seenOrder = new();

    /// <summary>
    /// Last time each repeating-warning key fired — the shared limiter behind
    /// <see cref="ShouldWarn"/>. One dictionary, one lock, reused by every warning in this class
    /// (missing security, missing account, missing point value, zero commission, undecidable
    /// replay status) rather than each growing its own timestamp field, the way CLAUDE.md's named
    /// idioms (_lastPnlWarning, _consecutiveSkips) already do one at a time elsewhere.
    /// </summary>
    private readonly object _warnLock = new();
    private readonly Dictionary<string, DateTime> _lastWarnByKey = new(StringComparer.Ordinal);

    private StreamWriter? _writer;
    private DateTime _openDate = DateTime.MinValue;
    private bool _disposed;
    private bool _sinkFailed;

    /// <summary>Says the DateTimeKind of the first fill once, so the timestamps can be audited.</summary>
    private bool _kindReported;

    /// <summary>
    /// Fills dropped since the last "Journal:ReplayUnknown" warning was allowed through — see
    /// FIX ROUND 2, FINDING I3 at the call site. The rate limiter keeps the log quiet; this keeps
    /// the drop from being silent about HOW MANY. Interlocked rather than _warnLock: incremented
    /// from whichever connector thread is currently in Record, read/reset only inside the already
    /// rate-limited branch, and a log line's exact count being off by one under concurrent access
    /// costs nothing this class' three governing rules care about.
    /// </summary>
    private int _replayUnknownDropped;

    public ExecutionRecorder(IOrderGateway gateway, string? directory = null)
    {
        _gateway = gateway;
        _directory = directory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "StreamDeckTrader", FolderName);

        _seal = new JournalSeal(_directory, (level, message, ex) =>
        {
            if (level == JournalSealLevel.Error) SdLog.Fail("Journal", ex, "{0}", message);
            else if (level == JournalSealLevel.Warn) SdLog.Warn("{0}: {1}", message, ex?.Message ?? "-");
            else SdLog.Info("{0}", message);
        });
    }

    /// <summary>
    /// Handles one fill.
    ///
    /// Swallows everything: an exception raised here would travel up the connector's event pipeline,
    /// and losing a journal line must never cost a trading session.
    /// </summary>
    public void Record(MyTrade? trade)
    {
        try
        {
            if (_disposed || trade == null) return;

            // A REPLAY FILL IS NOT A TRADE. ATAS's Replay mode walks historical data and raises real
            // MyTrade objects; journalling them would send fabricated trades to Bitlearn as if the
            // trader had taken them, inflating — or destroying — statistics nobody could later
            // explain.
            //
            // FIX ROUND 1, FINDING I3: TryCheckReplay, not a bare trade.IsReplay(). The reviewer
            // decoded the IL: Extensions.IsReplay(MyTrade) reads trade.Portfolio.IsReplay(), and
            // Describe below already treats trade.Portfolio as possibly null. A null Portfolio must
            // therefore read as "cannot tell", never silently as "not a replay" — the opposite
            // failure mode from every other gap in this class: an incomplete-but-honest line is
            // recoverable, but a replay fill written to the file as a real trade is not, once
            // Bitlearn has ingested it.
            if (!TryCheckReplay(trade, out var isReplay))
            {
                // FIX ROUND 2, FINDING I3: the warning used to say nothing about HOW MANY fills were
                // dropped between two allowed log lines. A connector that never populates Portfolio
                // would empty the journal at up to one log line an hour — from the log alone that
                // looks like a quiet session, not an emptying one. Counted the same way SafeTrades
                // counts its own losses; reported and reset only when the limiter lets a line through.
                Interlocked.Increment(ref _replayUnknownDropped);
                if (ShouldWarn("Journal:ReplayUnknown"))
                {
                    var total = Interlocked.Exchange(ref _replayUnknownDropped, 0);
                    SdLog.EventWarn("Journal",
                        "Could not determine replay status for {0} fill(s) since the last warning (Portfolio missing, or the check itself faulted) — NOT recorded: writing a possible replay fill as a real trade would be unrecoverable, worse than skipping it. Most recent: {1}",
                        total, trade.Id ?? "?");
                }
                return;
            }

            if (isReplay)
            {
                // Skipped in silence at TRACE: a replay session raises thousands of these.
                SdLog.TraceEvent("Journal", "Replay fill ignored — {0}", trade.Id ?? "?");
                return;
            }

            if (AlreadyRecorded(trade)) return;

            var record = Describe(trade);
            Write(record);

            // Reference-ported check (NinjaTrader Services/ExecutionRecorder.cs, WarnIfNoCommission)
            // — see its own doc for the incident this guards against. Run after Write, same as the
            // reference: it never gates whether the line is written, only whether a warning follows.
            WarnIfNoCommission(trade, ResolveAccount(trade));
        }
        catch (Exception ex)
        {
            SdLog.Fail("Journal", ex, "Execution not recorded");
        }
    }

    /// <summary>
    /// Same defensive shape as ContextResolver.SafeIsReplay — but DELIBERATELY DIFFERENT failure
    /// semantics, which is why this is its own copy rather than a shared call (that one is also
    /// private to its class). ContextResolver's version fails OPEN (false = "not replay") because a
    /// false negative there costs one extra check on the next 500ms tick. Here a false negative
    /// costs a fabricated trade permanently written into the trader's P&amp;L history, so this one
    /// fails CLOSED: <paramref name="isReplay"/> is only meaningful when the return value is true.
    /// </summary>
    private static bool TryCheckReplay(MyTrade trade, out bool isReplay)
    {
        isReplay = false;
        try
        {
            // Extensions.IsReplay(MyTrade) is trade.Portfolio.IsReplay() under the hood (reviewer's
            // IL read) — a null Portfolio would NRE inside it. Checked here explicitly rather than
            // relying on the catch below so the "cannot tell" path is a deliberate branch, not an
            // incidental one.
            if (trade.Portfolio == null) return false;
            isReplay = trade.IsReplay();
            return true;
        }
        catch (Exception ex)
        {
            SdLog.TraceEvent("Journal", "IsReplay() faulted: {0}", ex.GetType().Name);
            return false;
        }
    }

    /// <summary>
    /// True when this exact fill has already been written.
    ///
    /// A fill without an id is recorded rather than dropped: an id is what makes deduplication
    /// possible, and its absence must not cost the line itself.
    /// </summary>
    private bool AlreadyRecorded(MyTrade trade)
    {
        var id = trade.Id;
        if (string.IsNullOrEmpty(id)) return false;

        lock (_sync)
        {
            if (!_seen.Add(id)) return true;
            _seenOrder.Enqueue(id);
            while (_seenOrder.Count > MaxSeenExecutions) _seen.Remove(_seenOrder.Dequeue());
            return false;
        }
    }

    /// <summary>The account this fill belongs to, or empty when neither source knows it.</summary>
    private static string ResolveAccount(MyTrade trade) =>
        trade.AccountID ?? trade.Portfolio?.AccountID ?? string.Empty;

    /// <summary>
    /// Flattens a fill into the record the server expects.
    ///
    /// Every member is read defensively: a partially populated fill is rare but real during a
    /// disconnect, and a null reference here would be indistinguishable from a trading fault in the
    /// logs.
    /// </summary>
    private Dictionary<string, object> Describe(MyTrade trade)
    {
        var security = trade.Security;
        var account = ResolveAccount(trade);

        // FIX ROUND 1, FINDING I1 (second half), CORRECTED IN ROUND 2. The round-1 comment claimed
        // deck-host drops a journal line with an empty account while advancing its cursor past it —
        // that was wrong, and stated as fact rather than checked. deck-host/src/uploader.ts's
        // `retenu(compte, filtre) = filtre.tous || filtre.noms.has(compte)` is gated by `filtre.tous`
        // alone: `comptesJournalises` only ever sets `tous`, never populates `noms` any more (dead
        // code, per its own comment), so an empty account is retained or dropped exactly like any
        // other line — purely by whether journalling is on for that connector's account key
        // elsewhere in the layout. Nothing here confirms what Bitlearn itself does with an account
        // it cannot attribute, since this port does not touch that server. What IS certain, and is
        // reason enough to keep this warning: an empty account gives the server nothing to attach
        // this fill's P&amp;L to, wherever in the pipeline that eventually surfaces.
        if (string.IsNullOrEmpty(account) && ShouldWarn("Journal:NoAccount"))
        {
            SdLog.EventWarn("Journal",
                "No account on fill {0} — nothing for the server to attribute this fill's P&L to once uploaded",
                trade.Id ?? "?");
        }

        // Dictionary<string, object>, NOT <string, object?>: generics are invariant, and
        // JournalSeal.Stamp — the shared file — takes the non-nullable form. A null value still
        // goes in fine; only the type argument matters.
        var record = new Dictionary<string, object>
        {
            ["kind"] = "exec",
            ["execId"] = trade.Id ?? string.Empty,
            ["orderId"] = trade.OrderId ?? string.Empty,
            ["account"] = account,
            // Buy means the fill took a long position, Sell a short. The server reads
            // "Long"/"Short", as the NinjaTrader add-on has always written it — mapping here keeps
            // one vocabulary across both platforms rather than teaching the server a second one.
            ["marketPosition"] = trade.OrderDirection == OrderDirections.Buy ? "Long" : "Short",
            ["price"] = trade.Price,
            ["quantity"] = trade.Volume,
            // Nullable, exactly as today: a commission the broker never reported must stay
            // distinguishable from a commission of zero. See WarnIfNoCommission for what a reported
            // zero specifically means.
            ["commission"] = trade.Commission,
            ["orderName"] = OrderName(trade),
        };

        // The trend verdict is deliberately OMITTED, never written as "neutral": "neutral" is a
        // verdict ("the market is going nowhere") and conflating it with "we could not tell" would
        // quietly count every fill as a trade taken with the trend. The trend macro is out of scope
        // for this port, so the server reads every ATAS fill as "unknown", which is exactly true.

        if (security != null)
        {
            record["instrument"] = ContextResolver.InstrumentName(security);
            record["tickSize"] = security.TickSize;

            // THE ONLY DERIVED FIELD, AND THE MOST SENSITIVE ONE. NinjaTrader hands PointValue over
            // ready-made; ATAS does not, so it comes from TickCost / TickSize. The server computes
            // the P&L of every journalled trade from this number and has no instrument table of its
            // own: wrong, it corrupts the statistics of a whole day without anything looking broken.
            //
            // Omitted rather than faked when it cannot be derived. An absent field is read as
            // unknown; a fabricated 1.0 would price a MNQ trade at a fiftieth of its value.
            var pointValue = ContextResolver.PointValue(security);
            if (pointValue > 0m)
            {
                record["pointValue"] = pointValue;
            }
            else if (ShouldWarn("Journal:NoPointValue:" + ContextResolver.InstrumentName(security)))
            {
                // FIX ROUND 2: keyed per instrument, not by a single shared "Journal:NoPointValue"
                // key — a second mispriced symbol within the same hour used to be swallowed by the
                // first one's rate limit even though it is a distinct, unrelated fact worth its own
                // line.
                SdLog.EventWarn("Journal", "No point value for {0} (tickSize={1}, tickCost={2}) — the field is omitted",
                    ContextResolver.InstrumentName(security), security.TickSize, security.TickCost);
            }
        }
        else if (ShouldWarn("Journal:NoSecurity"))
        {
            // FIX ROUND 1, FINDING I1 (first half). This branch used to not exist at all: instrument,
            // tickSize and pointValue were all silently absent with nothing said anywhere. The line
            // is still written — losing the whole fill would be worse than writing an unpriceable
            // one — but the gap has to be visible to someone reading the log, not just to the server.
            SdLog.EventWarn("Journal", "No Security on fill {0} — instrument, tickSize and pointValue are all omitted, the server cannot price this line",
                trade.Id ?? "?");
        }

        // Two timestamps, deliberately. `time` is when the fill happened; `recordedAtUtc` is when we
        // saw it. Keeping both lets the server reconcile ordering.
        //
        // `time` MUST carry its offset. An ISO-8601 string without a zone is read as the READER's
        // local time — a fill at 12:42 in New York became 12:42 UTC once ingested on a server in
        // another zone, four hours off. A DateTime whose Kind is Unspecified is treated as local,
        // as on the NinjaTrader side; see the verification step, which establishes what ATAS
        // actually hands over rather than assuming it.
        var when = trade.Time.Kind == DateTimeKind.Unspecified
            ? DateTime.SpecifyKind(trade.Time, DateTimeKind.Local)
            : trade.Time;

        if (!_kindReported)
        {
            _kindReported = true;
            SdLog.Event("Journal", "First fill timestamp: raw={0:o} kind={1} → recorded as {2}",
                trade.Time, trade.Time.Kind, new DateTimeOffset(when).ToString("o", CultureInfo.InvariantCulture));
        }

        record["time"] = new DateTimeOffset(when).ToString("o", CultureInfo.InvariantCulture);
        record["recordedAtUtc"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);

        // Trading day, computed exactly as the bridge computes it: DateTime.Now formatted as a local
        // date. The two run on the same machine, off the same clock, so they agree without any
        // message passing — and they MUST agree, because this is the window on which the guard
        // resets its daily loss and its trade count, and the window on which Bitlearn cuts the
        // session it scores.
        //
        // Deliberately NOT derived from the fill's own time: a fill can be reported minutes after
        // the fact, and around midnight that would file it under the wrong session.
        record["tradingDay"] = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        return record;
    }

    /// <summary>
    /// What the deck calls this order.
    ///
    /// It is what the server uses to tell an exit at the stop from an exit at the target, and to
    /// separate what the deck sent from what the trader placed by hand — the distinction the whole
    /// behavioural statistic rests on, since an order that never went through the deck is exactly
    /// what the guard rules cannot prevent, only report.
    ///
    /// ATAS has no Order.Name, so the comment carries it. When there is none — an order from the
    /// DOM, or from another strategy — the trading manager's own classification fills in, and the
    /// field is left empty rather than guessed.
    /// </summary>
    private string OrderName(MyTrade trade)
    {
        var order = trade.Order;
        if (order == null) return string.Empty;

        var comment = order.Comment;
        if (!string.IsNullOrWhiteSpace(comment)) return comment!;

        try
        {
            if (_gateway.IsStopLossOrder(order)) return OrderEngine.BracketStopComment;
            if (_gateway.IsTakeProfitOrder(order)) return OrderEngine.BracketTargetComment;
        }
        catch (Exception ex)
        {
            // Classification is a nicety; the fill is not. Never let it cost the line.
            SdLog.Warn("Order name not classified on a fill: {0}", ex.Message);
        }

        return string.Empty;
    }

    /// <summary>
    /// Says out loud that a fill carried no cost data, because the consequence is invisible
    /// otherwise: the Bitlearn journal computes net = gross - commission, so a fill priced without
    /// one silently publishes a GROSS P&amp;L that looks perfectly legitimate.
    ///
    /// Ported from the NinjaTrader add-on (Services/ExecutionRecorder.cs, WarnIfNoCommission) after
    /// a named incident there: months of live journals showing P&amp;L before costs, while the
    /// simulation account — which had a commission template — looked correct and hid the problem.
    ///
    /// FIX ROUND 2, FINDING I4: the round-1 version fired only on trade.Commission == 0m, which on
    /// a NULLABLE decimal means trade.Commission != 0m is a LIFTED comparison — true for null, so
    /// the check returned early and never warned on the value ATAS most likely uses to express "no
    /// cost data" in the first place. That is exactly the state this guard exists to catch: the
    /// reference's non-nullable double could only spell "no cost data" as 0.0, but ATAS's own
    /// nullable field can spell it as null OR 0m, and the incident this guard is named after does
    /// not care which encoding is in use. GetValueOrDefault() folds both into the same check.
    /// Warning, not error: the fill itself is still recorded correctly (a null stays distinguishable
    /// from a reported zero in the file itself — see the field's own comment in Describe — this
    /// warning only says a human should look), and a trader who genuinely pays no commission is
    /// entitled to a quiet log once the condition has been seen and warned about.
    /// </summary>
    private void WarnIfNoCommission(MyTrade trade, string account)
    {
        if (trade.Commission.GetValueOrDefault() != 0m) return; // a genuine non-zero value
        if (!ShouldWarn("Journal:NoCommission")) return;

        SdLog.EventWarn("Journal",
            "No commission reported on a fill for {0} (commission={1}) — verify the connector actually reports it, otherwise the Bitlearn journal publishes P&L GROSS of costs",
            string.IsNullOrEmpty(account) ? "(unknown account)" : account,
            trade.Commission.HasValue ? "0" : "null");
    }

    /// <summary>
    /// Rate-limits a repeating warning to once an hour per key.
    ///
    /// FIX ROUND 1, MINOR: the point-value warning used to fire on every single fill of a
    /// mispriced instrument — hundreds a day for an active scalper on one broken symbol. This is
    /// the one limiter every warning in this class shares (see <see cref="_lastWarnByKey"/>),
    /// following the same idiom CLAUDE.md names elsewhere (_lastPnlWarning, _consecutiveSkips):
    /// a condition that persists across many fills must cost one log line, not one per fill.
    /// </summary>
    private bool ShouldWarn(string key)
    {
        lock (_warnLock)
        {
            if (_lastWarnByKey.TryGetValue(key, out var last) && DateTime.UtcNow - last < WarnInterval)
                return false;
            _lastWarnByKey[key] = DateTime.UtcNow;
            return true;
        }
    }

    private void Write(Dictionary<string, object> record)
    {
        lock (_sync)
        {
            // FIX ROUND 1, FINDING C1 (critical). Sealing used to happen BEFORE this lock, on the
            // theory that "sealed before serialising" only needed to precede the write. That was
            // true for a single-threaded caller, and stopped being true the moment NewMyTrades was
            // wired to EVERY connector the registry aggregates (task 9's whole point,
            // OrderMonitor.AttachOne): two connector threads can now both reach Write() at once.
            // With Stamp() outside the lock, thread A could take seq=N, thread B take seq=N+1
            // chained on A's still-unwritten signature, and then B could win the race into
            // EnsureWriter/WriteLine first — the file would then hold seq N+1 before seq N.
            // JournalSeal's chain is built on the assumption that lines land in the order they were
            // signed (its own doc: removing a line breaks the NEXT one's signature; reordering does
            // exactly the same). deck-host/src/uploader.ts reads and ships lines in file order, so a
            // single crossed pair on a busy multi-account session would silently void the chain for
            // the rest of that day, with nothing in any log explaining why. Sealing and writing are
            // now one atomic step under the same lock EnsureWriter already needed.
            //
            // A null commission canonicalises to an empty string on both sides of the wire, which is
            // what keeps "the broker reported nothing" distinguishable from "the commission was zero".
            _seal.Stamp(record);

            var line = JsonConvert.SerializeObject(record);

            var writer = EnsureWriter();
            writer?.WriteLine(line);
        }
    }

    /// <summary>
    /// Opens today's spool, rolling at midnight. Returns null when the sink is unusable — a
    /// read-only profile, a full disk — and says so once instead of on every fill.
    /// </summary>
    private StreamWriter? EnsureWriter()
    {
        var today = DateTime.UtcNow.Date;

        if (_writer != null && today != _openDate) CloseWriter();
        if (_writer != null) return _writer;
        if (_sinkFailed) return null;

        try
        {
            Directory.CreateDirectory(_directory);
            _openDate = today;

            var path = Path.Combine(_directory,
                string.Format(CultureInfo.InvariantCulture, "executions-{0:yyyy-MM-dd}.ndjson", today));

            // FileShare.ReadWrite so the host can upload what is already written while this process
            // keeps appending — the two never coordinate, and must not have to.
            var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);

            // NO BOM, unlike the log files. This one is parsed line by line by a program, and a
            // byte-order mark would sit in front of the first record and break it.
            _writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };

            PurgeOldFiles();
            SdLog.Event("Journal", "Journal spool opened at {0}", path);
            return _writer;
        }
        catch (Exception ex)
        {
            _sinkFailed = true;
            SdLog.Fail("Journal", ex, "Journal spool unavailable — executions will not be recorded");
            return null;
        }
    }

    private void PurgeOldFiles()
    {
        try
        {
            var cutoff = DateTime.UtcNow.AddDays(-RetentionDays);
            foreach (var file in Directory.GetFiles(_directory, "executions-*.ndjson"))
            {
                if (File.GetLastWriteTimeUtc(file) < cutoff) File.Delete(file);
            }
        }
        catch (Exception ex)
        {
            // Housekeeping must never cost a fill: a file locked by the uploader is normal.
            SdLog.Warn("Journal purge skipped: {0}", ex.Message);
        }
    }

    private void CloseWriter()
    {
        try { _writer?.Dispose(); }
        catch (Exception ex) { SdLog.Warn("Journal spool not closed cleanly: {0}", ex.Message); }
        finally { _writer = null; }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            _disposed = true;
            CloseWriter();
        }
    }
}
