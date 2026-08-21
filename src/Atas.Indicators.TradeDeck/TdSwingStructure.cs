using System.ComponentModel;
using System.ComponentModel.DataAnnotations;

using ATAS.Indicators;
using ATAS.Indicators.Drawing;
using ATAS.Indicators.Technical;

using OFT.Attributes;

// DEVIATION (tache 2) : ATAS.Indicators et OFT.Attributes definissent chacun un ParameterAttribute
// (le premier derive du second) ; les importer tous les deux rend [Parameter] ambigu (CS0104).
// ATAS.Indicators.ParameterAttribute est en plus marque [Obsolete("Use OFT.Attributes.ParameterAttribute
// instead.")] dans l'assembly reelle. L'alias pointe donc vers la version recommandee, non obsolete.
using Parameter = OFT.Attributes.ParameterAttribute;

namespace TradeDeck.Atas.Indicators;

[DisplayName("TD Swing Structure")]
[Display(Name = "TD Swing Structure",
    Description = "Creux et sommets structurels, seuil normalise par l'ATR. Detection seule, aucun ordre.")]
public class TdSwingStructure : Indicator
{
    private readonly EMA _ema = new() { Period = 40 };

    // ShowZeroValue = false remplace le double.NaN de NinjaTrader : decimal n'a pas de NaN, et une
    // serie decimal s'initialise a 0. Sans cette propriete, chaque barre sans valeur tracerait un
    // point a 0 et ecraserait l'echelle du graphique sur l'axe.
    private readonly ValueDataSeries _emaSeries = new("Ema", "EMA")
    {
        Color = DefaultColors.Blue.Convert(),
        VisualType = VisualMode.Line,
        Width = 2,
        ShowZeroValue = false
    };

    // Pas de `Panel = ...` : la valeur par defaut est le panneau des bougies, c'est-a-dire
    // l'equivalent de IsOverlay = true chez NinjaTrader.
    public TdSwingStructure() : base(true)
    {
        DataSeries[0] = _emaSeries;
        Add(_ema);
    }

    [Parameter]
    [Display(Name = "Periode EMA", GroupName = "Affichage", Order = 30)]
    [Range(1, 10000)]
    public int EmaPeriod
    {
        get => _ema.Period;
        // RecalculateValues() est OBLIGATOIRE : sans lui, changer le reglage ne relance aucun
        // calcul et l'indicateur ment silencieusement.
        set { _ema.Period = value; RecalculateValues(); }
    }

    protected override void OnCalculate(int bar, decimal value)
    {
        _emaSeries[bar] = _ema[bar];
    }
}
