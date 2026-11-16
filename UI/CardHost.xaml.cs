using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using PerfDeck.Core;

namespace PerfDeck.UI;

public partial class CardHost : UserControl
{
    public CardConfig Config { get; }

    private double _progress = -1;
    private double _valueFontBase = 34;
    private double _valueScale = 1.0;
    private bool _dragging;
    private readonly ScaleTransform _lift = new(1, 1);
    /// <summary>让位动画用的位移（渲染变换，不触发布局，比动 Canvas.Left/Top 流畅得多）。</summary>
    private readonly TranslateTransform _shift = new(0, 0);

    private readonly DropShadowEffect _shadow = new()
    {
        Direction = 315,
        Color = Colors.Black,
        RenderingBias = RenderingBias.Quality
    };
    private bool _shadowOn;

    private ThemeSpec _theme = Themes.All[0];
    private double _alpha = 1.0;
    private bool _hasChip;
    private bool _centered;
    /// <summary>当前卡片是否有“数值下方副行”（上行/写入速率等）</summary>
    private bool _hasSub;
    /// <summary>统一的主数值字号（0 = 按卡片高度自适应）</summary>
    private double _valueSize;
    /// <summary>量固定宽度用的离屏文本（时钟卡）</summary>
    private System.Windows.Controls.TextBlock? _probe;
    private bool _rowsThemed;
    private FontFamily _defaultValueFont = null!;
    private Color _lastBorder;

    private readonly List<(Grid Root, TextBlock Left, TextBlock Right)> _rows = new();

    public Thumb GripThumb => Grip;
    public TranslateTransform Shift => _shift;
    public Sparkline SparkView => Spark;

    public CardHost(CardConfig cfg)
    {
        InitializeComponent();
        Config = cfg;
        RenderTransformOrigin = new Point(0.5, 0.5);
        var xf = new TransformGroup();
        xf.Children.Add(_lift);
        xf.Children.Add(_shift);
        RenderTransform = xf;
        _lastBorder = _theme.Border;
        BarTrack.SizeChanged += (_, _) => ApplyProgress();
        Loaded += (_, _) => UpdateTypography();
        // 悬停时投影加强，配合 1.03 的放大做出“抬起”手感
        MouseEnter += (_, _) => UpdateShadow(1.75);
        MouseLeave += (_, _) => UpdateShadow(1.0);

        _defaultValueFont = ValueText.FontFamily;
        ApplyTheme(_theme, 1.0);
    }

    /// <summary>切换主题：卡片底色、文字、描边、进度条一起重刷。</summary>
    public void ApplyTheme(ThemeSpec t, double cardAlpha)
    {
        _theme = t;
        _alpha = cardAlpha;
        _lastBorder = t.Border;

        Shell.Background = new LinearGradientBrush(
            Tint(t.CardTop), Tint(t.CardBottom), new Point(0, 0), new Point(0.45, 1));

        TitleText.Foreground = Palette.Brush(t.TextSecondary);
        SubText.Foreground = Palette.Brush(t.TextSecondary);
        UnitText.Foreground = Palette.Brush(t.TextTertiary);
        FooterText.Foreground = Palette.Brush(t.TextTertiary);
        ChipBox.Background = Palette.Brush(Tint(t.ChipBg));
        ChipBox.BorderBrush = Palette.Brush(t.ChipBorder);
        ChipText.Foreground = Palette.Brush(t.TextSecondary);
        BarTrack.Background = Palette.Brush(t.Track);
        Spark.SetLight(t.IsLight);
        WeatherIcon.SetLight(t.IsLight);
        WeatherDays.SetTheme(t);

        _rowsThemed = false;
        foreach (var (_, left, right) in _rows)
        {
            left.Foreground = Palette.Brush(t.TextPrimary);
            right.Foreground = Palette.Brush(t.TextSecondary);
        }

        Shell.BorderBrush = Palette.Brush(_lastBorder);
        UpdateShadow();
    }

    /// <summary>
    /// 投影强度。浅色主题的阴影要淡（否则像脏污），深色主题要重才能看出层次；
    /// factor 由悬停 / 拖动状态给出。
    /// </summary>
    /// <summary>
    /// 投影参数。要"柔"不要"硬"：模糊半径大、下移距离小、透明度低，
    /// 出来的是一圈淡淡的悬浮感，而不是描边。浅色主题再淡一档。
    /// </summary>
    private void UpdateShadow(double factor = 1.0)
    {
        if (!_shadowOn) return;

        bool light = _theme.IsLight;
        _shadow.BlurRadius = 20 * factor;
        _shadow.ShadowDepth = 2.6 * factor;      // 只往右下挪一点点
        _shadow.Opacity = Math.Clamp((light ? 0.10 : 0.22) * factor, 0, 0.6);
        _shadow.Direction = 315;                 // 右下
    }

    /// <summary>开关卡片投影（设置项，默认关闭）。</summary>
    public void SetShadow(bool on)
    {
        if (_shadowOn == on) return;
        _shadowOn = on;

        if (!on)
        {
            Shell.Effect = null;
            return;
        }

        UpdateShadow();
        Shell.Effect = _shadow;
    }

    private Color Tint(Color c) => Color.FromArgb((byte)Math.Round(255 * Math.Clamp(_alpha, 0.05, 1)), c.R, c.G, c.B);

    /// <summary>拖动中的“浮起”状态：轻微放大 + 描边高亮 + 移动光标。</summary>
    public void SetDragging(bool on)
    {
        _dragging = on;
        // 只有真正进入拖动才切成“移动”光标；平时保持普通箭头
        Cursor = on ? System.Windows.Input.Cursors.SizeAll : null;
        _lift.ScaleX = _lift.ScaleY = on ? 1.03 : 1.0;
        UpdateShadow(on ? 2.6 : 1.0);
        Shell.BorderBrush = on
            ? Palette.Brush(Color.FromArgb(0xCC, 0x7D, 0xD3, 0xFC))
            : Palette.Brush(_lastBorder);
    }

    public void SetEditing(bool editing, bool allowResize = true)
    {
        // 自动适配模式下尺寸由布局统一决定，尺寸手柄没有意义，直接收起
        Grip.Visibility = editing && allowResize ? Visibility.Visible : Visibility.Collapsed;
        if (!_dragging) Cursor = null;      // 平时就是普通箭头，长按拖动时再切移动光标
    }

    private void UpdateTypography()
    {
        double h = ActualHeight, w = ActualWidth;
        if (h <= 0 || w <= 0) return;

        // 时钟是整行大卡片：只按高度算会显得又小又空，宽度也要参与
        _valueFontBase = _centered
            ? Math.Clamp(Math.Min(h * 0.44, w * 0.062), 18, 120) * _valueScale
            : (_valueSize > 0 ? _valueSize : Math.Clamp(h * 0.315, 14, 92)) * _valueScale;
        ValueText.FontSize = _valueFontBase;
        UnitText.FontSize = Math.Clamp(_valueFontBase * 0.36, 9, 28);
        TitleText.FontSize = Math.Clamp(h * 0.070, 8.5, 14);
        ChipText.FontSize = Math.Clamp(h * 0.062, 8, 13);
        FooterText.FontSize = Math.Clamp(h * 0.066, 8, 12.5);
        // 有副行时曲线让位（只剩一条细趋势线），否则副行会被裁掉
        Spark.Height = Math.Clamp(h * (_hasSub ? 0.055 : 0.185), 8, 46);

        double icon = Math.Clamp(h * 0.275, 18, 52);
        WeatherIcon.Width = icon;
        WeatherIcon.Height = icon;

        double d = Math.Clamp(h * 0.040, 4.5, 8);
        Dot.Width = Dot.Height = d;
        TitleText.Margin = new Thickness(Math.Clamp(h * 0.042, 4, 8), 0, 0, 0);

        double mx = Math.Clamp(w * 0.055, 9, 20);
        double my = Math.Clamp(h * 0.082, 7, 16);
        ContentRoot.Margin = new Thickness(mx, my, mx, my);

        FooterText.Visibility = h > 96 && !_centered ? Visibility.Visible : Visibility.Collapsed;
        FooterText.Margin = new Thickness(0, Math.Clamp(h * 0.045, 3, 9), 0, 0);
        ChipBox.Visibility = _hasChip && w > 150 ? Visibility.Visible : Visibility.Collapsed;

        // 卡片内容区的可用宽度：TextBlock 在 StackPanel 里宽度=内容宽度，
        // 不给上限的话长文本（IP、型号、健康度）会直接顶出卡片边界。
        double contentW = Math.Max(40, w - mx * 2);
        FooterText.MaxWidth = contentW;
        SubText.MaxWidth = contentW;
        ValueText.MaxWidth = contentW;
        ChipText.MaxWidth = Math.Max(0, contentW - 96);   // 给标题让出位置

        SubText.FontSize = _centered
            ? Math.Clamp(h * 0.125, 12, 26)
            : Math.Clamp(h * 0.075, 10, 15);
        SubText.Margin = new Thickness(0, Math.Clamp(h * 0.018, 1, 4), 0, 0);

        // 字号/字体变了，时钟单元格要重新量宽（否则宽度还是旧字号的）
        _digitCellW = -1;
        _colonCellW = -1;

        double rowFont = Math.Clamp(h * 0.058, 9, 15);
        foreach (var (_, left, right) in _rows)
        {
            left.FontSize = rowFont;
            right.FontSize = rowFont;
        }

        FitValue();
    }

    private void FitValue()
    {
        // 固定宽度只是用来“钉住”居中位置，量真实宽度前必须先放开，
        // 否则 Measure 会直接返回固定宽度，缩放判断失效。
        ValueText.ClearValue(WidthProperty);

        if (ContentRoot.ActualWidth <= 2) return;
        double avail = ContentRoot.ActualWidth;
        if (WeatherIcon.Visibility == Visibility.Visible) avail -= WeatherIcon.Width + 8;
        ValueText.FontSize = _valueFontBase;

        if (_centered)
        {
            // 时钟走逐字符单元格，用「最宽数字串」估算总宽，避免量到已折叠的 ValueText
            double unit = MeasureCellWidth("8");
            string txt = ValueText.Text ?? "";
            int digits = 0;
            for (int i = 0; i < txt.Length; i++) if (char.IsDigit(txt[i])) digits++;
            int others = txt.Length - digits;
            double needCells = unit * digits + MeasureCellWidth(":") * others;
            if (needCells > avail && needCells > 1)
                ValueText.FontSize = Math.Max(9, _valueFontBase * (avail / needCells));
            return;
        }

        ValueText.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        UnitText.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double need = ValueText.DesiredSize.Width;
        if (!string.IsNullOrEmpty(UnitText.Text)) need += UnitText.DesiredSize.Width + 5;
        if (need > avail && need > 1)
        {
            double k = avail / need;
            ValueText.FontSize = Math.Max(9, _valueFontBase * k);
        }
    }

    // ---------- 时钟：逐字符定宽渲染 ----------
    //
    // 中文字体（微软雅黑等）的数字是比例宽度，同一串 "23:39:11" 和 "23:39:88"
    // 总宽能差 20+ 像素，整串放在居中的 TextBlock 里，秒数一变整块就左右漂。
    // 这里把时间拆成每个字符一个定宽单元格：数字统一用「8」的宽度，
    // 冒号用自身宽度，串的总宽恒定，位置再也不动。

    private readonly List<TextBlock> _clockCells = new();
    private double _digitCellW = -1;
    private double _colonCellW = -1;
    private double _metricFontSize = -1;

    /// <summary>用离屏文本量一个字符的宽度（跟随当前字体/字号）。</summary>
    private double MeasureCellWidth(string probeText)
    {
        _probe ??= new TextBlock();
        _probe.Text = probeText;
        _probe.FontFamily = ValueText.FontFamily;
        _probe.FontSize = ValueText.FontSize;
        _probe.FontWeight = ValueText.FontWeight;
        TextOptions.SetTextFormattingMode(_probe, TextOptions.GetTextFormattingMode(ValueText));
        TextOptions.SetTextRenderingMode(_probe, TextOptions.GetTextRenderingMode(ValueText));
        _probe.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return Math.Ceiling(_probe.DesiredSize.Width);
    }

    /// <summary>按当前字号刷新单元格宽度（字号/字体变化时调用）。</summary>
    private void ResetClockCellMetrics()
    {
        _digitCellW = MeasureCellWidth("8");
        _colonCellW = MeasureCellWidth(":");
    }

    /// <summary>把时间文本渲染成一排定宽字符；居中模式之外自动隐藏。</summary>
    private void RenderClockChars(string? text, bool centered)
    {
        bool useCells = centered && !string.IsNullOrEmpty(text);
        ClockChars.Visibility = useCells ? Visibility.Visible : Visibility.Collapsed;
        ValueText.Visibility = useCells ? Visibility.Collapsed : Visibility.Visible;
        if (!useCells) return;

        // 字号变了要重新量宽，否则单元格宽度和字号对不上
        if (_digitCellW <= 0 || Math.Abs(_metricFontSize - ValueText.FontSize) > 0.01)
        {
            ResetClockCellMetrics();
            _metricFontSize = ValueText.FontSize;
        }

        // 单元格数量跟随文本长度（HH:mm 与 HH:mm:ss 之间切换时会变）
        while (_clockCells.Count < text!.Length)
        {
            var tb = new TextBlock
            {
                FontWeight = ValueText.FontWeight,
                TextAlignment = TextAlignment.Center,
                Foreground = ValueText.Foreground,
                VerticalAlignment = VerticalAlignment.Bottom
            };
            _clockCells.Add(tb);
            ClockChars.Children.Add(tb);
        }
        while (_clockCells.Count > text.Length)
        {
            ClockChars.Children.Remove(_clockCells[^1]);
            _clockCells.RemoveAt(_clockCells.Count - 1);
        }

        for (int i = 0; i < text.Length; i++)
        {
            char ch = text[i];
            var tb = _clockCells[i];
            if (tb.Text != ch.ToString()) tb.Text = ch.ToString();
            if (tb.FontFamily != ValueText.FontFamily) tb.FontFamily = ValueText.FontFamily;
            if (Math.Abs(tb.FontSize - ValueText.FontSize) > 0.01) tb.FontSize = ValueText.FontSize;
            if (!ReferenceEquals(tb.Foreground, ValueText.Foreground)) tb.Foreground = ValueText.Foreground;

            double w = char.IsDigit(ch) ? _digitCellW : _colonCellW;
            // 注意：TextBlock.Width 初始是 NaN，NaN 与任何数比较都是 false，
            // 所以必须先判 IsNaN，否则永远不会真正赋上宽度（这个坑踩过一次）
            if (double.IsNaN(tb.Width) || Math.Abs(tb.Width - w) > 0.01) tb.Width = w;
        }

        double total = 0;
        for (int i = 0; i < _clockCells.Count; i++) total += _clockCells[i].Width;
        total = Math.Ceiling(total);
        if (double.IsNaN(ClockChars.Width) || Math.Abs(ClockChars.Width - total) > 0.01)
            ClockChars.Width = total;
    }

    /// <summary>
    /// 命中交互元素（热搜条目 / 角标）时，在隧道阶段就把 Down 标为已处理。
    /// 否则外层会 CaptureMouse，子元素收不到 Up，表现为"点了没反应"。
    /// </summary>
    protected override void OnPreviewMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnPreviewMouseLeftButtonDown(e);
        if (FindTagged(e.OriginalSource as DependencyObject) != null) e.Handled = true;
    }

    /// <summary>
    /// 点击统一在这里收口：捕获鼠标后子元素收不到 Up，
    /// 只有卡片的 Preview 阶段还拿着原始命中元素，最可靠。
    /// </summary>
    protected override void OnPreviewMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnPreviewMouseLeftButtonUp(e);

        var fe = FindTagged(e.OriginalSource as DependencyObject);
        if (fe == null) return;

        e.Handled = true;
        try
        {
            if (fe.Tag is string url && !string.IsNullOrWhiteSpace(url)) OpenLink(url);
            else if (fe.Tag is Action act) act();
        }
        catch (Exception ex)
        {
            Log.Warn("卡片点击处理异常：" + ex.Message);
        }
    }

    /// <summary>向上找第一个带 Tag 的祖先元素（交互区标记）。</summary>
    private FrameworkElement? FindTagged(DependencyObject? src)
    {
        while (src != null && !ReferenceEquals(src, this))
        {
            if (src is FrameworkElement fe && fe.Tag is string or Action) return fe;
            src = VisualTreeHelper.GetParent(src);
        }
        return null;
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        UpdateTypography();
    }

    public void Apply(CardModel m)
    {
        // 副行是否存在会影响主数值字号与曲线高度（必须给副行留出空间，
        // 否则会被后声明的曲线控件盖住），所以先算出来再排版。
        bool hasSub = !m.Centered && !string.IsNullOrEmpty(m.Sub);
        if (hasSub != _hasSub)
        {
            _hasSub = hasSub;
            UpdateTypography();
        }

        if (Math.Abs(_valueScale - m.ValueScale) > 0.001 ||
            Math.Abs(_valueSize - m.ValueSize) > 0.01)
        {
            _valueScale = m.ValueScale;
            _valueSize = m.ValueSize;
            UpdateTypography();
        }

        if (m.WeatherCode >= 0)
        {
            WeatherIcon.Visibility = Visibility.Visible;
            WeatherIcon.SetCode(m.WeatherCode);
        }
        else
        {
            WeatherIcon.Visibility = Visibility.Collapsed;
        }

        if (TitleText.Text != m.Title) TitleText.Text = m.Title;

        // 悬停提示：卡片一行放不下的信息（IPv6、掩码、DNS、MTU…）收在这里
        string? tip = string.IsNullOrWhiteSpace(m.Tip) ? null : m.Tip;
        if ((ToolTip as string) != tip) ToolTip = tip;

        _hasChip = !string.IsNullOrEmpty(m.Chip);
        ChipBox.Cursor = m.ChipClick != null ? System.Windows.Input.Cursors.Hand : null;
        ChipBox.Tag = m.ChipClick;
        if (_hasChip)
        {
            if (ChipText.Text != m.Chip) ChipText.Text = m.Chip!;
            ChipBox.Visibility = ActualWidth > 88 ? Visibility.Visible : Visibility.Collapsed;
            var chipFg = Palette.Brush(m.ChipSeverity switch
            {
                Severity.Critical => _theme.CritText,
                Severity.Warn => _theme.WarnText,
                _ => _theme.TextSecondary
            });
            if (!ReferenceEquals(ChipText.Foreground, chipFg)) ChipText.Foreground = chipFg;

            var chipBorder = Palette.Brush(m.ChipSeverity switch
            {
                Severity.Critical => Color.FromArgb(0x88, _theme.CritText.R, _theme.CritText.G, _theme.CritText.B),
                Severity.Warn => Color.FromArgb(0x77, _theme.WarnText.R, _theme.WarnText.G, _theme.WarnText.B),
                _ => _theme.ChipBorder
            });
            if (!ReferenceEquals(ChipBox.BorderBrush, chipBorder)) ChipBox.BorderBrush = chipBorder;
        }
        else
        {
            ChipBox.Visibility = Visibility.Collapsed;
        }

        // 多日天气：只有卡片真正放大（宽 ≥330、高 ≥176）才展开，
        // 标准尺寸放不下 3 天信息，强行塞进去反而更难读，此时退回单日。
        bool showDays = m.Days is { Count: >= 3 } && ActualWidth >= 330 && ActualHeight >= 176;
        WeatherDays.Visibility = showDays ? Visibility.Visible : Visibility.Collapsed;

        if (showDays)
        {
            // 每列至少 92px 才好读；最多 6 天
            int n = Math.Clamp((int)(ActualWidth / 92), 3, m.Days!.Count);
            var slice = m.Days.GetRange(0, Math.Min(n, m.Days.Count));
            // 高度不够只留数字，避免曲线挤成一条糊线
            bool curve = ActualHeight >= 196;
            WeatherDays.SetData(slice, curve);
        }

        // 排行榜（进程卡片）/ 热搜列表 / 主数值
        bool hasHot = m.Hots is { Count: > 0 };
        bool hasRows = m.Rows != null && m.Rows.Count > 0;
        if (showDays)
        {
            ValueRow.Visibility = Visibility.Collapsed;
            ListArea.Visibility = Visibility.Collapsed;
        }
        else if (hasHot || hasRows)
        {
            ValueRow.Visibility = Visibility.Collapsed;
            ListArea.Visibility = Visibility.Visible;
            if (hasHot)
            {
                RenderHotTabs(m);
                RenderHotRows(m.Hots!);
            }
            else
            {
                HotTabs.Visibility = Visibility.Collapsed;
                RenderRows(m.Rows!);
            }
        }
        else
        {
            ListArea.Visibility = Visibility.Collapsed;
            ValueRow.Visibility = Visibility.Visible;

            bool changed = ValueText.Text != m.Value;
            if (changed) ValueText.Text = m.Value;
            if (UnitText.Text != m.Unit) UnitText.Text = m.Unit;
            if (changed) FitValue();
        }

        if (FooterText.Text != m.Footer) FooterText.Text = m.Footer;

        // 居中模式（时钟）：说明文字紧贴时间下方；
        // 普通卡片：m.Sub 作为大数值下面的副行（例如网络上行、磁盘写入速率）。
        string? subLine = m.Centered ? m.Footer : m.Sub;
        if (SubText.Text != subLine) SubText.Text = subLine ?? "";
        // 极窄卡片放不下副行（只能显示一两个字）时直接隐藏，宁可少信息也不要溢出
        bool subFits = ActualWidth >= 210 && ActualHeight > 104;
        SubText.Visibility = string.IsNullOrEmpty(subLine) || !subFits
            ? Visibility.Collapsed
            : Visibility.Visible;

        var wantFont = string.IsNullOrWhiteSpace(m.ValueFont)
            ? _defaultValueFont
            : new FontFamily(m.ValueFont);
        if (!Equals(ValueText.FontFamily.Source, wantFont.Source))
        {
            ValueText.FontFamily = wantFont;
            if (m.Centered) FitValue();
        }

        // 只有颜色真的变了才写依赖属性：DP 赋值会触发失效与重绘，
        // 每秒对 17 张卡重复写同样的值，纯属白烧 CPU。
        var valueBrush = Palette.Brush(m.Severity switch
        {
            Severity.Critical => _theme.CritText,
            Severity.Warn => _theme.WarnText,
            _ => _theme.TextPrimary
        });
        if (!ReferenceEquals(ValueText.Foreground, valueBrush)) ValueText.Foreground = valueBrush;

        var dotBrush = Palette.Brush(m.Severity switch
        {
            Severity.Critical => _theme.CritText,
            Severity.Warn => _theme.WarnText,
            _ => m.Accent
        });
        if (!ReferenceEquals(Dot.Fill, dotBrush)) Dot.Fill = dotBrush;

        _lastBorder = m.Severity switch
        {
            Severity.Critical => Color.FromArgb(0x99, _theme.CritText.R, _theme.CritText.G, _theme.CritText.B),
            Severity.Warn => Color.FromArgb(0x80, _theme.WarnText.R, _theme.WarnText.G, _theme.WarnText.B),
            _ => _theme.Border
        };
        var borderBrush = Palette.Brush(_dragging
            ? Color.FromArgb(0xCC, 0x7D, 0xD3, 0xFC)
            : _lastBorder);
        if (!ReferenceEquals(Shell.BorderBrush, borderBrush)) Shell.BorderBrush = borderBrush;

        // 曲线 / 进度条
        bool showSpark = m.Spark != null && m.Spark.Length > 2 && ActualHeight > 108;
        Spark.Visibility = showSpark ? Visibility.Visible : Visibility.Collapsed;

        bool showBar = !showSpark && m.Progress >= 0;
        BarTrack.Visibility = showBar ? Visibility.Visible : Visibility.Collapsed;
        if (showSpark)
            Spark.SetData(m.Spark, m.Accent, m.SparkMax, m.SparkLoss);

        if (_centered != m.Centered)
        {
            _centered = m.Centered;
            UpdateTypography();
        }
        _centered = m.Centered;

        // 居中模式（时钟）：标题 + 时间 + 日期合成一块整体垂直居中，
        // 否则标题贴顶、时间居中，中间会空出一条。
        // 行定义属于 ContentRoot（带内边距的那层），不是外层 RootGrid
        if (m.Centered)
        {
            ContentRoot.VerticalAlignment = VerticalAlignment.Center;
            ContentRoot.RowDefinitions[1].Height = GridLength.Auto;
            ContentRoot.RowDefinitions[2].Height = new GridLength(0);
            ContentRoot.RowDefinitions[3].Height = new GridLength(0);
        }
        else
        {
            ContentRoot.VerticalAlignment = VerticalAlignment.Stretch;
            ContentRoot.RowDefinitions[1].Height = new GridLength(1, GridUnitType.Star);
            ContentRoot.RowDefinitions[2].Height = GridLength.Auto;
            ContentRoot.RowDefinitions[3].Height = GridLength.Auto;
        }

        // 时钟卡不显示“时间”这个小标题：内容只有时间和日期，
        // 多一行标题会把整块往下推，上方就空出一条。
        TitleRow.Visibility = m.Centered ? Visibility.Collapsed : Visibility.Visible;
        TitleRow.HorizontalAlignment = HorizontalAlignment.Left;
        CenterStack.HorizontalAlignment = m.Centered ? HorizontalAlignment.Center : HorizontalAlignment.Stretch;
        // 非居中卡片一律顶部对齐：曲线/进度条/副行的有无都不再影响大数值的垂直位置
        CenterStack.VerticalAlignment = m.Centered ? VerticalAlignment.Center : VerticalAlignment.Top;
        CenterStack.Margin = m.Centered
            ? new Thickness(0)
            : new Thickness(0, Math.Clamp(ActualHeight * 0.045, 2, 8), 0, 0);
        ValueRow.HorizontalAlignment = m.Centered ? HorizontalAlignment.Center : HorizontalAlignment.Left;
        SubText.HorizontalAlignment = m.Centered ? HorizontalAlignment.Center : HorizontalAlignment.Left;
        SubText.TextAlignment = m.Centered ? TextAlignment.Center : TextAlignment.Left;
        FooterText.HorizontalAlignment = m.FooterRight ? HorizontalAlignment.Right : HorizontalAlignment.Left;

        // 居中模式：说明文字收到数值下方，避免大卡片里两行各自贴边、显得空
        // 热搜卡片把底部说明省掉，五行列表才放得下
        bool hotList = m.Hots is { Count: > 0 };
        FooterText.Visibility = (m.Centered || showDays || hotList || ActualHeight <= 96)
            ? Visibility.Collapsed
            : Visibility.Visible;
        RenderClockChars(m.Value, m.Centered);

        if (showBar)
        {
            _progress = Math.Clamp(m.Progress, 0, 1);
            BarFill.Background = Palette.Brush(m.Severity switch
            {
                Severity.Critical => _theme.CritText,
                Severity.Warn => _theme.WarnText,
                _ => m.Accent
            });
            ApplyProgress();
        }
    }

    private void RenderRows(List<(string Left, string Right)> rows)
    {
        while (_rows.Count < rows.Count) EnsureRow();

        double rowFont = Math.Clamp(ActualHeight * 0.058, 9, 15);
        for (int i = 0; i < _rows.Count; i++)
        {
            var (root, left, right) = _rows[i];
            if (i >= rows.Count)
            {
                root.Visibility = Visibility.Collapsed;
                continue;
            }
            root.Visibility = Visibility.Visible;
            root.Tag = null;
            root.Cursor = null;
            left.Text = rows[i].Left;
            right.Text = rows[i].Right;
            left.FontSize = rowFont;
            right.FontSize = rowFont;
            if (!_rowsThemed)
            {
                left.Foreground = Palette.Brush(_theme.TextPrimary);
                right.Foreground = Palette.Brush(_theme.TextSecondary);
            }
        }
        _rowsThemed = true;
    }

    /// <summary>热搜列表：序号 + 标题 + 来源；整行可点击跳转，AI 条目高亮并单独标注。</summary>
    private void RenderHotRows(List<PerfDeck.Core.HotItem> items)
    {
        while (_rows.Count < items.Count) EnsureRow();

        double rowFont = Math.Clamp(ActualHeight * 0.072, 10.5, 15);
        for (int i = 0; i < _rows.Count; i++)
        {
            var (root, left, right) = _rows[i];
            if (i >= items.Count)
            {
                root.Visibility = Visibility.Collapsed;
                continue;
            }

            var it = items[i];
            root.Visibility = Visibility.Visible;
            root.Tag = it.Url;
            root.Cursor = System.Windows.Input.Cursors.Hand;

            left.Text = $"{it.Rank}. {it.Title}";
            right.Text = it.IsAi && !string.Equals(it.Source, "AI", StringComparison.OrdinalIgnoreCase)
                ? "AI·" + it.Source
                : it.Source;
            left.FontSize = rowFont;
            right.FontSize = Math.Max(9.5, rowFont - 1.5);
            left.Foreground = Palette.Brush(_theme.TextPrimary);   // 缓存画刷，重复赋值不产生新对象
            var rfg = Palette.Brush(it.IsAi ? _theme.WarnText : _theme.TextTertiary);
            if (!ReferenceEquals(right.Foreground, rfg)) right.Foreground = rfg;
        }
    }

    /// <summary>热搜分类标签：点一下就切换分类，不用右键。</summary>
    private void RenderHotTabs(CardModel m)
    {
        if (m.HotTabs == null || m.HotTabs.Count == 0)
        {
            HotTabs.Visibility = Visibility.Collapsed;
            return;
        }

        HotTabs.Visibility = Visibility.Visible;
        HotTabs.Children.Clear();

        double font = Math.Clamp(ActualHeight * 0.048, 8.5, 12);
        foreach (var (key, name) in m.HotTabs)
        {
            bool on = string.Equals(key, m.HotActive, StringComparison.OrdinalIgnoreCase);
            var tb = new TextBlock
            {
                Text = name,
                FontSize = font,
                Padding = new Thickness(5, 1.5, 5, 1.5),
                Margin = new Thickness(0, 0, 4, 3),
                TextTrimming = TextTrimming.CharacterEllipsis
            };

            var box = new Border
            {
                Child = tb,
                CornerRadius = new CornerRadius(4),
                Margin = new Thickness(0, 0, 3, 3),
                Background = Palette.Brush(on
                    ? Color.FromArgb(0x38, m.Accent.R, m.Accent.G, m.Accent.B)
                    : Colors.Transparent),
                Cursor = System.Windows.Input.Cursors.Hand,
                Tag = key
            };
            tb.Foreground = Palette.Brush(on ? _theme.TextPrimary : _theme.TextTertiary);
            tb.FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal;

            string k = key;
            box.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                if (!on) m.HotTabSwitch?.Invoke(k);
            };
            HotTabs.Children.Add(box);
        }
    }

    private void EnsureRow()
    {
        var g = new Grid { Margin = new Thickness(0, 2, 0, 2) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var left = new TextBlock
        {
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center
        };
        var right = new TextBlock
        {
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        right.Typography.NumeralAlignment = FontNumeralAlignment.Tabular;
        Grid.SetColumn(left, 0);
        Grid.SetColumn(right, 1);
        g.Children.Add(left);
        g.Children.Add(right);

        // 透明背景才能命中测试到整行（点击由卡片的 Preview 统一处理）
        g.Background = Brushes.Transparent;

        ItemList.Children.Add(g);
        _rows.Add((g, left, right));
    }

    private static void OpenLink(string url)
    {
        try
        {
            Log.Info("打开热搜链接：" + url);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url)
            {
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            Log.Warn("打开链接失败：" + ex.Message);
        }
    }

    private void ApplyProgress()
    {
        if (_progress < 0 || BarTrack.ActualWidth <= 0) return;
        BarFill.Width = Math.Max(0, Math.Min(BarTrack.ActualWidth, BarTrack.ActualWidth * _progress));
    }
}
