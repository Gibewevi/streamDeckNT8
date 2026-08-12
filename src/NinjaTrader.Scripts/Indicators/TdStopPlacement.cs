#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Windows.Media;
using System.Xml.Serialization;
using NinjaTrader.Gui;
using NinjaTrader.Data;
#endregion

// This namespace holds Indicators in this folder and is required. Do not change it.
namespace NinjaTrader.NinjaScript.Indicators
{
    /// <summary>Which side the preview assumes an entry was taken on.</summary>
    public enum TdStopPreviewDirection
    {
        /// <summary>Long above the EMA, short below it. Matches how the trade would actually be taken.</summary>
        SuivreEma,
        Achat,
        Vente
    }

    /// <summary>
    /// Chooses where a stop loss belongs, and previews it on every bar so the choice can be judged
    /// before a single order exists.
    ///
    /// A CORRIDOR, NOT A REGIME CLASSIFIER. The natural framing is "detect the regime, then pick a
    /// major or a minor pivot". That is the wrong cut: a regime classifier flips exactly at the
    /// transitions, which is the worst possible moment, and the label is only ever a proxy for what
    /// actually matters — DISTANCE. Filtering the candidate levels by a distance corridor instead
    /// makes the major/minor choice fall out on its own. In a sustained trend the pullback lows sit
    /// close, so the nearest one still above the floor is usually a minor pivot; in a range the
    /// minor pivots are glued to price, drop below the floor, are eliminated, and the major pivot
    /// wins. The regime is never named and the behaviour is obtained anyway, with two settings
    /// instead of six.
    ///
    /// WHY THE MOST RECENT PIVOT IS SKIPPED. Not merely because it is close. It is the most OBVIOUS
    /// level on the chart, so it is where every stop is parked, so it is where the sweep goes —
    /// these pivots are liquidity pockets, which is the whole reason they are worth detecting.
    /// Hard-coding "always take the second to last" would be wrong the other way, taking a needlessly
    /// wide stop whenever the last low is already deep. The rule is therefore: step past the obvious
    /// level ONLY when another valid candidate exists.
    ///
    /// NO LOOK-AHEAD. Candidates are filtered on ConfirmationBar, never on the bar carrying the
    /// extreme. A pivot is not knowable until price has retraced away from it, so reading the last
    /// low off the finished chart would quietly read the future and produce a preview far better
    /// than anything achievable live.
    ///
    /// IT PLACES NO ORDERS. This is a measuring instrument: a stop is computed for a hypothetical
    /// entry at the close of every bar, plotted, and scored.
    /// </summary>
    public class TdStopPlacement : Indicator
    {
        private const double MinimumThresholdTicks = 2.0;

        private ATR _atr;
        private EMA _ema;
        private TdSwingEngine _engine;

        /// <summary>Reused per bar so the selection does not allocate on every candidate scan.</summary>
        private readonly List<TdSwingPivot> _candidates = new List<TdSwingPivot>();

        private readonly List<PendingEntry> _pending = new List<PendingEntry>();
        private readonly List<double> _distanceTicks = new List<double>();
        private readonly List<double> _distanceAtr = new List<double>();
        private int _structuralCount;
        private int _fallbackCount;
        private int _resolved;
        private int _swept;

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name = "TdStopPlacement";
                Description = "Choisit un stop structurel par couloir de distance, avec repli ATR. Aperçu et mesure, aucun ordre.";

                Calculate = Calculate.OnBarClose;
                IsOverlay = true;
                DisplayInDataBox = false;
                DrawOnPricePanel = true;
                PaintPriceMarkers = false;
                IsSuspendedWhileInactive = true;

                AtrPeriod = 20;
                MajorThresholdAtr = 2.5;
                MinorThresholdAtr = 1.0;
                MinBarsBetweenPivots = 3;

                MinDistanceAtr = 1.0;
                MaxDistanceAtr = 3.0;
                BufferTicks = 4;
                SkipMostRecentPivot = true;
                LookbackPivots = 12;
                FallbackAtrMultiple = 2.0;

                PreviewDirection = TdStopPreviewDirection.SuivreEma;
                EmaPeriod = 40;
                SweepHorizonBars = 20;

                AddPlot(new Stroke(Brushes.Orange, 2), PlotStyle.Line, "StopStructurel");
                AddPlot(new Stroke(Brushes.DimGray, 2), PlotStyle.Line, "StopRepliAtr");
                AddPlot(new Stroke(Brushes.Gold, 3), PlotStyle.Dot, "NiveauRetenu");
            }
            else if (State == State.DataLoaded)
            {
                _atr = ATR(AtrPeriod);
                _ema = EMA(EmaPeriod);
                _engine = new TdSwingEngine(512);
            }
            else if (State == State.Terminated)
            {
                PrintStatistics();
            }
        }

        protected override void OnBarUpdate()
        {
            Values[0][0] = double.NaN;
            Values[1][0] = double.NaN;
            Values[2][0] = double.NaN;

            if (CurrentBar < AtrPeriod + 1)
                return;

            double atr = Math.Max(_atr[0], TickSize);
            double floor = MinimumThresholdTicks * TickSize;

            _engine.Update(
                CurrentBar,
                High[0],
                Low[0],
                Math.Max(atr * MajorThresholdAtr, floor),
                Math.Max(atr * MinorThresholdAtr, floor),
                MinBarsBetweenPivots);

            bool isLong = ResolveDirection();
            double entry = Close[0];

            double anchor;
            double stop;

            if (TrySelectAnchor(isLong, entry, atr, out anchor))
            {
                stop = isLong
                    ? anchor - BufferTicks * TickSize
                    : anchor + BufferTicks * TickSize;
                stop = Instrument.MasterInstrument.RoundToTickSize(stop);

                Values[0][0] = stop;
                Values[2][0] = anchor;
                _structuralCount++;
            }
            else
            {
                // Repli. A structural level sitting inside the noise band is not a stop, it is a
                // donation — so when nothing clears the floor the volatility stop is the safer
                // answer, even though it is anchored to nothing.
                stop = isLong
                    ? entry - FallbackAtrMultiple * atr
                    : entry + FallbackAtrMultiple * atr;
                stop = Instrument.MasterInstrument.RoundToTickSize(stop);

                Values[1][0] = stop;
                _fallbackCount++;
            }

            double distance = Math.Abs(entry - stop);
            _distanceTicks.Add(distance / TickSize);
            _distanceAtr.Add(distance / atr);

            var pending = new PendingEntry();
            pending.Bar = CurrentBar;
            pending.Stop = stop;
            pending.IsLong = isLong;
            _pending.Add(pending);

            ResolvePending();
        }

        private bool ResolveDirection()
        {
            if (PreviewDirection == TdStopPreviewDirection.Achat) return true;
            if (PreviewDirection == TdStopPreviewDirection.Vente) return false;
            return Close[0] >= _ema[0];
        }

        /// <summary>
        /// Builds the candidate levels and applies the corridor. Returns false when nothing valid
        /// was found, which is the caller's cue to fall back.
        /// </summary>
        private bool TrySelectAnchor(bool isLong, double entry, double atr, out double anchor)
        {
            anchor = 0.0;

            double min = MinDistanceAtr * atr;
            double max = MaxDistanceAtr * atr;

            _candidates.Clear();

            var pivots = _engine.Pivots;
            int inspected = 0;

            // Backwards, so index 0 of _candidates is the most recent qualifying level.
            for (int i = pivots.Count - 1; i >= 0 && inspected < LookbackPivots; i--)
            {
                var pivot = pivots[i];

                // A long is protected by a swing LOW, a short by a swing HIGH.
                if (pivot.IsHigh == isLong)
                    continue;

                inspected++;

                // Only levels already knowable at this bar. The engine appends on confirmation, so
                // this holds by construction — the test guards against a future change of order.
                if (pivot.ConfirmationBar > CurrentBar)
                    continue;

                double distance = isLong ? entry - pivot.Price : pivot.Price - entry;
                if (distance <= 0.0)
                    continue;
                if (distance < min || distance > max)
                    continue;

                _candidates.Add(pivot);
            }

            if (_candidates.Count == 0)
                return false;

            // Step past the obvious level only when there is somewhere else to go.
            int start = SkipMostRecentPivot && _candidates.Count >= 2 ? 1 : 0;

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

        /// <summary>
        /// Scores past previews once enough bars have elapsed. Resolution is always backward-looking:
        /// a pending entry is only ever tested against bars that have already printed.
        /// </summary>
        private void ResolvePending()
        {
            for (int i = _pending.Count - 1; i >= 0; i--)
            {
                var pending = _pending[i];

                // The entry is taken at this bar's close, so this bar cannot stop it out.
                if (pending.Bar == CurrentBar)
                    continue;

                if (!pending.Hit)
                {
                    bool breached = pending.IsLong ? Low[0] <= pending.Stop : High[0] >= pending.Stop;
                    if (breached)
                        pending.Hit = true;
                }

                if (CurrentBar - pending.Bar < SweepHorizonBars)
                    continue;

                _resolved++;
                if (pending.Hit)
                    _swept++;
                _pending.RemoveAt(i);
            }
        }

        private void PrintStatistics()
        {
            int evaluated = _structuralCount + _fallbackCount;
            if (evaluated == 0)
                return;

            Print(string.Format(
                "TdStopPlacement — {0} barres évaluées : {1:F1} % niveau structurel, {2:F1} % repli ATR",
                evaluated,
                100.0 * _structuralCount / evaluated,
                100.0 * _fallbackCount / evaluated));

            if (_distanceTicks.Count > 0)
            {
                Print(string.Format(
                    "  largeur du stop : moyenne {0:F1} ticks ({1:F2} ATR), médiane {2:F1} ticks",
                    Mean(_distanceTicks), Mean(_distanceAtr), MedianOf(_distanceTicks)));
            }

            if (_resolved > 0)
            {
                Print(string.Format(
                    "  taux de balayage à {0} barres : {1:F1} % ({2} touchés sur {3} résolus)",
                    SweepHorizonBars, 100.0 * _swept / _resolved, _swept, _resolved));
            }
        }

        private static double Mean(List<double> values)
        {
            double sum = 0.0;
            for (int i = 0; i < values.Count; i++)
                sum += values[i];
            return sum / values.Count;
        }

        /// <summary>Named MedianOf, not Median: NinjaScriptBase already exposes a MEDIAN accessor.</summary>
        private static double MedianOf(List<double> values)
        {
            var copy = new List<double>(values);
            copy.Sort();
            int middle = copy.Count / 2;
            return copy.Count % 2 == 1 ? copy[middle] : (copy[middle - 1] + copy[middle]) / 2.0;
        }

        /// <summary>A preview awaiting its verdict.</summary>
        private class PendingEntry
        {
            public int Bar;
            public double Stop;
            public bool IsLong;
            public bool Hit;
        }

        #region Properties

        [NinjaScriptProperty]
        [Range(2, int.MaxValue)]
        [Display(Name = "Période ATR", Description = "Unité de mesure commune au seuil de détection et au couloir.", GroupName = "Détection", Order = 1)]
        public int AtrPeriod { get; set; }

        [NinjaScriptProperty]
        [Range(0.1, double.MaxValue)]
        [Display(Name = "Seuil structurel (x ATR)", Description = "À garder identique à celui de TdSwingStructure.", GroupName = "Détection", Order = 2)]
        public double MajorThresholdAtr { get; set; }

        [NinjaScriptProperty]
        [Range(0.1, double.MaxValue)]
        [Display(Name = "Seuil intermédiaire (x ATR)", Description = "À garder identique à celui de TdSwingStructure.", GroupName = "Détection", Order = 3)]
        public double MinorThresholdAtr { get; set; }

        [NinjaScriptProperty]
        [Range(1, int.MaxValue)]
        [Display(Name = "Barres minimum entre pivots", GroupName = "Détection", Order = 4)]
        public int MinBarsBetweenPivots { get; set; }

        [NinjaScriptProperty]
        [Range(0.1, double.MaxValue)]
        [Display(Name = "Distance minimale (x ATR)", Description = "Plancher du couloir. En dessous, le stop est dans le bruit d'une barre normale.", GroupName = "Couloir", Order = 1)]
        public double MinDistanceAtr { get; set; }

        [NinjaScriptProperty]
        [Range(0.1, double.MaxValue)]
        [Display(Name = "Distance maximale (x ATR)", Description = "Plafond du couloir. Au-delà, le niveau est structurel mais le risque ne l'est plus.", GroupName = "Couloir", Order = 2)]
        public double MaxDistanceAtr { get; set; }

        [NinjaScriptProperty]
        [Range(0, int.MaxValue)]
        [Display(Name = "Marge sous le niveau (ticks)", Description = "Le stop se pose au-delà du niveau retenu, pas dessus.", GroupName = "Couloir", Order = 3)]
        public int BufferTicks { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Sauter le pivot le plus récent", Description = "Uniquement si un autre candidat valide existe. Le niveau le plus évident est celui que la chasse balaie.", GroupName = "Couloir", Order = 4)]
        public bool SkipMostRecentPivot { get; set; }

        [NinjaScriptProperty]
        [Range(2, int.MaxValue)]
        [Display(Name = "Pivots examinés", Description = "Profondeur de l'historique consulté, par côté.", GroupName = "Couloir", Order = 5)]
        public int LookbackPivots { get; set; }

        [NinjaScriptProperty]
        [Range(0.1, double.MaxValue)]
        [Display(Name = "Repli : distance ATR", Description = "Stop de volatilité utilisé quand aucun niveau ne franchit le plancher.", GroupName = "Couloir", Order = 6)]
        public double FallbackAtrMultiple { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Sens supposé", Description = "SuivreEma : achat au-dessus de l'EMA, vente en dessous.", GroupName = "Aperçu", Order = 1)]
        public TdStopPreviewDirection PreviewDirection { get; set; }

        [NinjaScriptProperty]
        [Range(1, int.MaxValue)]
        [Display(Name = "Période EMA", GroupName = "Aperçu", Order = 2)]
        public int EmaPeriod { get; set; }

        [NinjaScriptProperty]
        [Range(1, int.MaxValue)]
        [Display(Name = "Horizon de balayage (barres)", Description = "Fenêtre sur laquelle on mesure si le stop aurait été touché.", GroupName = "Aperçu", Order = 3)]
        public int SweepHorizonBars { get; set; }

        [Browsable(false)]
        [XmlIgnore]
        public Series<double> StopStructurel { get { return Values[0]; } }

        [Browsable(false)]
        [XmlIgnore]
        public Series<double> StopRepliAtr { get { return Values[1]; } }

        [Browsable(false)]
        [XmlIgnore]
        public Series<double> NiveauRetenu { get { return Values[2]; } }

        #endregion
    }
}
