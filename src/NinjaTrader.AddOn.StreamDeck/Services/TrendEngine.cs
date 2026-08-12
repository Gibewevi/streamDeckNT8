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
    /// forming bar's close is the live price, so a gate reading it would allow an entry and refuse
    /// the same one seconds later, with nothing on the deck to explain the difference.
    ///
    /// A Heikin Ashi mode lived here and was removed. It answered the same question far less well —
    /// the colour of one HA candle is a single-bar statistic that flips on every pullback, and
    /// being a fixed formula it had nothing to tune — while costing a second code path, a doji
    /// threshold of its own, a setting on the key, and a field in the protocol. One method that
    /// works beats two that must both be explained.
    /// </summary>
    public class TrendEngine
    {
        /// <summary>
        /// Wilder's ATR period. 20 matches the default the market-structure study settled on, so a
        /// threshold calibrated on the chart means the same thing here.
        /// </summary>
        private const int AtrPeriod = 20;

        /// <summary>
        /// Bars needed before any answer is given: the ATR window, plus enough beyond it for the
        /// alternating swing machine to have confirmed a high AND a low. Below that there is no
        /// structure to break out of, and answering would mean answering from one extreme.
        /// </summary>
        public const int MinBarsForVerdict = AtrPeriod + 40;

        /// <summary>
        /// Floor under the swing threshold, in ticks. Same guard TdSwingStructure applies: on a
        /// flat series the ATR collapses towards zero and every one-tick wiggle would confirm a
        /// pivot, turning the direction into noise at exactly the times it matters least.
        /// </summary>
        private const int MinThresholdTicks = 2;

        private readonly double _tickSize;
        private readonly double _thresholdAtr;
        private readonly TdSwingEngine _swings = new TdSwingEngine(64);

        private int _barCount;

        // --- Wilder ATR state ---
        private double _atr;
        private int _atrSamples;
        private double _trSum;
        private bool _hasPreviousClose;
        private double _previousClose;

        private TrendDirection _direction = TrendDirection.Neutral;

        /// <param name="tickSize">
        /// Instrument tick size, for the threshold floor. A zero or negative value simply disables
        /// that floor rather than throwing — an unresolved instrument must not take the engine down.
        /// </param>
        /// <param name="thresholdAtr">
        /// Swing amplitude in ATR multiples. The study's INTERMEDIATE level (1.0) is the right
        /// default here, not the structural one (2.5): the structural tier exists to find liquidity
        /// pockets, the intermediate tier exists to catch trend pullbacks — which is exactly the
        /// question a direction asks.
        /// </param>
        public TrendEngine(double tickSize, double thresholdAtr)
        {
            _tickSize = tickSize > 0 ? tickSize : 0;
            _thresholdAtr = thresholdAtr > 0 ? thresholdAtr : 1.0;
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
        /// rebuilds the engine from scratch rather than replaying, because both the ATR smoothing
        /// and the swing machine are order-dependent and a repeat would corrupt them in silence.
        /// </summary>
        public void AddBar(double high, double low, double close)
        {
            UpdateAtr(high, low, close);
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
            var threshold = Math.Max(_atr * _thresholdAtr, _tickSize * MinThresholdTicks);
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
