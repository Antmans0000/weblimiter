using System;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using WebLimiter.App.Util;
using WebLimiter.App.ViewModels;

namespace WebLimiter.App.Controls;

/// <summary>Live area chart of the last two minutes of download / upload rates.</summary>
public sealed class TrafficGraph : FrameworkElement
{
    public static readonly DependencyProperty HistoryProperty = DependencyProperty.Register(
        nameof(History), typeof(History), typeof(TrafficGraph),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public History? History
    {
        get => (History?)GetValue(HistoryProperty);
        set => SetValue(HistoryProperty, value);
    }

    private static readonly Brush DownStroke = Freeze(new SolidColorBrush(Color.FromRgb(0x22, 0xC5, 0x5E)));
    private static readonly Brush DownFill = Freeze(new SolidColorBrush(Color.FromArgb(0x40, 0x22, 0xC5, 0x5E)));
    private static readonly Brush UpStroke = Freeze(new SolidColorBrush(Color.FromRgb(0xF5, 0x9E, 0x0B)));
    private static readonly Brush UpFill = Freeze(new SolidColorBrush(Color.FromArgb(0x30, 0xF5, 0x9E, 0x0B)));
    private static readonly Brush GridBrush = Freeze(new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)));
    private static readonly Brush LabelBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x8B, 0x93, 0xA1)));

    private static T Freeze<T>(T f) where T : Freezable { f.Freeze(); return f; }

    public void Invalidate() => InvalidateVisual();

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w < 10 || h < 10) return;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));

        var history = History;
        double max = 1;
        if (history != null)
            for (int i = 0; i < History.Length; i++)
                max = Math.Max(max, Math.Max(history.Down(i), history.Up(i)));
        max = NiceCeiling(max * 1.1);

        var pen = new Pen(GridBrush, 1);
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        for (int g = 1; g <= 3; g++)
        {
            double y = Math.Round(h * g / 4) + 0.5;
            dc.DrawLine(pen, new Point(0, y), new Point(w, y));
        }
        var label = new FormattedText(Format.Bytes(max) + "/s", CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI"), 10, LabelBrush, dpi);
        dc.DrawText(label, new Point(4, 2));

        if (history == null) return;
        DrawSeries(dc, w, h, max, history.Down, DownFill, DownStroke);
        DrawSeries(dc, w, h, max, history.Up, UpFill, UpStroke);
    }

    private static void DrawSeries(DrawingContext dc, double w, double h, double max,
        Func<int, double> value, Brush fill, Brush stroke)
    {
        int n = History.Length;
        double step = w / (n - 1);
        var line = new StreamGeometry();
        var area = new StreamGeometry();
        using (var lc = line.Open())
        using (var ac = area.Open())
        {
            ac.BeginFigure(new Point(0, h), true, true);
            for (int i = 0; i < n; i++)
            {
                var pt = new Point(i * step, h - value(i) / max * (h - 4));
                if (i == 0) lc.BeginFigure(pt, false, false);
                else lc.LineTo(pt, true, true);
                ac.LineTo(pt, false, true);
            }
            ac.LineTo(new Point(w, h), false, false);
        }
        line.Freeze();
        area.Freeze();
        dc.DrawGeometry(fill, null, area);
        dc.DrawGeometry(null, new Pen(stroke, 1.5) { LineJoin = PenLineJoin.Round }, line);
    }

    private static double NiceCeiling(double v)
    {
        double exp = Math.Pow(10, Math.Floor(Math.Log10(v)));
        foreach (double m in new[] { 1, 2, 2.5, 5, 10 })
            if (m * exp >= v) return m * exp;
        return 10 * exp;
    }
}
