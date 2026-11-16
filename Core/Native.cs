using System;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace PerfDeck.Core;

internal static class Native
{
    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    public static (ulong total, ulong avail) GetMemory()
    {
        var ms = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (GlobalMemoryStatusEx(ref ms)) return (ms.ullTotalPhys, ms.ullAvailPhys);
        return (0, 0);
    }

    // ---------- 系统内存池（分页 / 非分页缓冲池） ----------

    [StructLayout(LayoutKind.Sequential)]
    private struct PERFORMANCE_INFORMATION
    {
        public uint cb;
        public UIntPtr CommitTotal;
        public UIntPtr CommitLimit;
        public UIntPtr CommitPeak;
        public UIntPtr PhysicalTotal;
        public UIntPtr PhysicalAvailable;
        public UIntPtr SystemCache;
        public UIntPtr KernelTotal;
        public UIntPtr KernelPaged;
        public UIntPtr KernelNonpaged;
        public UIntPtr PageSize;
        public uint HandleCount;
        public uint ProcessCount;
        public uint ThreadCount;
    }

    [DllImport("psapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetPerformanceInfo(out PERFORMANCE_INFORMATION info, uint size);

    /// <summary>
    /// 分页 / 非分页缓冲池占用（MB）。来自 psapi!GetPerformanceInfo，
    /// 数据直接读内核计数器，不需要管理员权限，也不走性能计数器（零初始化开销）。
    /// </summary>
    public static (double pagedMb, double nonpagedMb) GetPool()
    {
        try
        {
            uint size = (uint)Marshal.SizeOf<PERFORMANCE_INFORMATION>();
            if (!GetPerformanceInfo(out var info, size)) return (0, 0);

            double page = info.PageSize.ToUInt64();
            if (page <= 0) return (0, 0);

            return (info.KernelPaged.ToUInt64() * page / 1048576.0,
                    info.KernelNonpaged.ToUInt64() * page / 1048576.0);
        }
        catch { return (0, 0); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemPowerStatus(ref SYSTEM_POWER_STATUS status);

    /// <summary>电池状态。无电池的设备返回 HasBattery = false。</summary>
    public static (bool hasBattery, int percent, bool charging, bool onAc, int lifeMinutes) GetPower()
    {
        try
        {
            var st = new SYSTEM_POWER_STATUS();
            if (!GetSystemPowerStatus(ref st))
                return (false, 0, false, true, 0);

            bool noBattery = (st.BatteryFlag & 128) != 0 || st.BatteryLifePercent == 255;
            bool charging = (st.BatteryFlag & 8) != 0;
            bool onAc = st.ACLineStatus == 1;
            int minutes = st.BatteryLifeTime > 0 ? st.BatteryLifeTime / 60 : 0;
            int percent = st.BatteryLifePercent == 255 ? 0 : st.BatteryLifePercent;

            return (!noBattery, percent, charging, onAc, minutes);
        }
        catch
        {
            return (false, 0, false, true, 0);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out POINT pt);

    /// <summary>取鼠标屏幕坐标（物理像素）。比 WPF 的 Mouse.GetPosition 可靠：鼠标在窗口外也有真实值。</summary>
    public static bool TryGetCursorPos(out int x, out int y)
    {
        x = y = 0;
        if (!GetCursorPos(out var pt)) return false;
        x = pt.X;
        y = pt.Y;
        return true;
    }

    public const int GWL_EXSTYLE = -20;
    public const int WS_EX_TRANSPARENT = 0x00000020;
    public const int WS_EX_LAYERED = 0x00080000;
    public const int WS_EX_TOOLWINDOW = 0x00000080;
    public const int WS_EX_APPWINDOW = 0x00040000;

    [DllImport("user32.dll", SetLastError = true)]
    public static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("psapi.dll")]
    private static extern bool EmptyWorkingSet(IntPtr hProcess);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll")]
    private static extern bool SetProcessWorkingSetSize(IntPtr proc, IntPtr min, IntPtr max);

    /// <summary>压低工作集，降低长期驻留的内存占用（不影响性能）。</summary>
    public static void TrimWorkingSet()
    {
        try
        {
            IntPtr p = GetCurrentProcess();
            EmptyWorkingSet(p);
            SetProcessWorkingSetSize(p, (IntPtr)(-1), (IntPtr)(-1));
        }
        catch { }
    }

    public const uint MOD_ALT = 0x0001;
    public const uint MOD_CONTROL = 0x0002;
    public const uint MOD_SHIFT = 0x0004;

    public static bool IsElevated()
    {
        try
        {
            using var id = WindowsIdentity.GetCurrent();
            var pr = new WindowsPrincipal(id);
            return pr.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }
}
