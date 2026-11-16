using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using PerfDeck.Core;
using PerfDeck.UI;

namespace PerfDeck;

public partial class App : Application
{
    private Mutex? _mutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 长期运行：异常只记录，不中断
        DispatcherUnhandledException += (_, args) =>
        {
            Log.Error("UI 线程异常：" + args.Exception);
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Log.Error("未处理异常：" + args.ExceptionObject);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Error("后台任务异常：" + args.Exception);
            args.SetObserved();
        };

        // 热搜自检：不占用单实例、不开窗口，抓一轮写日志后退出
        if (e.Args.Any(a => a.Equals("--hotcheck", StringComparison.OrdinalIgnoreCase)))
        {
            RunHotCheck();
            Shutdown();
            return;
        }

        // --api-dump：采一次数据写成 JSON 文件后退出（脚本/命令行取数用）
        // 必须放在单实例检查之前，否则面板正在跑时这条命令会被互斥拦掉
        if (e.Args.Any(a => a.Equals("--api-dump", StringComparison.OrdinalIgnoreCase)))
        {
            RunApiDump(AppConfig.Load());
            Shutdown();
            return;
        }

        _mutex = new Mutex(true, @"Local\PerfDeck.SingleInstance", out bool created);
        if (!created)
        {
            ActivateExisting();
            Shutdown();
            return;
        }

        var cfg = AppConfig.Load();

        // --api-port N：临时改接口端口（不写回配置）
        int portIdx = Array.FindIndex(e.Args, a => a.Equals("--api-port", StringComparison.OrdinalIgnoreCase));
        if (portIdx >= 0 && portIdx + 1 < e.Args.Length && int.TryParse(e.Args[portIdx + 1], out int port))
            cfg.ApiPort = port;

        if (e.Args.Any(a => a.Equals("--dump", StringComparison.OrdinalIgnoreCase)))
        {
            DumpSensors();
            Shutdown();
            return;
        }

        bool noElevate = e.Args.Any(a => a.Equals("--no-elevate", StringComparison.OrdinalIgnoreCase));
        if (!noElevate && cfg.AutoElevate && !Native.IsElevated())
        {
            if (TryElevate())
            {
                Shutdown();
                return;
            }
        }

        // 先注入主题资源，窗口 XAML 解析时才能取到 DynamicResource
        ThemeService.Apply(Themes.Find(cfg.Theme));

        var win = new MainWindow(cfg);
        MainWindow = win;
        win.Show();

        // --settings：启动后直接打开设置窗口（排障 / 独立调整设置时用）
        // --settings：启动后直接打开设置窗口（排障 / 单独调设置时用）
        if (e.Args.Any(a => a.Equals("--settings", StringComparison.OrdinalIgnoreCase)))
            Dispatcher.BeginInvoke(new Action(() => win.ShowSettings()), DispatcherPriority.Background);
    }

    /// <summary>命令行导出一次硬件数据（JSON 文件），用于脚本取数，不启动界面。</summary>
    private static void RunApiDump(AppConfig cfg)
    {
        try
        {
            using var sensors = new SensorService();
            sensors.Initialize();
            var snap = sensors.Sample();
            var data = HardwareApi.BuildSnapshot(snap);

            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PerfDeck");
            Directory.CreateDirectory(dir);
            string file = Path.Combine(dir, "api-dump.json");

            var json = System.Text.Json.JsonSerializer.Serialize(
                new Dictionary<string, object?>
                {
                    ["apiVersion"] = HardwareApi.Version,
                    ["ok"] = true,
                    ["timestamp"] = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss"),
                    ["data"] = data,
                },
                new System.Text.Json.JsonSerializerOptions
                {
                    WriteIndented = true,
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                });

            File.WriteAllText(file, json);
            Log.Info($"[API-DUMP] 已写入 {file}");
        }
        catch (Exception ex)
        {
            Log.Error("[API-DUMP] 失败：" + ex.Message);
        }
    }

    /// <summary>命令行热搜自检：把每个平台的抓取条数与首条标题写进日志。</summary>
    private static void RunHotCheck()
    {
        try
        {
            var cfg = AppConfig.Load();
            using var svc = new HotSearchService(cfg);
            string summary = svc.ProbeAsync().GetAwaiter().GetResult();
            Log.Info("[HOTCHECK] " + summary);
        }
        catch (Exception ex)
        {
            Log.Error("[HOTCHECK] 失败：" + ex);
        }
    }

    private static void DumpSensors()
    {
        try
        {
            using var svc = new SensorService();
            svc.Initialize();
            svc.Sample();
            System.Threading.Thread.Sleep(800);
            var snap = svc.Sample();

            var sb = new System.Text.StringBuilder();
            sb.AppendLine("=== 传感器清单 ===");
            sb.AppendLine(svc.Describe());
            sb.AppendLine("=== 一次采样 ===");
            sb.AppendLine("CPU: " + snap.Cpu);
            foreach (var g in snap.Gpus) sb.AppendLine("GPU: " + g);
            foreach (var d in snap.Disks) sb.AppendLine("DISK: " + d);
            sb.AppendLine($"MEM: {snap.MemUsedPct:0.0}% {snap.MemUsedGb:0.0}/{snap.MemTotalGb:0.0} GB");

            string path = System.IO.Path.Combine(AppConfig.DefaultDir, "sensors.txt");
            System.IO.Directory.CreateDirectory(AppConfig.DefaultDir);
            System.IO.File.WriteAllText(path, sb.ToString());
            Log.Info("传感器清单已导出：" + path);
        }
        catch (Exception ex)
        {
            Log.Error("导出传感器清单失败：" + ex);
        }
    }

    private static bool TryElevate()    {
        try
        {
            string? exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe) ||
                !exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
                exe.EndsWith("dotnet.exe", StringComparison.OrdinalIgnoreCase))
                return false;

            var psi = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = "--elevated",
                UseShellExecute = true,
                Verb = "runas"
            };
            Process.Start(psi);
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn("提权被取消或失败，继续以普通权限运行：" + ex.Message);
            return false;
        }
    }

    private static void ActivateExisting()
    {
        try
        {
            var p = Process.GetProcessesByName("PerfDeck").FirstOrDefault(x => x.Id != Environment.ProcessId);
            if (p != null && p.MainWindowHandle != IntPtr.Zero)
                SetForegroundWindow(p.MainWindowHandle);
        }
        catch { }
    }

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    protected override void OnExit(ExitEventArgs e)
    {
        try { _mutex?.ReleaseMutex(); } catch { }
        _mutex?.Dispose();
        base.OnExit(e);
    }
}
