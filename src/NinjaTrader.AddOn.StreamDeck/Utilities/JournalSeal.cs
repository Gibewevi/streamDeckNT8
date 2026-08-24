using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;

// Deliberately NOT a NinjaTrader.* namespace. This file is compiled by BOTH NinjaScript and the
// ATAS project (see src/Atas.Strategy.TradeDeck), and a NinjaTrader.* namespace inside an ATAS
// assembly makes the next reader doubt what they are looking at. NinjaScript compiles every .cs
// under bin\Custom into one assembly and places no constraint on the namespace of a plain class.
//
// C# 9 / net48 ONLY, like every shared file: a file-scoped namespace, a record or a required member
// compiles on the ATAS side and breaks the trader's ENTIRE NinjaScript compilation, his own
// indicators included.
namespace TradeDeck.Journal
{
    /// <summary>
    /// Severity of a message this class reports back to its host. Kept as an enum rather than
    /// folded into "exception or not": the three levels below already exist in both hosts' logs,
    /// and collapsing them would file a missing key — routine, the deck is simply unpaired — at the
    /// same level as a sealing failure.
    /// </summary>
    public enum JournalSealLevel { Info, Warn, Error }

    /// <summary>
    /// Seals each journal line with a chained HMAC.
    ///
    /// The spool is a plain text file on the trader's own disk. Without a seal, editing it before
    /// upload is trivial and invisible — and the XP Bitlearn grants a session would be forgeable
    /// with Notepad.
    ///
    /// Every line carries a monotonic <c>seq</c> and
    /// <c>sig = HMAC-SHA256(key, seq ‖ previousSig ‖ canonical form)</c>. The chaining is the part
    /// that matters: a signature alone stops a line from being edited, the chain also stops one
    /// from being deleted, inserted or reordered — removing a line breaks the signature of the
    /// NEXT one, whose computation depended on the one just taken out.
    ///
    /// **What it does not buy.** The key lives on this machine, put there by the host. Whoever
    /// extracts it can forge a consistent chain. The seal raises the cost of forgery from a text
    /// editor to reverse engineering; it does not remove it, and there is no point stacking
    /// obfuscation on top. What makes the scheme hold is on the server: balance reconciliation,
    /// and an XP that never pays full rate for unverifiable data.
    ///
    /// ⚠ **The canonical form below must stay byte-for-byte identical to
    /// `Bitlearn/lib/tradeDeck/integrity.js` and `deck-host/src/seal.ts`.** If they drift by one
    /// character nothing seals any more, and every journal silently drops to the unverifiable tier
    /// — silently, because this side still believes it is signing correctly.
    ///
    /// Like everything else in the capture path: it never throws. A line that cannot be sealed is
    /// written UNSIGNED rather than lost. Losing a fill is permanent; losing a seal costs one
    /// session's integrity tier.
    /// </summary>
    public sealed class JournalSeal
    {
        /// <summary>Unit separator: a control character cannot come out of a JSON value, so no
        /// content can fake a field boundary.</summary>
        private const string Sep = "\u001f";

        /// <summary>Fields signed on an execution, in this order. Mirrors CHAMPS_EXEC.</summary>
        private static readonly string[] ExecFields =
        {
            "execId", "orderId", "account", "instrument", "marketPosition", "price", "quantity",
            "commission", "pointValue", "tickSize", "orderName", "trend", "time", "recordedAtUtc",
            "tradingDay"
        };

        /// <summary>Retry interval for a key that is not there yet — the deck may be unpaired.</summary>
        private static readonly TimeSpan KeyRetry = TimeSpan.FromMinutes(1);

        private readonly object _sync = new object();
        private readonly string _directory;

        /// <summary>
        /// Where this class says what happened. A callback rather than a direct logger call: the
        /// same file is compiled into the NinjaTrader add-on, which logs through SdLogger, and into
        /// the ATAS strategy, which logs through SdLog. Neither exists on the other side.
        /// Null is legal and means "say nothing" — losing a log line must never cost a seal.
        /// </summary>
        private readonly Action<JournalSealLevel, string, Exception> _report;

        private byte[] _key;
        private long _seq;
        private string _sig = string.Empty;
        private DateTime _lastKeyLook = DateTime.MinValue;
        private bool _failureReported;

        public JournalSeal(string directory, Action<JournalSealLevel, string, Exception> report = null)
        {
            _directory = directory;
            _report = report;
            LoadState();
        }

        /// <summary>Reports without ever being able to throw back into the capture path.</summary>
        private void Report(JournalSealLevel level, string message, Exception ex)
        {
            var report = _report;
            if (report == null) return;
            try { report(level, message, ex); }
            catch { /* a logger that throws must not cost the seal */ }
        }

        /// <summary>
        /// True once the key has been found. Looks for it rather than reporting a cached answer:
        /// the key usually appears AFTER this object is built — NinjaTrader is generally running
        /// well before the deck is paired — so a property that only reflected construction time
        /// would answer "no" for the rest of the session.
        /// </summary>
        public bool Active
        {
            get { lock (_sync) { EnsureKey(); return _key != null; } }
        }

        /// <summary>
        /// Stamps <paramref name="record"/> with its counter and signature, in place.
        ///
        /// Does nothing when no key is available: the line goes out unsigned, the server stores it
        /// anyway, and the session it belongs to lands on the unverifiable tier. That is the right
        /// treatment for a journal nobody can check — it still counts, it just counts for less.
        /// </summary>
        public void Stamp(Dictionary<string, object> record)
        {
            if (record == null) return;

            lock (_sync)
            {
                EnsureKey();
                if (_key == null) return;

                try
                {
                    // seq comes from the clock, not from a counter. If this state file disappears —
                    // a reinstalled profile, a cleaned disk — a counter restarting at 1 would
                    // condemn the device to never sealing anything again: the server reads
                    // anything at or below the last accepted seq as a replay. A clock keeps moving
                    // on its own. The +1 keeps it strictly increasing when two fills land inside
                    // the same millisecond.
                    var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    var seq = Math.Max(now, _seq + 1);

                    record["seq"] = seq;
                    var payload = seq.ToString(CultureInfo.InvariantCulture) + Sep + _sig + Sep + Canonical(record);

                    string sig;
                    using (var hmac = new HMACSHA256(_key))
                    {
                        sig = ToHex(hmac.ComputeHash(Encoding.UTF8.GetBytes(payload)));
                    }

                    record["sig"] = sig;
                    _seq = seq;
                    _sig = sig;
                    SaveState();
                }
                catch (Exception ex)
                {
                    record.Remove("seq");
                    record.Remove("sig");
                    if (!_failureReported)
                    {
                        _failureReported = true;
                        Report(JournalSealLevel.Error, "Sealing failed — journal lines will go out unsigned", ex);
                    }
                }
            }
        }

        /// <summary>
        /// Canonical form: a FIXED field list, never a reordered JSON dump.
        ///
        /// The line crosses two serialisations before it is checked — written here, re-parsed by
        /// the host, re-serialised by HTTP. The original byte string is long gone; only a form
        /// recomputable from the parsed object can be verified on the other side.
        /// </summary>
        private static string Canonical(Dictionary<string, object> record)
        {
            var sb = new StringBuilder("exec");
            foreach (var field in ExecFields)
            {
                sb.Append(Sep);
                object value;
                if (record.TryGetValue(field, out value)) sb.Append(Text(value));
            }
            return sb.ToString();
        }

        /// <summary>
        /// One number format for all three languages: eight decimals, trailing zeros removed.
        /// Matches `toFixed(8)` followed by a trailing-zero strip on the JavaScript side.
        ///
        /// A missing value writes an EMPTY string, never "0". A commission the broker never
        /// reported would otherwise be indistinguishable from a commission of zero, and the two
        /// sides would stop producing the same string the moment an optional field is absent.
        /// </summary>
        private static string Text(object value)
        {
            if (value == null) return string.Empty;
            if (value is bool) return (bool)value ? "true" : "false";
            if (value is double) return ((double)value).ToString("0.########", CultureInfo.InvariantCulture);
            if (value is float) return ((float)value).ToString("0.########", CultureInfo.InvariantCulture);
            if (value is decimal) return ((decimal)value).ToString("0.########", CultureInfo.InvariantCulture);
            if (value is int) return ((int)value).ToString(CultureInfo.InvariantCulture);
            if (value is long) return ((long)value).ToString(CultureInfo.InvariantCulture);
            return Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
        }

        private static string ToHex(byte[] bytes)
        {
            var sb = new StringBuilder(bytes.Length * 2);
            foreach (var b in bytes) sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
            return sb.ToString();
        }

        private string KeyPath()
        {
            return Path.Combine(_directory, "journal.key");
        }

        /// <summary>Own state file: the host writes its own, and two processes must not share one.</summary>
        private string StatePath()
        {
            return Path.Combine(_directory, "seal-state-exec.json");
        }

        /// <summary>
        /// Looks for the key the host drops in the journal folder at pairing time.
        ///
        /// Re-checked periodically rather than once at startup: NinjaTrader is very often running
        /// before the deck is paired, and a key read only at boot would leave a whole session
        /// unsigned for no reason.
        /// </summary>
        private void EnsureKey()
        {
            if (_key != null) return;
            if (DateTime.UtcNow - _lastKeyLook < KeyRetry) return;
            _lastKeyLook = DateTime.UtcNow;

            try
            {
                var path = KeyPath();
                if (!File.Exists(path)) return;

                var hex = File.ReadAllText(path).Trim();
                if (hex.Length != 64) return;

                var bytes = new byte[32];
                for (var i = 0; i < 32; i++)
                {
                    bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
                }
                _key = bytes;
                Report(JournalSealLevel.Info, "Journal sealing key loaded — fills will be signed", null);
            }
            catch (Exception ex)
            {
                Report(JournalSealLevel.Warn, "Journal sealing key unreadable", ex);
            }
        }

        /// <summary>
        /// Reloads seq and sig from the state file.
        ///
        /// Hand-parsed rather than run through a JSON library: this file has exactly two fields, it
        /// is written by this class alone, and the library that used to parse it is the one
        /// dependency that stopped this file from being shared with the ATAS build. The parser is
        /// deliberately permissive — anything it cannot read leaves the state at zero, which costs
        /// ONE chain break and never a session.
        /// </summary>
        private void LoadState()
        {
            try
            {
                var path = StatePath();
                if (!File.Exists(path)) return;

                var text = File.ReadAllText(path);

                long seq;
                if (TryReadNumber(text, "seq", out seq)) _seq = seq;

                string sig;
                if (TryReadString(text, "sig", out sig)) _sig = sig;
            }
            catch (Exception ex)
            {
                // State lost: the next line breaks the chain once, the server resynchronises on it,
                // and everything after is sealed again. One damaged session, not a dead device.
                Report(JournalSealLevel.Warn, "Journal seal state unreadable — one chain break is expected", ex);
            }
        }

        /// <summary>Reads <c>"name":123</c>. Returns false on anything else, including a decimal.</summary>
        private static bool TryReadNumber(string text, string name, out long value)
        {
            value = 0;
            var at = IndexOfValue(text, name);
            if (at < 0) return false;

            var end = at;
            while (end < text.Length && (char.IsDigit(text[end]) || (end == at && text[end] == '-'))) end++;
            if (end == at) return false;

            return long.TryParse(text.Substring(at, end - at), NumberStyles.Integer,
                CultureInfo.InvariantCulture, out value);
        }

        /// <summary>
        /// Reads <c>"name":"abc"</c>. No unescaping: the only string this file holds is a hex
        /// signature, which cannot contain a quote or a backslash.
        /// </summary>
        private static bool TryReadString(string text, string name, out string value)
        {
            value = null;
            var at = IndexOfValue(text, name);
            if (at < 0 || at >= text.Length || text[at] != '"') return false;

            var end = text.IndexOf('"', at + 1);
            if (end < 0) return false;

            value = text.Substring(at + 1, end - at - 1);
            return true;
        }

        /// <summary>Index of the first character AFTER <c>"name":</c>, or -1.</summary>
        private static int IndexOfValue(string text, string name)
        {
            var key = "\"" + name + "\"";
            var at = text.IndexOf(key, StringComparison.Ordinal);
            if (at < 0) return -1;

            at += key.Length;
            while (at < text.Length && (text[at] == ' ' || text[at] == ':')) at++;
            return at < text.Length ? at : -1;
        }

        private void SaveState()
        {
            try
            {
                Directory.CreateDirectory(_directory);

                // Built by hand for the same reason LoadState parses by hand. The signature is hex
                // by construction, so nothing here needs escaping — and the guard below makes that
                // an enforced fact rather than an assumption, because a stray quote would write a
                // state file this class could never read back.
                var sig = IsHex(_sig) ? _sig : string.Empty;
                var json = "{\"seq\":" + _seq.ToString(CultureInfo.InvariantCulture)
                         + ",\"sig\":\"" + sig + "\"}";

                File.WriteAllText(StatePath(), json, Encoding.UTF8);
            }
            catch
            {
                // Not blocking: at worst one chain break on the next start.
            }
        }

        private static bool IsHex(string value)
        {
            if (string.IsNullOrEmpty(value)) return true;
            for (var i = 0; i < value.Length; i++)
            {
                var c = value[i];
                var ok = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
                if (!ok) return false;
            }
            return true;
        }
    }
}
