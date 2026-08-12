using System;
using NinjaTrader.NinjaScript.Indicators;

namespace NinjaTrader.NinjaScript.AddOns.StreamDeck.Services
{
    /// <summary>Which way one timeframe points. Neutral is a real answer, not a failure.</summary>
    public enum TrendDirection
    {
        Neutral = 0,
        Up = 1,
        Down = -1
    }

    /// <summary>How the direction is derived. Wire value, so the names must stay stable.</summary>
    public static class TrendMethods
    {
        public const string Structure = "structure";
        public const string HeikinAshi = "heikinAshi";

        public static string Normalize(string value)
        {
            return string.Equals(value, HeikinAshi, StringComparison.OrdinalIgnoreCase)
                ? HeikinAshi
                : Structure;
        }
    }

    /// <summary>
    /// Trend direction for ONE timeframe, from closed bars only.
    ///
    /// A plain class on purpose, exactly like <see cref="TdSwingEngine"/>: no NinjaScript base
    /// type, no indicator, no generated wrapper method. Two things follow, and both were the
    /// reason for the shape. It can be exercised from a throwaway net48 console project — the only
    /// automatable test in this whole feature — and the strategy filter sketched in the
    /// "Stratégies" study can reuse it without a copy.
    ///
    /// The caller feeds bars in order and only ever feeds CLOSED ones. That is not a detail: the
    /// forming bar's close is the live price, so its Heikin Ashi body flips several times a
    /// minute. A gate built on it would refuse an entry that was allowed five seconds earlier,
    /// with nothing on screen to explain why.
    /// </summary>
    public class TrendEngine
    {
        /// <summary>
        /// Wilder's ATR period. 20 matches the default the market-structure study settled on, so
        /// a threshold calibrated on the chart means the same thing here.
        /// </summary>
        private const int AtrPeriod = 20;

        /// <summary>
        /// Bars needed before any answer is given. The Heikin Ashi recursion converges
        /// geometrically — HAOpen[n] = (HAOpen[n-1] + HAClose[n-1]) / 2 halves the error left by
        /// whatever bar the series happened to start on — so 40 bars puts it a millionth of a
        /// point below the seed difference, far under a tick. The ATR needs its own window on top.
        /// </summary>
        public const int MinBarsForVerdict = AtrPeriod + 40;

        /// <summary>
        /// Share of ATR a Heikin Ashi body must reach to be allowed to flip the direction.
        ///
        /// A doji marks a pause INSIDE a trend, not a reversal, so a body under the threshold
        /// carries the previous direction forward rather than resetting to neutral. Reading a
        /// hesitation as "no trend" is how a filter ends up grey most of the day.
        /// </summary>
        private const double DojiBodyAtrShare = 0.1;

        /// <summary>
        /// Absolute floor for that threshold, in ticks. Same guard as TdSwingEngine's: on a flat
        /// series the ATR collapses and every one-tick body would qualify.
        /// </summary>
        private const int DojiBodyFloorTicks = 2;

        private readonly double _tickSize;
        private readonly string _method;
        private readonly double _thresholdAtr;

        // The swing detector is only built for the structure method. Instantiating it anyway
        // would be harmless but misleading: nothing should suggest Heikin Ashi consults pivots.
        private readonly TdSwingEngine _swings;

        private int _barCount;

        // --- Heikin Ashi recursion state ---
        private bool _haSeeded;
        private double _haOpen;
        private double _haClose;

        // --- Wilder ATR state ---
        private double _atr;
        private int _atrSamples;
        private double _trSum;
        private bool _hasPreviousClose;
        private double _previousClose;

        private TrendDirection _direction = TrendDirection.Neutral;

        /// <param name="tickSize">
        /// Instrument tick size, for the doji floor. A zero or negative value simply disables that
        /// floor rather than throwing — an unresolved instrument must not take the engine down.
        /// </param>
        /// <param name="thresholdAtr">
        /// Swing threshold in ATR multiples, structure method only. The study's INTERMEDIATE level
        /// (1.0) is the right default here, not the structural one (2.5): the structural tier
        /// exists to find liquidity pockets, the intermediate tier exists to catch trend pullbacks
        /// — which is exactly the question a direction asks.
        /// </param>
        public TrendEngine(double tickSize, string method, double thresholdAtr)
        {
            _tickSize = tickSize > 0 ? tickSize : 0;
            _method = TrendMethods.Normalize(method);
            _thresholdAtr = thresholdAtr > 0 ? thresholdAtr : 1.0;
            _swings = _method == TrendMethods.Structure ? new TdSwingEngine(64) : null;
        }

        /// <summary>Bars consumed so far. The caller uses it to decide whether to publish.</summary>
        public int BarCount { get { return _barCount; } }

        public bool HasEnoughBars { get { return _barCount >= MinBarsForVerdict; } }

        /// <summary>
        /// Current direction. Neutral until enough bars have been seen, so a half-loaded series
        /// never answers — the caller reports "no data" instead, which refuses nothing.
        /// </summary>
        public TrendDirection Direction
        {
            get { return HasEnoughBars ? _direction : TrendDirection.Neutral; }
        }

        /// <summary>
        /// Feeds one CLOSED bar. Bars must arrive in order and exactly once each; the caller
        /// rebuilds the engine from scratch rather than replaying, because both recursions here
        /// (Heikin Ashi and Wilder) would otherwise be silently corrupted by a repeat.
        /// </summary>
        public void AddBar(double open, double high, double low, double close)
        {
            UpdateAtr(high, low, close);
            UpdateHeikinAshi(open, high, low, close);

            if (_method == TrendMethods.HeikinAshi)
                UpdateFromHeikinAshi();
            else
                UpdateFromStructure(high, low, close);

            _barCount++;
        }

        /// <summary>Wilder's ATR: a simple average over the first window, then smoothed.</summary>
        private void UpdateAtr(double high, double low, double close)
        {
            var trueRange = high - low;
            if (_hasPreviousClose)
            {
                trueRange = Math.Max(trueRange, Math.Abs(high - _previousClose));
                trueRange = Math.Max(trueRange, Math.Abs(low - _previousClose));
            }

            if (_atrSamples < AtrPeriod)
            {
                _trSum += trueRange;
                _atrSamples++;
                _atr = _trSum / _atrSamples;
            }
            else
            {
                _atr = ((_atr * (AtrPeriod - 1)) + trueRange) / AtrPeriod;
            }

            _previousClose = close;
            _hasPreviousClose = true;
        }

        /// <summary>
        /// The four lines of the Heikin Ashi technique, identical to the HeikenAshi8 indicator the
        /// trader has on the chart. Recomputed here rather than requested as
        /// BarsPeriodType.HeikenAshi bars: that would be a SECOND series to load, and it would tie
        /// this engine to Heikin Ashi forever. One raw Minute series feeds every method.
        /// </summary>
        private void UpdateHeikinAshi(double open, double high, double low, double close)
        {
            if (!_haSeeded)
            {
                _haSeeded = true;
                _haOpen = open;
                _haClose = close;
                return;
            }

            var previousOpen = _haOpen;
            var previousClose = _haClose;

            _haClose = (open + high + low + close) * 0.25;
            _haOpen = (previousOpen + previousClose) * 0.5;
        }

        private void UpdateFromHeikinAshi()
        {
            var body = _haClose - _haOpen;
            if (Math.Abs(body) < DojiThreshold()) return;

            _direction = body > 0 ? TrendDirection.Up : TrendDirection.Down;
        }

        private double DojiThreshold()
        {
            var fromAtr = _atr * DojiBodyAtrShare;
            var floor = _tickSize * DojiBodyFloorTicks;
            return fromAtr > floor ? fromAtr : floor;
        }

        /// <summary>
        /// Market structure, and the direction flips on the BREAK rather than on the pivot.
        ///
        /// This is what sidesteps the limitation stated in docs/strategie-structure-marche.md — a
        /// pivot is only confirmable after price has retraced away from it, so anything keyed on
        /// confirmation is late by construction. The last confirmed high and low are known BEFORE
        /// the break; comparing the close against them costs nothing and is not late.
        ///
        /// It also buys hysteresis for free: flipping back requires taking out the opposite
        /// extreme, so an oscillation inside the range leaves the direction alone. That is the
        /// whole anti-whipsaw mechanism, and it needs no parameter of its own.
        /// </summary>
        private void UpdateFromStructure(double high, double low, double close)
        {
            var threshold = _atr * _thresholdAtr;
            if (threshold <= 0) return;

            // double.MaxValue on the structural tier so it never confirms. TdSwingEngine runs two
            // trackers into ONE pivot list; feeding both the same threshold would file every swing
            // twice and make "the last high" mean whichever copy came out on top.
            _swings.Update(_barCount, high, low, double.MaxValue, threshold, 3);

            var lastHigh = double.NaN;
            var lastLow = double.NaN;
            var pivots = _swings.Pivots;

            for (var i = pivots.Count - 1; i >= 0; i--)
            {
                var pivot = pivots[i];
                if (pivot.IsHigh)
                {
                    if (double.IsNaN(lastHigh)) lastHigh = pivot.Price;
                }
                else if (double.IsNaN(lastLow))
                {
                    lastLow = pivot.Price;
                }

                if (!double.IsNaN(lastHigh) && !double.IsNaN(lastLow)) break;
            }

            if (!double.IsNaN(lastHigh) && close > lastHigh)
                _direction = TrendDirection.Up;
            else if (!double.IsNaN(lastLow) && close < lastLow)
                _direction = TrendDirection.Down;
        }

        /// <summary>Wire value for a direction. Stable strings — the host renders on them.</summary>
        public static string ToWire(TrendDirection direction)
        {
            if (direction == TrendDirection.Up) return "up";
            if (direction == TrendDirection.Down) return "down";
            return "neutral";
        }

        /// <summary>
        /// Combines the reference timeframe with the optional higher one.
        ///
        /// Strict agreement: they must point the same way, and a disagreement resolves to neutral
        /// rather than to either side. Neutral is the honest answer there — the two timeframes are
        /// telling different stories, and picking one would hide that.
        /// </summary>
        public static TrendDirection Combine(TrendDirection reference, TrendDirection higher, bool higherEnabled)
        {
            if (!higherEnabled) return reference;
            return reference == higher ? reference : TrendDirection.Neutral;
        }
    }
}
