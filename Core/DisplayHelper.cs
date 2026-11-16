using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

namespace PerfDeck.Core;

public sealed record DisplayInfo(
    string DeviceName, bool Primary,
    int X, int Y, int Width, int Height,
    int WorkX, int WorkY, int WorkWidth, int WorkHeight,
    double Scale)
{
    public string Label => Primary ? "主屏" : "副屏";
    public string Describe() => $"{Label} {Width}×{Height} @{Scale * 100:0}%";
}

/// <summary>
/// 显示器识别。所有坐标均为物理像素；Scale 为该屏的 DPI 缩放（1.0 = 100%）。
/// 识别失败（没有副屏 / 读取异常）时调用方回退到主屏。
/// </summary>
public static class DisplayHelper
{
    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);

    /// <summary>枚举当前所有显示器；异常时返回空列表。</summary>
    public static List<DisplayInfo> GetDisplays()
    {
        var list = new List<DisplayInfo>();
        try
        {
            foreach (var s in System.Windows.Forms.Screen.AllScreens)
            {
                double scale = ScaleFor(s.Bounds.X + s.Bounds.Width / 2, s.Bounds.Y + s.Bounds.Height / 2);
                list.Add(new DisplayInfo(
                    s.DeviceName ?? "", s.Primary,
                    s.Bounds.X, s.Bounds.Y, s.Bounds.Width, s.Bounds.Height,
                    s.WorkingArea.X, s.WorkingArea.Y, s.WorkingArea.Width, s.WorkingArea.Height,
                    scale));
            }
        }
        catch (Exception ex)
        {
            Log.Warn("枚举显示器失败：" + ex.Message);
        }
        return list;
    }

    /// <summary>第一个副屏；只有一块屏时返回 null。</summary>
    public static DisplayInfo? Secondary(List<DisplayInfo> all)
        => all.FirstOrDefault(d => !d.Primary);

    public static DisplayInfo? Primary(List<DisplayInfo> all)
        => all.FirstOrDefault(d => d.Primary) ?? all.FirstOrDefault();

    /// <summary>某个物理坐标点落在哪块屏上（用于判断窗口当前在哪个显示器）。</summary>
    public static DisplayInfo? FromPoint(List<DisplayInfo> all, double px, double py)
        => all.FirstOrDefault(d =>
            px >= d.X && px < d.X + d.Width && py >= d.Y && py < d.Y + d.Height);

    private static double ScaleFor(int px, int py)
    {
        try
        {
            IntPtr mon = MonitorFromPoint(new POINT { X = px, Y = py }, 2 /*MONITOR_DEFAULTTONEAREST*/);
            if (mon != IntPtr.Zero && GetDpiForMonitor(mon, 0 /*EFFECTIVE*/, out uint dx, out _) == 0 && dx > 0)
                return dx / 96.0;
        }
        catch { }
        return 1.0;
    }
}
