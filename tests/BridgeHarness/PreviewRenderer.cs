using System;
using System.IO;
using NpsPushBridge;
using SkiaSharp;

namespace BridgeHarness;

/// <summary>
/// 把 <see cref="ConfigPanel"/> 直接渲染成 PNG —— 用于**人工核对界面观感**。
///
/// ★ 为什么需要它 ★
/// 面板是 SkiaSharp 全自绘的，改配色 / 控件样式（例如 2026-09-25 的 WinUI 3 风格改造）后，
/// 只靠单元测试能确认「几何没错」，却看不出「好不好看」。这里用与测试相同的
/// <c>PanelSmokeTests.FakeHost</c> 填一份有内容的示例数据，把界面渲染出来直接看。
///
/// 入口：<c>BridgeHarness.exe --preview &lt;输出目录&gt;</c>（见 Program.Main）。
/// </summary>
internal static class PreviewRenderer
{
    public static void Render(string outDir)
    {
        Directory.CreateDirectory(outDir);

        const int W = 660, H = 620;

        // ── ① 主页面（常态）──
        Save(RenderMain(W, H, hoverSaveButton: false, out _), Path.Combine(outDir, "01-主页面.png"));

        // ── ② 主页面（鼠标停在「保存配置」上 → 展示 WinUI 3 的悬停高亮）──
        Save(RenderMain(W, H, hoverSaveButton: true, out _), Path.Combine(outDir, "02-主页面-悬停态.png"));

        // ── ③ 图标映射页 ──
        Save(RenderIconMap(W, H, out _), Path.Combine(outDir, "03-图标映射页.png"));

        // ── ④ 窄窗口（验证 WinUI 样式在最小宽度下不塌）──
        Save(RenderMain(360, H, hoverSaveButton: false, out _), Path.Combine(outDir, "04-窄窗口.png"));

        // ── ⑤ 应用黑名单页 ──
        Save(RenderBlacklist(W, H, out _), Path.Combine(outDir, "05-应用黑名单页.png"));

        Console.WriteLine($"界面预览已输出到: {outDir}");
    }

    private static SKSurface RenderMain(int w, int h, bool hoverSaveButton, out ConfigPanel panel)
    {
        var host = SampleHost();
        panel = new ConfigPanel(host, () => { });

        var surface = SKSurface.Create(new SKImageInfo(w, h));
        Draw(panel, surface, w, h);

        if (hoverSaveButton)
        {
            var btn = ConfigLayout.Compute(w, h).SaveButton;
            panel.OnMouseMove(btn.MidX, btn.MidY);
            // 悬停是 150ms 的渐变动画，等它走完才看得到「完全悬停」的配色
            System.Threading.Thread.Sleep(220);
            Draw(panel, surface, w, h);
        }
        return surface;
    }

    private static SKSurface RenderIconMap(int w, int h, out ConfigPanel panel)
    {
        var host = SampleHost();
        panel = new ConfigPanel(host, () => { });

        var surface = SKSurface.Create(new SKImageInfo(w, h));
        Draw(panel, surface, w, h);

        // 点「图标映射」切到映射页（与真实交互同一条路径）
        var btn = ConfigLayout.Compute(w, h).IconMapButton;
        panel.OnMouseDown(btn.MidX, btn.MidY);
        panel.OnMouseUp(btn.MidX, btn.MidY);
        Draw(panel, surface, w, h);
        return surface;
    }

    private static SKSurface RenderBlacklist(int w, int h, out ConfigPanel panel)
    {
        var host = SampleHost();
        panel = new ConfigPanel(host, () => { });

        var surface = SKSurface.Create(new SKImageInfo(w, h));
        Draw(panel, surface, w, h);

        // 点标题行的「黑名单」切到该页（与真实交互同一条路径）
        var btn = ConfigLayout.BlacklistButtonOf(ConfigLayout.Compute(w, h));
        panel.OnMouseDown(btn.MidX, btn.MidY);
        panel.OnMouseUp(btn.MidX, btn.MidY);
        Draw(panel, surface, w, h);
        return surface;
    }

    private static void Draw(ConfigPanel panel, SKSurface surface, int w, int h)
    {
        using var canvas = surface.Canvas;   // 不释放 surface，只借画布
        panel.Draw(surface.Canvas, new SKRect(0, 0, w, h), PanelTheme.Window, true);
    }

    private static void Save(SKSurface surface, string path)
    {
        using var img = surface.Snapshot();
        using var data = img.Encode(SKEncodedImageFormat.Png, 100);
        File.WriteAllBytes(path, data.ToArray());
        surface.Dispose();
        Console.WriteLine($"  ✓ {Path.GetFileName(path)}");
    }

    /// <summary>一份「正在运行且有内容」的示例数据（空界面看不出样式差异）。</summary>
    private static PanelSmokeTests.FakeHost SampleHost() => new()
    {
        WhitelistRaw = "192.168.1.23\n192.168.1.*\n10.0.0.0/24",
        Port = 1403,
        ServiceRunning = true,
        Received = 27, Forwarded = 25, Rejected = 1, Failed = 1,
        LastIp = "192.168.1.23",
        LastError = null,
        // 本机地址（「手机端填」那一行显示的地址，见 PhoneAddress）
        LocalIps = new[] { "192.168.1.23" },
        LogLines = new[]
        {
            "22:41:07 服务已启动：监听 0.0.0.0:1403",
            "22:42:15 来自 192.168.1.23 → [微信] 你有一条新消息",
            "22:43:02 来自 192.168.1.30 → [短信] 验证码 8842，5 分钟内有效",
            "22:44:19 拒绝来自 192.168.1.88 的请求（不在白名单）",
            "22:45:33 来自 192.168.1.23 → [快手] 有人 @ 了你",
        },
        IconRaw = "微信=https://nps.georgewu.top/logo.ico\n短信=bilibili",
        // 应用黑名单页要显示的内容（一条应用名 + 一条包名 + 注释 + 一条通配）
        AppBlacklistRawText = "营销短信\ncom.tencent.mobileqq\n# 双十一期间临时屏蔽\ncom.taobao.*",
    };
}
