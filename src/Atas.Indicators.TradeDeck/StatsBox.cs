using System.Collections.Generic;
using System.Drawing;

using ATAS.Indicators;

using OFT.Rendering.Context;
using OFT.Rendering.Tools;

namespace TradeDeck.Atas.Indicators;

/// <summary>
/// The calibration figures, drawn in a corner of the chart.
///
/// NinjaTrader printed these to the output window on Terminated. ATAS has no output window at all,
/// and dropping the numbers would gut the indicator's actual purpose — they are what turns setting
/// the ATR multiple from an impression into a decision. Drawing them is also strictly better than
/// the original: they were only readable once the chart was CLOSED, and they are now live.
/// </summary>
internal static class StatsBox
{
    private const int Padding = 8;
    private const int Margin = 12;

    /// <summary>
    /// Vertical offset for a box that must stack below another one sharing the same corner.
    /// Sized to clear a four-line box at this font: pairing TdSwingStructure and TdStopPlacement
    /// on the same panel is the documented workflow (their threshold settings are meant to match),
    /// so without this the second box paints straight over the first.
    /// </summary>
    public const int StackedTopOffset = 100;

    private static readonly RenderFont Font = new("Arial", 11);
    private static readonly Color Background = Color.FromArgb(200, 20, 25, 35);
    private static readonly Color Text = Color.FromArgb(220, 220, 220);
    private static readonly RenderPen Border = new(Color.Gray, 1);

    public static void Draw(RenderContext context, IIndicatorContainer container, IReadOnlyList<string> lines, int topOffset)
    {
        if (context == null || container == null || lines == null || lines.Count == 0)
            return;

        var lineHeight = context.MeasureString("A", Font).Height;

        var width = 0;
        foreach (var line in lines)
        {
            var w = (int)context.MeasureString(line, Font).Width;
            if (w > width)
                width = w;
        }

        var boxWidth = width + Padding * 2;
        var boxHeight = lineHeight * lines.Count + Padding * 2;

        var x = container.Region.Width - boxWidth - Margin;
        var y = Margin + topOffset;
        if (x < 0)
            x = 0;

        var rectangle = new Rectangle(x, y, boxWidth, boxHeight);
        context.FillRectangle(Background, rectangle);
        context.DrawRectangle(Border, rectangle);

        var currentY = y + Padding;
        foreach (var line in lines)
        {
            context.DrawString(line, Font, Text, x + Padding, currentY);
            currentY += lineHeight;
        }
    }

    public static double Mean(List<double> values)
    {
        if (values.Count == 0)
            return 0.0;

        var sum = 0.0;
        for (var i = 0; i < values.Count; i++)
            sum += values[i];
        return sum / values.Count;
    }

    /// <summary>Named MedianOf, not Median, to mirror the NinjaTrader side where the base class
    /// already exposes a MEDIAN accessor.</summary>
    public static double MedianOf(List<double> values)
    {
        if (values.Count == 0)
            return 0.0;

        var copy = new List<double>(values);
        copy.Sort();
        var middle = copy.Count / 2;
        return copy.Count % 2 == 1 ? copy[middle] : (copy[middle - 1] + copy[middle]) / 2.0;
    }
}
