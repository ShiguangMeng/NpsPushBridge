using System.Diagnostics;
using System.Globalization;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace NpsPushBridge;

/// <summary>更新检查的结果状态。</summary>
internal enum UpdateStatus
{
    /// <summary>尚未检查过（或正在检查）。</summary>
    Unknown = 0,
    /// <summary>本地已是最新。</summary>
    UpToDate = 1,
    /// <summary>市场上有更新的版本。</summary>
    UpdateAvailable = 2,
    /// <summary>检查失败（网络 / 结构变更 / 解析失败）。</summary>
    Failed = 3,
}

/// <summary>一次更新检查的完整结果（不可变，可跨线程传递）。</summary>
/// <param name="UpdateUrl">用户可见的市场页地址（一定有值，最稳的落脚点）。</param>
/// <param name="DownloadUrl">市场记录里的 .zip 下载直链；站点未提供时为空串（界面据此退回市场页）。</param>
internal readonly record struct UpdateCheckResult(
    UpdateStatus Status,
    string LocalVersion,
    string RemoteVersion,
    string UpdateUrl,
    string Message,
    string DownloadUrl = "")
{
    public bool HasUpdate => Status == UpdateStatus.UpdateAvailable;

    /// <summary>给界面用的一行摘要。</summary>
    public string Describe()
    {
        var local = LocalVersion.Length > 0 ? "v" + LocalVersion : "v?";
        return Status switch
        {
            UpdateStatus.UpToDate => $"{local} · 已是最新",
            UpdateStatus.UpdateAvailable => $"{local} → v{RemoteVersion} · 有新版本",
            UpdateStatus.Failed => $"{local} · 检查失败",
            _ => $"{local} · 尚未检查",
        };
    }
}

/// <summary>
/// 插件市场版本检查。
///
/// ── 站点结构（实测 2026-09-19）────────────────────────────────────────────
/// 市场页 https://nps.georgewu.top/market 是一个前端渲染（Vue SPA）页面：
/// 它的 HTML 外壳里**不含**插件列表（grep 命中数为 0），列表数据由页面自己加载的
/// &lt;script src="/admin/api.php?action=market&amp;format=js"&gt; 提供，内容形如
///
///     window.__NPS_PLUGINS__=[{"id":"npspushbridge","name":"局域网推送网桥",…,"version":"1.1.0",…}];
///
/// 渲染完成后，每张卡片的 DOM 结构是（取自页面 bundle 的渲染函数）：
///
///     &lt;header class="pcard__head"&gt;
///       &lt;div class="pcard__title"&gt;&lt;h3&gt;局域网推送网桥&lt;/h3&gt;&lt;span class="badge"&gt;官方&lt;/span&gt;&lt;/div&gt;
///       &lt;span class="mono pcard__ver"&gt;v1.1.0&lt;/span&gt;   ← 与标题并列的下一个元素
///     &lt;/header&gt;
///
/// 注意两点（都会影响解析）：
///   1. 版本号渲染时会**加上前导 v**（`v1.1.0`），比较前必须归一化；
///   2. `official` 插件会在标题与版本之间插入一个「官方」徽章，
///      所以「取紧邻的下一个元素」不能直接取 h3 的 nextSibling，必须跳过非版本形态的内容。
///
/// ── 本模块的取值策略 ──────────────────────────────────────────────────────
/// ① 先访问市场页 HTML：若其中直接含有标记文本（站点将来改为服务端渲染也能用），
///    则按「标记元素 → 向后第一个版本形态的并列元素」取值；
/// ② HTML 里没有（当前 SPA 的实际情形）时，从 HTML 中发现市场数据脚本地址再取一次，
///    得到 JSON 后按 name 定位同一条记录，读它的 version 与 download 字段
///    （JSON 里 name 与 version 是同一对象的并列字段，对应页面上并列的两个元素）。
/// </summary>
internal static class PluginUpdate
{
    /// <summary>市场页地址（用户可见的「插件市场」）。</summary>
    public const string MarketPageUrl = "https://nps.georgewu.top/market";

    /// <summary>市场站点根，用于把相对下载地址补成绝对地址。</summary>
    private const string MarketOrigin = "https://nps.georgewu.top";

    /// <summary>本插件在市场里的名称 —— 定位锚点。</summary>
    public const string PluginNameMarker = "局域网推送网桥";

    /// <summary>
    /// 官网「配置教程」页地址 —— 配置界面的「配置教程」按钮打开它。
    ///
    /// ★ 文档真源在**网站仓库**，不在插件里 ★
    /// 教程与更新日志统一维护在 `ShiguangMeng-NPS-Plugins-Website/docs/{guide,changelog}/pushbridge.md`，
    /// 插件只负责唤起浏览器。因此：
    ///   · dll **不再内嵌**教程与日志图（改站网后内嵌副本只会过期，白占体积）；
    ///   · 站点的地址是**唯一真源**，改域名只需改这两处常量。
    /// 用户侧的收益：文档永远是线上最新的一份，不必等插件升级。
    /// </summary>
    public const string GuidePageUrl = "https://nps-plugins.yzhdemeng.dpdns.org/guide/pushbridge";

    /// <summary>
    /// 官网「更新日志」页地址 —— 配置界面标题行的「更新日志」按钮打开它。
    /// 与 <see cref="GuidePageUrl"/> 同源（同一站点、同一维护方式）。
    /// </summary>
    public const string ChangelogPageUrl = "https://nps-plugins.yzhdemeng.dpdns.org/changelog/pushbridge";

    /// <summary>市场数据脚本的特征片段（用于从 HTML 中发现数据地址）。</summary>
    private const string MarketDataHint = "api.php?action=market";

    /// <summary>版本形态：可选的 v 前缀 + 1~4 段数字 + 可选的预发布后缀。</summary>
    private static readonly Regex VersionShape = new(
        @"^[vV]?\d+(?:\.\d+){0,3}(?:-[0-9A-Za-z][0-9A-Za-z.\-]*)?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        try
        {
            client.DefaultRequestHeaders.UserAgent.ParseAdd("NpsPushBridge-UpdateChecker/1.0");
            client.DefaultRequestHeaders.Accept.ParseAdd("text/html,application/javascript,*/*");
        }
        catch { /* 头部不被接受也不影响请求 */ }
        return client;
    }

    // ====================================================================
    // 本地版本
    // ====================================================================

    /// <summary>
    /// 本地版本号。唯一真源是 csproj 的 &lt;Version&gt;（编译成 AssemblyInformationalVersion），
    /// 因此改版本只需改一处，界面、日志、更新比较都会跟着变。
    /// </summary>
    public static string LocalVersion { get; } = ResolveLocalVersion();

    private static string ResolveLocalVersion()
    {
        try
        {
            var asm = typeof(PluginUpdate).Assembly;

            var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            var normalized = NormalizeVersion(info);
            if (normalized != null) return normalized;

            var v = asm.GetName().Version;
            if (v != null) return $"{v.Major}.{v.Minor}.{Math.Max(v.Build, 0)}";
        }
        catch { /* 取不到就退回保守值 */ }
        return "0.0.0";
    }

    // ====================================================================
    // 版本归一化与对比
    // ====================================================================

    /// <summary>
    /// 归一化版本字符串：去空白、去引号、去前导 v/V、截掉构建元数据（+ 之后）。
    /// 不是版本形态（例如「官方」这类徽章文本）时返回 null —— 调用方据此跳过。
    /// </summary>
    public static string? NormalizeVersion(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        var s = raw.Trim().Trim('"', '\'', '\u201c', '\u201d', '\u2018', '\u2019');
        s = s.Trim();

        int plus = s.IndexOf('+');                       // 1.0.0+build.7 → 1.0.0
        if (plus >= 0) s = s[..plus];

        if (s.Length > 1 && (s[0] == 'v' || s[0] == 'V')) s = s[1..];   // v1.1.0 → 1.1.0
        s = s.Trim();

        // 只取开头连续版本形态的部分：容忍 "1.1.0 更新于 2026-09-19" 这类尾巴
        if (VersionShape.IsMatch(s)) return s;

        int cut = 0;
        while (cut < s.Length && (char.IsDigit(s[cut]) || s[cut] == '.')) cut++;
        if (cut > 0)
        {
            // 允许紧接着的预发布段
            if (cut < s.Length && s[cut] == '-')
            {
                int e = cut + 1;
                while (e < s.Length && (char.IsLetterOrDigit(s[e]) || s[e] == '.' || s[e] == '-')) e++;
                var candidate = s[..e];
                if (VersionShape.IsMatch(candidate)) return candidate;
            }
            var head = s[..cut].TrimEnd('.');
            if (VersionShape.IsMatch(head)) return head;
        }

        return null;
    }

    /// <summary>
    /// 语义化对比：a &lt; b 返回 -1，相等返回 0，a &gt; b 返回 1。
    ///
    /// 规则：
    ///   · 主版本按「段」逐位比较，缺位补 0（1.1 与 1.1.0 视为相等）；
    ///   · 段内非数字时只取前导数字（"1.2a" → 1.2），取不到按 0；
    ///   · 主版本相同时，带预发布后缀的**更小**（1.1.0-beta &lt; 1.1.0），
    ///     两边都带后缀则按不区分大小写的字典序比较。
    /// 任一侧无法归一化时按 0.0.0 处理，绝不抛异常。
    /// </summary>
    public static int CompareVersion(string? a, string? b)
    {
        var (coreA, preA) = SplitVersion(a);
        var (coreB, preB) = SplitVersion(b);

        int n = Math.Max(coreA.Count, coreB.Count);
        for (int i = 0; i < n; i++)
        {
            int x = i < coreA.Count ? coreA[i] : 0;
            int y = i < coreB.Count ? coreB[i] : 0;
            if (x != y) return x < y ? -1 : 1;
        }

        bool hasPreA = preA.Length > 0, hasPreB = preB.Length > 0;
        if (hasPreA && !hasPreB) return -1;
        if (!hasPreA && hasPreB) return 1;
        if (!hasPreA) return 0;
        return string.Compare(preA, preB, StringComparison.OrdinalIgnoreCase);
    }

    private static (List<int> Core, string Pre) SplitVersion(string? raw)
    {
        var v = NormalizeVersion(raw) ?? "0";
        var pre = "";
        int dash = v.IndexOf('-');
        if (dash >= 0) { pre = v[(dash + 1)..]; v = v[..dash]; }

        var core = new List<int>(4);
        foreach (var seg in v.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            int digits = 0;
            while (digits < seg.Length && char.IsDigit(seg[digits])) digits++;
            core.Add(digits > 0 && int.TryParse(seg[..digits], NumberStyles.Integer,
                CultureInfo.InvariantCulture, out int n) ? n : 0);
        }
        if (core.Count == 0) core.Add(0);
        return (core, pre);
    }

    // ====================================================================
    // 取值：① 标记元素 → 并列的下一个元素
    // ====================================================================

    /// <summary>
    /// 在（服务端渲染或静态的）HTML 里定位标记文本，取它**并列的下一个元素**的文本作为版本号。
    ///
    /// 实现细节：从标记之后按文档顺序遍历每一个「&gt;文本&lt;」片段，
    /// 返回第一个**版本形态**的片段 —— 这样能自然跳过 `official` 插件插在中间的「官方」徽章。
    /// </summary>
    public static bool TryParseSiblingVersion(string html, string marker, out string version, out string error)
    {
        version = "";
        error = "";

        if (string.IsNullOrEmpty(html)) { error = "页面内容为空"; return false; }

        int at = html.IndexOf(marker, StringComparison.Ordinal);
        if (at < 0) { error = $"页面里没有找到标记「{marker}」"; return false; }

        int i = at + marker.Length;
        int scanned = 0;
        while (i < html.Length && scanned < 32)
        {
            int gt = html.IndexOf('>', i);          // 走到当前元素的结束标签
            if (gt < 0) break;
            int lt = html.IndexOf('<', gt + 1);     // 下一个标签开始 = 文本节点结束
            if (lt < 0) break;

            var text = html[(gt + 1)..lt].Trim();
            if (text.Length > 0)
            {
                scanned++;
                var normalized = NormalizeVersion(text);
                if (normalized != null)
                {
                    version = normalized;
                    return true;
                }
            }
            i = lt;
        }

        error = "标记之后没有找到版本形态的并列元素";
        return false;
    }

    // ====================================================================
    // 取值：② 从页面 HTML 发现市场数据脚本
    // ====================================================================

    /// <summary>从市场页 HTML 中找出它自己加载的数据脚本地址（相对地址会被补成绝对地址）。</summary>
    public static bool TryFindMarketDataUrl(string html, out string url, out string error)
    {
        url = "";
        error = "";

        if (string.IsNullOrEmpty(html)) { error = "页面内容为空"; return false; }

        int hint = html.IndexOf(MarketDataHint, StringComparison.OrdinalIgnoreCase);
        if (hint < 0) { error = "页面里没有市场数据脚本（站点结构可能已变更）"; return false; }

        // 回溯到该属性值/URL 的起点，再向后取到引号或空白结束
        int start = hint;
        while (start > 0 && !IsUrlBoundary(html[start - 1])) start--;

        int end = hint;
        while (end < html.Length && !IsUrlBoundary(html[end])) end++;

        var candidate = html[start..end].Trim();
        if (candidate.Length == 0) { error = "市场数据脚本地址为空"; return false; }

        if (candidate.StartsWith("//", StringComparison.Ordinal)) candidate = "https:" + candidate;
        else if (candidate.StartsWith('/')) candidate = MarketOrigin + candidate;

        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            error = $"市场数据脚本地址不可用：{candidate}";
            return false;
        }

        url = uri.ToString();
        return true;
    }

    private static bool IsUrlBoundary(char c)
        => c is '"' or '\'' or '<' or '>' or ' ' or '\t' or '\r' or '\n' or '(' or ')' or ',' or ';';

    // ====================================================================
    // 取值：③ 从市场数据载荷解析目标记录
    // ====================================================================

    /// <summary>
    /// 从市场数据载荷里解析目标插件记录：定位 <c>window.__NPS_PLUGINS__=[…]</c> 数组，
    /// 找到 name 命中标记的那条记录，读它的 version（并列字段）与 download。
    /// </summary>
    public static bool TryParseMarketEntry(string payload, string marker, out MarketEntry entry, out string error)
    {
        entry = default;
        error = "";

        if (string.IsNullOrWhiteSpace(payload)) { error = "市场数据为空"; return false; }

        // 尽量锚定到 window.__NPS_PLUGINS__ 之后的那个 '['，避免载荷里更靠前的方括号干扰
        int bracket = -1;
        int anchor = payload.IndexOf("__NPS_PLUGINS__", StringComparison.Ordinal);
        if (anchor >= 0) bracket = payload.IndexOf('[', anchor);
        if (bracket < 0) bracket = payload.IndexOf('[');
        if (bracket < 0) { error = "市场数据里没有数组（结构可能已变更）"; return false; }

        int close = FindMatching(payload, bracket, '[', ']');
        if (close < 0) { error = "市场数据数组不完整"; return false; }

        JsonDocument doc;
        try { doc = JsonDocument.Parse(payload[bracket..(close + 1)]); }
        catch (JsonException ex) { error = $"市场数据不是合法 JSON：{ex.Message}"; return false; }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Array) { error = "市场数据不是数组"; return false; }

            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;

                if (!item.TryGetProperty("name", out var nameEl) || nameEl.ValueKind != JsonValueKind.String)
                    continue;

                var name = nameEl.GetString() ?? "";
                if (!name.Contains(marker, StringComparison.Ordinal)) continue;

                var version = NormalizeVersion(ReadString(item, "version"));
                if (version == null) { error = $"记录「{name}」没有可用的 version 字段"; return false; }

                var download = ReadString(item, "download");
                entry = new MarketEntry(name, version, ResolveUrl(download), ReadString(item, "updated"));
                return true;            }
        }

        error = $"市场数据里没有找到名称含「{marker}」的插件";
        return false;
    }

    /// <summary>把市场记录里的相对地址补成绝对地址；为空时回落到市场页。</summary>
    private static string ResolveUrl(string raw)
    {
        var s = (raw ?? "").Trim();
        if (s.Length == 0) return MarketPageUrl;
        if (s.StartsWith("//", StringComparison.Ordinal)) return "https:" + s;
        if (s.StartsWith('/')) return MarketOrigin + s;
        return Uri.TryCreate(s, UriKind.Absolute, out _) ? s : MarketPageUrl;
    }

    private static string ReadString(JsonElement obj, string property)
        => obj.TryGetProperty(property, out var el) && el.ValueKind == JsonValueKind.String
            ? el.GetString() ?? ""
            : "";

    /// <summary>括号配平查找（跳过字符串字面量与转义），用于切出 JSON 数组范围。</summary>
    private static int FindMatching(string s, int openIndex, char open, char close)
    {
        int depth = 0;
        bool inString = false;
        for (int i = openIndex; i < s.Length; i++)
        {
            char c = s[i];
            if (inString)
            {
                if (c == '\\') { i++; continue; }
                if (c == '"') inString = false;
                continue;
            }
            if (c == '"') { inString = true; continue; }
            if (c == open) depth++;
            else if (c == close)
            {
                depth--;
                if (depth == 0) return i;
            }
        }
        return -1;
    }

    // ====================================================================
    // 编排：访问市场 → 取值 → 对比
    // ====================================================================

    /// <summary>
    /// 执行一次完整检查。**任何异常都会被收敛成 <see cref="UpdateStatus.Failed"/> 结果**，
    /// 不向调用方抛出，也不会影响推送服务本身。
    /// </summary>
    public static Task<UpdateCheckResult> CheckAsync(CancellationToken token)
        => CheckAsync(MarketPageUrl, PluginNameMarker, token);

    /// <summary>
    /// 同上，但可指定市场页地址与定位标记 —— 供自动化测试注入「不可达地址 / 自定义标记」，
    /// 从而在不起真实网络请求的前提下验证异常收敛与判定逻辑。
    /// </summary>
    public static async Task<UpdateCheckResult> CheckAsync(string pageUrl, string marker, CancellationToken token)
    {
        var local = LocalVersion;
        try
        {
            // ① 访问市场页
            string html = await Http.GetStringAsync(pageUrl, token).ConfigureAwait(false);

            // ② 页面里直接有版本（服务端渲染 / 静态列表）→ 走「标记 → 并列的下一个元素」
            //    这条路径拿不到 download 字段（HTML 里只有版本号），因此下载直链留空 ——
            //    调用方会退回市场页，行为上仍然可用。
            if (TryParseSiblingVersion(html, marker, out var siblingVersion, out _))
                return Evaluate(local, siblingVersion, pageUrl, "页面标记元素");

            // ③ 当前 SPA 的实际情形：从 HTML 发现数据脚本，再取一次
            if (!TryFindMarketDataUrl(html, out var dataUrl, out var urlError))
                return Failed(local, urlError);

            string payload = await Http.GetStringAsync(dataUrl, token).ConfigureAwait(false);

            if (!TryParseMarketEntry(payload, marker, out var entry, out var parseError))
                return Failed(local, parseError);

            return Evaluate(local, entry.Version, entry.Url, $"市场记录（更新于 {entry.Updated}）",
                ResolveDownloadUrl(entry.Url));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return Failed(local, "检查已取消");
        }
        catch (TaskCanceledException)
        {
            return Failed(local, "请求超时（10 秒）：网络不通、被拦截或站点无响应");
        }
        catch (HttpRequestException ex)
        {
            return Failed(local, $"网络错误：{ex.Message}");
        }
        catch (Exception ex)
        {
            return Failed(local, $"检查失败：{ex.GetType().Name} {ex.Message}");
        }
    }

    private static UpdateCheckResult Evaluate(string local, string remote, string url, string source,
        string downloadUrl = "")
    {
        int cmp = CompareVersion(local, remote);
        return cmp < 0
            ? new UpdateCheckResult(UpdateStatus.UpdateAvailable, local, remote, url,
                $"发现新版本：本地 v{local} → 市场 v{remote}（{source}）", downloadUrl)
            : new UpdateCheckResult(UpdateStatus.UpToDate, local, remote, url,
                $"已是最新：本地 v{local}，市场 v{remote}（{source}）", downloadUrl);
    }

    private static UpdateCheckResult Failed(string local, string message)
        => new(UpdateStatus.Failed, local, "", MarketPageUrl, message);

    /// <summary>
    /// 把市场记录里的 download 字段整理成「可下载直链」。
    ///
    /// 为什么不能直接用 <see cref="MarketEntry.Url"/>：那个字段在 download 缺失时会被
    /// <see cref="ResolveUrl"/> 兜底成市场页，而市场页是 HTML —— 用它当「下载直链」会让
    /// 「下载更新包」按钮实际只是打开网页。所以这里只在**看起来真是安装包**时返回，
    /// 否则留空，由调用方退回市场页（用户仍能找到下载入口）。
    ///
    /// 判定同时接受 .zip / .npsx 这类压缩包与 GitHub release 的 download 路径 ——
    /// 站点将来换托管方式时不必改代码。
    /// </summary>
    public static string ResolveDownloadUrl(string? raw)
    {
        var s = (raw ?? "").Trim();
        if (s.Length == 0) return "";

        if (s.StartsWith("//", StringComparison.Ordinal)) s = "https:" + s;
        else if (s.StartsWith('/')) s = MarketOrigin + s;

        if (!Uri.TryCreate(s, UriKind.Absolute, out var uri)) return "";
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return "";

        // 看起来是安装包 / 下载端点才算直链；指向市场页自身的一律判为非直链
        var path = uri.AbsolutePath;
        bool looksLikePackage =
            path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".npsx", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
            || path.Contains("/download", StringComparison.OrdinalIgnoreCase)
            || uri.Query.Contains("download", StringComparison.OrdinalIgnoreCase);

        return looksLikePackage ? uri.ToString() : "";
    }

    // ====================================================================
    // 后续动作
    // ====================================================================

    /// <summary>用默认浏览器打开网址。失败时返回错误说明，成功返回空串。</summary>
    public static string OpenInBrowser(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return "更新链接为空";
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            return $"链接不是有效的 http/https 地址：{url}";

        try
        {
            Process.Start(new ProcessStartInfo { FileName = uri.ToString(), UseShellExecute = true });
            return "";
        }
        catch (Exception ex)
        {
            // 无默认浏览器 / 被组策略拦截等：收敛成可读文案，不向调用方抛异常。
            // 这里只「启动」不等待进程退出，否则会拖住调用线程。
            return $"打开浏览器失败：{ex.Message}";
        }
    }
}

/// <summary>市场里查到的一条插件记录。</summary>
internal readonly record struct MarketEntry(string Name, string Version, string Url, string Updated);
