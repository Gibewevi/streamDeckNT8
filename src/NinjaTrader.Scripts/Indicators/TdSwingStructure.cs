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
    /// <summary>
    /// Plots structural swing highs and lows as dots. Detection itself lives in TdSwingEngine.
    ///
    /// WHY NOT FRACTALS. The obvious detector — "a high greater than the n bars either side",
    /// which is what Williams fractals and NinjaTrader's own Swing(n) do — is a purely POSITIONAL
    /// test. It carries no notion of how far price actually travelled, so in a range it validates
    /// every micro-oscillation, and in a strong trend it misses the short pullbacks because n is
    /// fixed. Market structure is not a question of bar count, it is a question of DISTANCE.
    ///
    /// WHY ATR AND NOT TICKS OR PERCENT. A threshold in ticks has to be recalibrated for every
    /// instrument and again whenever volatility changes; a percentage is meaningless on futures.
    /// Expressed as a multiple of ATR, the same setting holds in a trend and in a range, at the
    /// open and at midday, on MNQ and elsewhere — the ATR absorbs the regime change itself.
    ///
    /// TWO TIERS, ON PURPOSE. This is the one real conflict in the algorithm: in a range the waves
    /// are wide and a high k gives clean structure, but in a strong trend the pullbacks are SHORT
    /// and those shallow lows are precisely the significant ones. A single threshold cannot serve
    /// both, so both are plotted at once — large dots for structure, small dots for trend
    /// pullbacks — and the eye decides.
    ///
    /// PIVOTS ARE CONFIRMED LATE, BY DEFINITION. A high is only known to have been a high once
    /// price has come back down from it. On a chart this is invisible and looks perfect; live, the
    /// dot appears some bars after the extreme it marks. That is not a defect to be fixed, but
    /// nothing built on top of these pivots may ever assume one was known at the time it formed —
    /// which is why TdSwingPivot carries a ConfirmationBar.
    /// </summary>
    public class TdSwingStructure : Indicator
    {
        /// <summary>
        /// Floor applied to the threshold, in ticks. On a dead series the ATR collapses and every
        /// bar would confirm a pivot; two ticks is the smallest move that can mean anything.
        /// </summary>
        private const double MinimumThresholdTicks = 2.0;

        private ATR _atr;
        private EMA _ema;
        private TdSwingEngine _engine;

        // Session statistics, printed on Terminated. Calibrating k by eye alone is slow; the
        // numbers make it a decision instead of an impression.
        private readonly List<double> _legTicks = new List<double>();
        private readonly List<double> _legBars = new List<double>();
        private int _majorHighs;
        private int _majorLows;
        private int _minorPivots;

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name = "TdSwingStructure";
                Description = "Creux et sommets structurels, seuil normalisé par l'ATR. Détection seule, aucun ordre.";

                Calculate = Calculate.OnBarClose;
                IsOverlay = true;
                DisplayInDataBox = false;
                DrawOnPricePanel = true;
                PaintPriceMarkers = false;
                IsSuspendedWhileInactive = true;

                // REQUIRED. A pivot is written back into the bar where the extreme occurred, which
                // may be far behind the confirmation bar during a long trend leg. With the default
                // 256-bar window those writes land outside the series and are silently lost.
                MaximumBarsLookBack = MaximumBarsLookBack.Infinite;

                AtrPeriod = 20;
                MajorThresholdAtr = 2.5;
                MinorThresholdAtr = 1.0;
                ShowMinorPivots = true;
                MinBarsBetweenPivots = 3;
                DotOffsetTicks = 4;
                ShowEma = true;
                EmaPeriod = 40;

                // Plot order fixes the Values[] indices used throughout OnBarUpdate.
                AddPlot(new Stroke(Brushes.Red, 4), PlotStyle.Dot, "SwingHigh");
                AddPlot(new Stroke(Brushes.Lime, 4), PlotStyle.Dot, "SwingLow");
                AddPlot(new Stroke(Brushes.IndianRed, 2), PlotStyle.Dot, "MinorHigh");
                AddPlot(new Stroke(Brushes.MediumSeaGreen, 2), PlotStyle.Dot, "MinorLow");
                AddPlot(new Stroke(Brushes.DodgerBlue, 2), PlotStyle.Line, "EMA");
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
            // Every plot starts the bar empty. A Series<double> initialises to 0, and a dot plotted
            // at 0 would drag the chart scale down to the axis on every bar without a pivot.
            Values[0][0] = double.NaN;
            Values[1][0] = double.NaN;
            Values[2][0] = double.NaN;
            Values[3][0] = double.NaN;
            Values[4][0] = ShowEma ? _ema[0] : double.NaN;

            // The ATR needs its window before the threshold means anything. Seeding the engine
            // earlier would anchor it on a value computed from almost no data.
            if (CurrentBar < AtrPeriod + 1)
                return;

            double unit = Math.Max(_atr[0], TickSize);
            double floor = MinimumThresholdTicks * TickSize;

            // Both tiers are ALWAYS tracked. ShowMinorPivots governs the drawing only — the
            // intermediate pivots are what TdStopPlacement leans on in a trend, and a display
            // toggle silently emptying the history would be a trap.
            _engine.Update(
                CurrentBar,
                High[0],
                Low[0],
                Math.Max(unit * MajorThresholdAtr, floor),
                Math.Max(unit * MinorThresholdAtr, floor),
                MinBarsBetweenPivots);

            var confirmed = _engine.JustConfirmed;
            for (int i = 0; i < confirmed.Count; i++)
                PlotPivot(confirmed[i]);
        }

        private void PlotPivot(TdSwingPivot pivot)
        {
            if (!pivot.IsMajor && !ShowMinorPivots)
                return;

            int barsAgo = CurrentBar - pivot.ExtremeBar;
            if (barsAgo < 0)
                return;

            double offset = DotOffsetTicks * TickSize;
            int plot = pivot.IsMajor
                ? (pivot.IsHigh ? 0 : 1)
                : (pivot.IsHigh ? 2 : 3);

            Values[plot][barsAgo] = pivot.IsHigh ? pivot.Price + offset : pivot.Price - offset;

            if (!pivot.IsMajor)
            {
                _minorPivots++;
                return;
            }

            if (pivot.IsHigh) _majorHighs++; else _majorLows++;

            if (pivot.HasPrevious)
            {
                _legTicks.Add(Math.Abs(pivot.Price - pivot.PreviousPrice) / TickSize);
                _legBars.Add(pivot.ExtremeBar - pivot.PreviousExtremeBar);
            }
        }

        private void PrintStatistics()
        {
            // Terminated also runs when a chart is simply closed; nothing to say if nothing ran.
            if (_majorHighs + _majorLows == 0)
                return;

            Print(string.Format(
                "TdSwingStructure — pivots majeurs : {0} sommets / {1} creux, mineurs : {2}",
                _majorHighs, _majorLows, _minorPivots));

            if (_legTicks.Count > 0)
            {
                Print(string.Format(
                    "  vagues majeures : {0} — amplitude moyenne {1:F1} ticks, médiane {2:F1} ticks",
                    _legTicks.Count, Mean(_legTicks), MedianOf(_legTicks)));
            }

            if (_legBars.Count > 0)
            {
                Print(string.Format(
                    "  durée : moyenne {0:F1} barres, médiane {1:F1} barres",
                    Mean(_legBars), MedianOf(_legBars)));
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

        #region Properties

        [NinjaScriptProperty]
        [Range(2, int.MaxValue)]
        [Display(Name = "Période ATR", Description = "Fenêtre de l'ATR qui sert d'unité au seuil. Longue = seuil stable.", GroupName = "Détection", Order = 1)]
        public int AtrPeriod { get; set; }

        [NinjaScriptProperty]
        [Range(0.1, double.MaxValue)]
        [Display(Name = "Seuil structurel (x ATR)", Description = "Retracement minimal pour valider un pivot majeur.", GroupName = "Détection", Order = 2)]
        public double MajorThresholdAtr { get; set; }

        [NinjaScriptProperty]
        [Range(0.1, double.MaxValue)]
        [Display(Name = "Seuil intermédiaire (x ATR)", Description = "Seuil des pivots mineurs : les pullbacks courts de tendance.", GroupName = "Détection", Order = 3)]
        public double MinorThresholdAtr { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Afficher les pivots intermédiaires", Description = "Affichage seul : les pivots mineurs restent détectés dans tous les cas.", GroupName = "Détection", Order = 4)]
        public bool ShowMinorPivots { get; set; }

        [NinjaScriptProperty]
        [Range(1, int.MaxValue)]
        [Display(Name = "Barres minimum entre pivots", Description = "Anti-grappe : empêche deux pivots collés sur une secousse.", GroupName = "Détection", Order = 5)]
        public int MinBarsBetweenPivots { get; set; }

        [NinjaScriptProperty]
        [Range(0, int.MaxValue)]
        [Display(Name = "Décalage des points (ticks)", Description = "Écart entre le point et la mèche, pour la lisibilité.", GroupName = "Affichage", Order = 1)]
        public int DotOffsetTicks { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Afficher l'EMA", Description = "Référence visuelle de tendance.", GroupName = "Affichage", Order = 2)]
        public bool ShowEma { get; set; }

        [NinjaScriptProperty]
        [Range(1, int.MaxValue)]
        [Display(Name = "Période EMA", Description = "40 par défaut.", GroupName = "Affichage", Order = 3)]
        public int EmaPeriod { get; set; }

        [Browsable(false)]
        [XmlIgnore]
        public Series<double> SwingHigh { get { return Values[0]; } }

        [Browsable(false)]
        [XmlIgnore]
        public Series<double> SwingLow { get { return Values[1]; } }

        [Browsable(false)]
        [XmlIgnore]
        public Series<double> MinorHigh { get { return Values[2]; } }

        [Browsable(false)]
        [XmlIgnore]
        public Series<double> MinorLow { get { return Values[3]; } }

        [Browsable(false)]
        [XmlIgnore]
        public Series<double> EmaPlot { get { return Values[4]; } }

        /// <summary>Confirmed pivots, in confirmation order. Empty before DataLoaded.</summary>
        [Browsable(false)]
        [XmlIgnore]
        public IList<TdSwingPivot> ConfirmedPivots
        {
            get { return _engine != null ? _engine.Pivots : new List<TdSwingPivot>(); }
        }

        #endregion
    }
}
