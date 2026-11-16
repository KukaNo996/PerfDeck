using System;
using Microsoft.Win32;

namespace PerfDeck.Core;

public static class AutoStart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string Name = "PerfDeck";

    public static void Apply(bool enable)
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKey, true);
            if (k == null) return;
            if (enable)
            {
                string exe = Environment.ProcessPath ?? "";
                if (!exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return;
                k.SetValue(Name, "\"" + exe + "\"");
            }
            else
            {
                k.DeleteValue(Name, false);
            }
        }
        catch (Exception ex)
        {
            Log.Warn("开机自启设置失败：" + ex.Message);
        }
    }

    public static bool IsEnabled()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKey, false);
            return k?.GetValue(Name) != null;
        }
        catch { return false; }
    }
}
