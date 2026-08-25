using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

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

    private StreamWriter? _writer;
    private DateTime _openDate = DateTime.MinValue;
    private bool _disposed;
    private bool _sinkFailed;

    /// <summary>Says the DateTimeKind of the first fill once, so the timestamps can be audited.</summary>
    private bool _kindReported;

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
            // explain. Skipped in silence at TRACE: a replay session raises thousands of these.
            if (trade.IsReplay())
            {
                SdLog.TraceEvent("Journal", "Replay fill ignored — {0}", trade.Id ?? "?");
                return;
            }

            if (AlreadyRecorded(trade)) return;

            var record = Describe(trade);
            Write(record);
        }
        catch (Exception ex)
        {
            SdLog.Fail("Journal", ex, "Execution not recorded");
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

        // Dictionary<string, object>, NOT <string, object?>: generics are invariant, and
        // JournalSeal.Stamp — the shared file — takes the non-nullable form. A null value still
        // goes in fine; only the type argument matters.
        var record = new Dictionary<string, object>
        {
            ["kind"] = "exec",
            ["execId"] = trade.Id ?? string.Empty,
            ["orderId"] = trade.OrderId ?? string.Empty,
            ["account"] = trade.AccountID ?? trade.Portfolio?.AccountID ?? string.Empty,
            // Buy means the fill took a long position, Sell a short. The server reads
            // "Long"/"Short", as the NinjaTrader add-on has always written it — mapping here keeps
            // one vocabulary across both platforms rather than teaching the server a second one.
            ["marketPosition"] = trade.OrderDirection == OrderDirections.Buy ? "Long" : "Short",
            ["price"] = trade.Price,
            ["quantity"] = trade.Volume,
            // Nullable, exactly as today: a commission the broker never reported must stay
            // distinguishable from a commission of zero.
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
            if (pointValue > 0m) record["pointValue"] = pointValue;
            else SdLog.EventWarn("Journal", "No point value for {0} (tickSize={1}, tickCost={2}) — the field is omitted",
                ContextResolver.InstrumentName(security), security.TickSize, security.TickCost);
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

    private void Write(Dictionary<string, object> record)
    {
        // Sealed BEFORE serialising: the signed line is the one that reaches the disk, leaving no
        // window during which the file holds an unsigned record.
        //
        // A null commission canonicalises to an empty string on both sides of the wire, which is
        // what keeps "the broker reported nothing" distinguishable from "the commission was zero".
        _seal.Stamp(record);

        var line = JsonConvert.SerializeObject(record);

        lock (_sync)
        {
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
