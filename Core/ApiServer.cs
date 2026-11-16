using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace PerfDeck.Core;

/// <summary>
/// 对外硬件数据接口（本地 HTTP，v1）。
///
/// 设计取舍：
/// - **只用回环地址**（127.0.0.1），不对外暴露，也不触发防火墙弹窗。
/// - 选 HTTP 而不是命名管道：外部程序用 curl / PowerShell / Python 都能调，语言无关。
/// - **只读**：没有任何会改变监控状态的接口，避免外部脚本把面板玩坏。
/// - 快照通过委托实时读取，采集线程与 HTTP 线程之间不共享可变状态
///   （每次采样都是新的 Snapshot 对象，引用交换即可保证可见性）。
/// </summary>
public sealed class ApiServer : IDisposable
{
    private readonly Func<Snapshot?> _provider;
    private readonly int _port;
    private readonly string _appVersion;
    private HttpListener? _listener;
    private Thread? _thread;
    private volatile bool _running;
    private DateTime _startedAt = DateTime.Now;

    public ApiServer(int port, Func<Snapshot?> provider, string appVersion = "1.0.0")
    {
        _port = port;
        _provider = provider;
        _appVersion = appVersion;
    }

    public int Port => _port;
    public bool IsRunning => _running;
    public string BaseUrl => $"http://127.0.0.1:{_port}{HardwareApi.BasePath}";

    /// <summary>启动服务；失败只记日志，不影响面板本身。</summary>
    public bool Start()
    {
        if (_port <= 0) return false;
        if (_running) return true;

        try
        {
            var listener = new HttpListener();
            // 只监听回环；端口被占用时不会波及其它服务
            listener.Prefixes.Add($"http://127.0.0.1:{_port}/");
            listener.Start();
            _listener = listener;
            _running = true;
            _startedAt = DateTime.Now;

            _thread = new Thread(Loop) { IsBackground = true, Name = "PerfDeck.Api" };
            _thread.Start();

            Log.Info($"硬件数据接口已启动：{BaseUrl}");
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"硬件数据接口启动失败（端口 {_port}）：{ex.Message}");
            _listener = null;
            _running = false;
            return false;
        }
    }

    public void Stop()
    {
        _running = false;
        try { _listener?.Stop(); } catch { }
        try { _listener?.Close(); } catch { }
        _listener = null;
    }

    private void Loop()
    {
        var listener = _listener;
        if (listener == null) return;

        while (_running)
        {
            HttpListenerContext ctx;
            try { ctx = listener.GetContext(); }
            catch (HttpListenerException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (Exception ex)
            {
                Log.Warn("接口请求处理异常：" + ex.Message);
                continue;
            }

            try { Handle(ctx); }
            catch (Exception ex)
            {
                Log.Warn("接口处理异常：" + ex.Message);
                try { WriteJson(ctx, 500, Error("internal_error", ex.Message)); } catch { }
            }
            finally
            {
                try { ctx.Response.Close(); } catch { }
            }
        }
    }

    private void Handle(HttpListenerContext ctx)
    {
        var req = ctx.Request;
        var path = (req.Url?.AbsolutePath ?? "/").TrimEnd('/');
        if (path.Length == 0) path = "/";

        // 只允许 GET / HEAD
        if (!string.Equals(req.HttpMethod, "GET", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(req.HttpMethod, "HEAD", StringComparison.OrdinalIgnoreCase))
        {
            WriteJson(ctx, 405, Error("unsupported_method",
                $"不支持 {req.HttpMethod} 方法",
                "本接口只提供只读的 GET"));
            return;
        }

        // 版本前缀校验：/api/v2 之类明确告诉调用方
        if (!path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase))
        {
            WriteJson(ctx, 404, Error("unknown_endpoint",
                $"未知路径 {path}",
                $"可用路径前缀：{HardwareApi.BasePath}"));
            return;
        }

        if (!path.StartsWith(HardwareApi.BasePath, StringComparison.OrdinalIgnoreCase))
        {
            WriteJson(ctx, 404, Error("unknown_endpoint",
                $"不支持的接口版本：{path}",
                $"当前版本为 {HardwareApi.BasePath}；v1 只增字段不删不改，破坏性变更会另开 /api/v2"));
            return;
        }

        var route = path[HardwareApi.BasePath.Length..];
        if (route.Length == 0) route = "/";

        switch (route.ToLowerInvariant())
        {
            case "/health":
                WriteJson(ctx, 200, Ok(new Dictionary<string, object?>
                {
                    ["status"] = "ok",
                    ["appVersion"] = _appVersion,
                    ["apiVersion"] = HardwareApi.Version,
                    ["uptimeSeconds"] = (int)(DateTime.Now - _startedAt).TotalSeconds,
                    ["sampledAt"] = _provider()?.Now.ToString("yyyy-MM-ddTHH:mm:ss"),
                }));
                return;

            case "/version":
                WriteJson(ctx, 200, Ok(new Dictionary<string, object?>
                {
                    ["apiVersion"] = HardwareApi.Version,
                    ["appVersion"] = _appVersion,
                    ["basePath"] = HardwareApi.BasePath,
                }));
                return;

            case "/schema":
                WriteJson(ctx, 200, Ok(HardwareApi.BuildSchema()));
                return;

            case "/snapshot":
                {
                    var snap = _provider();
                    if (snap == null)
                    {
                        WriteJson(ctx, 503, Error("not_ready", "还没有采到第一帧数据", "启动后约 1 秒内可用"));
                        return;
                    }
                    WriteJson(ctx, 200, Ok(HardwareApi.BuildSnapshot(snap)));
                    return;
                }

            case "/summary":
                {
                    var snap = _provider();
                    if (snap == null)
                    {
                        WriteJson(ctx, 503, Error("not_ready", "还没有采到第一帧数据", "启动后约 1 秒内可用"));
                        return;
                    }

                    var q = req.Url?.Query ?? "";
                    string? fields = GetQueryValue(q, "fields");
                    bool pretty = GetQueryValue(q, "pretty") is "1" or "true";

                    var data = HardwareApi.FilterFields(HardwareApi.BuildSnapshot(snap), fields);
                    WriteJson(ctx, 200, Ok(data), pretty);
                    return;
                }

            default:
                WriteJson(ctx, 404, Error("unknown_endpoint",
                    $"未知路径 {path}",
                    $"可用路径：{HardwareApi.BasePath}/health | /version | /schema | /snapshot | /summary"));
                return;
        }
    }

    private static string? GetQueryValue(string query, string key)
    {
        if (string.IsNullOrEmpty(query)) return null;
        foreach (var part in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = part.IndexOf('=');
            if (eq <= 0) continue;
            if (!string.Equals(part[..eq], key, StringComparison.OrdinalIgnoreCase)) continue;
            return Uri.UnescapeDataString(part[(eq + 1)..]);
        }
        return null;
    }

    // ---------- 响应封装 ----------

    private static Dictionary<string, object?> Ok(object? data) => new()
    {
        ["apiVersion"] = HardwareApi.Version,
        ["ok"] = true,
        ["timestamp"] = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss"),
        ["data"] = data,
    };

    private static Dictionary<string, object?> Error(string code, string message, string? hint = null) => new()
    {
        ["apiVersion"] = HardwareApi.Version,
        ["ok"] = false,
        ["timestamp"] = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss"),
        ["error"] = new Dictionary<string, object?>
        {
            ["code"] = code,
            ["message"] = message,
            ["hint"] = hint,
        },
    };

    private static void WriteJson(HttpListenerContext ctx, int status, Dictionary<string, object?> body, bool pretty = false)
    {
        var opts = new JsonSerializerOptions
        {
            WriteIndented = pretty,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };
        byte[] buf = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(body, opts));

        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "application/json; charset=utf-8";
        ctx.Response.ContentLength64 = buf.Length;
        // 允许本地网页跨端口调用
        ctx.Response.Headers["Access-Control-Allow-Origin"] = "*";
        ctx.Response.Headers["Cache-Control"] = "no-store";
        ctx.Response.OutputStream.Write(buf, 0, buf.Length);
    }

    public void Dispose()
    {
        Stop();
        try { _thread?.Join(500); } catch { }
    }
}
