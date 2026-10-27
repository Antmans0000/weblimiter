using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using WebLimiter.App.Util;
using WebLimiter.App.ViewModels;

namespace WebLimiter.App.Controls;

/// <summary>Stacked daily bars (download + upload) with a hover read-out.</summary>
public sealed class DailyBars : FrameworkElement
{
    public static readonly DependencyProperty DaysProperty = DependencyProperty.Register(
        nameof(Days), typeof(IReadOnlyList<DayBar>), typeof(DailyBars),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<DayBar>? Days
    {
        get => (IReadOnlyList<DayBar>?)GetValue(DaysProperty);
        set => SetValue(DaysProperty, value);
    }

    private static readonly Brush Down = Frozen(Color.FromRgb(0x22, 0xC5, 0x5E));
    private static readonly Brush Up = Frozen(Color.FromRgb(0xF5, 0x9E, 0x0B));
    private static readonly Brush Muted = Frozen(Color.FromRgb(0x8B, 0x93, 0xA1));
    private static readonly Brush Hover = Frozen(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF));
    private static readonly Brush Text = Frozen(Color.FromRgb(0xE6, 0xE8, 0xEC));

    private static Brush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

    private int _hover = -1;

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var days = Days;
        if (days == null || days.Count == 0) return;
        int i = (int)(e.GetPosition(this).X / (ActualWidth / days.Count));
        if (i != _hover) { _hover = Math.Clamp(i, 0, days.Count - 1); InvalidateVisual(); }
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        _hover = -1;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));
        var days = Days;
        if (days == null || days.Count == 0 || w < 10) return;

        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        const double labelH = 16, topPad = 22;
        double chartH = h - labelH - topPad;
        long max = Math.Max(1, days.Max(d => d.Down + d.Up));
        double slot = w / days.Count;
        double barW = Math.Max(2, Math.Min(28, slot * 0.7));
        int labelEvery = Math.Max(1, (int)Math.Ceiling(40 / slot));

        for (int i = 0; i < days.Count; i++)
        {
            var d = days[i];
            double x = i * slot + (slot - barW) / 2;
            if (i == _hover) dc.DrawRectangle(Hover, null, new Rect(i * slot, topPad, slot, chartH));
            double dh = d.Down / (double)max * chartH;
            double uh = d.Up / (double)max * chartH;
            double baseY = topPad + chartH;
            if (dh > 0) dc.DrawRectangle(Down, null, new Rect(x, baseY - dh, barW, dh));
            if (uh > 0) dc.DrawRectangle(Up, null, new Rect(x, baseY - dh - uh, barW, uh));

            if (i % labelEvery == 0 || i == days.Count - 1)
            {
                var ft = Ft(d.Day.ToString("dd/MM"), Muted, 10, dpi);
                dc.DrawText(ft, new Point(i * slot + (slot - ft.Width) / 2, h - labelH + 2));
            }
        }

        if (_hover >= 0 && _hover < days.Count)
        {
            var d = days[_hover];
            var ft = Ft($"{d.Day:dddd d MMMM}   ↓ {Format.Bytes(d.Down)}   ↑ {Format.Bytes(d.Up)}", Text, 12, dpi);
            dc.DrawText(ft, new Point(0, 0));
        }
    }

    private static FormattedText Ft(string s, Brush b, double size, double dpi) =>
        new(s, CultureInfo.GetCultureInfo("fr-FR"), FlowDirection.LeftToRight, new Typeface("Segoe UI"), size, b, dpi);
}
