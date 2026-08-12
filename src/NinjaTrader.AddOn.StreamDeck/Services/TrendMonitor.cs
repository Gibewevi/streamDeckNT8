using System;
using System.Collections.Generic;
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using NinjaTrader.NinjaScript.AddOns.StreamDeck.Utilities;

namespace NinjaTrader.NinjaScript.AddOns.StreamDeck.Services
{
    /// <summary>
    /// Loads the bars the trend needs and keeps a verdict ready for the state publisher.
    ///
    /// This is the ONLY market-data subscription in the add-on. Everything else it publishes comes
    /// from account and position objects; before this, the sole price anywhere in the deck was an
    /// opportunistic MarketData.Last.Price. The reason the calculation has to live here and nowhere
    /// else: neither the host (Node) nor the bridge (.NET 8) runs inside NinjaTrader, so neither
    /// can ask for a bar. No market data crosses the bridge — only the verdict does.
    ///
    /// THREADING. NinjaTrader raises <c>Update</c> on its data thread; the state publisher reads
    /// from a timer thread. Rather than lock the two against each other, the data thread computes a
    /// whole immutable <see cref="Verdict"/> and publishes it with a single reference assignment,
    /// and the timer thread only ever reads that reference. Nothing touches <c>Bars</c> off the
    /// data thread.
    ///
    /// The freshness test is applied by the READER and not by the writer, which is the one thing
    /// that cannot be got wrong here: a dead feed raises no Update at all, so a staleness check
    /// living in the update handler would be the one check that never runs precisely when it is
    /// needed. The verdict therefore carries the moment each series last advanced, and
    /// <see cref="BuildState"/> — driven by a timer that keeps ticking whatever the feed does —
    /// decides whether it is still worth anything.
    /// </summary>
    public class TrendMonitor : IDisposable
    {
        /// <summary>
        /// Bars requested per series. TrendEngine needs 60 before it answers at all, and the swing
        /// detector needs a good deal more than that before its alternating pivots mean anything.
        /// 300 one-minute bars is a session; on the higher timeframe it is several.
        /// </summary>
        private const int BarsBack = 300;

        /// <summary>
        /// A series is stale once it has gone this many periods without a new closed bar.
        ///
        /// Without it the worst failure mode of the whole feature is available: a frozen feed would
        /// leave the last known direction standing for hours, and — once the gate is armed in the
        /// next lot — it would keep refusing orders on it.
        ///
        /// Three periods and not two, with the floor below. NinjaTrader only creates a Minute bar
        /// when a trade prints in that minute, so a quiet overnight session leaves real gaps on a
        /// 1-minute series: a two-period tolerance would flap between a direction and NO DATA all
        /// night, on a feed that is perfectly alive.
        ///
        /// Measured against OUR clock since the bar arrived, deliberately, and never as the gap
        /// between now and the bar's own timestamp: NinjaTrader stamps bars in the user's
        /// configured display time zone, which is not necessarily this machine's. Subtracting one
        /// from the other would read hours of staleness on a healthy feed and switch the trend off
        /// for good.
        /// </summary>
        private const int StalePeriods = 3;

        /// <summary>
        /// Absolute floor under the staleness tolerance. Below this, a 1-minute series would be
        /// declared dead over an ordinary lull.
        /// </summary>
        private const int MinStaleMinutes = 5;

        private readonly object _lock = new object();

        private Series _reference;
        private Series _higher;

        private Instrument _instrument;
        private int _referenceMinutes = 1;
        private int _higherMinutes = 5;
        private bool _higherEnabled = true;
        private double _thresholdAtr = 1.0;

        private bool _disposed;

        /// <summary>
        /// Last time the series were (re)loaded, for the self-heal throttle below. Set by
        /// <see cref="Rebuild"/>, so the initial load counts as one and startup cannot thrash.
        /// </summary>
        private DateTime _lastLoadUtc = DateTime.MinValue;

        private static readonly TimeSpan SelfHealInterval = TimeSpan.FromMinutes(2);

        // Direction transitions are the trading narrative of this feature, so they are logged at
        // INFO — but only when they CHANGE. The publish loop runs twice a second.
        private string _lastLoggedVerdict;

        /// <summary>
        /// One answer, computed on the data thread and read on the timer thread. Immutable once
        /// published: every field is written before the reference assignment and never after.
        /// </summary>
        private class Verdict
        {
            public TrendDirection Reference;
            public TrendDirection Higher;

            /// <summary>True when the engine itself has an answer — enough bars, no load failure.</summary>
            public bool ReferenceReady;
            public bool HigherReady;

            /// <summary>UTC moment each series last gained a closed bar. MinValue = never.</summary>
            public DateTime ReferenceAdvanceUtc;
            public DateTime HigherAdvanceUtc;

            public int ReferenceMinutes;
            public int HigherMinutes;
            public bool HigherEnabled;

            /// <summary>
            /// Full name of the instrument these series were loaded for. Carried inside the verdict
            /// rather than read from <c>_instrument</c> at question time: the two would otherwise
            /// disagree for the moment between an instrument switch and the first bar of the new
            /// series, and that is exactly the moment a fill would be stamped with the wrong
            /// contract's direction.
            /// </summary>
            public string InstrumentName;
        }

        /// <summary>One timeframe: its request, its engine, and the last bar it consumed.</summary>
        private class Series
        {
            public BarsRequest Request;
            public TrendEngine Engine;
            public int Minutes;

            /// <summary>
            /// Timestamp of the newest CLOSED bar already folded into the engine, as the series
            /// itself stamps it. Identity, not a cursor — see <see cref="Consume"/>.
            /// </summary>
            public DateTime LastClosedBarTime;

            public DateTime LastAdvanceUtc;
            public bool Failed;
        }

        /// <summary>
        /// Latest verdict. Never null, so callers need no null check — "nothing known yet" is
        /// expressed as an unready series, which publishes available=false and refuses nothing.
        /// </summary>
        private volatile Verdict _verdict = new Verdict();

        public TrendMonitor()
        {
            _verdict = NewVerdict();
        }

        /// <summary>
        /// Adopts the trend settings pushed by the deck. Rebuilds the series only when something
        /// that shapes them actually changed: the host replays its whole configuration on every
        /// layout edit and every reconnection, so an unconditional rebuild would drop the loaded
        /// history several times a session for nothing.
        /// </summary>
        public void Configure(int referenceMinutes, bool higherEnabled, int higherMinutes, double thresholdAtr)
        {
            lock (_lock)
            {
                // Anything out of range keeps the current value. A field the host omitted arrives
                // here as 0, and 0 must mean "leave it alone" — never "reset the rule".
                var reference = Clamp(referenceMinutes, 1, 240, _referenceMinutes);
                var higher = Clamp(higherMinutes, 1, 1440, _higherMinutes);
                var threshold = thresholdAtr > 0 && thresholdAtr <= 10 ? thresholdAtr : _thresholdAtr;

                var changed = reference != _referenceMinutes
                              || higher != _higherMinutes
                              || higherEnabled != _higherEnabled
                              || Math.Abs(threshold - _thresholdAtr) > 0.0001;

                _referenceMinutes = reference;
                _higherMinutes = higher;
                _higherEnabled = higherEnabled;
                _thresholdAtr = threshold;

                if (!changed) return;

                SdLogger.Event("Trend", "Configured — reference={0}min higher={1} thresholdAtr={2}",
                    _referenceMinutes,
                    _higherEnabled ? _higherMinutes + "min" : "off", _thresholdAtr);

                Rebuild();
            }
        }

        /// <summary>
        /// Points the monitor at an instrument. Called on every publish tick, so it stays cheap and
        /// silent when nothing moved.
        ///
        /// The comparison is on the FULL name and not on the root the trader typed: ContextResolver
        /// turns "MNQ" into the current contract, so on roll day the same root resolves to a
        /// different instrument and the series must be reloaded. Comparing roots would leave the
        /// trend running on the expiring contract for the rest of the day.
        /// </summary>
        public void SetInstrument(Instrument instrument)
        {
            lock (_lock)
            {
                var currentName = _instrument != null ? _instrument.FullName : null;
                var nextName = instrument != null ? instrument.FullName : null;
                if (string.Equals(currentName, nextName, StringComparison.OrdinalIgnoreCase)) return;

                SdLogger.Event("Trend", "Instrument changed {0} → {1} — reloading bars",
                    currentName ?? "(none)", nextName ?? "(none)");

                _instrument = instrument;
                Rebuild();
            }
        }

        /// <summary>
        /// The <c>trend</c> block for the state update, and the place the freshness test is
        /// applied. Reads one volatile reference and a clock: no bar access, no lock, nothing that
        /// can block the publish loop.
        /// </summary>
        public Dictionary<string, object> BuildState()
        {
            var verdict = _verdict;

            var staleSeconds = 0;
            var direction = TrendDirection.Neutral;
            var available = Evaluate(verdict, out direction, out staleSeconds);

            var state = new Dictionary<string, object>();
            state["available"] = available;
            state["direction"] = TrendEngine.ToWire(direction);
            // The per-timeframe values are published even while unavailable: they are what makes a
            // NO DATA readable — the trader can see WHICH series is missing rather than guess.
            state["reference"] = TrendEngine.ToWire(verdict.Reference);
            state["higher"] = verdict.HigherEnabled ? TrendEngine.ToWire(verdict.Higher) : "";
            state["referenceMinutes"] = verdict.ReferenceMinutes;
            state["higherMinutes"] = verdict.HigherEnabled ? verdict.HigherMinutes : 0;
            state["staleSeconds"] = staleSeconds;

            LogVerdictChange(available, direction, verdict);
            if (!available) TrySelfHeal();
            return state;
        }

        /// <summary>
        /// The verdict for ONE instrument, as a wire value, or null when nothing can be said about
        /// it. Written on every fill so the journal can tell, months later, which way the market
        /// was pointing when the trader committed — a fact nobody can reconstruct afterwards, least
        /// of all a server that has never seen a bar.
        ///
        /// Null for three distinct situations, and they must stay indistinguishable to the caller
        /// because they all mean the same thing: WE DO NOT KNOW. No verdict yet, a stale series,
        /// or — the one this overload exists for — a fill on an instrument the monitor is not
        /// watching. The monitor follows the instrument selected on the deck; a trader who fills an
        /// order on another contract would otherwise get that contract's fill stamped with MNQ's
        /// direction, and nothing downstream could tell that fabricated fact from a measured one.
        ///
        /// Reads one volatile reference and a clock, like <see cref="BuildState"/>: it is called
        /// from NinjaTrader's execution pipeline, where blocking is not an option. Deliberately
        /// free of the self-heal and the transition log — a fill must not be able to trigger a
        /// series reload.
        /// </summary>
        public string DirectionFor(string instrumentFullName)
        {
            if (string.IsNullOrEmpty(instrumentFullName)) return null;

            var verdict = _verdict;
            if (!string.Equals(verdict.InstrumentName, instrumentFullName, StringComparison.OrdinalIgnoreCase))
                return null;

            var staleSeconds = 0;
            var direction = TrendDirection.Neutral;
            if (!Evaluate(verdict, out direction, out staleSeconds)) return null;

            return TrendEngine.ToWire(direction);
        }

        /// <summary>
        /// Turns a verdict into "is it usable, and what does it say". One implementation for the
        /// two readers: the state block and the journal must never be able to disagree about what
        /// the trend was at a given instant.
        /// </summary>
        private static bool Evaluate(Verdict verdict, out TrendDirection direction, out int staleSeconds)
        {
            var referenceStale = 0;
            var higherStale = 0;
            var referenceOk = IsUsable(verdict.ReferenceReady, verdict.ReferenceAdvanceUtc, verdict.ReferenceMinutes, out referenceStale);
            var higherOk = !verdict.HigherEnabled
                           || IsUsable(verdict.HigherReady, verdict.HigherAdvanceUtc, verdict.HigherMinutes, out higherStale);

            staleSeconds = Math.Max(referenceStale, higherStale);

            var available = referenceOk && higherOk;
            direction = available
                ? TrendEngine.Combine(verdict.Reference, verdict.Higher, verdict.HigherEnabled)
                : TrendDirection.Neutral;

            return available;
        }

        /// <summary>
        /// Reloads the series once the verdict has become unusable, and keeps trying.
        ///
        /// A watchdog that detects a dead series but cannot act on it only converts a silent
        /// failure into a permanent NO DATA. That is exactly what happened: nothing rebuilt a stuck
        /// series except a settings change or an instrument switch, so the key stayed dark until
        /// the trader happened to touch something — which is not a recovery path, it is a
        /// coincidence.
        ///
        /// Throttled, and the throttle is anchored on the last LOAD rather than on the last
        /// attempt: the first load therefore counts as one, so a series still warming up is left
        /// alone instead of being restarted under itself.
        /// </summary>
        private void TrySelfHeal()
        {
            if (DateTime.UtcNow - _lastLoadUtc < SelfHealInterval) return;

            lock (_lock)
            {
                if (_disposed || _instrument == null) return;
                if (DateTime.UtcNow - _lastLoadUtc < SelfHealInterval) return;

                SdLogger.EventWarn("Trend", "Trend unusable for {0:0}s — reloading the bars series",
                    (DateTime.UtcNow - _lastLoadUtc).TotalSeconds);
                Rebuild();
            }
        }

        /// <summary>
        /// Is this series' answer worth anything right now? False means "we do not know", which the
        /// deck renders as NO DATA and which refuses nothing — the same posture as
        /// pnlAvailable=false on the safety macro's loss rules. A discretionary filter must not
        /// lock the trader out of his own deck because a feed hiccuped.
        /// </summary>
        private static bool IsUsable(bool ready, DateTime lastAdvanceUtc, int minutes, out int staleSeconds)
        {
            staleSeconds = 0;
            if (!ready || lastAdvanceUtc == DateTime.MinValue) return false;

            var age = DateTime.UtcNow - lastAdvanceUtc;
            staleSeconds = age.TotalSeconds > 0 ? (int)age.TotalSeconds : 0;

            return age.TotalMinutes <= Math.Max(Math.Max(1, minutes) * StalePeriods, MinStaleMinutes);
        }

        private void LogVerdictChange(bool available, TrendDirection direction, Verdict verdict)
        {
            var signature = available
                ? direction + "|" + verdict.Reference + "|" + verdict.Higher
                : "unavailable";

            if (signature == _lastLoggedVerdict) return;
            _lastLoggedVerdict = signature;

            if (!available)
            {
                SdLogger.Event("Trend", "Trend UNAVAILABLE — bars missing, still loading, or stale");
                return;
            }

            SdLogger.Event("Trend", "Trend is {0} — {1}min={2}, {3}",
                TrendEngine.ToWire(direction).ToUpperInvariant(),
                verdict.ReferenceMinutes, TrendEngine.ToWire(verdict.Reference),
                verdict.HigherEnabled
                    ? verdict.HigherMinutes + "min=" + TrendEngine.ToWire(verdict.Higher)
                    : "higher timeframe off");
        }

        // --- series lifecycle (callers hold _lock) ---

        private void Rebuild()
        {
            Teardown();
            _lastLoggedVerdict = null;
            _lastLoadUtc = DateTime.UtcNow;

            if (!_disposed && _instrument != null)
            {
                _reference = StartSeries(_referenceMinutes);
                _higher = _higherEnabled ? StartSeries(_higherMinutes) : null;
            }

            Recompute();
        }

        private Series StartSeries(int minutes)
        {
            var series = new Series();
            series.Minutes = minutes;
            series.Engine = NewEngine();

            try
            {
                var request = new BarsRequest(_instrument, BarsBack);
                request.BarsPeriod = new BarsPeriod { BarsPeriodType = BarsPeriodType.Minute, Value = minutes };
                request.TradingHours = _instrument.MasterInstrument.TradingHours;
                series.Request = request;

                request.Update += OnBarsUpdate;
                request.Request(OnRequestCompleted);
            }
            catch (Exception ex)
            {
                // A series that cannot even be asked for must not take the add-on down — and must
                // not silently look like "no trend yet" forever either.
                series.Failed = true;
                SdLogger.Fail("Trend", ex, "Could not request {0}min bars for {1}",
                    minutes, _instrument != null ? _instrument.FullName : "(no instrument)");
            }

            return series;
        }

        private TrendEngine NewEngine()
        {
            var tickSize = 0.0;
            try { tickSize = _instrument.MasterInstrument.TickSize; } catch { }
            return new TrendEngine(tickSize, _thresholdAtr);
        }

        private void Teardown()
        {
            DisposeSeries(_reference);
            DisposeSeries(_higher);
            _reference = null;
            _higher = null;
        }

        private void DisposeSeries(Series series)
        {
            if (series == null || series.Request == null) return;

            try
            {
                series.Request.Update -= OnBarsUpdate;
                series.Request.Dispose();
            }
            catch (Exception ex)
            {
                SdLogger.Fail("Trend", ex, "Failed to release the {0}min bars request", series.Minutes);
            }
        }

        // --- NinjaTrader callbacks (data thread) ---

        private void OnRequestCompleted(BarsRequest request, ErrorCode errorCode, string message)
        {
            try
            {
                lock (_lock)
                {
                    // A callback from a request we have already replaced — instrument switched, or
                    // settings edited while the load was in flight. Same guard as the SuperDom
                    // volume column: answering for a dead request would feed one instrument's bars
                    // into another's engine.
                    var series = Match(request);
                    if (series == null) return;

                    if (errorCode == ErrorCode.UserAbort) return;

                    if (errorCode != ErrorCode.NoError)
                    {
                        series.Failed = true;
                        SdLogger.EventWarn("Trend", "{0}min bars unavailable for {1}: {2} — {3}",
                            series.Minutes, _instrument != null ? _instrument.FullName : "?",
                            errorCode, message ?? "(no message)");
                        Recompute();
                        return;
                    }

                    SdLogger.Event("Trend", "{0}min bars loaded for {1} — {2} bar(s)",
                        series.Minutes, _instrument != null ? _instrument.FullName : "?",
                        request.Bars != null ? request.Bars.Count : 0);

                    Consume(series);
                    Recompute();
                }
            }
            catch (Exception ex)
            {
                // This runs inside NinjaTrader's pipeline: an escaping exception destabilises the
                // platform itself, not just the deck.
                SdLogger.Fail("Trend", ex, "Bars request callback failed");
            }
        }

        private void OnBarsUpdate(object sender, BarsUpdateEventArgs e)
        {
            try
            {
                lock (_lock)
                {
                    var series = Match(sender as BarsRequest);
                    if (series == null) return;

                    Consume(series);
                    Recompute();
                }
            }
            catch (Exception ex)
            {
                SdLogger.Fail("Trend", ex, "Bars update handler failed");
            }
        }

        private Series Match(BarsRequest request)
        {
            if (request == null) return null;
            if (_reference != null && ReferenceEquals(_reference.Request, request)) return _reference;
            if (_higher != null && ReferenceEquals(_higher.Request, request)) return _higher;
            return null;
        }

        /// <summary>
        /// Rebuilds the engine from the loaded window whenever a new bar has CLOSED.
        ///
        /// Index Count-1 is the bar currently being built and is deliberately never fed: its close
        /// is the live price, so a gate reading it would allow an entry and refuse the same one
        /// seconds later, with nothing on the deck to explain the difference. The price of this
        /// choice is a lag of at most one bar at a turn, and that is the right trade.
        ///
        /// WHY THE WHOLE WINDOW, AND WHY A TIMESTAMP. <see cref="BarsBack"/> makes NinjaTrader keep
        /// a ROLLING window: when a bar closes, the oldest one drops out and <c>Bars.Count</c> stays
        /// at 300 forever. Two consequences, and the first one cost a release.
        ///
        /// Detecting "a bar closed" by watching Count therefore never fires — the trend loaded
        /// correctly, then went stale exactly two minutes later and stayed there, on every single
        /// cycle. Only a settings change or an instrument switch brought it back, because those
        /// rebuild the series. Identity now comes from the bar's OWN timestamp, compared against
        /// the previous one; that comparison is between two values from the same series, so the
        /// display-timezone hazard noted on StalePeriods does not apply here.
        ///
        /// And because the window rolls, an index does not name the same bar twice. Rather than
        /// track a cursor through shifting indices, the engine is simply rebuilt from the whole
        /// window — three hundred bars of a few flops, once a minute, is free, and it removes an
        /// entire class of off-by-one and missed-bar bugs. It also makes the verdict independent
        /// of when the add-on happened to start.
        /// </summary>
        private void Consume(Series series)
        {
            var bars = series.Request != null ? series.Request.Bars : null;
            if (bars == null) return;

            var newest = bars.Count - 2;
            if (newest < 0) return;

            var newestTime = bars.GetTime(newest);
            if (newestTime == series.LastClosedBarTime) return;

            var engine = NewEngine();
            for (var i = 0; i <= newest; i++)
            {
                engine.AddBar(bars.GetHigh(i), bars.GetLow(i), bars.GetClose(i));
            }

            series.Engine = engine;
            series.LastClosedBarTime = newestTime;
            series.LastAdvanceUtc = DateTime.UtcNow;
        }

        /// <summary>
        /// Snapshots the series into a fresh verdict and publishes it. Directions only — whether
        /// they are still fresh is the reader's business, for the reason given on the class.
        /// </summary>
        private void Recompute()
        {
            var verdict = NewVerdict();

            if (_reference != null && !_reference.Failed && _reference.Engine != null)
            {
                verdict.Reference = _reference.Engine.Direction;
                verdict.ReferenceReady = _reference.Engine.HasEnoughBars;
                verdict.ReferenceAdvanceUtc = _reference.LastAdvanceUtc;
            }

            if (_higher != null && !_higher.Failed && _higher.Engine != null)
            {
                verdict.Higher = _higher.Engine.Direction;
                verdict.HigherReady = _higher.Engine.HasEnoughBars;
                verdict.HigherAdvanceUtc = _higher.LastAdvanceUtc;
            }

            _verdict = verdict;
        }

        private Verdict NewVerdict()
        {
            var verdict = new Verdict();
            verdict.ReferenceMinutes = _referenceMinutes;
            verdict.HigherMinutes = _higherMinutes;
            verdict.HigherEnabled = _higherEnabled;
            verdict.InstrumentName = _instrument != null ? _instrument.FullName : null;
            return verdict;
        }

        private static int Clamp(int value, int min, int max, int fallback)
        {
            if (value < min || value > max) return fallback;
            return value;
        }

        public void Dispose()
        {
            lock (_lock)
            {
                if (_disposed) return;
                _disposed = true;
                Teardown();
                _verdict = NewVerdict();
            }
        }
    }
}
