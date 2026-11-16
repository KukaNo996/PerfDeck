using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PerfDeck.Core;

namespace PerfDeck.UI;

public partial class MainWindow : Window
{
    private const double Pad = 18;
    private const double Gap = 12;
    /// <summary>内容区最大宽度（DIP）。超过就居中，不再把卡片拉宽。</summary>
    private const double MaxContentWidth = 1760;
    /// <summary>内容区最小宽度（DIP）。极窄屏下也保证至少能放两列。</summary>
    private const double MinContentWidth = 560;
    private const int HotkeyClickThrough = 0xA001;
    private const int HotkeySettings = 0xA002;
    private const int HotkeyLock = 0xA003;
    private const int HotkeyMaximize = 0xA004;

    private readonly AppConfig _cfg;
    private readonly SensorService _sensors = new();
    private readonly NetMonitor _net;
    private readonly ProcessSampler _sampler = new();
    private WeatherService _weather = null!;
    private HotSearchService _hot = null!;
    private readonly GpuUsageSampler _gpuUsage = new();
    private DateTime _lastGpuSample = DateTime.MinValue;
    private bool _throttled;
    private string _weatherKey = "";
    private ThemeSpec _theme = Themes.All[0];
    private List<DisplayInfo> _displays = new();
    private bool _maximized;
    private bool _topmostBeforeMax = true;
    private string? _wallpaperLoaded;
    private BitmapImage? _wallpaperBitmap;
    private volatile bool _needProcs;
    private DateTime _lastProcSample = DateTime.MinValue;
    private List<ProcSnap> _cachedProcs = new();
    private int _cachedProcCount;
    private Rect _restoreBounds;
    private readonly List<CardHost> _cards = new();
    private readonly Dictionary<string, Ring> _rings = new();
    private Snapshot? _snap;
    /// <summary>对外数据接口（只读、仅回环）</summary>
    private Core.ApiServer? _api;
    private CancellationTokenSource? _cts;
    private DispatcherTimer _clockTimer = null!;
    private DateTime _lastTrim = DateTime.MinValue;
    private IntPtr _hwnd;
    private CardHost? _dragCard;
    private CardHost? _pendingCard;      // 已按下、还在等长按判定的卡片
    private Point _pendingStart;
    private DateTime _pendingAt;
    private Point _dragStart;
    private Point _dragOrig;
    private double _probeX, _probeY;
    private int _probeInsert = -1;
    private DispatcherTimer? _dragTimer;
    private bool _initialized;
    private SettingsWindow? _settings;
    private bool _lastUniform = true;
    private DispatcherTimer? _toolbarTimer;
    private DispatcherTimer? _toolbarPoll;
    private bool _toolbarShown;
    private DateTime _toolbarSuppressUntil = DateTime.MinValue;
    private double _lastUniformH = 168;

    /// <summary>
    /// 环形历史缓冲。Snapshot 复用同一块输出数组：
    /// 每秒每张卡都分配一个新 double[48] 纯属浪费，界面只在同一帧内读一次快照。
    /// </summary>
    private sealed class Ring
    {
        private readonly double[] _b;
        private readonly double[] _out;
        private int _i;

        public Ring(int n)
        {
            _b = new double[n];
            _out = new double[n];
            Array.Fill(_b, double.NaN);
        }

        public void Add(double v) { _b[_i] = v; _i = (_i + 1) % _b.Length; }

        public double[] Snapshot()
        {
            for (int i = 0; i < _out.Length; i++) _out[i] = _b[(_i + i) % _b.Length];
            return _out;
        }
    }

    public MainWindow(AppConfig cfg)
    {
        InitializeComponent();
        _cfg = cfg;
        _net = new NetMonitor(cfg);
        _weather = new WeatherService(cfg);
        _hot = new HotSearchService(cfg);

        ApplyChrome();
        HookChrome();
        HookDeck();

        Loaded += OnLoaded;
        Closing += OnClosing;
    }

    // ================= 启动 =================

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        PlaceWindow();
        ApplyThemeAndWallpaper();

        WallLayer.SizeChanged += (_, _) => UpdateWallClip();
        LocationChanged += (_, _) => { if (_maximized) return; };

        // 窗口尺寸变了必须重排：卡片槽位是按上次视口宽高算出来的，
        // 不重排的话缩小窗口后卡片会顶出边界（窄屏/带鱼屏切换时最明显）。
        SizeChanged += (_, e) =>
        {
            if (!_initialized || _cards.Count == 0) return;
            if (Math.Abs(e.NewSize.Width - e.PreviousSize.Width) < 0.5 &&
                Math.Abs(e.NewSize.Height - e.PreviousSize.Height) < 0.5) return;

            // 延后一帧：SizeChanged 触发时 ScrollViewer 还没完成新的排列，
            // 立刻重排会拿到旧视口宽度。
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (!_initialized || _cards.Count == 0) return;
                Relayout(false);
            }), DispatcherPriority.Loaded);
        };

        _clockTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(500)
        };
        _clockTimer.Tick += (_, _) => UpdateClockCards();
        _clockTimer.Start();

        InitToolbarAutoHide();

        // 点完任何工具栏按钮都把工具栏收起来（鼠标此刻还压在热区里）
        CtrlBar.PreviewMouseLeftButtonUp += (_, _) =>
            Dispatcher.BeginInvoke(new Action(() => HideToolbarNow()), DispatcherPriority.Background);

        _ = InitAsync();
    }

    private async Task InitAsync()
    {
        await Task.Run(() => _sensors.Initialize());

        RepairCardWidths();

        if (_cfg.Cards.Count == 0) BuildDefaultCards();
        else if (_cfg.ConfigVersion < 5)
        {
            // v5：新增热搜卡片；统一尺寸成为默认，同时清掉历史被拉伸污染的宽高
            _cfg.UniformCards = true;
            _cfg.Cards.RemoveAll(c => c.Kind == CardKind.HotSearch);
            EnsureNewCards();
            RepairCardWidths(force: true);
        }
        _cfg.ConfigVersion = 5;
        _lastUniform = _cfg.UniformCards;
        _lastUniformH = _cfg.UniformCardH;
        RebuildCards();
        Relayout(false);
        if (_cfg.WinW <= 0) FitHeightToContent();
        _initialized = true;
        EmptyHint.Visibility = Visibility.Collapsed;

        StartLoops();
        StartApi();
        EnsureWeather();
        _hot.Start();
        Native.TrimWorkingSet();

        if (Environment.GetCommandLineArgs().Any(a => a.Equals("--memreport", StringComparison.OrdinalIgnoreCase)))
            _ = RunMemReport();
    }

    private void StartLoops()
    {
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        _net.Start();

        _ = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    var snap = _sensors.Sample();
                    snap.Net = _net.Latest;
                    // 进程排行变化很慢，没必要跟着 1 秒刷新走：
                    // 每次都要枚举全部进程（200+），是最贵的一步。
                    if (_needProcs && (DateTime.Now - _lastProcSample).TotalMilliseconds >= 2800)
                    {
                        _lastProcSample = DateTime.Now;

                        // GPU Engine 计数器有三四百个实例，比进程枚举更贵，降到 6 秒一次
                        if (_gpuUsage.Available || _lastGpuSample == DateTime.MinValue)
                        {
                            if ((DateTime.Now - _lastGpuSample).TotalSeconds >= 6 || _lastGpuSample == DateTime.MinValue)
                            {
                                _lastGpuSample = DateTime.Now;
                                _gpuUsage.Sample();
                            }
                        }

                        var sort = SortFromTarget(snap.Procs.Count >= 0 ? FindProcCard()?.Target : null);
                        snap.Procs = _sampler.Sample(_cfg.ProcCount, sort, _gpuUsage.Usage);
                        snap.ProcessCount = _sampler.LastTotalCount;
                    }
                    else if (_needProcs)
                    {
                        snap.Procs = _cachedProcs;      // 复用上次结果
                        snap.ProcessCount = _cachedProcCount;
                    }

                    if (_needProcs && snap.Procs.Count > 0)
                    {
                        _cachedProcs = snap.Procs;
                        _cachedProcCount = snap.ProcessCount;
                    }
                    await Dispatcher.InvokeAsync(() => ApplySnapshot(snap));
                }
                catch (Exception ex)
                {
                    Log.Warn("采样循环异常：" + ex.Message);
                }

                if (DateTime.Now - _lastTrim > TimeSpan.FromMinutes(3))
                {
                    _lastTrim = DateTime.Now;
                    Native.TrimWorkingSet();
                }

                int delay = Math.Max(300, _cfg.RefreshMs);
                if (_cfg.AdaptiveThrottle)
                {
                    // 全屏游戏/渲染时把刷新频率降下来，避免和前台抢 CPU
                    bool busy = HeavyTask.IsBusy("PerfDeck", out string why);
                    if (busy != _throttled)
                    {
                        _throttled = busy;
                        Log.Info(busy ? $"检测到重型任务（{why}），刷新降到 {_cfg.ThrottleRefreshMs}ms"
                                      : "重型任务结束，恢复常规刷新");
                    }
                    if (busy) delay = Math.Max(delay, Math.Clamp(_cfg.ThrottleRefreshMs, 1000, 15000));
                }
                else if (_throttled)
                {
                    _throttled = false;
                }

                try { await Task.Delay(delay, token); }
                catch { break; }
            }
        }, token);
    }

    /// <summary>启动对外数据接口。失败只记日志，不影响面板功能。</summary>
    protected override void OnClosed(EventArgs e)
    {
        try { _api?.Dispose(); } catch { }
        _api = null;
        base.OnClosed(e);
    }

    private void StartApi()
    {
        try
        {
            int port = _cfg.ApiPort;
            if (!_cfg.ApiEnabled || port <= 0) return;

            _api?.Dispose();
            _api = new Core.ApiServer(port, () => CurrentSnapshot);
            _api.Start();
        }
        catch (Exception ex)
        {
            Log.Warn("对外数据接口启动异常：" + ex.Message);
        }
    }

    private void RestartLoops()
    {
        if (!_initialized) return;
        _net.Restart();
        StartLoops();
    }

    // ================= 工具栏自动隐藏 =================

    /// <summary>
    /// 右上角控制条平时不可见；鼠标进入右上角热区时淡入，离开 0.7 秒后淡出。
    /// 关闭自动隐藏则退回“半透明常显”。
    /// </summary>
    private void InitToolbarAutoHide()
    {
        // 轮询鼠标位置，而不是挂 MouseMove：
        // 工具栏透明且不可命中时事件会被下层元素吃掉，靠事件会漏判。
        if (_toolbarPoll != null)
        {
            _toolbarPoll.Stop();
            _toolbarPoll = null;
        }

        if (!_cfg.AutoHideToolbar)
        {
            _toolbarShown = true;
            FadeToolbar(0.42, 0, true);
            return;
        }

        _toolbarShown = false;
        FadeToolbar(0, 0, false);

        _toolbarPoll = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(160)
        };
        _toolbarPoll.Tick += (_, _) => PollToolbar();
        _toolbarPoll.Start();
    }

    /// <summary>
    /// 鼠标是否停在右上角热区。
    /// 用屏幕坐标判断：Mouse.GetPosition 在鼠标离开窗口后并不可靠，
    /// 会让工具栏卡在“常显”状态上不消失。
    /// </summary>
    private bool CursorInToolbarZone()
    {
        if (ActualWidth <= 10 || ActualHeight <= 10) return false;
        if (!Native.TryGetCursorPos(out int sx, out int sy)) return false;

        Point origin;
        try { origin = PointToScreen(new Point(0, 0)); }
        catch { return false; }

        var dpi = VisualTreeHelper.GetDpi(this);
        double wpx = ActualWidth * dpi.DpiScaleX;
        double hpx = ActualHeight * dpi.DpiScaleY;

        double lx = sx - origin.X;
        double ly = sy - origin.Y;

        // 鼠标在窗口外直接判否
        if (lx < 0 || ly < 0 || lx > wpx || ly > hpx) return false;

        return ly <= 64 * dpi.DpiScaleY && lx >= wpx - 280 * dpi.DpiScaleX;
    }

    private void PollToolbar()
    {
        if (!_cfg.AutoHideToolbar) return;
        if (_dragCard != null) return;                  // 拖卡片时不要弹工具栏
        if (!IsVisible || WindowState == WindowState.Minimized) return;

        if (CursorInToolbarZone()) ShowToolbar();
        else if (_toolbarShown) ScheduleToolbarHide();
    }

    private void ShowToolbar()
    {
        _toolbarTimer?.Stop();
        if (_toolbarShown) return;
        if (DateTime.Now < _toolbarSuppressUntil) return;   // 刚点完按钮，先别弹
        _toolbarShown = true;
        FadeToolbar(1, 120, true);
    }

    /// <summary>立即收起工具栏，并短暂抑制（点完按钮不该继续挂着）。</summary>
    private void HideToolbarNow(int suppressMs = 1200)
    {
        _toolbarTimer?.Stop();
        _toolbarSuppressUntil = DateTime.Now.AddMilliseconds(suppressMs);
        if (!_cfg.AutoHideToolbar) return;
        _toolbarShown = false;
        FadeToolbar(0, 160, false);
    }

    private void ScheduleToolbarHide()
    {
        if (!_cfg.AutoHideToolbar || !_toolbarShown) return;

        if (_toolbarTimer == null)
        {
            _toolbarTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(650) };
            _toolbarTimer.Tick += (_, _) =>
            {
                _toolbarTimer!.Stop();
                if (!_cfg.AutoHideToolbar) return;
                if (CursorInToolbarZone()) return;   // 鼠标又回来了

                _toolbarShown = false;
                FadeToolbar(0, 200, false);
            };
        }
        _toolbarTimer.Stop();
        _toolbarTimer.Start();
    }

    private void FadeToolbar(double target, int ms, bool hittable)
    {
        CtrlBar.BeginAnimation(OpacityProperty,
            new DoubleAnimation(target, TimeSpan.FromMilliseconds(ms))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });
        CtrlBar.IsHitTestVisible = hittable;
    }

    /// <summary>按当前 Target 重算一次进程排行并重绘该卡（不改维度）。</summary>
    private void CycleProcSortRefresh(CardConfig cc)
    {
        _lastProcSample = DateTime.MinValue;
        var snapNow = _snap;
        if (snapNow != null)
        {
            snapNow.Procs = _sampler.Sample(_cfg.ProcCount, SortFromTarget(cc.Target), _gpuUsage.Usage);
            snapNow.ProcessCount = _sampler.LastTotalCount;
            var host = _cards.FirstOrDefault(c => c.Config.Id == cc.Id);
            if (host != null) host.Apply(Build(cc, snapNow));
        }
        _cfg.Save();
    }

    // ================= 进程排序维度 =================

    private static readonly (string Key, string Name)[] ProcSorts =
    {
        ("cpu", "CPU"),
        ("mem", "内存"),
        ("igpu", "核显"),
        ("dgpu", "独显")
    };

    private CardConfig? FindProcCard() => _cfg.Cards.FirstOrDefault(c => c.Kind == CardKind.Proc);

    private static ProcSort SortFromTarget(string? target) => target switch
    {
        "mem" => ProcSort.Memory,
        "igpu" => ProcSort.GpuIntegrated,
        "dgpu" => ProcSort.GpuDiscrete,
        _ => ProcSort.Cpu
    };

    private static string SortKey(ProcSort s) => s switch
    {
        ProcSort.Memory => "mem",
        ProcSort.GpuIntegrated => "igpu",
        ProcSort.GpuDiscrete => "dgpu",
        _ => "cpu"
    };

    private static string SortName(ProcSort s) => s switch
    {
        ProcSort.Memory => "内存",
        ProcSort.GpuIntegrated => "核显",
        ProcSort.GpuDiscrete => "独显",
        _ => "CPU"
    };

    /// <summary>进程卡片排序：点角标循环切换 CPU → 内存 → 核显 → 独显。</summary>
    private void CycleProcSort(CardConfig cc)
    {
        var cur = SortFromTarget(cc.Target);
        var next = cur switch
        {
            ProcSort.Cpu => ProcSort.Memory,
            ProcSort.Memory => ProcSort.GpuIntegrated,
            ProcSort.GpuIntegrated => ProcSort.GpuDiscrete,
            _ => ProcSort.Cpu
        };

        cc.Target = SortKey(next);

        // 立刻重算一次，不等下一轮采样
        if (_needProcs)
        {
            _lastProcSample = DateTime.MinValue;
            var snapNow = _snap;
            if (snapNow != null)
            {
                snapNow.Procs = _sampler.Sample(_cfg.ProcCount, next, _gpuUsage.Usage);
                snapNow.ProcessCount = _sampler.LastTotalCount;
                var host = _cards.FirstOrDefault(c => c.Config.Id == cc.Id);
                if (host != null) host.Apply(Build(cc, snapNow));
            }
        }
        _cfg.Save();
    }

    // ================= 卡片尺寸预设 =================

    private static void tailMenu(MenuItem parent, MenuItem child) => parent.Items.Add(child);

    private static readonly (string Name, double W, double H)[] CardPresets =
    {
        ("小", 200, 130),
        ("标准", 250, 152),
        ("大", 330, 200),
        ("宽屏", 520, 152),
        ("高条", 250, 230)
    };

    /// <summary>套用预设尺寸。单卡自定义尺寸与“自动适配”互斥，因此会自动切到自由布局。</summary>
    private void ApplyCardPreset(CardConfig cc, double w, double h)
    {
        var host = _cards.FirstOrDefault(c => c.Config.Id == cc.Id);
        if (host == null) return;

        _cfg.UniformCards = false;
        _lastUniform = false;

        host.Config.W = w;
        host.Config.H = h;
        host.Config.FollowUniformH = false;
        host.Width = w;
        host.Height = h;

        foreach (var c in _cards) c.SetEditing(!_cfg.LayoutLocked, true);
        Relayout(true);
        FitHeightToContent(alsoShrink: true);
        _cfg.Save();
    }

    // ================= 内存诊断 =================

    /// <summary>
    /// 诊断模式：稳定运行一段时间后，把托管堆 / 非托管 / 图形层的占用分开打印，
    /// 用于判断内存到底堆在哪一层。仅在带 --memreport 启动时执行。
    /// </summary>
    private async Task RunMemReport()
    {
        await Task.Delay(TimeSpan.FromSeconds(25));

        var sb = new System.Text.StringBuilder();
        Dispatcher.Invoke(() =>
        {
            var p = System.Diagnostics.Process.GetCurrentProcess();
            long managed = GC.GetTotalMemory(true);
            var gi = GC.GetGCMemoryInfo();

            sb.AppendLine("===== PerfDeck 内存报告 =====");
            sb.AppendLine($"托管堆 GetTotalMemory   : {managed / 1048576.0:N1} MB");
            sb.AppendLine($"GC HeapSizeBytes        : {gi.HeapSizeBytes / 1048576.0:N1} MB");
            sb.AppendLine($"GC FragmentedBytes      : {gi.FragmentedBytes / 1048576.0:N1} MB");
            sb.AppendLine($"GC TotalCommitted       : {gi.TotalCommittedBytes / 1048576.0:N1} MB");
            sb.AppendLine($"PinnedObjects           : {gi.PinnedObjectsCount}");
            sb.AppendLine($"WorkingSet              : {p.WorkingSet64 / 1048576.0:N1} MB");
            sb.AppendLine($"PrivateMemory           : {p.PrivateMemorySize64 / 1048576.0:N1} MB");
            sb.AppendLine($"非托管≈Private-托管堆   : {(p.PrivateMemorySize64 - managed) / 1048576.0:N1} MB");
            sb.AppendLine($"GC 次数 0/1/2           : {GC.CollectionCount(0)} / {GC.CollectionCount(1)} / {GC.CollectionCount(2)}");
            sb.AppendLine($"线程 / 句柄             : {p.Threads.Count} / {p.HandleCount}");
            sb.AppendLine($"已加载模块              : {p.Modules.Count}");
            sb.AppendLine($"渲染层 Tier             : {RenderCapability.Tier >> 16}");
            sb.AppendLine($"卡片数 / 历史环         : {_cards.Count} / {_rings.Count}");

            // 非托管可疑源：WPF 图形层与 LHM 依赖的 WMI
            var wmi = p.Modules.Cast<System.Diagnostics.ProcessModule>()
                .Where(m => m.ModuleName.Contains("wmi", StringComparison.OrdinalIgnoreCase)
                            || m.ModuleName.Contains("wbem", StringComparison.OrdinalIgnoreCase))
                .Select(m => $"{m.ModuleName}({m.ModuleMemorySize / 1024}KB)").ToList();
            sb.AppendLine("WMI 相关模块            : " + (wmi.Count == 0 ? "无" : string.Join(", ", wmi)));
        });

        Log.Write("MEM", sb.ToString().Replace(Environment.NewLine, " | "));
        Console.WriteLine(sb.ToString());

        var log = Path.Combine(AppConfig.DefaultDir, "memreport.txt");
        File.WriteAllText(log, sb.ToString());
        Console.WriteLine("报告已写入 " + log);
    }

    // ================= 窗口 =================

    /// <summary>供设置窗口显示的天气状态。</summary>
    public string WeatherStatusText => _weather.Latest switch
    {
        null => "正在获取…",
        var w when !w.HasData => w.Error ?? "暂无数据",
        var w => $"{w.City} {w.TempC:0}°C {w.Condition} · 更新于 {w.Updated:HH:mm}"
    };

    private void ApplyChrome()
    {
        Topmost = _cfg.AlwaysOnTop;
        ShowInTaskbar = _cfg.ShowInTaskbar;
        Opacity = _cfg.Opacity;
        BtnPin.Foreground = Palette.Brush(_cfg.AlwaysOnTop ? Color.FromRgb(0x7D, 0xD3, 0xFC) : Color.FromRgb(0x6B, 0x7A, 0x8C));
    }

    /// <summary>设置窗口保存后调用，立即生效。</summary>
    public void ApplyConfig()
    {
        ApplyThemeAndWallpaper();
        ApplyChrome();
        ApplyClickThrough();
        RestartLoops();
        StartApi();      // 端口/开关可能被改过
        EnsureWeather();
        UpdateClockCards();
        if (_snap != null) ApplySnapshot(_snap);

        bool layoutChanged = _lastUniform != _cfg.UniformCards
                             || Math.Abs(_lastUniformH - _cfg.UniformCardH) > 0.5;
        _lastUniform = _cfg.UniformCards;
        _lastUniformH = _cfg.UniformCardH;

        foreach (var c in _cards) c.SetEditing(!_cfg.LayoutLocked, true);
        InitToolbarAutoHide();
        Relayout(false);
        FitHeightToContent(alsoShrink: layoutChanged);
        if (_snap != null) ApplySnapshot(_snap);
    }

    private void PlaceWindow()
    {
        RefreshDisplays("启动");

        double w = _cfg.WinW > 300 ? _cfg.WinW : 812;
        double h = _cfg.WinH > 200 ? _cfg.WinH : 606;
        Width = w;
        Height = h;

        if (_cfg.WinX.HasValue && _cfg.WinY.HasValue)
        {
            Left = _cfg.WinX.Value;
            Top = _cfg.WinY.Value;
            EnsureOnScreen();
            ClampToScreen();
            return;
        }

        // 默认落在副屏；只有一块屏（或识别失败）时落在主屏
        var target = DisplayHelper.Secondary(_displays) ?? DisplayHelper.Primary(_displays);
        if (target == null)
        {
            // 识别彻底失败：给一个安全的默认位置，不让窗口跑到屏幕外
            Left = 200;
            Top = 120;
            Log.Warn("显示器识别失败，使用默认位置");
            return;
        }

        double scale = target.Scale <= 0.1 ? 1.0 : target.Scale;
        double px = target.WorkX + Math.Max(0, (target.WorkWidth - w * scale) / 2);
        double py = target.WorkY + Math.Max(0, (target.WorkHeight - h * scale) / 2);
        Left = px / scale;
        Top = py / scale;
    }

    // ================= 显示器识别 =================

    /// <summary>重新识别显示器。启动时、显示器插拔（WM_DISPLAYCHANGE）、手动切换时调用。</summary>
    private void RefreshDisplays(string reason)
    {
        _displays = DisplayHelper.GetDisplays();
        if (_displays.Count == 0)
            Log.Warn($"显示器识别失败（{reason}），回退到窗口当前位置");
        else
            Log.Info($"显示器识别（{reason}）：" + string.Join(" / ", _displays.Select(d => d.DeviceName + " " + d.Describe())));
    }

    private DisplayInfo? CurrentScreen()
    {
        if (_displays.Count == 0) return null;
        double scale = 1.0;
        var ps = PresentationSource.FromVisual(this);
        if (ps?.CompositionTarget != null) scale = ps.CompositionTarget.TransformToDevice.M11;
        if (scale <= 0.1) scale = 1.0;
        return DisplayHelper.FromPoint(_displays, (Left + Width / 2) * scale, (Top + Height / 2) * scale)
               ?? DisplayHelper.FromPoint(_displays, Left * scale, Top * scale);
    }

    /// <summary>显示器被拔掉后，把窗口挪回仍然存在的屏幕内，避免“窗口消失在屏幕外”。</summary>
    private void EnsureOnScreen()
    {
        if (_displays.Count == 0) return;
        if (CurrentScreen() != null) return;

        var p = DisplayHelper.Primary(_displays);
        if (p == null) return;

        double scale = p.Scale <= 0.1 ? 1.0 : p.Scale;
        Left = (p.WorkX + 40) / scale;
        Top = (p.WorkY + 40) / scale;
        Log.Info("窗口原显示器已不存在，已移回主屏");
    }

    /// <summary>窗口尺寸超出所在屏幕时收回屏幕内（改过分辨率 / 换过显示器都会遇到）。</summary>
    private void ClampToScreen()
    {
        if (_maximized) return;     // 全屏时不收缩窗口
        var screen = CurrentScreen() ?? DisplayHelper.Primary(_displays);
        if (screen == null) return;

        double scale = screen.Scale <= 0.1 ? 1.0 : screen.Scale;
        double maxW = screen.Width / scale - 2;
        double maxH = screen.Height / scale - 2;

        if (Width > maxW) Width = maxW;
        if (Height > maxH) Height = maxH;
        if (Left + Width > (screen.X + screen.Width) / scale) Left = (screen.X + screen.Width) / scale - Width;
        if (Top + Height > (screen.Y + screen.Height) / scale) Top = (screen.Y + screen.Height) / scale - Height;
    }

    private void MoveToScreen(bool secondary)
    {
        RefreshDisplays("手动切换");
        var target = secondary ? DisplayHelper.Secondary(_displays) : DisplayHelper.Primary(_displays);
        if (target == null)
        {
            Log.Warn("未检测到副屏，保持当前位置");
            return;
        }

        // 还原状态下才移动；最大化时按目标屏重新铺满
        var bounds = _maximized ? _restoreBounds : new Rect(Left, Top, Width, Height);
        double scale = target.Scale <= 0.1 ? 1.0 : target.Scale;

        Left = (target.WorkX + Math.Max(0, (target.WorkWidth - bounds.Width * scale) / 2)) / scale;
        Top = (target.WorkY + Math.Max(0, (target.WorkHeight - bounds.Height * scale) / 2)) / scale;

        if (_maximized)
        {
            _maximized = false;
            ToggleMaximize();
        }

        SaveBounds();
    }

    // ================= 最大化 / 还原 =================

    /// <summary>
    /// 无边框窗口不走系统最大化，改为铺满整块屏幕（含任务栏区域），
    /// 这样最大化后能真正全屏、不被任务栏挤掉一条。
    /// 适用于主面板窗口；设置、诊断等对话框不参与。
    /// </summary>
    private void ToggleMaximize()
    {
        if (!_maximized)
        {
            var screen = CurrentScreen() ?? DisplayHelper.Primary(_displays);
            if (screen == null) return;

            _restoreBounds = new Rect(Left, Top, Width, Height);
            double scale = screen.Scale <= 0.1 ? 1.0 : screen.Scale;

            _maximized = true;
            // 用整屏尺寸而不是工作区，最大化时连任务栏一起盖住
            Left = screen.X / scale;
            Top = screen.Y / scale;
            Width = screen.Width / scale;
            Height = screen.Height / scale;

            // 盖住任务栏需要窗口在置顶层，退出最大化后恢复用户设置
            _topmostBeforeMax = Topmost;
            Topmost = true;

            BtnMax.Content = "\uE923";     // 还原图标
            WinGrip.Visibility = Visibility.Collapsed;
            ApplyCornerRadius();
        }
        else
        {
            _maximized = false;
            Left = _restoreBounds.X;
            Top = _restoreBounds.Y;
            Width = _restoreBounds.Width;
            Height = _restoreBounds.Height;

            BtnMax.Content = "\uE922";     // 最大化图标
            WinGrip.Visibility = Visibility.Visible;
            Topmost = _topmostBeforeMax;
            ApplyCornerRadius();
        }

        HideToolbarNow();

        // 宽度变了要重新排版；等布局完成后再算槽位
        Dispatcher.BeginInvoke(new Action(() =>
        {
            Relayout(false);
            UpdateWallClip();
        }), DispatcherPriority.Loaded);

        _cfg.Save();
    }

    // ================= 主题与壁纸 =================

    private void ApplyThemeAndWallpaper()
    {
        _theme = Themes.Find(_cfg.Theme);
        ThemeService.Apply(_theme);

        ShellPanel.Background = new LinearGradientBrush(
            _theme.PanelTop, _theme.PanelBottom, new Point(0.15, 0), new Point(0.85, 1));
        ShellPanel.BorderBrush = Palette.Brush(_theme.Separator);
        EmptyHint.Foreground = Palette.Brush(_theme.TextTertiary);
        BtnMax.Foreground = Palette.Brush(_theme.TextSecondary);
        ApplyCornerRadius();

        foreach (var c in _cards) c.ApplyTheme(_theme, _cfg.CardAlpha);
        ApplyCardShadow();
        ApplyWallpaper();
    }

    /// <summary>壁纸层：图片 + 高斯模糊 + 暗化，配合卡片半透明度使用。</summary>
    /// <summary>按配置给所有卡片开关投影。</summary>
    private void ApplyCardShadow()
    {
        foreach (var c in _cards) c.SetShadow(_cfg.CardShadow);
    }

    private void ApplyWallpaper()
    {
        if (string.IsNullOrWhiteSpace(_cfg.Wallpaper) || !System.IO.File.Exists(_cfg.Wallpaper))
        {
            if (!string.IsNullOrWhiteSpace(_cfg.Wallpaper))
                Log.Warn("壁纸文件不存在，已忽略：" + _cfg.Wallpaper);
            WallLayer.Visibility = Visibility.Collapsed;
            WallDim.Visibility = Visibility.Collapsed;
            WallLayer.Effect = null;
            _wallpaperBitmap = null;
            _wallpaperLoaded = null;
            return;
        }

        try
        {
            // 同一张图只解码一次：拖滑块调模糊/暗化时不再重复读盘
            if (_wallpaperLoaded != _cfg.Wallpaper || _wallpaperBitmap == null)
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
                bmp.UriSource = new Uri(_cfg.Wallpaper, UriKind.Absolute);
                bmp.EndInit();
                bmp.Freeze();

                _wallpaperBitmap = bmp;
                _wallpaperLoaded = _cfg.Wallpaper;
                WallBrush.ImageSource = bmp;
            }

            WallBrush.Opacity = _cfg.WallpaperImageOpacity;

            WallLayer.Effect = _cfg.WallpaperBlur > 0.5
                ? new BlurEffect { Radius = _cfg.WallpaperBlur, KernelType = KernelType.Gaussian }
                : null;

            WallLayer.Visibility = Visibility.Visible;
            WallDim.Visibility = Visibility.Visible;
            WallDim.Opacity = _cfg.WallpaperDim;

            UpdateWallClip();
        }
        catch (Exception ex)
        {
            Log.Warn("壁纸加载失败：" + ex.Message);
            WallLayer.Visibility = Visibility.Collapsed;
            WallDim.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>
    /// 铺满屏幕时必须去掉圆角，否则四个角是透明的，会露出底下的白色/桌面。
    /// </summary>
    private void ApplyCornerRadius()
    {
        double r = _maximized ? 0 : 18;
        ShellPanel.CornerRadius = new CornerRadius(r);
        WallLayer.CornerRadius = new CornerRadius(r);
        WallDim.CornerRadius = new CornerRadius(r);
        UpdateWallClip();
    }

    private void UpdateWallClip()
    {
        double w = WallLayer.ActualWidth, h = WallLayer.ActualHeight;
        if (w <= 1 || h <= 1) return;

        double r = _maximized ? 0 : 17;
        WallLayer.Clip = r <= 0
            ? null
            : new RectangleGeometry(new Rect(0, 0, w, h), r, r);
    }

    private void HookChrome()
    {
        ShellPanel.MouseLeftButtonDown += (s, e) =>
        {
            if (e.ChangedButton != MouseButton.Left) return;
            if (InThumb(e.OriginalSource as DependencyObject, null)) return;
            if (_dragCard != null || _pendingCard != null) return;   // 卡片拖动/长按判定中，不要跟着拖窗口
            try { DragMove(); } catch { }
        };

        BtnClose.Click += (_, _) => Close();
        BtnSettings.Click += (_, _) => OpenSettings();
        BtnPin.Click += (_, _) =>
        {
            _cfg.AlwaysOnTop = !_cfg.AlwaysOnTop;
            ApplyChrome();
            _cfg.Save();
        };
        BtnMax.Click += (_, _) => ToggleMaximize();
        BtnLock.Click += (_, _) => ToggleLock();
        BtnAdd.Click += (_, _) => ShowAddMenu(BtnAdd);

        WinGrip.DragDelta += (s, e) =>
        {
            Width = Math.Max(360, Width + e.HorizontalChange);
            Height = Math.Max(240, Height + e.VerticalChange);
        };
        WinGrip.DragCompleted += (_, _) =>
        {
            Relayout(false);
            FitHeightToContent();
            SaveBounds();
        };

        CtrlBar.MouseRightButtonUp += (s, e) => { e.Handled = true; };
    }

    private void HookDeck()
    {
        Deck.MouseLeftButtonDown += (s, e) =>
        {
            if (e.ChangedButton != MouseButton.Left) return;
            if (_dragCard != null || _pendingCard != null) return;   // 卡片拖动/长按判定中，不要跟着拖窗口
            try { DragMove(); } catch { }
        };

        Deck.MouseRightButtonUp += (s, e) =>
        {
            if (!ReferenceEquals(e.OriginalSource, Deck)) return;
            var menu = BuildDeckMenu();
            menu.PlacementTarget = Deck;
            menu.Placement = PlacementMode.MousePoint;
            menu.IsOpen = true;
            e.Handled = true;
        };
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;
        HwndSource.FromHwnd(_hwnd)?.AddHook(WndProc);

        Native.RegisterHotKey(_hwnd, HotkeyClickThrough, Native.MOD_CONTROL | Native.MOD_ALT, 0x58);
        Native.RegisterHotKey(_hwnd, HotkeySettings, Native.MOD_CONTROL | Native.MOD_ALT, 0x53);
        Native.RegisterHotKey(_hwnd, HotkeyLock, Native.MOD_CONTROL | Native.MOD_ALT, 0x4C);
        Native.RegisterHotKey(_hwnd, HotkeyMaximize, Native.MOD_CONTROL | Native.MOD_ALT, 0x4D);

        ApplyClickThrough();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_HOTKEY = 0x0312;
        const int WM_DISPLAYCHANGE = 0x007E;
        if (msg == WM_DISPLAYCHANGE)
        {
            // 插拔显示器 / 改分辨率：重新识别，必要时把窗口拉回可见区域
            RefreshDisplays("显示器变更");
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (_maximized)
                {
                    _maximized = false;
                    ToggleMaximize();          // 按新屏幕重新铺满
                }
                else
                {
                    EnsureOnScreen();
                    Relayout(false);
                }
                UpdateWallClip();
            }), DispatcherPriority.Background);
            handled = true;
        }
        else if (msg == WM_HOTKEY)
        {
            int id = wParam.ToInt32();
            if (id == HotkeyClickThrough)
            {
                _cfg.ClickThrough = !_cfg.ClickThrough;
                ApplyClickThrough();
                _cfg.Save();
                handled = true;
            }
            else if (id == HotkeySettings) { OpenSettings(); handled = true; }
            else if (id == HotkeyLock) { ToggleLock(); handled = true; }
            else if (id == HotkeyMaximize) { ToggleMaximize(); handled = true; }
        }
        return IntPtr.Zero;
    }

    private void ApplyClickThrough()
    {
        if (_hwnd == IntPtr.Zero) return;
        try
        {
            int ex = Native.GetWindowLong(_hwnd, Native.GWL_EXSTYLE);
            if (_cfg.ClickThrough) ex |= Native.WS_EX_TRANSPARENT;
            else ex &= ~Native.WS_EX_TRANSPARENT;
            Native.SetWindowLong(_hwnd, Native.GWL_EXSTYLE, ex);
        }
        catch { }
    }

    private void ToggleLock()
    {
        _cfg.LayoutLocked = !_cfg.LayoutLocked;
        foreach (var c in _cards)
        {
            c.SetEditing(!_cfg.LayoutLocked);
            c.ContextMenu = BuildCardMenu(c.Config);
        }
        BtnLock.Foreground = Palette.Brush(_cfg.LayoutLocked
            ? Color.FromRgb(0x7D, 0xD3, 0xFC)
            : Color.FromRgb(0x6B, 0x7A, 0x8C));
        _cfg.Save();
    }

    /// <summary>对外暴露：打开设置窗口（命令行 --settings 用）。</summary>
    public void ShowSettings() => OpenSettings();

    private void OpenSettings()
    {
        try
        {
            if (_settings is { IsVisible: true })
            {
                _settings.Activate();
                return;
            }

            // Owner 设为主窗口：设置窗会显示在主面板之上，且跟随主窗口所在屏幕；
            // Owner=null 时在多屏下会跑到另一块屏、被主面板整块盖住。
            _settings = new SettingsWindow(_cfg, this) { Owner = this };
            _settings.Closed += (_, _) => _settings = null;
            _settings.Show();
            _settings.Activate();
        }
        catch (Exception ex)
        {
            Log.Error("打开设置窗口失败：" + ex);
        }
    }

    // ================= 卡片构建 =================

    private void BuildDefaultCards()
    {
        const double cw = 250, ch = 152, chTall = 176;
        var list = new List<CardConfig>
        {
            new() { Kind = CardKind.Clock, W = cw * 3 + Gap * 2, H = 128 },
            new() { Kind = CardKind.Cpu, W = cw, H = ch },
            new() { Kind = CardKind.CpuTemp, W = cw, H = ch }
        };

        foreach (var g in _sensors.Gpus.OrderBy(g => g.Integrated))
            list.Add(new CardConfig
            {
                Kind = g.Integrated ? CardKind.Igpu : CardKind.Gpu,
                Target = g.Key,
                W = cw,
                H = ch
            });

        list.Add(new CardConfig { Kind = CardKind.Mem, W = cw, H = ch });
        list.Add(new CardConfig { Kind = CardKind.HotSearch, W = cw, H = chTall });
        list.Add(new CardConfig { Kind = CardKind.Net, W = cw, H = ch });
        list.Add(new CardConfig { Kind = CardKind.NetRate, W = cw, H = ch });
        list.Add(new CardConfig { Kind = CardKind.Proc, W = cw, H = chTall });

        foreach (var d in _sensors.Disks)
            list.Add(new CardConfig { Kind = CardKind.Disk, Target = d.Key, W = cw, H = ch });

        foreach (var d in _sensors.Disks.Take(2))
            list.Add(new CardConfig { Kind = CardKind.DiskIo, Target = d.Key, W = cw, H = ch });

        foreach (var f in _sensors.Fans.Take(2))
            list.Add(new CardConfig { Kind = CardKind.Fan, Target = f.Key, W = cw, H = ch });

        var discrete = _sensors.Gpus.FirstOrDefault(g => !g.Integrated);
        if (discrete != null)
            list.Add(new CardConfig { Kind = CardKind.GpuTemp, Target = discrete.Key, W = cw, H = ch });

        list.Add(new CardConfig { Kind = CardKind.Weather, W = cw, H = ch });
        list.Add(new CardConfig { Kind = CardKind.Battery, W = cw, H = ch });

        _cfg.Cards = list;
    }

    /// <summary>
    /// 修复历史数据：早期版本把“铺满整行后的宽度”写回了配置，
    /// 导致卡片被永久拉宽（时钟甚至会被存成 1500+）。这里按类型还原默认宽度。
    /// </summary>
    private void RepairCardWidths(bool force = false)
    {
        // 旧版本把“网络速率显示上行”存在 Target 里，现在统一走 Dir
        foreach (var c in _cfg.Cards)
            if (c.Kind == CardKind.NetRate && string.Equals(c.Target, "up", StringComparison.OrdinalIgnoreCase))
            {
                c.Dir = "up";
                c.Target = null;
            }

        foreach (var c in _cfg.Cards)
        {
            double defW = c.Kind == CardKind.Clock ? 774 : 250;
            double defH = c.Kind == CardKind.Proc ? 176 : c.Kind == CardKind.Clock ? 128 : 152;
            if (force || c.W > defW * 1.8 || c.W < 100) c.W = defW;
            if (force || c.H > defH * 1.8 || c.H < 72) c.H = defH;
            // 时钟高度是单独记的，超出合理区间就回到“自动”
            if (c.Kind == CardKind.Clock && (c.ClockH > 260 || (c.ClockH > 0 && c.ClockH < 72))) c.ClockH = 0;
        }
    }

    /// <summary>老配置升级：补齐本次新增的监控项，不动用户已有排布。</summary>
    private void EnsureNewCards()
    {
        const double cw = 250, ch = 152;
        bool Has(CardKind k) => _cfg.Cards.Any(c => c.Kind == k);

        if (!Has(CardKind.NetRate))
            _cfg.Cards.Add(new CardConfig { Kind = CardKind.NetRate, W = cw, H = ch });
        if (!Has(CardKind.CpuTemp))
            _cfg.Cards.Add(new CardConfig { Kind = CardKind.CpuTemp, W = cw, H = ch });
        if (!Has(CardKind.Proc))
            _cfg.Cards.Add(new CardConfig { Kind = CardKind.Proc, W = cw, H = 176 });
        if (!Has(CardKind.DiskIo) && _sensors.Disks.Count > 0)
            _cfg.Cards.Add(new CardConfig { Kind = CardKind.DiskIo, Target = _sensors.Disks[0].Key, W = cw, H = ch });
        if (!Has(CardKind.Fan) && _sensors.Fans.Count > 0)
            _cfg.Cards.Add(new CardConfig { Kind = CardKind.Fan, Target = _sensors.Fans[0].Key, W = cw, H = ch });
        if (!Has(CardKind.Weather))
            _cfg.Cards.Add(new CardConfig { Kind = CardKind.Weather, W = cw, H = ch });
        if (!Has(CardKind.Battery))
            _cfg.Cards.Add(new CardConfig { Kind = CardKind.Battery, W = cw, H = ch });
        if (!Has(CardKind.HotSearch))
            _cfg.Cards.Add(new CardConfig { Kind = CardKind.HotSearch, W = cw, H = 200 });
        if (!Has(CardKind.GpuTemp))
        {
            var dg = _sensors.Gpus.FirstOrDefault(g => !g.Integrated);
            if (dg != null)
                _cfg.Cards.Add(new CardConfig { Kind = CardKind.GpuTemp, Target = dg.Key, W = cw, H = ch });
        }
    }

    private void RebuildCards()
    {
        _dragCard = null;
        _pendingCard = null;
        StopDragTimer();
        Deck.Children.Clear();
        _cards.Clear();

        foreach (var cc in _cfg.Cards.Where(c => c.Visible))
        {
            var host = new CardHost(cc)
            {
                Width = cc.W,
                Height = cc.H,
                ContextMenu = BuildCardMenu(cc)
            };
            Canvas.SetLeft(host, cc.X);
            Canvas.SetTop(host, cc.Y);

            host.MouseLeftButtonDown += Card_MouseDown;
            host.MouseMove += Card_MouseMove;
            host.MouseLeftButtonUp += Card_MouseUp;
            host.GripThumb.DragDelta += (s, e) => Card_Resize(host, e);
            host.GripThumb.DragCompleted += (_, _) =>
            {
                SaveCardLayout(host);
                Relayout(false);
                FitHeightToContent();
                SaveBounds();
            };
            host.SetEditing(!_cfg.LayoutLocked, true);

            Deck.Children.Add(host);
            _cards.Add(host);
        }

        EmptyHint.Visibility = _cards.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_cards.Count == 0) EmptyHint.Text = "右键添加监控项";

        _needProcs = _cfg.Cards.Any(c => c.Kind == CardKind.Proc);
        foreach (var c in _cards) c.ApplyTheme(_theme, _cfg.CardAlpha);
        ApplyCardShadow();

        Relayout(false);
    }

    // ================= 拖动排序（手机桌面式） =================

    /// <summary>长按判定阈值：按住不动这么久才进入拖动，普通点击不移动卡片。</summary>
    private const int LongPressMs = 300;
    private const double LongPressSlop = 8;      // 长按期间允许的手抖幅度（DIP）

    private void Card_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        // 手柄内部的图标是 Path，直接判 "is Thumb" 会漏掉，必须往上找祖先
        if (InThumb(e.OriginalSource as DependencyObject, sender as DependencyObject)) return;

        var card = (CardHost)sender;

        // 角标（切热搜平台）、热搜条目（打开链接）这类区域只响应点击，不进入长按拖动判定。
        // 这里先吃掉事件，避免冒泡到 ShellPanel/Deck 的 DragMove() 去拖整个窗口。
        if (HitInteractiveElement(e.OriginalSource as DependencyObject, card)) { e.Handled = true; return; }

        // 锁定布局时保持旧行为：在卡片上按住可以拖动窗口
        if (_cfg.LayoutLocked) return;

        // 其余情况必须在卡片这一层吃掉事件，否则“按在卡片上拖”动的是整个窗口
        e.Handled = true;

        // 只登记“待判定”，不动卡片：单击不改变布局，按住超过阈值才进入拖动。
        _pendingCard = card;
        _pendingAt = DateTime.UtcNow;
        _pendingStart = e.GetPosition(Deck);
        StartDragTimer();
    }

    private void Card_MouseMove(object sender, MouseEventArgs e)
    {
        // 长按判定完全交给定时器（含 8px 位移容差与按键状态检查）。
        // 这里绝对不能用普通 MouseMove 去取消：手抖 1px 就会把长按废掉。
    }

    /// <summary>把被拖卡片按指针位置落位，并实时换位让周边卡片让出位置。</summary>
    private void DragTo(Point p)
    {
        var card = _dragCard;
        if (card == null) return;

        double nx = _dragOrig.X + (p.X - _dragStart.X);
        double ny = _dragOrig.Y + (p.Y - _dragStart.Y);

        // 边界按「画布内容」算，而不是窗口：内容比视口高时要能一路拖到最后一行。
        // 注意 Deck 是 Canvas，宽度由 ScrollViewer 决定（HorizontalContentAlignment=Left 时
        // ActualWidth 会是 0），所以宽度必须回退到视口宽度，否则 X 方向会被钳死在最左。
        double contentW = Math.Max(Deck.ActualWidth, DeckScroll.ViewportWidth);
        double contentH = Math.Max(Deck.ActualHeight, DeckScroll.ViewportHeight);
        if (contentW < 40) contentW = Math.Max(ActualWidth, Width);

        double maxX = Math.Max(2, contentW - card.Width - 2);
        double maxY = Math.Max(2, contentH - card.Height - 2);
        nx = Math.Clamp(nx, 2, maxX);
        ny = Math.Clamp(ny, 2, maxY);

        Canvas.SetLeft(card, nx);
        Canvas.SetTop(card, ny);

        // 用卡片当前中心去比槽位中心（而不是指针），这样判定与“按在卡片哪个位置”无关。
        // 每帧都把所有插入位重算一遍太浪费，位移不足 5px 时沿用上次结果。
        if (_probeInsert < 0 || Math.Abs(nx - _probeX) >= 5 || Math.Abs(ny - _probeY) >= 5)
        {
            _probeX = nx;
            _probeY = ny;
            _probeInsert = ComputeInsertPosition(nx + card.Width / 2, ny + card.Height / 2);
        }
        if (_probeInsert >= 0) ApplyOrder(_probeInsert);
    }

    private void Card_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_dragCard != null) { EndDrag(); return; }
        CancelPending();          // 普通单击：什么都不动
    }

    /// <summary>自身或祖先里是否有 Thumb（缩放手柄）。手柄里的图标是 Path，不能只看 OriginalSource。</summary>
    private static bool InThumb(DependencyObject? src, DependencyObject? stop)
    {
        while (src != null)
        {
            if (src is Thumb) return true;
            if (ReferenceEquals(src, stop)) return false;
            src = VisualTreeHelper.GetParent(src);
        }
        return false;
    }

    /// <summary>命中测试：是否点在卡片内部的交互元素上（带 Action / URL 标记的子元素）。</summary>
    private static bool HitInteractiveElement(DependencyObject? src, CardHost card)
    {
        while (src != null && !ReferenceEquals(src, card))
        {
            if (src is FrameworkElement fe)
            {
                if (fe.Tag is Action) return true;
                if (fe.Tag is string url && url.Length > 0) return true;
            }
            src = VisualTreeHelper.GetParent(src);
        }
        return false;
    }

    private void EndDrag()
    {
        if (_dragCard == null) return;
        var card = _dragCard;
        _dragCard = null;
        StopDragTimer();

        card.ReleaseMouseCapture();
        Canvas.SetZIndex(card, 0);
        card.SetDragging(false);

        // 几乎没动 = 长按后没挪，直接回原位，不参与排序
        double moved = Math.Abs(Canvas.GetLeft(card) - _dragOrig.X)
                       + Math.Abs(Canvas.GetTop(card) - _dragOrig.Y);
        if (moved < 5)
        {
            SetPosImmediate(card, _dragOrig.X, _dragOrig.Y);
            return;
        }

        // 落入空出的位置
        var slots = ComputeSlots();
        if (slots.TryGetValue(card.Config.Id, out var r))
            AnimateTo(card, r.X, r.Y);

        FitHeightToContent();
        // 拖动只改顺序；卡片宽度由布局计算，不能把“铺满时的宽度”写回配置
        _cfg.Save();
    }

    /// <summary>
    /// 卡片中心 (cx,cy) 对应的插入下标（相对于“除被拖卡片外”的顺序）。
    /// 做法：把被拖卡片依次试插到每个位置，算出它会落在哪个槽位，
    /// 取“槽位中心离卡片中心最近”的那个 —— 落点唯一、无抖动，和手机桌面一致。
    /// 注意必须用卡片中心而不是指针：指针在卡片上的落点可以偏上也可以偏下，
    /// 用指针判定时“按在标题处”要拖过将近一整张卡片高度，下方卡片才会让位。
    /// </summary>
    private int ComputeInsertPosition(double cx, double cy)
    {
        if (_dragCard == null) return -1;
        var others = _cards.Where(c => !ReferenceEquals(c, _dragCard)).ToList();
        if (others.Count == 0) return -1;

        int cur = _cards.IndexOf(_dragCard);   // 当前下标（= 原地插入位置）
        int best = -1;
        double bestDist = double.MaxValue;
        double curDist = double.MaxValue;
        var probe = new List<CardHost>(others.Count + 1);

        for (int i = 0; i <= others.Count; i++)
        {
            probe.Clear();
            probe.AddRange(others);
            probe.Insert(i, _dragCard);

            var slots = ComputeSlots(probe);
            if (!slots.TryGetValue(_dragCard.Config.Id, out var r)) continue;

            double dx = cx - (r.X + r.Width / 2);
            double dy = cy - (r.Y + r.Height / 2);
            double d = dx * dx + dy * dy;

            if (i == cur) curDist = d;
            if (d < bestDist) { bestDist = d; best = i; }
        }

        if (best < 0) return -1;

        // 滞回：新位置必须明显更近才换位，否则在临界线上会来回闪
        if (best != cur && curDist < double.MaxValue && bestDist > curDist * 0.85) return cur;
        return best;
    }

    // ---------- 拖动定时器：长按判定 + 拖到边缘自动滚动（只在按下/拖动期间运行） ----------

    private void StartDragTimer()
    {
        if (_dragTimer == null)
        {
            _dragTimer = new DispatcherTimer(DispatcherPriority.Render)
            {
                Interval = TimeSpan.FromMilliseconds(16)
            };
            _dragTimer.Tick += (_, _) => DragTick();
        }
        _dragTimer.Start();
    }

    private void StopDragTimer() => _dragTimer?.Stop();

    private void CancelPending()
    {
        _pendingCard = null;
        if (_dragCard == null) StopDragTimer();
    }

    private void DragTick()
    {
        // 拖动中：全程由定时器读取指针位置驱动，不依赖鼠标捕获，
        // 这样指针跑到卡片外（向左/右/上）也能跟随。
        if (_dragCard != null)
        {
            if (Mouse.LeftButton != MouseButtonState.Pressed) { EndDrag(); return; }
            DragTo(Mouse.GetPosition(Deck));
            DragAutoScroll();
            return;
        }

        var card = _pendingCard;
        if (card == null) { StopDragTimer(); return; }

        if (Mouse.LeftButton != MouseButtonState.Pressed) { CancelPending(); return; }

        Point now = Mouse.GetPosition(Deck);
        double dx = now.X - _pendingStart.X, dy = now.Y - _pendingStart.Y;
        if (dx * dx + dy * dy > LongPressSlop * LongPressSlop) { CancelPending(); return; }

        if ((DateTime.UtcNow - _pendingAt).TotalMilliseconds < LongPressMs) return;

        _pendingCard = null;
        BeginDrag(card, now);      // 长按成立，转入拖动
    }

    /// <summary>长按成立：真正开始拖动。之前单击/滑动都不会走到这里。</summary>
    private void BeginDrag(CardHost card, Point at)
    {
        // 取实际渲染位置（可能正处在让位动画中），清掉动画后落到该位置，避免起始跳动
        Point pos = card.TranslatePoint(new Point(0, 0), Deck);
        card.BeginAnimation(Canvas.LeftProperty, null);
        card.BeginAnimation(Canvas.TopProperty, null);
        Canvas.SetLeft(card, pos.X);
        Canvas.SetTop(card, pos.Y);

        card.Shift.BeginAnimation(TranslateTransform.XProperty, null);
        card.Shift.BeginAnimation(TranslateTransform.YProperty, null);
        card.Shift.X = card.Shift.Y = 0;

        _dragCard = card;
        _dragStart = at;
        _dragOrig = pos;
        _probeInsert = -1;

        Canvas.SetZIndex(card, 900);
        card.SetDragging(true);
        card.CaptureMouse();       // 能拿到更好（拿不到也不影响，位置由定时器驱动）
    }

    private void DragAutoScroll()
    {
        if (_dragCard == null) { StopDragTimer(); return; }
        if (DeckScroll.ScrollableHeight <= 1) return;

        const double edge = 46;      // 触边热区
        double vh = DeckScroll.ViewportHeight;
        if (vh <= 1) return;

        Point vp = Mouse.GetPosition(DeckScroll);
        double dy = 0;
        if (vp.Y < edge) dy = -(edge - vp.Y) * 0.30;
        else if (vp.Y > vh - edge) dy = (vp.Y - (vh - edge)) * 0.30;
        if (Math.Abs(dy) < 0.5) return;

        double before = DeckScroll.VerticalOffset;
        DeckScroll.ScrollToVerticalOffset(before + dy);
        if (Math.Abs(DeckScroll.VerticalOffset - before) < 0.1) return;   // 已到顶/到底

        // 滚动后同一屏幕位置对应的画布坐标变了 —— 下一帧定时器会按新坐标落位
    }

    /// <summary>按插入下标重排；顺序未变化时不做任何事（避免闪烁）。</summary>
    private void ApplyOrder(int insertAt)
    {
        if (_dragCard == null) return;

        var others = _cards.Where(c => !ReferenceEquals(c, _dragCard)).ToList();
        var newOrder = new List<CardHost>(others);
        newOrder.Insert(Math.Clamp(insertAt, 0, newOrder.Count), _dragCard);

        bool same = newOrder.Count == _cards.Count;
        if (same)
            for (int i = 0; i < newOrder.Count; i++)
                if (!ReferenceEquals(newOrder[i], _cards[i])) { same = false; break; }
        if (same) return;

        _cards.Clear();
        _cards.AddRange(newOrder);

        _cfg.Cards.Clear();
        foreach (var c in _cards) _cfg.Cards.Add(c.Config);

        Relayout(animate: true);
    }

    private void Card_Resize(CardHost host, DragDeltaEventArgs e)
    {
        if (_cfg.LayoutLocked) return;

        // 高度任何模式都能拖；宽度只在自由布局下有意义（自动适配的宽度由列数决定）
        host.Config.FollowUniformH = false;   // 一拖就脱离统一高度
        host.Height = Math.Max(80, host.Height + e.VerticalChange);

        // 时钟行高是按内容单独算的，不跟网格统一高度，所以必须单独记
        if (host.Config.Kind == CardKind.Clock)
            host.Config.ClockH = Math.Clamp(host.Height, 72, 260);

        if (!_cfg.UniformCards)
            host.Width = Math.Max(120, host.Width + e.HorizontalChange);

        e.Handled = true;
    }

    private void SaveCardLayout(CardHost host)
    {
        host.Config.X = Canvas.GetLeft(host);
        host.Config.Y = Canvas.GetTop(host);
        // 宽度在自动适配下由布局算出，不写回；高度是用户拖出来的，要记住
        if (!_cfg.UniformCards) host.Config.W = host.Width;
        host.Config.H = host.Height;
        if (host.Config.Kind == CardKind.Clock)
            host.Config.ClockH = Math.Clamp(host.Height, 72, 260);
        if (Math.Abs(host.Config.H - _cfg.UniformCardH) > 4) host.Config.FollowUniformH = false;
        _cfg.Save();
    }

    private void SaveOrder()
    {
        _cfg.Save();
    }

    private void SaveBounds()
    {
        if (_maximized) return;
        if (WindowState == WindowState.Normal)
        {
            _cfg.WinX = Left;
            _cfg.WinY = Top;
            _cfg.WinW = Width;
            _cfg.WinH = Height;
        }
        _cfg.Save();
    }

    // ================= 流式布局 =================

    /// <summary>按当前顺序计算每张卡片的目标位置（纯计算，不改动界面）。</summary>
    private Dictionary<string, Rect> ComputeSlots() => ComputeSlots(_cards);

    private Dictionary<string, Rect> ComputeSlots(IReadOnlyList<CardHost> order)
    {
        return _cfg.UniformCards ? ComputeUniform(order) : ComputeFree(order);
    }

    /// <summary>
    /// 自动适配布局：时钟横跨整行，其余卡片进同一个网格，宽高完全一致。
    /// 列数由窗口宽度自动挑选——先保证单卡宽度落在易读区间，再让末行留白最少，
    /// 这样卡片既不会窄到看不清数值，也不会在右侧拖出一条空洞。
    /// </summary>
    /// <summary>可用宽度：优先取滚动视口，扣除竖直滚动条占位。</summary>
    private double LayoutWidth()
    {
        // 以窗口宽度为准：滚动条是 Hidden 不占位，而 ScrollViewer.ViewportWidth
        // 在窗口刚变尺寸的那一帧还是旧值，拿它算会把卡片排到窗口外面去。
        double w = ActualWidth;
        if (w > 10) return w;
        double vp = DeckScroll.ViewportWidth;
        if (vp > 10) return vp;
        return Width;
    }

    /// <summary>
    /// 内容区宽度。带鱼屏 / 4K 上视口可能有 2560 DIP 宽，
    /// 全铺开会让每张卡宽到 500+，数字孤零零地漂在中间；
    /// 这里限制一个上限并整体居中，小屏则不低于下限。
    /// </summary>
    private double ContentWidth()
    {
        double vp = LayoutWidth();
        if (vp <= 10) vp = ActualWidth > 0 ? ActualWidth : Width;
        return Math.Clamp(vp, MinContentWidth, MaxContentWidth);
    }

    /// <summary>内容区相对视口的水平居中偏移。</summary>
    private double ContentOffsetX()
    {
        double vp = LayoutWidth();
        if (vp <= 10) return 0;
        return Math.Max(0, (vp - ContentWidth()) / 2);
    }

    /// <summary>视口高度（用于按屏高决定列数）。</summary>
    private double ViewportHeight()
    {
        double vh = DeckScroll.ViewportHeight;
        return vh > 10 ? vh : Math.Max(200, ActualHeight);
    }

    private Dictionary<string, Rect> ComputeUniform(IReadOnlyList<CardHost> order)
    {
        var res = new Dictionary<string, Rect>(order.Count);
        double avail = Math.Max(200, ContentWidth() - Pad * 2);
        double originX = ContentOffsetX() + Pad;      // 内容区居中后的左边界
        double h = Math.Clamp(_cfg.UniformCardH, 130, 300);

        var rest = order.Where(c => c.Config.Kind != CardKind.Clock).ToList();
        int n = rest.Count;

        int cols = n == 0 ? 1 : PickColumns(n, avail, ViewportHeight(), h);
        double cw = (avail - Gap * (cols - 1)) / cols;
        if (cw < 60) cw = Math.Max(60, avail);   // 极端窄窗口保护

        // 每张卡的高度：跟随统一高度，或用户单独拖过的高度
        double CardH(CardHost c) => c.Config.FollowUniformH
            ? h
            : Math.Clamp(c.Config.H, 80, 480);

        double y = Pad;
        double rowTop = Pad, rowH = 0;
        bool rowOpen = false;
        int idx = 0;                 // 已摆放的「非时钟」卡片数

        foreach (var card in order)
        {
            if (card.Config.Kind == CardKind.Clock)
            {
                // 时钟横跨整行，位置由它在顺序里的位置决定（不再是死钉首行，
                // 否则拖它往下时下方卡片永远不用让位）。
                // 默认按内容算一个矮高度，用户拖过手柄后按 ClockH 走。
                if (rowOpen)
                {
                    y = rowTop + rowH + Gap;      // 先结束当前行
                    rowOpen = false;
                    idx = ((idx + cols - 1) / cols) * cols;  // 下一行从行首开始，保持列对齐
                }

                double clockH = card.Config.ClockH > 0
                    ? Math.Clamp(card.Config.ClockH, 72, 260)
                    : Math.Clamp(h * 0.62, 92, 132);

                res[card.Config.Id] = new Rect(originX, y, avail, clockH);
                y += clockH + Gap;
                continue;
            }

            int c = idx % cols;
            if (!rowOpen)
            {
                rowTop = y;
                rowH = 0;
                int take = Math.Min(cols, n - idx);
                for (int k = 0; k < take; k++) rowH = Math.Max(rowH, CardH(rest[idx + k]));
                rowOpen = true;
            }

            res[card.Config.Id] = new Rect(originX + c * (cw + Gap), rowTop, cw, CardH(card));
            idx++;

            if (idx % cols == 0 || idx == n)
            {
                y = rowTop + rowH + Gap;
                rowOpen = false;
            }
        }

        return res;
    }

    /// <summary>
    /// 选列数：卡片宽度优先落在 [MinCardW, MaxCardW]；在候选里取末行留白最小的，
    /// 留白相同则取列数少的（卡片更宽、更易读）。一个候选都没有时按最大列宽反推。
    /// </summary>
    private static int PickColumns(int n, double avail, double viewportH = 0, double cardH = 0)
    {
        // 卡片宽度下限随屏幕变窄而放宽：1366×768 的笔记本扣掉边距后，
        // 硬守 196 会直接掉到 2 列、页面长得离谱。
        double minW = avail < 760 ? 150 : 196;
        const double maxW = 396;

        int bestFits = 0;          // 一屏放得下的最优列数
        double bestWaste = double.MaxValue;
        int bestAny = 0;           // 放不下时的兜底（列数最多 = 行数最少）
        double bestAnyWaste = double.MaxValue;

        for (int cols = 1; cols <= n; cols++)
        {
            double cw = (avail - Gap * (cols - 1)) / cols;
            if (cw < minW) break;          // 列再多元只会更窄
            if (cw > maxW) continue;       // 太宽，先看别的列数

            int rows = (n + cols - 1) / cols;
            int last = n - (rows - 1) * cols;
            double waste = (cols - last) * (cw + Gap);

            // 高度约束：整块内容尽量在一屏内
            double totalH = rows * (cardH + Gap) - Gap;
            bool fits = viewportH <= 0 || totalH <= viewportH;

            if (fits)
            {
                if (waste < bestWaste - 0.5) { bestWaste = waste; bestFits = cols; }
            }
            else if (waste < bestAnyWaste - 0.5) { bestAnyWaste = waste; bestAny = cols; }
        }

        if (bestFits > 0) return bestFits;
        if (bestAny > 0) return bestAny;

        // 卡片太少、宽度必然超出区间：取能塞下的最大列数（等效于铺满一行）
        int fit = (int)Math.Floor((avail + Gap) / (maxW + Gap));
        return Math.Clamp(fit < 1 ? 1 : fit, 1, n);
    }

    /// <summary>自由布局：按各卡片自定义宽高流式排列，每行把剩余宽度均摊。</summary>
    private Dictionary<string, Rect> ComputeFree(IReadOnlyList<CardHost> order)
    {
        var res = new Dictionary<string, Rect>(order.Count);
        double avail = Math.Max(200, ContentWidth() - Pad * 2);
        double originX = ContentOffsetX() + Pad;

        var rows = new List<List<CardHost>>();
        var rowWidths = new List<double>();
        var current = new List<CardHost>();
        double used = 0;

        foreach (var card in order)
        {
            double natural = Math.Min(card.Config.W, avail);
            double need = current.Count == 0 ? natural : used + Gap + natural;

            if (current.Count > 0 && need > avail + 0.5)
            {
                rows.Add(current);
                rowWidths.Add(used);
                current = new List<CardHost>();
                need = natural;
            }

            current.Add(card);
            used = need;
        }
        if (current.Count > 0)
        {
            rows.Add(current);
            rowWidths.Add(used);
        }

        double y = Pad;
        for (int r = 0; r < rows.Count; r++)
        {
            var row = rows[r];
            double leftover = Math.Max(0, avail - rowWidths[r]);

            var widths = new double[row.Count];
            for (int i = 0; i < row.Count; i++)
                widths[i] = Math.Min(row[i].Config.W, avail);

            if (row.Count == 1)
            {
                widths[0] = avail;
            }
            else if (leftover > 1)
            {
                double share = leftover / row.Count;
                double applied = 0;
                for (int i = 0; i < row.Count; i++)
                {
                    double add = Math.Min(share, widths[i] * 0.45);
                    widths[i] += add;
                    applied += add;
                }
                double rest = leftover - applied;
                if (rest > 1)
                {
                    double total = widths.Sum();
                    for (int i = 0; i < row.Count; i++)
                        widths[i] += rest * (widths[i] / total);
                }
            }

            double rowH = row.Max(c => c.Config.H);
            double x = originX;
            for (int i = 0; i < row.Count; i++)
            {
                res[row[i].Config.Id] = new Rect(x, y, widths[i], rowH);
                x += widths[i] + Gap;
            }
            y += rowH + Gap;
        }

        return res;
    }

    /// <summary>把卡片放到目标槽位；animate 时带缓动，被拖动的卡片除外。</summary>
    private void Relayout(bool animate)
    {
        if (_cards.Count == 0) return;
        var slots = ComputeSlots();

        // 内容总高度交给画布，窗口装不下时由 ScrollViewer 提供滚动。
        // 画布必须显式顶部左对齐：ScrollViewer 在内容比视口矮时会把内容居中，
        // 表现就是整块卡片往下掉、顶部空出一截。
        Deck.VerticalAlignment = VerticalAlignment.Top;
        Deck.HorizontalAlignment = HorizontalAlignment.Left;

        double contentBottom = slots.Values.Max(r => r.Y + r.Height) + Pad;
        if (double.IsNaN(Deck.Height) || Math.Abs(Deck.Height - contentBottom) > 1)
            Deck.Height = contentBottom;

        foreach (var card in _cards)
        {
            if (!slots.TryGetValue(card.Config.Id, out var r)) continue;
            card.Config.X = r.X;
            card.Config.Y = r.Y;

            if (Math.Abs(card.Width - r.Width) > 0.5) card.Width = r.Width;
            if (Math.Abs(card.Height - r.Height) > 0.5) card.Height = r.Height;

            if (ReferenceEquals(card, _dragCard)) continue;

            if (animate) AnimateTo(card, r.X, r.Y);
            else SetPosImmediate(card, r.X, r.Y);
        }
    }

    private static readonly CubicEase MoveEase = MakeMoveEase();

    private static CubicEase MakeMoveEase()
    {
        var e = new CubicEase { EasingMode = EasingMode.EaseOut };
        e.Freeze();      // 冻结后可跨动画复用，省掉每帧的分配
        return e;
    }

    /// <summary>
    /// 让位动画：位置直接落到目标值，视觉过渡交给 RenderTransform 的位移。
    /// 动 Canvas.Left/Top 每帧都会触发一次完整布局（16 张卡片的内容全部重排），
    /// 这是之前拖动“卡顿”的主因；渲染变换只走合成层，不再触发布局。
    /// </summary>
    private static void AnimateTo(CardHost card, double x, double y)
    {
        card.BeginAnimation(Canvas.LeftProperty, null);
        card.BeginAnimation(Canvas.TopProperty, null);

        // 当前视觉位置 = 画布位置 + 残留位移
        double vx = Canvas.GetLeft(card) + card.Shift.X;
        double vy = Canvas.GetTop(card) + card.Shift.Y;

        Canvas.SetLeft(card, x);
        Canvas.SetTop(card, y);

        double dx = vx - x, dy = vy - y;
        if (Math.Abs(dx) < 0.6 && Math.Abs(dy) < 0.6)
        {
            card.Shift.BeginAnimation(TranslateTransform.XProperty, null);
            card.Shift.BeginAnimation(TranslateTransform.YProperty, null);
            card.Shift.X = card.Shift.Y = 0;
            return;
        }

        var dur = TimeSpan.FromMilliseconds(170);
        card.Shift.BeginAnimation(TranslateTransform.XProperty,
            new DoubleAnimation(dx, 0, dur) { EasingFunction = MoveEase, FillBehavior = FillBehavior.Stop });
        card.Shift.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(dy, 0, dur) { EasingFunction = MoveEase, FillBehavior = FillBehavior.Stop });
        card.Shift.X = 0;
        card.Shift.Y = 0;
    }

    private static void SetPosImmediate(CardHost card, double x, double y)
    {
        card.BeginAnimation(Canvas.LeftProperty, null);
        card.BeginAnimation(Canvas.TopProperty, null);
        Canvas.SetLeft(card, x);
        Canvas.SetTop(card, y);
        card.Shift.BeginAnimation(TranslateTransform.XProperty, null);
        card.Shift.BeginAnimation(TranslateTransform.YProperty, null);
        card.Shift.X = card.Shift.Y = 0;
    }

    /// <summary>内容超出现有窗口高度时向下生长。</summary>
    private void FitHeightToContent(bool alsoShrink = false)
    {
        if (_cards.Count == 0) return;
        // 全屏时窗口尺寸由最大化状态决定，任何“按内容长高”都会把窗口拉回普通尺寸
        if (_maximized) return;
        var slots = ComputeSlots();
        double bottom = slots.Values.Max(r => r.Y + r.Height) + Pad;
        double target = Math.Max(200, Math.Ceiling(bottom));

        // 不要长到屏幕以外，否则底部卡片会被切掉、窗口还会超出显示器
        var screen = CurrentScreen() ?? DisplayHelper.Primary(_displays);
        if (screen != null)
        {
            double scale = screen.Scale <= 0.1 ? 1.0 : screen.Scale;
            double maxH = screen.Height / scale - 2;
            if (target > maxH) target = maxH;
        }

        if (target > Height + 1 || (alsoShrink && Math.Abs(target - Height) > 1))
            Height = target;
    }

    // ================= 数据刷新 =================

    private Ring RingFor(string id)
    {
        if (!_rings.TryGetValue(id, out var r))
        {
            r = new Ring(48);
            _rings[id] = r;
        }
        return r;
    }

    /// <summary>当前快照（线程安全读）。外部接口线程用它取数，不会读到半更新状态。</summary>
    public Snapshot? CurrentSnapshot => Volatile.Read(ref _snap);

    private void ApplySnapshot(Snapshot s)
    {
        Volatile.Write(ref _snap, s);

        // 记录历史
        foreach (var cc in _cfg.Cards)
        {
            switch (cc.Kind)
            {
                case CardKind.Cpu:
                    RingFor(cc.Id).Add(s.Cpu?.Load ?? double.NaN);
                    break;
                case CardKind.Gpu:
                case CardKind.Igpu:
                    var g = s.Gpus.FirstOrDefault(x => x.Key == cc.Target) ?? s.Gpus.FirstOrDefault(x => x.Integrated == (cc.Kind == CardKind.Igpu));
                    RingFor(cc.Id).Add(g?.Load ?? double.NaN);
                    break;
                case CardKind.Net:
                    RingFor(cc.Id).Add(s.Net?.LatencyMs ?? double.NaN);
                    break;
                case CardKind.NetRate:
                    RingFor(cc.Id).Add(s.Net?.DownBps ?? double.NaN);
                    break;
                case CardKind.CpuTemp:
                    RingFor(cc.Id).Add(s.Cpu?.Temp ?? double.NaN);
                    break;
                case CardKind.GpuTemp:
                    var gt = s.Gpus.FirstOrDefault(x => x.Key == cc.Target) ?? s.Gpus.FirstOrDefault();
                    RingFor(cc.Id).Add(gt?.Temp ?? double.NaN);
                    break;
                case CardKind.Fan:
                    var fr = s.Fans.FirstOrDefault(x => x.Key == cc.Target) ?? s.Fans.FirstOrDefault();
                    RingFor(cc.Id).Add(fr?.Rpm ?? double.NaN);
                    break;
                case CardKind.DiskIo:
                    var dr = s.Disks.FirstOrDefault(x => x.Key == cc.Target) ?? s.Disks.FirstOrDefault();
                    RingFor(cc.Id).Add((dr?.ReadBps ?? 0) + (dr?.WriteBps ?? 0));
                    break;
            }
        }

        foreach (var card in _cards)
        {
            if (card.Config.Kind == CardKind.Clock) continue;
            card.Apply(Build(card.Config, s));
        }
    }

    private void UpdateClockCards()
    {
        var now = DateTime.Now;
        foreach (var card in _cards)
        {
            if (card.Config.Kind != CardKind.Clock) continue;
            card.Apply(BuildClock(card.Config, now));
        }
    }

    private string TitleOf(CardConfig cc)
    {
        if (!string.IsNullOrWhiteSpace(cc.Title)) return cc.Title!;
        return cc.Kind switch
        {
            CardKind.Cpu => "CPU",
            CardKind.Gpu => "独立显卡",
            CardKind.Igpu => "核显",
            CardKind.Mem => "内存",
            CardKind.Net => "网络延迟",
            CardKind.Disk => "硬盘",
            CardKind.Clock => "时间",
            CardKind.NetRate => "网络速率",
            CardKind.CpuTemp => "CPU 温度",
            CardKind.DiskIo => "磁盘读写",
            CardKind.Proc => "进程",
            CardKind.Fan => "风扇",
            CardKind.Weather => "天气",
            CardKind.Battery => "电池",
            CardKind.GpuTemp => "显卡温度",
            CardKind.HotSearch => "今日热搜",
            _ => ""
        };
    }

    /// <summary>
    /// 所有卡片共用一档主数值字号（按“统一卡高”算，而不是各卡实际高度），
    /// 这样不管卡片多高、有没有副行/曲线，大数字都一样大。
    /// </summary>
    private double UniformValueSize()
    {
        double h = _cfg.UniformCards ? Math.Clamp(_cfg.UniformCardH, 130, 300) : 168;
        // 0.28 是“大数值 + 数值下方副行 + 曲线 + 说明文字”都能塞进标准卡高的上限，
        // 再大副行就会被曲线/说明文字裁掉
        return Math.Clamp(h * 0.28, 18, 64);
    }

    private CardModel Build(CardConfig cc, Snapshot s)
    {
        var m = BuildCore(cc, s);
        // 时钟卡是居中大卡片，字号按自身尺寸走，不参与统一
        m.ValueSize = m.Centered ? 0 : UniformValueSize();
        return m;
    }

    private CardModel BuildCore(CardConfig cc, Snapshot s) => cc.Kind switch
    {
        CardKind.Cpu => BuildCpu(cc, s),
        CardKind.Gpu or CardKind.Igpu => BuildGpu(cc, s),
        CardKind.Mem => BuildMem(cc, s),
        CardKind.Net => BuildNet(cc, s),
        CardKind.Disk => BuildDisk(cc, s),
        CardKind.NetRate => BuildNetRate(cc, s),
        CardKind.CpuTemp => BuildCpuTemp(cc, s),
        CardKind.DiskIo => BuildDiskIo(cc, s),
        CardKind.Fan => BuildFan(cc, s),
        CardKind.Proc => BuildProc(cc, s),
        CardKind.Weather => BuildWeather(cc, s),
        CardKind.Battery => BuildBattery(cc, s),
        CardKind.GpuTemp => BuildGpuTemp(cc, s),
        CardKind.HotSearch => BuildHotSearch(cc, s),
        _ => BuildClock(cc, s.Now)
    };

    private static Severity Sev(double v, double warn, double crit) =>
        v >= crit ? Severity.Critical : v >= warn ? Severity.Warn : Severity.Normal;

    private CardModel BuildCpu(CardConfig cc, Snapshot s)
    {
        var th = _cfg.Thresholds;
        var m = new CardModel { Title = TitleOf(cc), Accent = Palette.Cpu };
        var c = s.Cpu;
        if (c == null)
        {
            m.Value = "—";
            m.Unit = "%";
            m.Footer = "未检测到 CPU 传感器";
            return m;
        }

        double load = c.Load ?? 0;
        m.Value = c.Load.HasValue ? load.ToString("0") : "—";
        m.Unit = "%";

        var sLoad = Sev(load, th.LoadWarn, th.LoadCrit);
        var sTemp = c.Temp.HasValue ? Sev(c.Temp.Value, th.TempWarn, th.TempCrit) : Severity.Normal;
        m.Severity = (Severity)Math.Max((int)sLoad, (int)sTemp);

        // 右上角：功耗（读 MSR，需要管理员权限）；读不到时退回温度，避免这一格空着
        if (c.Power.HasValue)
        {
            m.Chip = $"{c.Power.Value:0}W";
            m.ChipSeverity = Severity.Normal;
        }
        else if (c.Temp.HasValue)
        {
            m.Chip = $"{c.Temp.Value:0}°C";
            m.ChipSeverity = sTemp;
        }
        else if (c.MaxClockMhz.HasValue)
        {
            // 没有功耗/温度传感器时，用当前频率把这格填上，比显示“无功耗”有用
            m.Chip = $"{c.MaxClockMhz.Value / 1000.0:0.0}GHz";
        }
        else
        {
            m.Chip = s.Elevated ? null : "需管理员";
            m.ChipSeverity = Severity.Warn;
        }

        // 左下角：完整型号（不硬截断，宽度不够时由控件在尾部省略）；
        // 线程数与频率放到悬停提示里，别占型号的一行位置
        m.Footer = Short(c.Name, 46);
        m.Tip = $"{c.Name}\n{c.Threads} 线程"
              + (c.MaxClockMhz.HasValue ? $" · {c.MaxClockMhz.Value / 1000.0:0.0} GHz" : "")
              + (c.Temp.HasValue ? $" · {c.Temp.Value:0}°C" : "");

        if (_cfg.ShowSparkline)
        {
            m.Spark = RingFor(cc.Id).Snapshot();
            m.SparkMax = 100;
        }
        return m;
    }

    private CardModel BuildGpu(CardConfig cc, Snapshot s)
    {
        var th = _cfg.Thresholds;
        var g = s.Gpus.FirstOrDefault(x => x.Key == cc.Target)
                ?? s.Gpus.FirstOrDefault(x => x.Integrated == (cc.Kind == CardKind.Igpu));

        var m = new CardModel
        {
            Title = TitleOf(cc),
            Accent = cc.Kind == CardKind.Igpu ? Palette.Igpu : Palette.Gpu
        };

        if (g == null)
        {
            m.Value = "无数据";
            m.Unit = "";
            m.ValueScale = 0.58;
            m.Footer = "显卡未找到";
            return m;
        }

        m.Value = g.Load.HasValue ? g.Load.Value.ToString("0") : "—";
        m.Unit = "%";

        var sLoad = g.Load.HasValue ? Sev(g.Load.Value, th.LoadWarn, th.LoadCrit) : Severity.Normal;
        var sTemp = g.Temp.HasValue ? Sev(g.Temp.Value, th.TempWarn, th.TempCrit) : Severity.Normal;
        m.Severity = (Severity)Math.Max((int)sLoad, (int)sTemp);

        bool integrated = cc.Kind == CardKind.Igpu || g.Integrated;
        bool hasMem = g.MemTotalGb is > 0.1;

        // 右上角：核显报“共享”显存、独显报“独享”显存，两边用词对称；都没有时退回温度
        if (hasMem)
        {
            string tag = integrated ? "共享 " : "独享 ";
            m.Chip = $"{tag}{g.MemUsedGb ?? 0:0.0}/{g.MemTotalGb:0.#}G";
            m.ChipSeverity = Severity.Normal;
        }
        else if (g.Temp.HasValue)
        {
            m.Chip = $"{g.Temp.Value:0}°C";
            m.ChipSeverity = sTemp;
        }
        else
        {
            m.Chip = null;
        }

        // 左下角：独显只放型号（频率归温度卡），核显补上频率便于看实际运行档位；
        // 型号不再硬截断，靠控件尾部省略 + 悬停看全名
        m.Footer = integrated && g.ClockMhz.HasValue
            ? $"{g.ClockMhz.Value:0}MHz · {Short(g.Name, 40)}"
            : Short(g.Name, 46);

        m.Tip = g.Name
              + (g.ClockMhz.HasValue ? $"\n核心频率：{g.ClockMhz.Value:0} MHz" : "")
              + (hasMem ? $"\n显存：{g.MemUsedGb ?? 0:0.0} / {g.MemTotalGb:0.#} GB" : "")
              + (g.Temp.HasValue ? $"\n温度：{g.Temp.Value:0}°C" : "")
              + (g.Power.HasValue ? $"\n功耗：{g.Power.Value:0} W" : "");

        if (_cfg.ShowSparkline)
        {
            m.Spark = RingFor(cc.Id).Snapshot();
            m.SparkMax = 100;
        }
        return m;
    }

    private CardModel BuildMem(CardConfig cc, Snapshot s)
    {
        var th = _cfg.Thresholds;
        var m = new CardModel { Title = TitleOf(cc), Accent = Palette.Mem };
        m.Value = s.MemUsedPct.ToString("0");
        m.Unit = "%";
        m.Progress = s.MemUsedPct / 100.0;
        m.Severity = Sev(s.MemUsedPct, th.LoadWarn, th.LoadCrit);
        // 右上角：可用物理内存；左下角：分页 / 非分页缓冲池（内核占用的那部分）
        m.Chip = s.MemAvailGb > 0 ? $"可用 {s.MemAvailGb:0.0}G" : null;
        m.Footer = s.PoolPagedMb > 0
            ? $"分页 {s.PoolPagedMb:0}M · 非分页 {s.PoolNonpagedMb:0}M"
            : $"{s.MemUsedGb:0.0} / {s.MemTotalGb:0.0} GB";
        return m;
    }

    private CardModel BuildNet(CardConfig cc, Snapshot s)
    {
        var th = _cfg.Thresholds;
        var m = new CardModel { Title = TitleOf(cc), Accent = Palette.Net };
        var n = s.Net;

        if (n == null || !n.HasData)
        {
            m.Value = "—";
            m.Unit = "ms";
            m.Footer = "探测中…";
            return m;
        }

        if (!n.LinkUp)
        {
            m.Value = "断网";
            m.Unit = "";
            m.Chip = "链路断开";
            m.ChipSeverity = Severity.Critical;
            m.Severity = Severity.Critical;
            m.Footer = "网卡未连接";
            return m;
        }

        m.Value = n.LatencyMs.HasValue ? n.LatencyMs.Value.ToString("0") : "—";
        m.Unit = "ms";

        var sev = Severity.Normal;
        if (n.LatencyMs.HasValue) sev = Sev(n.LatencyMs.Value, th.LatencyWarn, th.LatencyCrit);
        var sevLoss = Sev(n.LossPct, th.LossWarn, th.LossCrit);
        if (n.LatencyMs == null) sevLoss = Severity.Critical;
        m.Severity = (Severity)Math.Max((int)sev, (int)sevLoss);

        m.Chip = n.IcmpBlocked ? "ICMP受限" : $"丢包 {n.LossPct:0.#}%";
        m.ChipSeverity = n.IcmpBlocked ? Severity.Normal : sevLoss;

        // 上下行已经在“网络速率”卡片里，这里只留延迟相关指标
        m.Footer = $"抖动 {n.JitterMs:0.0}ms · 平均 {n.AvgMs:0}ms";

        if (_cfg.ShowSparkline)
        {
            double max = Math.Max(40, n.AvgMs * 1.8);
            m.Spark = n.History;
            m.SparkMax = max;
            m.SparkLoss = true;
        }
        return m;
    }

    private CardModel BuildDisk(CardConfig cc, Snapshot s)
    {
        var th = _cfg.Thresholds;
        var d = s.Disks.FirstOrDefault(x => x.Key == cc.Target) ?? s.Disks.FirstOrDefault();
        var m = new CardModel { Title = TitleOf(cc), Accent = Palette.Disk };

        if (d == null)
        {
            m.Value = "无数据";
            m.Unit = "";
            m.ValueScale = 0.58;
            m.Footer = "硬盘未找到（可能需要管理员权限）";
            return m;
        }

        if (d.Temp.HasValue)
        {
            m.Value = d.Temp.Value.ToString("0");
            m.Severity = Sev(d.Temp.Value, th.DiskTempWarn, th.DiskTempCrit);
        }
        else
        {
            m.Value = "—";
            m.Chip = "无温度传感器";
            m.ChipSeverity = Severity.Warn;
        }
        m.Unit = "°C";

        if (d.Activity.HasValue && m.Chip == null)
        {
            m.Chip = $"活动 {d.Activity.Value:0}%";
            m.ChipSeverity = Severity.Normal;
        }

        m.Progress = d.UsedPct.HasValue ? d.UsedPct.Value / 100.0 : -1;
        m.Footer = d.UsedPct.HasValue ? $"已用 {d.UsedPct.Value:0}%" : Short(d.Name, 24);
        return m;
    }


    // ---------- 新增监控项 ----------

    private static (string value, string unit) RateParts(double bps)
    {
        if (bps >= 1024 * 1024) return ((bps / 1048576.0).ToString(bps >= 10485760 ? "0" : "0.0"), "MB/s");
        if (bps >= 1024) return ((bps / 1024.0).ToString("0"), "KB/s");
        return (bps.ToString("0"), "B/s");
    }

    /// <summary>
    /// 网络速率卡左下角：IPv4/前缀 · 网关（放不下的一律进悬停提示）。
    /// 没有 IPv4 时退回 IPv6；两者都没有就报网卡名。
    /// </summary>
    private static string LocalNetLine(Core.NetStats n)
    {
        var parts = new List<string>(3);

        if (n.Ipv4.Length > 0)
            parts.Add(n.Ipv4Prefix > 0 ? $"{n.Ipv4}/{n.Ipv4Prefix}" : n.Ipv4);
        else if (n.Ipv6.Length > 0)
            parts.Add(Short(n.Ipv6, 22));

        if (n.Gateway.Length > 0) parts.Add("网关 " + n.Gateway);
        else if (n.Ipv4Mask.Length > 0) parts.Add("掩码 " + n.Ipv4Mask);

        if (parts.Count == 0) return Short(n.IfName, 20);
        return string.Join(" · ", parts);
    }

    /// <summary>网络速率卡的悬停提示：一行放不下的完整网络参数。</summary>
    private static string LocalNetTip(Core.NetStats n)
    {
        var sb = new System.Text.StringBuilder();
        if (n.IfName.Length > 0) sb.Append("网卡：").Append(n.IfName).Append('\n');
        if (n.Ipv4.Length > 0) sb.Append("IPv4：").Append(n.Ipv4);
        if (n.Ipv4Prefix > 0) sb.Append('/').Append(n.Ipv4Prefix);
        if (n.Ipv4Mask.Length > 0) sb.Append("（掩码 ").Append(n.Ipv4Mask).Append('）');
        sb.Append('\n');
        if (n.Ipv6.Length > 0) sb.Append("IPv6：").Append(n.Ipv6).Append('\n');
        if (n.Gateway.Length > 0) sb.Append("网关：").Append(n.Gateway).Append('\n');
        if (n.Dns.Length > 0) sb.Append("DNS：").Append(n.Dns).Append('\n');
        if (n.Mtu > 0) sb.Append("MTU：").Append(n.Mtu).Append('\n');
        if (n.LinkSpeed.Length > 0) sb.Append("链路速率：").Append(n.LinkSpeed);
        return sb.ToString().TrimEnd();
    }

    private static string ShortRate(double bps)
    {
        if (bps >= 1024 * 1024) return (bps / 1048576.0).ToString("0.0") + "M/s";
        if (bps >= 1024) return (bps / 1024.0).ToString("0") + "K/s";
        return bps.ToString("0") + "B/s";
    }

    /// <summary>网络速率：大字显示下行，角标显示上行，曲线是下行历史。</summary>
    private CardModel BuildNetRate(CardConfig cc, Snapshot s)
    {
        var m = new CardModel { Title = TitleOf(cc), Accent = Palette.NetRate };
        var n = s.Net;
        if (n == null || !n.HasData)
        {
            m.Value = "—";
            m.Unit = "MB/s";
            m.Footer = "探测中…";
            return m;
        }

        // 大字显示“当前方向”，右上角显示另一个方向的上传/下行速率；
        // 点右上角角标可以切换哪个方向占大字（选择记在 Target 里）。
        bool showUp = string.Equals(cc.Dir, "up", StringComparison.OrdinalIgnoreCase);
        double main = showUp ? n.UpBps : n.DownBps;
        double other = showUp ? n.DownBps : n.UpBps;

        var (mv, mu) = RateParts(main);
        m.Value = mv;
        m.Unit = mu;

        if (n.LinkUp)
        {
            m.Chip = (showUp ? "↓ " : "↑ ") + ShortRate(other);
            m.ChipSeverity = Severity.Normal;
            m.ChipClick = () => ToggleNetRateDirection(cc);
            m.Footer = LocalNetLine(n);
            m.Tip = LocalNetTip(n) + "\n（点右上角切换上下行）";
        }
        else
        {
            m.Chip = "链路断开";
            m.ChipSeverity = Severity.Critical;
            m.Footer = "网卡未连接";
            m.Severity = Severity.Warn;
        }

        if (_cfg.ShowSparkline)
        {
            m.Spark = RingFor(cc.Id).Snapshot();
            m.SparkMax = 0;
        }
        return m;
    }

    private sealed class IoAccum
    {
        public double Read;
        public double Write;
        public DateTime At;
    }

    private readonly Dictionary<string, IoAccum> _ioAccum = new();

    /// <summary>按两次采样之间的时间把速率积分成本次运行以来的累计读写量。</summary>
    private (double read, double write) AccumulateIo(string key, double readBps, double writeBps, DateTime now)
    {
        if (!_ioAccum.TryGetValue(key, out var a))
        {
            a = new IoAccum { At = now };
            _ioAccum[key] = a;
        }

        double dt = (now - a.At).TotalSeconds;
        if (dt is > 0 and < 30)      // 卡片被隐藏/恢复时时间差会很大，跳过这一段
        {
            a.Read += Math.Max(0, readBps) * dt;
            a.Write += Math.Max(0, writeBps) * dt;
        }
        a.At = now;
        return (a.Read, a.Write);
    }

    /// <summary>字节数转成易读单位（累计读写量用）。</summary>
    private static string Size(double bytes)
    {
        if (bytes >= 1024.0 * 1024 * 1024 * 1024) return (bytes / (1024.0 * 1024 * 1024 * 1024)).ToString("0.0") + "TB";
        if (bytes >= 1024.0 * 1024 * 1024) return (bytes / (1024.0 * 1024 * 1024)).ToString("0.0") + "G";
        if (bytes >= 1024.0 * 1024) return (bytes / (1024.0 * 1024)).ToString("0") + "M";
        if (bytes >= 1024) return (bytes / 1024.0).ToString("0") + "K";
        return bytes.ToString("0") + "B";
    }

    /// <summary>磁盘读写卡：点右上角角标，切换大字显示读取还是写入。</summary>
    private void ToggleDiskIoDirection(CardConfig cc)
    {
        bool showWrite = string.Equals(cc.Dir, "write", StringComparison.OrdinalIgnoreCase);
        cc.Dir = showWrite ? "read" : "write";

        var snapNow = _snap;
        if (snapNow != null)
        {
            var host = _cards.FirstOrDefault(c => c.Config.Id == cc.Id);
            if (host != null) host.Apply(Build(cc, snapNow));
        }
        _cfg.Save();
    }

    /// <summary>网络速率卡：点右上角角标，切换大字显示上行还是下行。</summary>
    private void ToggleNetRateDirection(CardConfig cc)
    {
        bool showUp = string.Equals(cc.Dir, "up", StringComparison.OrdinalIgnoreCase);
        cc.Dir = showUp ? "down" : "up";

        var snapNow = _snap;
        if (snapNow != null)
        {
            var host = _cards.FirstOrDefault(c => c.Config.Id == cc.Id);
            if (host != null) host.Apply(Build(cc, snapNow));
        }
        _cfg.Save();
    }

    /// <summary>CPU 温度：温度作为主数值，负载放在角标。</summary>
    private CardModel BuildCpuTemp(CardConfig cc, Snapshot s)
    {
        var th = _cfg.Thresholds;
        var m = new CardModel { Title = TitleOf(cc), Accent = Palette.Temp };
        var c = s.Cpu;
        m.Unit = "°C";

        if (c == null)
        {
            m.Value = "—";
            m.Footer = "未检测到 CPU 传感器";
            return m;
        }

        if (c.Temp.HasValue)
        {
            m.Value = c.Temp.Value.ToString("0");
            m.Severity = Sev(c.Temp.Value, th.TempWarn, th.TempCrit);
        }
        else
        {
            m.Value = "无数据";
            m.Unit = "";
            m.ValueScale = 0.58;
            m.Chip = s.Elevated ? "无温度传感器" : "需管理员";
            m.ChipSeverity = s.Elevated ? Severity.Normal : Severity.Warn;
        }

        if (m.Chip == null && c.Load.HasValue)
            m.Chip = $"负载 {c.Load.Value:0}%";

        m.Footer = c.MaxClockMhz.HasValue
            ? $"{Short(c.Name, 16)} · {c.MaxClockMhz.Value / 1000.0:0.0}GHz"
            : Short(c.Name, 22);

        if (_cfg.ShowSparkline)
        {
            m.Spark = RingFor(cc.Id).Snapshot();
            m.SparkMax = 0;
        }
        return m;
    }

    /// <summary>磁盘读写：大字显示读取速率，角标显示写入速率。</summary>
    private CardModel BuildDiskIo(CardConfig cc, Snapshot s)
    {
        var d = s.Disks.FirstOrDefault(x => x.Key == cc.Target) ?? s.Disks.FirstOrDefault();
        var m = new CardModel { Title = TitleOf(cc), Accent = Palette.DiskIo };

        if (d == null)
        {
            m.Value = "无数据";
            m.Unit = "";
            m.ValueScale = 0.58;
            m.Footer = "硬盘未找到（可能需要管理员权限）";
            return m;
        }

        // 与网络速率卡同样的交互：大字是当前方向，右上角显示另一个方向，点角标切换
        bool showWrite = string.Equals(cc.Dir, "write", StringComparison.OrdinalIgnoreCase);
        double mainBps = showWrite ? d.WriteBps ?? 0 : d.ReadBps ?? 0;
        double otherBps = showWrite ? d.ReadBps ?? 0 : d.WriteBps ?? 0;

        var (rv, ru) = RateParts(mainBps);
        m.Value = rv;
        m.Unit = ru;
        m.Chip = (showWrite ? "读 " : "写 ") + ShortRate(otherBps);
        m.ChipSeverity = Severity.Normal;
        m.ChipClick = () => ToggleDiskIoDirection(cc);

        // 左下角：优先硬盘健康度（NVMe 剩余寿命）；没有健康度传感器就显示累计读写
        var (totalRead, totalWrite) = AccumulateIo(cc.Id, d.ReadBps ?? 0, d.WriteBps ?? 0, s.Now);
        if (d.HealthPct.HasValue)
            m.Footer = $"健康度 {d.HealthPct.Value:0}% · 读 {Size(totalRead)}";
        else if (totalRead + totalWrite > 0)
            m.Footer = $"读 {Size(totalRead)} · 写 {Size(totalWrite)}";
        else
            m.Footer = Short(d.Name, 24);

        m.Tip = d.Name
              + (d.HealthPct.HasValue ? $"\n健康度：{d.HealthPct.Value:0}%" : "")
              + $"\n本次运行累计：读 {Size(totalRead)} / 写 {Size(totalWrite)}"
              + (d.Activity.HasValue ? $"\n活动：{d.Activity.Value:0}%" : "")
              + (d.UsedPct.HasValue ? $"\n已用空间：{d.UsedPct.Value:0}%" : "")
              + (d.Temp.HasValue ? $"\n温度：{d.Temp.Value:0}°C" : "")
              + "\n（点右上角切换读/写）";

        if (_cfg.ShowSparkline)
        {
            m.Spark = RingFor(cc.Id).Snapshot();
            m.SparkMax = 0;
        }
        return m;
    }

    /// <summary>风扇转速。</summary>
    private CardModel BuildFan(CardConfig cc, Snapshot s)
    {
        var f = s.Fans.FirstOrDefault(x => x.Key == cc.Target) ?? s.Fans.FirstOrDefault();
        var m = new CardModel { Title = TitleOf(cc), Accent = Palette.Fan };

        if (f == null)
        {
            m.Value = "无数据";
            m.Unit = "";
            m.ValueScale = 0.58;
            m.Chip = "无风扇传感器";
            m.Footer = s.Elevated ? "该设备未暴露风扇" : "需管理员权限";
            return m;
        }

        if (f.Rpm.HasValue)
        {
            m.Value = f.Rpm.Value.ToString("0");
            m.Unit = "RPM";
            if (f.Pct.HasValue) m.Chip = $"{f.Pct.Value:0}%";
        }
        else if (f.Pct.HasValue)
        {
            m.Value = f.Pct.Value.ToString("0");
            m.Unit = "%";
            m.Chip = "转速占比";
        }
        else
        {
            m.Value = "—";
            m.Unit = "RPM";
        }

        m.Footer = Short(f.Name, 12) + " · " + Short(f.Host, 16);

        if (_cfg.ShowSparkline && f.Rpm.HasValue)
        {
            m.Spark = RingFor(cc.Id).Snapshot();
            m.SparkMax = 0;
        }
        return m;
    }

    /// <summary>进程占用排行。</summary>
    private CardModel BuildProc(CardConfig cc, Snapshot s)
    {
        var m = new CardModel { Title = TitleOf(cc), Accent = Palette.Proc };
        var sort = SortFromTarget(cc.Target);

        if (s.Procs.Count == 0)
        {
            m.Value = "—";
            m.Unit = "";
            m.Chip = SortName(sort) + "  ▸";
            m.Footer = "采样中…";
            return m;
        }

        var rows = new List<(string, string)>(s.Procs.Count);
        foreach (var p in s.Procs)
        {
            string right = sort switch
            {
                ProcSort.Memory => p.MemMb >= 1024
                    ? (p.MemMb / 1024).ToString("0.0") + "G"
                    : p.MemMb.ToString("0") + "M",
                ProcSort.GpuIntegrated => p.GpuIg.ToString("0.0") + "%",
                ProcSort.GpuDiscrete => p.GpuDg.ToString("0.0") + "%",
                _ => p.CpuPct.ToString("0.0") + "%"
            };
            rows.Add((Short(p.Name, 13), right));
        }

        m.Rows = rows;
        // 角标就是排序维度，点一下换一种；底部只留进程总数，不再重复“按占用排行”
        m.Chip = SortName(sort) + "  ▸";
        m.ChipClick = () => CycleProcSort(cc);
        m.Footer = $"共 {s.ProcessCount} 个进程";
        return m;
    }

    /// <summary>天气卡片：温度为主，角标是天气现象，底部是城市与今日高低温。</summary>
    private CardModel BuildWeather(CardConfig cc, Snapshot s)
    {
        var m = new CardModel { Title = TitleOf(cc), Accent = Palette.Weather, Unit = "℃" };
        var w = _weather.Latest;

        if (w == null)
        {
            m.Value = "获取中";
            m.Unit = "";
            m.ValueScale = 0.58;
            m.Footer = "正在获取天气…";
            return m;
        }

        if (!w.HasData)
        {
            // 失败时不做整卡告警染色，只在角标与说明里提示，避免大号破折号变成一条彩色横杠
            m.Value = "无数据";
            m.Unit = "";
            m.ValueScale = 0.58;
            m.Chip = w.Mode == "auto" ? "定位失败" : "待设置";
            m.ChipSeverity = Severity.Warn;
            m.Footer = w.Error ?? "暂无天气数据";
            return m;
        }

        m.Value = w.TempC.ToString("0");
        m.Chip = $"{w.TempC:0}℃ {w.Condition}";
        m.WeatherCode = w.Code;
        m.Footer = $"{Short(w.City, 12)} · {w.HiC:0}℃/{w.LoC:0}℃ · {w.Updated:HH:mm}";

        // 多日预报：有数据就带上，是否展开由卡片尺寸决定（见 CardHost.Apply）
        if (w.Days.Count >= 3) m.Days = w.Days;
        return m;
    }

    /// <summary>
    /// 天气与定位：首次需要时在界面上征询许可。拒绝则降级为手动城市，
    /// 不会重复弹窗，也不会影响其他监控项。
    /// </summary>
    private void EnsureWeather()
    {
        bool hasCard = _cfg.Cards.Any(c => c.Kind == CardKind.Weather);
        if (!hasCard)
        {
            _weather.Stop();
            return;
        }

        if (_cfg.WeatherGeoConsent == null &&
            string.Equals(_cfg.WeatherMode, "auto", StringComparison.OrdinalIgnoreCase))
        {
            bool ok = AskWindow.Ask(this,
                "允许获取大致位置以显示天气？",
                "天气卡片需要知道所在城市。允许后，本机会通过 IP 归属地获取城市与经纬度，" +
                "仅用于查询天气，不保存、不上传其他任何信息。\n你也可以拒绝，然后在“设置 → 天气”里手动填写城市。",
                "允许", "拒绝");

            _cfg.WeatherGeoConsent = ok;
            if (!ok) _cfg.WeatherMode = "manual";
            _cfg.Save();
        }

        string key = $"{_cfg.WeatherMode}|{_cfg.WeatherCity}|{_cfg.WeatherGeoConsent}";
        if (key == _weatherKey) return;

        _weatherKey = key;
        _weather.Start();
    }

    /// <summary>电池：电量为主数值，充电状态在角标，剩余时间在底部。</summary>
    private CardModel BuildBattery(CardConfig cc, Snapshot s)
    {
        var m = new CardModel { Title = TitleOf(cc), Accent = Palette.Battery };

        if (!s.HasBattery)
        {
            m.Value = "无电池";
            m.Unit = "";
            m.ValueScale = 0.58;
            m.Chip = "台式机";
            m.Footer = "未检测到电池";
            return m;
        }

        m.Value = s.BatteryPercent.ToString();
        m.Unit = "%";
        m.Progress = s.BatteryPercent / 100.0;

        if (s.BatteryCharging)
            m.Chip = s.BatteryPercent >= 99 ? "已充满" : "充电中";
        else if (s.OnAc)
            m.Chip = s.BatteryPercent >= 99 ? "已充满" : "已接通电源";
        else
            m.Chip = "放电中";

        m.ChipSeverity = !s.OnAc && s.BatteryPercent <= 20 ? Severity.Warn : Severity.Normal;

        // 只有真正靠电池供电时才告警
        if (!s.OnAc)
        {
            if (s.BatteryPercent <= 10) m.Severity = Severity.Critical;
            else if (s.BatteryPercent <= 20) m.Severity = Severity.Warn;
        }

        string life = s.BatteryLifeMinutes > 0
            ? $"剩余 {s.BatteryLifeMinutes / 60}h{s.BatteryLifeMinutes % 60:00}m"
            : (s.BatteryCharging ? "正在充电" : "剩余时间未知");

        m.Footer = life + " · " + (s.OnAc ? "已接通电源" : "使用电池");
        return m;
    }

    /// <summary>显卡温度：温度做大字，负载在角标。核显共用 CPU 封装温度，通常没有独立读数。</summary>
    private CardModel BuildGpuTemp(CardConfig cc, Snapshot s)
    {
        var th = _cfg.Thresholds;
        var g = s.Gpus.FirstOrDefault(x => x.Key == cc.Target)
                ?? s.Gpus.FirstOrDefault(x => x.Integrated == (cc.Kind == CardKind.Igpu));

        var m = new CardModel
        {
            Title = TitleOf(cc),
            Accent = cc.Kind == CardKind.Igpu ? Palette.Igpu : Palette.Temp,
            Unit = "°C"
        };

        if (g == null)
        {
            m.Value = "无数据";
            m.Unit = "";
            m.ValueScale = 0.58;
            m.Footer = "显卡未找到";
            return m;
        }

        bool discrete = cc.Kind == CardKind.GpuTemp;

        if (g.Temp.HasValue)
        {
            m.Value = g.Temp.Value.ToString("0");
            m.Severity = Sev(g.Temp.Value, th.TempWarn, th.TempCrit);

            if (discrete)
            {
                // 右上角：功耗；左下角：核心频率
                m.Chip = g.Power.HasValue ? $"{g.Power.Value:0}W" : null;
                m.Footer = g.ClockMhz.HasValue ? $"核心频率 {g.ClockMhz.Value:0}MHz" : Short(g.Name, 16);
                if (_cfg.ShowSparkline)
                {
                    m.Spark = RingFor(cc.Id).Snapshot();
                    m.SparkMax = 0;
                }
                return m;
            }

            if (g.Load.HasValue) m.Chip = $"负载 {g.Load.Value:0}%";
        }
        else
        {
            m.Value = "无数据";
            m.Unit = "";
            m.ValueScale = 0.58;
            m.Chip = "核显共用 CPU 温度";
            m.ChipSeverity = Severity.Normal;
        }

        // 频率与显存已经在“负载卡”的底部显示过了，
        // 这张是温度专卡，底部只留型号和功耗，别和负载卡撞信息。
        var parts = new List<string> { Short(g.Name, 16) };
        if (g.Power.HasValue) parts.Add($"{g.Power.Value:0}W");
        else if (g.ClockMhz.HasValue) parts.Add($"{g.ClockMhz.Value:0}MHz");
        m.Footer = string.Join(" · ", parts);

        if (_cfg.ShowSparkline && g.Temp.HasValue)
        {
            m.Spark = RingFor(cc.Id).Snapshot();
            m.SparkMax = 0;
        }
        return m;
    }

    /// <summary>
    /// 今日热搜：多平台聚合，AI 领域条目置顶并单独标注来源；
    /// 每行都可以点击，直接打开对应平台的网页。
    /// </summary>
    private CardModel BuildHotSearch(CardConfig cc, Snapshot s)
    {
        var m = new CardModel { Title = TitleOf(cc), Accent = Palette.Hot };
        string platform = string.IsNullOrWhiteSpace(cc.Target) ? HotSearchService.All : cc.Target!;

        string pname = HotSearchService.Platforms.FirstOrDefault(x => x.Key == platform).Name ?? "综合";
        var list = _hot.Get(platform);

        if (list.Count == 0)
        {
            m.Value = "加载中";
            m.Unit = "";
            m.ValueScale = 0.58;
            m.Chip = pname + "  ▸";
            m.ChipSeverity = Severity.Warn;
            m.Footer = _hot.Status;
            m.ChipClick = () =>
            {
                var ps = HotSearchService.Platforms;
                int i0 = Array.FindIndex(ps, x => x.Key == platform);
                if (i0 < 0) i0 = 0;
                cc.Target = ps[(i0 + 1) % ps.Length].Key;
                _cfg.Save();
                var h0 = _cards.FirstOrDefault(c => c.Config.Id == cc.Id);
                if (h0 != null && _snap != null) h0.Apply(Build(cc, _snap));
            };
            return m;
        }

        int want = Math.Clamp(_cfg.HotCount, 3, 12);
        // 卡片矮的时候少显示几行（要扣掉分类标签那一行的高度）
        int fit = Math.Clamp((int)((ActualHeightFor(cc) - 50) / 21), 2, want);
        m.Hots = list.Take(Math.Min(want, fit)).ToList();

        // 点右上角角标就是切换平台：循环切换，不在卡片里铺一排标签
        var plats = HotSearchService.Platforms;
        m.Chip = pname + "  ▸";
        m.ChipClick = () =>
        {
            int idx = Array.FindIndex(plats, x => x.Key == platform);
            if (idx < 0) idx = 0;
            cc.Target = plats[(idx + 1) % plats.Length].Key;
            _cfg.Save();
            var host = _cards.FirstOrDefault(c => c.Config.Id == cc.Id);
            if (host != null && _snap != null) host.Apply(Build(cc, _snap));
        };
        m.ChipSeverity = m.Hots.Any(x => x.IsAi) ? Severity.Warn : Severity.Normal;
        m.Footer = $"{_hot.Updated:HH:mm} 更新 · 共 {list.Count} 条";
        return m;
    }

    /// <summary>取卡片当前实际高度（未渲染时退回配置值）。</summary>
    private double ActualHeightFor(CardConfig cc)
    {
        var host = _cards.FirstOrDefault(c => c.Config.Id == cc.Id);
        if (host != null && host.ActualHeight > 10) return host.ActualHeight;
        return cc.H > 10 ? cc.H : 168;
    }

    private CardModel BuildClock(CardConfig cc, DateTime now)
    {
        var m = new CardModel
        {
            Title = TitleOf(cc),
            Accent = Palette.Clock,
            Centered = true,
            ValueFont = _cfg.ClockFont
        };
        string txt;
        try { txt = now.ToString(_cfg.TimeFormat, CultureInfo.CurrentCulture); }
        catch { txt = now.ToString("HH:mm:ss"); }
        m.Value = txt;
        m.Footer = _cfg.ShowDate ? SafeDate(now) : "";
        return m;
    }

    private string SafeDate(DateTime now)
    {
        try { return now.ToString(_cfg.DateFormat, CultureInfo.CurrentCulture); }
        catch { return now.ToString("yyyy-MM-dd"); }
    }

    private static string Short(string s, int max)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        s = s.Replace("(R)", "").Replace("(TM)", "").Trim();
        return s.Length <= max ? s : s[..max];
    }

    // ================= 菜单 =================

    private static MenuItem Mi(string header, Action? onClick, string? gesture = null)
    {
        var mi = new MenuItem { Header = header };
        if (gesture != null) mi.InputGestureText = gesture;
        if (onClick != null) mi.Click += (_, _) => { try { onClick(); } catch (Exception ex) { Log.Warn("菜单操作异常：" + ex.Message); } };
        return mi;
    }

    private ContextMenu BuildCardMenu(CardConfig cc)
    {
        var cm = new ContextMenu();
        var sizeMenu = new MenuItem { Header = "尺寸" };
        if (_cfg.UniformCards)
        {
            var follow = new MenuItem { Header = "● 跟随自动适配（统一尺寸）" };
            follow.Click += (_, _) => { };
            tailMenu(sizeMenu, follow);
        }
        else
        {
            var back = new MenuItem { Header = "○ 跟随自动适配（统一尺寸）" };
            back.Click += (_, _) =>
            {
                _cfg.UniformCards = true;
                _lastUniform = true;
                foreach (var c in _cards) c.SetEditing(!_cfg.LayoutLocked, false);
                Relayout(true);
                FitHeightToContent(alsoShrink: true);
                _cfg.Save();
            };
            tailMenu(sizeMenu, back);
        }
        sizeMenu.Items.Add(new Separator());

        foreach (var (name, pw, ph) in CardPresets)
        {
            string label = name + $"  {pw:0}×{ph:0}";
            bool cur = !_cfg.UniformCards
                       && Math.Abs(cc.W - pw) < 1 && Math.Abs(cc.H - ph) < 1;
            var mi = new MenuItem { Header = (cur ? "● " : "○ ") + label };
            double w0 = pw, h0 = ph;
            mi.Click += (_, _) => ApplyCardPreset(cc, w0, h0);
            sizeMenu.Items.Add(mi);
        }
        cm.Items.Add(sizeMenu);

        // 进程卡片：排序维度
        if (cc.Kind == CardKind.Proc)
        {
            var sortMenu = new MenuItem { Header = "排序方式" };
            var curSort = SortFromTarget(cc.Target);
            foreach (var (key, name) in ProcSorts)
            {
                var mi = new MenuItem
                {
                    Header = (SortKey(curSort) == key ? "● " : "○ ") + "按" + name + "占用"
                };
                string k = key;
                mi.Click += (_, _) =>
                {
                    cc.Target = k;
                    CycleProcSortRefresh(cc);
                };
                sortMenu.Items.Add(mi);
            }
            cm.Items.Add(sortMenu);
            cm.Items.Add(new Separator());
        }

        // 热搜卡片：切换平台 / 刷新
        if (cc.Kind == CardKind.HotSearch)
        {
            var srcMenu = new MenuItem { Header = "热搜平台" };
            foreach (var (key, name) in HotSearchService.Platforms)
            {
                bool cur = string.Equals(cc.Target ?? HotSearchService.All, key, StringComparison.OrdinalIgnoreCase);
                var mi = new MenuItem { Header = (cur ? "● " : "○ ") + name };
                string k = key;
                mi.Click += (_, _) =>
                {
                    cc.Target = k;
                    var host = _cards.FirstOrDefault(c => c.Config.Id == cc.Id);
                    if (host != null && _snap != null) host.Apply(Build(cc, _snap));
                    _cfg.Save();
                };
                srcMenu.Items.Add(mi);
            }
            srcMenu.Items.Add(new Separator());
            srcMenu.Items.Add(Mi("立即刷新", () =>
            {
                _hot.RefreshNow();
                var host = _cards.FirstOrDefault(c => c.Config.Id == cc.Id);
                if (host != null && _snap != null) host.Apply(Build(cc, _snap));
            }));
            cm.Items.Add(srcMenu);
            cm.Items.Add(new Separator());
        }

        if (!cc.FollowUniformH || cc.ClockH > 0)
            cm.Items.Add(Mi("恢复统一高度", () =>
            {
                cc.FollowUniformH = true;
                cc.ClockH = 0;          // 时钟回到“按内容自动”的高度
                Relayout(true);
                FitHeightToContent(alsoShrink: true);
                _cfg.Save();
            }));

        cm.Items.Add(Mi("重置为默认尺寸", () =>
        {
            var host = _cards.FirstOrDefault(c => c.Config.Id == cc.Id);
            if (host == null) return;
            host.Width = cc.Kind == CardKind.Clock ? 774 : 250;
            host.Height = cc.Kind == CardKind.Clock ? 104 : 152;
            SaveCardLayout(host);
            Relayout(true);
            FitHeightToContent();
        }));
        cm.Items.Add(Mi("移除这一项", () =>
        {
            _cfg.Cards.RemoveAll(c => c.Id == cc.Id);
            _rings.Remove(cc.Id);
            _cfg.Save();
            RebuildCards();
        }));
        cm.Items.Add(new Separator());
        cm.Items.Add(Mi(_cfg.LayoutLocked ? "解锁布局" : "锁定布局", ToggleLock, "Ctrl+Alt+L"));
        cm.Items.Add(BuildThemeMenu());
        var dispMenu = new MenuItem { Header = "显示与窗口" };
        dispMenu.Items.Add(Mi(_maximized ? "还原窗口" : "最大化窗口", ToggleMaximize, "Ctrl+Alt+M"));
        dispMenu.Items.Add(new Separator());
        var secondary = DisplayHelper.Secondary(_displays);
        var toSecondary = Mi(secondary == null ? "移到副屏（未检测到副屏）" : "移到副屏", () => MoveToScreen(true));
        toSecondary.IsEnabled = secondary != null;
        dispMenu.Items.Add(toSecondary);
        dispMenu.Items.Add(Mi("移到主屏", () => MoveToScreen(false)));
        var info = Mi($"已识别 {_displays.Count} 块显示器", null);
        info.IsEnabled = false;
        dispMenu.Items.Add(new Separator());
        dispMenu.Items.Add(info);
        cm.Items.Add(dispMenu);
        cm.Items.Add(Mi("壁纸与外观…", OpenSettings));
        cm.Items.Add(new Separator());
        cm.Items.Add(Mi(_cfg.UniformCards ? "✓ 自动适配卡片大小" : "自动适配卡片大小", ToggleUniformCards));
        cm.Items.Add(Mi("自动排列", () =>
        {
            Relayout(true);
            FitHeightToContent(alsoShrink: true);
        }));
        cm.Items.Add(new Separator());
        cm.Items.Add(Mi("设置…", OpenSettings, "Ctrl+Alt+S"));
        return cm;
    }

    /// <summary>切换“自动适配卡片大小”：统一宽高的网格布局 vs 自由尺寸。</summary>
    private void ToggleUniformCards()
    {
        _cfg.UniformCards = !_cfg.UniformCards;
        foreach (var c in _cards) c.SetEditing(!_cfg.LayoutLocked, true);
        Relayout(true);
        FitHeightToContent(alsoShrink: true);
        _cfg.Save();
    }

    private MenuItem BuildThemeMenu()
    {
        var root = new MenuItem { Header = "主题" };
        foreach (var t in Themes.All)
        {
            string id = t.Id;
            var mi = new MenuItem { Header = (string.Equals(t.Id, _cfg.Theme, StringComparison.OrdinalIgnoreCase) ? "● " : "○ ") + t.Name };
            mi.Click += (_, _) =>
            {
                _cfg.Theme = id;
                ApplyThemeAndWallpaper();
                _cfg.Save();
            };
            root.Items.Add(mi);
        }
        return root;
    }

    private ContextMenu BuildDeckMenu()
    {
        var cm = new ContextMenu();

        var add = new MenuItem { Header = "添加监控项" };
        foreach (var item in AddableItems()) add.Items.Add(item);
        if (add.Items.Count == 0)
        {
            var none = new MenuItem { Header = "（已全部添加）", IsEnabled = false };
            add.Items.Add(none);
        }
        cm.Items.Add(add);

        cm.Items.Add(BuildThemeMenu());
        var dispMenu = new MenuItem { Header = "显示与窗口" };
        dispMenu.Items.Add(Mi(_maximized ? "还原窗口" : "最大化窗口", ToggleMaximize, "Ctrl+Alt+M"));
        dispMenu.Items.Add(new Separator());
        var secondary = DisplayHelper.Secondary(_displays);
        var toSecondary = Mi(secondary == null ? "移到副屏（未检测到副屏）" : "移到副屏", () => MoveToScreen(true));
        toSecondary.IsEnabled = secondary != null;
        dispMenu.Items.Add(toSecondary);
        dispMenu.Items.Add(Mi("移到主屏", () => MoveToScreen(false)));
        var info = Mi($"已识别 {_displays.Count} 块显示器", null);
        info.IsEnabled = false;
        dispMenu.Items.Add(new Separator());
        dispMenu.Items.Add(info);
        cm.Items.Add(dispMenu);
        cm.Items.Add(Mi("壁纸与外观…", OpenSettings));
        cm.Items.Add(new Separator());
        cm.Items.Add(Mi(_cfg.UniformCards ? "✓ 自动适配卡片大小" : "自动适配卡片大小", ToggleUniformCards));
        cm.Items.Add(Mi("自动排列", () =>
        {
            Relayout(true);
            FitHeightToContent(alsoShrink: true);
        }));
        cm.Items.Add(Mi(_cfg.LayoutLocked ? "解锁布局" : "锁定布局", ToggleLock, "Ctrl+Alt+L"));
        cm.Items.Add(new Separator());
        cm.Items.Add(Mi("设置…", OpenSettings, "Ctrl+Alt+S"));
        cm.Items.Add(Mi("传感器诊断…", ShowDiagnostics));
        cm.Items.Add(Mi("重新识别硬件", ReinitSensors));
        if (!Native.IsElevated())
            cm.Items.Add(Mi("以管理员身份重启（读取温度）", RestartElevated));
        cm.Items.Add(new Separator());
        cm.Items.Add(Mi(_cfg.AlwaysOnTop ? "窗口置顶：开" : "窗口置顶：关", () =>
        {
            _cfg.AlwaysOnTop = !_cfg.AlwaysOnTop;
            ApplyChrome();
            _cfg.Save();
        }));
        cm.Items.Add(Mi(_cfg.ClickThrough ? "鼠标穿透：开" : "鼠标穿透：关", () =>
        {
            _cfg.ClickThrough = !_cfg.ClickThrough;
            ApplyClickThrough();
            _cfg.Save();
        }, "Ctrl+Alt+X"));
        cm.Items.Add(Mi(_cfg.AutoStart ? "开机自启：开" : "开机自启：关", () =>
        {
            _cfg.AutoStart = !_cfg.AutoStart;
            AutoStart.Apply(_cfg.AutoStart);
            _cfg.Save();
        }));
        cm.Items.Add(new Separator());
        cm.Items.Add(Mi("退出", Close));
        return cm;
    }

    private IEnumerable<MenuItem> AddableItems()
    {
        if (!_cfg.Cards.Any(c => c.Kind == CardKind.Cpu))
            yield return Mi("CPU 占用与温度", () => AddCard(CardKind.Cpu, null));

        foreach (var g in _sensors.Gpus)
        {
            var kind = g.Integrated ? CardKind.Igpu : CardKind.Gpu;
            if (_cfg.Cards.Any(c => c.Kind == kind && c.Target == g.Key)) continue;
            yield return Mi((g.Integrated ? "核显 · " : "独显 · ") + Short(g.Name, 26),
                () => AddCard(kind, g.Key));
        }

        if (!_cfg.Cards.Any(c => c.Kind == CardKind.Mem))
            yield return Mi("内存占用", () => AddCard(CardKind.Mem, null));

        if (!_cfg.Cards.Any(c => c.Kind == CardKind.Net))
            yield return Mi("网络稳定性", () => AddCard(CardKind.Net, null));

        if (!_cfg.Cards.Any(c => c.Kind == CardKind.Clock))
            yield return Mi("时间", () => AddCard(CardKind.Clock, null));

        if (!_cfg.Cards.Any(c => c.Kind == CardKind.NetRate))
            yield return Mi("网络速率", () => AddCard(CardKind.NetRate, null));

        if (!_cfg.Cards.Any(c => c.Kind == CardKind.CpuTemp))
            yield return Mi("CPU 温度（大字）", () => AddCard(CardKind.CpuTemp, null));

        if (!_cfg.Cards.Any(c => c.Kind == CardKind.Proc))
            yield return Mi("进程占用排行", () => AddCard(CardKind.Proc, null));

        if (!_cfg.Cards.Any(c => c.Kind == CardKind.Weather))
            yield return Mi("天气", () => AddCard(CardKind.Weather, null));

        if (!_cfg.Cards.Any(c => c.Kind == CardKind.Battery))
            yield return Mi("电池电量", () => AddCard(CardKind.Battery, null));

        if (!_cfg.Cards.Any(c => c.Kind == CardKind.HotSearch))
            yield return Mi("今日热搜", () => AddCard(CardKind.HotSearch, null));

        foreach (var g in _sensors.Gpus)
        {
            if (_cfg.Cards.Any(c => c.Kind == CardKind.GpuTemp && c.Target == g.Key)) continue;
            yield return Mi("显卡温度 · " + Short(g.Name, 24), () => AddCard(CardKind.GpuTemp, g.Key));
        }

        foreach (var d in _sensors.Disks)
        {
            if (_cfg.Cards.Any(c => c.Kind == CardKind.DiskIo && c.Target == d.Key)) continue;
            yield return Mi("磁盘读写 · " + Short(d.Name, 24), () => AddCard(CardKind.DiskIo, d.Key));
        }

        foreach (var f in _sensors.Fans)
        {
            if (_cfg.Cards.Any(c => c.Kind == CardKind.Fan && c.Target == f.Key)) continue;
            yield return Mi("风扇 · " + Short(f.Name, 20), () => AddCard(CardKind.Fan, f.Key));
        }

        foreach (var d in _sensors.Disks)
        {
            if (_cfg.Cards.Any(c => c.Kind == CardKind.Disk && c.Target == d.Key)) continue;
            yield return Mi("硬盘 · " + Short(d.Name, 26), () => AddCard(CardKind.Disk, d.Key));
        }
    }

    private void AddCard(CardKind kind, string? target)
    {
        var cc = new CardConfig
        {
            Kind = kind,
            Target = target,
            W = kind == CardKind.Clock ? 774 : 250,
            H = kind == CardKind.Clock ? 128 : kind is CardKind.Proc or CardKind.HotSearch ? 200 : 152,
            X = Pad,
            Y = Pad
        };
        if (kind == CardKind.Disk && target != null)
        {
            var d = _sensors.Disks.FirstOrDefault(x => x.Key == target);
            if (d != null) cc.Title = Short(d.Name, 18);
        }
        else if (kind == CardKind.DiskIo && target != null)
        {
            var d = _sensors.Disks.FirstOrDefault(x => x.Key == target);
            cc.Title = d != null ? "读写 · " + Short(d.Name, 12) : "磁盘读写";
        }
        else if (kind == CardKind.Fan && target != null)
        {
            var f = _sensors.Fans.FirstOrDefault(x => x.Key == target);
            cc.Title = f != null ? Short(f.Name, 14) : "风扇";
        }
        _cfg.Cards.Add(cc);
        RebuildCards();
        Relayout(false);
        FitHeightToContent();
    }

    private void ShowAddMenu(UIElement anchor)
    {
        var menu = new ContextMenu();
        foreach (var item in AddableItems()) menu.Items.Add(item);
        if (menu.Items.Count == 0) menu.Items.Add(new MenuItem { Header = "（已全部添加）", IsEnabled = false });
        menu.PlacementTarget = anchor;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private void ReinitSensors()    {
        _cts?.Cancel();
        _sensors.Dispose();
        _ = Task.Run(() =>
        {
            _sensors.Initialize();
            Dispatcher.InvokeAsync(() =>
            {
                StartLoops();
                Deck.ContextMenu = BuildDeckMenu();
            });
        });
    }

    /// <summary>以管理员身份重启自身（读取 CPU / 硬盘温度必需）。</summary>
    private void RestartElevated()
    {
        try
        {
            string? exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) return;
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = exe,
                UseShellExecute = true,
                Verb = "runas"
            };
            System.Diagnostics.Process.Start(psi);
            Close();
        }
        catch (Exception ex)
        {
            Log.Warn("以管理员身份重启失败：" + ex.Message);
        }
    }

    private void ShowDiagnostics()    {
        var win = new DiagnosticsWindow(_sensors)
        {
            Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        win.Show();
    }

    // ================= 关闭 =================

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        try
        {
            SaveBounds();
            _cfg.Save();
            _cts?.Cancel();
            _net.Dispose();
            _weather.Dispose();
            _hot.Dispose();
            _gpuUsage.Dispose();
            _sensors.Dispose();
            Native.UnregisterHotKey(_hwnd, HotkeyClickThrough);
            Native.UnregisterHotKey(_hwnd, HotkeySettings);
            Native.UnregisterHotKey(_hwnd, HotkeyLock);
            Native.UnregisterHotKey(_hwnd, HotkeyMaximize);
        }
        catch { }
        Dispatcher.BeginInvoke(new Action(() => Application.Current.Shutdown()));
    }
}
