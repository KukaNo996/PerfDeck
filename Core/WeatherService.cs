using System;
using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace PerfDeck.Core;

/// <summary>某一天的预报。</summary>
public sealed record DayForecast(string Label, double Hi, double Lo, int Code, bool IsToday);

public sealed record WeatherSnap
{
    public double TempC { get; init; }
    public double HiC { get; init; }
    public double LoC { get; init; }
    public string Condition { get; init; } = "";
    /// <summary>WMO 天气代码，用于选图标</summary>
    public int Code { get; init; } = 3;
    public string City { get; init; } = "";
    public double Humidity { get; init; }
    public double WindKmh { get; init; }
    public DateTime Updated { get; init; } = DateTime.Now;
    public string? Error { get; init; }
    /// <summary>auto = 自动定位，manual = 手动城市</summary>
    public string Mode { get; init; } = "auto";
    /// <summary>未来几天预报（含今天），长度 0 表示只有当前实况。</summary>
    public System.Collections.Generic.List<DayForecast> Days { get; init; } = new();
    public bool HasData => Error == null && !string.IsNullOrEmpty(City) && Condition.Length > 0;
}

/// <summary>
/// 天气查询。全部使用免密钥公开接口，且带多级降级：
///   定位：太平洋网络 IP 库（国内、快）→ ip-api → ipapi.co
///   天气：Open-Meteo → wttr.in
/// 定位只取城市与经纬度用于查询天气，不写入配置、不上传其他信息。
/// 任何一步失败都只在卡片上提示，不影响其他监控项。
/// </summary>
public sealed class WeatherService : IDisposable
{
    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
        // 部分免费接口缺少 User-Agent 会直接 403
        c.DefaultRequestHeaders.UserAgent.ParseAdd("PerfDeck/1.0 (Windows; weather widget)");
        c.DefaultRequestHeaders.Accept.ParseAdd("application/json, text/plain, */*");
        return c;
    }
    private readonly AppConfig _cfg;
    private CancellationTokenSource? _cts;

    static WeatherService()
    {
        try { Encoding.RegisterProvider(CodePagesEncodingProvider.Instance); } catch { }
    }

    public WeatherSnap? Latest { get; private set; }

    public WeatherService(AppConfig cfg) => _cfg = cfg;

    public void Start()
    {
        Stop();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        _ = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try { await RefreshOnceAsync(token).ConfigureAwait(false); }
                catch (Exception ex)
                {
                    Log.Warn("天气刷新异常：" + ex.Message);
                    Publish(new WeatherSnap { Error = "天气获取失败", Mode = _cfg.WeatherMode });
                }

                try { await Task.Delay(TimeSpan.FromMinutes(15), token).ConfigureAwait(false); }
                catch { break; }
            }
        }, token);
    }

    public void Stop()
    {
        try { _cts?.Cancel(); } catch { }
        _cts?.Dispose();
        _cts = null;
    }

    private async Task RefreshOnceAsync(CancellationToken token)
    {
        bool useGeo = !string.Equals(_cfg.WeatherMode, "manual", StringComparison.OrdinalIgnoreCase);
        string mode = useGeo ? "auto" : "manual";

        string city = (_cfg.WeatherCity ?? "").Trim();
        double? lat = null, lon = null;

        if (useGeo)
        {
            if (_cfg.WeatherGeoConsent == false)
            {
                Publish(new WeatherSnap { Error = "定位权限未授予，可在设置中改为手动城市", Mode = mode });
                return;
            }

            var loc = await LocateAsync(token).ConfigureAwait(false);
            if (loc == null)
            {
                Publish(new WeatherSnap { Error = "自动定位失败，可在设置中手动填写城市", Mode = mode });
                return;
            }

            city = loc.Value.city;
            lat = loc.Value.lat;
            lon = loc.Value.lon;
        }

        if (city.Length == 0 && (lat == null || lon == null))
        {
            Publish(new WeatherSnap { Error = "尚未设置城市", Mode = mode });
            return;
        }

        // 手动填的城市名多是中文，open-meteo 的地名库对中文覆盖很差
        // （例如"咸宁"查不到、"xianning"才查得到）。所以手动模式先用
        // 支持中文查询的 wttr.in 直连，成功就不再走地理编码。
        if (lat == null && !string.IsNullOrWhiteSpace(city))
        {
            var direct = await FetchWttrAsync(city, mode, token).ConfigureAwait(false);
            if (direct is { HasData: true })
            {
                Publish(direct);
                return;
            }
        }

        // 只有城市名时先解析成经纬度
        if (lat == null || lon == null)
        {
            var geo = await GeocodeAsync(city, token).ConfigureAwait(false);
            if (geo == null)
            {
                Publish(new WeatherSnap { Error = $"未找到城市「{city}」", Mode = mode, City = city });
                return;
            }
            lat = geo.Value.lat;
            lon = geo.Value.lon;
            city = geo.Value.name;
        }

        WeatherSnap? snap = await FetchOpenMeteoAsync(lat.Value, lon.Value, city, mode, token).ConfigureAwait(false);

        // 主接口失败时再退一层
        if (snap == null && city.Length > 0)
            snap = await FetchWttrAsync(city, mode, token).ConfigureAwait(false);

        Publish(snap ?? new WeatherSnap { Error = "天气服务暂时不可用", Mode = mode, City = city });
    }

    private void Publish(WeatherSnap snap) => Latest = snap;

    // ---------------- 定位 ----------------

    /// <summary>IP 归属地定位，返回城市名与（可能有的）经纬度。</summary>
    private static async Task<(double? lat, double? lon, string city)?> LocateAsync(CancellationToken token)
    {
        // 1) ip-api：直接给出经纬度，最有用
        try
        {
            string json = await Http.GetStringAsync("http://ip-api.com/json/?lang=zh-CN", token).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.TryGetProperty("status", out var st) && st.GetString() == "success")
            {
                return (root.GetProperty("lat").GetDouble(),
                        root.GetProperty("lon").GetDouble(),
                        root.TryGetProperty("city", out var c) ? c.GetString() ?? "" : "");
            }
        }
        catch (Exception ex)
        {
            Log.Warn("IP 定位失败（ip-api）：" + ex.Message);
        }

        // 2) 太平洋网络：国内速度快，只有城市名
        try
        {
            byte[] bytes = await Http.GetByteArrayAsync("https://whois.pconline.com.cn/ipJson.jsp?json=true", token)
                                      .ConfigureAwait(false);
            string text = Encoding.GetEncoding("GB18030").GetString(bytes);
            int i = text.IndexOf('{');
            int j = text.LastIndexOf('}');
            if (i >= 0 && j > i)
            {
                using var doc = JsonDocument.Parse(text.Substring(i, j - i + 1));
                var root = doc.RootElement;
                string city = root.TryGetProperty("city", out var c) ? c.GetString() ?? "" : "";
                string pro = root.TryGetProperty("pro", out var p) ? p.GetString() ?? "" : "";
                if (city.Trim().Length > 0) return (null, null, city.Trim());
                if (pro.Trim().Length > 0) return (null, null, pro.Trim());
            }
        }
        catch (Exception ex)
        {
            Log.Warn("IP 定位失败（pconline）：" + ex.Message);
        }

        // 3) ipapi.co
        try
        {
            string json = await Http.GetStringAsync("https://ipapi.co/json/", token).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.TryGetProperty("latitude", out var la) && root.TryGetProperty("longitude", out var lo))
            {
                return (la.GetDouble(), lo.GetDouble(),
                        root.TryGetProperty("city", out var c) ? c.GetString() ?? "" : "");
            }
        }
        catch (Exception ex)
        {
            Log.Warn("IP 定位失败（ipapi）：" + ex.Message);
        }

        return null;
    }

    /// <summary>中文地名常见后缀，查不到时逐层剥掉/补上再试。</summary>
    private static readonly string[] Suffixes = { "市", "地区", "自治州", "区", "县", "镇", "街道" };

    private static async Task<(double lat, double lon, string name)?> GeocodeAsync(string city, CancellationToken token)
    {
        foreach (var query in new[] { city, city.Replace("市", "").Replace("省", "") })
        {
            try
            {
                string url = "https://geocoding-api.open-meteo.com/v1/search?count=1&language=zh&format=json&name="
                             + Uri.EscapeDataString(query);
                string json = await Http.GetStringAsync(url, token).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("results", out var arr) || arr.GetArrayLength() == 0)
                    continue;

                var first = arr[0];
                return (first.GetProperty("latitude").GetDouble(),
                        first.GetProperty("longitude").GetDouble(),
                        first.TryGetProperty("name", out var n) ? n.GetString() ?? city : city);
            }
            catch (Exception ex)
            {
                Log.Warn("城市解析失败：" + ex.Message);
            }
        }
        return null;
    }

    // ---------------- 天气 ----------------

    private static async Task<WeatherSnap?> FetchOpenMeteoAsync(double lat, double lon, string city, string mode, CancellationToken token)
    {
        try
        {
            string url = "https://api.open-meteo.com/v1/forecast"
                         + $"?latitude={lat.ToString("0.####", CultureInfo.InvariantCulture)}"
                         + $"&longitude={lon.ToString("0.####", CultureInfo.InvariantCulture)}"
                         + "&current=temperature_2m,relative_humidity_2m,weather_code,wind_speed_10m"
                         + "&daily=temperature_2m_max,temperature_2m_min,weather_code&timezone=auto&forecast_days=6";

            string json = await Http.GetStringAsync(url, token).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var cur = root.GetProperty("current");
            double temp = cur.GetProperty("temperature_2m").GetDouble();
            int code = cur.GetProperty("weather_code").GetInt32();
            double hum = cur.TryGetProperty("relative_humidity_2m", out var h) ? h.GetDouble() : 0;
            double wind = cur.TryGetProperty("wind_speed_10m", out var w) ? w.GetDouble() : 0;

            double hi = temp, lo = temp;
            var days = new System.Collections.Generic.List<DayForecast>();

            if (root.TryGetProperty("daily", out var daily))
            {
                if (daily.TryGetProperty("temperature_2m_max", out var mx) && mx.GetArrayLength() > 0)
                    hi = mx[0].GetDouble();
                if (daily.TryGetProperty("temperature_2m_min", out var mn) && mn.GetArrayLength() > 0)
                    lo = mn[0].GetDouble();

                // 逐日展开：日期 / 高低温 / 天气代码
                if (daily.TryGetProperty("time", out var times)
                    && daily.TryGetProperty("temperature_2m_max", out var his)
                    && daily.TryGetProperty("temperature_2m_min", out var los))
                {
                    int n = times.GetArrayLength();
                    var codes = daily.TryGetProperty("weather_code", out var wc) ? wc : default;

                    for (int i = 0; i < n && i < 6; i++)
                    {
                        if (his.GetArrayLength() <= i || los.GetArrayLength() <= i) break;

                        string date = times[i].GetString() ?? "";
                        int dc = codes.ValueKind == JsonValueKind.Array && codes.GetArrayLength() > i
                            ? codes[i].GetInt32() : 3;

                        string label;
                        if (i == 0) label = "今天";
                        else if (i == 1) label = "明天";
                        else if (DateTime.TryParse(date, out var dt)) label = WeekName(dt.DayOfWeek);
                        else label = "—";

                        days.Add(new DayForecast(label,
                            Math.Round(his[i].GetDouble()), Math.Round(los[i].GetDouble()), dc, i == 0));
                    }
                }
            }

            return new WeatherSnap
            {
                TempC = Math.Round(temp),
                HiC = Math.Round(hi),
                LoC = Math.Round(lo),
                Condition = DescribeWmo(code),
                Code = code,
                City = city,
                Humidity = hum,
                WindKmh = wind,
                Updated = DateTime.Now,
                Mode = mode,
                Days = days
            };
        }
        catch (Exception ex)
        {
            Log.Warn("天气接口异常（open-meteo）：" + ex.Message);
            return null;
        }
    }

    private static string WeekName(DayOfWeek d) => d switch
    {
        DayOfWeek.Monday => "周一",
        DayOfWeek.Tuesday => "周二",
        DayOfWeek.Wednesday => "周三",
        DayOfWeek.Thursday => "周四",
        DayOfWeek.Friday => "周五",
        DayOfWeek.Saturday => "周六",
        _ => "周日"
    };

    private static async Task<WeatherSnap?> FetchWttrAsync(string city, string mode, CancellationToken token)
    {
        try
        {
            string url = "https://wttr.in/" + Uri.EscapeDataString(city) + "?format=j1&lang=zh-cn";
            string json = await Http.GetStringAsync(url, token).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var cur = root.GetProperty("current_condition")[0];
            double temp = double.Parse(cur.GetProperty("temp_C").GetString() ?? "0", CultureInfo.InvariantCulture);

            string cond = "";
            if (cur.TryGetProperty("lang_zh-cn", out var zh) && zh.GetArrayLength() > 0)
                cond = zh[0].GetProperty("value").GetString() ?? "";
            else if (cur.TryGetProperty("weatherDesc", out var en) && en.GetArrayLength() > 0)
                cond = en[0].GetProperty("value").GetString() ?? "";

            double hi = temp, lo = temp;
            var days = new System.Collections.Generic.List<DayForecast>();

            if (root.TryGetProperty("weather", out var arr) && arr.GetArrayLength() > 0)
            {
                var d0 = arr[0];
                if (d0.TryGetProperty("maxtempC", out var mx))
                    hi = double.Parse(mx.GetString() ?? "0", CultureInfo.InvariantCulture);
                if (d0.TryGetProperty("mintempC", out var mn))
                    lo = double.Parse(mn.GetString() ?? "0", CultureInfo.InvariantCulture);

                // wttr 的 weather 数组就是逐日预报，直接展开
                for (int i = 0; i < arr.GetArrayLength() && i < 6; i++)
                {
                    var d = arr[i];
                    double dh = double.TryParse(d.GetProperty("maxtempC").GetString(), NumberStyles.Any,
                        CultureInfo.InvariantCulture, out var v1) ? v1 : temp;
                    double dl = double.TryParse(d.GetProperty("mintempC").GetString(), NumberStyles.Any,
                        CultureInfo.InvariantCulture, out var v2) ? v2 : temp;

                    string label = i switch
                    {
                        0 => "今天",
                        1 => "明天",
                        _ => d.TryGetProperty("date", out var ds)
                             && DateTime.TryParse(ds.GetString(), out var dt)
                                ? WeekName(dt.DayOfWeek) : "—"
                    };

                    days.Add(new DayForecast(label, Math.Round(dh), Math.Round(dl), 3, i == 0));
                }
            }

            return new WeatherSnap
            {
                TempC = Math.Round(temp),
                HiC = Math.Round(hi),
                LoC = Math.Round(lo),
                Condition = TranslateCond(cond),
                City = city,
                Updated = DateTime.Now,
                Mode = mode,
                Days = days
            };
        }
        catch (Exception ex)
        {
            Log.Warn("天气接口异常（wttr.in）：" + ex.Message);
            return null;
        }
    }

    /// <summary>
    /// wttr.in 的 lang_zh-cn 不一定生效（经常直接返回英文），
    /// 这里按关键词兜一层中文，避免卡片右上角出现 "Smoky haze"。
    /// </summary>
    private static string TranslateCond(string? en)
    {
        if (string.IsNullOrWhiteSpace(en)) return "未知";

        string t = en.Trim().ToLowerInvariant();

        // 先处理组合词，避免被更宽的关键词抢先命中
        if (t.Contains("thunder")) return t.Contains("rain") ? "雷阵雨" : "雷暴";
        if (t.Contains("sleet") || t.Contains("freezing rain")) return "雨夹雪";
        if (t.Contains("blizzard")) return "暴雪";
        if (t.Contains("heavy snow")) return "大雪";
        if (t.Contains("moderate snow")) return "中雪";
        if (t.Contains("snow")) return "小雪";
        if (t.Contains("heavy rain")) return "大雨";
        if (t.Contains("moderate rain")) return "中雨";
        if (t.Contains("torrential")) return "暴雨";
        if (t.Contains("drizzle")) return "毛毛雨";
        if (t.Contains("rain")) return "小雨";
        if (t.Contains("smoky") || t.Contains("smoke")) return "烟霾";
        if (t.Contains("haze")) return "霾";
        if (t.Contains("freezing fog")) return "冻雾";
        if (t.Contains("fog")) return "雾";
        if (t.Contains("mist")) return "薄雾";
        if (t.Contains("overcast")) return "阴";
        if (t.Contains("partly cloudy")) return "晴间多云";
        if (t.Contains("cloudy")) return "多云";
        if (t.Contains("sunny") || t.Contains("clear")) return "晴";
        if (t.Contains("wind")) return "大风";

        // 已经是中文（或无法识别）就原样返回
        foreach (char c in t)
            if (c > 0x4E00) return en.Trim();

        return en.Trim();
    }

    /// <summary>WMO 天气代码 → 中文描述。</summary>
    private static string DescribeWmo(int code) => code switch
    {
        0 => "晴",
        1 => "晴间多云",
        2 => "多云",
        3 => "阴",
        45 or 48 => "雾",
        51 or 53 or 55 => "毛毛雨",
        56 or 57 => "冻毛毛雨",
        61 => "小雨",
        63 => "中雨",
        65 => "大雨",
        66 or 67 => "冻雨",
        71 => "小雪",
        73 => "中雪",
        75 => "大雪",
        77 => "雪粒",
        80 or 81 => "阵雨",
        82 => "强阵雨",
        85 or 86 => "阵雪",
        95 => "雷阵雨",
        96 or 99 => "雷暴冰雹",
        _ => "未知"
    };

    public void Dispose() => Stop();
}
