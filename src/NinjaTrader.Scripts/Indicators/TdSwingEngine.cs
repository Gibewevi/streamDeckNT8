#region Using declarations
using System.Collections.Generic;
#endregion

// This namespace holds Indicators in this folder and is required. Do not change it.
namespace NinjaTrader.NinjaScript.Indicators
{
    /// <summary>
    /// One confirmed swing pivot.
    ///
    /// ConfirmationBar is the load-bearing field, not ExtremeBar. A pivot only becomes knowable
    /// once price has retraced away from it, so a consumer reading "the last low" must read the
    /// last low CONFIRMED BY NOW — never the last low that exists on the finished chart. Indexing
    /// on ExtremeBar is how a backtest quietly reads the future and looks better than the live
    /// system ever will.
    /// </summary>
    public class TdSwingPivot
    {
        /// <summary>Bar carrying the high or low itself. Where the dot is drawn.</summary>
        public int ExtremeBar;

        /// <summary>Bar on which the retracement threshold was crossed. When it became knowable.</summary>
        public int ConfirmationBar;

        public double Price;

        /// <summary>True for a swing high, false for a swing low.</summary>
        public bool IsHigh;

        /// <summary>True for the structural tier, false for the intermediate one.</summary>
        public bool IsMajor;

        public bool HasPrevious;
        public int PreviousExtremeBar;
        public double PreviousPrice;
    }

    /// <summary>
    /// The swing detection itself, as a plain class rather than an indicator.
    ///
    /// WHY NOT AN INDICATOR HOSTING ANOTHER. NinjaScript generates the `TdSwingStructure(...)`
    /// wrapper method from indicators it already knows, so a brand new indicator has no wrapper
    /// until the compile AFTER the one that introduced it. Anything calling it therefore fails on
    /// first compile — and since a NinjaScript compile is all-or-nothing, that failure also keeps
    /// the indicator itself out of the assembly. A plain class has no wrapper, no generation step
    /// and no compile order: every consumer simply owns an instance and feeds it its own bars.
    ///
    /// The algorithm: an alternating state machine looking for a high, then a low, then a high. A
    /// running extreme is extended for as long as price keeps making one, and is confirmed as a
    /// pivot only once price has retraced by the caller's threshold. Alternation is therefore
    /// guaranteed by construction, and noise is removed by amplitude rather than by bar count.
    /// </summary>
    public class TdSwingEngine
    {
        private readonly Tracker _major = new Tracker();
        private readonly Tracker _minor = new Tracker();

        private readonly List<TdSwingPivot> _pivots = new List<TdSwingPivot>();
        private readonly List<TdSwingPivot> _justConfirmed = new List<TdSwingPivot>();

        private readonly int _maxStored;

        /// <param name="maxStoredPivots">
        /// Rolling cap on the history. A full backtest confirms thousands of pivots and none of the
        /// consumers look further back than a few dozen.
        /// </param>
        public TdSwingEngine(int maxStoredPivots)
        {
            _maxStored = maxStoredPivots < 16 ? 16 : maxStoredPivots;
        }

        /// <summary>Confirmed pivots, oldest first — so the order is confirmation order.</summary>
        public IList<TdSwingPivot> Pivots { get { return _pivots; } }

        /// <summary>Pivots confirmed by the most recent Update call. Cleared on every call.</summary>
        public IList<TdSwingPivot> JustConfirmed { get { return _justConfirmed; } }

        /// <summary>
        /// Feeds one bar. Thresholds are passed in price units, already scaled by the caller, so the
        /// engine holds no opinion on ATR, ticks or regime.
        /// </summary>
        public void Update(int bar, double high, double low, double majorThreshold, double minorThreshold, int minBarsBetweenPivots)
        {
            _justConfirmed.Clear();
            Advance(_major, true, bar, high, low, majorThreshold, minBarsBetweenPivots);
            Advance(_minor, false, bar, high, low, minorThreshold, minBarsBetweenPivots);
        }

        /// <summary>
        /// The confirmation bar is always the FIRST bar whose retracement crosses the threshold, so
        /// the opposite extreme of the new leg is necessarily this bar's own high or low: any
        /// earlier bar that had gone further would have crossed the threshold before it.
        /// </summary>
        private void Advance(Tracker t, bool isMajor, int bar, double high, double low, double threshold, int minBarsBetweenPivots)
        {
            if (!t.Started)
            {
                // Seeded looking for a high, arbitrarily. Whichever side is wrong costs at most one
                // wave: the first confirmation flips the machine and the alternation settles.
                t.Started = true;
                t.Direction = 1;
                t.Extreme = high;
                t.ExtremeBar = bar;
                t.LastPivotBar = bar;
                return;
            }

            bool seekingHigh = t.Direction > 0;

            if (seekingHigh)
            {
                if (high > t.Extreme)
                {
                    t.Extreme = high;
                    t.ExtremeBar = bar;
                    return;
                }

                if (t.Extreme - low < threshold)
                    return;
            }
            else
            {
                if (low < t.Extreme)
                {
                    t.Extreme = low;
                    t.ExtremeBar = bar;
                    return;
                }

                if (high - t.Extreme < threshold)
                    return;
            }

            // Measured from the previous PIVOT and not from the previous confirmation, so the gate
            // cannot deadlock: the current bar keeps advancing while the pivot bar is fixed.
            if (bar - t.LastPivotBar < minBarsBetweenPivots)
                return;

            var pivot = new TdSwingPivot();
            pivot.ExtremeBar = t.ExtremeBar;
            pivot.ConfirmationBar = bar;
            pivot.Price = t.Extreme;
            pivot.IsHigh = seekingHigh;
            pivot.IsMajor = isMajor;
            pivot.HasPrevious = t.HasLastPivot;
            pivot.PreviousExtremeBar = t.LastPivotBar;
            pivot.PreviousPrice = t.LastPivotPrice;

            _pivots.Add(pivot);
            _justConfirmed.Add(pivot);
            if (_pivots.Count > _maxStored)
                _pivots.RemoveAt(0);

            t.HasLastPivot = true;
            t.LastPivotBar = pivot.ExtremeBar;
            t.LastPivotPrice = pivot.Price;

            t.Direction = seekingHigh ? -1 : 1;
            t.Extreme = seekingHigh ? low : high;
            t.ExtremeBar = bar;
        }

        /// <summary>One alternating detector. Two instances run side by side on different thresholds.</summary>
        private class Tracker
        {
            public bool Started;
            /// <summary>+1 = looking for a high, -1 = looking for a low.</summary>
            public int Direction;
            public double Extreme;
            public int ExtremeBar;

            public bool HasLastPivot;
            public int LastPivotBar;
            public double LastPivotPrice;
        }
    }
}
