using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using NinjaTrader.Cbi;
using NinjaTrader.NinjaScript.AddOns.StreamDeck.Utilities;

namespace NinjaTrader.NinjaScript.AddOns.StreamDeck.Services
{
    /// <summary>
    /// Records every fill to a local append-only spool, one JSON object per line.
    ///
    /// This add-on is the only component that sees the truth: fills reported by NinjaTrader
    /// itself, including orders placed outside the deck (SuperDOM, Chart Trader, DOM). The bridge
    /// and the host only ever see what passes through them, which is a subset.
    ///
    /// Three rules govern this class, and they outrank every other consideration:
    ///
    ///   1. It must never be able to break trading. It runs inside NinjaTrader's event pipeline:
    ///      no exception escapes, no network call, no blocking wait. Append a line, nothing else.
    ///   2. No external dependency — SimpleJson only, like the rest of the add-on. NinjaScript
    ///      compiles these sources as they are.
    ///   3. Point value and tick size are captured at fill time. The server can then compute P&amp;L
    ///      without an instrument table of its own, and contracts Bitlearn has never heard of
    ///      still produce correct numbers.
    ///
    /// Recording is local and unconditional; *uploading* is what the user opts into, per account,
    /// and the host filters on that. Writing a line to the trader's own disk is not transmission,
    /// and a spool that only starts once the toggle is flipped would lose the session that made
    /// the trader want the journal in the first place.
    /// </summary>
    public class ExecutionRecorder : IDisposable
    {
        /// <summary>Kept beside the logs: one place to look, one place to clean up.</summary>
        private const string FolderName = "journal";

        /// <summary>
        /// Beyond this, a spool that was never uploaded is a disk leak rather than a safety net.
        /// The host removes what it has sent; this is the backstop for a host that never runs.
        /// </summary>
        private const int RetentionDays = 30;

        private readonly object _sync = new object();
        private readonly string _directory;

        private StreamWriter _writer;
        private DateTime _openDate = DateTime.MinValue;
        private bool _disposed;
        private bool _sinkFailed;

        public ExecutionRecorder(string directory = null)
        {
            _directory = directory ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "StreamDeckTrader", FolderName);
        }

        /// <summary>
        /// Handles an ExecutionUpdate from the tracked account.
        ///
        /// Swallows everything: an exception raised here would travel up NinjaTrader's event
        /// pipeline, and losing a journal line must never cost a trading session.
        /// </summary>
        public void OnExecutionUpdate(object sender, ExecutionEventArgs e)
        {
            try
            {
                if (_disposed || e == null || e.Execution == null) return;
                Write(Describe(e.Execution));
            }
            catch (Exception ex)
            {
                SdLogger.Fail("Journal", ex, "Execution not recorded");
            }
        }

        /// <summary>
        /// Flattens an execution into the record the server expects.
        ///
        /// Every member is read defensively: a partially populated Execution is rare but real
        /// during a disconnect, and a null reference here would be indistinguishable from a
        /// trading fault in the logs.
        /// </summary>
        private static Dictionary<string, object> Describe(Execution exec)
        {
            var record = new Dictionary<string, object>();

            record["kind"] = "exec";
            record["execId"] = exec.ExecutionId ?? string.Empty;
            record["orderId"] = exec.Order != null ? (exec.Order.OrderId ?? string.Empty) : string.Empty;
            record["account"] = exec.Account != null ? (exec.Account.Name ?? string.Empty) : string.Empty;
            record["marketPosition"] = exec.MarketPosition.ToString();
            record["price"] = exec.Price;
            record["quantity"] = exec.Quantity;
            record["commission"] = exec.Commission;

            // Order name distinguishes what the deck sent ("StreamDeck") from what the trader
            // placed by hand. That distinction is the whole point of the behavioural statistics:
            // an order that never went through the deck is exactly what the guard rules cannot
            // prevent, only report.
            record["orderName"] = exec.Name ?? string.Empty;

            var instrument = exec.Instrument;
            if (instrument != null)
            {
                record["instrument"] = instrument.FullName ?? string.Empty;
                var master = instrument.MasterInstrument;
                if (master != null)
                {
                    record["pointValue"] = master.PointValue;
                    record["tickSize"] = master.TickSize;
                }
            }

            // Two timestamps, deliberately. `time` is when the fill happened; `recordedAtUtc` is
            // when we saw it. Keeping both lets the server reconcile ordering.
            //
            // `time` MUST carry its offset. NinjaTrader hands back a DateTime whose Kind is
            // Unspecified, and an ISO-8601 string without a zone is read as the *reader's* local
            // time — so a fill at 12:42 in New York became 12:42 UTC once ingested on a server in
            // another zone, four hours off. The value is the trader's local time by construction,
            // so we say so explicitly rather than let each reader guess.
            var quand = exec.Time.Kind == DateTimeKind.Unspecified
                ? DateTime.SpecifyKind(exec.Time, DateTimeKind.Local)
                : exec.Time;
            record["time"] = new DateTimeOffset(quand).ToString("o", CultureInfo.InvariantCulture);
            record["recordedAtUtc"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);

            return record;
        }

        private void Write(Dictionary<string, object> record)
        {
            var line = SimpleJson.Serialize(record);

            lock (_sync)
            {
                var writer = EnsureWriter();
                if (writer == null) return;
                writer.WriteLine(line);
            }
        }

        /// <summary>
        /// Opens today's spool, rolling at midnight. Returns null when the sink is unusable —
        /// a read-only profile, a full disk — and says so once instead of on every fill.
        /// </summary>
        private StreamWriter EnsureWriter()
        {
            var today = DateTime.UtcNow.Date;

            if (_writer != null && today != _openDate)
            {
                CloseWriter();
            }

            if (_writer != null) return _writer;
            if (_sinkFailed) return null;

            try
            {
                Directory.CreateDirectory(_directory);
                _openDate = today;

                var path = Path.Combine(
                    _directory,
                    string.Format(CultureInfo.InvariantCulture, "executions-{0:yyyy-MM-dd}.ndjson", today));

                // FileShare.ReadWrite so the host can upload what is already written while this
                // process keeps appending — the two never coordinate, and must not have to.
                var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);

                // No BOM, unlike the log files. This one is parsed line by line by a program, and
                // a byte-order mark would sit in front of the first record and break it.
                _writer = new StreamWriter(stream, new UTF8Encoding(false));
                _writer.AutoFlush = true;

                PurgeOldFiles();
                SdLogger.Info("Journal spool opened at {0}", path);
                return _writer;
            }
            catch (Exception ex)
            {
                _sinkFailed = true;
                SdLogger.Fail("Journal", ex, "Journal spool unavailable — executions will not be recorded");
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
                SdLogger.Warn("Journal purge skipped: {0}", ex.Message);
            }
        }

        private void CloseWriter()
        {
            try
            {
                if (_writer != null) _writer.Dispose();
            }
            catch (Exception ex)
            {
                SdLogger.Warn("Journal spool not closed cleanly: {0}", ex.Message);
            }
            finally
            {
                _writer = null;
            }
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
}
