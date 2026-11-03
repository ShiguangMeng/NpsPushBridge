using System.Collections.Concurrent;
using System.Text;

namespace NpsPushBridge;

/// <summary>
/// 图标解析结果：一次「按 kind 补 icon」的决策，连同「为什么这么定」一起返回。
///
/// 之所以不直接返回一个字符串：日志与状态页需要解释「这条消息为什么用/不用图标」，
/// 否则用户看到「没图标」时分不清是「本来就没配」还是「配了但没命中」。
/// </summary>
internal readonly record struct IconDecision(string Icon, string Source, string Reason)
{
    /// <summary>是否真的补上了图标（空串表示不补，交给宿主用预设图标）。</summary>
    public bool HasIcon => Icon.Length > 0;
}

/// <summary>
/// 「应用 → 图标」的映射真源：把入站消息的 <c>kind</c> 归类，决定要补的 <c>icon</c> 值。
///
/// ★ 兼容性契约（本类存在的全部理由）★
/// 宿主 1.8.0 起支持 HTTP 请求里的 <c>icon</c> 字段，但**旧版宿主完全不认识它**——
/// 而旧版 host 的解析是「只取 kind/title/subtitle 三个字段」，多出来的字段会被忽略，
/// 因此**多带一个 icon 字段不会让旧宿主出错**（这正是「保持与现有接口兼容」的实现依据）。
/// 反过来，本插件必须做到「拿不到映射就一个字节都不多写」，见 <see cref="BuildJsonForActivity"/>。
///
/// ★ 两种制品，优先级固定：内置别名 &gt; 图片链接 ★
/// 别名是「零成本」的（宿主从自己的 data/image 目录读本地文件，不走网络、没有超时），
/// 所以能用别名就一定用别名；只有在别名覆盖不到时，才退到图片链接。
/// 这个顺序不能反 —— 图片链接要联网，而宿主对下载只给 **3 秒超时**，
/// 一旦超时按钮就「静默回退预设图标」，用户会以为功能坏了。
///
/// ★ 别名只有 5 个能用（已实测宿主的 data/image 目录）★
/// 宿主 <c>ToastIconProvider.FindAliasFile</c> 的约定是
/// <c>data/image/&lt;别名&gt;-icon|logo.*</c>，而 1.8.0 只随包带了：
///   qq-icon.png / bilibili-logo.png / chrome-logo.png / edge-logo.png / potplayer-logo.jpg
///   / wintoast-icon.png（windows|wintoast|default 三个别名共用）
/// 所以 **微信 / 抖音 / 快手 / 小红书 / 短信 / 知乎 等一律没有别名可用**，
/// 硬写 "wechat" 这种别名只会「找不到文件 → 静默回退预设图标」，
/// 看着像配了、实际没生效。这些应用必须走图片链接（见 <see cref="BuiltinLinks"/>）。
/// </summary>
internal static class IconCatalog
{
    /// <summary>
    /// 别名映射：规整化后的 key → 宿主的内置别名。
    ///
    /// ⚠️ 别名**未必真的能命中** —— 宿主 <c>ToastIconProvider.ResolveBuiltin</c> 是去
    /// <c>&lt;宿主 exe 所在目录&gt;/data/image/&lt;别名&gt;-icon|logo.*</c> 读**磁盘文件**，
    /// 而这些 png 在宿主 csproj 里是 <c>Content + CopyToOutputDirectory</c>（**散文件**）。
    /// 若用户装的是**单文件发布版**（只有一个 exe），该目录根本不存在 →
    /// 所有别名一律查不到 → 静默回退预设图标。**QQ 图标不显示就是这个原因**。
    /// 因此真正决定用不用别名的是 <see cref="AliasFileExists"/>（运行时探测），
    /// 本表只负责「这个 key 对应哪个别名」。
    /// </summary>
    private static readonly Dictionary<string, string> BuiltinAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        // —— 用别名就够了（宿主自带图，最快最稳）——
        ["qq"] = "qq",
        ["com.tencent.mobileqq"] = "qq",
        ["com.tencent.qqlite"] = "qq",
        ["com.tencent.tim"] = "qq",          // TIM 无独立图，借 QQ 的
        ["bilibili"] = "bilibili",
        ["tv.danmaku.bili"] = "bilibili",
        ["哔哩哔哩"] = "bilibili",
        ["chrome"] = "chrome",
        ["googlechrome"] = "chrome",
        ["com.android.chrome"] = "chrome",
        ["edge"] = "edge",
        ["msedge"] = "edge",
        ["microsoftedge"] = "edge",
        ["potplayer"] = "potplayer",
        ["kmplayer"] = "potplayer",
        ["windows"] = "windows",
        ["wintoast"] = "windows",
        ["default"] = "windows",
        ["系统通知"] = "windows",
    };

    /// <summary>
    /// 宿主 <c>ToastIconProvider._aliasFiles</c> 的**镜像**：别名 → 文件名，**只放名字与别名不同前缀的那几条**。
    ///
    /// 宿主解析别名有两条路，优先级是死的：
    ///   ① 命中它硬编码的表 → 直接用表里的文件名（目前只有 windows / wintoast / default → wintoast-icon.png）；
    ///   ② 否则按 <c>&lt;别名&gt;-icon|logo|<i>空</i>.&lt;ext&gt;</c> 在 <c>data/image/</c> 里找 ——
    ///      **文件名前缀必须等于别名本身**。
    ///
    /// ⚠️⚠️ 所以本表**只能写①那几条**。曾经这里多了一条
    /// <c>["bili"] = ["bilibili-logo.png"]</c>，后果很隐蔽：
    /// 插件据此判定「别名 bili 可用」→ 把 <c>bili</c> 送给宿主 →
    /// 而宿主只按 ② 去找 <c>bili-logo.png</c>（目录里其实是 <c>bilibili-logo.png</c>）→ 找不到 →
    /// **静默回退默认图标**。用户看到的是「配了却没图标」，而且查不出原因。
    /// 2026-10-09 已删除，并把键 <c>bili</c> 的别名值改为 <c>bilibili</c>（真正能解析的那个）。
    ///
    /// 结论：**别在这里“顺手补一条”** —— 名字对不上就是不可用，宁可让
    /// <see cref="Decide"/> 走内联 / 链接兜底。键集合必须恒等于宿主硬编码表（见 IconCatalogTests 的契约断言）。
    /// </summary>
    private static readonly Dictionary<string, string[]> AliasFileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["windows"] = ["wintoast-icon.png"],
        ["wintoast"] = ["wintoast-icon.png"],
        ["default"] = ["wintoast-icon.png"],
    };

    /// <summary>宿主按 <c>&lt;别名&gt;-icon|logo.<ext></c> 约定自动查找的后缀组合。</summary>
    private static readonly string[] AliasSuffixes = ["-icon", "-logo", ""];
    private static readonly string[] AliasExts = [".png", ".jpg", ".jpeg", ".bmp"];

    /// <summary>
    /// 图片链接映射：别名覆盖不到的应用。
    ///
    /// 本表每一条都**实测过**：HTTP 200、返回 PNG（不是 SVG —— SkiaSharp 的
    /// <c>SKBitmap.Decode</c> 只能解位图，SVG 会静默失败）、响应时间远小于宿主的 3 秒超时。
    /// 用的是 alipayobjects（阿里 CDN），国内访问快且稳定，实测 0.03–0.25s、每张 5–9 KB。
    ///
    /// ⚠️ 加新条目必须自己先验一遍这三件事，否则等于给用户埋一个「偶尔没图标」的随机故障。
    /// 尤其**不要**用 simple-icons / iconify 这类图标站的链接：它们只提供 SVG，宿主解不出来。
    /// </summary>
    private static readonly Dictionary<string, string> BuiltinLinks = new(StringComparer.OrdinalIgnoreCase)
    {
        // 微信：com.tencent.mm 是手机端包名，微信 是通知里可能出现的显示名
        ["wechat"] = Link("A*_ELBTJLp0dQAAAAAAAAAAAAADmJ7AQ"),
        ["weixin"] = Link("A*_ELBTJLp0dQAAAAAAAAAAAAADmJ7AQ"),
        ["com.tencent.mm"] = Link("A*_ELBTJLp0dQAAAAAAAAAAAAADmJ7AQ"),
        ["微信"] = Link("A*_ELBTJLp0dQAAAAAAAAAAAAADmJ7AQ"),

        // ★ QQ：别名 qq 在**单文件发布版**宿主上会失效（data/image 不存在），
        //   所以这里必须备一条链接兜底 —— 否则「QQ 图标不显示」。
        //   正常情况下仍优先走别名（本地图、零网络），只有探测到别名不可用才落到这里。
        ["qq"] = Link("A*AbGNTpA5JLwAAAAAAAAAAAAADmJ7AQ"),
        ["com.tencent.mobileqq"] = Link("A*AbGNTpA5JLwAAAAAAAAAAAAADmJ7AQ"),
        ["com.tencent.qqlite"] = Link("A*AbGNTpA5JLwAAAAAAAAAAAAADmJ7AQ"),
        ["com.tencent.tim"] = Link("A*AbGNTpA5JLwAAAAAAAAAAAAADmJ7AQ"),

        ["kuaishou"] = Link("A*swueRrrKvbcAAAAAAAAAAAAADmJ7AQ"),
        ["com.smile.gifmaker"] = Link("A*swueRrrKvbcAAAAAAAAAAAAADmJ7AQ"),
        ["快手"] = Link("A*swueRrrKvbcAAAAAAAAAAAAADmJ7AQ"),

        // 抖音：海外版 TikTok 与国内版共用同一张音符图
        ["douyin"] = Link("A*8IXHQLvx9QkAAAAAAAAAAAAADmJ7AQ"),
        ["tiktok"] = Link("A*8IXHQLvx9QkAAAAAAAAAAAAADmJ7AQ"),
        ["com.ss.android.ugc.aweme"] = Link("A*8IXHQLvx9QkAAAAAAAAAAAAADmJ7AQ"),
        ["抖音"] = Link("A*8IXHQLvx9QkAAAAAAAAAAAAADmJ7AQ"),

        ["netease"] = Link("A*upKlRJ9QB4cAAAAAAAAAAAAADmJ7AQ"),
        ["neteasecloudmusic"] = Link("A*upKlRJ9QB4cAAAAAAAAAAAAADmJ7AQ"),
        ["com.netease.cloudmusic"] = Link("A*upKlRJ9QB4cAAAAAAAAAAAAADmJ7AQ"),
        ["网易云音乐"] = Link("A*upKlRJ9QB4cAAAAAAAAAAAAADmJ7AQ"),
    };

    /// <summary>
    /// 已知「有稳定图标源、但本插件尚未收录」的应用名。
    /// 命中它们时在日志里明确写「未收录」，而不是含糊地留空 ——
    /// 用户看到「未收录」才知道该去哪儿加（配置界面的「图标映射」页可自行补上）。
    /// </summary>
    private static readonly HashSet<string> KnownUnmapped = new(StringComparer.OrdinalIgnoreCase)
    {
        "xiaohongshu", "com.xingin.xhs", "小红书",
        "zhihu", "com.zhihu.android", "知乎",
        "weibo", "com.sina.weibo", "微博",
        "dingtalk", "com.alibaba.android.rimet", "钉钉",
        "wework", "com.tencent.wework", "企业微信",
        "taobao", "com.taobao.taobao", "淘宝",
        "alipay", "com.eg.android.alipaygphone", "支付宝",
        "sms", "mms", "短信",
    };

    /// <summary>阿里 CDN 上品牌图的前缀（图片以 ID 寻址，没有可读文件名）。</summary>
    private const string AlipayCdnPrefix = "https://mdn.alipayobjects.com/huamei_qa8qxu/afts/img/";

    private static string Link(string id) => AlipayCdnPrefix + id + "/original";

    /// <summary>
    /// 内置**别名**的条目数，供配置界面显示「内置别名 N 条」。
    /// ⚠️ 只数别名、不数链接：一览里现在只列别名类（见 <see cref="Showcase"/>），
    /// 数字与列表必须对得上，否则用户会以为漏显示了。
    /// </summary>
    public static int BuiltinAliasCount => BuiltinAliases.Count;

    /// <summary>内置映射（别名 + 链接）的条目总数 —— 仅用于日志 / 诊断，界面上的「内置 N 条」请用别名数。</summary>
    public static int BuiltinCount => BuiltinAliases.Count + BuiltinLinks.Count;

    /// <summary>内置别名表（只读视图，供配置界面列出「宿主自带、零成本」的那些）。</summary>
    public static IReadOnlyDictionary<string, string> Aliases => BuiltinAliases;

    /// <summary>
    /// 「文件名与别名不同前缀」的例外表（只读视图）。
    /// 暴露出来是为了让**契约断言**能守住它：键集合必须恒等于宿主硬编码表，
    /// 往这里顺手加一条就会造出「插件以为可用、宿主取不到」的假别名（见该表本身的注释）。
    /// </summary>
    public static IReadOnlyDictionary<string, string[]> AliasFileMap => AliasFileNames;

    /// <summary>
    /// 内置**别名**的「典型键」清单，供配置界面的「内置一览」展示（**只列别名类，不列图片链接**）。
    ///
    /// ★ 为什么只列别名类（2026-10-09 按用户口径收敛）★
    /// 一览的用途是「让用户知道哪些已经不用自己配了」。
    /// 别名是**零成本**的（宿主读本地文件，不联网、没有超时），列出来是实实在在的承诺；
    /// 而图片链接要联网、宿主只给 3 秒超时，偶尔失败是常态 ——
    /// 把它和别名混在一张表里，用户会以为「这些都已经配好了、肯定有图标」，
    /// 实际却可能因为网络拿不到图。所以一览只保留**宿主别名**这一类，
    /// 与「宿主支持什么别名」严格对齐（宿主文档：往 `data/image/` 丢 `<别名>-icon|logo.*` 即可扩展）。
    ///
    /// ⚠️ 链接类的映射**仍然保留在 <see cref="BuiltinLinks"/> 里并照常生效** ——
    /// 只是不在一览里展示。微信等应用的图标不会因此消失。
    ///
    /// ⚠️ 值一律走 <see cref="LookupBuiltin"/>（别名文件不存在时它**不会**替你降级到链接；
    /// 这里展示的是「表里写了什么」，不是「本机实际会用哪个」—— 实际用哪个由 <see cref="Decide"/> 决定，
    /// 因为那要读运行时探测结果）。早期这里对 qq/bilibili 等硬写了别名字面量，
    /// 一旦表里改了值就会出现「界面显示 A、实际用 B」的偏差，故改为统一查表。
    /// </summary>
    public static IReadOnlyList<(string Key, string Icon)> Showcase()
    {
        // 只列「值确实是别名」的键（判定走 Aliases 表，而不是靠手写清单 ——
        // 手写清单正是上一版把 bili 这种假别名混进来的原因）。
        string[] keys = ["qq", "bilibili", "chrome", "edge", "potplayer", "windows"];
        return keys.Select(k => (Key: k, Icon: LookupBuiltin(k)))
                   .Where(i => i.Icon.Length > 0 && BuiltinAliases.ContainsValue(i.Icon))
                   .ToList();
    }

    /// <summary>
    /// 决定这条消息该补什么图标。
    ///
    /// 优先级（自上而下，命中即返回）：
    ///   ① 入站消息**自己带了 icon** → 原样保留。这是最重要的一条：
    ///      发送端明确给的值永远高于我们的猜测，否则等于插件擅自改用户的数据。
    ///   ② 用户在配置界面写的**自定义映射**（可覆盖内置）。
    ///   ③ 内置**别名**（宿主自带图，零网络成本）。
    ///   ④ 内置**图片链接**。
    ///   ⑤ 都没有 → 不补（<see cref="IconDecision.HasIcon"/> 为 false），宿主动用预设图标。
    /// </summary>
    /// <param name="kind">入站消息的 kind 字段（应用名 / 包名 / 中文名都可能）。</param>
    /// <param name="existingIcon">入站消息里已有的 icon；为空表示发送端没给。</param>
    /// <param name="custom">用户自定义映射（可为 null）。</param>
    public static IconDecision Decide(string? kind, string? existingIcon, IconOverrides? custom)
    {
        // ① 发送端已经给了图标 —— 尊重它，一个字节都不改
        if (!string.IsNullOrWhiteSpace(existingIcon))
            return new IconDecision(existingIcon.Trim(), "入站", "发送端已指定 icon，原样保留");

        var name = Normalize(kind);
        if (name.Length == 0)
            return new IconDecision("", "无", "kind 为空，无法归类");

        // ② 用户自定义映射优先于内置
        if (custom != null && custom.TryGet(name, out var customIcon))
            return new IconDecision(customIcon, "自定义", $"命中自定义映射「{name}」");

        // ③④ 内置：先别名、后链接
        // ⚠️ 这里**不能**用 LooksLikeAlias 做前置过滤：安卓包名（com.tencent.mobileqq、
        //    tv.danmaku.bili）含「.」，会被那个判据排除掉，而别名表里恰恰为它们写了显式条目
        //    → 结果是「表里有、却永远查不到」。别名表本身就是白名单，直接查表即可。
        //
        // ★★ 关键：别名必须**运行时确认文件真的在**才敢用 ★★
        //    别名是「让宿主去 data/image/ 读本地文件」，而该目录在**单文件发布版**下
        //    根本不存在（qq-icon.png 等是 CopyToOutputDirectory 的散文件，不会进单文件包）
        //    → 用了别名 = 宿主查不到文件 = 静默回退预设图标。QQ 图标不显示就是这个原因。
        //
        //    三档降级（优先级即稳定性）：别名 → 内联 base64 → 网络链接
        //      ① 别名：最短，宿主读本地文件，零网络。但依赖 data/image 目录存在。
        //      ② 内联 base64：**不依赖任何目录布局、也不联网** —— 别名失效时的最优解
        //         （把宿主自己那张图读出来内联发回去，图还是官方那张）。
        //      ③ 网络链接：要联网且宿主只给 3 秒超时，仅在 ② 也拿不到时用。
        var alias = LookupAlias(name);
        if (alias.Length > 0)
        {
            if (AliasFileExists(alias))
                return new IconDecision(alias, "别名", $"命中内置别名「{name}」→ {alias}（宿主自带图，不走网络）");

            // 别名文件缺失（多为单文件发布版宿主）→ 优先内联宿主那张图，其次网络链接
            var inline = InlineAliasDataUri(alias);
            if (inline.Length > 0)
                return new IconDecision(inline, "内联",
                    $"本机 data/image 下没有别名「{alias}」的图（多为单文件发布版），已改为内联同一张图（不走网络）");

            var aliasFallback = LookupLink(name);
            if (aliasFallback.Length > 0)
                return new IconDecision(aliasFallback, "链接",
                    $"别名「{alias}」在本机 data/image 下不存在（多为单文件发布版），改用图片链接「{name}」");

            return new IconDecision("", "别名失效",
                $"内置别名「{alias}」在本机 data/image 下不存在（多为单文件发布版），且「{name}」无图片链接可兜底");
        }

        var link = LookupLink(name);
        if (link.Length > 0)
            return new IconDecision(link, "链接", $"命中内置图片链接「{name}」（需联网，宿主 3 秒超时）");

        // ⑤ 未命中 —— 区分「已知未收录」和「完全没听说过」，日志措辞不同
        if (KnownUnmapped.Contains(name))
            return new IconDecision("", "未收录", $"「{name}」尚未收录图标，可在配置界面「图标映射」里自行添加");

        return new IconDecision("", "未命中", $"「{name}」无对应图标，使用宿主预设图标");
    }

    /// <summary>
    /// 查内置映射的「最终 icon 值」（先别名、后链接 —— 与 <see cref="Decide"/> 同一优先级）。
    /// 供配置界面/一览展示用；只关心「这个键最终会补上什么」。
    ///
    /// ⚠️ 不能只查 <see cref="BuiltinAliases"/>：微信/抖音/快手/网易云这些**只有链接**的应用
    /// 会查不到，界面就会把「明明已内置」的条目标成空白。
    /// </summary>
    public static string LookupBuiltin(string key)
    {
        var alias = BuiltinAliases.TryGetValue(key, out var a) ? a : "";
        return alias.Length > 0 ? alias : LookupLink(key);
    }

    /// <summary>只查别名表（不含链接），用于判定「这个键是否有零网络成本的别名」。</summary>
    public static string LookupAlias(string key)
        => BuiltinAliases.TryGetValue(key, out var v) ? v : "";

    private static string LookupLink(string key)
        => BuiltinLinks.TryGetValue(key, out var v) ? v : "";

    /// <summary>
    /// 只查**图片链接表**（不含别名）。仅供测试与排查用 —— 因为
    /// <see cref="LookupBuiltin"/> 是「先别名后链接」，配了别名的键（如 qq）永远查不到它的链接，
    /// 于是「别名失效时到底有没有链接兜底」这件事就无法被断言。这个方法把链接表单独暴露出来，
    /// 让「双保险」的两个半环可以**各自独立**验证。
    ///
    /// ⚠️ 生产路径**不要**用它做决策：决定用别名还是链接的是 <see cref="Decide"/>（含运行时探测）。
    /// </summary>
    public static string LookupLinkForTest(string key) => LookupLink(key);

    // ========================================================================
    // ★★ 别名可用性探测（QQ 图标不显示的根因所在）★★
    // ========================================================================

    /// <summary>数据目录缓存：「宿主 exe 目录」→ 该目录下 data/image 里实际存在的文件名集合。
    /// 用 ConcurrentDictionary 因为 HTTP 监听线程会并发调用；每台机器只探一次。</summary>
    private static readonly ConcurrentDictionary<string, HashSet<string>> _aliasProbeCache = new();

    /// <summary>
    /// 宿主的数据目录（`&lt;宿主 exe 目录&gt;/data/image`，与宿主
    /// <c>ToastIconProvider.ResolveBuiltin</c> 的查找根一致）。
    ///
    /// ⚠️ 用 <see cref="AppContext.BaseDirectory"/> 而不是插件的目录：
    /// 插件下发给宿主的别名是**宿主自己**去解析的，所以必须按宿主的视角找文件。
    /// 插件运行在宿主进程内，`BaseDirectory` = 宿主 exe 所在目录 —— 正是宿主查找的地方。
    /// （单文件发布时它是 exe 所在目录，而 `data/image` 并不存在 → 探测会落空，这是正确的结论。）
    /// </summary>
    private static string DataImageDirectory()
        => Path.Combine(AppContext.BaseDirectory, "data", "image");

    /// <summary>
    /// 别名在这台机器上是否**真的能取到图** —— 即宿主能不能在
    /// `data/image/` 下找到 `<别名>-icon|logo|.<ext>`。
    ///
    /// ★ 为什么不直接信任别名表 ★
    /// 别名只是「告诉宿主去哪找」，找不到时宿主**不会报错**，会静默回退预设图标 ——
    /// 用户看到的是「配了图标却没生效」，极难排查。
    /// 实测：`qq-icon.png` 等在宿主 csproj 里是 `Content + CopyToOutputDirectory`（散文件），
    /// **单文件发布版不含这些文件**，于是 `data/image/` 目录不存在，所有别名一律失效。
    /// 所以插件必须自己先探一下：能取到才用别名，取不到就改用图片链接兜底。
    ///
    /// 结果按目录缓存（进程内只探一次），避免每条消息都打 9 次 File.Exists。
    /// </summary>
    public static bool AliasFileExists(string alias) => FindAliasFile(alias) != null;

    /// <summary>
    /// 找到别名对应的**绝对路径**（找不到返回 null）。查找顺序与宿主
    /// <c>ToastIconProvider.ResolveBuiltin</c> 完全一致，保证「插件判定可用」⇔「宿主能取到」。
    /// </summary>
    public static string? FindAliasFile(string alias)
    {
        if (string.IsNullOrWhiteSpace(alias)) return null;

        var dir = DataImageDirectory();
        var files = _aliasProbeCache.GetOrAdd(dir, static d =>
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (Directory.Exists(d))
                    foreach (var f in Directory.EnumerateFiles(d))
                        set.Add(Path.GetFileName(f));
            }
            catch
            {
                // 目录不可读（权限 / 竞态）→ 当作「没有」处理，宁可用链接兜底，也不要冒险用别名
            }
            return set;
        });

        if (files.Count == 0) return null;

        // ① 首查「文件名不规则」的那几个（与宿主 _aliasFiles 同源）
        if (AliasFileNames.TryGetValue(alias, out var mapped))
        {
            foreach (var name in mapped)
                if (files.Contains(name)) return Path.Combine(dir, name);
            // 映射到的名字不在，也继续按通用约定找一遍（宿主的行为就是这样）
        }

        // ② 通用约定：<别名>-icon|logo|.<ext>
        foreach (var suffix in AliasSuffixes)
        {
            foreach (var ext in AliasExts)
            {
                var name = alias + suffix + ext;
                if (files.Contains(name)) return Path.Combine(dir, name);
            }
        }
        return null;
    }

    /// <summary>
    /// 把别名对应的本地图片**内联成 <c>data:</c URI**，作为「宿主没有 data/image 目录」时
    /// 的零网络兜底方案。
    ///
    /// ★ 为什么需要这个 ★
    /// 别名只是让宿主去读本地文件；单文件发布版宿主不带 `data/image/`，
    /// 别名一律失效。而 base64 内联是**宿主直接解码字符串**，不依赖任何磁盘布局，
    /// 也不像图片链接那样要联网（宿主只给 3 秒超时）。因此这是最稳的一档：
    /// 别名可用 → 用别名（最短）；否则 **能用内联就用内联**；再不行才落到网络链接。
    ///
    /// 成本：宿主对 base64 的长度上限是 6 MB，而 data/image 下的图只有 5–56 KB，
    /// 编码后约 8–76 KB —— 完全在限内，且只在「别名失效」的机器上才会走这条路径（带缓存）。
    /// </summary>
    public static string InlineAliasDataUri(string alias)
    {
        var path = FindAliasFile(alias);
        if (path == null) return "";

        if (_inlineCache.TryGetValue(path, out var cached)) return cached;

        string uri = "";
        try
        {
            var bytes = File.ReadAllBytes(path);
            // 设上限防御：万一 data/image 下被塞了张超大图，宁可退回网络链接也不撑爆请求体
            if (bytes.Length is > 0 and <= 512 * 1024)
            {
                var mime = Path.GetExtension(path).ToLowerInvariant() switch
                {
                    ".jpg" or ".jpeg" => "image/jpeg",
                    ".bmp" => "image/bmp",
                    _ => "image/png",
                };
                uri = $"data:{mime};base64,{Convert.ToBase64String(bytes)}";
            }
        }
        catch
        {
            // 读盘失败 → 空串，调用方会继续往下一档降级
        }

        _inlineCache[path] = uri;
        return uri;
    }

    /// <summary>别名对应的图片内联缓存（路径 → data URI）。</summary>
    private static readonly ConcurrentDictionary<string, string> _inlineCache = new();

    /// <summary>清空探测缓存（配置界面「重新探测」或测试用）。</summary>
    public static void ResetAliasProbeCache()
    {
        _aliasProbeCache.Clear();
        _inlineCache.Clear();
    }

    /// <summary>
    /// 当前数据目录与探测结论的说明文本，供状态页 / 日志排查「别名为什么没生效」。
    /// </summary>
    public static string DescribeAliasProbe()
    {
        var dir = DataImageDirectory();
        bool has = Directory.Exists(dir);
        return has
            ? $"别名数据目录：{dir}（存在，{(_aliasProbeCache.TryGetValue(dir, out var f) ? f.Count : 0)} 个文件）"
            : $"别名数据目录：{dir}（**不存在** → 内置别名无法生效，插件会改用图片链接兜底；"
              + "单文件发布版属正常现象）";
    }

    /// <summary>
    /// 把 kind 规整成查表用的 key：去空白、去外层引号、转小写、去掉常见后缀。
    ///
    /// 手机端模板里的 {{APP_NAME}} 可能带这些尾巴：<c>"微信"</c>、<c>微信(3条新消息)</c>、
    /// <c>微信 </c>。不处理的话「微信(3条新消息)」查不到「微信」——
    /// 而通知标题里的「(N条新消息)」恰恰是最高频的形态之一。
    /// </summary>
    public static string Normalize(string? kind)
    {
        if (string.IsNullOrWhiteSpace(kind)) return "";

        var s = kind.Trim().Trim('"', '\'', '“', '”').Trim();
        if (s.Length == 0) return "";

        // 去掉「(3条新消息)」「（3条新消息）」「[2条]」这类纯计数尾巴
        s = StripTrailingCount(s);

        // 包名保持原样（大小写不敏感查表）；显示名转小写以便匹配英文别名
        return s.Trim().ToLowerInvariant();
    }

    /// <summary>
    /// 去掉显示名末尾的计数括号，如「微信(3条新消息)」→「微信」。
    /// 只在括号**位于末尾**且括号内**不含空格分隔的第二个词**时剥离，
    /// 以免误伤「QQ(国际版)」这类本身就是名字一部分的情形 —— 后者剥了反而查不到。
    /// </summary>
    private static string StripTrailingCount(string s)
    {
        if (s.Length < 3) return s;

        int open = -1, close = -1;
        char last = s[^1];
        if (last == ')' || last == '）')
        {
            close = s.Length - 1;
            open = last == ')' ? s.LastIndexOf('(') : s.LastIndexOf('（');
        }
        else if (last == ']' || last == '】')
        {
            close = s.Length - 1;
            open = last == ']' ? s.LastIndexOf('[') : s.LastIndexOf('【');
        }

        if (open <= 0 || close <= open + 1) return s;

        var inner = s[(open + 1)..close];
        // 括号里必须「有数字」且「不含空格」才认定是计数尾巴
        bool hasDigit = inner.Any(char.IsDigit);
        bool hasSpace = inner.Any(char.IsWhiteSpace);
        if (!hasDigit || hasSpace) return s;

        return s[..open].Trim();
    }

    /// <summary>
    /// 字符串是否「看起来是个别名」（纯字母数字 + 连字符下划线，长度 ≤32）。
    /// 与宿主 <c>ToastIconProvider.IsPlainAlias</c> 的判据保持一致 —— 本插件据此
    /// 判断「这个名字有可能命中宿主的别名机制」，含空格或中文的名字不可能命中，直接跳过。
    /// </summary>
    private static bool LooksLikeAlias(string s)
    {
        if (s.Length == 0 || s.Length > 32) return false;
        foreach (char c in s)
            if (!char.IsLetterOrDigit(c) && c != '-' && c != '_') return false;
        return true;
    }

    /// <summary>
    /// 生成转发给宿主的 JSON。
    ///
    /// ★ 兼容性关键：<paramref name="icon"/> 为空时**不写 icon 字段** ★
    /// 旧宿主忽略未知字段，所以多写不会出错；但「本来就没有图标」时少写一个字段，
    /// 能让请求体与升级前的插件**逐字节一致**（便于排查，也让老用户看不出差别）。
    /// </summary>
    public static string BuildJson(string kind, string title, string subtitle, string icon)
    {
        var sb = new StringBuilder(200);
        sb.Append("{\"kind\":");
        AppendJsonString(sb, kind);
        sb.Append(",\"title\":");
        AppendJsonString(sb, title);
        sb.Append(",\"subtitle\":");
        AppendJsonString(sb, subtitle);

        if (!string.IsNullOrEmpty(icon))
        {
            sb.Append(",\"icon\":");
            AppendJsonString(sb, icon);
        }

        sb.Append('}');
        return sb.ToString();
    }

    /// <summary>只转义双引号，非 ASCII 按原样输出（与既有 <c>NpsActivityEndpoint.AppendJsonString</c> 同风格）。</summary>
    private static void AppendJsonString(StringBuilder sb, string value)
    {
        sb.Append('"');
        foreach (char ch in value)
        {
            if (ch == '"') sb.Append('\\').Append('"');
            else sb.Append(ch);
        }
        sb.Append('"');
    }
}

/// <summary>
/// 用户自定义的「应用 → icon」映射（配置界面「图标映射」页维护）。
///
/// 存成一行一条的 <c>应用名=icon值</c> 文本（与白名单同样的持久化风格）：
///   · 数据结构简单到不需要 schema，用户也能直接看懂存了什么；
///   · 键匹配统一走 <see cref="IconCatalog.Normalize"/>&#160;—— 与内置映射同一套规整规则，
///     否则「微信」和「微信 」在界面上看着一样、行为却不同。
/// </summary>
internal sealed class IconOverrides
{
    private readonly Dictionary<string, string> _map = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>原始文本（持久化用，保留用户写的顺序与注释）。</summary>
    public string Raw { get; private set; } = "";

    public int Count => _map.Count;

    /// <summary>已解析的全部键（按加入顺序），供界面列出。</summary>
    public IReadOnlyCollection<string> Keys => _map.Keys;

    /// <summary>从持久化文本解析。任意一行格式不对只是被跳过，绝不让整份配置失效。</summary>
    public static IconOverrides Parse(string? raw)
    {
        var result = new IconOverrides();
        result.Raw = raw ?? "";

        foreach (var line in result.Raw.Split('\n'))
        {
            var text = line.Trim();
            if (text.Length == 0 || text.StartsWith('#')) continue;   // 空行与注释跳过

            int eq = text.IndexOf('=');
            if (eq <= 0) continue;

            var key = IconCatalog.Normalize(text[..eq]);
            var value = text[(eq + 1)..].Trim();
            if (key.Length == 0 || value.Length == 0) continue;

            result._map[key] = value;   // 同名键后值覆盖前值
        }

        return result;
    }

    public bool TryGet(string normalizedKey, out string icon)
    {
        if (_map.TryGetValue(normalizedKey, out var v) && v.Length > 0)
        {
            icon = v;
            return true;
        }
        icon = "";
        return false;
    }

    /// <summary>把「键=值」列表序列化回持久化文本（去掉键的重复、保持给定顺序）。</summary>
    public static string Serialize(IEnumerable<(string Key, string Icon)> pairs)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sb = new StringBuilder();

        foreach (var (key, icon) in pairs)
        {
            var k = key.Trim();
            var v = icon.Trim();
            if (k.Length == 0 || v.Length == 0) continue;
            if (!seen.Add(k)) continue;
            if (sb.Length > 0) sb.Append('\n');
            sb.Append(k).Append('=').Append(v);
        }

        return sb.ToString();
    }
}
