using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace PerfDeck.Core;

/// <summary>一条热搜。</summary>
public sealed record HotItem(string Title, string Url, string Source, long Heat, int Rank, bool IsAi);

/// <summary>
/// 多平台热搜聚合：微博 / 百度 / B站 / 抖音 / 头条，另加一路 AI 领域专项。
/// 抓取在后台线程，界面只读缓存；失败平台保留上一轮结果，不影响其它平台。
/// </summary>
public sealed class HotSearchService : IDisposable
{
    public const string All = "all";
    public const string Ai = "ai";
    public const string Weibo = "weibo";
    public const string Baidu = "baidu";
    public const string Bili = "bili";
    public const string Toutiao = "toutiao";
    public const string Douyin = "douyin";

    public static readonly (string Key, string Name)[] Platforms =
    {
        (All, "综合"),
        (Ai, "AI 进展"),
        (Weibo, "微博"),
        (Baidu, "百度"),
        (Bili, "B站"),
        (Douyin, "抖音"),
        (Toutiao, "头条")
    };

    /// <summary>判定“AI 领域”的关键词，中英文都覆盖，命中即置顶并打标。</summary>
    private static readonly string[] AiKeys =
    {
        "ai", "人工智能", "大模型", "aigc", "gpt", "chatgpt", "openai", "claude", "gemini", "llm",
        "深度学习", "机器学习", "神经网络", "算法模型", "英伟达", "nvidia", "算力", "智能体", "agent",
        "sora", "文心", "通义", "豆包", "kimi", "deepseek", "midjourney", "stable diffusion", "多模态",
        "机器人", "自动驾驶", "智驾", "芯片", "transformer", "推理模型"
    };

    private static readonly HttpClient Http = CreateClient();
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(15);

    private readonly AppConfig _cfg;
    private readonly Dictionary<string, List<HotItem>> _boards = new();
    private readonly object _gate = new();
    private CancellationTokenSource? _cts;
    private DateTime _updated = DateTime.MinValue;

    public string Status { get; private set; } = "尚未加载";
    public DateTime Updated { get { lock (_gate) return _updated; } }
    public bool Ready { get; private set; }

    public HotSearchService(AppConfig cfg)
    {
        _cfg = cfg;
    }

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0 Safari/537.36");
        c.DefaultRequestHeaders.Accept.ParseAdd("application/json, text/plain, */*");
        return c;
    }

    public void Start()
    {
        Stop();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        _ = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try { await FetchAllAsync(token).ConfigureAwait(false); }
                catch (Exception ex) { Log.Warn("热搜抓取异常：" + ex.Message); }

                try { await Task.Delay(RefreshInterval, token).ConfigureAwait(false); }
                catch { break; }
            }
        }, token);
    }

    /// <summary>立即刷新一轮（右键“刷新热搜”用）。</summary>
    public void RefreshNow() => _ = Task.Run(async () =>
    {
        try { await FetchAllAsync(CancellationToken.None).ConfigureAwait(false); }
        catch (Exception ex) { Log.Warn("热搜刷新失败：" + ex.Message); }
    });

    public void Stop()
    {
        try { _cts?.Cancel(); } catch { }
        _cts?.Dispose();
        _cts = null;
    }

    public List<HotItem> Get(string? platform)
    {
        lock (_gate)
        {
            string key = string.IsNullOrWhiteSpace(platform) ? All : platform!;
            return _boards.TryGetValue(key, out var list) ? list : new List<HotItem>();
        }
    }

    /// <summary>
    /// 命令行自检：立刻抓一轮，返回各平台条数摘要（写日志用）。
    /// 不弹窗、不依赖 UI，方便在没有界面时确认数据源是否可用。
    /// </summary>
    public async Task<string> ProbeAsync()
    {
        await FetchAllAsync(CancellationToken.None).ConfigureAwait(false);

        var sb = new System.Text.StringBuilder();
        foreach (var (key, name) in Platforms)
        {
            var list = Get(key);
            string first = list.Count > 0 ? " | " + list[0].Title : "";
            sb.Append($"{name}={list.Count}{first}; ");
        }
        return sb.ToString();
    }

    // ================= 抓取 =================

    private async Task FetchAllAsync(CancellationToken token)
    {
        var results = new Dictionary<string, List<HotItem>>();
        var errors = new List<string>();

        async Task<(string key, List<HotItem>? list, string? err)> Try(string key,
            Func<CancellationToken, Task<List<HotItem>>> fn)
        {
            try { return (key, await fn(token).ConfigureAwait(false), null); }
            catch (Exception ex) { return (key, null, key + "：" + ex.Message); }
        }

        var tasks = new[]
        {
            Try(Weibo, FetchWeiboAsync),
            Try(Baidu, FetchBaiduAsync),
            Try(Bili, FetchBiliAsync),
            Try(Toutiao, FetchToutiaoAsync),
            Try(Douyin, FetchDouyinAsync),
            Try(Ai, FetchAiAsync)
        };

        foreach (var t in await Task.WhenAll(tasks).ConfigureAwait(false))
        {
            if (t.list is { Count: > 0 }) results[t.key] = t.list;
            else if (t.err != null) errors.Add(t.err);
        }

        if (results.Count == 0)
        {
            Status = errors.Count > 0 ? "抓取失败：" + errors[0] : "暂无数据";
            Log.Warn("热搜全部平台失败：" + string.Join(" / ", errors));
            return;
        }

        // 综合榜：各平台取前 8 条，AI 置顶，去掉同名
        var merged = new List<HotItem>();
        if (results.TryGetValue(Ai, out var aiList)) merged.AddRange(aiList.Take(3));
        foreach (var key in new[] { Weibo, Baidu, Bili, Douyin, Toutiao })
            if (results.TryGetValue(key, out var l))
                merged.AddRange(l.Where(i => i.Rank <= 6));
        results[All] = Dedup(merged, 20);

        lock (_gate)
        {
            // 抓取失败的平台保留上一轮，避免卡片突然空掉
            foreach (var kv in results) _boards[kv.Key] = kv.Value;
            _updated = DateTime.Now;
        }

        Ready = true;
        Status = $"{results.Count - 1} 个平台 · {_updated:HH:mm}";
        if (errors.Count > 0) Log.Warn("部分热搜平台失败：" + string.Join(" / ", errors));
    }

    private static List<HotItem> Dedup(IEnumerable<HotItem> items, int take)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<HotItem>();
        foreach (var i in items.OrderByDescending(x => x.IsAi).ThenBy(x => x.Rank))
        {
            if (string.IsNullOrWhiteSpace(i.Title)) continue;
            if (!seen.Add(i.Title.Trim())) continue;
            list.Add(i with { Rank = list.Count + 1 });
            if (list.Count >= take) break;
        }
        return list;
    }

    private async Task<string> GetJsonAsync(string url, string? referer = null, CancellationToken token = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        if (referer != null) req.Headers.Referrer = new Uri(referer);
        using var resp = await Http.SendAsync(req, token).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsStringAsync(token).ConfigureAwait(false);
    }

    // ---------- 微博 ----------

    private async Task<List<HotItem>> FetchWeiboAsync(CancellationToken token)
    {
        string json = await GetJsonAsync("https://weibo.com/ajax/statuses/hot_band", "https://weibo.com/", token)
            .ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("data", out var data)) return new();

        var list = new List<HotItem>();
        if (!data.TryGetProperty("band_list", out var arr)) return list;

        int rank = 0;
        foreach (var it in arr.EnumerateArray())
        {
            string title = Str(it, "note");
            if (string.IsNullOrWhiteSpace(title)) title = Str(it, "word");
            if (string.IsNullOrWhiteSpace(title)) continue;

            // 广告位（is_ad）跳过
            if (it.TryGetProperty("is_ad", out var ad) && ad.ValueKind == JsonValueKind.True) continue;

            long heat = Num(it, "num");
            string url = "https://s.weibo.com/weibo?q=" + Uri.EscapeDataString("#" + title + "#");
            list.Add(new HotItem(title, url, "微博", heat, ++rank, IsAiTitle(title)));
        }
        return list;
    }

    // ---------- 百度 ----------

    private async Task<List<HotItem>> FetchBaiduAsync(CancellationToken token)
    {
        string json = await GetJsonAsync("https://top.baidu.com/api/board?platform=wise&tab=realtime",
            "https://top.baidu.com/", token).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("data", out var data)) return new();
        if (!data.TryGetProperty("cards", out var cards)) return new();

        var items = new List<JsonElement>();
        foreach (var card in cards.EnumerateArray())
        {
            if (!card.TryGetProperty("content", out var content)) continue;
            foreach (var node in content.EnumerateArray())
            {
                // 内容可能再嵌一层 { content: [...] }
                if (node.TryGetProperty("content", out var inner) && inner.ValueKind == JsonValueKind.Array)
                {
                    foreach (var it in inner.EnumerateArray()) items.Add(it);
                }
                else if (node.ValueKind == JsonValueKind.Object && node.TryGetProperty("word", out _))
                {
                    items.Add(node);
                }
            }
        }

        var list = new List<HotItem>();
        int rank = 0;
        foreach (var it in items)
        {
            string title = Str(it, "word");
            if (string.IsNullOrWhiteSpace(title)) continue;

            long heat = Num(it, "hotScore");
            string url = Str(it, "url");
            if (string.IsNullOrWhiteSpace(url))
                url = "https://www.baidu.com/s?wd=" + Uri.EscapeDataString(title);

            list.Add(new HotItem(title, url, "百度", heat, ++rank, IsAiTitle(title)));
            if (list.Count >= 30) break;
        }
        return list;
    }

    // ---------- B站 ----------

    private async Task<List<HotItem>> FetchBiliAsync(CancellationToken token)
    {
        string json = await GetJsonAsync("https://s.search.bilibili.com/main/hotword",
            "https://www.bilibili.com/", token).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("list", out var arr)) return new();

        var list = new List<HotItem>();
        int rank = 0;
        foreach (var it in arr.EnumerateArray())
        {
            string title = Str(it, "show_name");
            if (string.IsNullOrWhiteSpace(title)) title = Str(it, "keyword");
            if (string.IsNullOrWhiteSpace(title)) continue;

            string url = "https://search.bilibili.com/all?keyword=" + Uri.EscapeDataString(title);
            list.Add(new HotItem(title, url, "B站", 0, ++rank, IsAiTitle(title)));
        }
        return list;
    }

    // ---------- 头条 ----------

    private async Task<List<HotItem>> FetchToutiaoAsync(CancellationToken token)
    {
        string json = await GetJsonAsync("https://www.toutiao.com/hot-event/hot-board/?origin=toutiao_pc",
            "https://www.toutiao.com/", token).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("data", out var arr)) return new();

        var list = new List<HotItem>();
        int rank = 0;
        foreach (var it in arr.EnumerateArray())
        {
            string title = Str(it, "Title");
            if (string.IsNullOrWhiteSpace(title)) continue;

            long heat = Num(it, "HotValue");
            string url = Str(it, "Url");
            if (string.IsNullOrWhiteSpace(url))
                url = "https://so.toutiao.com/search?keyword=" + Uri.EscapeDataString(title);

            list.Add(new HotItem(title, url, "头条", heat, ++rank, IsAiTitle(title)));
            if (list.Count >= 30) break;
        }
        return list;
    }

    // ---------- 抖音 ----------

    /// <summary>
    /// 抖音热搜：用的是网页版榜单接口（无需签名），返回 word_list。
    /// label 只是“热/沸/新”之类的标记，没有广告位字段，按顺序取即可。
    /// </summary>
    private async Task<List<HotItem>> FetchDouyinAsync(CancellationToken token)
    {
        string json = await GetJsonAsync("https://www.iesdouyin.com/web/api/v2/hotsearch/billboard/word/",
            "https://www.douyin.com/", token).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("word_list", out var arr)) return new();

        var list = new List<HotItem>();
        int rank = 0;
        foreach (var it in arr.EnumerateArray())
        {
            string title = Str(it, "word");
            if (string.IsNullOrWhiteSpace(title)) continue;

            long heat = Num(it, "hot_value");
            string url = "https://www.douyin.com/search/" + Uri.EscapeDataString(title);

            list.Add(new HotItem(title, url, "抖音", heat, ++rank, IsAiTitle(title)));
            if (list.Count >= 30) break;
        }
        return list;
    }

    // ---------- AI 专项 ----------

    private async Task<List<HotItem>> FetchAiAsync(CancellationToken token)
    {
        string json = await GetJsonAsync(
            "https://hn.algolia.com/api/v1/search?query=AI%20OR%20LLM%20OR%20OpenAI&tags=story&hitsPerPage=25",
            null, token).ConfigureAwait(false);

        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("hits", out var hits)) return new();

        var list = new List<HotItem>();
        int rank = 0;
        foreach (var it in hits.EnumerateArray())
        {
            string title = Str(it, "title");
            if (string.IsNullOrWhiteSpace(title)) continue;

            string url = Str(it, "url");
            if (string.IsNullOrWhiteSpace(url))
            {
                string oid = Str(it, "objectID");
                url = string.IsNullOrWhiteSpace(oid) ? "https://news.ycombinator.com/" : "https://news.ycombinator.com/item?id=" + oid;
            }

            long heat = Num(it, "points");
            list.Add(new HotItem(title, url, "AI", heat, ++rank, true));
        }
        return list;
    }

    // ================= 工具 =================

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static long Num(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v)) return 0;
        return v.ValueKind switch
        {
            JsonValueKind.Number => v.TryGetInt64(out long n) ? n : (long)v.GetDouble(),
            JsonValueKind.String => long.TryParse(v.GetString(), out long s) ? s : 0,
            _ => 0
        };
    }

    /// <summary>标题是否属于 AI 领域。</summary>
    public static bool IsAiTitle(string title)
    {
        if (string.IsNullOrWhiteSpace(title)) return false;
        string t = title.ToLowerInvariant();
        foreach (var k in AiKeys)
            if (t.Contains(k, StringComparison.Ordinal)) return true;
        return false;
    }

    public void Dispose()
    {
        Stop();
    }
}
