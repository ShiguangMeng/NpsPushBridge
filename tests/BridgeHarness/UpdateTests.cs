using System.Reflection;
using System.Text.RegularExpressions;
using NpsPushBridge;

namespace BridgeHarness;

/// <summary>
/// 自动更新逻辑的验证。分层覆盖，全部为**可离线重复运行**的纯函数断言，
/// 另加一个联网用例（访问真实市场）作为端到端确认。
///
///   ① 版本归一化：v 前缀 / 空白 / 构建元数据 / 非版本文本
///   ② 对比规则：逐段比较、缺位补 0、预发布更小、数值而非字典序
///   ③ 取值①「标记元素 → 并列的下一个元素」：真实卡片结构（含 official 徽章干扰）
///   ④ 取值②「从页面 HTML 发现市场数据脚本」：相对地址绝对化
///   ⑤ 取值③「市场数据 → 目标记录」：真实载荷样本 + 各类结构异常
///   ⑥ 异常收敛：不可达地址不得抛异常，必须收敛成 Failed 结果
///   ⑦ 联网端到端：真实访问市场页并解析
/// </summary>
internal static class UpdateTests
{
    private static int _passed, _failed, _skipped;

    /// <summary>已通过的用例数（供外层汇总）。</summary>
    public static int Passed => _passed;
    public static int Failed => _failed;
    public static int Skipped => _skipped;

    /// <summary>真实市场数据载荷（2026-09-19 实测，保留嵌套 files 数组以检验括号配平）。</summary>
    private const string RealPayload =
        "window.__NPS_PLUGINS__=[" +
        "{\"id\":\"nps-test-plugin\",\"name\":\"默认测试插件\",\"author\":\"Ryenryen\",\"version\":\"1.0.0\"," +
        "\"official\":true,\"files\":[{\"name\":\"TestPlugin.dll\",\"size\":13824}],\"download\":\"/download.php?id=nps-test-plugin\"}," +
        "{\"id\":\"hello-plugin\",\"name\":\"HelloPlugin\",\"author\":\"HXH\",\"version\":\"1.0.0\"," +
        "\"files\":[{\"name\":\"HelloPlugin.dll\",\"size\":10240}],\"download\":\"/download.php?id=hello-plugin\"}," +
        "{\"id\":\"npspushbridge\",\"name\":\"局域网推送网桥\",\"author\":\"拾光梦\",\"category\":\"utility\"," +
        "\"version\":\"1.1.0\",\"downloads\":4,\"tags\":[\"HTTP\"],\"updated\":\"2026-09-19\",\"official\":false," +
        "\"files\":[{\"name\":\"NpsPushBridge.zip\",\"size\":36953}],\"download\":\"/download.php?id=npspushbridge\"}," +
        "{\"id\":\"onesaying\",\"name\":\"一言\",\"author\":\"ZZJ-JACK\",\"version\":\"1.0.0\"," +
        "\"files\":[{\"name\":\"OneSaying.dll\",\"size\":37376}],\"download\":\"/download.php?id=onesaying\"}" +
        "];window.__NPS_ADMIN__=null;";

    /// <summary>市场页 HTML 里加载数据的脚本标签（实测形态）。</summary>
    private const string RealScriptTag = "<script src=\"/admin/api.php?action=market&format=js\"></script>";

    /// <summary>渲染后的卡片结构（取自页面 bundle 的渲染函数：版本元素是「标题 div 的并列下一个元素」）。</summary>
    private const string RealCardHtml =
        "<article class=\"pcard glass-card\"><header class=\"pcard__head\">" +
        "<div class=\"pcard__title\"><h3>局域网推送网桥</h3><span class=\"badge badge--acc\">官方</span></div>" +
        "<span class=\"mono pcard__ver\">v1.1.0</span></header>" +
        "<p class=\"pcard__desc\">通过主程序的HTTP本地监听建立局域网转发渠道</p></article>";

    /// <summary>同步用例（纯函数，离线可重复运行）。</summary>
    public static void Run()
    {
        Console.WriteLine("【自动更新：版本解析与对比】");
        TestNormalize();
        TestCompare();

        Console.WriteLine();
        Console.WriteLine("【自动更新：市场取值规则】");
        TestSiblingRule();
        TestDataScriptDiscovery();
        TestMarketEntryParsing();
        TestLocalVersion();

        Console.WriteLine();
        Console.WriteLine("【自动更新：异常收敛与联网端到端】");
    }

    public static async Task RunNetworkAsync()
    {
        await TestFailureConvergenceAsync();
        await TestLiveMarketAsync();
    }

    // ====================================================================
    // ① 版本归一化
    // ====================================================================

    private static void TestNormalize()
    {
        Check("普通版本原样保留", PluginUpdate.NormalizeVersion("1.1.0") == "1.1.0");
        Check("市场渲染带前导 v → 去掉", PluginUpdate.NormalizeVersion("v1.1.0") == "1.1.0",
            PluginUpdate.NormalizeVersion("v1.1.0") ?? "null");
        Check("大写 V 前缀也去掉", PluginUpdate.NormalizeVersion("V2.0") == "2.0",
            PluginUpdate.NormalizeVersion("V2.0") ?? "null");
        Check("前后空白与引号被清掉", PluginUpdate.NormalizeVersion("  \"v1.2.3\"  ") == "1.2.3",
            PluginUpdate.NormalizeVersion("  \"v1.2.3\"  ") ?? "null");
        Check("构建元数据（+ 之后）被截掉", PluginUpdate.NormalizeVersion("1.0.0+build.7") == "1.0.0",
            PluginUpdate.NormalizeVersion("1.0.0+build.7") ?? "null");
        Check("带尾巴时只取开头版本段", PluginUpdate.NormalizeVersion("1.1.0 更新于 2026-09-19") == "1.1.0",
            PluginUpdate.NormalizeVersion("1.1.0 更新于 2026-09-19") ?? "null");
        Check("预发布后缀保留", PluginUpdate.NormalizeVersion("v1.2.0-beta.1") == "1.2.0-beta.1",
            PluginUpdate.NormalizeVersion("v1.2.0-beta.1") ?? "null");
        Check("徽章文本「官方」不是版本 → null", PluginUpdate.NormalizeVersion("官方") == null,
            PluginUpdate.NormalizeVersion("官方") ?? "null");
        Check("空串 → null", PluginUpdate.NormalizeVersion("") == null);
        Check("null → null", PluginUpdate.NormalizeVersion(null) == null);
        Check("纯字母 → null", PluginUpdate.NormalizeVersion("abc") == null,
            PluginUpdate.NormalizeVersion("abc") ?? "null");
    }

    // ====================================================================
    // ② 对比规则
    // ====================================================================

    private static void TestCompare()
    {
        Check("1.0.0 < 1.1.0（有新版本）", PluginUpdate.CompareVersion("1.0.0", "1.1.0") < 0);
        Check("1.1.0 > 1.0.0（本地更新）", PluginUpdate.CompareVersion("1.1.0", "1.0.0") > 0);
        Check("1.1.0 == 1.1.0", PluginUpdate.CompareVersion("1.1.0", "1.1.0") == 0);
        Check("1.1 == 1.1.0（缺位补 0）", PluginUpdate.CompareVersion("1.1", "1.1.0") == 0);
        Check("1.0 == 1.0.0.0（缺位补 0）", PluginUpdate.CompareVersion("1.0", "1.0.0.0") == 0);

        Check("1.10.0 > 1.9.0（按数字比，不是字典序）", PluginUpdate.CompareVersion("1.10.0", "1.9.0") > 0,
            $"{PluginUpdate.CompareVersion("1.10.0", "1.9.0")}");
        Check("1.2.0 > 1.1.9", PluginUpdate.CompareVersion("1.2.0", "1.1.9") > 0);
        Check("2.0.0 > 1.99.99", PluginUpdate.CompareVersion("2.0.0", "1.99.99") > 0);

        Check("预发布更小：1.1.0-beta < 1.1.0", PluginUpdate.CompareVersion("1.1.0-beta", "1.1.0") < 0,
            $"{PluginUpdate.CompareVersion("1.1.0-beta", "1.1.0")}");
        Check("正式版大于预发布", PluginUpdate.CompareVersion("1.1.0", "1.1.0-rc.1") > 0);
        Check("两个预发布按字典序", PluginUpdate.CompareVersion("1.1.0-alpha", "1.1.0-beta") < 0);

        Check("v 前缀参与比较也正确", PluginUpdate.CompareVersion("v1.0.0", "v1.1.0") < 0);
        Check("非法输入视为 0.0.0，不抛异常", PluginUpdate.CompareVersion("abc", "1.0.0") < 0);
        Check("两侧都非法 → 相等", PluginUpdate.CompareVersion("abc", "xyz") == 0);
        Check("null 不抛异常", PluginUpdate.CompareVersion(null, "1.0.0") < 0);
    }

    // ====================================================================
    // ③ 标记元素 → 并列的下一个元素
    // ====================================================================

    private static void TestSiblingRule()
    {
        bool ok = PluginUpdate.TryParseSiblingVersion(RealCardHtml, PluginUpdate.PluginNameMarker,
            out var version, out var error);
        Check("真实卡片：取到并列的下一个元素作为版本（跳过「官方」徽章）",
            ok && version == "1.1.0", ok ? version : error);

        // 没有徽章时同样成立
        const string noBadge =
            "<div class=\"pcard__title\"><h3>局域网推送网桥</h3></div><span class=\"mono pcard__ver\">v1.0.9</span>";
        Check("无徽章时同样取下一个元素",
            PluginUpdate.TryParseSiblingVersion(noBadge, PluginUpdate.PluginNameMarker, out var v2, out _) && v2 == "1.0.9");

        // 标记之后只有非版本内容 → 必须失败，不能瞎取
        const string noVersion =
            "<div class=\"pcard__title\"><h3>局域网推送网桥</h3></div><span class=\"badge\">官方</span><p>暂无版本信息</p>";
        Check("标记之后没有版本形态 → 判定失败",
            !PluginUpdate.TryParseSiblingVersion(noVersion, PluginUpdate.PluginNameMarker, out _, out _));

        Check("页面里没有标记 → 判定失败并给出原因",
            !PluginUpdate.TryParseSiblingVersion("<html><body>其它内容</body></html>",
                PluginUpdate.PluginNameMarker, out _, out var e3) && e3.Contains("没有找到标记"));
        Check("空页面 → 判定失败", !PluginUpdate.TryParseSiblingVersion("", PluginUpdate.PluginNameMarker, out _, out _));

        // 真实 SPA 外壳里没有插件数据 —— 这正是必须走「发现数据脚本」那条路的原因
        const string spaShell = "<!doctype html><html><head><title>NotchPeninsula</title></head><body><div id=\"app\"></div></body></html>";
        Check("SPA 外壳里没有标记（故需回退到数据脚本）",
            !PluginUpdate.TryParseSiblingVersion(spaShell, PluginUpdate.PluginNameMarker, out _, out _));
    }

    // ====================================================================
    // ④ 从 HTML 发现市场数据脚本
    // ====================================================================

    private static void TestDataScriptDiscovery()
    {
        bool ok = PluginUpdate.TryFindMarketDataUrl(RealScriptTag, out var url, out var error);
        Check("从脚本标签发现数据地址并绝对化",
            ok && url == "https://nps.georgewu.top/admin/api.php?action=market&format=js", ok ? url : error);

        const string amp = "<script src=\"/admin/api.php?action=market&amp;format=js\"></script>";
        Check("HTML 实体转义形式也能发现",
            PluginUpdate.TryFindMarketDataUrl(amp, out var u2, out _) && u2.Contains("action=market"), u2);

        Check("没有该脚本 → 失败并提示结构可能变更",
            !PluginUpdate.TryFindMarketDataUrl("<html></html>", out _, out var e2) && e2.Contains("结构"));

        Check("空 HTML → 失败", !PluginUpdate.TryFindMarketDataUrl("", out _, out _));
    }

    // ====================================================================
    // ⑤ 市场数据 → 目标记录
    // ====================================================================

    private static void TestMarketEntryParsing()
    {
        bool ok = PluginUpdate.TryParseMarketEntry(RealPayload, PluginUpdate.PluginNameMarker, out var entry, out var error);
        Check("真实载荷：定位到本插件记录", ok, ok ? "" : error);
        if (ok)
        {
            Check("版本解析为 1.1.0", entry.Version == "1.1.0", entry.Version);
            Check("名称就是标记", entry.Name == "局域网推送网桥", entry.Name);
            Check("相对下载地址被绝对化",
                entry.Url == "https://nps.georgewu.top/download.php?id=npspushbridge", entry.Url);
            Check("updated 字段被读出", entry.Updated == "2026-09-19", entry.Updated);
            Check("多条记录中只取标记那一条（不是第一条 test-plugin）", entry.Version != "1.0.0");
        }

        Check("找不到标记 → 失败",
            !PluginUpdate.TryParseMarketEntry(RealPayload, "不存在的插件名", out _, out var e2) && e2.Contains("没有找到"));
        Check("载荷没有数组 → 失败并提示结构变更",
            !PluginUpdate.TryParseMarketEntry("window.__NPS_PLUGINS__=null;", PluginUpdate.PluginNameMarker, out _, out var e3)
            && e3.Contains("没有数组"));
        Check("非 JSON 载荷 → 失败", !PluginUpdate.TryParseMarketEntry("window.X=[{oops", PluginUpdate.PluginNameMarker, out _, out _));
        Check("空载荷 → 失败", !PluginUpdate.TryParseMarketEntry("", PluginUpdate.PluginNameMarker, out _, out _));

        // 记录存在但缺少 version：必须明确失败，而不是拿 0.0.0 去比
        const string noVersion = "window.__NPS_PLUGINS__=[{\"id\":\"x\",\"name\":\"局域网推送网桥\",\"download\":\"/d\"}];";
        Check("记录缺 version → 失败并指出原因",
            !PluginUpdate.TryParseMarketEntry(noVersion, PluginUpdate.PluginNameMarker, out _, out var e4)
            && e4.Contains("version"));

        // download 缺失时回落到市场页，保证「复制/打开」始终有可用链接
        const string noDownload = "window.__NPS_PLUGINS__=[{\"name\":\"局域网推送网桥\",\"version\":\"2.0.0\"}];";
        Check("download 缺失 → 回落到市场页",
            PluginUpdate.TryParseMarketEntry(noDownload, PluginUpdate.PluginNameMarker, out var e5, out _)
            && e5.Version == "2.0.0" && e5.Url == PluginUpdate.MarketPageUrl, e5.Url);

        // 嵌套数组（files）不应破坏数组边界识别
        Check("嵌套 files 数组不影响数组边界识别", ok && entry.Updated.Length > 0);
    }

    // ====================================================================
    // ⑥ 本地版本
    // ====================================================================

    private static void TestLocalVersion()
    {
        // 注意：测试工程把 PluginUpdate.cs 当**源码**一起编译（见 BridgeHarness.csproj），
        // 所以这里的 PluginUpdate.LocalVersion 读到的是「本测试工程程序集」的版本，
        // 不是插件的版本。因此下面只做形态与大小关系的健全性检查；
        // **真正校验插件版本的是再下面那条：直接读编译产物 dll 的元数据。**
        var local = PluginUpdate.LocalVersion;
        Check("本地版本能从程序集读出且是版本形态",
            PluginUpdate.NormalizeVersion(local) != null, local);

        // 校验解析逻辑本身：LocalVersion 应当等于「本类型所在程序集」的版本
        // （在插件里即 NpsPushBridge.dll 的版本；在测试工程里即 BridgeHarness 的版本）
        var asmVersion = typeof(PluginUpdate).Assembly.GetName().Version?.ToString();
        Check("本地版本解析为所在程序集的版本",
            asmVersion != null && PluginUpdate.CompareVersion(local, asmVersion) == 0,
            $"{local} / {asmVersion ?? "?"}");

        // ★ 单一真源校验：csproj 声明的 <Version> 必须与**编译产物**的版本一致 ——
        //   改完版本号却忘记重新编译时，这条会立刻失败（最容易犯的错）。
        var declared = ReadCsprojVersion();
        var built = ReadBuiltPluginVersion();
        Check("csproj 声明的 <Version> 与编译产物 dll 的版本一致（改版本后必须重新编译）",
            declared != null && built != null && PluginUpdate.CompareVersion(declared, built) == 0,
            $"csproj={declared ?? "?"} · dll={built ?? "?"}");
    }

    /// <summary>从插件工程文件里读出声明的 <c>&lt;Version&gt;</c>。</summary>
    private static string? ReadCsprojVersion()
    {
        var csproj = FindPluginProjectFile();
        if (csproj == null) return null;
        try
        {
            var match = Regex.Match(File.ReadAllText(csproj), @"<Version>\s*([^<\s]+)\s*</Version>");
            return match.Success ? match.Groups[1].Value.Trim() : null;
        }
        catch { return null; }
    }

    /// <summary>读编译产物 NpsPushBridge.dll 的程序集版本（优先分发包，其次 bin 输出）。</summary>
    private static string? ReadBuiltPluginVersion()
    {
        var dir = FindPluginProjectFile() is { } csproj
            ? Path.GetDirectoryName(csproj)
            : null;
        if (dir == null) return null;

        foreach (var relative in new[]
                 {
                     @"dist\NpsPushBridge\NpsPushBridge.dll",
                     @"bin\Release\net10.0-windows10.0.19041.0\NpsPushBridge.dll",
                 })
        {
            var path = Path.Combine(dir, relative);
            if (!File.Exists(path)) continue;
            try { return AssemblyName.GetAssemblyName(path).Version?.ToString(); }
            catch { /* 读不到继续找下一个候选 */ }
        }
        return null;
    }

    /// <summary>从测试输出目录向上查找插件工程文件；找不到返回 null（调用方跳过比对）。</summary>
    private static string? FindPluginProjectFile()
    {
        try
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            for (int depth = 0; depth < 8 && dir != null; depth++, dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, "NpsPushBridge.csproj");
                if (File.Exists(candidate)) return candidate;
            }
        }
        catch { /* 忽略：返回 null 由调用方处理 */ }
        return null;
    }

    // ====================================================================
    // ⑦ 异常收敛 + 联网端到端
    // ====================================================================

    private static async Task TestFailureConvergenceAsync()
    {
        // 指向本机 1 号端口（必然拒绝连接）：验证网络异常被收敛，而不是抛给调用方
        var result = await PluginUpdate.CheckAsync("https://127.0.0.1:1/market", PluginUpdate.PluginNameMarker,
            CancellationToken.None);

        Check("不可达地址：收敛为 Failed 而不抛异常", result.Status == UpdateStatus.Failed,
            result.Status.ToString());
        Check("不可达地址：状态摘要不显示「有新版本」", !result.HasUpdate);
        Check("不可达地址：Message 里有可读原因", result.Message.Length > 0, result.Message);
        Check("不可达地址：UpdateUrl 回落到市场页",
            result.UpdateUrl == PluginUpdate.MarketPageUrl, result.UpdateUrl);
        Check("不可达地址：Describe() 可安全调用", result.Describe().Contains("检查失败"), result.Describe());
    }

    private static async Task TestLiveMarketAsync()
    {
        var local = PluginUpdate.LocalVersion;
        var result = await PluginUpdate.CheckAsync(CancellationToken.None);

        if (result.Status == UpdateStatus.Failed)
        {
            _skipped++;
            Console.WriteLine($"  [SKIP] 联网检查（{result.Message}）");
            return;
        }

        Check("联网：能判定出「已是最新 / 有新版本」",
            result.Status is UpdateStatus.UpToDate or UpdateStatus.UpdateAvailable, result.Status.ToString());
        Check("联网：远端版本是版本形态",
            PluginUpdate.NormalizeVersion(result.RemoteVersion) != null, result.RemoteVersion);
        Check("联网：判定方向与版本比较一致",
            result.HasUpdate == (PluginUpdate.CompareVersion(local, result.RemoteVersion) < 0),
            $"{local} vs {result.RemoteVersion}");
        Check("联网：更新链接指向市场站点",
            result.UpdateUrl.StartsWith("https://nps.georgewu.top/", StringComparison.Ordinal), result.UpdateUrl);

        Console.WriteLine($"  [INFO] 市场版本 v{result.RemoteVersion} · 判定 {result.Status} · 链接 {result.UpdateUrl}");

        // 另外算一次「插件产物版本 vs 市场版本」，这才是用户实际会看到的判定结果
        //（上面结果里的 LocalVersion 来自测试工程自身程序集，见 TestLocalVersion 的说明）
        var pluginVersion = ReadBuiltPluginVersion();
        if (pluginVersion != null)
        {
            int cmp = PluginUpdate.CompareVersion(pluginVersion, result.RemoteVersion);
            Console.WriteLine($"  [INFO] 插件产物 v{pluginVersion} · 市场 v{result.RemoteVersion} · " +
                              (cmp < 0 ? "需要更新" : "已是最新"));
        }
    }

    // ====================================================================

    private static void Check(string name, bool condition, string? detail = null)
    {
        if (condition)
        {
            _passed++;
            Console.WriteLine($"  [PASS] {name}");
        }
        else
        {
            _failed++;
            Console.WriteLine($"  [FAIL] {name}{(string.IsNullOrEmpty(detail) ? "" : $"  →  {detail}")}");
        }
    }
}
