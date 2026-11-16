using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using LibreHardwareMonitor.Hardware;
using Microsoft.Win32;

namespace PerfDeck.Core;

public sealed class CpuInfo
{
    public string Name = "CPU";
    public int Threads;
    internal IHardware Hw = null!;
    internal ISensor? Load, Temp, Power;
    internal readonly List<ISensor> Clocks = new();
    internal string Key = "cpu";
}

public sealed class GpuInfo
{
    public string Key = "";
    public string Name = "";
    public bool Integrated;
    internal IHardware Hw = null!;
    internal ISensor? Load, Temp, Clock, MemUsed, MemTotal, Power;
}

public sealed class DiskInfo
{
    public string Key = "";
    public string Name = "";
    internal IHardware Hw = null!;
    internal ISensor? Temp, Used, Activity, ReadRate, WriteRate, Health;
}

public sealed class FanInfo
{
    public string Key = "";
    public string Name = "";
    public string Host = "";
    internal IHardware Hw = null!;
    internal ISensor? Rpm, Pct;
}

public sealed record CpuSnap(string Key, double? Load, double? Temp, double? MaxClockMhz, string Name, int Threads, double? Power);
public sealed record GpuSnap(string Key, string Name, bool Integrated, double? Load, double? Temp, double? ClockMhz, double? MemUsedGb, double? MemTotalGb, double? Power);
public sealed record DiskSnap(string Key, string Name, double? Temp, double? UsedPct, double? Activity, double? ReadBps, double? WriteBps, double? HealthPct);
public sealed record FanSnap(string Key, string Name, string Host, double? Rpm, double? Pct);

public sealed class Snapshot
{
    public DateTime Now = DateTime.Now;
    public CpuSnap? Cpu;
    public List<GpuSnap> Gpus = new();
    public List<DiskSnap> Disks = new();
    public List<FanSnap> Fans = new();
    public List<ProcSnap> Procs = new();
    public int ProcessCount;
    public double MemUsedPct;
    public double MemUsedGb;
    public double MemTotalGb;
    public double MemAvailGb;
    /// <summary>分页缓冲池占用（MB）</summary>
    public double PoolPagedMb;
    /// <summary>非分页缓冲池占用（MB）</summary>
    public double PoolNonpagedMb;
    public NetStats? Net;
    public bool Elevated;

    public bool HasBattery;
    public int BatteryPercent;
    public bool BatteryCharging;
    public bool OnAc;
    public int BatteryLifeMinutes;
}

/// <summary>
/// 硬件传感器采集。CPU / 显卡每帧刷新；硬盘 SMART（15 秒）与主板风扇（3 秒）
/// 走各自的低频周期，把自身 CPU 占用压到最低。
/// </summary>
public sealed class SensorService : IDisposable
{
    private Computer? _computer;
    private readonly object _gate = new();
    private DateTime _lastStorageUpdate = DateTime.MinValue;
    private DateTime _lastBoardUpdate = DateTime.MinValue;
    private static readonly TimeSpan StorageInterval = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan BoardInterval = TimeSpan.FromSeconds(3);

    public CpuInfo? Cpu { get; private set; }
    public List<GpuInfo> Gpus { get; } = new();
    public List<DiskInfo> Disks { get; } = new();
    public List<FanInfo> Fans { get; } = new();
    public string Status { get; private set; } = "未初始化";
    public bool Ready { get; private set; }

    public void Initialize()
    {
        lock (_gate)
        {
            try
            {
                _computer = new Computer
                {
                    IsCpuEnabled = true,
                    IsGpuEnabled = true,
                    IsStorageEnabled = true,
                    IsMotherboardEnabled = true,   // 风扇转速来自主板 SuperIO
                    IsControllerEnabled = false,
                    IsMemoryEnabled = false,
                    IsNetworkEnabled = false,
                    IsPsuEnabled = false,
                    IsBatteryEnabled = false
                };
                _computer.Open();

                // 部分传感器（核显 D3D、主板风扇）要等首次刷新后才建立
                UpdateAll(DateTime.Now);
                Discover(_computer.Hardware);
                Ready = true;
                Status = $"已识别 {Gpus.Count} 个显卡 / {Disks.Count} 个硬盘 / {Fans.Count} 个风扇";
                Log.Info("传感器初始化完成：" + Status);
            }
            catch (Exception ex)
            {
                Ready = false;
                Status = "传感器不可用：" + ex.Message;
                Log.Error("传感器初始化失败：" + ex);
            }
        }
    }

    private void Discover(IEnumerable<IHardware> hws)
    {
        foreach (var hw in hws)
        {
            switch (hw.HardwareType)
            {
                case HardwareType.Cpu:
                    Cpu ??= BuildCpu(hw);
                    break;
                case HardwareType.GpuNvidia:
                case HardwareType.GpuAmd:
                case HardwareType.GpuIntel:
                    var g = BuildGpu(hw);
                    if (g != null) Gpus.Add(g);
                    break;
                case HardwareType.Storage:
                    var d = BuildDisk(hw);
                    if (d != null) Disks.Add(d);
                    break;
            }

            CollectFans(hw);

            if (hw.SubHardware.Length > 0) Discover(hw.SubHardware);
        }
    }

    // ---------- CPU ----------

    private CpuInfo BuildCpu(IHardware hw)
    {
        var info = new CpuInfo
        {
            Hw = hw,
            Name = CpuNameFromRegistry() ?? CleanName(hw.Name),
            Threads = Environment.ProcessorCount
        };

        info.Load = PickByName(hw, SensorType.Load, "CPU Total", "Total")
                     ?? PickContains(hw, SensorType.Load, "Total");

        info.Temp = PickByName(hw, SensorType.Temperature,
                        "Core (Tctl/Tdie)", "CPU Package", "Core Average", "CCD1 (Tdie)", "Tctl", "Package", "Core Max")
                    ?? PickContains(hw, SensorType.Temperature, "Package", "Tdie", "Tctl", "Core")
                    ?? PickAny(hw, SensorType.Temperature);

        foreach (var s in AllSensors(hw).Where(s => s.SensorType == SensorType.Clock))
            if (s.Name.Contains("Core #", StringComparison.OrdinalIgnoreCase) ||
                s.Name.Contains("Cores", StringComparison.OrdinalIgnoreCase))
                info.Clocks.Add(s);

        // CPU 封装功耗：Intel 的 "CPU Package" / AMD 的 "Package"，需要读 MSR（管理员权限）
        info.Power = PickByName(hw, SensorType.Power, "CPU Package", "Package", "CPU Cores", "Core")
                     ?? PickContains(hw, SensorType.Power, "Package", "Power");

        if (info.Clocks.Count == 0)
            info.Clocks.AddRange(AllSensors(hw).Where(s => s.SensorType == SensorType.Clock));

        return info;
    }

    // ---------- 显卡 ----------

    private GpuInfo? BuildGpu(IHardware hw)
    {
        string name = CleanName(hw.Name);
        if (string.IsNullOrWhiteSpace(name)) return null;

        var info = new GpuInfo { Hw = hw, Name = name };
        BindGpu(info);

        info.Key = $"{hw.HardwareType}:{name}";
        int dup = Gpus.Count(x => x.Key == info.Key);
        if (dup > 0) info.Key += "#" + (dup + 1);
        return info;
    }

    /// <summary>绑定（或补绑）显卡传感器。核显的传感器是延迟创建的，需要多次尝试。</summary>
    private void BindGpu(GpuInfo info)
    {
        var hw = info.Hw;

        var dedicatedTotal = PickByName(hw, SensorType.SmallData, "GPU Memory Total", "D3D Dedicated Memory Total");
        var dedicatedUsed = PickByName(hw, SensorType.SmallData, "GPU Memory Used", "D3D Dedicated Memory Used");

        double dedicatedMb = dedicatedTotal?.Value is float t ? t : 0;

        bool intel = hw.HardwareType == HardwareType.GpuIntel;
        bool apuName = Regex.IsMatch(info.Name, @"Radeon.*Graphics|Vega \d|Radeon\(TM\)|UHD Graphics|Iris|HD Graphics",
            RegexOptions.IgnoreCase);
        info.Integrated = intel ? dedicatedMb < 1024 : (apuName && dedicatedMb < 1024);

        if (info.Integrated)
        {
            info.MemTotal ??= dedicatedTotal ?? PickByName(hw, SensorType.SmallData, "D3D Shared Memory Total");
            info.MemUsed ??= dedicatedUsed ?? PickByName(hw, SensorType.SmallData, "D3D Shared Memory Used");
        }
        else
        {
            info.MemTotal ??= dedicatedTotal;
            info.MemUsed ??= dedicatedUsed;
        }

        info.Load ??= PickByName(hw, SensorType.Load, "GPU Core", "D3D 3D", "GPU Core Load")
                      ?? PickContains(hw, SensorType.Load, "3D", "Core", "GPU")
                      ?? PickAny(hw, SensorType.Load);

        info.Temp ??= PickByName(hw, SensorType.Temperature, "GPU Core", "GPU Temperature", "Core")
                      ?? PickContains(hw, SensorType.Temperature, "Core", "GPU")
                      ?? PickAny(hw, SensorType.Temperature);

        info.Clock ??= PickByName(hw, SensorType.Clock, "GPU Core", "Core Clock")
                       ?? PickContains(hw, SensorType.Clock, "Core") ?? PickAny(hw, SensorType.Clock);

        info.Power ??= PickByName(hw, SensorType.Power, "GPU Package", "GPU Power", "GPU Core")
                       ?? PickContains(hw, SensorType.Power, "Package", "Power");
    }

    // ---------- 硬盘 ----------

    private DiskInfo? BuildDisk(IHardware hw)
    {
        string name = CleanName(hw.Name);
        if (string.IsNullOrWhiteSpace(name)) return null;
        if (name.Contains("Virtual", StringComparison.OrdinalIgnoreCase)) return null;

        var info = new DiskInfo { Hw = hw, Name = name };
        BindDisk(info);

        if (info.Temp == null && info.Used == null && info.Activity == null &&
            info.ReadRate == null && info.WriteRate == null) return null;

        info.Key = "disk:" + name;
        int dup = Disks.Count(x => x.Key == info.Key);
        if (dup > 0) info.Key += "#" + (dup + 1);
        return info;
    }

    private void BindDisk(DiskInfo info)
    {
        var hw = info.Hw;
        info.Temp ??= PickByName(hw, SensorType.Temperature, "Temperature", "Drive Temperature")
                      ?? PickAny(hw, SensorType.Temperature);
        info.Used ??= PickByName(hw, SensorType.Load, "Used Space") ?? PickContains(hw, SensorType.Load, "Used");
        info.Activity ??= PickByName(hw, SensorType.Load, "Total Activity") ?? PickContains(hw, SensorType.Load, "Activity");
        info.ReadRate ??= PickByName(hw, SensorType.Throughput, "Read Rate", "Read")
                          ?? PickContains(hw, SensorType.Throughput, "Read");
        info.WriteRate ??= PickByName(hw, SensorType.Throughput, "Write Rate", "Write")
                           ?? PickContains(hw, SensorType.Throughput, "Write");

        // 寿命/健康度（NVMe 的 Remaining Life、部分 SATA 的 Life/Health）。
        // “Percentage Used”语义相反，这里不认，避免把 5% 磨损显示成 5% 健康。
        info.Health ??= PickByName(hw, SensorType.Level, "Remaining Life", "Life", "Health", "Wear Leveling Count")
                        ?? PickContains(hw, SensorType.Level, "Remaining Life", "Life", "Health");
    }

    // ---------- 风扇 ----------

    private void CollectFans(IHardware hw)
    {
        var rpm = hw.Sensors.Where(s => s.SensorType == SensorType.Fan).ToList();
        var pct = hw.Sensors.Where(s => s.SensorType == SensorType.Control &&
                                        s.Name.Contains("Fan", StringComparison.OrdinalIgnoreCase)).ToList();
        if (rpm.Count == 0 && pct.Count == 0) return;

        string host = CleanName(hw.Name);
        int n = Math.Max(rpm.Count, pct.Count);
        for (int i = 0; i < n; i++)
        {
            var f = new FanInfo
            {
                Hw = hw,
                Host = host,
                Rpm = i < rpm.Count ? rpm[i] : null,
                Pct = i < pct.Count ? pct[i] : null
            };
            f.Name = f.Rpm?.Name ?? f.Pct?.Name ?? ("Fan #" + (i + 1));

            f.Key = $"fan:{host}:{f.Name}";
            int dup = Fans.Count(x => x.Key == f.Key);
            if (dup > 0) f.Key += "#" + (dup + 1);
            Fans.Add(f);
        }
    }

    // ---------- 刷新 ----------

    private void UpdateAll(DateTime now)
    {
        if (_computer == null) return;
        bool storageDue = now - _lastStorageUpdate >= StorageInterval;
        bool boardDue = now - _lastBoardUpdate >= BoardInterval;
        bool storageDone = false, boardDone = false;

        foreach (var hw in _computer.Hardware)
            UpdateRecursive(hw, storageDue, boardDue, ref storageDone, ref boardDone, false);

        if (storageDone) _lastStorageUpdate = now;
        if (boardDone) _lastBoardUpdate = now;

        RebindMissing();
    }

    private static void UpdateRecursive(IHardware hw, bool storageDue, bool boardDue,
                                        ref bool storageDone, ref bool boardDone, bool inSlow)
    {
        bool isStorage = inSlow || hw.HardwareType == HardwareType.Storage;
        bool isBoard = inSlow || hw.HardwareType == HardwareType.Motherboard;

        if (isStorage)
        {
            if (storageDue) { try { hw.Update(); } catch { } storageDone = true; }
        }
        else if (isBoard)
        {
            if (boardDue) { try { hw.Update(); } catch { } boardDone = true; }
        }
        else
        {
            try { hw.Update(); } catch { }
        }

        foreach (var sub in hw.SubHardware)
            UpdateRecursive(sub, storageDue, boardDue, ref storageDone, ref boardDone, isStorage || isBoard);
    }

    /// <summary>补绑延迟出现的传感器（核显、部分硬盘与风扇）。</summary>
    private void RebindMissing()
    {
        foreach (var g in Gpus)
            if (g.Load == null || g.Temp == null || g.Clock == null) BindGpu(g);

        foreach (var d in Disks)
            if (d.Temp == null || d.Activity == null || d.ReadRate == null || d.Health == null) BindDisk(d);
    }

    public Snapshot Sample()
    {
        var snap = new Snapshot { Now = DateTime.Now, Elevated = Native.IsElevated() };
        lock (_gate)
        {
            if (!Ready || _computer == null)
            {
                var (t0, a0) = Native.GetMemory();
                FillMem(snap, t0, a0);
                return snap;
            }

            try { UpdateAll(DateTime.Now); }
            catch (Exception ex) { Log.Warn("传感器刷新异常：" + ex.Message); }

            if (Cpu != null)
            {
                double? clock = null;
                foreach (var c in Cpu.Clocks)
                    if (c.Value is float v && (!clock.HasValue || v > clock.Value)) clock = v;
                snap.Cpu = new CpuSnap(Cpu.Key, Cpu.Load?.Value, Cpu.Temp?.Value, clock, Cpu.Name, Cpu.Threads,
                    Cpu.Power?.Value);
            }

            foreach (var g in Gpus)
            {
                double? usedGb = null, totalGb = null;
                if (g.MemUsed?.Value is float mu) usedGb = mu / 1024.0;
                if (g.MemTotal?.Value is float mt && mt > 0) totalGb = mt / 1024.0;
                snap.Gpus.Add(new GpuSnap(g.Key, g.Name, g.Integrated,
                    g.Load?.Value, g.Temp?.Value, g.Clock?.Value, usedGb, totalGb, g.Power?.Value));
            }

            foreach (var d in Disks)
                snap.Disks.Add(new DiskSnap(d.Key, d.Name, d.Temp?.Value, d.Used?.Value,
                    d.Activity?.Value, d.ReadRate?.Value, d.WriteRate?.Value, d.Health?.Value));

            foreach (var f in Fans)
                snap.Fans.Add(new FanSnap(f.Key, f.Name, f.Host, f.Rpm?.Value, f.Pct?.Value));

            var (total, avail) = Native.GetMemory();
            FillMem(snap, total, avail);

            var (hasBat, pct, charging, onAc, minutes) = Native.GetPower();
            snap.HasBattery = hasBat;
            snap.BatteryPercent = pct;
            snap.BatteryCharging = charging;
            snap.OnAc = onAc;
            snap.BatteryLifeMinutes = minutes;
        }
        return snap;
    }

    private static void FillMem(Snapshot snap, ulong total, ulong avail)
    {
        var (paged, nonpaged) = Native.GetPool();
        snap.PoolPagedMb = paged;
        snap.PoolNonpagedMb = nonpaged;

        if (total == 0) return;
        double tg = total / 1024.0 / 1024.0 / 1024.0;
        double ag = avail / 1024.0 / 1024.0 / 1024.0;
        snap.MemTotalGb = tg;
        snap.MemAvailGb = ag;
        snap.MemUsedGb = tg - ag;
        snap.MemUsedPct = (tg - ag) / tg * 100.0;
    }

    /// <summary>诊断用：列出所有已识别传感器。</summary>
    public string Describe()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"状态：{Status}");
        sb.AppendLine($"管理员权限：{(Native.IsElevated() ? "是" : "否")}");
        sb.AppendLine();
        lock (_gate)
        {
            if (_computer == null) return sb.ToString();
            foreach (var hw in _computer.Hardware) Dump(hw, sb, 0);
        }
        return sb.ToString();
    }

    private static void Dump(IHardware hw, System.Text.StringBuilder sb, int depth)
    {
        sb.AppendLine($"{new string(' ', depth * 2)}[{hw.HardwareType}] {hw.Name}");
        foreach (var s in hw.Sensors)
        {
            string v = s.Value.HasValue ? s.Value.Value.ToString(s.SensorType == SensorType.Temperature ? "0.0" : "0.##") : "-";
            sb.AppendLine($"{new string(' ', depth * 2 + 4)}{s.SensorType,-12} {s.Name,-28} {v}");
        }
        if (hw.SubHardware.Length > 0)
            foreach (var sub in hw.SubHardware) Dump(sub, sb, depth + 1);
    }

    // ---------- helpers ----------

    private static IEnumerable<ISensor> AllSensors(IHardware hw)
    {
        foreach (var s in hw.Sensors) yield return s;
        foreach (var sub in hw.SubHardware)
            foreach (var s in sub.Sensors) yield return s;
    }

    private static ISensor? PickByName(IHardware hw, SensorType type, params string[] names)
    {
        foreach (var n in names)
            foreach (var s in AllSensors(hw))
                if (s.SensorType == type && string.Equals(s.Name, n, StringComparison.OrdinalIgnoreCase))
                    return s;
        return null;
    }

    private static ISensor? PickContains(IHardware hw, SensorType type, params string[] keys)
    {
        foreach (var k in keys)
            foreach (var s in AllSensors(hw))
                if (s.SensorType == type && s.Name.Contains(k, StringComparison.OrdinalIgnoreCase))
                    return s;
        return null;
    }

    private static ISensor? PickAny(IHardware hw, SensorType type) =>
        AllSensors(hw).FirstOrDefault(s => s.SensorType == type);

    private static string CleanName(string n) => Regex.Replace(n ?? "", @"\s+", " ").Trim();

    private static string? CpuNameFromRegistry()
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            string? s = k?.GetValue("ProcessorNameString") as string;
            return string.IsNullOrWhiteSpace(s)
                ? null
                : CleanName(s).Replace("(R)", "").Replace("(TM)", "").Replace("CPU ", "").Trim();
        }
        catch { return null; }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            try { _computer?.Close(); } catch { }
            _computer = null;
            Ready = false;
        }
    }
}
