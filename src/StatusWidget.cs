using NotchPeninsula.Plugins;
using SkiaSharp;

namespace NpsPushBridge;

/// <summary>灵动岛组件读取的插件状态视图（只关心「服务在不在跑」与「此刻健不健康」）。</summary>
internal interface IWidgetStatus
{
    /// <summary>推送服务是否正在运行。</summary>
    bool ServiceRunning { get; }

    /// <summary>是否有**尚未恢复**的失败（成功转发一次即清除）。</summary>
    bool Unhealthy { get; }

    void ToggleService();
    void OpenConfig();

    /// <summary>
    /// 宿主详情页（右键状态灯展开的「配置摘要」卡片）的**自动收起延迟**。
    ///
    /// 直接对应宿主的 <c>IDetailPage.AutoCollapseDelay</c>：
    /// <see cref="TimeSpan.Zero"/> = 用宿主内置的 **900ms**；**正数** = 自定义（宿主夹到 0.5~60 秒）；
    /// **负数**（惯例 <see cref="Timeout.InfiniteTimeSpan"/>）= **鼠标离开不收、点到岛外也不收**
    /// （用户可在配置界面里开关这一档）。
    ///
    /// ⚠️ 宿主要在鼠标离开时**每帧读**它，实现里不能有 IO 或长时间持锁。
    /// </summary>
    TimeSpan DetailAutoCollapseDelay { get; }

    /// <summary>写一条插件日志（状态灯变化时用它留痕，便于核对灯与实际状态）。</summary>
    void Note(string message);

    /// <summary>
    /// 详情页渲染所需的运行快照（端口 / 累计计数）。每帧调用，内部有锁。
    ///
    /// ★ 为什么详情页要它，而状态灯本身不需要 ★
    /// 状态灯只画一颗纯几何圆点，不碰文字；而右键展开的配置详情页要显示
    /// 「端口 / 已转发」这类数值摘要（承接早先从岛上移入配置界面的那行状态），
    /// 所以需要把端口与计数一并交出来。宿主渲染线程每帧调用，内部有锁保证线程安全。
    /// </summary>
    ConfigSnapshot Snapshot();
}

/// <summary>
/// 灵动岛组件：只剩一盏状态灯。
///
///   绿 = 服务运行中 · 橙 = 运行中但**此刻**有未恢复的失败 · 灰 = 服务已停止
///
/// 状态每帧实时读取（<see cref="LightStatus.Decide"/>），不做任何缓存；
/// 灯色**发生变化时**写一行插件日志，方便用户对照「灯显示的颜色」与「服务真实状态」。
///
/// 交互：
///   · 左键单击 → 开启 / 停止推送服务（岛内唯一的操作）；
///   · 右键 → 展开**配置详情页**（状态摘要 + 「打开完整配置」按钮）。
///     本组件 <see cref="DetailPage"/> 非空，宿主会消费掉这次右键、不再打开设置窗口，
///     于是「插件配置窗口」与「宿主设置窗口」不再叠加。见 <see cref="ConfigDetailPage"/>。
///
/// 原先显示在岛上的端口号、转发条数等文字已移入设置界面的「状态」摘要行（见 ConfigPanel.DrawStatusSummary）。
///
/// 为什么改成固定宽度：宿主对组件宽度是**按注册表版本缓存**的 —— Renderer 只在组件注册 / 注销 /
/// 顺序变化时调用一次 MeasureWidth，之后每帧复用缓存值（见 Renderer.cs 的 _pluginWidths）。
/// 因此「宽度随文字长度变化」的组件在本宿主上会一直停在首次测量值，文字会被裁掉；
/// 固定宽度的状态灯天然不受该缓存影响，既契合精简需求，也消除了这一隐患。
/// </summary>
internal sealed class StatusWidget : IWidget
{
    /// <summary>灯的半径（逻辑像素）。</summary>
    private const float DotRadius = 5f;

    /// <summary>
    /// 左右留白：让灯不贴边，同时把点击区域撑大便于点中。
    ///
    /// ★ 为什么从 7 收到 4.5 ★ 宿主 <c>Renderer.DrawPluginWidgets</c> 在 <c>x += w</c> 处是
    /// **零间距**排布组件的，所以本组件每多留 1px，就原封不动地变成「灯与右侧更新提示条之间的空白」。
    /// 7px 时总宽 24px，而灯本体只有 10px —— 一半的宽度是空的，加上提示条自身的左留白，
    /// 两个组件之间会出现 16px 的空洞。收到 4.5px 后总宽 19px，仍是 10px 灯径的近两倍点击区
    /// （19px ≥ 微软建议的最小可点尺寸 18px 的观感），空白明显收紧。
    /// </summary>
    private const float SidePadding = 4.5f;

    private readonly IWidgetStatus _status;
    private readonly ConfigDetailPage _detailPage;

    /// <summary>上一次绘制的灯色，仅用于「变化时记一条日志」，初值 -1 保证首帧必记一次。</summary>
    private int _lastLoggedState = -1;

    public StatusWidget(IWidgetStatus status)
    {
        _status = status;
        _detailPage = new ConfigDetailPage(status);
    }

    public string Id => "com.nps.pushbridge.widget";
    public string DisplayName => "推送网桥";
    public IDetailPage? DetailPage => _detailPage;

    /// <summary>宽度恒定（灯 + 两侧留白），与内容无关，因此无需通知宿主重新测量。</summary>
    public float MeasureWidth(float availableHeight) => (DotRadius + SidePadding) * 2f;

    public void Draw(SKCanvas canvas, SKRect rect, WidgetFrame frame)
    {
        float centerX = rect.MidX;

        // ★ 只用矩形中心，**不加 frame.TextOffsetY** ★
        // TextOffsetY 是给「文字」对齐基线用的；本组件是一颗纯几何圆点，没有文字。
        // 宿主允许用户更换灵动岛字体，字体的度量差异会让 TextOffsetY 明显变化，
        // 加上它可能把灯推出该行可见区域（表现为「灯不见了 / 位置奇怪」）。
        float centerY = rect.MidY;

        var state = LightStatus.Decide(_status.ServiceRunning, _status.Unhealthy);
        LogStateChange(state);

        // 颜色由 LightStatus 统一给出：与日志 / 状态页里的「灰/橙/绿」同源，不会各写一份而走样
        var (r, g, b) = LightStatus.Rgb(state);
        var color = new SKColor(r, g, b);

        // 画笔在 Draw 内临时创建、画完即释放：绝不跨线程缓存原生对象（文档「踩坑」第 1 条）
        using var halo = new SKPaint { IsAntialias = true };
        using var dot = new SKPaint { IsAntialias = true };

        // 没有文字了，用一圈淡光晕提示「这里可以点」（鼠标悬停在岛上时）
        if (frame.IsHovered)
        {
            halo.Color = color.WithAlpha((byte)(frame.Alpha * 0.28f));
            canvas.DrawCircle(centerX, centerY, DotRadius + 4f, halo);
        }

        dot.Color = color.WithAlpha(frame.Alpha);
        canvas.DrawCircle(centerX, centerY, DotRadius, dot);
    }

    /// <summary>灯色变化时记一条日志（同一颜色不会重复刷屏）。宿主渲染线程调用，日志内部有锁。</summary>
    private void LogStateChange(LightState state)
    {
        int code = (int)state;
        if (code == _lastLoggedState) return;
        _lastLoggedState = code;

        try { _status.Note($"状态灯 → {LightStatus.Describe(state)}"); }
        catch { /* 记日志失败不影响绘制 */ }
    }

    /// <summary>命中整个矩形（比灯本身大一圈），单击即可开关服务。</summary>
    public WidgetHit HitTest(float x, float y, SKRect rect)
        => x >= 0 && x <= rect.Width && y >= 0 && y <= rect.Height
            ? new WidgetHit("toggle")
            : WidgetHit.None;

    public void OnLeftClick(string? action, float x, float y) => _status.ToggleService();

    /// <summary>
    /// 右键：**不再直接开窗口**。本组件 <see cref="DetailPage"/> 非空 → 宿主会
    /// 自动展开详情页并**消费掉这次右键**（不再打开宿主设置窗口），
    /// 冲突由此消除。「打开完整配置」按钮在详情页里。见 <see cref="ConfigDetailPage"/> 的说明。
    /// </summary>
    public void OnRightClick() { }

    public void OnActivate(IPluginHost host) { }

    public void OnDeactivate() { }
}
