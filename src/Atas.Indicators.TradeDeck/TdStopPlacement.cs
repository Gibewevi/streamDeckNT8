using System;
using System.Collections.Generic;
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

using TradeDeck.Analytics;

namespace TradeDeck.Atas.Indicators;

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
/// before a single order exists. The selection itself lives in TdStopSelector — the same file the
/// NinjaTrader build compiles.
///
/// IT PLACES NO ORDERS. This is a measuring instrument: a stop is computed for a hypothetical entry
/// at the close of every bar, plotted, and scored.
/// </summary>
[DisplayName("TD Stop Placement")]
[Display(Name = "TD Stop Placement",
    Description = "Choisit un stop structurel par couloir de distance, avec repli ATR. Apercu et mesure, aucun ordre.")]
public class TdStopPlacement : Indicator
{
    private const decimal MinimumThresholdTicks = 2m;
    private const int MaxStoredPivots = 512;

    private readonly ATR _atr = new() { Period = 20 };
    private readonly EMA _ema = new() { Period = 40 };

    private readonly ValueDataSeries _structural = new("StopStructurel", "Stop structurel")
    {
        Color = DefaultColors.Orange.Convert(), VisualType = VisualMode.Line, Width = 2, ShowZeroValue = false
    };
    private readonly ValueDataSeries _fallback = new("StopRepliAtr", "Stop de repli ATR")
    {
        Color = System.Drawing.Color.DimGray.Convert(), VisualType = VisualMode.Line, Width = 2, ShowZeroValue = false
    };
    private readonly ValueDataSeries _anchor = new("NiveauRetenu", "Niveau retenu")
    {
        Color = System.Drawing.Color.Gold.Convert(), VisualType = VisualMode.Dots, Width = 3, ShowZeroValue = false
    };

    private TdSwingEngine _engine = new(MaxStoredPivots);
    private readonly TdStopSelector _selector = new();
    private int _lastBar = -1;

    private readonly List<double> _distanceTicks = new();
    private readonly List<double> _distanceAtr = new();
    private int _structuralCount;
    private int _fallbackCount;

    private decimal _majorThresholdAtr = 2.5m;
    private decimal _minorThresholdAtr = 1.0m;
    private int _minBarsBetweenPivots = 3;
    private decimal _minDistanceAtr = 1.0m;
    private decimal _maxDistanceAtr = 3.0m;
    private int _bufferTicks = 4;
    private bool _skipMostRecentPivot = true;
    private int _lookbackPivots = 12;
    private decimal _fallbackAtrMultiple = 2.0m;
    private TdStopPreviewDirection _previewDirection = TdStopPreviewDirection.SuivreEma;

    public TdStopPlacement() : base(true)
    {
        DataSeries[0] = _structural;
        DataSeries.Add(_fallback);
        DataSeries.Add(_anchor);

        Add(_atr);
        Add(_ema);
    }

    protected override void OnRecalculate()
    {
        _engine = new TdSwingEngine(MaxStoredPivots);
        _lastBar = -1;
        _distanceTicks.Clear();
        _distanceAtr.Clear();
        _structuralCount = 0;
        _fallbackCount = 0;
        DataSeries.ForEach(x => x.Clear());
    }

    protected override void OnCalculate(int bar, decimal value)
    {
        // Same bar-close discipline as TdSwingStructure, and for the same reason: the engine is a
        // state machine, and ATAS re-enters OnCalculate on every tick of the forming bar.
        if (bar == _lastBar)
            return;
        _lastBar = bar;

        if (bar == 0)
            return;

        var closed = bar - 1;

        if (closed < _atr.Period + 1)
            return;

        var tick = InstrumentInfo?.TickSize ?? 0m;
        if (tick <= 0m)
            return;

        var atr = _atr[closed];
        if (atr < tick)
            atr = tick;

        var floor = MinimumThresholdTicks * tick;
        var candle = GetCandle(closed);

        _engine.Update(
            closed,
            (double)candle.High,
            (double)candle.Low,
            (double)Math.Max(atr * _majorThresholdAtr, floor),
            (double)Math.Max(atr * _minorThresholdAtr, floor),
            _minBarsBetweenPivots);

        var isLong = ResolveDirection(closed);
        var entry = candle.Close;

        decimal stop;

        if (_selector.TrySelectAnchor(
                _engine.Pivots, closed, isLong, (double)entry,
                (double)(_minDistanceAtr * atr), (double)(_maxDistanceAtr * atr),
                _lookbackPivots, _skipMostRecentPivot, out var anchor))
        {
            var level = (decimal)anchor;
            stop = isLong ? level - _bufferTicks * tick : level + _bufferTicks * tick;
            stop = RoundToTick(stop, tick);

            _structural[closed] = stop;
            _anchor[closed] = level;
            _structuralCount++;
        }
        else
        {
            // Repli. A structural level sitting inside the noise band is not a stop, it is a
            // donation — so when nothing clears the floor the volatility stop is the safer answer,
            // even though it is anchored to nothing.
            stop = isLong ? entry - _fallbackAtrMultiple * atr : entry + _fallbackAtrMultiple * atr;
            stop = RoundToTick(stop, tick);

            _fallback[closed] = stop;
            _fallbackCount++;
        }

        var distance = Math.Abs(entry - stop);
        _distanceTicks.Add((double)(distance / tick));
        _distanceAtr.Add((double)(distance / atr));
    }

    private bool ResolveDirection(int closed)
    {
        if (_previewDirection == TdStopPreviewDirection.Achat) return true;
        if (_previewDirection == TdStopPreviewDirection.Vente) return false;
        return GetCandle(closed).Close >= _ema[closed];
    }

    /// <summary>
    /// NinjaTrader had Instrument.MasterInstrument.RoundToTickSize. ATAS has no equivalent, but it
    /// does not need one: rounding in decimal is exact, where the NinjaTrader version worked in
    /// double and could land a hair off a tick boundary.
    /// </summary>
    private static decimal RoundToTick(decimal price, decimal tick)
    {
        return tick <= 0m ? price : Math.Round(price / tick) * tick;
    }

    #region Reglages

    [Parameter]
    [Display(Name = "Periode ATR", Description = "Unite de mesure commune au seuil de detection et au couloir.", GroupName = "Detection", Order = 1)]
    [Range(2, 10000)]
    public int AtrPeriod
    {
        get => _atr.Period;
        set { _atr.Period = value; RecalculateValues(); }
    }

    [Parameter]
    [Display(Name = "Seuil structurel (x ATR)", Description = "A garder identique a celui de TD Swing Structure.", GroupName = "Detection", Order = 2)]
    [Range(0.1, 1000)]
    public decimal MajorThresholdAtr
    {
        get => _majorThresholdAtr;
        set { _majorThresholdAtr = value; RecalculateValues(); }
    }

    [Parameter]
    [Display(Name = "Seuil intermediaire (x ATR)", Description = "A garder identique a celui de TD Swing Structure.", GroupName = "Detection", Order = 3)]
    [Range(0.1, 1000)]
    public decimal MinorThresholdAtr
    {
        get => _minorThresholdAtr;
        set { _minorThresholdAtr = value; RecalculateValues(); }
    }

    [Parameter]
    [Display(Name = "Barres minimum entre pivots", GroupName = "Detection", Order = 4)]
    [Range(1, 10000)]
    public int MinBarsBetweenPivots
    {
        get => _minBarsBetweenPivots;
        set { _minBarsBetweenPivots = value; RecalculateValues(); }
    }

    [Parameter]
    [Display(Name = "Distance minimale (x ATR)", Description = "Plancher du couloir. En dessous, le stop est dans le bruit d'une barre normale.", GroupName = "Couloir", Order = 10)]
    [Range(0.1, 1000)]
    public decimal MinDistanceAtr
    {
        get => _minDistanceAtr;
        set { _minDistanceAtr = value; RecalculateValues(); }
    }

    [Parameter]
    [Display(Name = "Distance maximale (x ATR)", Description = "Plafond du couloir. Au-dela, le niveau est structurel mais le risque ne l'est plus.", GroupName = "Couloir", Order = 11)]
    [Range(0.1, 1000)]
    public decimal MaxDistanceAtr
    {
        get => _maxDistanceAtr;
        set { _maxDistanceAtr = value; RecalculateValues(); }
    }

    [Parameter]
    [Display(Name = "Marge sous le niveau (ticks)", Description = "Le stop se pose au-dela du niveau retenu, pas dessus.", GroupName = "Couloir", Order = 12)]
    [Range(0, 10000)]
    public int BufferTicks
    {
        get => _bufferTicks;
        set { _bufferTicks = value; RecalculateValues(); }
    }

    [Parameter]
    [Display(Name = "Sauter le pivot le plus recent", Description = "Uniquement si un autre candidat valide existe. Le niveau le plus evident est celui que la chasse balaie.", GroupName = "Couloir", Order = 13)]
    public bool SkipMostRecentPivot
    {
        get => _skipMostRecentPivot;
        set { _skipMostRecentPivot = value; RecalculateValues(); }
    }

    [Parameter]
    [Display(Name = "Pivots examines", Description = "Profondeur de l'historique consulte, par cote.", GroupName = "Couloir", Order = 14)]
    [Range(2, 10000)]
    public int LookbackPivots
    {
        get => _lookbackPivots;
        set { _lookbackPivots = value; RecalculateValues(); }
    }

    [Parameter]
    [Display(Name = "Repli : distance ATR", Description = "Stop de volatilite utilise quand aucun niveau ne franchit le plancher.", GroupName = "Couloir", Order = 15)]
    [Range(0.1, 1000)]
    public decimal FallbackAtrMultiple
    {
        get => _fallbackAtrMultiple;
        set { _fallbackAtrMultiple = value; RecalculateValues(); }
    }

    [Parameter]
    [Display(Name = "Sens suppose", Description = "SuivreEma : achat au-dessus de l'EMA, vente en dessous.", GroupName = "Apercu", Order = 20)]
    public TdStopPreviewDirection PreviewDirection
    {
        get => _previewDirection;
        set { _previewDirection = value; RecalculateValues(); }
    }

    [Parameter]
    [Display(Name = "Periode EMA", GroupName = "Apercu", Order = 21)]
    [Range(1, 10000)]
    public int EmaPeriod
    {
        get => _ema.Period;
        set { _ema.Period = value; RecalculateValues(); }
    }

    #endregion
}
