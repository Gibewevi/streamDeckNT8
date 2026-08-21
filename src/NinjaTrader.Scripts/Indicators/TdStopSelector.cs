#region Using declarations
using System.Collections.Generic;
#endregion

namespace TradeDeck.Analytics
{
    /// <summary>
    /// Chooses which confirmed pivot a stop belongs behind. Extracted from TdStopPlacement so that
    /// NinjaTrader and ATAS share the selection itself rather than two drifting copies of it.
    ///
    /// A CORRIDOR, NOT A REGIME CLASSIFIER. The natural framing is "detect the regime, then pick a
    /// major or a minor pivot". That is the wrong cut: a regime classifier flips exactly at the
    /// transitions, which is the worst possible moment, and the label is only ever a proxy for what
    /// actually matters — DISTANCE. Filtering candidates by a distance corridor makes the
    /// major/minor choice fall out on its own: in a sustained trend the pullback lows sit close, so
    /// the nearest one above the floor is usually a minor pivot; in a range the minor pivots are
    /// glued to price, drop below the floor, are eliminated, and the major pivot wins.
    ///
    /// WHY THE MOST RECENT PIVOT IS SKIPPED. Not merely because it is close. It is the most OBVIOUS
    /// level on the chart, so it is where every stop is parked, so it is where the sweep goes.
    /// Hard-coding "always take the second to last" would be wrong the other way, taking a
    /// needlessly wide stop whenever the last low is already deep — hence: step past the obvious
    /// level ONLY when another valid candidate exists.
    ///
    /// NO LOOK-AHEAD. Candidates are filtered on ConfirmationBar, never on the bar carrying the
    /// extreme. Reading the last low off the finished chart would quietly read the future.
    ///
    /// Distances are passed in PRICE UNITS, already scaled by the caller — like TdSwingEngine, this
    /// class holds no opinion on ATR, ticks or regime.
    /// </summary>
    public class TdStopSelector
    {
        /// <summary>Reused per call so the scan does not allocate on every bar.</summary>
        private readonly List<TdSwingPivot> _candidates = new List<TdSwingPivot>();

        /// <returns>False when nothing valid was found, which is the caller's cue to fall back.</returns>
        public bool TrySelectAnchor(
            IList<TdSwingPivot> pivots,
            int currentBar,
            bool isLong,
            double entry,
            double minDistance,
            double maxDistance,
            int lookbackPivots,
            bool skipMostRecent,
            out double anchor)
        {
            anchor = 0.0;
            _candidates.Clear();

            int inspected = 0;

            // Backwards, so index 0 of _candidates is the most recent qualifying level.
            for (int i = pivots.Count - 1; i >= 0 && inspected < lookbackPivots; i--)
            {
                var pivot = pivots[i];

                // A long is protected by a swing LOW, a short by a swing HIGH.
                if (pivot.IsHigh == isLong)
                    continue;

                inspected++;

                // Only levels already knowable at this bar. The engine appends on confirmation, so
                // this holds by construction — the test guards against a future change of order.
                if (pivot.ConfirmationBar > currentBar)
                    continue;

                double distance = isLong ? entry - pivot.Price : pivot.Price - entry;
                if (distance <= 0.0)
                    continue;
                if (distance < minDistance || distance > maxDistance)
                    continue;

                _candidates.Add(pivot);
            }

            if (_candidates.Count == 0)
                return false;

            // Step past the obvious level only when there is somewhere else to go.
            int start = skipMostRecent && _candidates.Count >= 2 ? 1 : 0;

            double best = 0.0;
            double bestDistance = double.MaxValue;
            for (int i = start; i < _candidates.Count; i++)
            {
                var pivot = _candidates[i];
                double distance = isLong ? entry - pivot.Price : pivot.Price - entry;
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = pivot.Price;
                }
            }

            anchor = best;
            return true;
        }
    }
}
