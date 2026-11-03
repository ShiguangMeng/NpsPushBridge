using NpsPushBridge;
using SkiaSharp;

namespace BridgeHarness;

/// <summary>
/// 配置面板的**端到端冒烟测试**：用假宿主实例化 <see cref="ConfigPanel"/>，
/// 真的往 SKSurface 上画、真的投递鼠标与键盘事件，然后断言「用户输入 → 保存 → 落盘文本」。
///
/// ★ 这层测试为什么值得存在 ★
/// 本次改动（白名单改逐条输入框、图标映射拆成两个框、加校验与拦截）**全部发生在绘制与
/// 事件处理代码里** —— 而这类代码以前完全不在测试范围内（面板被认为依赖 WinForms）。
/// 结果是：几何算错、焦点找不到、按键落到错误的目标、保存拼错字符串，
/// 都只能靠人开窗口手工点，出了问题也说不清是哪一步。
///
/// 现在这套测试能挡住下面这些**以前只能靠肉眼**的失误：
///   · 画一遍会不会抛异常（新几何里任何越界/空引用都会在这里炸出来）；
///   · 点进某个输入框后敲字，字是不是真的进了**那一个**框；
///   · 保存时用的是不是**校验通过后的那串**、非法输入有没有被拦下；
///   · 落盘文本喂给真实解析器（<see cref="IpWhitelist"/> / <see cref="IconOverrides"/>）后是否真的生效。
///
/// ⚠️ 假宿主刻意只实现「面板会用到」的行为，不做任何真实 IO —— 测试必须可重复、无副作用。
/// </summary>
internal static class PanelSmokeTests
{
    private static int _passed, _failed;

    /// <summary>与真实窗口一致的画布尺寸（见 <see cref="ConfigPanelDefaults"/>）。</summary>
    private const int CanvasWidth = ConfigPanelDefaults.WindowWidth;
    private const int CanvasHeight = ConfigPanelDefaults.WindowHeight;

    public static (int Passed, int Failed) Run()
    {
        Console.WriteLine("【配置面板：绘制 / 输入 / 保存（端到端）】");

        RunDrawSection();
        RunWhitelistFlowSection();
        RunIconMapFlowSection();
        RunBlacklistFlowSection();
        RunEditingSection();
        RunWindowChromeSection();

        return (_passed, _failed);
    }

    // ====================================================================
    // ① 绘制不抛异常（新几何的第一道门槛）
    // ====================================================================

    private static void RunDrawSection()
    {
        Console.WriteLine("  -- 绘制 --");

        var host = new FakeHost();
        var panel = new ConfigPanel(host, () => { });

        Check("绘制主页面（660×620，含逐条输入框）不抛异常", DrawSafely(panel, out var err), err);
        Check("绘制后在标题行看到关键输入框的提示文案…（用命中判定反证几何已建立）",
            panel.HitTestName(ConfigLayout.Compute(CanvasWidth, CanvasHeight).WhitelistEntryField(0).MidX,
                ConfigLayout.Compute(CanvasWidth, CanvasHeight).WhitelistEntryField(0).MidY) == "WhitelistBox");

        // 空配置（没有任何白名单条目）也要能画
        host.WhitelistRaw = "";
        panel.Reload();
        Check("空配置下绘制不抛异常", DrawSafely(panel, out var err2), err2);

        // 超过一屏的条目（触发滚动路径）
        host.WhitelistRaw = string.Join("\n", Enumerable.Range(0, 7).Select(i => $"192.168.1.{i + 10}"));
        panel.Reload();
        Check("7 条白名单（超出可见 3 条）绘制不抛异常", DrawSafely(panel, out var err3), err3);

        // 超长内容（截断路径）
        host.WhitelistRaw = new string('9', 200);
        panel.Reload();
        Check("超长条目（走截断路径）绘制不抛异常", DrawSafely(panel, out var err4), err4);

        // ★ 回归防线：面板绘制**不得**释放共享字体缓存 ★
        // DrawCore 每帧从 PluginFonts 取字体，而那把 Typeface 是**进程内共享的缓存实例**
        // （PluginFonts 明文约定「从不 Dispose 缓存里的实例」）。早先这里是
        // `using var typeface = PluginFonts.Cjk;` —— 每画一帧就把共享实例释放一次：
        // 写法看着像「资源管理得当」，实际是把全局字体资源交了出去，之后每一帧取到的都是
        // 同一个已释放对象。这里在**连画 4 次之后**确认共享字体仍然可用，把这个坑钉住。
        var shared = PluginFonts.Cjk;
        Check("★ 连画多帧后共享字体缓存仍可用（面板不得 Dispose 它）",
            TextLayout.CanRender(shared, "有新版本端口白名单保存"), PluginFonts.UiFontName);
    }

    // ====================================================================
    // ② 白名单：点进去打字 → 保存（含非法输入被拦）
    // ====================================================================

    private static void RunWhitelistFlowSection()
    {
        Console.WriteLine("  -- 白名单：输入 → 保存 --");

        var layout = ConfigLayout.Compute(CanvasWidth, CanvasHeight);

        // ── 非法输入必须被拦下 ──
        {
            var host = new FakeHost { WhitelistRaw = "192.168.1.23" };
            var panel = new ConfigPanel(host, () => { });
            Draw(panel);

            Click(panel, layout.WhitelistEntryField(0));      // 点进第 1 条输入框
            RetypeWhitelist(panel, 0, "192.168.1.256");             // 越界地址
            Draw(panel);

            Click(panel, layout.SaveButton);
            Check("★ 非法地址（192.168.1.256）：点保存**不会**写配置",
                host.SaveCount == 0, $"SaveCount={host.SaveCount}");
            Check("★ 非法地址：给出可读原因（指出是第几条、错在哪）",
                host.Notes.Any(n => n.Contains("保存失败") && n.Contains("第 1 条")),
                string.Join(" | ", host.Notes));
            Check("非法地址：仍留着未保存标记（不假装成功）", panel.HasWhitelistDirty);
        }

        // ── 合法输入保存并真的生效 ──
        {
            var host = new FakeHost { WhitelistRaw = "" };
            var panel = new ConfigPanel(host, () => { });
            Draw(panel);

            Click(panel, layout.WhitelistEntryField(0));
            RetypeWhitelist(panel, 0, "192.168.1.*");
            Draw(panel);

            Click(panel, layout.SaveButton);
            Check("★ 合法地址：点保存**会**写配置，且内容 = 校验归一化后的值",
                host.SaveCount == 1 && host.SavedWhitelist == "192.168.1.*",
                $"{host.SaveCount} / 「{host.SavedWhitelist}」");
            Check("★ 合法地址：保存后真实解析器认这条规则（1 条）",
                IpWhitelist.Parse(host.SavedWhitelist).RuleCount == 1);
            Check("★ 合法地址：该规则确实放行同网段来源",
                IpWhitelist.Parse(host.SavedWhitelist).IsAllowed(System.Net.IPAddress.Parse("192.168.1.55")));
            Check("保存成功后清掉未保存标记", !panel.HasWhitelistDirty);
        }

        // ── 「＋添加」按钮新增条目 ──
        {
            var host = new FakeHost { WhitelistRaw = "10.0.0.1" };
            var panel = new ConfigPanel(host, () => { });
            Draw(panel);

            Click(panel, layout.WhitelistAddButton);
            Draw(panel);
            Check("「＋添加」后出现第 2 条（且已聚焦，可直接打字）",
                panel.WhitelistLineCount == 2 && panel.IsWhitelistFocused,
                $"{panel.WhitelistLineCount}");

            Type(panel, "10.0.0.2");   // 刚「＋添加」出来的条目本来就是空的，直接敲
            Click(panel, layout.SaveButton);
            Check("两条都对 → 保存后真实解析器得到 2 条规则",
                IpWhitelist.Parse(host.SavedWhitelist).RuleCount == 2,
                $"「{host.SavedWhitelist.Replace("\n", " | ")}」");
        }

        // ── 端口非数字（键盘只吃数字，但清空后就是空串）──
        {
            var host = new FakeHost { Port = 1403 };
            var panel = new ConfigPanel(host, () => { });
            Draw(panel);

            Click(panel, layout.PortField);
            SendKey(panel, '\b');   // 删掉 '1403' 的最后一位 → "140"
            Draw(panel);
            Click(panel, layout.SaveButton);
            Check("端口被改成 140：能保存（合法范围）", host.SaveCount == 1 && host.SavedPort == 140,
                $"{host.SaveCount} / {host.SavedPort}");
        }

        // ── 端口清空后保存 → 必须拦住 ──
        {
            var host = new FakeHost { Port = 1403 };
            var panel = new ConfigPanel(host, () => { });
            Draw(panel);

            Click(panel, layout.PortField);
            for (int i = 0; i < 4; i++) SendKey(panel, '\b');   // 清空
            Draw(panel);
            Click(panel, layout.SaveButton);
            Check("★ 端口清空：保存被拦下（不写入一个非法端口）",
                host.SaveCount == 0 && host.Notes.Any(n => n.Contains("保存失败") && n.Contains("端口")),
                string.Join(" | ", host.Notes));
        }
    }

    // ====================================================================
    // ③ 图标映射：两个输入框、回车递进、保存
    // ====================================================================

    private static void RunIconMapFlowSection()
    {
        Console.WriteLine("  -- 图标映射：两个框 → 保存 --");

        var main = ConfigLayout.Compute(CanvasWidth, CanvasHeight);
        var map = ConfigLayout.ComputeIconMap(CanvasWidth);

        // ── 进入映射页 → 在「应用名」框打字 → 回车跳「图标值」框 → 继续打字 → 保存 ──
        {
            var host = new FakeHost { IconRaw = "" };
            var panel = new ConfigPanel(host, () => { });
            Draw(panel);

            Click(panel, ConfigLayout.IconMapButtonOf(main));   // 进入图标映射页
            Draw(panel);
            Click(panel, map.AddButton);                        // ＋添加一行
            Draw(panel);

            Click(panel, map.KeyFieldAt(0));                    // 点应用名框
            Type(panel, "钉钉");
            Draw(panel);
            Check("★ 打进去的字进了**应用名**框（图标值框仍为空）",
                panel.IconRowAt(0) == "钉钉", panel.IconRowAt(0));

            SendKey(panel, '\r');                               // 回车 → 递进到图标值框
            Type(panel, "https://a/b.png");
            Draw(panel);
            Check("★ 回车递进后，字进了**图标值**框（合成 应用名=图标值）",
                panel.IconRowAt(0) == "钉钉=https://a/b.png", panel.IconRowAt(0));

            Click(panel, map.ApplyButton);                      // 保存映射
            Draw(panel);
            Check("★ 保存映射：写进宿主的正是「应用名=图标值」",
                host.IconSaveCount == 1 && host.SavedIconRaw == "钉钉=https://a/b.png",
                $"{host.IconSaveCount} / 「{host.SavedIconRaw}」");

            var parsed = IconOverrides.Parse(host.SavedIconRaw);
            Check("★ 保存映射：真实解析器能读出这条（1 条）", parsed.Count == 1, parsed.Count.ToString());
            Check("★ 保存映射：该映射参与最终图标判定（Source = 自定义）",
                IconCatalog.Decide("钉钉", null, parsed).Source.Contains("自定义"));
        }

        // ── 只填一半 → 保存必须被拦 ──
        {
            var host = new FakeHost { IconRaw = "" };
            var panel = new ConfigPanel(host, () => { });
            Draw(panel);
            Click(panel, ConfigLayout.IconMapButtonOf(main));
            Draw(panel);
            Click(panel, map.AddButton);
            Draw(panel);

            Click(panel, map.KeyFieldAt(0));
            Type(panel, "只写应用名");
            Draw(panel);
            Click(panel, map.ApplyButton);
            Draw(panel);

            Check("★ 只填应用名：保存被拦下（旧版会静默丢弃这条）",
                host.IconSaveCount == 0, $"IconSaveCount={host.IconSaveCount}");
            Check("★ 只填应用名：提示里指明缺图标值",
                host.Notes.Any(n => n.Contains("保存失败") && n.Contains("图标值")),
                string.Join(" | ", host.Notes));
            Check("★ 只填应用名：反证——运行时解析器确实会丢掉它",
                IconOverrides.Parse("只写应用名").Count == 0);
        }

        // ── 反斜杠路径 → 保存必须被拦（宿主会把 \ 当转义符）──
        {
            var host = new FakeHost { IconRaw = "" };
            var panel = new ConfigPanel(host, () => { });
            Draw(panel);
            Click(panel, ConfigLayout.IconMapButtonOf(main));
            Draw(panel);

            Click(panel, map.KeyFieldAt(0));
            Type(panel, "微信");
            SendKey(panel, '\r');
            Type(panel, @"C:\pics\qq.png");   // 反斜杠
            Draw(panel);
            Click(panel, map.ApplyButton);
            Draw(panel);

            Check("★ 反斜杠路径：保存被拦下（否则必然找不到图片）",
                host.IconSaveCount == 0 && host.Notes.Any(n => n.Contains("正斜杠")),
                string.Join(" | ", host.Notes));
        }

        // ── 合法但未收录的别名 → 允许保存，但要有提醒 ──
        {
            var host = new FakeHost { IconRaw = "" };
            var panel = new ConfigPanel(host, () => { });
            Draw(panel);
            Click(panel, ConfigLayout.IconMapButtonOf(main));
            Draw(panel);

            Click(panel, map.KeyFieldAt(0));
            Type(panel, "某应用");
            SendKey(panel, '\r');
            Type(panel, "不存在的别名");
            Draw(panel);
            Click(panel, map.ApplyButton);
            Draw(panel);

            Check("未收录的别名：**允许**保存（可能是有意为之）",
                host.IconSaveCount == 1, $"IconSaveCount={host.IconSaveCount}");
            Check("未收录的别名：保存后如实提醒「可能不会生效」",
                host.Notes.Any(n => n.Contains("可能不会生效")), string.Join(" | ", host.Notes));
        }

        // ── 映射页也画一遍滚动/多行，确认新几何不掉链子 ──
        {
            var host = new FakeHost
            {
                IconRaw = string.Join("\n", Enumerable.Range(0, 8).Select(i => $"应用{i}=qq")),
            };
            var panel = new ConfigPanel(host, () => { });
            Draw(panel);
            Click(panel, ConfigLayout.IconMapButtonOf(main));
            Check("8 条映射（超出可见 5 条）绘制不抛异常", DrawSafely(panel, out var err), err);
        }
    }

    // ====================================================================
    // ③b 应用黑名单页（独立子页）：一条一个框 → 保存
    //
    // 守的是三件事：
    //   · 切页 / 返回真的换了页，且标题行按钮在拖动条内也点得中（见窗口节）；
    //   · 增行 / 输入 / 删行都真的落到缓冲上，并能写进宿主；
    //   · **非法条目必须被拦下** —— 运行时解析器对中间通配是宽容的（按前缀处理），
    //     放它进去就是「提示保存成功、却拦下远超预期的应用」，且没有任何报错。
    // ====================================================================

    private static void RunBlacklistFlowSection()
    {
        Console.WriteLine("  -- 应用黑名单：一条一个框 → 保存 --");

        var main = ConfigLayout.Compute(CanvasWidth, CanvasHeight);
        var bl = ConfigLayout.ComputeBlacklist(CanvasWidth);

        // ── 进入 → 添加一行 → 输入应用名 → 保存 ──
        {
            var host = new FakeHost { AppBlacklistRawText = "" };
            var panel = new ConfigPanel(host, () => { });
            Draw(panel);

            Click(panel, ConfigLayout.BlacklistButtonOf(main));
            Draw(panel);
            Check("★ 进入黑名单页：空名单会补一个空行（否则编辑区是空白卡片，看不出能输入）",
                panel.ProbeBlacklistLines.Count == 1, panel.ProbeBlacklistLines.Count.ToString());

            Click(panel, bl.AddButton);
            Draw(panel);
            Check("「＋添加一行」→ 缓冲变 2 行且标记为未保存",
                panel.ProbeBlacklistLines.Count == 2 && panel.ProbeBlacklistDirty,
                $"{panel.ProbeBlacklistLines.Count} / dirty={panel.ProbeBlacklistDirty}");

            Click(panel, bl.FieldAt(0));
            Type(panel, "微信");
            Draw(panel);
            Check("★ 打进去的字进了**该行的框**", panel.ProbeBlacklistLines[0] == "微信",
                panel.ProbeBlacklistLines[0]);

            Click(panel, bl.ApplyButton);
            Draw(panel);
            Check("★ 保存名单：写进宿主的正是「微信」",
                host.AppBlacklistSaveCount == 1 && host.SavedAppBlacklist == "微信",
                $"{host.AppBlacklistSaveCount} / 「{host.SavedAppBlacklist}」");
            Check("★ 保存名单：真实解析器能读出这条（1 条规则）",
                AppBlacklist.Parse(host.SavedAppBlacklist).RuleCount == 1);
            Check("★ 保存名单：该条目真的参与最终拦截判定",
                AppBlacklist.Parse(host.SavedAppBlacklist).IsBlocked("", "微信"));
            Check("保存后「有未保存改动」标记复位", !panel.ProbeBlacklistDirty);
        }

        // ── 非法条目（中间通配）→ 保存必须被拦 ──
        {
            var host = new FakeHost { AppBlacklistRawText = "" };
            var panel = new ConfigPanel(host, () => { });
            Draw(panel);
            Click(panel, ConfigLayout.BlacklistButtonOf(main));
            Draw(panel);

            Click(panel, bl.FieldAt(0));
            Type(panel, "com.*.mm");
            Draw(panel);
            Click(panel, bl.ApplyButton);
            Draw(panel);

            Check("★ 中间通配：保存被拦下（否则会拦下远超预期的应用）",
                host.AppBlacklistSaveCount == 0, $"AppBlacklistSaveCount={host.AppBlacklistSaveCount}");
            Check("★ 中间通配：提示指明「通配符只能写在末尾」",
                host.Notes.Any(n => n.Contains("保存失败") && n.Contains("末尾")),
                string.Join(" | ", host.Notes));
            Check("★ 中间通配：反证——解析层确实宽容（按 com. 前缀处理），所以界面必须拦",
                AppBlacklist.Parse("com.*.mm").IsBlocked("com.anything.else", ""));
        }

        // ── 载入既有名单 → 缓冲逐行重建 ──
        {
            var host = new FakeHost { AppBlacklistRawText = "微信\ncom.tencent.*\n# 注释" };
            var panel = new ConfigPanel(host, () => { });
            Draw(panel);
            Click(panel, ConfigLayout.BlacklistButtonOf(main));
            Draw(panel);

            Check("★ 进页刷新缓冲：既有条目进缓冲，**注释行原样保留**（保存时不会被抹掉）",
                panel.ProbeBlacklistLines.Count == 3
                && panel.ProbeBlacklistLines[0] == "微信"
                && panel.ProbeBlacklistLines[1] == "com.tencent.*"
                && panel.ProbeBlacklistLines[2] == "# 注释",
                string.Join(" | ", panel.ProbeBlacklistLines));
            Check("注释行不参与拦截判定（解析器跳过 # 行）",
                AppBlacklist.Parse("# 注释\n微信").RuleCount == 1);
            Check("刚载入的既有名单不算「有未保存改动」", !panel.ProbeBlacklistDirty);
        }

        // ── 删除一条 ──
        {
            var host = new FakeHost { AppBlacklistRawText = "微信\nQQ" };
            var panel = new ConfigPanel(host, () => { });
            Draw(panel);
            Click(panel, ConfigLayout.BlacklistButtonOf(main));
            Draw(panel);

            Click(panel, bl.RemoveButtonAt(0));
            Draw(panel);
            Check("★ 点行的「×」→ 该行被删（剩下 QQ）",
                panel.ProbeBlacklistLines.Count == 1 && panel.ProbeBlacklistLines[0] == "QQ",
                string.Join(" | ", panel.ProbeBlacklistLines));
            Check("删除后标记为未保存", panel.ProbeBlacklistDirty);
        }

        // ── 删到一条不剩 → 留空行，保存下去是空名单（= 不拦截）──
        {
            var host = new FakeHost { AppBlacklistRawText = "微信" };
            var panel = new ConfigPanel(host, () => { });
            Draw(panel);
            Click(panel, ConfigLayout.BlacklistButtonOf(main));
            Draw(panel);

            Click(panel, bl.RemoveButtonAt(0));
            Draw(panel);
            Check("★ 删到一条不剩：仍留一个空行（编辑区不会变成空白卡片）",
                panel.ProbeBlacklistLines.Count == 1 && panel.ProbeBlacklistLines[0].Length == 0,
                panel.ProbeBlacklistLines.Count.ToString());

            Click(panel, bl.ApplyButton);
            Draw(panel);
            Check("★ 空名单保存 → 写下去的是空串（= 不拦截任何应用）",
                host.AppBlacklistSaveCount == 1 && host.SavedAppBlacklist == "",
                $"「{host.SavedAppBlacklist}」");
            Check("★ 空名单保存后：解析器确认「什么都不拦」",
                !AppBlacklist.Parse(host.SavedAppBlacklist).IsBlocked("com.tencent.mm", "微信"));
        }

        // ── 名单很长时绘制不抛异常（滚动路径）──
        {
            var host = new FakeHost
            {
                AppBlacklistRawText = string.Join("\n", Enumerable.Range(0, 12).Select(i => $"应用{i}")),
            };
            var panel = new ConfigPanel(host, () => { });
            Draw(panel);
            Click(panel, ConfigLayout.BlacklistButtonOf(main));
            Check("12 条黑名单（超出可见 5 条）绘制不抛异常", DrawSafely(panel, out var err), err);
            Check("12 条时滚动下翻 1 格生效",
                ScrollDownOnce(panel, bl, 12) == 1, "ProbeBlacklistScroll 应为 1");
        }
    }

    /// <summary>按一下「▼」并返回新的滚动位置（黑名单页滚动断言用）。</summary>
    private static int ScrollDownOnce(ConfigPanel panel, BlacklistLayout bl, int expectedRows)
    {
        _ = expectedRows;
        Click(panel, bl.ScrollDownButton);
        Draw(panel);
        return panel.ProbeBlacklistScroll;
    }

    // ====================================================================
    // ④ 输入框的**真实编辑行为**：光标 / 选区 / 剪贴板
    //
    // 这一节守的是用户那句「知不知道什么叫做输入框？」：
    //   · 光标能落在任意位置（点哪儿落哪儿），输入与退格都发生在光标处；
    //   · 能拖选、能双击全选，选中后输入会替换选区；
    //   · Ctrl+A / C / X / V 四个键真的走系统剪贴板；
    //   · 结构性按钮改过缓冲之后，编辑模型必须重新绑定 —— 否则会把上一行的内容写回当前行。
    // 用「框的最左/最右边缘」当落点，是为了让断言与机器字体无关（不依赖某个字符多宽）。
    // ====================================================================

    private static void RunEditingSection()
    {
        Console.WriteLine("  -- 输入框：光标 / 选区 / 剪贴板 --");

        var layout = ConfigLayout.Compute(CanvasWidth, CanvasHeight);
        var field = layout.WhitelistEntryField(0);
        float leftX = field.Left + 2f;
        float rightX = field.Right - 2f;

        // ── ① 点击落点决定光标位置 ──
        {
            var host = new FakeHost { WhitelistRaw = "192.168.1.23" };
            var panel = new ConfigPanel(host, () => { });
            Draw(panel);

            Click(panel, leftX, field.MidY);
            Draw(panel);
            Check("点框左边缘 → 光标下标 0", panel.ProbeCaretIndex == 0, panel.ProbeCaretIndex.ToString());

            Type(panel, "X");
            Draw(panel);
            Check("★ 光标在开头时打字 = 插到最前面（旧版只会追加到末尾）",
                panel.ProbeFieldText == "X192.168.1.23", panel.ProbeFieldText);

            Click(panel, rightX, field.MidY);
            Draw(panel);
            Check("点框右边缘 → 光标落到末尾",
                panel.ProbeCaretIndex == "X192.168.1.23".Length, panel.ProbeCaretIndex.ToString());

            Type(panel, "Y");
            Draw(panel);
            Check("★ 光标在末尾时打字 = 追加", panel.ProbeFieldText == "X192.168.1.23Y", panel.ProbeFieldText);
        }

        // ── ② 退格发生在光标处（不是「永远删最后一个字符」）──
        {
            var host = new FakeHost { WhitelistRaw = "ABCD" };
            var panel = new ConfigPanel(host, () => { });
            Draw(panel);

            Click(panel, leftX, field.MidY);
            Draw(panel);
            SendKey(panel, '\b');
            Draw(panel);
            Check("★ 光标在开头时退格什么都不删（旧版会删掉末尾的 D）",
                panel.ProbeFieldText == "ABCD", panel.ProbeFieldText);

            Click(panel, rightX, field.MidY);
            Draw(panel);
            SendKey(panel, '\b');
            Draw(panel);
            Check("光标在末尾时退格删掉最后一个字符", panel.ProbeFieldText == "ABC", panel.ProbeFieldText);
        }

        // ── ③ 拖选 → 输入替换选区 ──
        {
            var host = new FakeHost { WhitelistRaw = "192.168.1.23" };
            var panel = new ConfigPanel(host, () => { });
            Draw(panel);

            panel.OnMouseMove(leftX, field.MidY);
            panel.OnMouseDown(leftX, field.MidY);
            panel.OnMouseMove(rightX, field.MidY);   // 按住左键移动 = 扩选
            Draw(panel);
            panel.OnMouseUp(rightX, field.MidY);
            Draw(panel);

            Check("★ 拖选选中整条内容（拖到右边缘即全选）",
                panel.ProbeSelectionStart == 0 && panel.ProbeSelectionEnd == 12,
                $"{panel.ProbeSelectionStart}..{panel.ProbeSelectionEnd}");
            Check("松手后不再处于拖拽态", !panel.ProbeDragging);

            Type(panel, "10.0.0.5");
            Draw(panel);
            Check("★ 有选区时输入会替换选区（而不是插到光标处）",
                panel.ProbeFieldText == "10.0.0.5", panel.ProbeFieldText);
        }

        // ── ④ 双击 = 全选（宿主窗口类没有 CS_DBLCLKS，双击由插件自己判定）──
        {
            var host = new FakeHost { WhitelistRaw = "1.2.3.4" };
            var panel = new ConfigPanel(host, () => { });
            Draw(panel);

            Click(panel, leftX, field.MidY);
            Click(panel, leftX, field.MidY);   // 同位置、紧接着的第二次点击
            Draw(panel);
            Check("★ 双击选中全部内容",
                panel.ProbeSelectionStart == 0 && panel.ProbeSelectionEnd == 7,
                $"{panel.ProbeSelectionStart}..{panel.ProbeSelectionEnd}");

            Type(panel, "9");
            Draw(panel);
            Check("双击后输入 = 整条替换", panel.ProbeFieldText == "9", panel.ProbeFieldText);
        }

        // ── ⑤ 键盘剪贴板：Ctrl+A / C / X / V ──
        {
            var host = new FakeHost { WhitelistRaw = "192.168.1.23" };
            var panel = new ConfigPanel(host, () => { });
            Draw(panel);
            Click(panel, rightX, field.MidY);
            Draw(panel);

            SendKey(panel, '\u0001');   // Ctrl+A
            Draw(panel);
            Check("★ Ctrl+A 全选",
                panel.ProbeSelectionStart == 0 && panel.ProbeSelectionEnd == 12,
                $"{panel.ProbeSelectionStart}..{panel.ProbeSelectionEnd}");

            SendKey(panel, '\u0003');   // Ctrl+C
            Draw(panel);
            Check("★ Ctrl+C 把选中内容写进系统剪贴板",
                host.Clipboard == "192.168.1.23", host.Clipboard ?? "(null)");
            Check("复制不改动内容", panel.ProbeFieldText == "192.168.1.23", panel.ProbeFieldText);

            SendKey(panel, '\u0018');   // Ctrl+X
            Draw(panel);
            Check("★ Ctrl+X 剪切：内容被清空", panel.ProbeFieldText == "", panel.ProbeFieldText);
            Check("剪切后剪贴板里是刚剪掉的内容", host.Clipboard == "192.168.1.23", host.Clipboard ?? "(null)");

            host.Clipboard = "10.0.0.9";
            SendKey(panel, '\u0016');   // Ctrl+V
            Draw(panel);
            Check("★ Ctrl+V 把剪贴板内容粘进光标处", panel.ProbeFieldText == "10.0.0.9", panel.ProbeFieldText);
        }

        // ── ⑥ 无选区时 Ctrl+C 复制整格（框里是 IP / 路径，逐字拖选很烦）──
        {
            var host = new FakeHost { WhitelistRaw = "192.168.1.99" };
            var panel = new ConfigPanel(host, () => { });
            Draw(panel);
            Click(panel, leftX, field.MidY);   // 只落光标，不选中任何东西
            Draw(panel);

            SendKey(panel, '\u0003');
            Draw(panel);
            Check("★ 没有选区时 Ctrl+C 复制整格内容（而不是什么都不做）",
                host.Clipboard == "192.168.1.99", host.Clipboard ?? "(null)");
        }

        // ── ⑦ 端口框：光标可定位 + 过滤非数字 ──
        {
            var host = new FakeHost { Port = 1403 };
            var panel = new ConfigPanel(host, () => { });
            Draw(panel);

            Click(panel, layout.PortField.Left + 2f, layout.PortField.MidY);
            Draw(panel);
            Type(panel, "9");
            Draw(panel);
            Check("★ 端口框在开头插数字（说明光标确实可定位）",
                panel.ProbeFieldText == "91403", panel.ProbeFieldText);

            Type(panel, "abc");
            Draw(panel);
            Check("★ 端口框过滤掉非数字", panel.ProbeFieldText == "91403", panel.ProbeFieldText);
        }

        // ── ⑧ Tab / Enter 换框 ──
        {
            var host = new FakeHost { WhitelistRaw = "1.1.1.1\n2.2.2.2" };
            var panel = new ConfigPanel(host, () => { });
            Draw(panel);

            Click(panel, rightX, field.MidY);
            Draw(panel);
            Check("点进第 1 条后编辑模型装的是第 1 条", panel.ProbeFieldText == "1.1.1.1", panel.ProbeFieldText);

            SendKey(panel, '\t');   // Tab → 下一条
            Draw(panel);
            Check("★ Tab 跳到下一条（宿主不给 Tab 键事件，靠 WM_CHAR 的 0x09 实现）",
                panel.ProbeFieldText == "2.2.2.2", panel.ProbeFieldText);

            Type(panel, "Z");
            Draw(panel);
            Check("Tab 之后打字落在第 2 条上", panel.ProbeFieldText == "2.2.2.2Z", panel.ProbeFieldText);

            SendKey(panel, '\r');   // Enter → 新增一条空的
            Draw(panel);
            Check("★ Enter 在下方新增一条空框并聚焦它（不是插入换行）",
                panel.ProbeFieldText == "", $"[{panel.ProbeFieldText}]");
        }

        // ── ⑨ 结构性操作后编辑模型必须重新绑定 ──
        //
        // 这是「编辑模型存的是某一行的拷贝」最容易出错的地方：
        // 点「＋最近来源」会**绕过编辑模型**直接往缓冲里加一行，若不同步重绑，
        // 接下来敲的字符会带着**上一行**的内容一起写进新那行（等于把新加的那条吃掉）。
        {
            var host = new FakeHost { WhitelistRaw = "1.1.1.1", LastIp = "192.168.1.77" };
            var panel = new ConfigPanel(host, () => { });
            Draw(panel);

            Click(panel, rightX, field.MidY);        // 聚焦第 1 条（编辑模型里装着 "1.1.1.1"）
            Draw(panel);
            Check("聚焦后编辑模型装的是第 1 条", panel.ProbeFieldText == "1.1.1.1", panel.ProbeFieldText);

            Click(panel, layout.LastSourceButton);   // 点「＋最近来源」→ 缓冲被直接改动
            Draw(panel);
            Check("★ 点「＋最近来源」后编辑模型已重新绑定到新加的那条",
                panel.ProbeFieldText == "192.168.1.77", $"[{panel.ProbeFieldText}]");

            // 此时两条都是合法地址 → 保存应当成功且内容正确（顺便验证「重绑」没有破坏内容）
            Click(panel, layout.SaveButton);
            Draw(panel);
            Check("★ 保存写出的两条白名单都是各自的正确内容（没有互相污染）",
                host.SavedWhitelist.Split('\n').Length == 2
                && host.SavedWhitelist.StartsWith("1.1.1.1")
                && host.SavedWhitelist.Contains("192.168.1.77"),
                host.SavedWhitelist.Replace("\n", " | "));

            // 再敲一个字符：必须落在**新那条**上，且第 1 条纹丝不动
            var field1 = ConfigLayout.Compute(CanvasWidth, CanvasHeight).WhitelistEntryField(1);
            Click(panel, field1.Right - 2f, field1.MidY);
            Draw(panel);
            Check("重新点第 2 条，编辑模型装的是第 2 条",
                panel.ProbeFieldText.StartsWith("192.168.1.77"), $"[{panel.ProbeFieldText}]");

            Type(panel, "!");
            Draw(panel);
            Check("★ 打字只落在第 2 条上（旧版会把第 1 条的内容整段写进第 2 条）",
                panel.ProbeFieldText == "192.168.1.77!", $"[{panel.ProbeFieldText}]");

            var field0 = ConfigLayout.Compute(CanvasWidth, CanvasHeight).WhitelistEntryField(0);
            Click(panel, field0.Right - 2f, field0.MidY);
            Draw(panel);
            Check("★ 回到第 1 条：内容仍是 \"1.1.1.1\"，没有被第 2 条的操作污染",
                panel.ProbeFieldText == "1.1.1.1", $"[{panel.ProbeFieldText}]");
        }

        // ── ⑩ 光标状态与心跳 ──
        {
            var host = new FakeHost { WhitelistRaw = "1.1.1.1" };
            var panel = new ConfigPanel(host, () => { });
            Draw(panel);

            Check("尚未点入任何框时没有光标", !panel.ProbeCaretVisible);
            Check("没有输入框聚焦时不需要心跳（插件据此不空转）", !panel.NeedsTicks);

            Click(panel, field.MidX, field.MidY);
            Draw(panel);
            Check("★ 点进框后光标可见（这是「它是个输入框」最直接的证据）", panel.ProbeCaretVisible);
            Check("★ 点进框后开始需要心跳（驱动闪烁与方向键轮询）", panel.NeedsTicks);
            Check("聚焦的是白名单框", panel.ProbeFocus == "Whitelist", panel.ProbeFocus);

            panel.Tick();
            Check("心跳本身不抛异常（无键盘时应当是无害的空操作）", true);
        }

        // ── ⑪ 保存后模型被重载 → 编辑态必须复位 ──
        {
            var host = new FakeHost { WhitelistRaw = "" };
            var panel = new ConfigPanel(host, () => { });
            Draw(panel);

            Click(panel, rightX, field.MidY);
            Type(panel, "10.1.1.1");
            Draw(panel);
            Check("先输入一条内容", panel.ProbeFieldText == "10.1.1.1", panel.ProbeFieldText);

            Click(panel, layout.SaveButton);
            Draw(panel);
            // ⚠️ 不要用 `!panel.NeedsTicks` 来间接判断「编辑态已复位」：
            //    NeedsTicks 现在还包含「悬停过渡动画进行中」（鼠标刚停在保存按钮上就会为 true），
            //    它不再是「有没有输入框聚焦」的可靠代理。这里直接断言编辑态本身。
            Check("★ 保存（= 模型被整体重载）后编辑态复位，不再持有旧文本",
                panel.ProbeFocus == "None" && panel.ProbeFieldText == "",
                $"focus={panel.ProbeFocus} text=[{panel.ProbeFieldText}]");
        }
    }

    // ====================================================================
    // ⑤ 窗口外壳：标题行的「置顶」开关 + 滚轮滚动
    //
    // 「窗口可调节大小、位置」由自建窗口（PanelWindow）实现，**没法在无头测试里断言**
    // （那要真的创建窗口、真的拖动）。这里守的是**面板这一侧**能守的部分：
    //   · 「置顶」开关的位置可达、点击真的把请求转达给窗口；
    //   · 窗口可缩放之后，滚轮成了主要滚动手段 —— 它必须按指针所在区域分派。
    // ====================================================================

    private static void RunWindowChromeSection()
    {
        Console.WriteLine("  -- 窗口：置顶开关 / 滚轮 --");

        var layout = ConfigLayout.Compute(CanvasWidth, CanvasHeight);
        var btn = ConfigLayout.TopmostButtonOf(layout);

        // ── ① 置顶开关 ──
        {
            var host = new FakeHost();
            var panel = new ConfigPanel(host, () => { });
            Draw(panel);

            Check("标题行有「置顶」开关，且命中判定可达",
                panel.HitTestName(btn.MidX, btn.MidY) == "TopmostToggle",
                panel.HitTestName(btn.MidX, btn.MidY));

            Check("★ 默认**不置顶**（需求：取消置顶，改为点按钮才置顶）", !host.Topmost);

            Click(panel, btn);
            Draw(panel);
            Check("★ 点一下 → 请求把窗口置顶",
                host.TopmostSetCount == 1 && host.LastTopmostValue,
                $"count={host.TopmostSetCount} value={host.LastTopmostValue}");

            Click(panel, btn);
            Draw(panel);
            Check("★ 再点一下 → 请求取消置顶",
                host.TopmostSetCount == 2 && !host.LastTopmostValue,
                $"count={host.TopmostSetCount} value={host.LastTopmostValue}");

            // 开关与状态胶囊不能重叠（两者都在标题行右侧）
            Check("★ 「置顶」开关与状态胶囊不重叠",
                btn.Right <= ConfigLayout.StatusPillOf(layout).Left,
                $"{btn.Right} vs {ConfigLayout.StatusPillOf(layout).Left}");
            Check("「置顶」开关留在标题行内（不会压到下面的内容）",
                btn.Top >= layout.Title.Top && btn.Bottom <= layout.Title.Bottom,
                $"{btn.Top}..{btn.Bottom} in {layout.Title.Top}..{layout.Title.Bottom}");

            // ★ 胶囊右端必须给窗口右上角的关闭按钮让路 ★
            //   关闭按钮由 PanelWindow 画在**面板内容之后**（永远盖在上面），
            //   胶囊若顶到内容右边界，右端会被那个圆盘压掉一截 ——
            //   这种问题只在某些窗口宽度下才明显，靠肉眼很容易漏掉，所以钉成断言。
            //   关闭按钮的左边缘（换算到面板坐标）= 内容宽 − 距右边缘 − 半径。
            var pill = ConfigLayout.StatusPillOf(layout);
            float closeLeft = layout.ContentWidth - ConfigLayout.CloseButtonRightInset
                              - ConfigLayout.CloseButtonRadius;
            Check("★ 状态胶囊不被关闭按钮压到（关闭按钮画在面板之上）",
                pill.Right <= closeLeft,
                $"胶囊右端 {pill.Right} vs 关闭按钮左缘 {closeLeft}");

            // 同理，标题文字也不能长到「置顶」按钮底下
            Check("★ 「置顶」开关左边仍留得下标题文字（不会被标题挤掉）",
                btn.Left > layout.Title.Left + 120f,
                $"按钮左端 {btn.Left}，标题起点 {layout.Title.Left}");
        }

        // ── ② 滚轮：按指针所在区域分派 ──
        {
            var host = new FakeHost
            {
                WhitelistRaw = string.Join("\n", Enumerable.Range(0, 8).Select(i => $"192.168.1.{i + 10}")),
            };
            var panel = new ConfigPanel(host, () => { });
            Draw(panel);

            Check("初始在最上面", panel.ProbeWhitelistScroll == 0, panel.ProbeWhitelistScroll.ToString());

            // 向下滚 = 看后面的内容（每格 1 条，便于断言）
            panel.OnWheel(layout.WhitelistBox.MidX, layout.WhitelistBox.MidY, -1f);
            Check("★ 在白名单框内向下滚 → 列表下移",
                panel.ProbeWhitelistScroll == 1, panel.ProbeWhitelistScroll.ToString());

            panel.OnWheel(layout.WhitelistBox.MidX, layout.WhitelistBox.MidY, 1f);
            Check("向上滚 → 回到最上面",
                panel.ProbeWhitelistScroll == 0, panel.ProbeWhitelistScroll.ToString());

            // 已经到顶：继续向上滚不该变成负数
            panel.OnWheel(layout.WhitelistBox.MidX, layout.WhitelistBox.MidY, 1f);
            Check("★ 到顶后继续向上滚不越界（不会出现负数索引）",
                panel.ProbeWhitelistScroll == 0, panel.ProbeWhitelistScroll.ToString());

            // 指针不在任何可滚动区域 → 不该动（避免「鼠标停在别处却把列表滚了」）
            panel.OnWheel(layout.PortField.MidX, layout.PortField.MidY, -1f);
            Check("★ 指针不在可滚动区域时空操作（不会误滚别的区域）",
                panel.ProbeWhitelistScroll == 0, panel.ProbeWhitelistScroll.ToString());
        }

        // ── ③ 原生按键通路（自建窗口给出真 WM_KEYDOWN）──
        //    这一节守的是「一次方向键只走一格」：原生通路与轮询通路**不能同时生效**。
        {
            var host = new FakeHost { WhitelistRaw = "192.168.1.23" };
            var panel = new ConfigPanel(host, () => { });
            Draw(panel);

            Click(panel, layout.WhitelistEntryField(0));
            Draw(panel);
            Check("先点进第 1 条", panel.ProbeFieldText == "192.168.1.23", panel.ProbeFieldText);

            // 默认（宿主设置页环境）**不**声称有原生按键事件 → 靠轮询
            Check("★ 默认不声称具备原生按键事件（宿主设置页那条通路靠轮询兜底）",
                !panel.ProbeNativeKeyEvents);

            panel.NativeKeyEvents = true;
            Check("★ 声明具备原生按键事件后，需要的心跳仍在（光标闪烁要用）", panel.NeedsTicks);

            // 光标在末尾，VK_HOME 应到头
            panel.OnSpecialKey(0x23, false, false);   // VK_END
            Draw(panel);
            Check("★ VK_END → 光标到末尾", panel.ProbeCaretIndex == 12, panel.ProbeCaretIndex.ToString());

            panel.OnSpecialKey(0x24, false, false);   // VK_HOME
            Draw(panel);
            Check("★ VK_HOME → 光标到开头", panel.ProbeCaretIndex == 0, panel.ProbeCaretIndex.ToString());

            panel.OnSpecialKey(0x27, false, false);   // VK_RIGHT
            Draw(panel);
            Check("★ VK_RIGHT → 光标右移一格", panel.ProbeCaretIndex == 1, panel.ProbeCaretIndex.ToString());

            panel.OnSpecialKey(0x25, false, false);   // VK_LEFT
            Draw(panel);
            Check("VK_LEFT → 光标左移一格", panel.ProbeCaretIndex == 0, panel.ProbeCaretIndex.ToString());

            // Ctrl+→ = 按词跳：整段 IP 一次跳完（`.` 算词字符）
            panel.OnSpecialKey(0x27, false, true);    // VK_RIGHT + Ctrl
            Draw(panel);
            Check("★ Ctrl+VK_RIGHT → 按词跳（整段 IP 一次跳过，不是一格一格走）",
                panel.ProbeCaretIndex == 12, panel.ProbeCaretIndex.ToString());

            // Shift 扩选：宿主设置页拿不到，自建窗口能拿到
            panel.OnSpecialKey(0x24, true, false);    // VK_HOME + Shift
            Draw(panel);
            Check("★ Shift+VK_HOME → 扩选到开头（修饰键在自建窗口里可用）",
                panel.ProbeSelectionStart == 0 && panel.ProbeSelectionEnd == 12,
                $"{panel.ProbeSelectionStart}..{panel.ProbeSelectionEnd}");

            // VK_DELETE 删光标处（不是删末尾）
            panel.OnSpecialKey(0x24, false, false);   // 先回到开头
            panel.OnSpecialKey(0x2E, false, false);   // VK_DELETE
            Draw(panel);
            Check("★ VK_DELETE 删的是**光标处**那个字符（旧版只会删末尾）",
                panel.ProbeFieldText == "92.168.1.23", panel.ProbeFieldText);

            // 无关按键不能改动任何东西（例如 VK_F5 = 0x74）
            var before = panel.ProbeFieldText;
            int caretBefore = panel.ProbeCaretIndex;
            panel.OnSpecialKey(0x74, false, false);
            Draw(panel);
            Check("无关按键不改内容、不动光标",
                panel.ProbeFieldText == before && panel.ProbeCaretIndex == caretBefore,
                panel.ProbeFieldText);

            // 没有输入框聚焦时，特殊键必须完全无副作用
            var panel2 = new ConfigPanel(new FakeHost { WhitelistRaw = "1.1.1.1" }, () => { });
            Draw(panel2);
            panel2.NativeKeyEvents = true;
            panel2.OnSpecialKey(0x2E, false, false);
            Check("★ 没有输入框聚焦时，特殊键被忽略（不会去改某一行）",
                !panel2.NeedsTicks && panel2.ProbeFieldText == "", $"[{panel2.ProbeFieldText}]");
        }

        // ── ④ 标题条的「拖动 / 缩放」与标题行按钮不能互相吃掉 ──
        {
            var host = new FakeHost();
            var panel = new ConfigPanel(host, () => { });
            Draw(panel);

            Check("★ 「置顶」按钮处：面板声明「这一点归我」（否则点它会变成拖窗口）",
                panel.WantsMouseAt(btn.MidX, btn.MidY));

            // 标题条右侧一段（不含任何按钮）应仍可拖动 → 面板不声明占用
            Check("标题条空白处不声明占用（那一段要留给拖动窗口）",
                !panel.WantsMouseAt(layout.Title.Left + 300f, layout.Title.MidY));

            // 内容区不声明占用（面板只在必要时排除，其余交给窗口判定）
            Check("内容区不声明占用",
                !panel.WantsMouseAt(layout.WhitelistBox.MidX, layout.WhitelistBox.MidY));

            // ── ★ 表驱动遍历：每个页面的**每一个标题行按钮**都必须声明「归面板」★ ──
            //   标题行按钮落在窗口拖动条（y ≤ 30）内的那一段若不被排除，就会被当成「拖窗口」，
            //   症状是「按钮上半截点不动、要挪到下半截才生效」。
            //   这个坑犯过两次（图标映射页的「返回/保存映射」、以及新加的「更新日志」入口），
            //   所以这里**做成表**：以后再加标题行按钮，只要漏进 WantsMouseAt 就当场变红。
            const float TitleStripHeight = 30f;   // 与 PanelWindow.TitleStripHeight 一致

            void CheckTitleRowButtons(string page, (string Name, SKRect Rect)[] items)
            {
                foreach (var (name, r) in items)
                {
                    Check($"{page}「{name}」确实横跨拖动条边界（前提成立，测的才是上半段）",
                        r.Top < TitleStripHeight && r.Bottom > TitleStripHeight, $"{r.Top}..{r.Bottom}");
                    Check($"★ {page}「{name}」**上半段**（拖动条内）声明归面板 → 点得中",
                        panel.WantsMouseAt(r.MidX, r.Top + 1f));
                }
            }

            CheckTitleRowButtons("主页面", new[]
            {
                ("黑名单", ConfigLayout.BlacklistButtonOf(layout)),
                ("更新日志", ConfigLayout.ChangelogButtonOf(layout)),
                ("置顶", ConfigLayout.TopmostButtonOf(layout)),
            });

            // ── 应用黑名单页：进入 → 标题行按钮归面板 → 返回 ──
            Click(panel, ConfigLayout.BlacklistButtonOf(layout));
            Draw(panel);
            var bl = ConfigLayout.ComputeBlacklist(layout.ContentWidth);
            Check("★ 点「黑名单」→ 切到黑名单页", panel.ProbePage == "Blacklist", panel.ProbePage);
            CheckTitleRowButtons("黑名单页", new[] { ("返回", bl.BackButton), ("保存名单", bl.ApplyButton) });
            Check("黑名单页标题行空白处不声明占用（标题文字与按钮之间仍可拖窗口）",
                !panel.WantsMouseAt(layout.Title.Left + 300f, layout.Title.MidY));

            Click(panel, bl.BackButton);
            Draw(panel);
            Check("黑名单页「返回」→ 回到主页面", panel.ProbePage == "Main", panel.ProbePage);

            Click(panel, layout.IconMapButton);
            Draw(panel);
            var m = ConfigLayout.ComputeIconMap(layout.ContentWidth);
            CheckTitleRowButtons("图标映射页", new[] { ("返回", m.BackButton), ("保存映射", m.ApplyButton) });
            Check("映射页标题行空白处不声明占用（标题文字与按钮之间仍可拖窗口）",
                !panel.WantsMouseAt(layout.Title.Left + 300f, layout.Title.MidY));

            Click(panel, m.BackButton);
            Draw(panel);

            // 「更新日志」入口现在**用浏览器打开官网**（不再切内置页）——
            // 所以这里断言的是「交给宿主去打开」，而不是「窗口里换了一页」。
            // ⚠️ 不能真的调 host 的实现：假宿主只记次数，真实现会唤起浏览器。
            int before = host.OpenChangelogCount;
            Click(panel, ConfigLayout.ChangelogButtonOf(layout));
            Check("★ 点「更新日志」→ 请求宿主打开官网（不再切内置页）",
                host.OpenChangelogCount == before + 1,
                $"{before} → {host.OpenChangelogCount}");
            Check("★ 点完后仍停在主页面（不再有内置日志页）",
                panel.HitTestName(layout.SaveButton.MidX, layout.SaveButton.MidY) == "Save");
        }

        // ── ⑤ 缩放窗口后布局必须重算（缩放能不能「真的有用」取决于这一步）──
        {
            var host = new FakeHost
            {
                WhitelistRaw = string.Join("\n", Enumerable.Range(0, 20).Select(i => $"192.168.1.{i + 10}")),
            };
            var panel = new ConfigPanel(host, () => { });
            Draw(panel);

            var small = ConfigLayout.Compute(CanvasWidth, CanvasHeight);
            var big = ConfigLayout.Compute(CanvasWidth + 260, CanvasHeight + 200);

            // ⚠️ 白名单框的高度是**常量**（`WhitelistBoxHeight`，固定 96）——
            //    拉高窗口**不会**让它多显示条目，多出来的高度归日志区。
            //    这是有意的：条目再多靠 ▲/▼ 滚动，而日志才是越看越多的那块。
            //    把这条写成断言，是为了防止有人「顺手」把它改成随高度伸缩
            //    （那会让纵向布局算术全部重排，属于大改，不该悄悄发生）。
            Check("★ 拉高窗口**不会**改变白名单可见条数（固定高度 96，靠 ▲/▼ 滚动）",
                big.VisibleWhitelistEntries == small.VisibleWhitelistEntries,
                $"{small.VisibleWhitelistEntries} → {big.VisibleWhitelistEntries}");

            Check("★ 拉高窗口把多出来的高度给了**日志区**（日志行数变多）",
                big.VisibleLogLines > small.VisibleLogLines,
                $"{small.VisibleLogLines} → {big.VisibleLogLines}");

            Check("更宽的窗口内容更宽", big.ContentWidth > small.ContentWidth,
                $"{small.ContentWidth} → {big.ContentWidth}");

            // 按放大后的尺寸绘制：几何必须整体跟着变大，而不是内容留在左上角
            panel.RequestRedrawForResize();
            bool drew = true;
            try { Draw(panel, CanvasWidth + 260, CanvasHeight + 200); }
            catch { drew = false; }
            Check("★ 按新尺寸绘制不抛异常（缩放后仍能正常出图）", drew);

            var afterLayout = ConfigLayout.Compute(CanvasWidth + 260, CanvasHeight + 200);
            float entryWidth = afterLayout.WhitelistEntryField(0).Width;
            Check("★ 按新尺寸绘制后，输入框宽度跟着变宽（内容真的重排了）",
                entryWidth > small.WhitelistEntryField(0).Width,
                $"{small.WhitelistEntryField(0).Width} → {entryWidth}");

            // 缩到最小尺寸也要能画（不能出现负宽 / 崩）
            bool minimumOk = true;
            try
            {
                Draw(panel, (int)ConfigLayout.MinContentWidth, (int)ConfigLayout.MinRecommendedHeight);
            }
            catch { minimumOk = false; }
            Check("★ 缩到布局下限仍能绘制（边界不炸）", minimumOk);
        }

        // ── ⑥ 手机端地址行：显示 / 一键复制 / 跟随端口 / 检测不到时的降级 ──
        // 这一行是「用户直接照抄到手机上」的东西，所以既验证**拼出来的串**，
        // 也验证「端口改到一半、还没保存时它跟着变」与「检测不到网卡时不复制」这两条边界。
        {
            var host = new FakeHost { LocalIps = new[] { "192.168.1.42" }, Port = 1403 };
            var panel = new ConfigPanel(host, () => { });
            Draw(panel);
            var l = ConfigLayout.Compute(CanvasWidth, CanvasHeight);

            Check("★ 地址行在端口行下方，且「标签 → 地址 → 复制」依次排开不重叠",
                l.PhoneLabel.Top >= l.PortField.Bottom
                && l.PhoneLabel.Right <= l.PhoneValue.Left
                && l.PhoneValue.Right <= l.PhoneCopyButton.Left,
                $"{l.PhoneLabel.Top:0} / {l.PhoneLabel.Right:0}→{l.PhoneValue.Left:0} / {l.PhoneValue.Right:0}→{l.PhoneCopyButton.Left:0}");

            Check("★ 地址行不压到下一块（「面板显示时长」标签行）",
                l.PhoneCopyButton.Bottom <= l.CollapseLabel.Top,
                $"{l.PhoneCopyButton.Bottom:0} vs {l.CollapseLabel.Top:0}");

            Check("命中：地址行的「复制」按钮可点",
                panel.HitTestName(l.PhoneCopyButton.MidX, l.PhoneCopyButton.MidY) == "PhoneCopy",
                panel.HitTestName(l.PhoneCopyButton.MidX, l.PhoneCopyButton.MidY));

            host.Clipboard = null;
            Click(panel, l.PhoneCopyButton);
            Check("★ 点「复制」→ 剪贴板拿到完整地址 http://192.168.1.42:1403/",
                host.Clipboard == "http://192.168.1.42:1403/", host.Clipboard ?? "<null>");

            // 改端口但**不保存**：地址必须立刻跟着变（用户是照着屏幕抄的，不能等保存）
            Click(panel, l.PortField);
            for (int i = 0; i < 4; i++) SendKey(panel, '\b');
            Type(panel, "1405");
            Draw(panel);
            host.Clipboard = null;
            Click(panel, l.PhoneCopyButton);
            Check("★ 端口改到一半（尚未保存）地址就跟着变：http://192.168.1.42:1405/",
                host.Clipboard == "http://192.168.1.42:1405/", host.Clipboard ?? "<null>");

            // 清空端口：退回**已保存**的端口，而不是跳回默认值（避免看起来像「改了没生效」）
            for (int i = 0; i < 4; i++) SendKey(panel, '\b');
            Draw(panel);
            host.Clipboard = null;
            Click(panel, l.PhoneCopyButton);
            Check("★ 端口框清空 → 退回已保存端口（1403）拼出的地址仍可用",
                host.Clipboard == "http://192.168.1.42:1403/", host.Clipboard ?? "<null>");

            // 检测不到局域网地址：按钮置灰、不复制（一个点不出效果的按钮比没有按钮更让人困惑）
            host.LocalIps = Array.Empty<string>();
            panel.InvalidateLocalIps();
            bool drawnOk = true;
            try { Draw(panel); } catch { drawnOk = false; }
            Check("检测不到局域网地址时该行仍能正常绘制（显示提示、不崩）", drawnOk);

            host.Clipboard = "sentinel";
            Click(panel, l.PhoneCopyButton);
            Check("★ 检测不到局域网地址时不复制（剪贴板保持原样）",
                host.Clipboard == "sentinel", host.Clipboard ?? "<null>");
        }

        // ── ⑦ 提醒显示时长：纯逻辑 / 几何 / 滑块交互 ──
        {
            // ① 纯逻辑（CollapseDuration）
            Check("时长下限：低于 1 秒的输入被夹回 1.00",
                CollapseDuration.Normalize(0.2f) == CollapseDuration.MinSeconds);
            Check("时长上限：高于 30 秒的输入被夹回 30.00",
                CollapseDuration.Normalize(999f) == CollapseDuration.MaxSeconds);
            Check("★ 四舍五入到 2 位小数（需求：支持 2 位小数）",
                CollapseDuration.Normalize(3.456f) == 3.46f, CollapseDuration.Normalize(3.456f).ToString());
            Check("★ 格式化**恒定** 2 位小数（位数跳会让右侧文字宽度抖）",
                CollapseDuration.Format(4f) == "4.00 秒" && CollapseDuration.Format(12.5f) == "12.50 秒",
                CollapseDuration.Format(12.5f));
            Check("NaN / 无穷大退回默认值（配置被改坏也不能把界面带崩）",
                CollapseDuration.Normalize(float.NaN) == CollapseDuration.DefaultSeconds
                && CollapseDuration.Normalize(float.PositiveInfinity) == CollapseDuration.DefaultSeconds);
            Check("★ 解析宽容：空 / 坏值退回默认，越界夹进范围",
                CollapseDuration.Parse(null) == CollapseDuration.DefaultSeconds
                && CollapseDuration.Parse("abc") == CollapseDuration.DefaultSeconds
                && CollapseDuration.Parse("") == CollapseDuration.DefaultSeconds
                && CollapseDuration.Parse("100") == CollapseDuration.MaxSeconds
                && CollapseDuration.Parse("-5") == CollapseDuration.MinSeconds);
            Check("解析：正常值原样读出（含 2 位小数）", CollapseDuration.Parse("7.25") == 7.25f);
            Check("往返：Serialize → Parse 不丢精度",
                CollapseDuration.Parse(CollapseDuration.Serialize(7.25f)) == 7.25f);
            Check("比例换算：下限 → 0、上限 → 1、往返不漂移",
                CollapseDuration.ToRatio(CollapseDuration.MinSeconds) == 0f
                && CollapseDuration.ToRatio(CollapseDuration.MaxSeconds) == 1f
                && Math.Abs(CollapseDuration.FromRatio(CollapseDuration.ToRatio(7.25f)) - 7.25f) < 0.011f);

            // ② 几何：滑块行不越界、不与相邻行重叠
            var rl = ConfigLayout.Compute(CanvasWidth, CanvasHeight);
            Check("★ 提醒行几何：滑块与数值区不重叠、滑块仍有可用宽度",
                rl.CollapseSlider.Right <= rl.CollapseValue.Left && rl.CollapseSlider.Width > 40f,
                $"{rl.CollapseSlider.Right} vs {rl.CollapseValue.Left}");
            Check("提醒行几何：数值区右边界贴内容右边界（右对齐收口）",
                Math.Abs(rl.CollapseValue.Right - (CanvasWidth - ConfigLayout.PadX)) < 0.01f,
                rl.CollapseValue.Right.ToString());
            Check("★ 提醒行几何：标签行在滑块行上方，且整块在端口行与按钮行之间",
                rl.CollapseLabel.Bottom <= rl.CollapseSlider.Top + 0.01f
                && rl.CollapseLabel.Top >= rl.PortField.Bottom - 0.01f
                && rl.CollapseSlider.Bottom <= rl.SaveButton.Top + 0.01f,
                $"{rl.PortField.Bottom} / {rl.CollapseLabel.Top} / {rl.CollapseSlider.Bottom} / {rl.SaveButton.Top}");

            // ③ 交互：点哪跳哪 / 拖动跟随 / **抬手才落盘**
            var shost = new FakeHost { CollapseSeconds = 4f };
            var spanel = new ConfigPanel(shost, () => { });
            Draw(spanel);
            var sl = ConfigLayout.Compute(CanvasWidth, CanvasHeight);

            spanel.OnMouseDown(sl.CollapseSlider.Right - 2f, sl.CollapseSlider.MidY);
            Draw(spanel);
            Check("★ 按在滑块右端：值直接跳到上限 60.00（Windows 滑块是「点哪跳哪」）",
                spanel.ProbeCollapseSeconds == CollapseDuration.MaxSeconds,
                spanel.ProbeCollapseSeconds.ToString());
            Check("★ 拖动期间**不落盘**（一次拖动只该写一次盘）",
                shost.CollapseSaveCount == 0, shost.CollapseSaveCount.ToString());

            spanel.OnMouseMove(sl.CollapseSlider.MidX, sl.CollapseSlider.MidY);
            Draw(spanel);
            Check("★ 拖动跟随：移到中点附近，值随之落到中段（对数刻度下中点约 5.5 秒）",
                spanel.ProbeCollapseSeconds > 4f && spanel.ProbeCollapseSeconds < 7f,
                spanel.ProbeCollapseSeconds.ToString());
            Check("★ 对数刻度：中点对应的值明显低于线性中点（0.5~5 秒这一段被拉开，好拖）",
                spanel.ProbeCollapseSeconds < (CollapseDuration.MinSeconds + CollapseDuration.MaxSeconds) / 2f,
                spanel.ProbeCollapseSeconds.ToString());

            spanel.OnMouseUp(sl.CollapseSlider.MidX, sl.CollapseSlider.MidY);
            Draw(spanel);
            Check("★ 抬手才落盘，且只落一次",
                shost.CollapseSaveCount == 1, shost.CollapseSaveCount.ToString());
            Check("落盘值与界面显示值一致",
                Math.Abs(shost.CollapseSeconds - spanel.ProbeCollapseSeconds) < 0.001f,
                $"{shost.CollapseSeconds} vs {spanel.ProbeCollapseSeconds}");

            // 再完整拖一次 → 恰好再落一次盘（不多不少）
            spanel.OnMouseDown(sl.CollapseSlider.Left + 5f, sl.CollapseSlider.MidY);
            spanel.OnMouseMove(sl.CollapseSlider.Left + 40f, sl.CollapseSlider.MidY);
            spanel.OnMouseUp(sl.CollapseSlider.Left + 40f, sl.CollapseSlider.MidY);
            Draw(spanel);
            Check("第二次完整拖动恰好再落盘一次（累计 2 次）",
                shost.CollapseSaveCount == 2, shost.CollapseSaveCount.ToString());

            // ④ 非拖动时以宿主配置为准（面板是「显示的」而不是「自己记一份」）
            Check("★ 非拖动状态下，面板回读宿主配置（两边一致）",
                Math.Abs(spanel.ProbeCollapseSeconds - shost.CollapseSeconds) < 0.011f,
                $"{spanel.ProbeCollapseSeconds} vs {shost.CollapseSeconds}");

            // ⑤ 值超出范围时滑块仍能画（宿主里塞一个越界值）
            var bhost = new FakeHost { CollapseSeconds = 999f };
            var bpanel = new ConfigPanel(bhost, () => { });
            bool okOut = true;
            try { Draw(bpanel); } catch { okOut = false; }
            Check("★ 宿主里的值越界时滑块仍能正常绘制（绘制侧自己夹一次，不靠上游保证）", okOut);
        }

        // ── ⑧ 「面板显示时长」→ 宿主 AutoCollapseDelay 的换算（本次修复的核心）──
        //    宿主**只读**这一个时长接口（ReminderData.Duration 被它整条忽略），
        //    所以滑块必须落在它上面才有反应。换算规则抽在 CollapseDuration 里，
        //    因此这里能直接断言 —— ConfigDetailPage 本身依赖宿主的 IDetailPage，进不了本工程。
        {
            var d = CollapseDuration.ToAutoCollapseDelay(15f, ignoreMouse: false);
            Check("★ 未勾「忽略外部鼠标」时，如实返回设定秒数",
                Math.Abs(d.TotalSeconds - 15) < 0.001, d.ToString());

            d = CollapseDuration.ToAutoCollapseDelay(7.25f, ignoreMouse: false);
            Check("★ 两位小数如实透传（7.25 秒）",
                Math.Abs(d.TotalSeconds - 7.25) < 0.001, d.ToString());

            d = CollapseDuration.ToAutoCollapseDelay(3f, ignoreMouse: true);
            Check("★ 勾了「忽略外部鼠标操作」→ 返回负数（宿主完全不自动收起）",
                d < TimeSpan.Zero, d.ToString());

            d = CollapseDuration.ToAutoCollapseDelay(999f, ignoreMouse: false);
            Check("换算前先规范化：越界值被夹到上限 60 秒",
                Math.Abs(d.TotalSeconds - CollapseDuration.MaxSeconds) < 0.001, d.ToString());

            Check("★ 换算规则与「显示值」一致（界面写 0.90 秒，宿主就等 0.90 秒）",
                Math.Abs(CollapseDuration.ToAutoCollapseDelay(CollapseDuration.DefaultSeconds, false).TotalSeconds
                    - CollapseDuration.DefaultSeconds) < 0.001);
        }
    }

    // ====================================================================
    // 测试辅助
    // ====================================================================

    private static void Draw(ConfigPanel panel)
    {
        using var surface = SKSurface.Create(new SKImageInfo(CanvasWidth, CanvasHeight));
        panel.Draw(surface.Canvas, new SKRect(0, 0, CanvasWidth, CanvasHeight), PanelTheme.Window, true);
    }

    /// <summary>按指定尺寸绘制（用于验证「窗口缩放后布局确实重算」）。</summary>
    private static void Draw(ConfigPanel panel, int width, int height)
    {
        using var surface = SKSurface.Create(new SKImageInfo(width, height));
        panel.Draw(surface.Canvas, new SKRect(0, 0, width, height), PanelTheme.Window, true);
    }

    private static bool DrawSafely(ConfigPanel panel, out string error)
    {
        try
        {
            Draw(panel);
            error = "";
            return true;
        }
        catch (Exception ex)
        {
            error = $"{ex.GetType().Name}: {ex.Message}";
            return false;
        }
    }

    private static void Click(ConfigPanel panel, SKRect target)
        => Click(panel, target.MidX, target.MidY);

    private static void Click(ConfigPanel panel, float x, float y)
    {
        panel.OnMouseMove(x, y);
        panel.OnMouseDown(x, y);
        panel.OnMouseUp(x, y);
    }

    private static void SendKey(ConfigPanel panel, char ch) => panel.OnChar(ch);

    private static void Type(ConfigPanel panel, string text)
    {
        foreach (var ch in text) panel.OnChar(ch);
    }

    /// <summary>
    /// 把**某一个**白名单输入框重填成 <paramref name="text"/>（模拟用户全选删除后重敲）。
    ///
    /// ⚠️ 只能按「这一条现有内容的长度」发退格 —— 早先这里写的是「无脑发 200 个退格」，
    ///    结果退格在空条目上会**删掉整条**，一路把别的条目也吃掉了，
    ///    于是断言「两条规则」变成了「一条」。这是**测试助手**的错，不是产品的错 ——
    ///    「空条目上退格 = 删掉这条」正是设计好的行为。
    /// </summary>
    private static void RetypeWhitelist(ConfigPanel panel, int index, string text)
    {
        int existing = panel.WhitelistLineAt(index).Length;
        for (int i = 0; i < existing; i++) panel.OnChar('\b');
        Type(panel, text);
    }

    /// <summary>
    /// 假宿主：只在内存里记状态，绝不碰注册表 / 剪贴板 / 文件系统。
    /// 刻意把「面板发出了什么调用」也记下来，这样断言的是**交互结果**而不是内部字段。
    /// </summary>
    // 供测试与「界面预览渲染」共用（见 PreviewRenderer.cs）
    internal sealed class FakeHost : IBridgePanelHost
    {
        public string WhitelistRaw = "";
        public int Port = 1403;
        public string IconRaw = "";

        // 供「界面预览渲染」填示例数据（测试里全部保持默认值 = 原有行为，不受影响）
        public bool ServiceRunning;
        public long Received, Forwarded, Rejected, Failed;
        public string[] LogLines = Array.Empty<string>();
        public string? LastError;

        public int SaveCount;
        public string SavedWhitelist = "";
        public int SavedPort;
        public int IconSaveCount;
        public string SavedIconRaw = "";
        public readonly List<string> Notes = new();

        public ConfigSnapshot Snapshot() => new(
            WhitelistRaw: WhitelistRaw,
            Port: Port,
            ServiceRunning: ServiceRunning,
            WhitelistDescription: "",
            Received: Received, Forwarded: Forwarded, Rejected: Rejected, Failed: Failed,
            LogLines: LogLines,
            LastError: LastError,
            CollapseSeconds: CollapseSeconds,
            CollapseIgnoreMouse: CollapseIgnoreMouse);

        public void SaveConfig(string whitelistRaw, int port)
        {
            SaveCount++;
            SavedWhitelist = whitelistRaw;
            SavedPort = port;
            WhitelistRaw = whitelistRaw;   // 保存后再次读取应与写进去的一致（供 Reload 用）
            Port = port;
        }

        public void SaveIconOverrides(string raw)
        {
            IconSaveCount++;
            SavedIconRaw = raw;
            IconRaw = raw;
        }

        public string IconOverridesRaw() => IconRaw;

        /// <summary>应用黑名单原文（界面读它；保存后写回，供再次读取时保持一致）。</summary>
        public string AppBlacklistRawText = "";
        /// <summary>黑名单落盘次数 —— 测试用它断言「点保存确实写下去了」。</summary>
        public int AppBlacklistSaveCount;
        /// <summary>最后一次写下的黑名单文本。</summary>
        public string? SavedAppBlacklist;
        public string AppBlacklistRaw() => AppBlacklistRawText;
        public void SaveAppBlacklist(string raw)
        {
            AppBlacklistSaveCount++;
            SavedAppBlacklist = raw;
            AppBlacklistRawText = raw;
        }


        /// <summary>「面板显示时长」（秒）。测试用它断言「拖动抬手后确实落盘了」。</summary>
        public float CollapseSeconds = CollapseDuration.DefaultSeconds;
        /// <summary>滑块落盘次数 —— 用来守住「拖动过程中不写盘、只在抬手写一次」这条约定。</summary>
        public int CollapseSaveCount;
        public void SetCollapseSeconds(float seconds)
        {
            CollapseSeconds = CollapseDuration.Normalize(seconds);
            CollapseSaveCount++;
        }

        /// <summary>「忽略外部鼠标操作（不自动收起）」开关。</summary>
        public bool CollapseIgnoreMouse;
        /// <summary>开关落盘次数 —— 守住「点一下就落一次盘」。</summary>
        public int CollapseIgnoreSaveCount;
        public void SetCollapseIgnoreMouse(bool ignore)
        {
            CollapseIgnoreMouse = ignore;
            CollapseIgnoreSaveCount++;
        }

        public void Note(string message) => Notes.Add(message);

        // ── 以下成员本次测试用不到，给最小可用实现 ──
        public void ToggleService() { }
        /// <summary>「最近一次连接的来源 IP」（null = 还没有过连接，供「＋最近来源」测试用）。</summary>
        public string? LastIp;

        public string? LastRemoteIp() => LastIp;

        /// <summary>
        /// 模拟的本机 IPv4 列表（宿主按「最可能能上网的网卡」排序，面板取第一个拼手机端地址）。
        /// 默认**给一个地址**：这样「手机端填」这一行在多数用例里是「有地址」的正常形态，
        /// 需要验证「检测不到」的分支时把它设为空数组即可（见端口与地址行的用例）。
        /// </summary>
        public string[] LocalIps = { "192.168.1.10" };

        /// <summary>枚举本机网卡的调用次数（面板有 1 秒节流，用它验证「不是每帧都枚举」）。</summary>
        public int DetectLocalIpCount;

        public string[] DetectLocalIPv4()
        {
            DetectLocalIpCount++;
            return LocalIps;
        }

        public bool CopyText(string text) { Clipboard = text; return true; }

        /// <summary>模拟的系统剪贴板（CopyText 写入、PasteText 读出）。</summary>
        public string? Clipboard;

        /// <summary>模拟的窗口置顶状态（面板只读它画按钮，真正的切换由窗口完成）。</summary>
        public bool IsTopmost;

        /// <summary>「置顶」被请求设置的次数与最后一次的值（用于断言点击真的转达出去了）。</summary>
        public int TopmostSetCount;
        public bool LastTopmostValue;

        public bool Topmost => IsTopmost;

        public void SetTopmost(bool value)
        {
            TopmostSetCount++;
            LastTopmostValue = value;
            IsTopmost = value;   // 真实实现里窗口会立刻改状态，这里保持一致
        }

        public string PasteText() => Clipboard ?? "";

        /// <summary>「配置教程」被点击的次数（真实现会用浏览器打开官网教程页）。</summary>
        public int OpenTutorialCount;
        public void OpenTutorial() => OpenTutorialCount++;

        /// <summary>「更新日志」被点击的次数（同上，打开官网更新日志页）。</summary>
        public int OpenChangelogCount;
        public void OpenChangelog() => OpenChangelogCount++;

        public UpdateCheckResult UpdateInfo() => default;
        public void CheckUpdateNow() { }
        public void CopyUpdateInfo() { }
        public void OpenMarketPage() { }
        public void OpenDownload() { }
        public UpdateBannerMode CurrentBannerMode => UpdateBannerMode.Clickable;
        public void SetBannerMode(UpdateBannerMode mode) { }
        public UpdateClickAction CurrentClickAction => UpdateClickAction.Market;
        public void SetClickAction(UpdateClickAction action) { }
        public bool SilentUpgradeEnabled => false;
        public void SetSilentUpgrade(bool enabled) { }
        public UpgradeStage CurrentUpgradeStage => UpgradeStage.Idle;
        public string UpgradeStatusText() => "";
        public void UpgradeNow() { }
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
