using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace PerfDeck.Core;

/// <summary>
/// 对外硬件数据接口的 v1 数据模型。
///
/// 设计约束：
/// 1. **只增不改**：v1 里已有的字段名与语义永远不变；新增字段属于兼容变更。
///    破坏性变更（改名/改语义/删字段）必须另开 /api/v2。
/// 2. 字段一律用 camelCase 的稳定名字，不用内部 record 的属性名，
///    避免内部重构意外改变对外契约。
/// 3. 取不到值时给 null（而不是 0 或空字符串），调用方能区分"没有"和"确实是 0"。
/// </summary>
public static class HardwareApi
{
    /// <summary>接口版本号。路径里的 v1 与这里的版本号必须一致。</summary>
    public const string Version = "1.0";

    /// <summary>协议前缀。</summary>
    public const string BasePath = "/api/v1";

    /// <summary>把内部快照转成对外的 JSON 友好结构。</summary>
    public static Dictionary<string, object?> BuildSnapshot(Snapshot s)
    {
        var d = new Dictionary<string, object?>();

        d["cpu"] = s.Cpu == null
            ? null
            : new Dictionary<string, object?>
            {
                ["name"] = s.Cpu.Name,
                ["loadPercent"] = Round(s.Cpu.Load),
                ["tempC"] = Round(s.Cpu.Temp),
                ["maxClockMhz"] = Round(s.Cpu.MaxClockMhz),
                ["threads"] = s.Cpu.Threads,
                ["powerW"] = Round(s.Cpu.Power),
            };

        d["gpus"] = s.Gpus.Select(g => (object?)new Dictionary<string, object?>
        {
            ["name"] = g.Name,
            ["integrated"] = g.Integrated,
            ["loadPercent"] = Round(g.Load),
            ["tempC"] = Round(g.Temp),
            ["clockMhz"] = Round(g.ClockMhz),
            ["memUsedGb"] = Round(g.MemUsedGb),
            ["memTotalGb"] = Round(g.MemTotalGb),
            ["powerW"] = Round(g.Power),
        }).ToList();

        d["memory"] = new Dictionary<string, object?>
        {
            ["usedGb"] = Round(s.MemUsedGb),
            ["totalGb"] = Round(s.MemTotalGb),
            ["availGb"] = Round(s.MemAvailGb),
            ["usedPercent"] = Round(s.MemUsedPct),
            ["poolPagedMb"] = Round(s.PoolPagedMb),
            ["poolNonpagedMb"] = Round(s.PoolNonpagedMb),
        };

        d["net"] = s.Net == null
            ? null
            : new Dictionary<string, object?>
            {
                ["latencyMs"] = Round(s.Net.LatencyMs),
                ["jitterMs"] = Round(s.Net.JitterMs),
                ["lossPercent"] = Round(s.Net.LossPct),
                ["downBps"] = Round(s.Net.DownBps),
                ["upBps"] = Round(s.Net.UpBps),
                ["linkUp"] = s.Net.LinkUp,
                ["linkSpeed"] = s.Net.LinkSpeed,
                ["icmpBlocked"] = s.Net.IcmpBlocked,
                ["ipv4"] = s.Net.Ipv4,
                ["ipv6"] = s.Net.Ipv6,
                ["ipv4Mask"] = s.Net.Ipv4Mask,
                ["gateway"] = s.Net.Gateway,
                ["dns"] = s.Net.Dns,
                ["mtu"] = s.Net.Mtu,
            };

        d["disks"] = s.Disks.Select(x => (object?)new Dictionary<string, object?>
        {
            ["name"] = x.Name,
            ["tempC"] = Round(x.Temp),
            ["usedPercent"] = Round(x.UsedPct),
            ["activityPercent"] = Round(x.Activity),
            ["readBps"] = Round(x.ReadBps),
            ["writeBps"] = Round(x.WriteBps),
            ["healthPercent"] = Round(x.HealthPct),
        }).ToList();

        d["fans"] = s.Fans.Select(x => (object?)new Dictionary<string, object?>
        {
            ["name"] = x.Name,
            ["host"] = x.Host,
            ["rpm"] = Round(x.Rpm),
            ["percent"] = Round(x.Pct),
        }).ToList();

        d["battery"] = new Dictionary<string, object?>
        {
            ["present"] = s.HasBattery,
            ["percent"] = s.HasBattery ? s.BatteryPercent : (int?)null,
            ["charging"] = s.HasBattery ? s.BatteryCharging : (bool?)null,
            ["onAc"] = s.HasBattery ? s.OnAc : (bool?)null,
            ["lifeMinutes"] = s.HasBattery && s.BatteryLifeMinutes > 0 ? s.BatteryLifeMinutes : (int?)null,
        };

        d["processes"] = s.Procs.Take(20).Select(p => (object?)new Dictionary<string, object?>
        {
            ["name"] = p.Name,
            ["cpuPercent"] = Round(p.CpuPct),
            ["memoryMb"] = Round(p.MemMb),
        }).ToList();

        d["elevated"] = s.Elevated;
        d["sampledAt"] = s.Now.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);

        return d;
    }

    /// <summary>summary 接口用 fields 参数挑顶层字段。</summary>
    public static Dictionary<string, object?> FilterFields(Dictionary<string, object?> full, string? fields)
    {
        if (string.IsNullOrWhiteSpace(fields)) return full;

        var want = fields.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                         .Select(x => x.ToLowerInvariant())
                         .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var res = new Dictionary<string, object?>();
        foreach (var kv in full)
        {
            if (want.Contains(kv.Key))
            {
                res[kv.Key] = kv.Value;
                continue;
            }
            // 允许前缀匹配：fields=gpu 也能命中 gpus，fields=disk 也能命中 disks
            if (want.Any(w => kv.Key.StartsWith(w, StringComparison.OrdinalIgnoreCase)))
                res[kv.Key] = kv.Value;
        }
        return res;
    }

    /// <summary>字段说明（机器可读），供调用方自查字段含义。</summary>
    public static Dictionary<string, object?> BuildSchema()
    {
        return new Dictionary<string, object?>
        {
            ["apiVersion"] = Version,
            ["basePath"] = BasePath,
            ["endpoints"] = new[]
            {
                new Dictionary<string, object?>
                {
                    ["path"] = "/api/v1/health",
                    ["desc"] = "存活探针：返回 ok、版本、运行时长、快照时间",
                },
                new Dictionary<string, object?>
                {
                    ["path"] = "/api/v1/version",
                    ["desc"] = "接口版本号与程序版本",
                },
                new Dictionary<string, object?>
                {
                    ["path"] = "/api/v1/schema",
                    ["desc"] = "字段说明（本文档的机器可读版）",
                },
                new Dictionary<string, object?>
                {
                    ["path"] = "/api/v1/snapshot",
                    ["desc"] = "完整硬件快照（cpu/gpus/memory/net/disks/fans/battery/processes）",
                },
                new Dictionary<string, object?>
                {
                    ["path"] = "/api/v1/summary",
                    ["desc"] = "精简快照；查询参数 fields=cpu,gpu,net 逗号分隔，pretty=1 美化输出",
                },
            },
            ["fields"] = new Dictionary<string, object?>
            {
                ["cpu.name"] = "CPU 型号",
                ["cpu.loadPercent"] = "CPU 占用率 %",
                ["cpu.tempC"] = "CPU 封装温度 ℃（读不到为 null）",
                ["cpu.powerW"] = "CPU 封装功耗 W（需要管理员权限，读不到为 null）",
                ["gpus[].integrated"] = "true=核显，false=独显",
                ["gpus[].memUsedGb/memTotalGb"] = "显存占用 / 总量 GB",
                ["memory.poolPagedMb/poolNonpagedMb"] = "分页 / 非分页缓冲池 MB",
                ["net.latencyMs/jitterMs/lossPercent"] = "延迟 / 抖动 / 丢包",
                ["net.downBps/upBps"] = "下行 / 上行字节每秒",
                ["net.ipv4/ipv6/gateway/ipv4Mask/dns/mtu"] = "本机网络参数",
                ["disks[].healthPercent"] = "硬盘剩余寿命 %（无该传感器为 null）",
                ["battery.percent/charging/onAc"] = "电池状态",
                ["processes[]"] = "占用率前 20 的进程",
            },
            ["compatibility"] = new Dictionary<string, object?>
            {
                ["policy"] = "v1 只增字段不删不改；破坏性变更走 /api/v2",
                ["envelope"] = "{ apiVersion, ok, timestamp, data | error }",
                ["errors"] = "error.code 取值：unknown_endpoint / unsupported_method / invalid_query / internal_error / disabled",
            },
        };
    }

    private static double? Round(double? v) =>
        v.HasValue ? Math.Round(v.Value, 2) : (double?)null;
}
