using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using PerfDeck.Core;

namespace PerfDeck.UI;

/// <summary>
/// 多日天气：横向分列显示日期、天气图标、高低温和温度带。
/// 自绘，无子元素、无动画，开销与一张折线图相当。
/// 视觉分三段：日期行 → 图标 → 温度，底部是高温/低温曲线夹出的色带。
/// </summary>
public sealed class WeatherStrip : FrameworkElement
{
    private List<DayForecast> _days = new();
    private Color _hiColor = Color.FromRgb(0xF5, 0x92, 0x2B);
    private Color _loColor = Color.FromRgb(0x4E, 0x96, 0xE8);
    private Color _text = Color.FromRgb(0xE9, 0xF1, 0xFA);
    private Color _sub = Color.FromRgb(0x93, 0xA4, 0xB8);
    private Color _icon = Color.FromRgb(0x84, 0x94, 0xA8);
    private bool _light;
    private bool _showCurve = true;

    private static readonly Typeface FaceNormal = new(
        new FontFamily("Microsoft YaHei UI, Segoe UI"),
        FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

    private static readonly Typeface FaceBold = new(
        new FontFamily("Microsoft YaHei UI, Segoe UI"),
        FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

    public void SetLight(bool light)
    {
        if (_light == light) return;
        _light = light;
        _icon = light ? Color.FromRgb(0xA8, 0xB6, 0xC8) : Color.FromRgb(0x84, 0x94, 0xA8);
        InvalidateVisual();
    }

    public void SetTheme(ThemeSpec t)
    {
        _text = t.TextPrimary;
        _sub = t.TextSecondary;
        _hiColor = t.IsLight ? Color.FromRgb(0xE8, 0x7C, 0x18) : Color.FromRgb(0xF5, 0x92, 0x2B);
        _loColor = t.IsLight ? Color.FromRgb(0x3B, 0x82, 0xD6) : Color.FromRgb(0x4E, 0x96, 0xE8);
        SetLight(t.IsLight);
        InvalidateVisual();
    }

    /// <summary>空间不足时只保留数字，不画曲线。</summary>
    public void SetData(List<DayForecast>? days, bool showCurve)
    {
        _days = days ?? new List<DayForecast>();
        _showCurve = showCurve;
        InvalidateVisual();
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w < 40 || h < 30 || _days.Count == 0) return;

        int n = _days.Count;
        double colW = w / n;
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        // 温度范围：上下各留余量，曲线不会贴边
        double minT = double.MaxValue, maxT = double.MinValue;
        foreach (var d in _days)
        {
            if (d.Lo < minT) minT = d.Lo;
            if (d.Hi > maxT) maxT = d.Hi;
        }
        if (maxT - minT < 4) { maxT += 2; minT -= 2; }
        double span = Math.Max(1, maxT - minT);

        // 三行：日期 → 图标 → 温度，底部留给曲线
        double dateY = h * 0.01;
        double iconCy = h * 0.30;
        double iconR = Math.Clamp(Math.Min(colW * 0.20, h * 0.115), 6.5, 16);
        double tempY = h * 0.545;

        bool curve = _showCurve && h >= 96;
        double curveTop = h * 0.73;
        double curveBottom = h - 2;
        double curveH = Math.Max(0, curveBottom - curveTop);

        for (int i = 0; i < n; i++)
        {
            var d = _days[i];
            double cx = colW * (i + 0.5);

            DrawText(dc, d.Label, cx, dateY, 9.5, d.IsToday ? _text : _sub,
                d.IsToday ? FaceBold : FaceNormal, dpi);

            DrawGlyph(dc, d.Code, cx, iconCy, iconR);

            // 高温 / 低温并排：高温在前且更醒目
            var hi = Format($"{d.Hi:0}°", 12.5, _text, FaceBold, dpi);
            var lo = Format($"{d.Lo:0}°", 10, _sub, FaceNormal, dpi);
            double total = hi.Width + 3 + lo.Width;
            double sx = cx - total / 2;
            double maxH = Math.Max(hi.Height, lo.Height);
            dc.DrawText(hi, new Point(sx, tempY + (maxH - hi.Height)));
            dc.DrawText(lo, new Point(sx + hi.Width + 3, tempY + (maxH - lo.Height)));
        }

        if (!curve || curveH < 8) return;

        // 折线点位
        var hiPts = new List<Point>(n);
        var loPts = new List<Point>(n);
        for (int i = 0; i < n; i++)
        {
            double cx = colW * (i + 0.5);
            hiPts.Add(new Point(cx, curveBottom - (_days[i].Hi - minT) / span * curveH));
            loPts.Add(new Point(cx, curveBottom - (_days[i].Lo - minT) / span * curveH));
        }

        // 先铺温度带（高温线与低温线之间），再画线
        if (n >= 2) DrawBand(dc, hiPts, loPts);
        DrawLine(dc, loPts, _loColor);
        DrawLine(dc, hiPts, _hiColor);
    }

    /// <summary>高温线与低温线之间的半透明色带，让“温度区间”一眼可见。</summary>
    private void DrawBand(DrawingContext dc, List<Point> hi, List<Point> lo)
    {
        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            ctx.BeginFigure(hi[0], true, true);
            for (int i = 1; i < hi.Count; i++) ctx.LineTo(hi[i], true, false);
            for (int i = lo.Count - 1; i >= 0; i--) ctx.LineTo(lo[i], true, false);
        }
        geo.Freeze();

        var brush = new LinearGradientBrush(
            Color.FromArgb(0x2E, _hiColor.R, _hiColor.G, _hiColor.B),
            Color.FromArgb(0x22, _loColor.R, _loColor.G, _loColor.B), 90);
        brush.Freeze();

        dc.DrawGeometry(brush, null, geo);
    }

    private void DrawLine(DrawingContext dc, List<Point> pts, Color color)
    {
        if (pts.Count < 2) return;

        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            ctx.BeginFigure(pts[0], false, false);
            for (int i = 1; i < pts.Count; i++) ctx.LineTo(pts[i], true, false);
        }
        geo.Freeze();

        var pen = new Pen(Palette.Brush(color), 1.7)
        {
            LineJoin = PenLineJoin.Round,
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round
        };
        pen.Freeze();
        dc.DrawGeometry(null, pen, geo);

        // 节点：外圈用背景色描一圈，避免两线交叉时糊在一起
        var dot = Palette.Brush(color);
        var ring = new Pen(Palette.Brush(_light
            ? Color.FromArgb(0xE6, 0xFF, 0xFF, 0xFF)
            : Color.FromArgb(0xE6, 0x14, 0x1A, 0x22)), 1.2);
        ring.Freeze();
        foreach (var p in pts) dc.DrawEllipse(dot, ring, p, 2.1, 2.1);
    }

    // ---------- 天气符号 ----------

    private void DrawGlyph(DrawingContext dc, int code, double cx, double cy, double r)
    {
        var sun = Palette.Brush(_light ? Color.FromRgb(0xF5, 0xA6, 0x23) : Color.FromRgb(0xFB, 0xBF, 0x24));
        var cloud = Palette.Brush(_icon);
        var rain = Palette.Brush(_loColor);

        bool isSun = code is 0 or 1;
        bool isRain = code is >= 51 and <= 67 or >= 80 and <= 82 or >= 95;
        bool isSnow = code is >= 71 and <= 77 or 85 or 86;
        bool partly = code == 2;

        if (isSun)
        {
            dc.DrawEllipse(sun, null, new Point(cx, cy), r * 0.44, r * 0.44);
            if (code == 0)
            {
                var pen = new Pen(sun, 1.1) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
                pen.Freeze();
                for (int i = 0; i < 8; i++)
                {
                    double a = Math.PI * 2 * i / 8;
                    dc.DrawLine(pen,
                        new Point(cx + Math.Cos(a) * r * 0.64, cy + Math.Sin(a) * r * 0.64),
                        new Point(cx + Math.Cos(a) * r * 0.93, cy + Math.Sin(a) * r * 0.93));
                }
            }
            return;
        }

        // 多云：太阳露出一点，再压一朵云
        if (partly)
            dc.DrawEllipse(sun, null, new Point(cx + r * 0.42, cy - r * 0.42), r * 0.30, r * 0.30);

        // 云：两个圆 + 底部圆角矩形，拼出饱满轮廓
        double baseY = cy + (partly ? r * 0.10 : r * 0.04);
        dc.DrawEllipse(cloud, null, new Point(cx - r * 0.33, baseY), r * 0.30, r * 0.26);
        dc.DrawEllipse(cloud, null, new Point(cx + r * 0.30, baseY), r * 0.27, r * 0.235);
        dc.DrawEllipse(cloud, null, new Point(cx - r * 0.02, baseY - r * 0.22), r * 0.37, r * 0.31);
        dc.DrawRoundedRectangle(cloud, null,
            new Rect(cx - r * 0.33, baseY - r * 0.24, r * 0.63, r * 0.50), r * 0.25, r * 0.25);

        if (!isRain && !isSnow) return;

        var dropPen = new Pen(isSnow ? cloud : rain, 1.1)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round
        };
        dropPen.Freeze();

        for (int i = -1; i <= 1; i++)
        {
            double x = cx + i * r * 0.30;
            if (isSnow) dc.DrawEllipse(cloud, null, new Point(x, cy + r * 0.56), 1.0, 1.0);
            else dc.DrawLine(dropPen, new Point(x, cy + r * 0.44), new Point(x - r * 0.09, cy + r * 0.68));
        }
    }

    // ---------- 文本 ----------

    private static FormattedText Format(string text, double size, Color color, Typeface face, double dpi) =>
        new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            face, size, Palette.Brush(color), dpi);

    private static void DrawText(DrawingContext dc, string text, double cx, double y, double size,
        Color color, Typeface face, double dpi)
    {
        var ft = Format(text, size, color, face, dpi);
        dc.DrawText(ft, new Point(cx - ft.Width / 2, y));
    }
}
