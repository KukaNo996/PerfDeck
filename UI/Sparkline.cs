using System;
using System.Windows;
using System.Windows.Media;

namespace PerfDeck.UI;

/// <summary>
/// 轻量迷你趋势图。直接用 DrawingContext 绘制，无动画、无依赖，开销极低。
/// </summary>
public sealed class Sparkline : FrameworkElement
{
    private double[]? _values;
    private Color _accent = Color.FromRgb(0x4C, 0xC9, 0xF0);
    private double _fixedMax;
    private bool _showLoss;
    private Brush _lineBrush = Brushes.DodgerBlue;
    private Brush _fillBrush = Brushes.Transparent;
    private Brush _lossBrush = Brushes.IndianRed;
    private Brush _baseBrush = Brushes.Transparent;
    private Pen _pen = new(Brushes.DodgerBlue, 1.3);
    private double _lastMax = -1;
    private bool _light;

    public void SetLight(bool light)
    {
        if (_light == light) return;
        _light = light;
        RebuildBrushes();
        InvalidateVisual();
    }

    public void SetData(double[]? values, Color accent, double fixedMax = 0, bool showLoss = false)
    {
        _values = values;
        _fixedMax = fixedMax;
        _showLoss = showLoss;
        if (accent != _accent)
        {
            _accent = accent;
            RebuildBrushes();
        }
        InvalidateVisual();
    }

    private void RebuildBrushes()
    {
        _lineBrush = Palette.Brush(Color.FromArgb(0xDD, _accent.R, _accent.G, _accent.B));
        _pen = new Pen(_lineBrush, 1.3) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        _pen.Freeze();

        var g = new LinearGradientBrush(
            Color.FromArgb(0x38, _accent.R, _accent.G, _accent.B),
            Color.FromArgb(0x00, _accent.R, _accent.G, _accent.B), 90);
        g.Freeze();
        _fillBrush = g;

        _lossBrush = Palette.Brush(Color.FromArgb(0xCC, 0xF8, 0x71, 0x71));
        _baseBrush = _light
            ? Palette.Brush(Color.FromArgb(0x1F, 0x00, 0x00, 0x00))
            : Palette.Brush(Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF));
    }

    public Sparkline()
    {
        RebuildBrushes();
        SnapsToDevicePixels = false;
        IsHitTestVisible = false;
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w < 6 || h < 6) return;

        // 底部基线
        dc.DrawRectangle(_baseBrush, null, new Rect(0, h - 1, w, 1));

        var v = _values;
        if (v == null || v.Length < 2) return;

        int validCount = 0;
        int firstValid = -1;
        for (int i = 0; i < v.Length; i++)
        {
            if (double.IsNaN(v[i])) continue;
            validCount++;
            if (firstValid < 0) firstValid = i;
        }
        if (validCount == 0) return;

        double max = _fixedMax;
        if (max <= 0)
        {
            max = 0;
            for (int i = 0; i < v.Length; i++)
                if (!double.IsNaN(v[i]) && v[i] > max) max = v[i];
            max *= 1.25;
            if (max < 1) max = 1;
        }
        _lastMax = max;

        double n = v.Length;
        double X(int i) => i * (w - 1) / (n - 1);
        double Y(double val)
        {
            double r = Math.Clamp(val / max, 0, 1);
            return h - 2 - r * (h - 4);
        }

        // 分段折线 + 填充
        int i0 = 0;
        while (i0 < v.Length)
        {
            if (double.IsNaN(v[i0])) { i0++; continue; }
            int i1 = i0;
            while (i1 + 1 < v.Length && !double.IsNaN(v[i1 + 1])) i1++;

            if (i1 > i0)
            {
                var geo = new StreamGeometry();
                using (var ctx = geo.Open())
                {
                    ctx.BeginFigure(new Point(X(i0), h - 1), true, true);
                    for (int i = i0; i <= i1; i++)
                        ctx.LineTo(new Point(X(i), Y(v[i])), true, false);
                    ctx.LineTo(new Point(X(i1), h - 1), true, false);
                }
                geo.Freeze();
                dc.DrawGeometry(_fillBrush, null, geo);

                var line = new StreamGeometry();
                using (var ctx = line.Open())
                {
                    ctx.BeginFigure(new Point(X(i0), Y(v[i0])), false, false);
                    for (int i = i0 + 1; i <= i1; i++)
                        ctx.LineTo(new Point(X(i), Y(v[i])), true, false);
                }
                line.Freeze();
                dc.DrawGeometry(null, _pen, line);
            }
            else
            {
                // 孤立点
                dc.DrawEllipse(_lineBrush, null, new Point(X(i0), Y(v[i0])), 1.6, 1.6);
            }
            i0 = i1 + 1;
        }

        // 丢包标记（仅网络曲线，且只标记已有数据之后的部分）
        if (_showLoss)
        {
            for (int i = firstValid + 1; i < v.Length; i++)
            {
                if (!double.IsNaN(v[i])) continue;
                dc.DrawRectangle(_lossBrush, null, new Rect(Math.Max(0, X(i) - 1), h - 5, 2, 4));
            }
        }
    }
}
