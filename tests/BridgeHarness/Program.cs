using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using NpsPushBridge;

namespace BridgeHarness;

/// <summary>
/// NpsPushBridge 的端到端验证：
///   1. 白名单匹配规则（精确 / 通配 / CIDR / IPv4-mapped IPv6）；
///   2. 文本净化与 JSON 编码 —— 关键：验证经「宿主 toast.cs 的那次字符串变换」后内容仍然正确；
///   3. 真实网络链路：假的 47300 端点 ← 插件 HttpBridgeServer ← HTTP 客户端，覆盖 200/400/403/状态页。
/// 退出码 0 表示全部通过。
/// </summary>
internal static class Program
{
    private const int BridgePort = 14031;
    private const int RejectPort = 14032;
    private static int _passed, _failed, _skipped;

    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        // 界面预览模式：把配置面板渲染成 PNG（人工核对观感），不跑测试。
        //   BridgeHarness.exe --preview <输出目录>
        if (args.Length > 0 && args[0] == "--preview")
        {
            PreviewRenderer.Render(args.Length > 1 ? args[1] : "preview");
            return 0;
        }

        TestWhitelist();
        TestSanitizeAndJson();
        await TestEndToEndAsync();

        Console.WriteLine();
        var (lightPassed, lightFailed) = LightTests.Run();
        _passed += lightPassed;
        _failed += lightFailed;

        Console.WriteLine();
        var (noticePassed, noticeFailed) = UpdateNoticeTests.Run();
        _passed += noticePassed;
        _failed += noticeFailed;

        Console.WriteLine();
        var (silentPassed, silentFailed) = SilentUpgradeTests.Run();
        _passed += silentPassed;
        _failed += silentFailed;

        Console.WriteLine();
        var (upgraderPassed, upgraderFailed) = SilentUpgraderTests.Run();
        _passed += upgraderPassed;
        _failed += upgraderFailed;

        Console.WriteLine();
        var (layoutPassed, layoutFailed) = LayoutTests.Run();
        _passed += layoutPassed;
        _failed += layoutFailed;

        Console.WriteLine();
        var (phonePassed, phoneFailed) = PhoneAddressTests.Run();
        _passed += phonePassed;
        _failed += phoneFailed;

        Console.WriteLine();
        var (textPassed, textFailed) = TextLayoutTests.Run();
        _passed += textPassed;
        _failed += textFailed;

        
        
        
        
        Console.WriteLine();
        var (iconPassed, iconFailed) = IconCatalogTests.Run();
        _passed += iconPassed;
        _failed += iconFailed;

        Console.WriteLine();
        var (inputPassed, inputFailed) = InputRulesTests.Run();
        _passed += inputPassed;
        _failed += inputFailed;

        Console.WriteLine();
        var (blacklistPassed, blacklistFailed) = AppBlacklistTests.Run();
        _passed += blacklistPassed;
        _failed += blacklistFailed;

        Console.WriteLine();
        var (codePassed, codeFailed) = VerificationCodeTests.Run();
        _passed += codePassed;
        _failed += codeFailed;

        Console.WriteLine();
        var (residuePassed, residueFailed) = ResidueCleanerTests.Run();
        _passed += residuePassed;
        _failed += residueFailed;

        Console.WriteLine();
        var (retryPassed, retryFailed) = ForwardRetryTests.Run();
        _passed += retryPassed;
        _failed += retryFailed;

        Console.WriteLine();
        var (editPassed, editFailed) = TextEditTests.Run();
        _passed += editPassed;
        _failed += editFailed;

        Console.WriteLine();
        var (winPassed, winFailed) = WindowStateTests.Run();
        _passed += winPassed;
        _failed += winFailed;

        Console.WriteLine();
        var (panelPassed, panelFailed) = PanelSmokeTests.Run();
        _passed += panelPassed;
        _failed += panelFailed;

        Console.WriteLine();
        var (uiPassed, uiFailed, uiSkipped) = UiShellTests.Run();
        _passed += uiPassed;
        _failed += uiFailed;
        _skipped += uiSkipped;

        Console.WriteLine();
        UpdateTests.Run();
        await UpdateTests.RunNetworkAsync();
        _passed += UpdateTests.Passed;
        _failed += UpdateTests.Failed;
        _skipped += UpdateTests.Skipped;

        Console.WriteLine();
        Console.WriteLine($"===== 通过 {_passed} 项，失败 {_failed} 项，跳过 {_skipped} 项 =====");
        return _failed == 0 ? 0 : 1;
    }

    // ====================================================================
    // 1. 白名单
    // ====================================================================

    private static void TestWhitelist()
    {
        Console.WriteLine("【白名单匹配】");

        var empty = IpWhitelist.Parse("");
        Check("空白名单被识别为不限制", empty.IsEmpty);
        Check("空白名单放行任意地址", empty.IsAllowed(IPAddress.Parse("203.0.113.9")));

        var mixed = IpWhitelist.Parse("""
            # 本机与局域网
            192.168.1.23
            192.168.1.*
            10.0.0.0/24

            """);
        Check("忽略注释与空行后规则数 = 3", mixed.RuleCount == 3, $"实际 {mixed.RuleCount}");
        Check("精确地址命中", mixed.IsAllowed(IPAddress.Parse("192.168.1.23")));
        Check("通配前缀命中", mixed.IsAllowed(IPAddress.Parse("192.168.1.88")));
        Check("CIDR 段内命中", mixed.IsAllowed(IPAddress.Parse("10.0.0.254")));
        Check("网段外拒绝", !mixed.IsAllowed(IPAddress.Parse("10.0.1.1")));
        Check("无关地址拒绝", !mixed.IsAllowed(IPAddress.Parse("203.0.113.9")));
        Check("IPv4-mapped IPv6 归一后命中", mixed.IsAllowed(IPAddress.Parse("::ffff:192.168.1.23")));

        var cidr24 = IpWhitelist.Parse("192.168.1.0/24");
        Check("/24 覆盖 .1", cidr24.IsAllowed(IPAddress.Parse("192.168.1.1")));
        Check("/24 不含 .2 网段", !cidr24.IsAllowed(IPAddress.Parse("192.168.2.1")));

        var ipv6 = IpWhitelist.Parse("fe80::1");
        Check("IPv6 精确地址命中", ipv6.IsAllowed(IPAddress.Parse("fe80::1")));
        Check("IPv6 其它地址拒绝", !ipv6.IsAllowed(IPAddress.Parse("fe80::2")));
    }

    // ====================================================================
    // 2. 净化与 JSON 编码
    // ====================================================================

    private static void TestSanitizeAndJson()
    {
        Console.WriteLine();
        Console.WriteLine("【文本净化与 JSON 编码】");

        Check("反斜杠 → /", NpsActivityEndpoint.Sanitize(@"C:\temp\x") == "C:/temp/x");
        // CRLF 视作「一个换行」：连续换行（多行正文、空行）折叠成一个空格，避免灵动岛上出现连排空格
        Check("换行 → 空格", NpsActivityEndpoint.Sanitize("甲\n乙\r\n丙") == "甲 乙 丙");
        Check("连续换行折叠为一个空格", NpsActivityEndpoint.Sanitize("甲\n\n\n乙") == "甲 乙");
        Check("制表符 → 空格", NpsActivityEndpoint.Sanitize("a\tb") == "a b");
        Check("控制字符被丢弃", NpsActivityEndpoint.Sanitize("a\u0001b") == "ab");
        Check("中文与 emoji 原样保留", NpsActivityEndpoint.Sanitize("会议提醒 🎉") == "会议提醒 🎉");
        Check("代理对不被拆开", NpsActivityEndpoint.Sanitize("🎉").Length == 2);

        var activity = new PushActivity("手机消息", "标题\"含引号", @"路径 C:\a\b 与换行");
        string json = NpsActivityEndpoint.BuildJson(activity);

        // 只允许出现 \" 一种转义
        bool onlyQuoteEscapes = true;
        for (int i = 0; i < json.Length; i++)
            if (json[i] == '\\' && (i + 1 >= json.Length || json[i + 1] != '"')) onlyQuoteEscapes = false;
        Check("转发体只含 \\\" 一种转义", onlyQuoteEscapes, json);

        Check("中文按原始 UTF-8 输出，未被写成 \\uXXXX", !json.Contains("\\u"));

        // 关键：模拟宿主 toast.cs 的那次变换，验证内容仍然完全正确
        string transformed = HostTransform(json);
        using var doc = JsonDocument.Parse(transformed);
        var root = doc.RootElement;
        Check("经宿主变换后 kind 正确", root.GetProperty("kind").GetString() == "手机消息");
        Check("经宿主变换后 title 正确", root.GetProperty("title").GetString() == "标题\"含引号");
        Check("经宿主变换后 subtitle 正确", root.GetProperty("subtitle").GetString() == "路径 C:/a/b 与换行");

        // 反证：不做净化的「原生序列化」会被宿主变换破坏 —— 这就是净化的存在理由
        string naive = JsonSerializer.Serialize(new { kind = "手机消息", title = @"路径 C:\a\b", subtitle = "x" });
        using var naiveDoc = JsonDocument.Parse(HostTransform(naive));
        string naiveKind = naiveDoc.RootElement.GetProperty("kind").GetString() ?? "";
        string naiveTitle = naiveDoc.RootElement.GetProperty("title").GetString() ?? "";
        Check("反证：原生序列化的中文被宿主变换破坏", naiveKind != "手机消息", naiveKind);
        Check("反证：原生序列化的反斜杠被宿主变换破坏", naiveTitle != @"路径 C:\a\b", naiveTitle);

        // 空体 / 非法体
        Check("空请求体被拒绝", !NpsActivityEndpoint.TryParse(Array.Empty<byte>(), out _, out _));
        Check("非法 JSON 被拒绝", !NpsActivityEndpoint.TryParse(Encoding.UTF8.GetBytes("not-json"), out _, out _));
        Check("缺失字段回落到默认值",
            NpsActivityEndpoint.TryParse(Encoding.UTF8.GetBytes("{}"), out var fallback, out _)
            && fallback.Kind == "手机消息" && fallback.Title == "" && fallback.Subtitle == "");
        Check("body 别名可作 subtitle",
            NpsActivityEndpoint.TryParse(Encoding.UTF8.GetBytes("""{"body":"别名正文"}"""), out var alias, out _)
            && alias.Subtitle == "别名正文");

        // ——— 新格式：from=<应用包名>&content=<正文>（content 常被 URL 编码成 %7B%22…） ———
        Check("表单：只取 content= 后面的内容并解码",
            NpsActivityEndpoint.TryParse(Encoding.UTF8.GetBytes(
                "from=com.tencent.mm&content=%E4%BC%9A%E8%AE%AE%E6%8F%90%E9%86%92"), out var f1, out _)
            && f1.Kind == "微信" && f1.Subtitle == "会议提醒" && !f1.Subtitle.Contains('%'));

        Check("表单：content 是 JSON 时抽出 title/text，不留花括号乱码",
            NpsActivityEndpoint.TryParse(Encoding.UTF8.GetBytes(
                "from=com.tencent.mm&content=%7B%22title%22%3A%22%E7%BE%A4%E5%90%8D%22%2C%22text%22%3A%22%E4%BD%A0%E5%A5%BD%22%7D"),
                out var f2, out _)
            && f2.Title == "群名" && f2.Subtitle == "你好" && !f2.Subtitle.Contains('{') && !f2.Subtitle.Contains('%'));

        Check("表单：二次编码也能解到底",
            NpsActivityEndpoint.TryParse(Encoding.UTF8.GetBytes(
                "content=%257B%2522text%2522%253A%2522%E4%BD%A0%E5%A5%BD%2522%257D"), out var f3, out _)
            && f3.Subtitle == "你好" && !f3.Subtitle.Contains('%'));

        Check("表单：未知包名原样保留，便于辨认来源",
            NpsActivityEndpoint.TryParse(Encoding.UTF8.GetBytes("from=com.foo.bar&content=hi"), out var f4, out _)
            && f4.Kind == "com.foo.bar" && f4.Subtitle == "hi");

        Check("表单：缺少 from 时回落到默认应用名",
            NpsActivityEndpoint.TryParse(Encoding.UTF8.GetBytes("content=hi"), out var f5, out _)
            && f5.Kind == PushActivity.DefaultKind);

        Check("表单：+ 视为空格",
            NpsActivityEndpoint.TryParse(Encoding.UTF8.GetBytes("content=hello+world"), out var f6, out _)
            && f6.Subtitle == "hello world");

        Check("表单：title 键可覆盖从 content 解析出的标题",
            NpsActivityEndpoint.TryParse(Encoding.UTF8.GetBytes("from=com.tencent.mm&title=%E6%A0%87%E9%A2%98&content=%E6%AD%A3%E6%96%87"),
                out var f7, out _)
            && f7.Title == "标题" && f7.Subtitle == "正文");

        Check("表单：非法百分号序列不抛异常",
            NpsActivityEndpoint.TryParse(Encoding.UTF8.GetBytes("from=com.tencent.mm&content=%E4%ZZ"), out _, out var formError)
            && formError.Length == 0);

        // ——— 多行正文：手机模板拼出的 JSON 会因此损坏，插件必须照样解析出 kind/title/subtitle ———
        // 手机端模板：{"title": "{{TITLE}}", "subtitle": "{{MSG}}", "kind": "{{APP_NAME}}"}
        // {{MSG}} 里出现换行时没有任何一方会替我们转义，JSON 字符串内部就多出裸换行 → 严格解析必然失败。
        string multilineJson = "{\"title\": \"标题\", \"subtitle\": \"第一行\n第二行\", \"kind\": \"微信\"}";
        Check("表单：多行 JSON 仍能解出 kind / title",
            NpsActivityEndpoint.TryParse(Encoding.UTF8.GetBytes("content=" + Uri.EscapeDataString(multilineJson)),
                out var m1, out _) && m1.Kind == "微信" && m1.Title == "标题",
            $"{m1.Kind} / {m1.Title} / {m1.Subtitle}");
        Check("表单：多行 JSON 的正文完整取到，且不含花括号 / 键名",
            m1.Subtitle == "第一行\n第二行" && !m1.Subtitle.Contains('{'), m1.Subtitle);
        Check("…转发时换行被净化为一个空格（灵动岛单行显示）",
            Shown(m1.Subtitle) == "第一行 第二行", Shown(m1.Subtitle));

        string blankLineJson = "{\"title\": \"标题\", \"subtitle\": \"第一行\n\n第三行\", \"kind\": \"微信\"}";
        Check("表单：正文含空行也能解析（连续换行折叠成一个空格）",
            NpsActivityEndpoint.TryParse(Encoding.UTF8.GetBytes("content=" + Uri.EscapeDataString(blankLineJson)),
                out var m2, out _) && m2.Kind == "微信" && Shown(m2.Subtitle) == "第一行 第三行", Shown(m2.Subtitle));

        string rawMultiline = "{\"kind\":\"微信\",\"title\":\"标题\",\"subtitle\":\"甲\n乙\"}";
        Check("JSON 请求体含裸换行也能解析",
            NpsActivityEndpoint.TryParse(Encoding.UTF8.GetBytes(rawMultiline), out var m3, out var lenientError)
            && m3.Kind == "微信" && m3.Subtitle == "甲\n乙", lenientError);
        Check("…且转发体里不再有裸换行（已净化为空格）",
            !NpsActivityEndpoint.BuildJson(m3).Contains('\n') && NpsActivityEndpoint.BuildJson(m3).Contains("甲 乙"),
            NpsActivityEndpoint.BuildJson(m3));

        string quotedJson = "{\"title\": \"标题\", \"subtitle\": \"他说\"你好\"\", \"kind\": \"微信\"}";
        Check("表单：正文含未转义双引号也能解析",
            NpsActivityEndpoint.TryParse(Encoding.UTF8.GetBytes("content=" + Uri.EscapeDataString(quotedJson)),
                out var m4, out _) && m4.Kind == "微信" && m4.Title == "标题" && m4.Subtitle == "他说\"你好\"",
            m4.Subtitle);

        string windowsPathJson = "{\"kind\":\"微信\",\"title\":\"路径\",\"subtitle\":\"C:\\Users\\logs\"}";
        Check("JSON 请求体含非法转义（Windows 路径）也能解析",
            NpsActivityEndpoint.TryParse(Encoding.UTF8.GetBytes(windowsPathJson), out var m5, out _)
            && m5.Subtitle == @"C:\Users\logs", m5.Subtitle);

        // ★★ content JSON 里的 kind 是「发送端显式指定的显示名」，必须**胜过** from 的包名 ★★
        // 用户手机模板：from=<app 包名> & content={"kind":"<中文软件名>", …}
        // 早前把 from 排在前面 → 中文名被包名盖掉，灵动岛上显示 com.smile.gifmaker 反而是常态。
        string packageKindJson = "{\"kind\":\"com.tencent.wework\",\"title\":\"t\",\"subtitle\":\"b\"}";
        Check("★ 表单：content 里的 kind 优先于 from=（显式显示名 > 包名推断）",
            NpsActivityEndpoint.TryParse(Encoding.UTF8.GetBytes(
                "from=com.tencent.mm&content=" + Uri.EscapeDataString(packageKindJson)), out var m7, out _)
            && m7.Kind == "企业微信", m7.Kind);
        Check("表单：没有 from= 时取 content 里的 kind，并翻译包名",
            NpsActivityEndpoint.TryParse(Encoding.UTF8.GetBytes(
                "content=" + Uri.EscapeDataString(packageKindJson)), out var m8, out _)
            && m8.Kind == "企业微信", m8.Kind);

        // ★ 用户的真实场景：中文软件名 必须是最终 kind，不能被 from 的包名覆盖
        Check("★ 真实场景：中文软件名不被 from 包名覆盖（快手）",
            NpsActivityEndpoint.TryParse(Encoding.UTF8.GetBytes(
                "from=com.smile.gifmaker&content=" + Uri.EscapeDataString(
                    "{\"kind\":\"快手\",\"title\":\"标题\",\"subtitle\":\"正文\"}")), out var m9, out _)
            && m9.Kind == "快手", m9.Kind);
        Check("★ 真实场景：中文软件名不被 from 包名覆盖（闲鱼）",
            NpsActivityEndpoint.TryParse(Encoding.UTF8.GetBytes(
                "from=com.taobao.idlefish&content=" + Uri.EscapeDataString(
                    "{\"kind\":\"闲鱼\",\"title\":\"标题\",\"subtitle\":\"正文\"}")), out var m10, out _)
            && m10.Kind == "闲鱼", m10.Kind);
        // content 里的 kind 若本身是包名，仍要翻译成中文（不能因为优先级变了就忘了翻译）
        Check("content 里的 kind 是包名时同样翻译成中文",
            NpsActivityEndpoint.TryParse(Encoding.UTF8.GetBytes(
                "from=com.foo.bar&content=" + Uri.EscapeDataString(
                    "{\"kind\":\"com.tencent.mobileqq\",\"title\":\"t\",\"subtitle\":\"b\"}")), out var m11, out _)
            && m11.Kind == "QQ", m11.Kind);
        // content 没带 kind 时才回退到 from
        Check("content 无 kind 时回退到 from 包名",
            NpsActivityEndpoint.TryParse(Encoding.UTF8.GetBytes(
                "from=com.tencent.mobileqq&content=" + Uri.EscapeDataString(
                    "{\"title\":\"t\",\"subtitle\":\"b\"}")), out var m12, out _)
            && m12.Kind == "QQ", m12.Kind);
        // app 是 kind 的别名写法，也要享受同一优先级
        Check("content 里的 app 字段与 kind 同权",
            NpsActivityEndpoint.TryParse(Encoding.UTF8.GetBytes(
                "from=com.tencent.mm&content=" + Uri.EscapeDataString(
                    "{\"app\":\"知乎\",\"title\":\"t\",\"subtitle\":\"b\"}")), out var m13, out _)
            && m13.Kind == "知乎", m13.Kind);

        // 兜底：修复后仍不成形的 JSON，也要嗅探出字段 —— 绝不允许把 {"title":…} 原样显示到灵动岛
        Check("兜底：截断的 JSON 仍能嗅探出字段",
            NpsActivityEndpoint.TryParse(Encoding.UTF8.GetBytes("{\"title\": \"标题\", \"subtitle\": \"正文无结尾\""),
                out var m6, out _) && m6.Title == "标题" && m6.Subtitle == "正文无结尾", m6.Subtitle);
        Check("兜底：嗅探不到任何字段时仍然拒绝（不弹空提示）",
            !NpsActivityEndpoint.TryParse(Encoding.UTF8.GetBytes("{oops"), out _, out _));
    }

    /// <summary>转发时的净化效果 —— 也就是灵动岛实际显示出来的文本。</summary>
    private static string Shown(string raw) => NpsActivityEndpoint.Sanitize(raw);

    /// <summary>复刻宿主 toast.cs 中对原始 JSON 的变换逻辑。</summary>
    private static string HostTransform(string raw) => raw.Replace("\\", "\\\\").Replace("\\\\\"", "\\\"");

    // ====================================================================
    // 3. 端到端网络链路
    // ====================================================================

    private static async Task TestEndToEndAsync()
    {
        Console.WriteLine();
        Console.WriteLine("【端到端：客户端 → 插件 → 宿主接口】");

        // 假的宿主接口：监听 127.0.0.1:47300 并记录收到的 JSON。
        // 若真实 NPS 正在运行（它同样占用 47300），则退化为「直接对真实宿主做端到端验证」：
        // 仍然验证 200/400/403/状态页/计数，只是无法再断言转发体的内容（此时会真实弹出灵动岛提示）。
        var received = new ConcurrentQueue<string>();
        HttpListener? mock = null;
        bool mockAvailable = false;
        try
        {
            mock = new HttpListener();
            mock.Prefixes.Add("http://127.0.0.1:47300/");
            mock.Start();
            mockAvailable = true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  [NOTE] 47300 已被占用，判定为真实 NPS 正在运行：{ex.Message}");
            Console.WriteLine("  [NOTE] 改为对真实宿主接口做端到端验证（转发体内容无法断言，且会真实弹出提示）");
            mock = null;
        }

        var mockListener = mock;
        var mockLoop = (mockAvailable && mockListener != null)
            ? Task.Run(async () =>
            {
                while (mockListener.IsListening)
                {
                    HttpListenerContext ctx;
                    try { ctx = await mockListener.GetContextAsync(); }
                    catch { break; }

                    using (var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8))
                        received.Enqueue(await reader.ReadToEndAsync());

                    var ok = Encoding.UTF8.GetBytes("{\"ok\":true}");
                    ctx.Response.StatusCode = 200;
                    ctx.Response.ContentType = "application/json";
                    ctx.Response.ContentLength64 = ok.Length;
                    await ctx.Response.OutputStream.WriteAsync(ok);
                    ctx.Response.Close();
                }
            })
            : Task.CompletedTask;

        var log = new List<string>();
        var whitelist = IpWhitelist.Parse("127.0.0.1");
        var iconOverrides = IconOverrides.Parse(null);
        // 应用黑名单：本段用例不放行任何应用（空名单 = 不拦截），
        // 拦截行为另有专门的用例覆盖。
        var appBlacklist = AppBlacklist.Parse(null);
        using var server = new HttpBridgeServer(BridgePort, () => whitelist, () => appBlacklist,
            () => iconOverrides, l => { lock (log) log.Add(l); }, _ => { });

        Check("插件服务可绑定端口", server.Start(out var startError), startError);
        Check("服务状态为运行中", server.IsRunning);

        using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{BridgePort}/"), Timeout = TimeSpan.FromSeconds(10) };

        // ① 正常推送（含中文 / 反斜杠 / 换行 / 引号）
        string payload = """{"kind":"手机消息","title":"测试标题","subtitle":"第一行\n第二行 C:\\temp\\x \"引号\""}""";
        using (var content = new StringContent(payload, Encoding.UTF8, "application/json"))
        using (var response = await client.PostAsync("", content))
        {
            string body = await response.Content.ReadAsStringAsync();
            Check("合法推送返回 HTTP 200", (int)response.StatusCode == 200, body);
            Check("响应体为 {\"ok\":true}", body.Replace(" ", "") == "{\"ok\":true}", body);
        }

        // 等待转发落地
        for (int i = 0; i < 40 && received.IsEmpty; i++) await Task.Delay(50);

        var forwardedBodies = received.ToArray();
        if (mockAvailable)
        {
            Check("宿主接口收到 1 条转发", forwardedBodies.Length == 1, $"实际 {forwardedBodies.Length}");

            if (forwardedBodies.Length >= 1)
            {
                string forwarded = HostTransform(forwardedBodies[0]);
                using var doc = JsonDocument.Parse(forwarded);
                var root = doc.RootElement;
                Check("转发 kind = 手机消息", root.GetProperty("kind").GetString() == "手机消息");
                Check("转发 title = 测试标题", root.GetProperty("title").GetString() == "测试标题");
                Check("转发正文换行已转空格、反斜杠已净化、引号保留",
                    root.GetProperty("subtitle").GetString() == "第一行 第二行 C:/temp/x \"引号\"",
                    root.GetProperty("subtitle").GetString());
            }
        }
        else
        {
            Console.WriteLine("  [SKIP] 无假宿主，转发体内容断言跳过（链路已由 200 响应证明）");
        }

        Check("统计：接收 1 / 转发 1", server.Received == 1 && server.Forwarded == 1, $"接收 {server.Received} 转发 {server.Forwarded}");
        Check("记录到最近来源 IP", server.LastRemoteIp == "127.0.0.1", server.LastRemoteIp);

        // ①-b ★ 按 kind 二次补全 icon：真跑一遍插件 → 宿主 的转发链路 ★
        //     订阅转发体（而不是只看假宿主收到的原始文本），以验证「补全发生在转发之前」。
        if (mockAvailable)
        {
            received.Clear();

            // 「微信」在宿主里没有别名，内置映射给它的是图片链接 → 应补上 icon 字段
            string wechatPayload = """{"kind":"微信","title":"张三","subtitle":"在吗"}""";
            using (var content = new StringContent(wechatPayload, Encoding.UTF8, "application/json"))
            using (var response = await client.PostAsync("", content))
                Check("kind=微信 的推送返回 200", (int)response.StatusCode == 200);

            for (int i = 0; i < 40 && received.IsEmpty; i++) await Task.Delay(50);

            var wechatBodies = received.ToArray();
            if (wechatBodies.Length >= 1)
            {
                using var doc = JsonDocument.Parse(HostTransform(wechatBodies[0]));
                var root = doc.RootElement;
                Check("微信推送被补上了 icon 字段", root.TryGetProperty("icon", out _));
                Check("补的是内置图片链接",
                    root.TryGetProperty("icon", out var iconEl)
                    && (iconEl.GetString() ?? "").StartsWith("https://mdn.alipayobjects.com/"),
                    root.TryGetProperty("icon", out var i2) ? i2.GetString() ?? "" : "(无 icon)");
                Check("补全不影响原有三字段",
                    root.GetProperty("kind").GetString() == "微信"
                    && root.GetProperty("title").GetString() == "张三"
                    && root.GetProperty("subtitle").GetString() == "在吗");
            }
            else
            {
                Check("微信推送被转发到宿主", false, $"收到 {wechatBodies.Length} 条");
            }

            // 「qq」有内置别名 → 补别名（零网络成本路径）
            received.Clear();
            string qqPayload = """{"kind":"qq","title":"李四","subtitle":"你好"}""";
            using (var content = new StringContent(qqPayload, Encoding.UTF8, "application/json"))
            using (var response = await client.PostAsync("", content))
                Check("kind=qq 的推送返回 200", (int)response.StatusCode == 200);

            for (int i = 0; i < 40 && received.IsEmpty; i++) await Task.Delay(50);

            var qqBodies = received.ToArray();
            if (qqBodies.Length >= 1)
            {
                using var doc = JsonDocument.Parse(HostTransform(qqBodies[0]));
                Check("qq 推送补的是内置别名 qq",
                    doc.RootElement.TryGetProperty("icon", out var iconEl) && iconEl.GetString() == "qq",
                    doc.RootElement.TryGetProperty("icon", out var i3) ? i3.GetString() ?? "" : "(无 icon)");
            }

            // ★ 兼容性关键：完全未命中的 kind → 请求体里**不能有** icon 字段
            received.Clear();
            string plainPayload = """{"kind":"未知应用xyz","title":"王五","subtitle":"喂"}""";
            using (var content = new StringContent(plainPayload, Encoding.UTF8, "application/json"))
            using (var response = await client.PostAsync("", content))
                Check("未命中 kind 的推送返回 200", (int)response.StatusCode == 200);

            for (int i = 0; i < 40 && received.IsEmpty; i++) await Task.Delay(50);

            var plainBodies = received.ToArray();
            if (plainBodies.Length >= 1)
            {
                string forwardedPlain = HostTransform(plainBodies[0]);
                Check("★ 未命中时不输出 icon 字段（旧宿主兼容）",
                    !forwardedPlain.Contains("icon"), forwardedPlain);

                using var doc = JsonDocument.Parse(forwardedPlain);
                Check("未命中时请求体仍是三字段形态",
                    doc.RootElement.EnumerateObject().Count() == 3,
                    string.Join(",", doc.RootElement.EnumerateObject().Select(p => p.Name)));
            }

            // 入站自带 icon → 原样透传，不被内置映射覆盖
            received.Clear();
            string inboundPayload = """{"kind":"微信","title":"赵六","subtitle":"hi","icon":"my-custom-alias"}""";
            using (var content = new StringContent(inboundPayload, Encoding.UTF8, "application/json"))
            using (var response = await client.PostAsync("", content))
                Check("入站带 icon 的推送返回 200", (int)response.StatusCode == 200);

            for (int i = 0; i < 40 && received.IsEmpty; i++) await Task.Delay(50);

            var inboundBodies = received.ToArray();
            if (inboundBodies.Length >= 1)
            {
                using var doc = JsonDocument.Parse(HostTransform(inboundBodies[0]));
                Check("★ 入站 icon 原样透传（不被内置图片链接覆盖）",
                    doc.RootElement.TryGetProperty("icon", out var iconEl)
                    && iconEl.GetString() == "my-custom-alias",
                    doc.RootElement.TryGetProperty("icon", out var i4) ? i4.GetString() ?? "" : "(无 icon)");
            }

            Check("日志里记下了图标来源", log.Any(l => l.Contains("图标：")),
                string.Join(" | ", log.Where(l => l.Contains("图标")).Take(3)));
        }
        else
        {
            Console.WriteLine("  [SKIP] 无假宿主，icon 转发体断言跳过");
        }

        // ①-c ★ 应用黑名单：命中的消息**不转发**（静默丢弃），对发送端仍返回成功 ★
        //     这一节能守住三条彼此独立的约定：
        //       ① 不上岛（宿主确实收不到）；
        //       ② 发送端看到的是成功（手机端不会把它当故障而重试）；
        //       ③ 状态灯**不变橙**（拦截是预期行为，不是「服务出问题」）。
        if (mockAvailable)
        {
            received.Clear();
            long blockedBefore = server.Blocked;
            bool unhealthyBefore = server.Unhealthy;
            int logCountBefore = log.Count;

            // 名单里放「微信」——只拦它，别的应用照常
            appBlacklist = AppBlacklist.Parse("微信");

            string blockedPayload = """{"kind":"微信","title":"张三","subtitle":"这条不该上岛"}""";
            using (var content = new StringContent(blockedPayload, Encoding.UTF8, "application/json"))
            using (var response = await client.PostAsync("", content))
            {
                string body = await response.Content.ReadAsStringAsync();
                Check("★ 黑名单命中：对发送端仍返回 200（不让手机端当成失败重试）",
                    (int)response.StatusCode == 200, body);
                Check("★ 黑名单命中：响应体带 blocked 标记，与普通成功可区分",
                    body.Replace(" ", "").Contains("\"blocked\":true"), body);
            }

            // 给「本该发生的转发」留足时间：等不到，才说明它确实没发生（反证）
            for (int i = 0; i < 8 && received.IsEmpty; i++) await Task.Delay(50);
            Check("★ 黑名单命中：宿主**没有**收到这条（不上岛）", received.IsEmpty,
                $"实际收到 {received.Count} 条");
            Check("★ 黑名单命中：Blocked 计数 +1", server.Blocked == blockedBefore + 1,
                $"{blockedBefore} → {server.Blocked}");
            Check("★ 黑名单命中：状态灯不因拦截变橙（拦截是预期行为，不是故障）",
                server.Unhealthy == unhealthyBefore, $"{unhealthyBefore} → {server.Unhealthy}");
            Check("★ 黑名单命中：日志写明被拦的是哪个应用（用户能确认拦对了）",
                log.Skip(logCountBefore).Any(l => l.Contains("已拦截") && l.Contains("微信")),
                string.Join(" | ", log.Skip(logCountBefore).Take(3)));

            // 反证：不在名单里的应用照旧上岛 —— 黑名单不是「全拦」
            received.Clear();
            string passPayload = """{"kind":"QQ","title":"李四","subtitle":"这条应该上岛"}""";
            using (var content = new StringContent(passPayload, Encoding.UTF8, "application/json"))
            using (var response = await client.PostAsync("", content))
                Check("未命中的应用仍返回 200", (int)response.StatusCode == 200);

            for (int i = 0; i < 40 && received.IsEmpty; i++) await Task.Delay(50);
            Check("★ 反证：不在名单里的应用照常上岛", !received.IsEmpty,
                $"收到 {received.Count} 条");

            // 包名也要能匹配（手机模板的 from 是包名，显示名可能被翻译过）
            received.Clear();
            appBlacklist = AppBlacklist.Parse("com.tencent.mm");
            string pkgPayload = """{"kind":"微信","title":"王五","subtitle":"按包名拦","package":"com.tencent.mm"}""";
            using (var content = new StringContent(pkgPayload, Encoding.UTF8, "application/json"))
            using (var response = await client.PostAsync("", content))
                Check("按包名拦截：仍返回 200", (int)response.StatusCode == 200);

            for (int i = 0; i < 8 && received.IsEmpty; i++) await Task.Delay(50);
            Check("★ 按包名拦截：kind 是「微信」而名单写的是包名 —— 依然拦住（包名也参与匹配）",
                received.IsEmpty, $"实际收到 {received.Count} 条");

            // 反证②：**表单格式**（手机端真实使用的格式）同样要能正常上岛 / 被拦。
            // 手机端就是 `from=<包名>&content={"kind":"<中文名>",…}`，与 JSON 走的是同一个 TryParse，
            // 但两条分支各自填字段，必须分别验证 —— 只测 JSON 会漏掉表单分支的回归。
            received.Clear();
            appBlacklist = AppBlacklist.Parse("微信");

            string formOk = "from=com.tencent.mobileqq&content=" + Uri.EscapeDataString(
                """{"title":"群消息","subtitle":"表单格式","kind":"QQ"}""");
            using (var content = new StringContent(formOk, Encoding.UTF8, "application/x-www-form-urlencoded"))
            using (var response = await client.PostAsync("", content))
                Check("表单格式（非黑名单）返回 200", (int)response.StatusCode == 200);

            for (int i = 0; i < 40 && received.IsEmpty; i++) await Task.Delay(50);
            Check("★ 表单格式：非黑名单应用照常上岛",
                !received.IsEmpty, $"收到 {received.Count} 条");

            if (!received.IsEmpty)
            {
                using var doc = JsonDocument.Parse(HostTransform(received.ToArray()[0]));
                Check("表单格式：kind 取的是 content 里的中文名「QQ」（不是 from 的包名）",
                    doc.RootElement.GetProperty("kind").GetString() == "QQ",
                    doc.RootElement.GetProperty("kind").GetString());
            }

            received.Clear();
            string formBlocked = "from=com.tencent.mm&content=" + Uri.EscapeDataString(
                """{"title":"张三","subtitle":"表单格式","kind":"微信"}""");
            using (var content = new StringContent(formBlocked, Encoding.UTF8, "application/x-www-form-urlencoded"))
            using (var response = await client.PostAsync("", content))
                Check("表单格式（黑名单）返回 200", (int)response.StatusCode == 200);

            for (int i = 0; i < 8 && received.IsEmpty; i++) await Task.Delay(50);
            Check("★ 表单格式：黑名单应用不上岛", received.IsEmpty, $"收到 {received.Count} 条");

            appBlacklist = AppBlacklist.Parse(null);   // 复原：后续用例不受影响
            Check("复原：空名单后不再拦截", !appBlacklist.IsBlocked("com.tencent.mm", "微信"));
        }
        else
        {
            Console.WriteLine("  [SKIP] 无假宿主，黑名单拦截断言跳过");
        }

        // ② 空请求体 → 400
        using (var emptyContent = new StringContent("", Encoding.UTF8, "application/json"))
        using (var response = await client.PostAsync("", emptyContent))
            Check("空请求体返回 400", (int)response.StatusCode == 400, ((int)response.StatusCode).ToString());

        // ③ 非法 JSON → 400
        using (var badContent = new StringContent("{oops", Encoding.UTF8, "application/json"))
        using (var response = await client.PostAsync("", badContent))
            Check("非法 JSON 返回 400", (int)response.StatusCode == 400, ((int)response.StatusCode).ToString());

        // ④ GET / 状态页
        using (var response = await client.GetAsync(""))
        {
            string body = await response.Content.ReadAsStringAsync();
            Check("状态页返回 200 且包含服务信息",
                (int)response.StatusCode == 200 && body.Contains("NpsPushBridge") && body.Contains(BridgePort.ToString()),
                body.Split('\n').FirstOrDefault() ?? "");
        }

        Check("失败计数 = 2", server.Failed == 2, server.Failed.ToString());

        // ⑤ 白名单拒绝：换一个只放行 10.99.99.99 的服务，本机 127.0.0.1 应被 403
        var strictWhitelist = IpWhitelist.Parse("10.99.99.99");
        using var strictServer = new HttpBridgeServer(RejectPort, () => strictWhitelist,
            () => AppBlacklist.Parse(null), () => IconOverrides.Parse(null), _ => { }, _ => { });
        Check("受限服务可绑定端口", strictServer.Start(out var strictError), strictError);

        using (var strictClient = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{RejectPort}/"), Timeout = TimeSpan.FromSeconds(10) })
        using (var content = new StringContent(payload, Encoding.UTF8, "application/json"))
        using (var response = await strictClient.PostAsync("", content))
        {
            string body = await response.Content.ReadAsStringAsync();
            Check("非白名单来源返回 403", (int)response.StatusCode == 403, ((int)response.StatusCode).ToString());
            Check("403 响应体带 ip_not_allowed", body.Contains("ip_not_allowed"), body);
        }

        Check("拒绝计数 = 1", strictServer.Rejected == 1, strictServer.Rejected.ToString());
        Check("★ 403 不置位健康标记（被拒 ≠ 服务不健康；否则用户每次开状态页自检都会把灯弄橙）",
            !strictServer.Unhealthy);
        Check("被拒绝的来源仍被记录（可一键加白）", strictServer.LastRemoteIp == "127.0.0.1", strictServer.LastRemoteIp);

        // ⑥ 新格式端到端：from=<应用包名>&content=<URL 编码的 JSON 正文>
        const string formPayload =
            "from=com.tencent.mm&content=%7B%22title%22%3A%22%E7%BE%A4%E5%90%8D%22%2C%22text%22%3A%22%E4%BD%A0%E5%A5%BD%EF%BC%8C%E4%B8%96%E7%95%8C%22%7D";
        using (var formContent = new StringContent(formPayload, Encoding.UTF8, "application/x-www-form-urlencoded"))
        using (var response = await client.PostAsync("", formContent))
            Check("表单推送返回 HTTP 200", (int)response.StatusCode == 200, ((int)response.StatusCode).ToString());

        for (int i = 0; i < 40 && received.Count < 2; i++) await Task.Delay(50);

        var all = received.ToArray();
        if (!mockAvailable)
        {
            Console.WriteLine("  [SKIP] 无假宿主，表单转发体断言跳过（链路已由 200 响应证明）");
        }
        else if (all.Length >= 2)
        {
            using var doc = JsonDocument.Parse(HostTransform(all[1]));
            var root = doc.RootElement;
            Check("表单推送 kind 已换成中文应用名", root.GetProperty("kind").GetString() == "微信", root.GetProperty("kind").GetString());
            Check("表单推送 title 解析正确", root.GetProperty("title").GetString() == "群名", root.GetProperty("title").GetString());
            Check("表单推送正文已解码，无 %7B%22 乱码",
                root.GetProperty("subtitle").GetString() == "你好，世界", root.GetProperty("subtitle").GetString());
        }
        else
        {
            Check("宿主接口收到表单转发", false, $"实际 {all.Length} 条");
        }

        // ⑦ 多行正文端到端：手机模板 → 表单 → 插件解析 → 转发。
        //    这里断言「插件日志」里的解析结果，因此不依赖假宿主是否可用（真实 NPS 在跑时也能验证）。
        string multilinePayload = "content=" + Uri.EscapeDataString(
            "{\"title\": \"群名\", \"subtitle\": \"第一行\n第二行\", \"kind\": \"微信\"}");

        int logMark;
        lock (log) logMark = log.Count;

        using (var mlContent = new StringContent(multilinePayload, Encoding.UTF8, "application/x-www-form-urlencoded"))
        using (var response = await client.PostAsync("", mlContent))
            Check("多行正文推送返回 HTTP 200", (int)response.StatusCode == 200, ((int)response.StatusCode).ToString());

        string? forwardedLog = null;
        for (int i = 0; i < 40 && forwardedLog == null; i++)
        {
            await Task.Delay(50);
            lock (log) forwardedLog = log.Skip(logMark).FirstOrDefault(l => l.Contains("→ ["));
        }
        // 说明：kind=微信 会命中内置图片链接，因此转发日志末尾会多出「 · 图标: <链接>」。
        // 这里只断言「正文折叠 + 前缀部分」——图标后缀由【图标映射与补全】组单独覆盖。
        Check("多行正文经真实 HTTP 链路解析后转发（日志仍是一条一行）",
            forwardedLog != null && forwardedLog.StartsWith("来自 127.0.0.1 → [微信] 群名 — 第一行 第二行"),
            forwardedLog ?? "(未产生转发日志)");

        // ⑧ 状态灯健康标记（端到端）：走真实 HTTP 链路验证「橙 → 绿」自愈。
        //    放在最后做，避免新增的成功转发打乱前面按下标断言 forwarded 的顺序。
        //    此刻刚有过一次成功转发（⑦），标记应为洁净 —— 正好作为干净的起点。
        Check("起点：上一次成功转发已清场，健康标记为洁净（灯绿）", !server.Unhealthy);

        using (var bad1 = new StringContent("{oops", Encoding.UTF8, "application/json"))
        using (var response = await client.PostAsync("", bad1))
            Check("请求无效仍返回 400", (int)response.StatusCode == 400, ((int)response.StatusCode).ToString());

        Check("★ 请求无效后健康标记置位（灯应转橙）", server.Unhealthy);

        string pageWhileOrange;
        using (var response = await client.GetAsync(""))
            pageWhileOrange = await response.Content.ReadAsStringAsync();
        Check("状态页与状态灯同源：显示为橙，并给出最近失败原因",
            pageWhileOrange.Contains("状态灯  : 橙") && pageWhileOrange.Contains("最近失败"),
            pageWhileOrange.Split('\n').FirstOrDefault(l => l.StartsWith("状态灯")) ?? "(无状态灯行)");

        // 再失败一次：标记保持置位（累计计数会 +1，但状态不变 —— 统计 ≠ 状态）
        using (var bad2 = new StringContent("", Encoding.UTF8, "application/json"))
        using (var response = await client.PostAsync("", bad2))
            Check("空请求体仍返回 400", (int)response.StatusCode == 400, ((int)response.StatusCode).ToString());

        Check("连续两次失败后仍为橙（不因次数叠加而改变状态）", server.Unhealthy);

        // ★ 关键：一次成功转发必须让灯回绿 —— 旧版读累计计数，这里会永久停在橙 ★
        using (var good = new StringContent(payload, Encoding.UTF8, "application/json"))
        using (var response = await client.PostAsync("", good))
            Check("恢复用的合法推送返回 200", (int)response.StatusCode == 200, ((int)response.StatusCode).ToString());

        Check("★★ 成功转发一次即自愈（灯回绿，而不是永久橙）", !server.Unhealthy);

        string pageAfterRecovery;
        using (var response = await client.GetAsync(""))
            pageAfterRecovery = await response.Content.ReadAsStringAsync();
        Check("状态页同步回绿，并注明灯已恢复",
            pageAfterRecovery.Contains("状态灯  : 绿") && pageAfterRecovery.Contains("灯已回绿"),
            pageAfterRecovery.Split('\n').FirstOrDefault(l => l.StartsWith("状态灯")) ?? "(无状态灯行)");

        Check("累计失败计数只增不减，与「是否未恢复」分离（统计 ≠ 状态）",
            server.Failed == 4, $"失败 {server.Failed} / 未恢复 {server.Unhealthy}");

        // ⑨ 停止服务
        server.Stop();
        Check("停止后状态为未运行", !server.IsRunning);
        Check("停止后端口可被重新占用", await TryBindAsync(BridgePort));

        // 收尾
        if (mockAvailable) mock?.Stop();
        await Task.WhenAny(mockLoop, Task.Delay(1000));
    }

    private static async Task<bool> TryBindAsync(int port)
    {
        try
        {
            var probe = new System.Net.Sockets.TcpListener(IPAddress.Any, port);
            probe.Start();
            probe.Stop();
            await Task.CompletedTask;
            return true;
        }
        catch { return false; }
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
