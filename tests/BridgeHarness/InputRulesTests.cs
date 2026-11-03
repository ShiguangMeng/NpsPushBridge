using NpsPushBridge;

namespace BridgeHarness;

/// <summary>
/// 用户输入项的校验规则验证（<see cref="InputRules"/>）。
///
/// ★ 这一节守的是什么 ★
/// 三个输入位置（白名单 / 端口 / 图标映射）最终都交给**宽容的运行时解析器**：
///   · <see cref="IpWhitelist.Parse"/> —— 认不出的行直接丢掉；
///   · <see cref="IconOverrides.Parse"/> —— 键或值为空的行直接丢掉。
/// 于是「界面看着填好了、保存也提示成功、规则却从未生效」是**完全可能发生**的，
/// 而且用户没有任何线索。所以这里分两层钉住：
///
///   ① **规则本身**：哪些写法判为 Ok / Warn / Error，归一化后是什么；
///   ② **★ 拦截的必要性 ★**：拿真实解析器跑一遍被拦下的输入，证明它**确实会被丢掉**
///      —— 否则「拦下来」只是我们的臆测。同时验证合法输入经「界面 → 序列化 → 解析」
///      之后**真的生效**。
/// </summary>
internal static class InputRulesTests
{
    private static int _passed, _failed;

    public static (int Passed, int Failed) Run()
    {
        Console.WriteLine("【输入项校验规则（端口 / 白名单 / 图标映射）】");

        RunPortSection();
        RunWhitelistSection();
        RunIconSection();
        RunSaveEffectSection();

        return (_passed, _failed);
    }

    // ====================================================================
    // ① 端口
    // ====================================================================

    private static void RunPortSection()
    {
        Console.WriteLine("  -- 端口 --");

        Check("端口：1403 → Ok",
            InputRules.CheckPort("1403").Level == InputRules.Level.Ok);
        Check("端口：首尾空白被容忍（复制常带空格）",
            InputRules.CheckPort("  1403 ").Level == InputRules.Level.Ok
            && InputRules.CheckPort("  1403 ").Normalized == "1403",
            InputRules.CheckPort("  1403 ").Normalized);
        Check("端口：空 → Error 且给出规则文案",
            InputRules.CheckPort("").BlocksSave && InputRules.CheckPort("").Message.Contains("1-65535"),
            InputRules.CheckPort("").Message);
        Check("端口：非数字 → Error",
            InputRules.CheckPort("abc").BlocksSave);
        Check("端口：0 与 65536 越界 → Error",
            InputRules.CheckPort("0").BlocksSave && InputRules.CheckPort("65536").BlocksSave);
        Check("端口：边界 1 → 不报错（属保留端口，Warn 但仍可保存）、65535 → Ok",
            !InputRules.CheckPort("1").BlocksSave
            && InputRules.CheckPort("65535").Level == InputRules.Level.Ok);
        Check("端口：80 属保留端口 → Warn 但**不拦**（可能真要用）",
            InputRules.CheckPort("80").Level == InputRules.Level.Warn
            && !InputRules.CheckPort("80").BlocksSave,
            InputRules.CheckPort("80").Message);
        Check("端口：1024 及以上不再 Warn",
            InputRules.CheckPort("1024").Level == InputRules.Level.Ok);
        Check("端口：TryResolvePort 对 Error 返回 false、对 Warn 返回真值",
            !InputRules.TryResolvePort("70000", out _)
            && InputRules.TryResolvePort("80", out int p80) && p80 == 80);
        Check("端口：Verdict.None 的 HasMessage 不 NRE（default 的 Message 是 null）",
            !InputRules.Verdict.None.HasMessage && !default(InputRules.Verdict).HasMessage);
    }

    // ====================================================================
    // ② 白名单（每条一个框）
    // ====================================================================

    private static void RunWhitelistSection()
    {
        Console.WriteLine("  -- 白名单 --");

        var exact = InputRules.CheckWhitelistEntry("192.168.1.23");
        Check("白名单：精确 IPv4 → Ok",
            exact.Level == InputRules.Level.Ok && exact.Normalized == "192.168.1.23");

        Check("白名单：IPv6 精确地址 → Ok（运行时也支持）",
            InputRules.CheckWhitelistEntry("fe80::1").Level == InputRules.Level.Ok);

        var cidr = InputRules.CheckWhitelistEntry("192.168.1.0/24");
        Check("白名单：CIDR → Ok",
            cidr.Level == InputRules.Level.Ok && cidr.Normalized == "192.168.1.0/24", cidr.Normalized);
        Check("白名单：CIDR 位数越界（/33）→ Error",
            InputRules.CheckWhitelistEntry("192.168.1.0/33").BlocksSave);
        Check("白名单：CIDR 的地址部分不合法 → Error",
            InputRules.CheckWhitelistEntry("999.1.1.0/24").BlocksSave);
        Check("白名单：CIDR 只支持 IPv4（IPv6 网段 → Error 并说明）",
            InputRules.CheckWhitelistEntry("fe80::/64").BlocksSave);

        Check("白名单：通配 192.168.1.* → Ok",
            InputRules.CheckWhitelistEntry("192.168.1.*").Level == InputRules.Level.Ok);

        // ★ 这两条是本节的重点：旧实现里它们都会「静默失效」
        var badPrefix = InputRules.CheckWhitelistEntry("abc*");
        Check("★ 白名单：通配前缀不是 IP（abc*）→ Error（运行时 StartsWith 永远匹配不到）",
            badPrefix.BlocksSave, badPrefix.Message);

        var loosePrefix = InputRules.CheckWhitelistEntry("192.168.1*");
        Check("★ 白名单：通配前缀没以「.」结尾 → Warn（会连 192.168.10.x 一起放行）",
            loosePrefix.Level == InputRules.Level.Warn && !loosePrefix.BlocksSave,
            loosePrefix.Message);
        Check("★ 白名单：Warn 不改写用户输入（保存的是他自己写的那串）",
            loosePrefix.Normalized == "192.168.1*", loosePrefix.Normalized);

        Check("白名单：通配符后面还有内容 → Error",
            InputRules.CheckWhitelistEntry("192.168.1.*x").BlocksSave);
        Check("白名单：只有通配符（*）→ Error",
            InputRules.CheckWhitelistEntry("*").BlocksSave);

        Check("★ 白名单：192.168.1.256 超范围 → Error（旧版会被静默丢弃）",
            InputRules.CheckWhitelistEntry("192.168.1.256").BlocksSave,
            InputRules.CheckWhitelistEntry("192.168.1.256").Message);
        Check("白名单：随手一段文字 → Error",
            InputRules.CheckWhitelistEntry("我家的电脑").BlocksSave);

        Check("白名单：空行 → Ignored（不报错、不写进配置）",
            InputRules.CheckWhitelistEntry("   ").Level == InputRules.Level.Ignored
            && !InputRules.CheckWhitelistEntry("   ").IsStorable);
        Check("白名单：# 注释 → Ignored（运行时也跳过）",
            InputRules.CheckWhitelistEntry("# 客厅手机").Level == InputRules.Level.Ignored);

        Check("白名单：超长（>120）→ Error",
            InputRules.CheckWhitelistEntry(new string('1', 130)).BlocksSave);

        Check("白名单：IpWhitelist 的三种写法判据与我们一致",
            IpWhitelist.Parse("192.168.1.23\n192.168.1.*\n192.168.1.0/24").RuleCount == 3);
    }

    // ====================================================================
    // ③ 图标映射（应用名 + 图标值）
    // ====================================================================

    private static void RunIconSection()
    {
        Console.WriteLine("  -- 图标映射 --");

        // split / compose 往返 —— 两个输入框靠它俩与持久化文本互相转换
        Check("拆分：微信=qq → (微信, qq)",
            InputRules.SplitIconRow("微信=qq") == ("微信", "qq"));
        Check("拆分：没有等号时整行算应用名（用户正打到一半）",
            InputRules.SplitIconRow("微信") == ("微信", ""));
        Check("拆分：以第一个等号为界（值里可以有 =）",
            InputRules.SplitIconRow("icon=data:image/png;base64,AA==") == ("icon", "data:image/png;base64,AA=="));
        Check("合成：值未填时不补等号（补了会被运行时空值丢弃）",
            InputRules.ComposeIconRow("微信", "") == "微信");
        Check("合成：两框都有值 → 应用名=图标值",
            InputRules.ComposeIconRow("微信", "qq") == "微信=qq");
        Check("合成 → 拆分 往返一致",
            InputRules.SplitIconRow(InputRules.ComposeIconRow("钉钉", "https://a/b.png")) == ("钉钉", "https://a/b.png"));

        var key = InputRules.CheckIconKey("微信");
        Check("应用名：普通名字 → Ok", key.Level == InputRules.Level.Ok && key.Normalized == "微信");
        var noisyKey = InputRules.CheckIconKey("  微信(3条新消息) ");
        Check("★ 应用名：会走 Normalize（去空格与计数尾巴），并把结果告诉用户",
            noisyKey.Level == InputRules.Level.Ok && noisyKey.Normalized == "微信"
            && noisyKey.Message.Contains("微信"),
            $"{noisyKey.Normalized} / {noisyKey.Message}");
        Check("应用名：空 → Ignored", InputRules.CheckIconKey("  ").Level == InputRules.Level.Ignored);
        Check("应用名：与内置一致时说明「会覆盖内置」",
            InputRules.CheckIconKey("qq").HasMessage);

        Check("图标值：内置别名 qq → Ok 且说明是别名",
            InputRules.CheckIconValue("qq") is { Level: InputRules.Level.Ok } v1 && v1.Message.Contains("别名"),
            InputRules.CheckIconValue("qq").Message);
        Check("图标值：图片链接 → Ok",
            InputRules.CheckIconValue("https://example.com/a.png").Level == InputRules.Level.Ok);
        Check("图标值：base64 → Ok",
            InputRules.CheckIconValue("data:image/png;base64,AAAA").Level == InputRules.Level.Ok);
        Check("图标值：base64 写法缺 base64, → Error",
            InputRules.CheckIconValue("data:image/png,AAAA").BlocksSave);
        Check("图标值：本地路径（正斜杠 + 盘符）→ Ok",
            InputRules.CheckIconValue("C:/pics/qq.png").Level == InputRules.Level.Ok);

        var backslash = InputRules.CheckIconValue(@"C:\pics\qq.png");
        Check("★ 图标值：反斜杠路径 → Error（宿主会把 \\ 当转义符，必然找不到图）",
            backslash.BlocksSave, backslash.Message);

        var unknown = InputRules.CheckIconValue("某个没收录的别名");
        Check("★ 图标值：未收录的别名 → Warn（允许保存，但说明会回退预设图标）",
            unknown.Level == InputRules.Level.Warn && !unknown.BlocksSave, unknown.Message);

        // 整条校验
        Check("整条：两框都空 → Ignored",
            InputRules.CheckIconRow("").Level == InputRules.Level.Ignored);
        var half = InputRules.CheckIconRow("钉钉");
        Check("★ 整条：只填了应用名 → Error 并指明缺哪一半（旧版会静默丢弃）",
            half.BlocksSave && half.Message.Contains("图标值"), half.Message);
        Check("整条：只有等号（缺应用名）→ Error",
            InputRules.CheckIconRow("=qq").BlocksSave);
        var row = InputRules.CheckIconRow("钉钉=https://a/b.png");
        Check("整条：完整 → Ok 且非阻塞",
            row.Level == InputRules.Level.Ok && !row.BlocksSave);
        Check("整条：# 注释行 → Ignored（保存时原样保留、不该报错）",
            InputRules.CheckIconRow("# 这是我的备注").Level == InputRules.Level.Ignored);
    }

    // ====================================================================
    // ④ ★ 保存 → 解析 → 真的生效（本节最有价值的部分）★
    // ====================================================================

    private static void RunSaveEffectSection()
    {
        Console.WriteLine("  -- 保存后是否真的生效（用真实解析器验证）--");

        // ── 反证：被我们拦下的输入，运行时确实会丢掉 ──
        //    如果这些断言不成立，说明「拦截」是多余的（或者拦错了方向）。
        Check("★ 反证：IpWhitelist.Parse 确实会丢掉 192.168.1.256（所以必须拦）",
            IpWhitelist.Parse("192.168.1.256").RuleCount == 0);
        // abc* 比「被丢掉」更隐蔽：它会被**记成一条规则**（界面上显得已配置），
        // 但因为用的是 StartsWith("abc")，任何 IP 都不会命中 —— 配了等于没配。
        Check("★ 反证：IpWhitelist.Parse 把 abc* 记成 1 条规则、却永远匹配不到任何地址（所以必须拦）",
            IpWhitelist.Parse("abc*").RuleCount == 1
            && !IpWhitelist.Parse("abc*").IsAllowed(System.Net.IPAddress.Parse("192.168.1.5")));
        Check("★ 反证：IconOverrides.Parse 确实会丢掉「只有应用名」的行（所以必须拦）",
            IconOverrides.Parse("钉钉").Count == 0);
        Check("★ 反证：IconOverrides.Parse 确实会丢掉「值为空」的行（所以必须拦）",
            IconOverrides.Parse("微信=").Count == 0);

        // ── 正向：界面输入 → 拼持久化文本 → 真实解析器 → 生效 ──
        //    模拟「用户在白名单里填了三条、点保存」这一步：
        //    界面做的事就是把每条 Normalized 用 \n 连起来交给 SaveConfig。
        string[] typed = { "192.168.1.23", "192.168.1.*", "10.0.0.0/24", "", "# 备注" };
        var persisted = string.Join("\n", typed
            .Select(InputRules.CheckWhitelistEntry)
            .Where(v => v.IsStorable)
            .Select(v => v.Normalized));

        var whitelist = IpWhitelist.Parse(persisted);
        Check("★ 生效：三条有效输入全部落地（空行与注释不写进配置）",
            whitelist.RuleCount == 3, $"持久化文本 = {persisted.Replace("\n", " | ")}");

        // ⚠️ 精确地址与通配必须在**各自独立的**规则集里验语义：
        //    上面那份同时含 192.168.1.23 与 192.168.1.*，后者会把 192.168.1.99 也放行，
        //    拿它断言「拒绝别的地址」是自相矛盾的（这条断言第一版就写错了，被测试当场纠正）。
        var exactOnly = IpWhitelist.Parse("192.168.1.23");
        Check("★ 生效：只写精确地址时，放行自己、拒绝同网段的别的地址",
            exactOnly.IsAllowed(System.Net.IPAddress.Parse("192.168.1.23"))
            && !exactOnly.IsAllowed(System.Net.IPAddress.Parse("192.168.1.99")));
        Check("★ 生效：通配 192.168.1.* 放行同网段",
            whitelist.IsAllowed(System.Net.IPAddress.Parse("192.168.1.200")));
        Check("★ 生效：CIDR 10.0.0.0/24 放行 10.0.0.7",
            whitelist.IsAllowed(System.Net.IPAddress.Parse("10.0.0.7")));

        // 模拟图标映射页：两个框 → ComposeIconRow → 拼文本 → IconOverrides.Parse
        (string Key, string Value)[] rows =
        {
            ("钉钉", "https://example.com/dingtalk.png"),
            ("微信", "qq"),
            ("  知乎  ", "C:/pics/zhihu.png"),
            ("", ""),                       // 空行：跳过
        };
        var iconText = string.Join("\n", rows
            .Select(r => InputRules.CheckIconRow(InputRules.ComposeIconRow(r.Key, r.Value)))
            .Where(v => v.IsStorable)
            .Select(v => v.Normalized));

        var overrides = IconOverrides.Parse(iconText);
        Check("★ 生效：三条映射全部解析出来（空行跳过）",
            overrides.Count == 3, $"持久化文本 = {iconText.Replace("\n", " | ")}");
        Check("★ 生效：键按 Normalize 规整后能查到（「  知乎  」→「知乎」）",
            overrides.TryGet("知乎", out var zhihu) && zhihu == "C:/pics/zhihu.png", zhihu);
        Check("★ 生效：网络链接与别名都原样保留",
            overrides.TryGet("钉钉", out var ding) && ding == "https://example.com/dingtalk.png"
            && overrides.TryGet("微信", out var wx) && wx == "qq");

        // 端到端：自定义映射真的会盖过内置判定
        var decision = IconCatalog.Decide("钉钉", null, overrides);
        Check("★ 生效：自定义映射参与最终图标判定（Source = 自定义）",
            decision.Source.Contains("自定义"), $"{decision.Source} / {decision.Icon}");

        // 端到端：白名单为空 = 放行全部（界面上「全部留空」的含义）
        Check("★ 生效：全部留空 → 放行所有来源（与界面提示一致）",
            IpWhitelist.Parse("").IsEmpty && IpWhitelist.Parse("").IsAllowed(System.Net.IPAddress.Parse("1.2.3.4")));
    }

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
