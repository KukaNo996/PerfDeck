using System;

namespace PerfDeck.Core;

/// <summary>
/// 性能挡位：把"刷新频率 / 趋势曲线 / 进程条数 / 热搜条数"打包成三档，
/// 让用户按机器性能与在意程度一键切换，不用逐个调。
/// </summary>
public static class PerfProfiles
{
    public sealed record Spec(
        string Key, string Name, string Desc, string MemHint,
        int RefreshMs, bool Sparkline, int ProcCount, int HotCount);

    public static readonly Spec Low = new(
        "low", "省内存", "刷新 3 秒 · 关闭趋势曲线 · 进程 3 条", "预估 150–175 MB",
        3000, false, 3, 3);

    public static readonly Spec Balanced = new(
        "balanced", "均衡", "刷新 1 秒 · 趋势曲线 · 进程 5 条", "预估 200–225 MB",
        1000, true, 5, 5);

    public static readonly Spec High = new(
        "high", "流畅", "刷新 0.5 秒 · 趋势曲线 · 进程 8 条", "预估 230–255 MB",
        500, true, 8, 10);

    public static readonly Spec[] All = { Low, Balanced, High };

    public static Spec Find(string? key) => key switch
    {
        "low" => Low,
        "high" => High,
        _ => Balanced
    };

    /// <summary>写入挡位对应的各项参数。</summary>
    public static void Apply(AppConfig cfg, string? key)
    {
        var s = Find(key);
        cfg.PerfProfile = s.Key;
        cfg.RefreshMs = s.RefreshMs;
        cfg.ShowSparkline = s.Sparkline;
        cfg.ProcCount = s.ProcCount;
        cfg.HotCount = s.HotCount;
    }

    /// <summary>当前参数是否恰好等于某个挡位（用于回显选中态）。</summary>
    public static string? Detect(AppConfig cfg)
    {
        foreach (var s in All)
            if (cfg.RefreshMs == s.RefreshMs && cfg.ShowSparkline == s.Sparkline
                && cfg.ProcCount == s.ProcCount && cfg.HotCount == s.HotCount)
                return s.Key;
        return null;
    }
}
