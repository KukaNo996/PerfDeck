using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace PerfDeck.Core;

/// <summary>进程卡片的排序维度。</summary>
public enum ProcSort
{
    Cpu,
    Memory,
    GpuIntegrated,
    GpuDiscrete
}

public sealed record ProcSnap(string Name, double CpuPct, double MemMb, double GpuIg, double GpuDg)
{
    public double SortValue(ProcSort s) => s switch
    {
        ProcSort.Memory => MemMb,
        ProcSort.GpuIntegrated => GpuIg,
        ProcSort.GpuDiscrete => GpuDg,
        _ => CpuPct
    };
}

/// <summary>
/// 进程占用排行。按进程名聚合，CPU 用两次采样的时间差算；
/// 内存取工作集；GPU 由外部按 pid 传入（来自 GPU Engine 计数器）。
/// 只在面板上有“进程”卡片时才会被调用。
/// </summary>
public sealed class ProcessSampler
{
    private Dictionary<int, TimeSpan> _prev = new();
    private DateTime _prevAt = DateTime.MinValue;
    private List<ProcSnap> _last = new();

    public int LastTotalCount { get; private set; }

    public List<ProcSnap> Sample(int top, ProcSort sort,
        Dictionary<int, (double Integrated, double Discrete)>? gpu)
    {
        var now = DateTime.UtcNow;
        double dt = (now - _prevAt).TotalSeconds;
        if (dt < 0.35 && _last.Count > 0) return _last;

        var cur = new Dictionary<int, TimeSpan>(256);
        var raw = new List<(string name, double pct, double mem, double ig, double dg)>(256);

        Process[] procs;
        try { procs = Process.GetProcesses(); }
        catch { return _last; }

        int cores = Math.Max(1, Environment.ProcessorCount);

        foreach (var p in procs)
        {
            try
            {
                TimeSpan cpu = p.TotalProcessorTime;
                int pid = p.Id;
                cur[pid] = cpu;

                double pct = 0;
                if (dt > 0.2 && _prev.TryGetValue(pid, out var prev))
                    pct = Math.Max(0, (cpu - prev).TotalSeconds) / dt / cores * 100.0;

                double ig = 0, dg = 0;
                if (gpu != null && gpu.TryGetValue(pid, out var g))
                {
                    ig = g.Integrated;
                    dg = g.Discrete;
                }

                raw.Add((p.ProcessName, pct, p.WorkingSet64 / 1048576.0, ig, dg));
            }
            catch
            {
                // 受保护进程读不到，忽略
            }
            finally
            {
                p.Dispose();
            }
        }

        _prev = cur;
        _prevAt = now;
        LastTotalCount = cur.Count;

        _last = raw
            .GroupBy(x => x.name, StringComparer.OrdinalIgnoreCase)
            .Select(g => new ProcSnap(g.Key,
                Math.Round(g.Sum(x => x.pct), 1),
                Math.Round(g.Sum(x => x.mem), 0),
                Math.Round(g.Sum(x => x.ig), 1),
                Math.Round(g.Sum(x => x.dg), 1)))
            .OrderByDescending(x => x.SortValue(sort))
            .ThenByDescending(x => x.CpuPct)
            .Take(Math.Clamp(top, 3, 10))
            .ToList();

        return _last;
    }
}
