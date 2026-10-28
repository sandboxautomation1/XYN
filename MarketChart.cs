using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace GoldDesk.Desktop;

public sealed class MarketChart : FrameworkElement
{
    public IReadOnlyList<MarketCandle> Candles { get; set; } = [];
    public decimal? CurrentPrice { get; set; }
    public decimal? BuyAbove { get; set; }
    public decimal? SellBelow { get; set; }

    private readonly Typeface axisTypeface = new("Segoe UI");
    private static readonly Brush GridBrush = new SolidColorBrush(Color.FromRgb(74, 68, 55));
    private static readonly Brush AxisBrush = new SolidColorBrush(Color.FromRgb(185, 176, 156));
    private static readonly Brush BullBrush = new SolidColorBrush(Color.FromRgb(115, 212, 154));
    private static readonly Brush BearBrush = new SolidColorBrush(Color.FromRgb(241, 124, 101));
    private static readonly Brush PriceBrush = new SolidColorBrush(Color.FromRgb(228, 185, 79));

    static MarketChart()
    {
        GridBrush.Freeze(); AxisBrush.Freeze(); BullBrush.Freeze(); BearBrush.Freeze(); PriceBrush.Freeze();
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(25, 24, 21)), null, new Rect(RenderSize));

        var candles = Candles.Where(x => x.High > 0 && x.Low > 0).OrderBy(x => x.Timestamp).ToList();
        if (candles.Count == 0)
        {
            DrawText(dc, "Няма налични OHLC данни за графиката", AxisBrush, 14, new Point(24, 24));
            return;
        }

        const double left = 56, right = 72, top = 18, bottom = 32;
        var plot = new Rect(left, top, Math.Max(10, ActualWidth - left - right), Math.Max(10, ActualHeight - top - bottom));
        var min = candles.Min(x => x.Low);
        var max = candles.Max(x => x.High);
        if (BuyAbove is decimal buy) { min = Math.Min(min, buy); max = Math.Max(max, buy); }
        if (SellBelow is decimal sell) { min = Math.Min(min, sell); max = Math.Max(max, sell); }
        var padding = Math.Max((max - min) * 0.08m, 0.01m);
        min -= padding; max += padding;

        double Y(decimal value) => plot.Bottom - (double)((value - min) / (max - min)) * plot.Height;
        double X(int index) => plot.Left + (index + 0.5) * plot.Width / candles.Count;

        for (var i = 0; i <= 4; i++)
        {
            var y = plot.Top + i * plot.Height / 4;
            dc.DrawLine(new Pen(GridBrush, 1), new Point(plot.Left, y), new Point(plot.Right, y));
            var value = max - (max - min) * i / 4;
            DrawText(dc, value.ToString("0.00", CultureInfo.InvariantCulture), AxisBrush, 11, new Point(6, y - 8));
        }

        var candleWidth = Math.Max(3, Math.Min(24, plot.Width / candles.Count * 0.62));
        for (var i = 0; i < candles.Count; i++)
        {
            var candle = candles[i];
            var x = X(i);
            var up = candle.Close >= candle.Open;
            var brush = up ? BullBrush : BearBrush;
            dc.DrawLine(new Pen(brush, 1), new Point(x, Y(candle.High)), new Point(x, Y(candle.Low)));
            var bodyTop = Math.Min(Y(candle.Open), Y(candle.Close));
            var bodyHeight = Math.Max(1.5, Math.Abs(Y(candle.Open) - Y(candle.Close)));
            dc.DrawRectangle(brush, null, new Rect(x - candleWidth / 2, bodyTop, candleWidth, bodyHeight));
        }

        DrawLevel(dc, plot, Y, CurrentPrice, PriceBrush, "цена");
        DrawLevel(dc, plot, Y, BuyAbove, BullBrush, "BUY");
        DrawLevel(dc, plot, Y, SellBelow, BearBrush, "SELL");

        var labelStep = Math.Max(1, candles.Count / 5);
        for (var i = 0; i < candles.Count; i += labelStep)
            DrawText(dc, candles[i].Timestamp.ToString(candles.Count > 30 ? "dd.MM" : "dd MMM", CultureInfo.InvariantCulture), AxisBrush, 10, new Point(X(i) - 16, plot.Bottom + 8));
    }

    private void DrawLevel(DrawingContext dc, Rect plot, Func<decimal, double> y, decimal? value, Brush brush, string label)
    {
        if (value is not decimal v || v <= 0 || y(v) < plot.Top || y(v) > plot.Bottom) return;
        var line = new Pen(brush, label == "цена" ? 1.5 : 1) { DashStyle = label == "цена" ? DashStyles.Solid : DashStyles.Dash };
        dc.DrawLine(line, new Point(plot.Left, y(v)), new Point(plot.Right, y(v)));
        DrawText(dc, $"{label} {v:0.00}", brush, 10, new Point(plot.Right + 5, y(v) - 8));
    }

    private void DrawText(DrawingContext dc, string text, Brush brush, double size, Point point)
    {
        var formatted = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, axisTypeface, size, brush, 1.0);
        dc.DrawText(formatted, point);
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        InvalidateVisual();
    }
}
