using NotchPeninsula.Plugins;
using SkiaSharp;

namespace NpsPushBridge;

/// <summary>
/// 状态灯右键展开的「配置详情页」：一小块卡片，展示运行摘要 + 「打开完整配置」按钮。
///
/// ── 为什么要有这个详情页（这是它存在的全部理由）────────────────────────
/// 宿主的岛内右键优先级是「收起详情页 → 广播给命中的插件组件 → 打开宿主设置窗口」
/// （见宿主 <c>NotchWindow</c> 的 <c>WM_RBUTTONDOWN</c>）。而广播路径里，
/// 宿主对命中的组件**无条件调用 <c>OnRightClick</c>**，却**只有在组件 <c>DetailPage</c> 非空时
/// 才「消费」这次右键**（否则继续打开设置窗口）。
///
/// 状态灯此前 <c>DetailPage = null</c>，于是右键状态灯会同时触发两件事：
///   ① 插件 <c>OnRightClick</c> 打开独立配置窗口；
///   ② 宿主因「无详情页」继续 <c>ConsoleWindow.Toggle()</c> 打开设置窗口 —— 两个窗口叠加。
///
/// 给状态灯一个**非空**详情页后，宿主会改为「展开详情页」并**消费掉这次右键**，
/// 不再打开设置窗口 —— 冲突消除，且**完全不修改宿主源码**。
///
/// ── 详情页的内容 ─────────────────────────────────────────────────────
/// 顺带承接了早先从岛上移入配置界面的那行状态摘要（端口 / 已转发），
/// 让用户右键时先看到状态，再决定是否打开完整配置（独立窗口）。
/// 交互：点状态区 = 开关服务；点「打开完整配置」按钮 = 打开独立窗口。
/// 再次右键（或点岛外）由宿主收起详情页，回到状态灯。
/// </summary>
internal sealed class ConfigDetailPage : IDetailPage
{
    private readonly IWidgetStatus _status;

    public ConfigDetailPage(IWidgetStatus status) => _status = status;

    // 尺寸在宿主详情页上下限（宽 180..1000、高 48..480）之内。
    private const float Width = 232f;
    private const float Height = 48f;

    /// <summary>第一行（状态区）的高度；以下是「打开完整配置」按钮。</summary>
    private const float StatusRowHeight = 24f;

    private const float DotRadius = 4f;
    private const float PadX = 10f;

    public float MeasureWidth() => Width;
    public float MeasureHeight() => Height;

    /// <summary>
    /// 宿主详情页的自动收起延迟 —— 直接透传插件配置（见 <see cref="IWidgetStatus.DetailAutoCollapseDelay"/>）。
    ///
    /// ★ 为什么需要它 ★
    /// 宿主默认 900ms 后自动收起。用户在配置界面里把「面板显示时长」调长（或勾选
    /// 「忽略外部鼠标操作」）后，这里如实返回，宿主就会照做 ——
    /// 这是**唯一**由插件控制、且宿主真的会读的时长；
    /// 相比之下 <c>ReminderData.Duration</c> 被宿主完全忽略（见 CollapseDuration 的说明）。
    ///
    /// 宿主在鼠标离开时每帧读一次，所以这里只做一次属性读取、不加锁不做 IO。
    /// </summary>
    public TimeSpan AutoCollapseDelay => _status.DetailAutoCollapseDelay;

    /// <summary>
    /// 绘制。
    ///
    /// ⚠️ **两个坐标陷阱，任一踩中都会让展开动画不顺滑**：
    /// ① 宿主传的 <c>rect</c> 是**岛内绝对坐标**（<c>Left</c> 不一定为 0）；
    /// ② <c>rect</c> 在展开 / 收起**弹簧动画期间只是「插值尺寸」**，不是目标尺寸
    ///    （目标 232×48，动画前几帧可能只有 130×24，甚至高度为 0）。
    ///
    /// 所以这里**不拿 rect 当排版基准**：一律按 <see cref="Width"/> × <see cref="Height"/>
    /// 这个固定坐标系排版，绘制前整体 <c>Translate</c> + <c>Scale</c> 到当前 <c>rect</c>。
    /// 这样动画期间内容随面板一起等比长大（不再出现「按钮被算成负高度、文字每帧跳位」），
    /// 展开完成后 <c>scale = 1</c>，与设计尺寸 1:1。这是宿主插件文档明确要求的写法。
    /// </summary>
    public void Draw(SKCanvas canvas, SKRect rect, WidgetFrame frame)
    {
        var snapshot = _status.Snapshot();
        var state = LightStatus.Decide(snapshot.ServiceRunning, _status.Unhealthy);
        var (r, g, b) = LightStatus.Rgb(state);
        byte alpha = frame.Alpha;

        // 弹簧动画有过冲，scale 封顶 1 免得边缘被裁；<=0 / NaN（动画起点）直接不画。
        float scale = Math.Min(1f, Math.Min(rect.Width / Width, rect.Height / Height));
        if (!(scale > 0f)) return;

        canvas.Save();
        canvas.Translate(rect.MidX, rect.MidY);
        canvas.Scale(scale);
        canvas.Translate(-Width * 0.5f, -Height * 0.5f);

        // ============ 以下全部按 (0, 0, Width, Height) 这个固定坐标系绘制 ============

        // 第一行：状态灯圆点 + 状态摘要
        float dotY = StatusRowHeight / 2f;
        using (var dot = new SKPaint { IsAntialias = true })
        {
            dot.Color = new SKColor(r, g, b).WithAlpha(alpha);
            canvas.DrawCircle(PadX + DotRadius, dotY, DotRadius, dot);
        }

        string summary = $"{(snapshot.ServiceRunning ? "运行中" : "已停止")} · 端口 {snapshot.Port} · 已转发 {snapshot.Forwarded}";
        var summaryRect = new SKRect(PadX + DotRadius * 2f + 6f, 0f, Width - PadX, StatusRowHeight);
        using (var text = PluginFonts.IslandPaint(11f, frame.Theme.TextColor.WithAlpha(alpha)))
        {
            PluginFonts.Fit(text, summary);
            DrawVerticallyCentered(canvas, summary, summaryRect, text);
        }

        // 第二行：「打开完整配置」按钮（圆角块，整行可点）
        var btnRect = new SKRect(PadX, StatusRowHeight + 1f, Width - PadX, Height - 2f);
        using (var btnFill = new SKPaint { IsAntialias = true })
        {
            // ★ 基础半透明白（alpha 24）按 frame.Alpha 等比缩放，得到淡入时的正确透明度。
            // 绝不能写成 `new SKColor(255,255,255,24).WithAlpha(alpha)` —— WithAlpha 是**替换** alpha，
            // 会把 24 覆盖成 frame.Alpha（稳态 255）= 纯白，白色文字画上去就「白字白底」看不见。
            byte bgAlpha = (byte)(24 * alpha / 255);
            btnFill.Color = new SKColor(255, 255, 255, bgAlpha);
            canvas.DrawRoundRect(btnRect, 6f, 6f, btnFill);
        }

        const string label = "打开完整配置";
        using (var btnText = PluginFonts.IslandPaint(11f, frame.Theme.TextColor.WithAlpha(alpha)))
        {
            PluginFonts.Fit(btnText, label);
            // 水平居中 + 垂直居中
            float tw = btnText.MeasureText(label);
            float tx = btnRect.Left + Math.Max(0f, (btnRect.Width - tw) / 2f);
            DrawVerticallyCentered(canvas, label, btnRect, btnText, x: tx);
        }

        canvas.Restore();
    }

    /// <summary>
    /// 命中：⚠️ 宿主传的 <c>x/y</c> 是**相对 rect 左上角**的逻辑坐标（与 <see cref="IWidget.HitTest"/> 一致）。
    /// 按**高度比例**划分两行（而不是拿固定的 24px 去比）—— 绘制是等比缩放到 <c>rect</c> 的，
    /// 所以在展开动画期间（<c>rect</c> 为插值尺寸）判定仍与看到的内容对得上。
    /// 第一行（上 1/2）= 开关服务；第二行 = 打开完整配置。
    /// </summary>
    public WidgetHit HitTest(float x, float y, SKRect rect)
        => y < rect.Height * (StatusRowHeight / Height) ? new WidgetHit("toggle") : new WidgetHit("open");

    public void OnAction(string? action, float x, float y)
    {
        switch (action)
        {
            case "open": _status.OpenConfig(); break;
            case "toggle": _status.ToggleService(); break;
        }
    }

    /// <summary>在 <paramref name="rect"/> 内垂直居中画一行文字（基线按字体度量精确计算）。</summary>
    private static void DrawVerticallyCentered(SKCanvas canvas, string text, SKRect rect, SKPaint paint, float? x = null)
    {
        float baseline = rect.MidY - (paint.FontMetrics.Ascent + paint.FontMetrics.Descent) / 2f;
        float left = x ?? rect.Left;
        canvas.DrawText(text, left, baseline, paint);
    }
}
