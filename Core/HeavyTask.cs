using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace PerfDeck.Core;

/// <summary>
/// 判断当前是否正在跑“重型任务”（全屏独占的游戏、渲染、视频等）。
/// 只做轻量判断：前台窗口是否铺满整块屏幕、进程是否在游戏名单里。
/// </summary>
public static class HeavyTask
{
    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsZoomed(IntPtr hWnd);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out int value, int size);

    /// <summary>常见的启动器 / 桌面外壳，全屏也不算重型任务。</summary>
    private static readonly HashSet<string> Ignored = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer", "PerfDeck", "ShellExperienceHost", "SearchHost", "StartMenuExperienceHost",
        "Taskmgr", "ApplicationFrameHost", "LockApp", "TextInputHost"
    };

    /// <summary>命中即认为进入重型任务。</summary>
    public static bool IsBusy(string ownProcessName, out string reason)
    {
        reason = "";
        try
        {
            IntPtr hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return false;

            GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == 0) return false;

            string name = ProcessNameOf(pid);
            if (string.IsNullOrEmpty(name)) return false;
            if (Ignored.Contains(name)) return false;
            if (string.Equals(name, ownProcessName, StringComparison.OrdinalIgnoreCase)) return false;

            if (!GetWindowRect(hwnd, out var r)) return false;
            int w = r.Right - r.Left;
            int h = r.Bottom - r.Top;
            if (w < 320 || h < 240) return false;

            // 全屏判定：窗口左上角贴 (0,0) 且铺满整块屏幕（覆盖任务栏）
            var screen = DisplayHelper.Primary(DisplayHelper.GetDisplays());
            int sw = (int)(screen?.Width ?? 0);
            int sh = (int)(screen?.Height ?? 0);
            if (sw <= 0 || sh <= 0) return false;

            bool fullscreen = Math.Abs(r.Left) <= 2 && Math.Abs(r.Top) <= 2
                              && w >= sw - 2 && h >= sh - 2;
            if (!fullscreen)
            {
                // 无边框全屏游戏有时不含任务栏区域，退一步按“接近满屏”判断
                fullscreen = Math.Abs(r.Left) <= 2 && Math.Abs(r.Top) <= 2
                             && w >= sw * 0.98 && h >= sh * 0.9;
            }
            if (!fullscreen) return false;

            reason = name + " 全屏运行";
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string ProcessNameOf(uint pid)
    {
        try
        {
            using var p = Process.GetProcessById((int)pid);
            return p.ProcessName;
        }
        catch
        {
            return "";
        }
    }
}
