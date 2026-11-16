using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PerfDeck.Core;

public enum CardKind
{
    Cpu, Gpu, Igpu, Mem, Net, Disk, Clock,
    /// <summary>网络上下行速率</summary>
    NetRate,
    /// <summary>CPU 温度（温度为大字）</summary>
    CpuTemp,
    /// <summary>磁盘读写速率</summary>
    DiskIo,
    /// <summary>进程占用排行</summary>
    Proc,
    /// <summary>风扇转速</summary>
    Fan,
    /// <summary>天气</summary>
    Weather,
    /// <summary>电池电量</summary>
    Battery,
    /// <summary>显卡温度（温度为大字）</summary>
    GpuTemp,
    /// <summary>今日热搜（多平台聚合）</summary>
    HotSearch
}

public sealed class CardConfig
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N").Substring(0, 8);
    public CardKind Kind { get; set; }
    /// <summary>硬件标识（GPU 名称 / 硬盘型号）。CPU、内存、网络、时钟为空。</summary>
    public string? Target { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
    public double W { get; set; } = 252;
    public double H { get; set; } = 156;
    public bool Visible { get; set; } = true;
    /// <summary>高度是否跟随“自动适配”的统一高度；拖动过手柄就变 false</summary>
    public bool FollowUniformH { get; set; } = true;
    /// <summary>时钟卡单独记住的高度（0 = 按内容自动）；时钟行高不跟网格统一高度，必须单独存</summary>
    public double ClockH { get; set; }
    /// <summary>双指标卡片的显示方向（网络速率 up/down，磁盘读写 read/write）</summary>
    public string? Dir { get; set; }
    public string? Accent { get; set; }
    public string? Title { get; set; }
}

public sealed class Thresholds
{
    public double LoadWarn { get; set; } = 85;
    public double LoadCrit { get; set; } = 95;
    public double TempWarn { get; set; } = 80;
    public double TempCrit { get; set; } = 90;
    public double DiskTempWarn { get; set; } = 50;
    public double DiskTempCrit { get; set; } = 60;
    public double LatencyWarn { get; set; } = 100;
    public double LatencyCrit { get; set; } = 250;
    public double LossWarn { get; set; } = 2;
    public double LossCrit { get; set; } = 10;
}

public sealed class AppConfig
{
    public int ConfigVersion { get; set; } = 1;

    // 对外数据接口
    /// <summary>是否开启本地硬件数据接口（127.0.0.1 的 HTTP 服务）</summary>
    public bool ApiEnabled { get; set; } = true;
    /// <summary>接口端口，0 表示关闭</summary>
    public int ApiPort { get; set; } = 17821;
    public int RefreshMs { get; set; } = 1000;

    // 外观
    public string Theme { get; set; } = "obsidian";
    public string? Wallpaper { get; set; }
    public double WallpaperBlur { get; set; } = 12;
    public double WallpaperDim { get; set; } = 0.45;
    public double WallpaperImageOpacity { get; set; } = 1.0;
    /// <summary>卡片背景不透明度，配合壁纸使用</summary>
    public double CardAlpha { get; set; } = 1.0;
    public int ProcCount { get; set; } = 5;

    // 天气
    /// <summary>auto = 自动定位，manual = 手动城市</summary>
    public string WeatherMode { get; set; } = "auto";
    public string? WeatherCity { get; set; }
    /// <summary>null = 尚未询问；true = 允许定位；false = 拒绝</summary>
    public bool? WeatherGeoConsent { get; set; }
    public string TimeFormat { get; set; } = "HH:mm:ss";
    /// <summary>时钟卡片字体，空 = 跟随界面字体</summary>
    public string? ClockFont { get; set; }
    public bool ShowDate { get; set; } = true;
    public string DateFormat { get; set; } = "yyyy年M月d日 dddd";

    public string PingHost { get; set; } = "223.5.5.5";
    public int PingTimeoutMs { get; set; } = 1200;

    public bool LayoutLocked { get; set; }
    public bool AlwaysOnTop { get; set; } = true;
    public bool ClickThrough { get; set; }
    public bool AutoElevate { get; set; } = true;
    public bool ShowSparkline { get; set; } = true;
    public bool ShowInTaskbar { get; set; } = true;
    public bool SnapToGrid { get; set; } = true;

    // 卡片尺寸
    /// <summary>自动适配：所有卡片统一宽高，按最优列数排成整齐网格</summary>
    public bool UniformCards { get; set; } = true;
    /// <summary>统一卡片高度（自动适配模式生效）</summary>
    public double UniformCardH { get; set; } = 168;
    /// <summary>右上角工具栏自动隐藏，鼠标靠近才显示</summary>
    public bool AutoHideToolbar { get; set; } = true;

    /// <summary>卡片投影（立体感）。默认关闭：纯平更利落，也省一层渲染。</summary>
    public bool CardShadow { get; set; }

    // 智能降频
    /// <summary>检测到全屏游戏/重型任务时自动降低刷新频率</summary>
    public bool AdaptiveThrottle { get; set; } = true;
    /// <summary>降频后的刷新间隔</summary>
    public int ThrottleRefreshMs { get; set; } = 5000;

    // 热搜
    /// <summary>热搜卡片显示条数</summary>
    public int HotCount { get; set; } = 5;

    /// <summary>性能挡位：low 省内存 / balanced 均衡 / high 流畅</summary>
    public string PerfProfile { get; set; } = "balanced";
    public bool AutoStart { get; set; }
    public double Opacity { get; set; } = 1.0;
    public double? WinX { get; set; }
    public double? WinY { get; set; }
    public double WinW { get; set; }
    public double WinH { get; set; }

    public Thresholds Thresholds { get; set; } = new();
    public List<CardConfig> Cards { get; set; } = new();

    // ---------- 持久化 ----------

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static string DefaultDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PerfDeck");

    public static string ConfigPath
    {
        get
        {
            // 便携模式：exe 同目录存在 config.json 时优先使用
            try
            {
                string exeDir = AppContext.BaseDirectory;
                string portable = Path.Combine(exeDir, "config.json");
                if (File.Exists(portable)) return portable;
            }
            catch { }
            return Path.Combine(DefaultDir, "config.json");
        }
    }

    public static AppConfig Load()
    {
        try
        {
            string p = ConfigPath;
            if (File.Exists(p))
            {
                var cfg = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(p), JsonOpts);
                if (cfg != null)
                {
                    cfg.Normalize();
                    return cfg;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn("配置读取失败，使用默认配置：" + ex.Message);
        }
        return new AppConfig();
    }

    public void Normalize()
    {
        if (RefreshMs < 200) RefreshMs = 200;
        if (RefreshMs > 60000) RefreshMs = 60000;
        if (PingTimeoutMs < 200) PingTimeoutMs = 200;
        if (Opacity < 0.3) Opacity = 0.3;
        if (Opacity > 1) Opacity = 1;
        if (WallpaperBlur < 0) WallpaperBlur = 0;
        if (WallpaperBlur > 60) WallpaperBlur = 60;
        WallpaperDim = Math.Clamp(WallpaperDim, 0, 0.92);
        WallpaperImageOpacity = Math.Clamp(WallpaperImageOpacity, 0.05, 1);
        CardAlpha = Math.Clamp(CardAlpha, 0.35, 1);
        if (ProcCount < 3) ProcCount = 3;
        if (ProcCount > 10) ProcCount = 10;
        if (string.IsNullOrWhiteSpace(Theme)) Theme = "obsidian";
        if (WeatherMode != "manual") WeatherMode = "auto";
        if (WeatherCity != null) WeatherCity = WeatherCity.Trim();
        UniformCardH = Math.Clamp(UniformCardH, 130, 300);
        if (PerfProfile is not ("low" or "balanced" or "high")) PerfProfile = "balanced";
        if (string.IsNullOrWhiteSpace(TimeFormat)) TimeFormat = "HH:mm:ss";
        if (string.IsNullOrWhiteSpace(DateFormat)) DateFormat = "yyyy年M月d日 dddd";
        if (string.IsNullOrWhiteSpace(PingHost)) PingHost = "223.5.5.5";
        Cards ??= new List<CardConfig>();
        Thresholds ??= new Thresholds();
    }

    public void Save()
    {
        try
        {
            Normalize();
            string p = ConfigPath;
            string? dir = Path.GetDirectoryName(p);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            string tmp = p + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, JsonOpts));
            if (File.Exists(p)) File.Delete(p);
            File.Move(tmp, p);
        }
        catch (Exception ex)
        {
            Log.Warn("配置保存失败：" + ex.Message);
        }
    }
}

public static class Log
{
    private static readonly object Gate = new();
    public static string LogPath => Path.Combine(AppConfig.DefaultDir, "perfdeck.log");

    public static void Write(string level, string msg)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(AppConfig.DefaultDir);
                // 超过 256KB 自动截断，避免长期运行无限增长
                var fi = new FileInfo(LogPath);
                if (fi.Exists && fi.Length > 256 * 1024)
                    File.WriteAllText(LogPath, "");
                File.AppendAllText(LogPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{level}] {msg}{Environment.NewLine}");
            }
        }
        catch { }
    }

    public static void Info(string m) => Write("INF", m);
    public static void Warn(string m) => Write("WRN", m);
    public static void Error(string m) => Write("ERR", m);
}
