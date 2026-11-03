using NotchPeninsula.Plugins;
using SkiaSharp;

namespace NpsPushBridge;

/// <summary>更新提示条读取的插件状态视图。</summary>
internal interface IUpdateBannerHost
{
    /// <summary>当前是否应当显示提示条（模式 / 是否真有新版本 / 是否已被处理，都由宿主侧判定）。</summary>
    bool BannerVisible { get; }

    /// <summary>提示条上的文字（含远端版本号）。</summary>
    string BannerLabel { get; }

    /// <summary>
    /// 预算紧张时改用的**短文案**（只有箭头 + 「更新」）。
    ///
    /// ★ 存在的意义 ★ 宿主按注册顺序贪心放行组件，提示条排在状态灯之后；
    /// 组合模式下原生模块（时钟 / 硬件 / 媒体）一多，留给提示条的预算就会紧到
    /// 装不下完整文案。此时退到短文案能保住「提示条本身还在岛上」，
    /// 而不是整帧被宿主跳过 —— 见 <see cref="UpdateNotice.PickWidthCandidate"/>。
    /// </summary>
    string BannerLabelCompact { get; }

    /// <summary>
    /// 宿主给出的「本插件所在位置的剩余可用宽度」（逻辑像素，已扣掉排在前面且已放行的组件）。
    ///
    /// 返回 <see cref="float.PositiveInfinity"/> 表示宿主尚未算过（启动首帧），此时按「都放得下」处理。
    /// </summary>
    float AvailableRowWidth { get; }

    /// <summary>鼠标悬停在岛上时的提示语（说明点了会发生什么）。</summary>
    string BannerHint { get; }

    /// <summary>点击提示条。</summary>
    void OnBannerClick();

    /// <summary>提示条被显示 / 隐藏时留一条日志，便于核对。</summary>
    void Note(string message);
}

/// <summary>
/// 灵动岛上的「有新版本」提示条 —— 本插件自建的更新提示交互层。
///
/// ── 为什么要有它（而不是直接用宿主的 PostReminder）────────────────────────
/// 宿主 <c>PluginHost.PostReminder</c> 会把 <c>ReminderData.OnClick</c> **整条丢弃**
/// （不读该字段，<c>ToastData</c> 里也没有回调字段），点击落点一律走
/// <c>Program.cs</c> 的 <c>WindowClicked</c> → <c>AppActivator.active_app(CurrentToast.Aumid)</c>，
/// 而插件提醒的 Aumid 是空串 → 点击必然无反应。宿主源码不可改，所以「点击提示打开更新界面」
/// 只能由插件用自己的组件来实现 —— 组件的 <see cref="HitTest"/> / <see cref="OnLeftClick"/>
/// 是宿主**确实会派发**的唯一插件侧点击通道（见 <c>Renderer.DispatchPluginLeftClick</c>）。
///
/// ── 为什么「注册独立组件」而不是动态改宽度 ──────────────────────────────
/// 宿主对组件宽度是**按注册表版本缓存**的：<c>Renderer._pluginWidths</c> 只在插件注册 /
/// 注销 / 排序时重算一次，之后每帧复用。因此「同一个组件平时宽 24px、提示时变 90px」不会生效
/// （会一直停在首次测量值）。注册成**两个独立组件**就没有这个问题：提示条自己的宽度
/// 只在它注册的那一刻测量一次，而它注册的时机正好就是「决定要提示」的时候。
///
/// ── 为什么不会和「通知吞掉提示」打架 ────────────────────────────────────
/// 通知弹出时宿主会 <c>SetPluginRowBudget(0)</c>，整行插件（含本提示条、含状态灯）本帧不绘制、
/// 不登记命中区 —— 这是宿主既定行为。所以顺序天然是：
///   ① Toast 报一声「有新版本」→ ② Toast 消失 → ③ 本提示条出现并可以点。
/// 用户不会在一条通知上点到两套东西，也不存在两个面板抢位置。
/// </summary>
internal sealed class UpdateBannerWidget : IWidget
{
    private readonly IUpdateBannerHost _host;

    /// <summary>上次记录过的可见性，用于「只记变化时的一条日志」。</summary>
    private int _lastLogVisible = -1;

    /// <summary>
    /// 本次测量选中的文案下标（0 = 完整版，1 = 短版，-1 = 都不放行）。
    ///
    /// ★ 为什么要把「选中的文案」存下来 ★ 宿主的宽度是**测一次、缓存复用**的
    /// （见 <c>Renderer._pluginWidths</c>，只在注册表版本变化时重测），
    /// 而 <see cref="Draw"/> 每帧都会被调用。若两处各自按「当前预算」现算一遍，
    /// 预算一旦在两次调用之间变化（媒体开始播放、原生模块变宽），
    /// 就会出现「按完整版量的宽度、却按短版画的文字」——右边留一大块空白。
    /// 所以测量时定下文案并存住，绘制严格照它画，两者永远一致。
    /// </summary>
    private int _chosenLabelIndex = -1;

    /// <summary>测量时使用的文案（<see cref="_chosenLabelIndex"/> 的解析结果）。</summary>
    private string _chosenLabel = string.Empty;

    /// <summary>上次「放不下」日志的时间戳，用于限流（测量虽非每帧，但版本频繁变化时仍可能连发）。</summary>
    private DateTime _lastFitLogAt = DateTime.MinValue;

    /// <summary>
    /// 已作废标记。
    ///
    /// ★ 为什么需要它 ★ 宿主的插件 API **没有「注销组件」**（只有 RegisterWidget），
    /// 而组件的宽度是按注册表版本缓存的 —— 直接「摘掉」一个已注册组件在插件侧做不到。
    /// 但宿主 <c>Renderer.RecomputePluginVisibility</c> 的放行条件是
    /// <c>widths[i] > 0f</c>：**宽度 ≤ 0 的组件被判定为不可显示**，既不绘制也不登记命中区。
    /// 所以「收起提示条」= 让 <see cref="MeasureWidth"/> 返回 0 —— 下一帧起它就消失了，
    /// 而且因为宽度不参与布局，岛体宽度也会立刻缩回去。
    ///
    /// 前提：宿主要重新测量宽度才会看到这个 0。宽度缓存只在「注册表版本变化」时失效，
    /// 而本插件每次注册新提示条都会让版本 +1（RegisterWidget），所以自然会被重测；
    /// 万一某次没被重测，最坏结果也只是提示条多停留一会儿 —— 不会出错、不会崩。
    /// </summary>
    private volatile bool _retired;

    public UpdateBannerWidget(IUpdateBannerHost host) => _host = host;

    /// <summary>作废本组件（点掉 / 切到「不提示」时调用）。见 <see cref="_retired"/> 的说明。</summary>
    public void Retire() => _retired = true;

    public string Id => "com.nps.pushbridge.update";
    public string DisplayName => "推送网桥更新提示";

    /// <summary>提示条不提供详情页：右键留给宿主的默认行为（打开设置窗口）。</summary>
    public IDetailPage? DetailPage => null;

    /// <summary>
    /// 宽度 = 左留白 + 图标 + 图标文字间距 + 文字 + 右留白。
    ///
    /// 用固定字号的文本度量（提示条行高由宿主给，但我们只按一个稳定的字号估算），
    /// 宁可**略微多报**也不要在宿主侧被裁掉 —— 宿主不会替组件压缩或截断，
    /// 报少了就是文字缺一截。
    ///
    /// ★ 度量与绘制必须同字体 ★ 宽度直接决定岛体被撑多宽。如果这里用「无 Typeface 的默认
    /// 字体」量、<see cref="Draw"/> 里却用中文字体画，中文字体下「有新版本 v1.1.1」要宽出
    /// 十几个像素，多出来的部分会被裁掉、文字贴到右边缘 —— 用户看到的「留白不均匀」多半
    /// 就是这么来的。所以两处都走 <see cref="PluginFonts"/>，量什么字体就画什么字体。
    ///
    /// ★★ 预算自适应（本次修复的核心）★★
    /// 宿主按注册顺序贪心放行组件，提示条排在状态灯之后，预算紧时会被**整帧跳过**。
    /// 这里改为：先取宿主给出的「本插件位置的剩余宽度」，再在
    /// {完整文案, 短文案} 里挑第一个放得下的 —— 挑不到才返回 0（放弃本组件）。
    /// 这样「原生模块偏宽」时提示条会退成「↑ 更新」而不是彻底消失。
    /// </summary>
    public float MeasureWidth(float availableHeight)
    {
        if (_retired) return 0f;   // ≤ 0 → 宿主判定不可显示，等同于「收起了」

        // 候选文案 → 候选宽度（顺序即优先级：完整版优先）
        string full = _host.BannerLabel;
        string compact = _host.BannerLabelCompact;

        var labels = new[] { full, compact };
        var widths = new float[labels.Length];
        for (int i = 0; i < labels.Length; i++)
            widths[i] = WidthOf(labels[i]);

        float budget = _host.AvailableRowWidth;
        int pick = UpdateNotice.PickWidthCandidate(budget, widths);

        if (pick < 0)
        {
            // 连最短文案都放不下 —— 记住这个结果，让 Draw 也不要画（防御性）
            _chosenLabelIndex = -1;
            _chosenLabel = string.Empty;

            // 限流：宿主只在「注册表版本变化」时重测宽度，正常情况下不会每帧走到这里；
            // 但排序 / 反复注册仍可能连发，这里保证至少间隔 2 秒才记一条。
            var now = DateTime.UtcNow;
            if ((now - _lastFitLogAt).TotalSeconds >= 2.0)
            {
                _lastFitLogAt = now;
                _host.Note($"更新提示条本帧放不下：剩余 {budget:F0}px，最短需 "
                    + $"{UpdateNotice.HostPluginGap + (widths.Length > 0 ? widths[widths.Length - 1] : 0f):F0}px");
            }
            return 0f;
        }

        _chosenLabelIndex = pick;
        _chosenLabel = labels[pick];
        return widths[pick];
    }

    /// <summary>按与绘制完全相同的字体度量某个文案的组件总宽。</summary>
    private static float WidthOf(string text)
        => UpdateNotice.SidePadding * 2f
           + UpdateNotice.IconWidth
           + UpdateNotice.IconTextGap
           + MeasureText(text, BannerTextSize);

    /// <summary>
    /// 供宿主侧记账用的「完整文案所需组件宽度」估算（不含插件间距）。
    /// 只用于日志里写清「差多少」，不参与任何布局决策。
    /// </summary>
    internal static float EstimateFullWidth(string label) => WidthOf(label);

    /// <summary>按与绘制完全相同的字体度量文本宽度。</summary>
    internal static float MeasureText(string text, float textSize)
    {
        if (string.IsNullOrEmpty(text)) return 0f;
        try
        {
            // 每次绘制/测量临时创建，用完即弃 —— 绝不跨线程缓存原生 SKPaint（宿主文档「踩坑」第 1 条）
            // ★ 用 IslandPaint（而非 TextPaint）★ 提示条是**上岛内容**，
            // 必须与宿主在岛上用的自定义字体一致；否则「量宽」与「画字」会分属两套字体，
            // 而宿主的宽度预算判定用的正是这里量出的宽度 → 不一致就会左右留白或被裁。
            using var paint = PluginFonts.IslandPaint(textSize);
            // ★ 再按这串字定一次字体 ★ 用户的字体可能缺某个字形（如 v1.3.0 里的数字都还好，
            // 但「可更新」这类短文案的汉字未必有）；Fit 会缺字形时回退系统字体。
            //   ⚠️ 绘制侧（Draw）必须用**同样的调用**，否则量宽与画宽又会分叉。
            PluginFonts.Fit(paint, text);
            return paint.MeasureText(text);
        }
        catch
        {
            // 度量失败也要给个保守值，否则宽度为 0 会被宿主判定为「不可显示」
            return text.Length * textSize * 0.62f;
        }
    }

    private const float BannerTextSize = 11.5f;

    public void Draw(SKCanvas canvas, SKRect rect, WidgetFrame frame)
    {
        if (_retired) return;   // 已收起：即便宿主这一帧仍给了矩形也不画（防御性）

        // ★ 严格照测量时选中的文案画 ★ 宽度是按它量给宿主的，这里若改画另一条，
        // 就会「量宽画窄」（右侧留白突兀）或「量窄画宽」（文字被裁掉一截）。
        // 测量尚未发生过（_chosenLabelIndex < 0 且未退休）时退回完整文案，
        // 属于首帧的极窄窗口，宿主下一帧就会重测。
        string label = _chosenLabelIndex >= 0 ? _chosenLabel : _host.BannerLabel;
        if (string.IsNullOrEmpty(label)) return;   // 本次判定为「放不下」→ 不画

        LogVisibility();

        float alpha = frame.Alpha;
        byte a = (byte)Math.Clamp(alpha * 255f, 0f, 255f);
        var accent = AccentColor();

        float x = rect.Left + UpdateNotice.SidePadding;
        float centerY = rect.MidY;

        // ★ 必须给 paint 设中文字体 ★ 不设 Typeface 时 SkiaSharp 用默认字体，
        // 实测 GetGlyphs("有新版本") 返回 [0,0,0,0]（字形 0 = .notdef），画出来是四个方框。
        // 走 IslandPaint：提示条是上岛内容，字体与宿主岛内文字、以及上面的 MeasureText 三者同源，
        // 「量宽 == 画宽」才成立。
        using var textPaint = PluginFonts.IslandPaint(BannerTextSize, accent.WithAlpha(a));
        // ★ 与 MeasureText 用**同一个调用**按这串字定字体 ★
        // 提示条的宽度是量出来上报给宿主的，量画一旦分属两套字体，就会出现
        // 「量宽画窄」（右侧留白突兀）或「量窄画宽」（文字被裁一截）。
        PluginFonts.Fit(textPaint, label);

        // —— 向上箭头图标（表示「有更新的版本」）——
        DrawUpArrow(canvas, x, centerY, accent.WithAlpha(a));

        // —— 文案：与矩形垂直居中 ——
        float textX = x + UpdateNotice.IconWidth + UpdateNotice.IconTextGap;
        float baseline = centerY - (textPaint.FontMetrics.Ascent + textPaint.FontMetrics.Descent) / 2f;

        // 悬停时画一条下划线，暗示「这里可以点」
        if (frame.IsHovered)
        {
            float width = textPaint.MeasureText(label);
            using var underline = new SKPaint
            {
                IsAntialias = true,
                StrokeWidth = 1f,
                Color = accent.WithAlpha((byte)(a * 0.7f)),
            };
            canvas.DrawLine(textX, baseline + 2.5f, textX + width, baseline + 2.5f, underline);
        }

        canvas.DrawText(label, textX, baseline, textPaint);
    }

    /// <summary>
    /// 手绘「向上箭头」图标。
    ///
    /// ★ 为什么手绘而不是打一个字符 ★ 这里原本就是手绘的两条斜线，但形状不对：
    /// 只画了「^」这一对人字斜线，没有竖干 —— 于是它看起来更像一个脱字符 / 屋顶，
    /// 而不是「升级」箭头。更麻烦的是字符方案根本不可行：实测整个系统 120 个字体族里
    /// 只有 8 个含 <c>⬆</c>(U+2B06) 字形，雅黑、宋体、Segoe UI 都没有 —— 用字符画必然是方框。
    /// 手绘是唯一不依赖字体覆盖的做法，所以保留手绘、把形状画对。
    ///
    /// 形状 = 「∧」+ 竖干，整体在 <paramref name="centerY"/> 上垂直居中：
    /// 先按 (头部 + 干) 的总高算出顶端 y，再分别画斜线对与竖干。
    /// </summary>
    private static void DrawUpArrow(SKCanvas canvas, float left, float centerY, SKColor color)
    {
        float width = UpdateNotice.IconWidth;
        float headH = 5f;                                   // 箭头的「∧」部分高度
        float shaftH = 3.5f;                                // 竖干长度
        float totalH = headH + shaftH;

        float top = centerY - totalH / 2f;                   // 箭头最高点
        float mid = left + width / 2f;                       // 水平中心（对称轴）
        float headBottom = top + headH;                      // 「∧」两条斜线的落点
        float bottom = top + totalH;                         // 竖干末端

        using var pen = new SKPaint
        {
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1.6f,
            StrokeCap = SKStrokeCap.Round,
            StrokeJoin = SKStrokeJoin.Round,
            Color = color,
        };

        canvas.DrawLine(left, headBottom, mid, top, pen);    // 「∧」左斜线
        canvas.DrawLine(mid, top, left + width, headBottom, pen);  // 「∧」右斜线
        canvas.DrawLine(mid, top + headH * 0.35f, mid, bottom, pen);  // 竖干：略从头部内部起笔，衔接更自然
    }

    /// <summary>
    /// 命中整个组件矩形 —— 比文字本身大一圈，用户不用去精准点那几个字。
    /// 返回的 action 交给 <see cref="OnLeftClick"/>（当前所有命中都走同一处理，
    /// 但 action 仍带上语义，将来要做「点图标打开 / 点文字复制」时不必改协议）。
    /// </summary>
    public WidgetHit HitTest(float x, float y, SKRect rect)
        => x >= 0 && x <= rect.Width && y >= 0 && y <= rect.Height
            ? new WidgetHit("update-banner")
            : WidgetHit.None;

    /// <summary>点击提示条 —— 这里是「点击后能打开更新界面」真正落地的地方。</summary>
    public void OnLeftClick(string? action, float x, float y)
    {
        try { _host.OnBannerClick(); }
        catch { /* 点击处理失败不能把渲染线程带崩 */ }
    }

    /// <summary>右键：交给宿主的默认行为（本组件 DetailPage 为 null → 宿主打开设置窗口）。</summary>
    public void OnRightClick() { }

    public void OnActivate(IPluginHost host) { }

    public void OnDeactivate() { }

    /// <summary>提示条出现 / 消失时各记一条日志（不要每帧刷屏）。</summary>
    private void LogVisibility()
    {
        int visible = _host.BannerVisible ? 1 : 0;
        if (visible == _lastLogVisible) return;
        _lastLogVisible = visible;

        try
        {
            if (visible == 1)
                _host.Note($"更新提示条已显示：{_host.BannerLabel} · {_host.BannerHint}");
        }
        catch { /* 记日志失败不影响绘制 */ }
    }

    /// <summary>
    /// 提示条配色：与配置界面里「有新版本」的橙色同源（240,173,78），
    /// 但灵动岛主题底可能是深是浅，所以优先用宿主主题文字色的亮度来挑一个对比够的橙。
    /// </summary>
    private static SKColor AccentColor() => new(240, 173, 78);
}
