using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;

namespace PerfDeck.UI;

public static class Palette
{
    public static readonly Color Bg = Color.FromRgb(0x0A, 0x0E, 0x13);
    public static readonly Color CardTop = Color.FromRgb(0x15, 0x1D, 0x27);
    public static readonly Color CardBottom = Color.FromRgb(0x0E, 0x14, 0x1B);
    public static readonly Color Border = Color.FromRgb(0x1E, 0x27, 0x33);
    public static readonly Color TextPrimary = Color.FromRgb(0xE9, 0xF1, 0xFA);
    public static readonly Color TextSecondary = Color.FromRgb(0x93, 0xA4, 0xB8);
    public static readonly Color TextTertiary = Color.FromRgb(0x5D, 0x6B, 0x7C);

    public static readonly Color Cpu = Color.FromRgb(0x4C, 0xC9, 0xF0);
    public static readonly Color Gpu = Color.FromRgb(0xB3, 0x88, 0xFF);
    public static readonly Color Igpu = Color.FromRgb(0x4A, 0xDE, 0x80);
    public static readonly Color Mem = Color.FromRgb(0xF4, 0x72, 0xB6);
    public static readonly Color Net = Color.FromRgb(0x60, 0xA5, 0xFA);
    public static readonly Color Disk = Color.FromRgb(0xFB, 0xBF, 0x24);
    public static readonly Color Clock = Color.FromRgb(0x94, 0xA3, 0xB8);
    public static readonly Color NetRate = Color.FromRgb(0x38, 0xBD, 0xF8);
    public static readonly Color Temp = Color.FromRgb(0xFB, 0x92, 0x3C);
    public static readonly Color DiskIo = Color.FromRgb(0xF5, 0x9E, 0x0B);
    public static readonly Color Proc = Color.FromRgb(0x2D, 0xD4, 0xBF);
    public static readonly Color Fan = Color.FromRgb(0x8E, 0xE0, 0x8A);
    public static readonly Color Weather = Color.FromRgb(0xA5, 0xB4, 0xFC);
    public static readonly Color Battery = Color.FromRgb(0x4A, 0xDE, 0x80);
    public static readonly Color Hot = Color.FromRgb(0xFF, 0x6B, 0x6B);

    public static readonly Color Warn = Color.FromRgb(0xFB, 0xBF, 0x24);
    public static readonly Color Crit = Color.FromRgb(0xF8, 0x71, 0x71);
    public static readonly Color Ok = Color.FromRgb(0x4A, 0xDE, 0x80);

    private static readonly System.Collections.Generic.Dictionary<Color, SolidColorBrush> Cache = new();

    public static SolidColorBrush Brush(Color c)
    {
        lock (Cache)
        {
            if (Cache.TryGetValue(c, out var b)) return b;
            b = new SolidColorBrush(c);
            b.Freeze();
            Cache[c] = b;
            return b;
        }
    }

    public static Color AccentFor(Core.CardKind kind) => kind switch
    {
        Core.CardKind.Cpu => Cpu,
        Core.CardKind.Gpu => Gpu,
        Core.CardKind.Igpu => Igpu,
        Core.CardKind.Mem => Mem,
        Core.CardKind.Net => Net,
        Core.CardKind.Disk => Disk,
        Core.CardKind.NetRate => NetRate,
        Core.CardKind.CpuTemp => Temp,
        Core.CardKind.DiskIo => DiskIo,
        Core.CardKind.Proc => Proc,
        Core.CardKind.Fan => Fan,
        Core.CardKind.Weather => Weather,
        Core.CardKind.Battery => Battery,
        Core.CardKind.GpuTemp => Temp,
        _ => Clock
    };
}

public enum Severity { Normal, Warn, Critical }

public sealed class CardModel
{
    public string Title = "";
    public string? Chip;
    public string Value = "—";
    public string Unit = "";
    /// <summary>主数值字号缩放（空数据时缩小，避免破折号变成横杠）</summary>
    public double ValueScale = 1.0;
    /// <summary>主数值字号（DIP）。由主窗口按统一基准给，保证所有卡片数值一样大；0 = 按卡片高度自适应</summary>
    public double ValueSize;
    /// <summary>数值下方的第二行（上传/写入速率、副指标）；空则不显示</summary>
    public string? Sub;
    /// <summary>鼠标悬停提示（放不进一行的补充信息，如 IPv6 / 掩码 / DNS）</summary>
    public string? Tip;
    public string Footer = "";
    /// <summary>0..1，负数表示隐藏进度条</summary>
    public double Progress = -1;
    public Severity Severity = Severity.Normal;
    public Severity ChipSeverity = Severity.Normal;
    public Color Accent = Palette.Cpu;
    public double[]? Spark;
    /// <summary>0 表示按数据自动缩放</summary>
    public double SparkMax;
    /// <summary>曲线中标记丢包（仅网络）</summary>
    public bool SparkLoss;
    /// <summary>底部说明文字右对齐</summary>
    public bool FooterRight;
    /// <summary>数值、标题、说明整体居中（时钟卡片用）</summary>
    public bool Centered;
    /// <summary>主数值字体（时钟卡片可换字体），空 = 跟随主题字体</summary>
    public string? ValueFont;
    /// <summary>排行榜内容（进程卡片），非空时替代主数值</summary>
    public List<(string Left, string Right)>? Rows;
    /// <summary>热搜条目（可点击跳转），非空时替代主数值</summary>
    public List<PerfDeck.Core.HotItem>? Hots;
    /// <summary>多日天气（含今天），尺寸够时替代大号温度</summary>
    public List<PerfDeck.Core.DayForecast>? Days;
    /// <summary>热搜分类标签（卡片内可直接点击切换）</summary>
    public List<(string Key, string Name)>? HotTabs;
    /// <summary>当前选中的分类 key</summary>
    public string? HotActive;
    /// <summary>点击分类标签后的回调</summary>
    public Action<string>? HotTabSwitch;
    /// <summary>点击右上角角标的回调（热搜用来循环切换平台）</summary>
    public Action? ChipClick;
    /// <summary>天气图标代码，<0 表示不显示</summary>
    public int WeatherCode = -1;
}
