using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace PerfDeck.Core;

/// <summary>
/// 逐进程 GPU 占用。数据来自 Windows 的 "GPU Engine" 性能计数器，
/// 实例名里带 pid 与适配器 LUID，例如：
///   pid_22064_luid_0x00000000_0x00010CF0_phys_0_eng_0_engtype_3D
///
/// 核显 / 独显通过 "GPU Adapter Memory" 的显存特征区分：
/// 有可观的 Dedicated Usage 的是独显，只有 Shared 的是核显。
/// 计数器对象按实例名缓存复用，避免每次重建（实例常有三四百个）。
/// </summary>
public sealed class GpuUsageSampler : IDisposable
{
    private readonly Dictionary<string, PerformanceCounter> _counters = new();
    private readonly Dictionary<string, bool> _luidIsDiscrete = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _adapterChecked = DateTime.MinValue;
    private bool _available;
    private bool _probed;

    /// <summary>pid → (核显占用%, 独显占用%)</summary>
    public Dictionary<int, (double Integrated, double Discrete)> Usage { get; private set; } = new();

    public bool Available => _available;

    public void Sample()
    {
        if (!EnsureReady()) return;

        var acc = new Dictionary<int, (double ig, double dg)>(64);

        try
        {
            string[] names = PerformanceCounterCategory.GetCategories()
                .FirstOrDefault(c => c.CategoryName == "GPU Engine")?
                .GetInstanceNames() ?? Array.Empty<string>();

            foreach (string name in names)
            {
                if (!TryParse(name, out int pid, out string luid)) continue;

                double v = ReadCounter("GPU Engine", "Utilization Percentage", name);
                if (v <= 0.01) continue;

                bool discrete = _luidIsDiscrete.TryGetValue(luid, out bool d) && d;
                acc.TryGetValue(pid, out var cur);
                acc[pid] = discrete
                    ? (cur.ig, cur.dg + v)
                    : (cur.ig + v, cur.dg);
            }
        }
        catch (Exception ex)
        {
            Log.Warn("GPU 占用采样失败：" + ex.Message);
        }

        Usage = acc;
    }

    private bool EnsureReady()
    {
        if (_probed) return _available;
        _probed = true;

        try
        {
            _available = PerformanceCounterCategory.Exists("GPU Engine");
        }
        catch
        {
            _available = false;
        }

        if (!_available) Log.Info("本机没有 GPU Engine 计数器，逐进程 GPU 占用不可用");
        return _available;
    }

    /// <summary>刷新 LUID → 核显/独显 映射（显存占用会变，所以定期重判）。</summary>
    private void RefreshAdapters()
    {
        if (DateTime.Now - _adapterChecked < TimeSpan.FromMinutes(2)) return;
        _adapterChecked = DateTime.Now;

        try
        {
            string[] names = new PerformanceCounterCategory("GPU Adapter Memory").GetInstanceNames();
            var stat = new Dictionary<string, (double ded, double sha)>();

            foreach (string name in names)
            {
                if (!TryParseLuid(name, out string luid)) continue;
                double ded = ReadCounter("GPU Adapter Memory", "Dedicated Usage", name);
                double sha = ReadCounter("GPU Adapter Memory", "Shared Usage", name);
                stat[luid] = (ded, sha);
            }

            // 有专用显存、且明显多于共享内存的 → 独显
            foreach (var kv in stat)
            {
                double dedMb = kv.Value.ded / 1048576.0;
                double shaMb = kv.Value.sha / 1048576.0;
                _luidIsDiscrete[kv.Key] = dedMb >= 128 && dedMb > shaMb;
            }
        }
        catch (Exception ex)
        {
            Log.Warn("GPU 适配器特征读取失败：" + ex.Message);
        }
    }

    private double ReadCounter(string category, string counter, string instance)
    {
        string key = category + "|" + counter + "|" + instance;
        if (!_counters.TryGetValue(key, out var pc))
        {
            try
            {
                pc = new PerformanceCounter(category, counter, instance, true);
                pc.NextValue();           // 第一次调用只是建立基线
                _counters[key] = pc;
            }
            catch
            {
                return 0;
            }
        }

        try { return pc.NextValue(); }
        catch { return 0; }
    }

    /// <summary>从 GPU Engine 实例名里取 pid 与 luid 段。</summary>
    private static bool TryParse(string instance, out int pid, out string luid)
    {
        pid = 0;
        luid = "";

        int p = instance.IndexOf("pid_", StringComparison.Ordinal);
        if (p < 0) return false;
        int end = instance.IndexOf('_', p + 4);
        if (end < 0) return false;
        if (!int.TryParse(instance.AsSpan(p + 4, end - p - 4), out pid)) return false;

        int l = instance.IndexOf("luid_", StringComparison.Ordinal);
        if (l < 0) return false;
        int lEnd = instance.IndexOf("_phys", l, StringComparison.Ordinal);
        if (lEnd < 0) lEnd = instance.Length;
        luid = instance.Substring(l, lEnd - l);
        return true;
    }

    private static bool TryParseLuid(string instance, out string luid)
    {
        luid = "";
        int l = instance.IndexOf("luid_", StringComparison.Ordinal);
        if (l < 0) return false;
        int end = instance.IndexOf("_phys", l, StringComparison.Ordinal);
        if (end < 0) end = instance.Length;
        luid = instance.Substring(l, end - l);
        return true;
    }

    public void Dispose()
    {
        foreach (var c in _counters.Values)
        {
            try { c.Dispose(); } catch { }
        }
        _counters.Clear();
    }
}
