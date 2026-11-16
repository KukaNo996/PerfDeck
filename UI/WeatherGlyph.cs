using System;
using System.Windows;
using System.Windows.Media;

namespace PerfDeck.UI;

/// <summary>
/// 天气图标：用矢量几何绘制，跟随主题配色，不依赖图片资源或字体。
/// 按 WMO 天气代码切换形状（晴 / 多云 / 阴 / 雾 / 雨 / 雪 / 雷）。
/// </summary>
public sealed class WeatherGlyph : FrameworkElement
{
    private int _code = -1;
    private bool _light;

    private Brush _cloudBrush = Brushes.WhiteSmoke;
    private Brush _sunBrush = Brushes.Gold;
    private Brush _accentBrush = Brushes.LightSkyBlue;
    private Pen _rayPen = new(Brushes.Gold, 1.4);

    public void SetLight(bool light)
    {
        if (_light == light) return;
        _light = light;
        Rebuild();
        InvalidateVisual();
    }

    private void Rebuild()
    {
        _cloudBrush = Palette.Brush(_light ? Color.FromRgb(0x9C, 0xAD, 0xC2) : Color.FromRgb(0xDF, 0xEA, 0xF7));
        _sunBrush = Palette.Brush(Color.FromRgb(0xFB, 0xC0, 0x4B));
        _accentBrush = Palette.Brush(Color.FromRgb(0x7D, 0xD3, 0xFC));
        _rayPen = new Pen(_sunBrush, 1.6) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        _rayPen.Freeze();
    }

    public WeatherGlyph()
    {
        Rebuild();
        IsHitTestVisible = false;
    }

    public void SetCode(int code)
    {
        if (_code == code) return;
        _code = code;
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
        if (w < 6 || h < 6 || _code < 0) return;

        double s = Math.Min(w, h);
        double ox = (w - s) / 2, oy = (h - s) / 2;

        // 归一化到 24×24 的画布，再整体缩放
        dc.PushTransform(new TranslateTransform(ox, oy));
        dc.PushTransform(new ScaleTransform(s / 24.0, s / 24.0));

        switch (KindOf(_code))
        {
            case Kind.Sun:
                DrawSun(dc, 12, 12, 5.2, true);
                break;

            case Kind.PartlyCloudy:
                DrawSun(dc, 9, 8.5, 3.8, true);
                DrawCloud(dc, 7.5, 11.5, 15.5, 10.5);
                break;

            case Kind.Cloudy:
                DrawCloud(dc, 7, 7.5, 13, 9, 0.55);
                DrawCloud(dc, 5, 10.5, 15, 10);
                break;

            case Kind.Fog:
                DrawCloud(dc, 5.5, 6, 14, 9.5);
                DrawLine(dc, 5, 18, 19, 18, 1.5);
                DrawLine(dc, 7, 21, 17, 21, 1.5);
                break;

            case Kind.Rain:
                DrawCloud(dc, 5, 5.5, 15, 9.5);
                DrawDrop(dc, 8.5, 16.5);
                DrawDrop(dc, 12.5, 18.5);
                DrawDrop(dc, 16.5, 16.5);
                break;

            case Kind.Snow:
                DrawCloud(dc, 5, 5.5, 15, 9.5);
                DrawFlake(dc, 8.5, 17.5);
                DrawFlake(dc, 12.5, 20);
                DrawFlake(dc, 16.5, 17.5);
                break;

            case Kind.Thunder:
                DrawCloud(dc, 5, 5, 15, 9.5);
                DrawBolt(dc);
                break;
        }

        dc.Pop();
        dc.Pop();
    }

    private enum Kind { Sun, PartlyCloudy, Cloudy, Fog, Rain, Snow, Thunder }

    private static Kind KindOf(int code) => code switch
    {
        0 => Kind.Sun,
        1 or 2 => Kind.PartlyCloudy,
        3 => Kind.Cloudy,
        45 or 48 => Kind.Fog,
        51 or 53 or 55 or 56 or 57 => Kind.Rain,
        61 or 63 or 65 or 66 or 67 or 80 or 81 or 82 => Kind.Rain,
        71 or 73 or 75 or 77 or 85 or 86 => Kind.Snow,
        95 or 96 or 99 => Kind.Thunder,
        _ => Kind.Cloudy
    };

    private void DrawSun(DrawingContext dc, double cx, double cy, double r, bool rays)
    {
        dc.DrawEllipse(_sunBrush, null, new Point(cx, cy), r, r);
        if (!rays) return;

        for (int i = 0; i < 8; i++)
        {
            double a = Math.PI * 2 * i / 8;
            double x1 = cx + Math.Cos(a) * (r + 2.0);
            double y1 = cy + Math.Sin(a) * (r + 2.0);
            double x2 = cx + Math.Cos(a) * (r + 4.2);
            double y2 = cy + Math.Sin(a) * (r + 4.2);
            dc.DrawLine(_rayPen, new Point(x1, y1), new Point(x2, y2));
        }
    }

    /// <summary>由两个圆 + 一个圆角底座堆成的云。</summary>
    private void DrawCloud(DrawingContext dc, double x, double y, double w, double h, double opacity = 1.0)
    {
        var brush = _cloudBrush;
        if (opacity < 1.0)
        {
            var c = ((SolidColorBrush)brush).Color;
            brush = Palette.Brush(Color.FromArgb((byte)(255 * opacity), c.R, c.G, c.B));
        }

        dc.DrawEllipse(brush, null, new Point(x + w * 0.34, y + h * 0.40), w * 0.27, h * 0.27);
        dc.DrawEllipse(brush, null, new Point(x + w * 0.64, y + h * 0.34), w * 0.30, h * 0.30);
        dc.DrawRoundedRectangle(brush, null,
            new Rect(x + w * 0.10, y + h * 0.46, w * 0.80, h * 0.34), h * 0.17, h * 0.17);
    }

    private void DrawLine(DrawingContext dc, double x1, double y1, double x2, double y2, double thickness)
    {
        var pen = new Pen(_accentBrush, thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        pen.Freeze();
        dc.DrawLine(pen, new Point(x1, y1), new Point(x2, y2));
    }

    private void DrawDrop(DrawingContext dc, double x, double y)
    {
        var pen = new Pen(_accentBrush, 1.7) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        pen.Freeze();
        dc.DrawLine(pen, new Point(x + 0.9, y), new Point(x - 0.4, y + 3.2));
    }

    private void DrawFlake(DrawingContext dc, double x, double y)
    {
        dc.DrawEllipse(_accentBrush, null, new Point(x, y), 1.25, 1.25);
    }

    private void DrawBolt(DrawingContext dc)
    {
        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            ctx.BeginFigure(new Point(13.4, 13.5), true, true);
            ctx.LineTo(new Point(9.8, 19.2), true, false);
            ctx.LineTo(new Point(12.2, 19.2), true, false);
            ctx.LineTo(new Point(10.6, 22.5), true, false);
            ctx.LineTo(new Point(15.6, 17.6), true, false);
            ctx.LineTo(new Point(13.0, 17.6), true, false);
            ctx.LineTo(new Point(14.9, 13.5), true, false);
        }
        geo.Freeze();
        dc.DrawGeometry(_sunBrush, null, geo);
    }
}
