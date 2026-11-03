using System.Text;
using NpsPushBridge;

namespace BridgeHarness;

/// <summary>
/// <see cref="AppBlacklist"/>（应用黑名单）与 <see cref="InputRules.CheckBlacklistEntry"/> 的验证。
///
/// ★ 为什么这几条值得钉死 ★
/// 黑名单的失败是**静默**的：用户以为拦住了，实际消息照旧上岛（或反之，本想只拦一个应用
/// 却把一整类都拦掉了）。两种错都不报错、不留痕，只能靠断言把语义钉死：
///   · 精确匹配**不做子串**（`bilibili` 不该命中 `bili`）；
///   · 通配只做**前缀**且**只认末尾 `*`**（`com.tencent.*` 要拦得住 `com.tencent.mm`，
///     但拦不住 `com.tencentx.mm`）；
///   · 归一化与图标映射**同一套规则**（「微信(3条新消息)」要能命中「微信」）；
///   · 空名单**什么都不拦**（防止「默认全拦」这种灾难性倒退）。
/// </summary>
internal static class AppBlacklistTests
{
    private static int _passed, _failed;

    private static void Check(string name, bool ok, string detail = "")
    {
        if (ok) { _passed++; Console.WriteLine($"  [PASS] {name}"); }
        else { _failed++; Console.WriteLine($"  [FAIL] {name}{(detail.Length > 0 ? "  —— " + detail : "")}"); }
    }

    public static (int Passed, int Failed) Run()
    {
        Console.WriteLine("【应用黑名单】");

        EmptySection();
        ExactSection();
        NormalizeSection();
        WildcardSection();
        PackageSection();
        PayloadSection();
        DescribeSection();
        ValidateSection();

        return (_passed, _failed);
    }

    // ── ★ 从真实手机 payload 到拦截判定的完整链路 ★ ──────────────────────
    //
    // 这一节回答的是「为什么用户说『其他应用也不上岛』」：
    // 把手机端**实际发出的那段表单**喂进解析器，再拿解析结果去问黑名单 ——
    // 只要「微信」被放行、只有「QQ」被拦，就说明误杀不可能来自判定逻辑。
    // 这里的每一步都是真实的（同一个 TryParse、同一个 AppBlacklist），不构造假数据。

    private static void PayloadSection()
    {
        // ① 微信（非黑名单）：解析 + 判定必须放行
        string wechatForm = "from=com.tencent.mm&content=" + Uri.EscapeDataString(
            """{"title":"张三","subtitle":"在吗","kind":"微信"}""");
        bool wechatOk = NpsActivityEndpoint.TryParse(
            Encoding.UTF8.GetBytes(wechatForm), out var wechat, out string wechatErr);

        Check("★ 链路：手机表单能解析成 activity", wechatOk, wechatErr);
        Check("★ 链路：Kind=微信 且 Package=com.tencent.mm（两个名字都留住了）",
            wechat.Kind == "微信" && wechat.Package == "com.tencent.mm",
            $"Kind={wechat.Kind ?? "<null>"} Package={wechat.Package ?? "<null>"}");
        Check("★★ 链路：黑名单只有 QQ 时，**微信必须放行**（「其他应用被误杀」的核心反证）",
            !AppBlacklist.Parse("QQ").IsBlocked(wechat.Package, wechat.Kind),
            $"Kind={wechat.Kind ?? "<null>"} Package={wechat.Package ?? "<null>"}");

        // ② QQ（黑名单）：必须被拦
        string qqForm = "from=com.tencent.mobileqq&content=" + Uri.EscapeDataString(
            """{"title":"群消息","subtitle":"你好","kind":"QQ"}""");
        NpsActivityEndpoint.TryParse(Encoding.UTF8.GetBytes(qqForm), out var qq, out _);
        Check("★ 链路：黑名单 QQ → QQ 必须被拦",
            AppBlacklist.Parse("QQ").IsBlocked(qq.Package, qq.Kind),
            $"Kind={qq.Kind ?? "<null>"} Package={qq.Package ?? "<null>"}");

        // ③ content 里没配 kind：Kind 由包名翻译而来，照样要拦
        string bareForm = "from=com.tencent.mobileqq&content=" + Uri.EscapeDataString("你好");
        NpsActivityEndpoint.TryParse(Encoding.UTF8.GetBytes(bareForm), out var bare, out _);
        Check("★ 链路：content 没有 kind 时由包名翻译成「QQ」，照样被拦",
            AppBlacklist.Parse("QQ").IsBlocked(bare.Package, bare.Kind),
            $"Kind={bare.Kind ?? "<null>"} Package={bare.Package ?? "<null>"}");

        // ④ 短信（另一个常见应用）：不能被 QQ 这条规则牵连
        string smsForm = "from=com.android.mms&content=" + Uri.EscapeDataString(
            """{"title":"验证码","subtitle":"8842","kind":"短信"}""");
        NpsActivityEndpoint.TryParse(Encoding.UTF8.GetBytes(smsForm), out var sms, out _);
        Check("★ 链路：短信不被 QQ 规则牵连（放行）",
            !AppBlacklist.Parse("QQ").IsBlocked(sms.Package, sms.Kind),
            $"Kind={sms.Kind ?? "<null>"} Package={sms.Package ?? "<null>"}");
    }

    // ── 空名单 ───────────────────────────────────────────────────────────

    private static void EmptySection()
    {
        foreach (var raw in new[] { null, "", "   ", "\n\n", "# 只是注释" })
        {
            var b = AppBlacklist.Parse(raw);
            Check($"空名单（{raw ?? "null"}）→ IsEmpty 且不拦任何东西",
                b.IsEmpty && b.RuleCount == 0
                && !b.IsBlocked("com.tencent.mm", "微信")
                && !b.IsBlocked("", ""));
        }

        Check("空名单的 Describe 明说「未配置」（而不是显示 0 条规则，那会被读成「配了但没生效」）",
            AppBlacklist.Parse("").Describe().Contains("未配置"));
    }

    // ── 精确匹配 ─────────────────────────────────────────────────────────

    private static void ExactSection()
    {
        var b = AppBlacklist.Parse("微信\ncom.tencent.mobileqq");

        Check("精确：kind 命中 → 拦截", b.IsBlocked("", "微信"));
        Check("精确：package 命中 → 拦截", b.IsBlocked("com.tencent.mobileqq", "QQ"));
        Check("精确：两者都不命中 → 放行", !b.IsBlocked("com.sina.weibo", "微博"));
        Check("精确：两个名字任一命中即拦（kind 中、package 不中）",
            b.IsBlocked("com.unknown", "微信"));

        // ★ 反证：精确匹配**不是子串匹配** ★
        // 「bilibili」不该把「bili」也拦下来 —— 否则「配了一个却顺带拦住别的」且毫无提示。
        var exact = AppBlacklist.Parse("bilibili");
        Check("★ 反证：精确匹配不做子串（bilibili 不命中 bili）",
            !exact.IsBlocked("bili", "bili") && exact.IsBlocked("bilibili", "bilibili"));

        Check("大小写不敏感：条目 WeChat 命中 kind「wechat」",
            AppBlacklist.Parse("WeChat").IsBlocked("", "wechat"));

        Check("空行与 # 注释行被忽略",
            AppBlacklist.Parse("\n# 注释\n微信\n\n").RuleCount == 1);
    }

    // ── 归一化 ───────────────────────────────────────────────────────────

    private static void NormalizeSection()
    {
        var b = AppBlacklist.Parse("微信");

        Check("★ 归一化与图标映射同源：条目「微信」命中「微信(3条新消息)」",
            b.IsBlocked("", "微信(3条新消息)"));
        Check("归一化：条目「微信」命中带空白的「微信 」", b.IsBlocked("", "微信 "));
        Check("归一化：条目「微信」命中带引号的「\"微信\"」", b.IsBlocked("", "\"微信\""));

        var boxed = AppBlacklist.Parse("微信(3条新消息)");
        Check("反向也成立：条目带计数尾巴，仍命中干净的「微信」", boxed.IsBlocked("", "微信"));
    }

    // ── 通配 ─────────────────────────────────────────────────────────────

    private static void WildcardSection()
    {
        var b = AppBlacklist.Parse("com.tencent.*");

        Check("通配：com.tencent.* 拦住 com.tencent.mm", b.IsBlocked("com.tencent.mm", ""));
        Check("通配：com.tencent.* 拦住 com.tencent.mobileqq", b.IsBlocked("com.tencent.mobileqq", ""));
        Check("★ 反证：通配 com.tencent.* 拦不住 com.tencentx.mm（前缀含点，不会误伤同前缀段）",
            !b.IsBlocked("com.tencentx.mm", ""));

        Check("通配：前缀本身也命中（com.tencent. 是 com.tencent.xx 的前缀）",
            !b.IsBlocked("com.tencent", ""), "com.tencent 不含末尾点，不该命中");
        Check("通配：条目 com.tencent* 命中 com.tencent（不带点）",
            AppBlacklist.Parse("com.tencent*").IsBlocked("com.tencent", ""));

        var cn = AppBlacklist.Parse("微信*");
        Check("通配中文：微信* 命中「微信」与「微信支付」",
            cn.IsBlocked("", "微信") && cn.IsBlocked("", "微信支付"));
        Check("通配中文：微信* 拦不住「QQ」", !cn.IsBlocked("", "QQ"));

        var mixed = AppBlacklist.Parse("微信\ncom.tencent.*");
        Check("精确与通配共存，各自的命中互不影响",
            mixed.IsBlocked("", "微信") && mixed.IsBlocked("com.tencent.mm", "") && !mixed.IsBlocked("QQ", "QQ"));
    }

    // ── 包名参与匹配 ─────────────────────────────────────────────────────

    private static void PackageSection()
    {
        var b = AppBlacklist.Parse("com.smile.gifmaker");

        Check("★ 按包名拦截：条目是包名，kind 是中文显示名「快手」",
            b.IsBlocked("com.smile.gifmaker", "快手"));
        Check("包名为空时只按 kind 匹配，不会因为缺包名而误判",
            !AppBlacklist.Parse("快手").IsBlocked("", "微信"));
    }

    // ── 摘要 ─────────────────────────────────────────────────────────────

    private static void DescribeSection()
    {
        var exact = AppBlacklist.Parse("微信\nQQ");
        Check("RuleCount 计精确条数", exact.RuleCount == 2);
        Check("无通配时 Describe 不出现「通配」", !exact.Describe().Contains("通配"));

        var wild = AppBlacklist.Parse("微信\ncom.tencent.*");
        Check("RuleCount = 精确 + 通配", wild.RuleCount == 2);
        Check("WildcardCount 单独可读", wild.WildcardCount == 1);
        Check("含通配时 Describe 会点出条数", wild.Describe().Contains("通配"));
    }

    // ── 输入校验 ─────────────────────────────────────────────────────────

    private static void ValidateSection()
    {
        Check("校验：空 / 注释 → Ignored（不算错）",
            InputRules.CheckBlacklistEntry("").Level == InputRules.Level.Ignored
            && InputRules.CheckBlacklistEntry("   ").Level == InputRules.Level.Ignored
            && InputRules.CheckBlacklistEntry("# 注释").Level == InputRules.Level.Ignored);

        Check("校验：超长 → Error",
            InputRules.CheckBlacklistEntry(new string('a', InputRules.BlacklistMaxLength + 1)).BlocksSave);

        var mid = InputRules.CheckBlacklistEntry("com.*.mm");
        Check("★ 校验：中间通配 → Error（会被读成「com. 前缀」，拦下远超预期的东西）",
            mid.BlocksSave && mid.Message.Contains("末尾"));

        Check("★ 校验：只有 * → Error（前缀为空）",
            InputRules.CheckBlacklistEntry("*").BlocksSave);

        var ok = InputRules.CheckBlacklistEntry("com.tencent.*");
        Check("校验：com.tencent.* → Ok 且归一化回同一写法",
            ok.Level == InputRules.Level.Ok && ok.Normalized == "com.tencent.*");
        Check("校验：通配的说明里写出前缀（用户能核对到底会拦什么）",
            ok.Message.Contains("com.tencent."));

        var normalized = InputRules.CheckBlacklistEntry("微信(3条新消息)");
        Check("★ 校验：归一化结果与输入不同时给出提示（否则用户以为「必须一模一样才生效」）",
            normalized.Level == InputRules.Level.Ok
            && normalized.Normalized == "微信"
            && normalized.Message.Contains("将按「微信」匹配"));

        Check("校验：首尾空白在校验前已修掉，不算「归一化差异」（与图标映射的校验一致）",
            InputRules.CheckBlacklistEntry("微信 ") is { Level: InputRules.Level.Ok, Message: "" });

        Check("校验：普通条目原样通过，没有多余提示",
            InputRules.CheckBlacklistEntry("微信") is { Level: InputRules.Level.Ok, Message: "" });
    }
}
