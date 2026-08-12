#region Using declarations
using System.ComponentModel.DataAnnotations;
using NinjaTrader.NinjaScript.Indicators;
#endregion

// This namespace holds Strategies in this folder and is required. Do not change it.
namespace NinjaTrader.NinjaScript.Strategies
{
    /// <summary>
    /// Displays TdSwingStructure and the EMA on the chart. Step one of the TradeDeck strategy work:
    /// detection only, so the quality of the pivots can be judged by eye before anything is built
    /// on top of them.
    ///
    /// IT PLACES NO ORDERS, ON PURPOSE. No entry, no stop, no target, no position management. A
    /// backtest of this strategy will therefore report zero trades — that is the expected result.
    /// What is being tested here is whether the dots land on the real wave reversals across a
    /// strong trend, a weak trend and a range.
    ///
    /// WHY THE LOGIC IS NOT IN HERE. The detector is an indicator so it can be dropped on a chart
    /// and retuned live without relaunching a backtest, and so the strategies that come next can
    /// reuse it instead of copying it. Its parameters are all [NinjaScriptProperty], which is what
    /// will later let the deck discover and render them on its own (see
    /// docs/strategie-structure-marche.md).
    /// </summary>
    public class TdStructureViewer : Strategy
    {
        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name = "TdStructureViewer";
                Description = "Affiche les creux et sommets structurels et l'EMA. Aucune logique de trading.";

                Calculate = Calculate.OnBarClose;
                IsOverlay = true;
                EntriesPerDirection = 1;
                EntryHandling = EntryHandling.AllEntries;
                IsExitOnSessionCloseStrategy = false;
                IsInstantiatedOnEachOptimizationIteration = false;
                BarsRequiredToTrade = 20;

                AtrPeriod = 20;
                MajorThresholdAtr = 2.5;
                MinorThresholdAtr = 1.0;
                ShowMinorPivots = true;
                MinBarsBetweenPivots = 3;
                DotOffsetTicks = 4;
                ShowEma = true;
                EmaPeriod = 40;
            }
            else if (State == State.DataLoaded)
            {
                // Argument order follows the [NinjaScriptProperty] declaration order in the
                // indicator — that is what NinjaScript's generated wrapper expects.
                AddChartIndicator(TdSwingStructure(
                    AtrPeriod,
                    MajorThresholdAtr,
                    MinorThresholdAtr,
                    ShowMinorPivots,
                    MinBarsBetweenPivots,
                    DotOffsetTicks,
                    ShowEma,
                    EmaPeriod));
            }
        }

        protected override void OnBarUpdate()
        {
            // Deliberately empty. Step one is detection; entries come later.
        }

        #region Properties

        [NinjaScriptProperty]
        [Range(2, int.MaxValue)]
        [Display(Name = "Période ATR", GroupName = "Détection", Order = 1)]
        public int AtrPeriod { get; set; }

        [NinjaScriptProperty]
        [Range(0.1, double.MaxValue)]
        [Display(Name = "Seuil structurel (x ATR)", GroupName = "Détection", Order = 2)]
        public double MajorThresholdAtr { get; set; }

        [NinjaScriptProperty]
        [Range(0.1, double.MaxValue)]
        [Display(Name = "Seuil intermédiaire (x ATR)", GroupName = "Détection", Order = 3)]
        public double MinorThresholdAtr { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Afficher les pivots intermédiaires", GroupName = "Détection", Order = 4)]
        public bool ShowMinorPivots { get; set; }

        [NinjaScriptProperty]
        [Range(1, int.MaxValue)]
        [Display(Name = "Barres minimum entre pivots", GroupName = "Détection", Order = 5)]
        public int MinBarsBetweenPivots { get; set; }

        [NinjaScriptProperty]
        [Range(0, int.MaxValue)]
        [Display(Name = "Décalage des points (ticks)", GroupName = "Affichage", Order = 1)]
        public int DotOffsetTicks { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Afficher l'EMA", GroupName = "Affichage", Order = 2)]
        public bool ShowEma { get; set; }

        [NinjaScriptProperty]
        [Range(1, int.MaxValue)]
        [Display(Name = "Période EMA", GroupName = "Affichage", Order = 3)]
        public int EmaPeriod { get; set; }

        #endregion
    }
}
