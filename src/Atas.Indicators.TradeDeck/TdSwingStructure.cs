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

/// <summary>
/// Plots structural swing highs and lows as dots. Detection itself lives in TdSwingEngine, which is
/// the SAME FILE the NinjaTrader build compiles — see the csproj.
///
/// WHY NOT FRACTALS. The obvious detector — "a high greater than the n bars either side" — is a
/// purely POSITIONAL test. It carries no notion of how far price actually travelled, so in a range
/// it validates every micro-oscillation, and in a strong trend it misses the short pullbacks
/// because n is fixed. Market structure is a question of DISTANCE, not of bar count.
///
/// WHY ATR AND NOT TICKS OR PERCENT. A threshold in ticks has to be recalibrated for every
/// instrument and again whenever volatility changes; a percentage is meaningless on futures.
///
/// PIVOTS ARE CONFIRMED LATE, BY DEFINITION. A high is only known to have been a high once price
/// has come back down from it. Live, the dot appears some bars after the extreme it marks. Nothing
/// built on these pivots may assume one was known at the time it formed.
/// </summary>
[DisplayName("TD Swing Structure")]
[Display(Name = "TD Swing Structure",
    Description = "Creux et sommets structurels, seuil normalise par l'ATR. Detection seule, aucun ordre.")]
public class TdSwingStructure : Indicator
{
    /// <summary>
    /// Floor applied to the threshold, in ticks. On a dead series the ATR collapses and every bar
    /// would confirm a pivot; two ticks is the smallest move that can mean anything.
    /// </summary>
    private const decimal MinimumThresholdTicks = 2m;

    private const int MaxStoredPivots = 512;

    private readonly ATR _atr = new() { Period = 20 };
    private readonly EMA _ema = new() { Period = 40 };

    private readonly ValueDataSeries _swingHigh = new("SwingHigh", "Sommet structurel")
    {
        Color = DefaultColors.Red.Convert(), VisualType = VisualMode.Dots, Width = 4, ShowZeroValue = false
    };
    private readonly ValueDataSeries _swingLow = new("SwingLow", "Creux structurel")
    {
        Color = DefaultColors.Green.Convert(), VisualType = VisualMode.Dots, Width = 4, ShowZeroValue = false
    };
    private readonly ValueDataSeries _minorHigh = new("MinorHigh", "Sommet intermediaire")
    {
        Color = System.Drawing.Color.IndianRed.Convert(), VisualType = VisualMode.Dots, Width = 2, ShowZeroValue = false
    };
    private readonly ValueDataSeries _minorLow = new("MinorLow", "Creux intermediaire")
    {
        Color = System.Drawing.Color.MediumSeaGreen.Convert(), VisualType = VisualMode.Dots, Width = 2, ShowZeroValue = false
    };
    private readonly ValueDataSeries _emaSeries = new("Ema", "EMA")
    {
        Color = DefaultColors.Blue.Convert(), VisualType = VisualMode.Line, Width = 2, ShowZeroValue = false
    };

    private TdSwingEngine _engine = new(MaxStoredPivots);

    /// <summary>Last bar index seen by OnCalculate. -1 means "nothing processed yet".</summary>
    private int _lastBar = -1;

    // Session statistics. Rendered by the box in TdSwingStructure.OnRender (task 4).
    private readonly List<double> _legTicks = new();
    private readonly List<double> _legBars = new();
    private int _majorHighs;
    private int _majorLows;
    private int _minorPivots;

    private decimal _majorThresholdAtr = 2.5m;
    private decimal _minorThresholdAtr = 1.0m;
    private bool _showMinorPivots = true;
    private int _minBarsBetweenPivots = 3;
    private int _dotOffsetTicks = 4;
    private bool _showEma = true;

    public TdSwingStructure() : base(true)
    {
        DataSeries[0] = _swingHigh;
        DataSeries.Add(_swingLow);
        DataSeries.Add(_minorHigh);
        DataSeries.Add(_minorLow);
        DataSeries.Add(_emaSeries);

        Add(_atr);
        Add(_ema);
    }

    protected override void OnRecalculate()
    {
        _engine = new TdSwingEngine(MaxStoredPivots);
        _lastBar = -1;
        _legTicks.Clear();
        _legBars.Clear();
        _majorHighs = 0;
        _majorLows = 0;
        _minorPivots = 0;
        DataSeries.ForEach(x => x.Clear());
    }

    protected override void OnCalculate(int bar, decimal value)
    {
        // THE ONE THING THIS PORT COULD GET WRONG.
        //
        // NinjaTrader's Calculate.OnBarClose called OnBarUpdate exactly once, on a bar that had
        // closed. ATAS re-enters OnCalculate on EVERY TICK of the forming bar. TdSwingEngine is a
        // state machine that mutates on every call, so two distinct corruptions follow: feeding the
        // same bar twice breaks the alternation, and feeding a bar that is still forming anchors a
        // running extreme on a partial High that later grows.
        //
        // So: advance only when the index moves, and feed the bar that has just CLOSED. ATAS's own
        // EMA uses `CurrentBar - 1` the same way for its alerts.
        //
        // Side effect, and a welcome one: the historical pass feeds bars 0..CurrentBar-1 and leaves
        // the forming bar alone — exactly NinjaTrader's behaviour, for free.
        if (bar == _lastBar)
            return;
        _lastBar = bar;

        if (bar == 0)
            return;

        var closed = bar - 1;

        // The EMA is drawn from the first bar, before the ATR warm-up gate below — matching the
        // NinjaTrader version, which set the EMA plot before its own early return.
        _emaSeries[closed] = _showEma ? _ema[closed] : 0m;

        // The ATR needs its window before the threshold means anything. Seeding the engine earlier
        // would anchor it on a value computed from almost no data.
        if (closed < _atr.Period + 1)
            return;

        var tick = InstrumentInfo?.TickSize ?? 0m;
        if (tick <= 0m)
            return;

        var unit = _atr[closed];
        if (unit < tick)
            unit = tick;

        var floor = MinimumThresholdTicks * tick;
        var candle = GetCandle(closed);

        // Both tiers are ALWAYS tracked. ShowMinorPivots governs the drawing only — the
        // intermediate pivots are what TdStopPlacement leans on in a trend, and a display toggle
        // silently emptying the history would be a trap.
        _engine.Update(
            closed,
            (double)candle.High,
            (double)candle.Low,
            (double)Math.Max(unit * _majorThresholdAtr, floor),
            (double)Math.Max(unit * _minorThresholdAtr, floor),
            _minBarsBetweenPivots);

        var confirmed = _engine.JustConfirmed;
        for (var i = 0; i < confirmed.Count; i++)
            PlotPivot(confirmed[i], tick);
    }

    private void PlotPivot(TdSwingPivot pivot, decimal tick)
    {
        if (!pivot.IsMajor && !_showMinorPivots)
            return;

        var offset = _dotOffsetTicks * tick;
        var price = (decimal)pivot.Price;

        // Absolute bar indexing: writing a pivot back into the bar that carried the extreme is
        // trivial here. NinjaTrader needed MaximumBarsLookBack.Infinite for the same write, because
        // its indexing is relative and the default 256-bar window swallowed it in silence.
        var target = pivot.IsHigh ? price + offset : price - offset;

        if (!pivot.IsMajor)
        {
            if (pivot.IsHigh)
                _minorHigh[pivot.ExtremeBar] = target;
            else
                _minorLow[pivot.ExtremeBar] = target;

            _minorPivots++;
            return;
        }

        if (pivot.IsHigh)
        {
            _swingHigh[pivot.ExtremeBar] = target;
            _majorHighs++;
        }
        else
        {
            _swingLow[pivot.ExtremeBar] = target;
            _majorLows++;
        }

        if (pivot.HasPrevious && tick > 0m)
        {
            _legTicks.Add(Math.Abs(pivot.Price - pivot.PreviousPrice) / (double)tick);
            _legBars.Add(pivot.ExtremeBar - pivot.PreviousExtremeBar);
        }
    }

    #region Reglages

    [Parameter]
    [Display(Name = "Periode ATR", Description = "Fenetre de l'ATR qui sert d'unite au seuil. Longue = seuil stable.", GroupName = "Detection", Order = 1)]
    [Range(2, 10000)]
    public int AtrPeriod
    {
        get => _atr.Period;
        set { _atr.Period = value; RecalculateValues(); }
    }

    [Parameter]
    [Display(Name = "Seuil structurel (x ATR)", Description = "Retracement minimal pour valider un pivot majeur.", GroupName = "Detection", Order = 2)]
    [Range(0.1, 1000)]
    public decimal MajorThresholdAtr
    {
        get => _majorThresholdAtr;
        set { _majorThresholdAtr = value; RecalculateValues(); }
    }

    [Parameter]
    [Display(Name = "Seuil intermediaire (x ATR)", Description = "Seuil des pivots mineurs : les pullbacks courts de tendance.", GroupName = "Detection", Order = 3)]
    [Range(0.1, 1000)]
    public decimal MinorThresholdAtr
    {
        get => _minorThresholdAtr;
        set { _minorThresholdAtr = value; RecalculateValues(); }
    }

    [Parameter]
    [Display(Name = "Afficher les pivots intermediaires", Description = "Affichage seul : les pivots mineurs restent detectes dans tous les cas.", GroupName = "Detection", Order = 4)]
    public bool ShowMinorPivots
    {
        get => _showMinorPivots;
        set { _showMinorPivots = value; RecalculateValues(); }
    }

    [Parameter]
    [Display(Name = "Barres minimum entre pivots", Description = "Anti-grappe : empeche deux pivots colles sur une secousse.", GroupName = "Detection", Order = 5)]
    [Range(1, 10000)]
    public int MinBarsBetweenPivots
    {
        get => _minBarsBetweenPivots;
        set { _minBarsBetweenPivots = value; RecalculateValues(); }
    }

    [Parameter]
    [Display(Name = "Decalage des points (ticks)", Description = "Ecart entre le point et la meche, pour la lisibilite.", GroupName = "Affichage", Order = 10)]
    [Range(0, 10000)]
    public int DotOffsetTicks
    {
        get => _dotOffsetTicks;
        set { _dotOffsetTicks = value; RecalculateValues(); }
    }

    [Parameter]
    [Display(Name = "Afficher l'EMA", Description = "Reference visuelle de tendance.", GroupName = "Affichage", Order = 20)]
    public bool ShowEma
    {
        get => _showEma;
        set { _showEma = value; RecalculateValues(); }
    }

    [Parameter]
    [Display(Name = "Periode EMA", GroupName = "Affichage", Order = 30)]
    [Range(1, 10000)]
    public int EmaPeriod
    {
        get => _ema.Period;
        set { _ema.Period = value; RecalculateValues(); }
    }

    #endregion
}
