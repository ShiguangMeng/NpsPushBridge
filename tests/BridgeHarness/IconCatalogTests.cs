using NpsPushBridge;

namespace BridgeHarness;

/// <summary>
/// 按 kind 二次补全 icon 的验证（纯逻辑，不联网、不依赖宿主）。
///
/// ★ 本组用例守住的三条核心语义 ★
///   ① **入站 icon 优先级最高**：发送端明确给了图标就必须原样透传，
///      插件绝不能用自己的猜测覆盖 —— 否则等于擅自篡改用户数据。
///   ② **拿不到映射就一个字节都不多写**：<see cref="IconCatalog.BuildJson"/> 在
///      icon 为空时**不输出 icon 字段**，请求体与升级前逐字节一致。
///      这是「保持与现有接口兼容」的可测形式（旧宿主只认 kind/title/subtitle）。
///   ③ **别名优先于链接**：别名是宿主读本地文件（零网络、零超时），链接要联网且
///      宿主只给 3 秒超时；顺序反了会让本可稳的图标变成「偶尔不出现」。
/// </summary>
internal static class IconCatalogTests
{
    public static (int Passed, int Failed) Run()
    {
        Console.WriteLine();
        Console.WriteLine("【图标映射与补全】");

        // ★★ 必须在**任何别名探测之前**把输出目录下的 data/image 清空 ★★
        // 本组用例会读「宿主目录下有没有 data/image/<别名>-icon|logo.*」来分支，
        // 而文件末尾的释放用例（第 10 节）会真的往那儿写文件。若不在这里先归零，
        // 上一次运行留下的**部分文件**会制造出「某些别名可用、某些不可用」的中间状态：
        //   · 第 2 节的 `aliasUsable`（按 "qq" 探）可能为 false → 走进「别名不可用」分支，
        //     可 bilibili / windows 的图恰好还在 → 那几条断言拿到 `别名` 而非 `别名失效` → FAIL；
        //   · 而且 FAIL 与否取决于上一轮跑成什么样 —— 结果随运行次数漂移，极难定位。
        // 归零后状态唯一：「目录不存在 → 所有别名一律不可用」，所有分支都确定。
        int staleFiles = CleanDataImageDir(Path.Combine(AppContext.BaseDirectory, "data", "image"));
        if (staleFiles > 0)
            Console.WriteLine($"  [INFO] 启动清理：删除了上一轮残留的 {staleFiles} 个 data/image 文件");
        IconCatalog.ResetAliasProbeCache();

        int passed = 0, failed = 0;

        void Check(string name, bool condition, string detail = "")
        {
            if (condition)
            {
                passed++;
                Console.WriteLine($"  [PASS] {name}");
            }
            else
            {
                failed++;
                Console.WriteLine($"  [FAIL] {name}{(detail.Length == 0 ? "" : $"  →  {detail}")}");
            }
        }

        // ── 1. 入站 icon 一律原样保留（最高优先级）──────────────────────
        var inbound = IconCatalog.Decide("微信", "https://example.com/inbound.png", null);
        Check("入站已带 icon → 原样保留，不被内置映射覆盖",
            inbound.Icon == "https://example.com/inbound.png" && inbound.Source == "入站",
            $"{inbound.Source} / {inbound.Icon}");
        Check("入站 icon 带首尾空白 → 去掉空白后保留",
            IconCatalog.Decide("微信", "  data:image/png;base64,AAAA  ", null).Icon
                == "data:image/png;base64,AAAA");
        Check("入站 icon 优先级高于自定义映射",
            IconCatalog.Decide("微信", "inbound", IconOverrides.Parse("微信=自定义值")).Icon == "inbound");

        // ── 2. 内置别名（宿主自带图，零网络成本）────────────────────────
        // ★★ 别名是否可用取决于「宿主 exe 目录下有没有 data/image/<别名>-icon|logo.*」★★
        // 验证工程**刻意不产出** data/image（见 IconCatalogTests 末尾的释放用例：它进来先清空、
        // 出去再清空），因此这里探到的应是「不可用」→ 断言「应当降级」。
        // 这正是用户机器上「QQ 图标不显示」的成因（宿主是单文件发布版，不带 data/image）。
        //
        // ⚠️ 这里必须以 `aliasUsable` 为准做分支，**不能**假设它一定是某个值：
        //   曾因为释放用例把文件留在输出目录，导致出现「部分别名可用」的中间状态
        //   （qq-icon.png 被删、bilibili-logo.png 还在）→ 分支选了一边、另一边的断言却按
        //   相反的前提执行 → 结果随运行次数漂移。真正的修法是让释放用例自己收尾清空
        //   （见文件末尾 RunSeederSection），这里只需如实按探测结果分支。
        bool aliasUsable = IconCatalog.AliasFileExists("qq");
        Console.WriteLine($"  [INFO] 本机别名探测：data/image 可用 = {aliasUsable}（测试工程应为 False）");
        Console.WriteLine($"  [INFO] {IconCatalog.DescribeAliasProbe()}");

        if (aliasUsable)
        {
            Check("别名可用时：QQ → 别名 qq", IconCatalog.Decide("qq", "", null) is { Icon: "qq", Source: "别名" });
            Check("别名可用时：包名 com.tencent.mobileqq → 别名 qq",
                IconCatalog.Decide("com.tencent.mobileqq", "", null).Icon == "qq");
            Check("别名可用时：包名 tv.danmaku.bili → 别名 bilibili",
                IconCatalog.Decide("tv.danmaku.bili", "", null).Icon == "bilibili");
            Check("别名可用时：Chrome → 别名 chrome",
                IconCatalog.Decide("Chrome", "", null).Icon == "chrome");
            Check("别名可用时：默认别名 windows 可命中",
                IconCatalog.Decide("wintoast", "", null).Icon == "windows");
            Check("★ 别名优先于链接（qq 走别名而非链接）",
                IconCatalog.Decide("qq", "", null).Source == "别名");
        }
        else
        {
            // ★★ 核心回归：别名文件不存在时，**必须**改用图片链接兜底，而不是下发一个死的别名 ★★
            Check("★ 别名不可用时：QQ 降级为图片链接（不再下发死的别名 qq）",
                IconCatalog.Decide("qq", "", null) is { Source: "链接" }
                && IconCatalog.Decide("qq", "", null).Icon.StartsWith("https://mdn.alipayobjects.com/"),
                $"{IconCatalog.Decide("qq", "", null).Source} / {IconCatalog.Decide("qq", "", null).Icon}");
            Check("★ 别名不可用时：包名 com.tencent.mobileqq 同样降级为链接",
                IconCatalog.Decide("com.tencent.mobileqq", "", null).Source == "链接");

            // ★ 别名不可用 = 宿主没带 data/image 目录（单文件发布版）。
            //   此时能否兜底**完全取决于该应用有没有内置图片链接**，于是应用被分成两类：
            //     · 有链接的（qq/微信/快手/抖音/网易云）→ 降级为「链接」，图标照常显示；
            //     · **无链接的**（bilibili/chrome/edge/potplayer/windows）→ 只能如实标「别名失效」。
            //   早期这里断言 bilibili / Chrome 也「降级为链接」，是把两类混为一谈了 ——
            //   它们本就没有链接兜底，断言必然 FAIL。分两类验，才是对现状的准确刻画。
            Check("★ 别名不可用 + 无链接兜底：tv.danmaku.bili 如实标「别名失效」（不谎报链接）",
                IconCatalog.Decide("tv.danmaku.bili", "", null) is { HasIcon: false, Source: "别名失效" },
                $"{IconCatalog.Decide("tv.danmaku.bili", "", null).Source} / {IconCatalog.Decide("tv.danmaku.bili", "", null).Icon}");
            Check("★ 别名不可用 + 无链接兜底：Chrome 如实标「别名失效」",
                IconCatalog.Decide("Chrome", "", null) is { HasIcon: false, Source: "别名失效" });
            Check("★ 别名不可用 + **有**链接兜底：快手照样降级为链接（对比组）",
                IconCatalog.Decide("快手", "", null).Source == "链接");
            Check("★ 别名不可用时：无链接兜底的别名（windows）如实标为「别名失效」",
                IconCatalog.Decide("wintoast", "", null) is { HasIcon: false, Source: "别名失效" },
                $"{IconCatalog.Decide("wintoast", "", null).Source} / {IconCatalog.Decide("wintoast", "", null).Icon}");
        }

        Check("别名探测：空白别名一律判为不可用", !IconCatalog.AliasFileExists("") && !IconCatalog.AliasFileExists("   "));

        // ── 3. 内置图片链接（别名覆盖不到的应用）───────────────────────
        var wechat = IconCatalog.Decide("微信", "", null);
        Check("微信 → 图片链接（宿主无该别名，只能走链接）",
            wechat is { Source: "链接" } && wechat.Icon.StartsWith("https://mdn.alipayobjects.com/"),
            $"{wechat.Source} / {wechat.Icon}");
        Check("快手 → 图片链接", IconCatalog.Decide("快手", "", null).Source == "链接");
        Check("抖音 → 图片链接", IconCatalog.Decide("douyin", "", null).Source == "链接");
        Check("网易云音乐 → 图片链接", IconCatalog.Decide("netease", "", null).Source == "链接");

        // ── 4. kind 规整化 ────────────────────────────────────────────
        Check("带引号的 kind 可规整：\"微信\" → 命中",
            IconCatalog.Decide("\"微信\"", "", null).Source == "链接");
        Check("带计数尾巴的 kind 可规整：微信(3条新消息) → 命中",
            IconCatalog.Decide("微信(3条新消息)", "", null).Source == "链接");
        Check("全角括号计数尾巴同样可规整：微信（5条）",
            IconCatalog.Decide("微信（5条）", "", null).Source == "链接");
        Check("kind 带首尾空格可规整",
            IconCatalog.Decide("  微信  ", "", null).Source == "链接");

        // ⚠️ 反例：括号里含空格或没有数字时**不能**剥离 —— 否则会误伤「QQ(国际版)」
        Check("方括号计数尾巴可规整：微信[2条]",
            IconCatalog.Decide("微信[2条]", "", null).Source == "链接");
        Check("括号内无数字 → 不剥离（避免误伤真实名字）",
            IconCatalog.Normalize("wechat(client)") == "wechat(client)",
            IconCatalog.Normalize("wechat(client)"));
        Check("括号内有空格 → 不剥离",
            IconCatalog.Normalize("wechat(a b)") == "wechat(a b)",
            IconCatalog.Normalize("wechat(a b)"));

        Check("Normalize 对 null / 空白返回空串",
            IconCatalog.Normalize(null) == "" && IconCatalog.Normalize("   ") == "");
        Check("kind 为空 → 不补图标",
            IconCatalog.Decide("", "", null) is { HasIcon: false, Source: "无" });

        // ── 5. 未收录 / 未命中：都不补图标，但原因措辞必须可区分 ────────
        var zhihu = IconCatalog.Decide("知乎", "", null);
        Check("知乎 → 未收录（明确告知可在界面自行添加）",
            zhihu is { HasIcon: false, Source: "未收录" } && zhihu.Reason.Contains("图标映射"),
            $"{zhihu.Source} / {zhihu.Reason}");
        Check("小红书 → 未收录", IconCatalog.Decide("小红书", "", null).Source == "未收录");
        Check("短信 → 未收录", IconCatalog.Decide("短信", "", null).Source == "未收录");
        Check("完全没听说过的名字 → 未命中（不是「未收录」）",
            IconCatalog.Decide("someunknownapp", "", null) is { HasIcon: false, Source: "未命中" });

        // ── 6. 自定义映射：可覆盖内置，也可补未收录 ─────────────────────
        var custom = IconOverrides.Parse("""
            # 我自己的映射
            微信=my-wechat-alias
            知乎=https://example.com/zhihu.png
            """);

        Check("自定义映射条数正确（跳过注释行）", custom.Count == 2, custom.Count.ToString());
        Check("自定义映射可覆盖内置链接（微信 → 我的别名）",
            IconCatalog.Decide("微信", "", custom) is { Icon: "my-wechat-alias", Source: "自定义" });
        Check("自定义映射可补上「未收录」的应用（知乎）",
            IconCatalog.Decide("知乎", "", custom).Icon == "https://example.com/zhihu.png");
        Check("自定义键同样走 Normalize：微信(2条) 也能命中自定义",
            IconCatalog.Decide("微信(2条)", "", custom).Icon == "my-wechat-alias");

        // ★ 入站 icon 仍然压过自定义（顺序不可反）
        Check("★ 入站 icon > 自定义映射 > 内置",
            IconCatalog.Decide("微信", "inbound", custom).Icon == "inbound");

        // ── 7. IconOverrides 解析的健壮性：「一行坏掉不能让整份配置失效」──
        var messy = IconOverrides.Parse("""
            微信=https://a.example/w.png

            这条没有等号，应被跳过
            =只有等号左边为空，跳过
            只有键没有值=
            知乎=zk
            """);
        Check("坏行被跳过、好行照常生效（微信 + 知乎）", messy.Count == 2, messy.Count.ToString());
        Check("坏行不影响好行取值", messy.TryGet("微信", out var w) && w == "https://a.example/w.png");
        Check("无等号的行确实没进来", !messy.TryGet("这条没有等号，应被跳过", out _));
        Check("Parse(null) 得到空映射且不抛异常", IconOverrides.Parse(null).Count == 0);

        var dup = IconOverrides.Parse("微信=first\n微信=second");
        Check("同名键后值覆盖前值", dup.Count == 1 && dup.TryGet("微信", out var dv) && dv == "second");

        var serialized = IconOverrides.Serialize([("微信", "w1"), ("微信", "w2"), ("知乎", "z1")]);
        Check("Serialize 去重复键且保持顺序", serialized == "微信=w1\n知乎=z1", serialized.Replace("\n", "\\n"));
        Check("Serialize → Parse 往返一致",
            IconOverrides.Parse(serialized).Count == 2);

        // ── 8. ★★ BuildJson 兼容性契约 ★★ ────────────────────────────
        var noIcon = IconCatalog.BuildJson("微信", "标题", "副标题", "");
        Check("无图标时不输出 icon 字段（与升级前逐字节一致）",
            noIcon == "{\"kind\":\"微信\",\"title\":\"标题\",\"subtitle\":\"副标题\"}", noIcon);
        Check("无图标时字段顺序仍是 kind/title/subtitle（旧宿主解析前提）",
            noIcon.StartsWith("{\"kind\":\"") && noIcon.Contains("\"title\":") && noIcon.Contains("\"subtitle\":")
            && !noIcon.Contains("icon"));

        var withIcon = IconCatalog.BuildJson("微信", "标题", "副标题", "qq");
        Check("有图标时在末尾追加 icon 字段",
            withIcon == "{\"kind\":\"微信\",\"title\":\"标题\",\"subtitle\":\"副标题\",\"icon\":\"qq\"}", withIcon);

        Check("icon 内的双引号被转义",
            IconCatalog.BuildJson("k", "t", "s", "a\"b").Contains("\"icon\":\"a\\\"b\""));
        Check("非 ASCII 原样输出（不转 \\u，与既有实现同风格）",
            IconCatalog.BuildJson("微信", "t", "s", "qq").Contains("微信"));

        // ── 9. 展示与统计口径 ─────────────────────────────────────────
        Check("BuiltinCount = 别名条数 + 链接条数（两个表相加，不是拍脑袋的常数）",
            IconCatalog.BuiltinCount > IconCatalog.Aliases.Count,
            $"count={IconCatalog.BuiltinCount} aliases={IconCatalog.Aliases.Count}");
        Check("★ 内置一览的条数用「别名数」（列表只列别名类，数字必须与之一致）",
            IconCatalog.BuiltinAliasCount == IconCatalog.Aliases.Count,
            $"{IconCatalog.BuiltinAliasCount} vs {IconCatalog.Aliases.Count}");

        // ★ 别名集合必须与「宿主真正能解析的别名」一致 ★
        // 宿主的别名规则只有两条（见 NotchPeninsula/Notify/ToastIconProvider.cs）：
        //   ① 命中它硬编码的表（windows / wintoast / default → wintoast-icon.png）；
        //   ② 否则按 <别名>-icon|logo|<空>.<ext> 去 data/image/ 找 —— **文件名前缀必须等于别名**。
        // 所以别名值只能取这两种来源。2026-10-09 删掉了自造的 `bili`
        // （它对应 bilibili-logo.png，前缀对不上 ⇒ 宿主永远找不到 ⇒ 静默回退默认图标）。
        Check("★ 别名集合与宿主可解析的别名一致（不含自造别名，如曾经的 bili）",
            IconCatalog.Aliases.Values.Distinct().OrderBy(v => v).SequenceEqual(
                new[] { "bilibili", "chrome", "edge", "potplayer", "qq", "windows" }.OrderBy(v => v)),
            string.Join(",", IconCatalog.Aliases.Values.Distinct().OrderBy(v => v)));

        // ★ QQ 必须有「别名 + 链接」双保险：别名在单文件发布版宿主上会失效，
        //   没有链接兜底就会出现「QQ 图标不显示」。
        //
        // ⚠️ 这里**不能**断言 LookupBuiltin("qq") 落在链接上：LookupBuiltin 的语义就是
        //   「先别名后链接」（与 Decide 一致），所以只要别名表里配了 qq，它就必然返回 "qq"。
        //   要验的是**两件事都在**：① 别名表里有 → 说明「有别名可用时走本地图」；
        //   ② 链接表里也有 → 说明「别名失效时能兜底」。二者分开断言，才真正守住双保险。
        //   （早期把二者合成一句并写成「builtin 应以 https 开头」，属于把 LookupBuiltin
        //   误当成了 Decide —— 别名存在时那个断言永远不可能成立，必然 FAIL。）
        Check("★ QQ 配了别名（别名可用时走本地图、零网络）",
            IconCatalog.LookupAlias("qq") == "qq",
            $"alias={IconCatalog.LookupAlias("qq")}");
        Check("★ QQ 同时配了链接兜底（别名失效时 QQ 图标仍能显示）",
            IconCatalog.LookupLinkForTest("qq").StartsWith("https://mdn.alipayobjects.com/"),
            $"link={IconCatalog.LookupLinkForTest("qq")}");
        Check("★ QQ 别名优先于链接（LookupBuiltin 与 Decide 同一优先级）",
            IconCatalog.LookupBuiltin("qq") == "qq",
            $"builtin={IconCatalog.LookupBuiltin("qq")}");

        var showcase = IconCatalog.Showcase();
        Check("Showcase 只返回真的能取到 icon 的条目", showcase.All(i => i.Icon.Length > 0));
        Check("★ Showcase 只列**别名类**（不列靠网络链接的应用）",
            showcase.All(i => IconCatalog.Aliases.Values.Contains(i.Icon)),
            string.Join(",", showcase.Select(i => $"{i.Key}={i.Icon}")));
        Check("★ Showcase 与别名表同步（6 个宿主别名一个不漏）",
            showcase.Select(i => i.Icon).Distinct().OrderBy(v => v)
                .SequenceEqual(new[] { "bilibili", "chrome", "edge", "potplayer", "qq", "windows" }.OrderBy(v => v)),
            string.Join(",", showcase.Select(i => $"{i.Key}={i.Icon}")));
        Check("★ Showcase 不再列微信（它靠图片链接，映射仍在、但不上一览）",
            showcase.All(i => i.Key != "wechat"));
        Check("Showcase 的 QQ 项能取到值（不是硬编码字面量）",
            showcase.First(i => i.Key == "qq").Icon.Length > 0);

        // ── 9b. ★★ 别名契约：插件判定「可用」必须 ⇔ 宿主真能取到 ★★
        // 这一节守的是 2026-10-09 修掉的那类隐蔽缺陷：
        // 插件以为某个别名可用 → 把它送给宿主 → 宿主按**同名前缀**找不到文件 → 静默回退默认图标。
        // 现象是「配了却没图标」，而且查不出原因。三条断言把这种「假别名」挡在提交之前。
        {
            // ⚠️ 契约快照，取自 NotchPeninsula/Notify/ToastIconProvider.cs 的 _aliasFiles。
            //    宿主若改了这张表，本断言会红 —— 那时要看宿主为什么变，而不是把期望值改成新的了事。
            string[] hostHardcoded = ["windows", "wintoast", "default"];

            Check("★ 例外表（文件名≠别名）的键集合 = 宿主硬编码表",
                IconCatalog.AliasFileMap.Keys.OrderBy(k => k, StringComparer.Ordinal)
                    .SequenceEqual(hostHardcoded.OrderBy(k => k, StringComparer.Ordinal)),
                string.Join(",", IconCatalog.AliasFileMap.Keys));

            // 例外表里除宿主硬编码那三条，其余条目的文件名必须与别名**同前缀** ——
            // 否则宿主按 <别名>-icon|logo.* 去找时必然落空（这正是当年 bili 的错法）。
            foreach (var (alias, names) in IconCatalog.AliasFileMap)
            {
                if (hostHardcoded.Contains(alias)) continue;

                bool samePrefix = names.All(n =>
                    n.StartsWith(alias + "-icon", StringComparison.OrdinalIgnoreCase)
                    || n.StartsWith(alias + "-logo", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(Path.GetFileNameWithoutExtension(n), alias, StringComparison.OrdinalIgnoreCase));

                Check($"★ 例外表「{alias}」的文件名与别名同前缀（否则宿主找不到）",
                    samePrefix, string.Join(",", names));
            }

            // 别名值必须满足宿主的 IsPlainAlias（长度 1~32、只含字母数字与 -/_）——
            // 不满足的话宿主连别名分支都不会进，直接当成本地路径去查，必然失败。
            foreach (string alias in IconCatalog.Aliases.Values.Distinct().OrderBy(a => a, StringComparer.Ordinal))
            {
                bool plain = alias.Length is > 0 and <= 32
                    && alias.All(c => char.IsLetterOrDigit(c) || c is '-' or '_');
                Check($"★ 别名「{alias}」满足宿主的 IsPlainAlias 约定", plain, alias);
            }
        }

        // ── 10. ★★ 宿主图标释放（HostImageSeeder）★★ ────────────────────
        // 这是「QQ 图标不显示」的**根因修法**：宿主单文件发布版不带 data/image 散文件，
        // 插件把内嵌的官方图补进去，别名机制才真的可用。用例守住四件事：
        //   ① 6 个资源真的打进了 dll（防「打包漏资源」这类构建事故）；
        //   ② 释放后目录里确实有这 6 个文件（真的写盘了，不是空跑）；
        //   ③ 幂等 + 不覆盖（第二次调用不重写；用户换过的图不被夺回）；
        //   ④ 释放后**别名探测结论翻转**（这是「补图」与「别名生效」之间的因果链）。
        //
        // ⚠️ 把本地函数包一层再传：C# 的本地函数带默认参数时**不能**直接当 Action<string,bool,string> 用
        //   （方法组转换要求签名精确匹配，默认参数不参与）。包成 lambda 即可，语义一致。
        RunSeederSection((name, ok, detail) => Check(name, ok, detail));

        // ── 11. ★★ 复制 / 粘贴规则（IconClipboard）★★ ───────────────────
        // 这里守的是**冲突语义**，而不是「字符串能不能拼对」：
        //   · 什么时候该弹二次确认（有同名键）—— 判据必须与 IconOverrides 的查表口径一致；
        //   · 确认后，冲突项**原地替换**、新增项**插到活动行下方**；
        //   · 「一条键只有一行」这个不变式在任何输入下都不破（含同一次粘贴里出现重复键）。
        // 这些规则以前埋在 ConfigPanel 的鼠标/键盘分支里，只能靠人肉点界面验、极易退化；
        // 抽成纯类之后才能这样逐条钉住。
        RunClipboardSection((name, ok, detail) => Check(name, ok, detail));

        Console.WriteLine($"  —— 图标用例：通过 {passed}，失败 {failed}");
        return (passed, failed);
    }

    /// <summary>
    /// 复制 / 粘贴规则的验证（纯逻辑，不碰剪贴板、不写盘）。
    ///
    /// ★ 为什么冲突语义值得这么多用例 ★
    /// 「粘贴时同名怎么办」是整个功能里唯二会**改掉用户已有数据**的地方（另一个是保存），
    /// 而它同时又是最容易在重构里被悄悄改坏的地方 —— 把「先提示、二次确认」退化成
    /// 「直接覆盖」，界面上几乎看不出来，只有用户的旧配置被吃掉时才会发现。
    /// </summary>
    private static void RunClipboardSection(Action<string, bool, string> check)
    {
        void Check(string name, bool condition, string detail = "") => check(name, condition, detail);

        // ── 11.1 行解析：与 IconOverrides / ParseIconLine 同一套判据 ──
        Check("解析：正常一行「微信=qq」",
            IconClipboard.TryParseLine("微信=qq", out var p1) && p1.Key == "微信" && p1.Icon == "qq",
            $"{p1.Key}/{p1.Icon}");
        Check("解析：等号两侧空白被去掉",
            IconClipboard.TryParseLine("  微信 = qq  ", out var p2) && p2.Key == "微信" && p2.Icon == "qq",
            $"{p2.Key}/{p2.Icon}");
        Check("解析：以**第一个**等号为界（值里的 = 归到值里）",
            IconClipboard.TryParseLine("a=b=c", out var p3) && p3.Key == "a" && p3.Icon == "b=c",
            $"{p3.Key}/{p3.Icon}");
        Check("解析：base64 的 = 补位不被当分隔符",
            IconClipboard.TryParseLine("icon=data:image/png;base64,AA==", out var p4)
            && p4.Key == "icon" && p4.Icon == "data:image/png;base64,AA==",
            $"{p4.Key}/{p4.Icon}");
        Check("解析：没有等号 → 无效", !IconClipboard.TryParseLine("微信", out _));
        Check("解析：等号在最前（键为空）→ 无效", !IconClipboard.TryParseLine("=qq", out _));
        Check("解析：值为空 → 无效", !IconClipboard.TryParseLine("微信=", out _));
        Check("解析：键为空白 → 无效", !IconClipboard.TryParseLine("   =qq", out _));
        Check("解析：空串 / 纯空白 → 无效",
            !IconClipboard.TryParseLine("", out _) && !IconClipboard.TryParseLine("   ", out _));

        // ── 11.2 复制：只复制有效行，空行/半成品给空串（不往剪贴板塞垃圾）──
        var buffer = new List<string> { "微信=qq", "", "  bilibili=https://x  ", "半成品" };
        Check("复制：取活动行原文", IconClipboard.CopyLine(buffer, 0) == "微信=qq");
        Check("复制：行内首尾空白被规整", IconClipboard.CopyLine(buffer, 2) == "bilibili=https://x",
            IconClipboard.CopyLine(buffer, 2));
        Check("复制：空行 → 空串（提示用户「无可复制内容」，而不是复制一个空串）",
            IconClipboard.CopyLine(buffer, 1) == "");
        Check("复制：半成品（没有 =）→ 空串", IconClipboard.CopyLine(buffer, 3) == "");
        Check("复制：未选中 / 越界 → 空串",
            IconClipboard.CopyLine(buffer, -1) == "" && IconClipboard.CopyLine(buffer, 99) == "");

        // ── 11.3 计划：无冲突 → 不需要确认 ──
        var free = IconClipboard.Plan("微信=qq", ["知乎=z"]);
        Check("计划：无同名键 → 无冲突、无需确认",
            free.HasAnything && free.Entries.Count == 1 && free.Conflicts.Count == 0
            && free.Mode == IconClipboard.ConflictMode.None,
            $"{free.Entries.Count}/{free.Conflicts.Count}/{free.Mode}");
        Check("计划：空 / null 剪贴板 → 没有任何可粘贴内容",
            !IconClipboard.Plan("", []).HasAnything && !IconClipboard.Plan(null, []).HasAnything);
        Check("计划：纯空白不算「无效行」而是被忽略（用户多按了回车不该被报错）",
            IconClipboard.Plan("   \n\n  ", []).Invalid.Count == 0);

        // ── 11.4 计划：多行 + 坏行不阻断其余行 ──
        var mixed = IconClipboard.Plan("微信=qq\n\n坏行\n知乎=z", []);
        Check("计划：多行都解析出来，空行忽略",
            mixed.Entries.Count == 2
            && mixed.Entries[0].Key == "微信" && mixed.Entries[1].Key == "知乎",
            string.Join(",", mixed.Entries.Select(e => e.Key)));
        Check("计划：坏行如实记入 Invalid（不静默丢弃）",
            mixed.Invalid.Count == 1 && mixed.Invalid[0] == "坏行",
            string.Join(",", mixed.Invalid));
        Check("计划：坏行不影响其余行照常粘贴", mixed.HasAnything && mixed.Conflicts.Count == 0);
        Check("计划：全是坏行时 → 无内容但有 Invalid（界面据此区分「剪贴板为空」与「格式不对」）",
            !IconClipboard.Plan("随手复制的一段普通文字", []).HasAnything
            && IconClipboard.Plan("随手复制的一段普通文字", []).Invalid.Count == 1);

        // ── 11.5 计划：冲突判定（必须与 IconOverrides 的查表口径一致）──
        var dup1 = IconClipboard.Plan("微信=new", ["微信=old"]);
        Check("★ 计划：同名键 → 冲突 + 需二次确认（不得静默覆盖）",
            dup1.Conflicts.Count == 1 && dup1.Mode == IconClipboard.ConflictMode.NeedsConfirm
            && dup1.Conflicts[0].Key == "微信",
            $"{dup1.Conflicts.Count}/{dup1.Mode}");
        Check("★ 计划：大小写不敏感（WeChat 与 wechat 视为同一键）",
            IconClipboard.Plan("WeChat=qq", ["wechat=old"]).Conflicts.Count == 1);
        Check("★ 计划：缓冲里的行带空格也算同名（按解析后的键比较，而非字符串）",
            IconClipboard.Plan("微信=new", ["微信 = old"]).Conflicts.Count == 1);
        Check("计划：部分冲突也需确认（一条同名就够）",
            IconClipboard.Plan("微信=new\n知乎=z", ["微信=old"]) is
            { Entries.Count: 2, Conflicts.Count: 1, Mode: IconClipboard.ConflictMode.NeedsConfirm });
        Check("计划：无同名键时不弹确认（不打扰用户）",
            IconClipboard.Plan("快手=k", ["微信=old"]).Mode == IconClipboard.ConflictMode.None);

        // ★ 同一次粘贴里出现重复键：只保留最后一条
        //   否则 Apply 会插入两行同名 —— 界面上「同一应用两行配置」，实际只有最后一行生效。
        var selfDup = IconClipboard.Plan("微信=a\n微信=b", []);
        Check("★ 计划：同一次粘贴里的重复键只留一条（后值生效，与 IconOverrides.Parse 一致）",
            selfDup.Entries.Count == 1 && selfDup.Entries[0].Icon == "b",
            $"{selfDup.Entries.Count}/{string.Join(",", selfDup.Entries.Select(e => e.Icon))}");
        var selfDup2 = IconClipboard.Plan("微信=a\n知乎=z\n微信=b", []);
        Check("★ 计划：去重后保持首次出现的顺序位置",
            selfDup2.Entries.Count == 2 && selfDup2.Entries[0].Key == "微信"
            && selfDup2.Entries[0].Icon == "b" && selfDup2.Entries[1].Key == "知乎",
            string.Join("|", selfDup2.Entries.Select(e => $"{e.Key}={e.Icon}")));

        // ── 11.6 执行：冲突原地替换 / 新增插到活动行下方 ──
        var baseLines = new List<string> { "微信=old", "知乎=z" };

        var overwrite = IconClipboard.Apply(
            baseLines, 0, IconClipboard.Plan("微信=new", baseLines));
        Check("★ 执行：冲突项**原地替换**（位置不变，不会出现同名两行）",
            overwrite.Lines.SequenceEqual(["微信=new", "知乎=z"])
            && overwrite.Overwritten == 1 && overwrite.Added == 0,
            $"{string.Join(",", overwrite.Lines)} / {overwrite.Overwritten}+{overwrite.Added}");

        var insert = IconClipboard.Apply(
            baseLines, 0, IconClipboard.Plan("快手=k", baseLines));
        Check("★ 执行：无冲突项插到**活动行下方**",
            insert.Lines.SequenceEqual(["微信=old", "快手=k", "知乎=z"])
            && insert.Overwritten == 0 && insert.Added == 1,
            string.Join(",", insert.Lines));

        var combo = IconClipboard.Apply(
            baseLines, 1, IconClipboard.Plan("微信=new\n快手=k", baseLines));
        Check("★ 执行：混合时各走各的路（替换在 0、插入在 2）",
            combo.Lines.SequenceEqual(["微信=new", "知乎=z", "快手=k"])
            && combo.Overwritten == 1 && combo.Added == 1,
            $"{string.Join(",", combo.Lines)} / {combo.Overwritten}+{combo.Added}");

        var past = IconClipboard.Apply(
            ["微信=old"], 5, IconClipboard.Plan("快手=k", ["微信=old"]));
        Check("执行：活动行越界 → 退化为追加到末尾（最不意外的行为）",
            past.Lines.SequenceEqual(["微信=old", "快手=k"]), string.Join(",", past.Lines));

        var empty = IconClipboard.Apply([], -1, IconClipboard.Plan("快手=k", []));
        Check("执行：空缓冲 + 未选中 → 直接放进第一行",
            empty.Lines.SequenceEqual(["快手=k"]), string.Join(",", empty.Lines));

        var spaced = IconClipboard.Apply(
            ["微信 = old"], 0, IconClipboard.Plan("微信=new", ["微信 = old"]));
        Check("执行：缓冲行带空格也能被替换（按解析后的键定位）",
            spaced.Lines.SequenceEqual(["微信=new"]) && spaced.Overwritten == 1,
            string.Join(",", spaced.Lines));

        Check("执行：不改动原列表（纯函数，避免界面拿着同一个引用被别名坑）",
            baseLines.SequenceEqual(["微信=old", "知乎=z"]), string.Join(",", baseLines));

        Check("★ 执行：同一次粘贴里的重复键最终只落一行（不变式守住）",
            IconClipboard.Apply([], -1, IconClipboard.Plan("微信=x\n微信=y", [])).Lines.Count == 1);

        // 复制 → 粘贴的往返：复制出来的原文粘回去必须还是同一条
        var copied = IconClipboard.CopyLine(["微信=https://x/w.png"], 0);
        var roundTrip = IconClipboard.Apply(
            ["微信=https://x/w.png"], 0,
            IconClipboard.Plan(copied, ["微信=https://x/w.png"]));
        Check("★ 往返：复制一行再粘回原处 → 内容不变、且被识别为「覆盖」而非「新增」",
            roundTrip.Lines.SequenceEqual(["微信=https://x/w.png"]) && roundTrip.Added == 0,
            string.Join(",", roundTrip.Lines));

        // ── 11.7 文案：确认提示与结果说明 ──
        var oneConflict = IconClipboard.Plan("微信=new", ["微信=old"]).ConfirmPrompt();
        Check("★ 提示（1 条）：点明「已存在」与「会覆盖」，且不出现「有 1 条」这种别扭说法",
            oneConflict.Contains("微信") && oneConflict.Contains("已存在")
            && oneConflict.Contains("覆盖") && !oneConflict.Contains("有 1 条"),
            oneConflict);
        var twoConflicts = IconClipboard.Plan("微信=a\n知乎=b", ["微信=1", "知乎=2"]).ConfirmPrompt();
        Check("★ 提示（多条）：给出条数与全部键名（用户才知道要牺牲什么）",
            twoConflicts.Contains("有 2 条已存在") && twoConflicts.Contains("微信")
            && twoConflicts.Contains("知乎"),
            twoConflicts);

        Check("说明：全新增 → 「已粘贴 N 条映射」",
            IconClipboard.Plan("微信=a\n知乎=b", []).DoneMessage() == "已粘贴 2 条映射",
            IconClipboard.Plan("微信=a\n知乎=b", []).DoneMessage());
        Check("说明：全覆盖 → 「已覆盖 N 条映射」",
            IconClipboard.Plan("微信=new", ["微信=old"]).DoneMessage() == "已覆盖 1 条映射",
            IconClipboard.Plan("微信=new", ["微信=old"]).DoneMessage());
        Check("说明：混合 → 同时给出总数与覆盖数",
            IconClipboard.Plan("微信=new\n快手=k", ["微信=old"]).DoneMessage()
                == "已粘贴 2 条（其中 1 条覆盖了同名项）",
            IconClipboard.Plan("微信=new\n快手=k", ["微信=old"]).DoneMessage());

        // ── 11.8 落盘文本（必须与 IconOverrides.Serialize 的格式一致，否则存回配置会读到空行）──
        Check("落盘：逐行 trim、丢掉空行",
            IconClipboard.ToPersistText(["微信=qq", "", "  知乎=z  "]) == "微信=qq\n知乎=z",
            IconClipboard.ToPersistText(["微信=qq", "", "  知乎=z  "]).Replace("\n", "\\n"));
        Check("落盘：空缓冲 / 全空行 → 空串（不会存成一堆 \\n）",
            IconClipboard.ToPersistText([]) == ""
            && IconClipboard.ToPersistText(["", "   "]) == "");
        Check("落盘 → 解析 往返一致（存回去的值下次还能读出来）",
            IconOverrides.Parse(IconClipboard.ToPersistText(["微信=qq", "知乎=z"])).Count == 2);
    }

    /// <summary>
    /// 宿主图标释放的验证。抽成独立方法是因为它需要**真实写盘**（在测试工程自己的输出目录下），
    /// 并且会改变 <see cref="IconCatalog"/> 的别名探测结论 —— 必须放在全部「别名不可用」断言之后，
    /// 否则会把前面的用例带偏（探测结果是按目录缓存的）。
    ///
    /// ★★ 本组用例**自己管理初始与结束状态**，绝不依赖上一轮运行的残留 ★★
    /// 曾踩过的坑：释放用例把文件写到测试输出目录后没有清掉，下一次运行的第 2 节
    /// <c>aliasUsable</c> 就会探到 true 而走另一条分支，于是
    /// 「别名不可用时…」那几条断言在**本不该执行的状态**下被执行并 FAIL，
    /// 同时「首次释放写了 6 个」也退化成「written=1 skipped=5」——
    /// 表现是「结果随运行次数漂移」，最难排查。
    /// 因此这里 **进入时清空、退出时再清空**，让每次运行都从同一个确定状态出发。
    /// </summary>
    private static void RunSeederSection(Action<string, bool, string> check)
    {
        void Check(string name, bool condition, string detail = "") => check(name, condition, detail);

        var dir = Path.Combine(AppContext.BaseDirectory, "data", "image");

        // 前置条件：`Run()` 开头已把该目录归零（那里是唯一的清理点，必须在别名探测之前）。
        // 这里只做一次确认，防止「有人把开头的清理删掉」而无人察觉。
        Check("★ 前置条件：data/image 已归零（首次释放必然是全新写入）",
            !Directory.Exists(dir) || !Directory.EnumerateFiles(dir).Any());

        // ① 资源齐全：内嵌 6 个（少一个都说明 csproj 的 EmbeddedResource 配错了）
        Check("★ 6 张宿主官方图标已内嵌进插件 dll",
            HostImageSeeder.EmbeddedCount == 6,
            $"embedded={HostImageSeeder.EmbeddedCount} / 目标 {HostImageSeeder.TargetFileNames.Count}");

        // ② 释放：真的写盘（前置条件已归零，故必然是 6 个全写）
        var first = HostImageSeeder.Seed();
        Check("★ 释放成功且无失败项", first is { Failed: 0, Error: "" }, first.Describe());
        Check("★ 释放后 6 个文件都在目标目录里",
            HostImageSeeder.TargetFileNames.All(f => File.Exists(Path.Combine(dir, f))),
            string.Join(",", HostImageSeeder.TargetFileNames.Where(f => !File.Exists(Path.Combine(dir, f))).Select(f => "缺:" + f)));
        Check("★ 从空目录出发：6 个文件全部是本次写入（不是空跑、也不依赖残留）",
            first.Written == 6 && first.Skipped == 0 && first.ChangedAnything,
            $"written={first.Written} skipped={first.Skipped}");

        // ③ 幂等：再调一次，一个文件都不该重写
        var second = HostImageSeeder.Seed();
        Check("★ 重复调用幂等：第二次不写任何文件",
            second.Written == 0 && second.Skipped == 6,
            $"written={second.Written} skipped={second.Skipped}");
        Check("★ 幂等时 ChangedAnything=false（上层据此不重复提示重启）", !second.ChangedAnything);

        // ④ 不覆盖：把某个文件改成自定义内容，再释放一次，内容必须原样保留
        var probe = Path.Combine(dir, "qq-icon.png");
        var customBytes = new byte[] { 0xAA, 0xBB, 0xCC };
        File.WriteAllBytes(probe, customBytes);
        HostImageSeeder.Seed();
        Check("★ 已存在的文件绝不被覆盖（尊重用户自备的图）",
            File.ReadAllBytes(probe).SequenceEqual(customBytes),
            $"len={new FileInfo(probe).Length}（期望 3）");

        // ⑤ 状态页文案：此时目录里 6 个都在 → 应如实显示「已就位 6/6」
        //   （必须放在收尾清理**之前**断言，清空后就不该再显示 6/6 了）
        Check("DescribeStatus 能反映「已就位 6/6」",
            HostImageSeeder.DescribeStatus().Contains("6/6"),
            HostImageSeeder.DescribeStatus());

        // ⑤ 收尾：清空目录 + 让别名探测重来
        //   ⚠️ 两件事都不能省：残留文件会污染下一次运行（见本方法注释）；
        //      不清探测缓存则会污染**本次进程内**后续的一切图标判定。
        CleanDataImageDir(dir);
        IconCatalog.ResetAliasProbeCache();

        Check("★ 用例收尾后目录已清空（保证可重复运行）", !Directory.Exists(dir) || !Directory.EnumerateFiles(dir).Any());
    }

    /// <summary>
    /// 删除测试输出目录下的 data/image（含目录本身）。返回删除的文件数。
    /// 只动**测试工程自己输出目录**里的这一个子目录，不碰任何真实数据。
    /// </summary>
    private static int CleanDataImageDir(string dir)
    {
        try
        {
            if (!Directory.Exists(dir)) return 0;

            int n = 0;
            foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                try { File.Delete(f); n++; } catch { /* 单个文件删不掉不影响其余 */ }
            }

            try { Directory.Delete(dir, recursive: true); } catch { /* 目录非空/占用时留着即可 */ }
            return n;
        }
        catch
        {
            return 0;
        }
    }
}
