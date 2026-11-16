using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace PerfDeck.Core;

public sealed class NetStats
{
    public const int HistoryLen = 48;

    public double? LatencyMs;
    public double AvgMs;
    public double JitterMs;
    public double LossPct;
    public double DownBps;
    public double UpBps;
    public bool LinkUp;
    public string LinkSpeed = "";
    public bool IcmpBlocked;
    public bool HasData;

    /// <summary>本地网卡参数（低频刷新，网络速率卡片左下角展示）</summary>
    public string IfName = "";
    public string Ipv4 = "";
    public string Ipv4Mask = "";
    /// <summary>IPv4 前缀长度（由子网掩码换算，如 255.255.255.0 → 24）</summary>
    public int Ipv4Prefix;
    public string Ipv6 = "";
    public string Gateway = "";
    public string Dns = "";
    public int Mtu;
    /// <summary>延迟历史，NaN 表示该次探测丢失。</summary>
    public double[] History = new double[HistoryLen];

    public NetStats Clone()
    {
        var c = (NetStats)MemberwiseClone();
        c.History = (double[])History.Clone();
        return c;
    }
}

/// <summary>
/// 网络稳定性探测：ICMP 延迟 / 抖动 / 丢包，ICMP 被限制时自动回落到 TCP 握手计时。
/// </summary>
public sealed class NetMonitor : IDisposable
{
    private readonly AppConfig _cfg;
    private readonly Ping _ping = new();
    private readonly object _gate = new();
    private readonly double[] _hist = new double[NetStats.HistoryLen];
    private int _histIdx;
    private int _filled;
    private bool _icmpBlocked;
    private int _consecFails;
    private NetworkInterface? _nic;
    private long _lastRx, _lastTx;
    private DateTime _lastTpSample = DateTime.MinValue;
    private DateTime _lastInfoSample = DateTime.MinValue;
    private string _ipv4 = "", _ipv4Mask = "", _ipv6 = "", _gw = "", _dns = "", _ifName = "";
    private int _mtu;
    private int _prefix;
    private double _down, _up;
    private CancellationTokenSource? _cts;
    private Task? _loop;

    public NetStats Latest { get; private set; } = new();

    public NetMonitor(AppConfig cfg)
    {
        _cfg = cfg;
        for (int i = 0; i < _hist.Length; i++) _hist[i] = double.NaN;
    }

    public void Start()
    {
        Stop();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _loop = Task.Run(async () =>
        {
            int interval = Math.Clamp(_cfg.RefreshMs, 1000, 5000);
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await SampleOnceAsync(token);
                }
                catch (Exception ex)
                {
                    Log.Warn("网络探测异常：" + ex.Message);
                }
                try { await Task.Delay(interval, token); } catch { break; }
            }
        }, token);
    }

    public void Stop()
    {
        try { _cts?.Cancel(); } catch { }
        _cts?.Dispose();
        _cts = null;
    }

    /// <summary>刷新频率或探测目标变更后重建采样循环。</summary>
    public void Restart()
    {
        Start();
    }

    private async Task SampleOnceAsync(CancellationToken token)
    {
        string host = _cfg.PingHost;
        int timeout = _cfg.PingTimeoutMs;

        double rtt = double.NaN;

        if (!_icmpBlocked)
        {
            try
            {
                var reply = await _ping.SendPingAsync(host, timeout).ConfigureAwait(false);
                if (reply.Status == IPStatus.Success) rtt = reply.RoundtripTime;
            }
            catch { rtt = double.NaN; }

            if (double.IsNaN(rtt))
            {
                _consecFails++;
                if (_consecFails >= 5)
                {
                    // 连续丢包：判断是否是本机屏蔽了 ICMP
                    bool tcpOk = await TcpProbeAsync(host, 443, timeout).ConfigureAwait(false);
                    if (tcpOk)
                    {
                        _icmpBlocked = true;
                        Log.Info("ICMP 被屏蔽，改用 TCP 探测：" + host);
                    }
                }
            }
            else _consecFails = 0;
        }
        else
        {
            var ms = await TcpTimingAsync(host, 443, timeout).ConfigureAwait(false);
            if (ms.HasValue) rtt = ms.Value;
        }

        long rx = 0, tx = 0;
        bool linkUp = false;
        string linkSpeed = "";
        var nic = PickNic();
        if (nic != null)
        {
            try
            {
                var st = nic.GetIPv4Statistics();
                rx = st.BytesReceived;
                tx = st.BytesSent;
                linkUp = nic.OperationalStatus == OperationalStatus.Up;
                long sp = nic.Speed;
                linkSpeed = sp >= 1_000_000_000 ? (sp / 1_000_000_000.0).ToString("0.#") + " Gbps"
                          : sp >= 1_000_000 ? (sp / 1_000_000).ToString("0") + " Mbps" : "";
            }
            catch { }
        }

        DateTime now = DateTime.Now;
        if (now - _lastInfoSample > TimeSpan.FromSeconds(30))
        {
            _lastInfoSample = now;
            SampleNicInfo(nic);
        }

        if (_lastTpSample != DateTime.MinValue)
        {
            double sec = (now - _lastTpSample).TotalSeconds;
            if (sec > 0.05)
            {
                double dRx = Math.Max(0, rx - _lastRx);
                double dTx = Math.Max(0, tx - _lastTx);
                // 指数平滑，读数抖动更小
                _down = _down * 0.55 + (dRx / sec) * 0.45;
                _up = _up * 0.55 + (dTx / sec) * 0.45;
            }
        }
        _lastRx = rx; _lastTx = tx; _lastTpSample = now;

        lock (_gate)
        {
            _hist[_histIdx] = rtt;
            _histIdx = (_histIdx + 1) % _hist.Length;
            _filled = Math.Min(_filled + 1, _hist.Length);

            var st = new NetStats
            {
                LatencyMs = double.IsNaN(rtt) ? null : Math.Round(rtt, 0),
                DownBps = _down,
                UpBps = _up,
                LinkUp = linkUp,
                LinkSpeed = linkSpeed,
                IcmpBlocked = _icmpBlocked,
                HasData = true,
                IfName = _ifName,
                Ipv4 = _ipv4,
                Ipv4Mask = _ipv4Mask,
                Ipv4Prefix = _prefix,
                Ipv6 = _ipv6,
                Gateway = _gw,
                Dns = _dns,
                Mtu = _mtu
            };

            int total = 0, lost = 0;
            double sum = 0;
            var vals = new List<double>(_hist.Length);
            for (int i = 0; i < _hist.Length; i++)
            {
                // 按时间顺序展开
                int idx = (_histIdx + i) % _hist.Length;
                double v = _hist[idx];
                st.History[i] = v;

                // 尚未采到的槽位不参与统计（避免刚启动时显示 100% 丢包）
                if (i < _hist.Length - _filled) continue;

                total++;
                if (double.IsNaN(v)) lost++;
                else { sum += v; vals.Add(v); }
            }

            st.LossPct = total > 0 ? lost * 100.0 / total : 0;
            st.AvgMs = vals.Count > 0 ? sum / vals.Count : 0;

            double jsum = 0;
            int jn = 0;
            for (int i = 1; i < vals.Count; i++)
            {
                jsum += Math.Abs(vals[i] - vals[i - 1]);
                jn++;
            }
            st.JitterMs = jn > 0 ? jsum / jn : 0;

            Latest = st;
        }

        await Task.CompletedTask;
    }

    /// <summary>读取当前网卡的 IPv4/IPv6、子网掩码、默认网关、DNS 与 MTU。</summary>
    private void SampleNicInfo(NetworkInterface? nic)
    {
        _ipv4 = _ipv4Mask = _ipv6 = _gw = _dns = _ifName = "";
        _mtu = 0;
        _prefix = 0;
        if (nic == null) return;

        try
        {
            _ifName = nic.Name;
            var props = nic.GetIPProperties();

            foreach (var ua in props.UnicastAddresses)
            {
                var addr = ua.Address;
                if (addr.AddressFamily == AddressFamily.InterNetwork && _ipv4.Length == 0)
                {
                    _ipv4 = addr.ToString();
                    try
                    {
                        var mask = ua.IPv4Mask;
                        var m = mask?.ToString();
                        if (!string.IsNullOrEmpty(m) && m != "0.0.0.0")
                        {
                            _ipv4Mask = m;
                            _prefix = PrefixLength(mask!);
                        }
                    }
                    catch { }
                    try { _mtu = props.GetIPv4Properties()?.Mtu ?? 0; } catch { }
                }
                else if (addr.AddressFamily == AddressFamily.InterNetworkV6 && _ipv6.Length == 0)
                {
                    // 优先取全球地址，链路本地（fe80::）只作兜底
                    bool global = !addr.IsIPv6LinkLocal && !addr.IsIPv6SiteLocal;
                    if (global || _ipv6.Length == 0) _ipv6 = addr.ToString();
                    if (_mtu == 0) { try { _mtu = props.GetIPv6Properties()?.Mtu ?? 0; } catch { } }
                }
            }

            var gw = props.GatewayAddresses
                .FirstOrDefault(g => g.Address.AddressFamily == AddressFamily.InterNetwork)
                ?? props.GatewayAddresses.FirstOrDefault();
            if (gw != null) _gw = gw.Address.ToString();

            _dns = string.Join(", ", props.DnsAddresses
                .Where(a => a.AddressFamily == AddressFamily.InterNetwork)
                .Select(a => a.ToString())
                .Take(2));
        }
        catch { }
    }

    /// <summary>子网掩码 → 前缀长度。</summary>
    private static int PrefixLength(System.Net.IPAddress mask)
    {
        int bits = 0;
        foreach (byte b in mask.GetAddressBytes())
            for (int i = 7; i >= 0; i--)
                if ((b & (1 << i)) != 0) bits++;
        return bits;
    }

    private NetworkInterface? PickNic()
    {
        try
        {
            if (_nic != null && _nic.OperationalStatus == OperationalStatus.Up &&
                _nic.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                return _nic;
        }
        catch { _nic = null; }

        try
        {
            var list = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up)
                .Where(n => n.NetworkInterfaceType is NetworkInterfaceType.Ethernet
                            or NetworkInterfaceType.Wireless80211
                            or NetworkInterfaceType.GigabitEthernet
                            or NetworkInterfaceType.Ppp)
                .Where(n => n.GetIPProperties().GatewayAddresses.Count > 0)
                .OrderByDescending(n => { try { return n.Speed; } catch { return 0; } })
                .ToList();

            _nic = list.FirstOrDefault();
        }
        catch { }
        return _nic;
    }

    private static async Task<bool> TcpProbeAsync(string host, int port, int timeoutMs)
    {
        var ms = await TcpTimingAsync(host, port, timeoutMs).ConfigureAwait(false);
        return ms.HasValue;
    }

    private static async Task<double?> TcpTimingAsync(string host, int port, int timeoutMs)
    {
        try
        {
            using var client = new TcpClient();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var task = client.ConnectAsync(host, port);
            var done = await Task.WhenAny(task, Task.Delay(timeoutMs)).ConfigureAwait(false);
            if (done != task || !task.IsCompletedSuccessfully) return null;
            sw.Stop();
            return sw.Elapsed.TotalMilliseconds;
        }
        catch { return null; }
    }

    public void Dispose()
    {
        Stop();
        try { _ping.Dispose(); } catch { }
    }
}
