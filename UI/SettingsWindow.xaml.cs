using System;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using System.Windows.Media;
using Microsoft.Win32;
using PerfDeck.Core;

namespace PerfDeck.UI;

public partial class SettingsWindow : Window
{
    private readonly AppConfig _cfg;
    private readonly MainWindow _main;
    private int _refreshMs;
    private double _opacity;
    private double _blur;
    private double _dim;
    private double _cardAlpha;
    private int _procCount;
    private string _weatherMode = "auto";
    private double _cardH = 168;
    private int _throttleMs = 5000;
    private int _hotCount = 5;
    private bool _loading = true;
    private DispatcherTimer? _applyTimer;

    public SettingsWindow(AppConfig cfg, MainWindow main)
    {
        InitializeComponent();
        _cfg = cfg;
        _main = main;
        Topmost = true;

        BtnClose.Click += (_, _) => Close();
        Closed += (_, _) => { _applyTimer?.Stop(); _loading = true; };
        ChkUniform.Checked += Uniform_Toggled;
        ChkUniform.Unchecked += Uniform_Toggled;
        ChkAutoHideBar.Checked += Chrome_Toggled;
        ChkAutoHideBar.Unchecked += Chrome_Toggled;
        ChkShadow.Checked += Shadow_Toggled;
        ChkShadow.Unchecked += Shadow_Toggled;
        ChkAdaptiveThrottle.Checked += Throttle_Toggled;
        ChkAdaptiveThrottle.Unchecked += Throttle_Toggled;

        _loading = true;
        LoadFromConfig();
        _loading = false;
    }

    private void LoadFromConfig()
    {
        _refreshMs = _cfg.RefreshMs;
        SldInterval.Value = Math.Clamp(_refreshMs / 1000.0, 0.5, 10);
        LblInterval.Text = $"{_refreshMs / 1000.0:0.0} 秒";

        TxtTimeFormat.Text = _cfg.TimeFormat;
        TxtDateFormat.Text = _cfg.DateFormat;
        ChkShowDate.IsChecked = _cfg.ShowDate;
        UpdatePreview();

        TxtPingHost.Text = _cfg.PingHost;
        TxtTimeout.Text = _cfg.PingTimeoutMs.ToString();

        _opacity = _cfg.Opacity;
        SldOpacity.Value = _opacity;
        LblOpacity.Text = $"{_opacity * 100:0}%";

        BuildThemePills();
        BuildPerfPills();
        BuildClockFontPills();
        _blur = _cfg.WallpaperBlur;
        _dim = _cfg.WallpaperDim;
        _cardAlpha = _cfg.CardAlpha;
        _procCount = _cfg.ProcCount;
        SldBlur.Value = _blur;
        SldDim.Value = _dim;
        SldCardAlpha.Value = _cardAlpha;
        LblBlur.Text = $"{_blur:0}";
        LblDim.Text = $"{_dim * 100:0}%";
        LblCardAlpha.Text = $"{_cardAlpha * 100:0}%";
        SldProc.Value = Math.Clamp(_procCount, 3, 8);
        LblProc.Text = $"{_procCount} 条";

        ChkUniform.IsChecked = _cfg.UniformCards;
        ChkAutoHideBar.IsChecked = _cfg.AutoHideToolbar;
        ChkShadow.IsChecked = _cfg.CardShadow;

        ChkAdaptiveThrottle.IsChecked = _cfg.AdaptiveThrottle;
        _throttleMs = _cfg.ThrottleRefreshMs;
        SldThrottle.Value = Math.Clamp(_throttleMs / 1000.0, 1, 15);
        LblThrottle.Text = $"{_throttleMs / 1000.0:0} 秒";

        _hotCount = _cfg.HotCount;
        SldHotCount.Value = Math.Clamp(_hotCount, 3, 10);
        LblHotCount.Text = $"{_hotCount} 条";
        _cardH = _cfg.UniformCardH;
        SldCardH.Value = Math.Clamp(_cardH, 130, 300);
        LblCardH.Text = $"{_cardH:0}";

        _weatherMode = _cfg.WeatherMode;
        SelectPill(WeatherModePanel, _weatherMode);
        TxtWeatherCity.Text = _cfg.WeatherCity ?? "";
        LblWeatherStatus.Text = "状态：" + _main.WeatherStatusText;
        UpdateWallLabel();

        ChkTopmost.IsChecked = _cfg.AlwaysOnTop;
        ChkTaskbar.IsChecked = _cfg.ShowInTaskbar;
        ChkSpark.IsChecked = _cfg.ShowSparkline;
        ChkClickThrough.IsChecked = _cfg.ClickThrough;
        ChkAutoStart.IsChecked = AutoStart.IsEnabled();
        ChkElevate.IsChecked = _cfg.AutoElevate;
        ChkApi.IsChecked = _cfg.ApiEnabled;
        TxtApiPort.Text = _cfg.ApiPort.ToString();

        var th = _cfg.Thresholds;
        TxtLoadWarn.Text = th.LoadWarn.ToString("0");
        TxtLoadCrit.Text = th.LoadCrit.ToString("0");
        TxtTempWarn.Text = th.TempWarn.ToString("0");
        TxtTempCrit.Text = th.TempCrit.ToString("0");
        TxtDiskWarn.Text = th.DiskTempWarn.ToString("0");
        TxtDiskCrit.Text = th.DiskTempCrit.ToString("0");
        TxtLatWarn.Text = th.LatencyWarn.ToString("0");
        TxtLatCrit.Text = th.LatencyCrit.ToString("0");
        TxtLossWarn.Text = th.LossWarn.ToString("0.#");
        TxtLossCrit.Text = th.LossCrit.ToString("0.#");
    }

    private static void SelectPill(Panel panel, string tag)
    {
        foreach (var tb in panel.Children.OfType<ToggleButton>())
        {
            bool match = string.Equals(tb.Tag?.ToString(), tag, StringComparison.OrdinalIgnoreCase);
            if (match && tag.Contains('.'))
            {
                match = double.TryParse(tag, NumberStyles.Any, CultureInfo.InvariantCulture, out var v)
                        && double.TryParse(tb.Tag?.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var pv)
                        && Math.Abs(v - pv) < 0.001;
            }
            tb.IsChecked = match;
        }
    }



    // ---------- 滑块（外观参数即时预览） ----------

    private void Opacity_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_cfg == null) return;
        _opacity = e.NewValue;
        if (LblOpacity != null) LblOpacity.Text = $"{_opacity * 100:0}%";
        QueueApply();
    }

    private void Blur_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_cfg == null) return;
        _blur = e.NewValue;
        if (LblBlur != null) LblBlur.Text = $"{_blur:0}";
        QueueApply();
    }

    private void Dim_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_cfg == null) return;
        _dim = e.NewValue;
        if (LblDim != null) LblDim.Text = $"{_dim * 100:0}%";
        QueueApply();
    }

    private void CardAlpha_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_cfg == null) return;
        _cardAlpha = e.NewValue;
        if (LblCardAlpha != null) LblCardAlpha.Text = $"{_cardAlpha * 100:0}%";
        QueueApply();
    }

    private void Interval_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_cfg == null) return;
        _refreshMs = (int)Math.Round(e.NewValue * 1000);
        if (LblInterval != null) LblInterval.Text = $"{e.NewValue:0.0} 秒";
        QueueApply();
    }

    private void Shadow_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _cfg.CardShadow = ChkShadow.IsChecked == true;
        _main.ApplyConfig();
        _cfg.Save();
    }

    private void Chrome_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _cfg.AutoHideToolbar = ChkAutoHideBar.IsChecked == true;
        _cfg.CardShadow = ChkShadow.IsChecked == true;
        _main.ApplyConfig();
        _cfg.Save();
    }

    private void Uniform_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _cfg.UniformCards = ChkUniform.IsChecked == true;
        _cfg.UniformCardH = _cardH;
        _main.ApplyConfig();
        _cfg.Save();
    }

    private void Throttle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _cfg.AdaptiveThrottle = ChkAdaptiveThrottle.IsChecked == true;
        _cfg.ThrottleRefreshMs = _throttleMs;
        _cfg.Save();
    }

    private void Throttle_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_cfg == null) return;
        _throttleMs = (int)Math.Round(e.NewValue * 1000);
        if (LblThrottle != null) LblThrottle.Text = $"{e.NewValue:0} 秒";
        if (_loading) return;
        _cfg.ThrottleRefreshMs = _throttleMs;
        _cfg.Save();
    }

    private void HotCount_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_cfg == null) return;
        _hotCount = (int)Math.Round(e.NewValue);
        if (LblHotCount != null) LblHotCount.Text = $"{_hotCount} 条";
        if (_loading) return;
        _cfg.HotCount = _hotCount;
        _cfg.Save();
    }

    private void CardH_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading || _cfg == null) return;
        _cardH = e.NewValue;
        if (LblCardH != null) LblCardH.Text = $"{_cardH:0}";
        _cfg.UniformCards = ChkUniform?.IsChecked == true;
        QueueApply();
    }

    private void Proc_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_cfg == null) return;
        _procCount = (int)Math.Round(e.NewValue);
        if (LblProc != null) LblProc.Text = $"{_procCount} 条";
        QueueApply();
    }

    /// <summary>拖动过程中先只更新数字，停手 260ms 再真正刷新面板，避免重复重载壁纸。</summary>
    private void QueueApply()
    {
        if (_loading) return;

        if (_applyTimer == null)
        {
            _applyTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(260) };
            _applyTimer.Tick += (_, _) =>
            {
                _applyTimer!.Stop();
                if (_loading || _cfg == null || _main == null) return;
                _cfg.Opacity = Math.Clamp(_opacity, 0.3, 1);
                _cfg.AutoHideToolbar = ChkAutoHideBar.IsChecked == true;
        _cfg.CardShadow = ChkShadow.IsChecked == true;
        _cfg.WallpaperBlur = Math.Clamp(_blur, 0, 40);
                _cfg.WallpaperDim = Math.Clamp(_dim, 0, 0.92);
                _cfg.CardAlpha = Math.Clamp(_cardAlpha, 0.35, 1);
                _cfg.RefreshMs = Math.Clamp(_refreshMs, 500, 10000);
                _cfg.ProcCount = Math.Clamp(_procCount, 3, 8);
                _cfg.UniformCards = ChkUniform.IsChecked == true;
                _cfg.UniformCardH = Math.Clamp(_cardH, 130, 300);
                _cfg.AdaptiveThrottle = ChkAdaptiveThrottle.IsChecked == true;
                _cfg.ThrottleRefreshMs = Math.Clamp(_throttleMs, 1000, 15000);
                _cfg.HotCount = Math.Clamp(_hotCount, 3, 10);
        UpdatePerfHint();
                _main.ApplyConfig();
                _cfg.Save();
            };
        }

        _applyTimer.Stop();
        _applyTimer.Start();
    }

    // ---------- 天气 ----------

    private void WeatherMode_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton tb) return;
        foreach (var c in WeatherModePanel.Children.OfType<ToggleButton>())
            c.IsChecked = ReferenceEquals(c, tb);
        tb.IsChecked = true;
        _weatherMode = tb.Tag?.ToString() ?? "auto";
        _cfg.WeatherMode = _weatherMode;
        _cfg.Save();
        _main.ApplyConfig();     // 首次切到自动定位时会在这里弹权限询问
        LblWeatherStatus.Text = "状态：" + _main.WeatherStatusText;
    }

    private void WeatherRefresh_Click(object sender, RoutedEventArgs e)
    {
        _cfg.WeatherCity = TxtWeatherCity.Text.Trim();
        _cfg.WeatherMode = _weatherMode;
        _cfg.Save();
        _main.ApplyConfig();
        LblWeatherStatus.Text = "状态：" + _main.WeatherStatusText;
    }

    private void WeatherReask_Click(object sender, RoutedEventArgs e)
    {
        _cfg.WeatherGeoConsent = null;
        _cfg.WeatherMode = "auto";
        _weatherMode = "auto";
        SelectPill(WeatherModePanel, "auto");
        _cfg.Save();
        _main.ApplyConfig();
        LblWeatherStatus.Text = "状态：" + _main.WeatherStatusText;
    }

    // ---------- 主题 ----------


    private void BuildThemePills()
    {
        ThemePanel.Children.Clear();
        foreach (var t in Themes.All)
        {
            var tb = new ToggleButton
            {
                Content = t.Name,
                Tag = t.Id,
                Style = (Style)FindResource("Pill"),
                IsChecked = string.Equals(t.Id, _cfg.Theme, StringComparison.OrdinalIgnoreCase)
            };
            var id = t.Id;
            tb.Click += (_, _) =>
            {
                foreach (var c in ThemePanel.Children.OfType<ToggleButton>())
                    c.IsChecked = ReferenceEquals(c, tb);
                tb.IsChecked = true;
                _cfg.Theme = id;
                _main.ApplyConfig();
                _cfg.Save();
            };
            ThemePanel.Children.Add(tb);
        }
    }

    // ---------- 性能挡位 ----------

    private void BuildPerfPills()
    {
        PerfPanel.Children.Clear();
        string cur = PerfProfiles.Detect(_cfg) ?? _cfg.PerfProfile;

        foreach (var spec in PerfProfiles.All)
        {
            var tb = new ToggleButton
            {
                Content = spec.Name,
                Tag = spec.Key,
                Style = (Style)FindResource("Pill"),
                IsChecked = string.Equals(spec.Key, cur, StringComparison.OrdinalIgnoreCase)
            };

            PerfProfiles.Spec captured = spec;
            tb.Click += (_, _) =>
            {
                foreach (var c in PerfPanel.Children.OfType<ToggleButton>())
                    c.IsChecked = ReferenceEquals(c, tb);
                tb.IsChecked = true;

                PerfProfiles.Apply(_cfg, captured.Key);
                SyncFromConfig();

                _main.ApplyConfig();
                _cfg.Save();
            };

            PerfPanel.Children.Add(tb);
        }

        UpdatePerfHint();
    }

    private void UpdatePerfHint()
    {
        string cur = PerfProfiles.Detect(_cfg) ?? _cfg.PerfProfile;
        var spec = PerfProfiles.Find(cur);
        LblPerfDesc.Text = spec.Desc;
        LblPerfMem.Text = spec.MemHint + "（实测会随卡片数量浮动）";
    }

    /// <summary>挡位切换后把其它控件的显示同步过来。</summary>
    private void SyncFromConfig()
    {
        _refreshMs = _cfg.RefreshMs;
        SldInterval.Value = Math.Clamp(_refreshMs / 1000.0, 0.5, 10);
        LblInterval.Text = $"{_refreshMs / 1000.0:0.0} 秒";

        _procCount = _cfg.ProcCount;
        SldProc.Value = Math.Clamp(_procCount, 3, 8);
        LblProc.Text = $"{_procCount} 条";

        _hotCount = _cfg.HotCount;
        SldHotCount.Value = Math.Clamp(_hotCount, 3, 10);
        LblHotCount.Text = $"{_hotCount} 条";

        ChkSpark.IsChecked = _cfg.ShowSparkline;

        // 手动调过单项后，挡位就变成“自定义”
        _loading = true;
        foreach (var c in PerfPanel.Children.OfType<ToggleButton>())
            c.IsChecked = string.Equals(c.Tag?.ToString(), PerfProfiles.Detect(_cfg),
                StringComparison.OrdinalIgnoreCase);
        _loading = false;

        UpdatePerfHint();
    }

    // ---------- 时间字体 ----------

    private static readonly (string Label, string Font)[] ClockFonts =
    {
        ("默认", ""),
        ("等宽数码", "Consolas"),
        ("细体", "Segoe UI Light"),
        ("雅黑 Light", "Microsoft YaHei UI Light"),
        ("衬线", "Georgia"),
        ("圆润", "Microsoft YaHei UI")
    };

    private void BuildClockFontPills()
    {
        ClockFontPanel.Children.Clear();
        string current = _cfg.ClockFont ?? "";

        foreach (var (label, font) in ClockFonts)
        {
            var tb = new ToggleButton
            {
                Content = label,
                Tag = font,
                Style = (Style)FindResource("Pill"),
                IsChecked = string.Equals(current, font, StringComparison.OrdinalIgnoreCase)
            };
            if (!string.IsNullOrEmpty(font))
                tb.FontFamily = new FontFamily(font);

            string selected = font;
            tb.Click += (_, _) =>
            {
                foreach (var c in ClockFontPanel.Children.OfType<ToggleButton>())
                    c.IsChecked = ReferenceEquals(c, tb);
                tb.IsChecked = true;
                _cfg.ClockFont = string.IsNullOrEmpty(selected) ? null : selected;
                _main.ApplyConfig();
                _cfg.Save();
            };
            ClockFontPanel.Children.Add(tb);
        }
    }

    // ---------- 壁纸 ----------

    private void UpdateWallLabel()
    {
        LblWall.Text = string.IsNullOrWhiteSpace(_cfg.Wallpaper)
            ? "未设置"
            : System.IO.Path.GetFileName(_cfg.Wallpaper);
    }

    private void Wallpaper_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "选择壁纸图片",
            Filter = "图片文件|*.png;*.jpg;*.jpeg;*.bmp;*.webp;*.gif|所有文件|*.*",
            CheckFileExists = true
        };
        if (dlg.ShowDialog(this) != true) return;

        _cfg.Wallpaper = dlg.FileName;
        if (_cfg.CardAlpha >= 0.99) _cfg.CardAlpha = 0.85;   // 首次设置壁纸时自动让卡片透出背景
        _cardAlpha = _cfg.CardAlpha;
        SldCardAlpha.Value = _cardAlpha;
        LblCardAlpha.Text = $"{_cardAlpha * 100:0}%";

        _main.ApplyConfig();
        _cfg.Save();
        UpdateWallLabel();
    }

    private void WallClear_Click(object sender, RoutedEventArgs e)
    {
        _cfg.Wallpaper = null;
        _main.ApplyConfig();
        _cfg.Save();
        UpdateWallLabel();
    }

    // ---------- 交互 ----------


    private void Header_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        try { DragMove(); } catch { }
    }

    private void Format_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton tb) return;
        TxtTimeFormat.Text = tb.Tag?.ToString() ?? "HH:mm:ss";
    }

    private void TxtTimeFormat_TextChanged(object sender, TextChangedEventArgs e) => UpdatePreview();

    private void UpdatePreview()
    {
        if (LblPreview == null) return;
        string fmt = TxtTimeFormat.Text;
        string preview;
        try { preview = DateTime.Now.ToString(fmt); }
        catch (FormatException) { preview = "⚠ 格式无效，将回退为 HH:mm:ss"; }
        catch { preview = "⚠ 格式无效"; }
        LblPreview.Text = "预览：" + preview;
    }

    private static double Num(TextBox t, double dflt)
        => double.TryParse(t.Text.Trim(), out var v) && v >= 0 ? v : dflt;

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        _cfg.RefreshMs = Math.Clamp(_refreshMs, 300, 60000);

        string fmt = string.IsNullOrWhiteSpace(TxtTimeFormat.Text) ? "HH:mm:ss" : TxtTimeFormat.Text.Trim();
        try { _ = DateTime.Now.ToString(fmt); }
        catch { fmt = "HH:mm:ss"; }
        _cfg.TimeFormat = fmt;

        string dfmt = string.IsNullOrWhiteSpace(TxtDateFormat.Text) ? "yyyy-MM-dd" : TxtDateFormat.Text.Trim();
        try { _ = DateTime.Now.ToString(dfmt); }
        catch { dfmt = "yyyy-MM-dd"; }
        _cfg.DateFormat = dfmt;

        _cfg.ShowDate = ChkShowDate.IsChecked == true;

        string host = TxtPingHost.Text.Trim();
        _cfg.PingHost = string.IsNullOrWhiteSpace(host) ? "223.5.5.5" : host;
        _cfg.PingTimeoutMs = (int)Math.Clamp(Num(TxtTimeout, 1200), 200, 5000);

        _cfg.Opacity = Math.Clamp(_opacity, 0.3, 1);
        _cfg.WallpaperBlur = Math.Clamp(_blur, 0, 60);
        _cfg.WallpaperDim = Math.Clamp(_dim, 0, 0.92);
        _cfg.CardAlpha = Math.Clamp(_cardAlpha, 0.35, 1);
        _cfg.ProcCount = Math.Clamp(_procCount, 3, 10);
        _cfg.WeatherMode = _weatherMode;
        _cfg.WeatherCity = string.IsNullOrWhiteSpace(TxtWeatherCity.Text) ? null : TxtWeatherCity.Text.Trim();
        _cfg.AlwaysOnTop = ChkTopmost.IsChecked == true;
        _cfg.ShowInTaskbar = ChkTaskbar.IsChecked == true;
        _cfg.ShowSparkline = ChkSpark.IsChecked == true;
        _cfg.ClickThrough = ChkClickThrough.IsChecked == true;
        _cfg.AutoElevate = ChkElevate.IsChecked == true;
        _cfg.ApiEnabled = ChkApi.IsChecked == true;
        _cfg.ApiPort = (int)Math.Clamp(Num(TxtApiPort, 17821), 0, 65535);

        var th = _cfg.Thresholds;
        th.LoadWarn = Num(TxtLoadWarn, 85);
        th.LoadCrit = Num(TxtLoadCrit, 95);
        th.TempWarn = Num(TxtTempWarn, 80);
        th.TempCrit = Num(TxtTempCrit, 90);
        th.DiskTempWarn = Num(TxtDiskWarn, 50);
        th.DiskTempCrit = Num(TxtDiskCrit, 60);
        th.LatencyWarn = Num(TxtLatWarn, 100);
        th.LatencyCrit = Num(TxtLatCrit, 250);
        th.LossWarn = Num(TxtLossWarn, 2);
        th.LossCrit = Num(TxtLossCrit, 10);

        bool autoStart = ChkAutoStart.IsChecked == true;
        if (autoStart != AutoStart.IsEnabled()) AutoStart.Apply(autoStart);
        _cfg.AutoStart = autoStart;

        _cfg.Save();
        _main.ApplyConfig();
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();
}
