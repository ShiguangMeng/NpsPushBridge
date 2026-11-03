using SkiaSharp;

namespace NpsPushBridge;

/// <summary>
/// 配置界面的布局计算（纯几何，不含绘制与交互，便于配置页与配置窗口共用同一套规则）。
///
/// 尺寸规则（对应需求）：
///   · 白名单框、端口行、保存按钮 / 开启服务按钮 —— 高度为固定值；
///   · 上述各行的宽度 = 可用宽度 − 左右留白，随窗口缩放，并受 <see cref="MinContentWidth"/> 约束（最小宽度）；
///   · 日志区域 —— 宽度取剩余宽度、高度取剩余高度，宽高均可变，并分别受最小宽度 / <see cref="LogMinHeight"/> 约束。
/// </summary>
internal sealed class ConfigLayout
{
    // ---- 固定尺寸常量（逻辑像素） ----
    public const float PadX = 16f;
    public const float PadY = 14f;
    /// <summary>内容区最小宽度：窗口再窄也不会把控件压得不可用。</summary>
    public const float MinContentWidth = 340f;
    /// <summary>端口行 / 按钮行的固定高度。</summary>
    public const float RowHeight = 30f;
    /// <summary>白名单框的固定高度。</summary>
    public const float WhitelistBoxHeight = 96f;
    /// <summary>日志区域的最小高度（宽度最小值为 <see cref="MinContentWidth"/> − 两侧留白）。</summary>
    public const float LogMinHeight = 96f;
    /// <summary>端口输入框的最小宽度。</summary>
    public const float PortFieldMinWidth = 96f;
    /// <summary>
    /// 「手机端填」标签的宽度。
    /// 四个汉字按 11~12px 字号约 48px，留一点余量；标签定宽才能保证地址值的左边界不随字数抖动。
    /// </summary>
    public const float PhoneLabelWidth = 62f;
    /// <summary>地址行「复制」按钮的宽度（两个汉字 + 内边距）。</summary>
    public const float PhoneCopyButtonWidth = 52f;
    public const float Gap = 10f;
    public const float TinyGap = 4f;
    public const float TitleHeight = 24f;
    public const float HeaderHeight = 22f;
    public const float LabelHeight = 18f;
    public const float LineHeight = 20f;
    public const float PortButtonWidth = 34f;

    /// <summary>
    /// 「提醒显示时长」滑块右侧数值区的宽度。
    /// 够放下最长的 <c>30.00 秒</c>（11px 字体下约 52px）+ 一点右对齐余量。
    /// 固定宽度是有意的：否则拖动时数值宽度随位数变化，滑块会**边拖边伸缩**。
    /// </summary>
    public const float CollapseValueWidth = 62f;

    /// <summary>胶囊开关（Toggle）本体的宽度 —— WinUI 3 的 ToggleSwitch 约 40，这里取 34 更紧凑。</summary>
    public const float ToggleWidth = 34f;

    /// <summary>胶囊开关本体的高度。</summary>
    public const float ToggleHeight = 18f;
    public const float SmallButtonHeight = 18f;
    public const float LineRemoveWidth = 18f;

    /// <summary>
    /// 白名单每条输入框的高度。
    /// 24 = 11px 文字 + 上下各约 5px 留白：比原来 20px 的纯文本行更能看出「这是个输入框」，
    /// 又刚好让固定 96 高的框放下 3 条（见 <see cref="VisibleWhitelistEntries"/>）。
    /// </summary>
    public const float WhitelistEntryHeight = 24f;

    /// <summary>白名单条目之间的垂直间距。</summary>
    public const float WhitelistEntryGap = 4f;

    /// <summary>白名单标题行里「＋添加」按钮的宽度（与「清空」同档，够放下三字加加号）。</summary>
    public const float WhitelistAddButtonWidth = 58f;

    // 更新区块的小按钮宽度（与其它行一样：高度固定、整行宽度随窗口缩放）
    public const float UpdateCheckWidth = 68f;
    public const float UpdateCopyWidth = 92f;
    public const float UpdateOpenWidth = 80f;
    public const float UpdateModeOffWidth = 60f;
    public const float UpdateModeAutoWidth = 64f;
    public const float UpdateModeClickWidth = 72f;
    public const float UpdateActionMarketWidth = 80f;
    public const float UpdateActionClipboardWidth = 96f;
    public const float UpdateActionDownloadWidth = 88f;
    // 静默升级开关行：左侧说明，右侧「开启 / 关闭」二选一 + 一个「立即升级」
    public const float UpgradeToggleOnWidth = 52f;
    public const float UpgradeToggleOffWidth = 52f;
    public const float UpgradeNowWidth = 72f;

    /// <summary>日志区域起始 Y（顶部固定行区域的总高度），用于给出最小窗口高度建议。</summary>
    /// <remarks>
    /// 注意：这里的每一项必须与 <see cref="Compute"/> 里对应的 y 推进量**逐项一致**，
    /// 并且**顺序**也要一一对应。各行的推进量依次是：
    ///   标题(TitleHeight+Gap) → 状态摘要(HeaderHeight+TinyGap) → 白名单标题(HeaderHeight+TinyGap)
    ///   → **白名单规则提示行(LabelHeight+TinyGap)** → 白名单框(WhitelistBoxHeight+Gap)
    ///   → 端口标题(LabelHeight+TinyGap)
    ///   → 端口行(RowHeight+Gap) → 按钮行(RowHeight+Gap)
    ///   → 更新状态行(HeaderHeight+TinyGap) → 提示模式行(SmallButtonHeight+Gap)
    ///   → 点击动作行(SmallButtonHeight+Gap) → 静默升级开关行(SmallButtonHeight+Gap)
    ///   → 升级进度行(SmallButtonHeight+Gap) → 日志标题行(HeaderHeight+TinyGap)。
    /// 历史上曾三次写错：一次把「更新状态行」写成 SmallButtonHeight（差 22），
    /// 一次在加「提示模式行」时多补了一组，都被布局断言
    /// <c>日志区起点与 FixedTopHeight 常量精确一致</c> 逮到。
    /// 2026-09-23 新增「白名单规则提示行」时同步改了本常量与 <see cref="MinRecommendedHeight"/>，
    /// 断言当场验证通过（这正是那两条断言存在的意义）。
    /// 2026-09-26 新增「提醒显示时长」行（标签行 + 滑块行两段），同样两处一起改。
    /// 2026-10-09 新增「手机端填」地址行（一行），同样两处一起改。
    /// **改任何一行都要同步改这里和 <see cref="MinRecommendedHeight"/> 两处**。
    /// </remarks>
    public const float FixedTopHeight = PadY + TitleHeight + Gap + HeaderHeight + TinyGap
                                      + HeaderHeight + TinyGap
                                      + LabelHeight + TinyGap
                                      + WhitelistBoxHeight + Gap + LabelHeight + TinyGap
                                      + RowHeight + Gap
                                      + RowHeight + Gap
                                      + LabelHeight + TinyGap + RowHeight + Gap
                                      + RowHeight + Gap
                                      + RowHeight + Gap
                                      + HeaderHeight + TinyGap + SmallButtonHeight + Gap
                                      + SmallButtonHeight + Gap
                                      + SmallButtonHeight + Gap
                                      + SmallButtonHeight + Gap
                                      + HeaderHeight + TinyGap;

    /// <summary>
    /// 建议的最小窗口高度：固定行 + 日志最小高度 + 底部留白。
    ///
    /// ⚠️ 不能写成 <c>FixedTopHeight + LogMinHeight + PadY</c>：C# 的 const 在**声明处**求值，
    /// 而 <see cref="FixedTopHeight"/> 本身也是 const —— 那样写会把「当前这一版」的
    /// FixedTopHeight 烧进这个值里，之后往顶部再加一行就只改到 FixedTopHeight，
    /// 本值仍是旧数（表现为「最小推荐高度下日志区比最小值还高」的断言失败）。
    /// 所以这里把同一个表达式完整重写一遍，两处必须一起改。
    /// </summary>
    public const float MinRecommendedHeight = PadY + TitleHeight + Gap + HeaderHeight + TinyGap
                                            + HeaderHeight + TinyGap
                                            + LabelHeight + TinyGap
                                            + WhitelistBoxHeight + Gap + LabelHeight + TinyGap
                                            + RowHeight + Gap
                                            + RowHeight + Gap
                                            + LabelHeight + TinyGap + RowHeight + Gap
                                            + RowHeight + Gap
                                            + RowHeight + Gap
                                            + HeaderHeight + TinyGap + SmallButtonHeight + Gap
                                            + SmallButtonHeight + Gap
                                            + SmallButtonHeight + Gap
                                            + SmallButtonHeight + Gap
                                            + HeaderHeight + TinyGap
                                            + LogMinHeight + PadY;

    // ---- 实际使用的尺寸 ----
    public float ContentWidth { get; private set; }
    public float ContentHeight { get; private set; }
    /// <summary>可用宽度小于最小宽度、已按最小宽度布局（此时右侧内容会被裁切）。</summary>
    public bool WidthClamped { get; private set; }

    // ---- 各控件矩形 ----
    public SKRect Title { get; private set; }
    public SKRect StatusSummary { get; private set; }
    public SKRect WhitelistHeaderLabel { get; private set; }
    /// <summary>
    /// 白名单规则提示行（标题行与输入框区之间那一行）。
    ///
    /// ★ 为什么专门给它一整行 ★ 用户提的要求是「明确各个输入框对应的用途与校验规则」——
    /// 规则写在日志里、或只在出错时才出现，都等于没写。这一行是**常驻**的，
    /// 内容取自 <see cref="InputRules.WhitelistRule"/>（与真正做校验的那份文案同源，
    /// 不会出现「界面写支持 /24、代码其实不认」这种漂移）。
    /// </summary>
    public SKRect WhitelistHint { get; private set; }
    /// <summary>白名单标题行里的「＋添加」按钮（逐条输入后，用它新增一个空输入框）。</summary>
    public SKRect WhitelistAddButton { get; private set; }
    public SKRect LastSourceButton { get; private set; }
    public SKRect ClearButton { get; private set; }
    public SKRect ScrollUpButton { get; private set; }
    public SKRect ScrollDownButton { get; private set; }
    public SKRect WhitelistBox { get; private set; }
    public SKRect PortHeader { get; private set; }
    public SKRect PortMinus10 { get; private set; }
    public SKRect PortMinus1 { get; private set; }
    public SKRect PortField { get; private set; }
    public SKRect PortPlus1 { get; private set; }
    public SKRect PortPlus10 { get; private set; }

    /// <summary>「手机端填」标签（左侧固定宽度，说明这一行的用途）。</summary>
    public SKRect PhoneLabel { get; private set; }
    /// <summary>
    /// 手机端要填的完整地址（<c>http://IP:端口/</c>）显示区。
    /// 宽度随窗口变化，放不下时**截断显示**（复制拿到的仍是完整地址）。
    /// </summary>
    public SKRect PhoneValue { get; private set; }
    /// <summary>地址行右侧的「复制」按钮。</summary>
    public SKRect PhoneCopyButton { get; private set; }

    /// <summary>「提醒显示时长」的标签行（左侧说明文字）。</summary>
    public SKRect CollapseLabel { get; private set; }
    /// <summary>「提醒显示时长」的**滑块**（可拖动）—— 右端给数值显示让出位置。</summary>
    public SKRect CollapseSlider { get; private set; }
    /// <summary>滑块右侧的数值显示区（形如 <c>4.00 秒</c>），文字**右对齐**贴内容右边界。</summary>
    public SKRect CollapseValue { get; private set; }

    /// <summary>「忽略外部鼠标操作」整行的点击区 —— 开关本体与文字**都可点**（比只点小胶囊好用）。</summary>
    public SKRect CollapseIgnoreRow { get; private set; }
    /// <summary>「忽略外部鼠标操作」的胶囊开关本体（靠左、垂直居中）。</summary>
    public SKRect CollapseIgnoreToggle { get; private set; }
    public SKRect SaveButton { get; private set; }
    public SKRect ServiceButton { get; private set; }
    /// <summary>更新状态行（左侧状态文本，右侧三个操作按钮）。</summary>
    public SKRect UpdateHeader { get; private set; }
    public SKRect UpdateCheckButton { get; private set; }
    public SKRect UpdateCopyButton { get; private set; }
    public SKRect UpdateOpenButton { get; private set; }
    /// <summary>提示模式行：左侧说明文字，右侧「不提示 / 只显示 / 可点击」三选一。</summary>
    public SKRect UpdateModeRow { get; private set; }
    public SKRect UpdateModeOffButton { get; private set; }
    public SKRect UpdateModeAutoButton { get; private set; }
    public SKRect UpdateModeClickButton { get; private set; }
    /// <summary>点击动作行：左侧说明文字，右侧「打开下载页 / 复制到剪贴板 / 下载更新包」三选一。</summary>
    public SKRect UpdateActionRow { get; private set; }
    public SKRect UpdateActionMarketButton { get; private set; }
    public SKRect UpdateActionClipboardButton { get; private set; }
    public SKRect UpdateActionDownloadButton { get; private set; }
    /// <summary>静默升级开关行：左侧说明，右侧「开启 / 关闭」+「立即升级」。</summary>
    public SKRect UpgradeToggleRow { get; private set; }
    public SKRect UpgradeToggleOnButton { get; private set; }
    public SKRect UpgradeToggleOffButton { get; private set; }
    public SKRect UpgradeNowButton { get; private set; }
    /// <summary>升级进度 / 状态行（整行文本，随升级阶段变化）。</summary>
    public SKRect UpgradeStatusRow { get; private set; }
    public SKRect LogHeader { get; private set; }
    public SKRect LogCopyButton { get; private set; }
    public SKRect LogScrollUpButton { get; private set; }
    public SKRect LogScrollDownButton { get; private set; }
    public SKRect LogBox { get; private set; }

    /// <summary>
    /// 白名单框内能完整显示多少条输入框。
    ///
    /// ⚠️ 语义从「文本行」改成了「输入框」：每个条目现在是一个**独立带边框的输入框**
    /// （高 <see cref="WhitelistEntryHeight"/>、间隔 <see cref="WhitelistEntryGap"/>），
    /// 比原来 20px 一行的纯文本行距更占地方 —— 实测 96 高的框里正好放下 3 条。
    /// 条目再多靠 ▲/▼ 滚动。
    /// </summary>
    public int VisibleWhitelistEntries => Math.Max(1, (int)Math.Floor(
        (WhitelistBoxHeight - 12f + WhitelistEntryGap) / (WhitelistEntryHeight + WhitelistEntryGap)));

    /// <summary>日志框内可显示的行数。</summary>
    public int VisibleLogLines => Math.Max(0, (int)Math.Floor((LogBox.Height - 10f) / LineHeight));

    /// <summary>第 n 个条目（框内序号）的顶边 Y 坐标。</summary>
    public float WhitelistEntryTop(int indexInBox)
        => WhitelistBox.Top + 6f + indexInBox * (WhitelistEntryHeight + WhitelistEntryGap);

    /// <summary>
    /// 第 n 个条目的**输入框本体**（不含右侧删除按钮）—— 用户在这里填写。
    /// 右边留出删除按钮与间距，所以它不是整行宽度。
    /// </summary>
    public SKRect WhitelistEntryField(int indexInBox)
    {
        float top = WhitelistEntryTop(indexInBox);
        float right = WhitelistBox.Right - 8f - LineRemoveWidth - 6f;
        return new SKRect(WhitelistBox.Left + 8f, top, right, top + WhitelistEntryHeight);
    }

    /// <summary>第 n 个条目的删除按钮矩形（在输入框右侧、垂直居中）。</summary>
    public SKRect LineRemoveButton(int indexInBox)
    {
        var field = WhitelistEntryField(indexInBox);
        float top = field.Top + (WhitelistEntryHeight - LineRemoveWidth) / 2f;
        float right = WhitelistBox.Right - 8f;
        return new SKRect(right - LineRemoveWidth, top, right, top + LineRemoveWidth);
    }

    /// <summary>条目输入框内的文本可用宽度（减去左右内边距）。</summary>
    public float WhitelistEntryTextWidth => WhitelistEntryField(0).Width - 12f;

    /// <summary>某一行的文本可用宽度（兼容旧名，等价于 <see cref="WhitelistEntryTextWidth"/>）。</summary>
    public float LineTextWidth => WhitelistEntryTextWidth;

    /// <summary>日志框内文本可用宽度。</summary>
    public float LogTextWidth => LogBox.Width - 16f;

    /// <summary>
    /// 「配置教程」按钮的宽度（与「保存配置 / 开启服务」同一行，放在最右侧）。
    /// 76 够放下四个汉字 + 左右各 12px 内边距。
    /// </summary>
    public const float TutorialButtonWidth = 76f;

    /// <summary>
    /// 「图标映射」按钮的宽度（紧邻「配置教程」左侧）。
    /// 与教程按钮用同一个值，保证两个入口视觉等宽、排版对齐。
    /// </summary>
    public const float IconMapButtonWidth = 76f;

    /// <summary>
    /// 「配置教程」按钮矩形。
    ///
    /// ★ 为什么是**计算**而不是 <see cref="Compute"/> 里的一个字段 ★
    /// 多一个字段就要改 <see cref="FixedTopHeight"/> 的推进量，而那个常量与
    /// <see cref="MinRecommendedHeight"/> 是「两处必须同步」的脆弱结构（见其注释）。
    /// 这个按钮只占**同一行内**的横向空间，不改变任何行的纵向位置 ——
    /// 做成纯函数就能零风险地加进来：纵向布局逐像素不变，
    /// 而「保存 / 服务」两按钮的宽度改为从本按钮左侧开始等分（见 <see cref="ButtonsRowWidth"/>）。
    /// </summary>
    public SKRect TutorialButton
    {
        get
        {
            var row = SaveButton;
            // ⚠️ 锚点是**整行的右边界**，不能写成 SaveButton.Right：
            // 保存按钮现在只占「扣掉教程按钮后」的一半宽度，用它的右边当锚点会把教程按钮
            // 塞到行中间（这个错误被布局断言当场逮到过：宽度 340 时按钮落在 x=49）。
            // 行右边界 = 保存左边 + ButtonsRowWidth（见 Compute 的按钮行）。
            float rowRight = row.Left + ButtonsRowWidth;
            return new SKRect(rowRight - TutorialButtonWidth, row.Top, rowRight, row.Bottom);
        }
    }

    /// <summary>
    /// 「图标映射」按钮矩形 —— 与 <see cref="TutorialButton"/> 同样的「纯计算、不占纵向」思路，
    /// 紧贴在教程按钮左侧（之间留一个 <see cref="TinyGap"/>）。
    /// </summary>
    public SKRect IconMapButton
    {
        get
        {
            var row = SaveButton;
            float rowRight = row.Left + ButtonsRowWidth;
            float iconRight = rowRight - TutorialButtonWidth - TinyGap;
            return new SKRect(iconRight - IconMapButtonWidth, row.Top, iconRight, row.Bottom);
        }
    }

    /// <summary>
    /// 按钮行里「保存 / 服务 / 图标映射 / 配置教程」四者共用的总宽度（含三个间隙）。
    ///
    /// ⚠️ **由 <see cref="Compute"/> 显式赋值**，绝不是从 SaveButton/ServiceButton 反推 ——
    /// 后者会形成循环依赖：Compute 里 ServiceButton 的右边界取自 <see cref="IconMapButton"/>.Left，
    /// 而 IconMapButton 又依赖本属性 → 首次求值时 ServiceButton.Width 还是 0，
    /// 整行宽度凭空少一半（布局断言当场逮到过：宽度 340 时服务按钮宽度变成 0、
    /// 教程按钮右边界差 66 到不了内容右边）。
    /// </summary>
    public float ButtonsRowWidth { get; private set; }

    /// <summary>供 <c>ConfigPanel</c> 直接取用（避免它自己拼几何）。</summary>
    public static SKRect TutorialButtonOf(ConfigLayout layout) => layout.TutorialButton;

    /// <summary>供 <c>ConfigPanel</c> 直接取用（避免它自己拼几何）。</summary>
    public static SKRect IconMapButtonOf(ConfigLayout layout) => layout.IconMapButton;

    // ── 窗口右上角「关闭按钮」占位的几何（**窗口与面板共用这一份**）──────────────
    //   关闭按钮由 `PanelWindow` 绘制，而且是在**面板内容之后**画 —— 永远盖在上面。
    //   因此面板里任何右对齐的元素都必须给它让路；两处各写一份数值迟早会漂移，
    //   而症状（胶囊右端被圆盘压掉一截）只在特定窗口宽度下才明显，很难被发现。
    //
    //   ⚠️ 这两个常量是「距**窗口**右边缘」的物理量，与 PadX 无关 ——
    //   换算到「距内容右边界」时要把 PadX 减掉（见 ChromeRightGutter）。

    /// <summary>关闭按钮圆心距**窗口**右边缘的距离（逻辑像素）。</summary>
    public const float CloseButtonRightInset = 18f;
    /// <summary>关闭按钮半径（逻辑像素）。</summary>
    public const float CloseButtonRadius = 10f;

    /// <summary>
    /// 内容右边界到「窗口右边缘」的距离（= 右侧留白）。
    /// 背景元素右端要让出 <see cref="ChromeRightGutter"/>。
    /// </summary>
    private static float ChromeRightGutter
        => CloseButtonRightInset + CloseButtonRadius + 4f - PadX;   // 末尾 4px 是视觉间距

    /// <summary>
    /// 标题行右上角的**运行状态胶囊**（运行中 / 已停止）。
    ///
    /// ★ 为什么把它也提到布局层 ★ 它原先是在 <c>DrawTitle</c> 里就地算的（宽 66、高 18）。
    /// 「置顶」按钮要以它为参照点往左排 —— 若两处各写一份宽度，改一处就会重叠。
    /// 本项目已经有过「同一几何两处各写、改一处忘了另一处」的教训，这里直接收拢到一处。
    ///
    /// ★ 右端为什么不贴内容右边界 ★ 见 <see cref="ChromeRightGutter"/> ——
    /// 贴上去会被窗口右上角的关闭按钮盖掉一截。
    /// </summary>
    public static SKRect StatusPillOf(ConfigLayout layout)
    {
        const float pillWidth = 66f, pillHeight = 18f;
        float right = layout.Title.Right - ChromeRightGutter;
        return new SKRect(
            right - pillWidth,
            layout.Title.MidY - pillHeight / 2f,
            right,
            layout.Title.MidY + pillHeight / 2f);
    }

    /// <summary>标题行右侧的「置顶」开关按钮（紧挨在状态胶囊左侧）。</summary>
    public static SKRect TopmostButtonOf(ConfigLayout layout)
    {
        const float buttonWidth = 54f, buttonHeight = 18f, gap = 8f;
        float right = StatusPillOf(layout).Left - gap;
        return new SKRect(
            right - buttonWidth,
            layout.Title.MidY - buttonHeight / 2f,
            right,
            layout.Title.MidY + buttonHeight / 2f);
    }

    /// <summary>
    /// 标题行右侧的「更新日志」入口（紧挨在「置顶」左侧）。
    ///
    /// ★ 为什么放标题行、而不是「保存 / 服务 / 图标映射 / 配置教程」那一行 ★
    /// 按钮行在窄窗口（340）下已经没有余量：`buttonsInner` 只剩 142px，
    /// 再加一个固定宽度按钮会把 `halfWidth` 压到下限 40px ——「保存配置」四个汉字都放不下。
    /// 标题行则是**纯计算、不占纵向**的区域（与 <see cref="TopmostButtonOf"/> 同一思路），
    /// 加在这里纵向布局逐像素不变。
    ///
    /// ⚠️ 标题文字的最大宽度必须相应收窄，见 <see cref="TitleTextWidth"/>。
    ///
    /// 行为：点击它用浏览器打开**官网的更新日志页**（<see cref="PluginUpdate.ChangelogPageUrl"/>）——
    /// 2026-10-09 起不再在窗口内分屏显示 PNG（文档统一维护在网站仓库）。
    /// </summary>
    public static SKRect ChangelogButtonOf(ConfigLayout layout)
    {
        const float buttonWidth = 76f, buttonHeight = 18f, gap = 8f;
        float right = TopmostButtonOf(layout).Left - gap;
        return new SKRect(
            right - buttonWidth,
            layout.Title.MidY - buttonHeight / 2f,
            right,
            layout.Title.MidY + buttonHeight / 2f);
    }

    /// <summary>
    /// 标题行右侧的「黑名单」入口（紧挨在「更新日志」左侧）。
    ///
    /// ★ 为什么也在标题行 ★
    /// 按钮行在窄窗口下已经没有余量（见 <see cref="ChangelogButtonOf"/> 的说明），
    /// 标题行则是**纯计算、不占纵向**的区域。应用黑名单维护在**独立子页**里
    /// （见 <see cref="ComputeBlacklist"/>），主页面纵向布局因此逐像素不变。
    /// 62 够放下「黑名单」三个汉字 + 左右内边距。
    ///
    /// ⚠️ 标题文字的最大宽度随之收窄，见 <see cref="TitleTextWidth"/>。
    /// </summary>
    public static SKRect BlacklistButtonOf(ConfigLayout layout)
    {
        const float buttonWidth = 62f, buttonHeight = 18f, gap = 8f;
        float right = ChangelogButtonOf(layout).Left - gap;
        return new SKRect(
            right - buttonWidth,
            layout.Title.MidY - buttonHeight / 2f,
            right,
            layout.Title.MidY + buttonHeight / 2f);
    }

    /// <summary>
    /// 标题文字可用的最大宽度 —— **由标题行最靠左的那个按钮反推**，
    /// 而不是写死一个数字：这样标题行右侧再加按钮时，文字会自动让出空间，
    /// 不会出现「标题压到按钮底下」（右边留白被吃掉）。
    ///
    /// ⚠️ 锚点必须是**最左**的按钮（当前是 <see cref="BlacklistButtonOf"/>）——
    /// 用右边那个反推会让标题压在更左的按钮上。窄窗口（内容宽 340）下这里会退到下限 60px，
    /// 标题被省略号截断属预期：按钮优先，标题是纯装饰。
    /// </summary>
    public static float TitleTextWidth(ConfigLayout layout)
        => Math.Max(60f, BlacklistButtonOf(layout).Left - layout.Title.Left - 10f);

    /// <summary>日志区第 n 个可视行的基线 Y 坐标。</summary>
    public float LogLineBaseline(int indexInBox) => LogBox.Top + 6f + indexInBox * LineHeight + 12f;

    /// <summary>按给定可用区域计算布局。</summary>
    public static ConfigLayout Compute(float width, float height)
    {
        var l = new ConfigLayout();
        l.WidthClamped = width < MinContentWidth;
        l.ContentWidth = Math.Max(width, MinContentWidth);
        l.ContentHeight = height;

        float innerW = l.ContentWidth - PadX * 2f;
        float x = PadX;
        float y = PadY;

        l.Title = new SKRect(x, y, x + innerW, y + TitleHeight);
        y += TitleHeight + Gap;

        // —— 状态摘要行：承接原先显示在灵动岛上的信息（端口 / 已转发 / 最近来源） ——
        l.StatusSummary = new SKRect(x, y, x + innerW, y + HeaderHeight);
        y += HeaderHeight + TinyGap;

        // —— 白名单标题行：左侧标题，右侧一排固定尺寸的小按钮 ——
        l.WhitelistHeaderLabel = new SKRect(x, y, x + innerW, y + HeaderHeight);
        float buttonTop = y + (HeaderHeight - SmallButtonHeight) / 2f;
        float right = x + innerW;
        l.ScrollDownButton = TakeRight(ref right, 20f, buttonTop, SmallButtonHeight);
        l.ScrollUpButton = TakeRight(ref right, 20f, buttonTop, SmallButtonHeight);
        l.ClearButton = TakeRight(ref right, 46f, buttonTop, SmallButtonHeight);
        l.LastSourceButton = TakeRight(ref right, 84f, buttonTop, SmallButtonHeight);
        l.WhitelistAddButton = TakeRight(ref right, WhitelistAddButtonWidth, buttonTop, SmallButtonHeight);
        y += HeaderHeight + TinyGap;

        // —— 白名单规则提示行：常驻的「用途 + 支持的写法」，与校验逻辑同源 ——
        l.WhitelistHint = new SKRect(x, y, x + innerW, y + LabelHeight);
        y += LabelHeight + TinyGap;

        // —— 白名单框：固定高度、宽度随窗口；框内是逐条独立的输入框 ——
        l.WhitelistBox = new SKRect(x, y, x + innerW, y + WhitelistBoxHeight);
        y += WhitelistBoxHeight + Gap;

        // —— 端口行：固定高度；左右各一组固定宽度的微调按钮，中间的输入框吃掉剩余宽度（随窗口缩放） ——
        l.PortHeader = new SKRect(x, y, x + innerW, y + LabelHeight);
        y += LabelHeight + TinyGap;

        l.PortMinus10 = new SKRect(x, y, x + PortButtonWidth, y + RowHeight);
        l.PortMinus1 = new SKRect(x + PortButtonWidth + TinyGap, y, x + 2f * PortButtonWidth + TinyGap, y + RowHeight);

        float plus10Left = x + innerW - PortButtonWidth;
        float plus1Left = plus10Left - TinyGap - PortButtonWidth;
        l.PortPlus1 = new SKRect(plus1Left, y, plus1Left + PortButtonWidth, y + RowHeight);
        l.PortPlus10 = new SKRect(plus10Left, y, x + innerW, y + RowHeight);

        float fieldLeft = x + 2f * PortButtonWidth + 2f * TinyGap;
        float fieldWidth = Math.Max(plus1Left - TinyGap - fieldLeft, PortFieldMinWidth);
        l.PortField = new SKRect(fieldLeft, y, fieldLeft + fieldWidth, y + RowHeight);
        y += RowHeight + Gap;

        // —— 手机端地址行：左侧标签、中间完整地址、右侧「复制」按钮 ——
        //    紧跟在端口行下面：这一行显示的就是「手机端 Webhook 要填的地址」，
        //    它由**本机 IP + 端口**拼成，放在端口旁边最容易被看见、也最方便核对。
        //    ⚠️ 它占用一行纵向推进 ⇒ FixedTopHeight / MinRecommendedHeight 两处都要同步加。
        l.PhoneLabel = new SKRect(x, y, x + PhoneLabelWidth, y + RowHeight);
        float copyLeft = x + innerW - PhoneCopyButtonWidth;
        l.PhoneCopyButton = new SKRect(copyLeft, y, x + innerW, y + RowHeight);
        l.PhoneValue = new SKRect(l.PhoneLabel.Right + TinyGap, y, copyLeft - TinyGap, y + RowHeight);
        y += RowHeight + Gap;

        // —— 提醒行：左侧标签，右侧「滑块 + 数值」——
        //    数值区宽度**固定**（见 CollapseValueWidth）：否则拖动时位数变化会让滑块边拖边伸缩。
        //    纵向推进两段（标签行 + 滑块行），所以 FixedTopHeight / MinRecommendedHeight 都要同步加。
        l.CollapseLabel = new SKRect(x, y, x + innerW, y + LabelHeight);
        y += LabelHeight + TinyGap;
        l.CollapseSlider = new SKRect(x, y, x + innerW - CollapseValueWidth - Gap, y + RowHeight);
        l.CollapseValue = new SKRect(x + innerW - CollapseValueWidth, y, x + innerW, y + RowHeight);
        y += RowHeight + Gap;

        // —— 「忽略外部鼠标操作」开关行：整行可点，胶囊靠左、文字随其后 ——
        l.CollapseIgnoreRow = new SKRect(x, y, x + innerW, y + RowHeight);
        float toggleTop = y + (RowHeight - ToggleHeight) / 2f;
        l.CollapseIgnoreToggle = new SKRect(x, toggleTop, x + ToggleWidth, toggleTop + ToggleHeight);
        y += RowHeight + Gap;

        // —— 按钮行：固定高度；最右是「配置教程」与「图标映射」两个固定宽度按钮，
        //    剩余宽度由「保存 / 开启服务」等分 ——
        // ⚠️ 这一行仍然只推进 RowHeight + Gap，纵向布局与加这些按钮之前**逐像素一致**。
        // ⚠️ TutorialButton / IconMapButton 是**计算属性**（不在这里赋值）——
        //    它们只占同一行内的横向空间，做成纯函数就不会牵动 FixedTopHeight。
        //    这里只负责把「保存 / 服务」的可用宽度算对：整行宽度减去右侧两个按钮及其间隙。
        float buttonsInner = innerW - TutorialButtonWidth - IconMapButtonWidth - TinyGap - Gap;
        float halfWidth = Math.Max(40f, (buttonsInner - Gap) / 2f);
        // 先在算矩形之前把「行宽」定下来：IconMapButton / TutorialButton 这两个计算属性
        // 都依赖它，而 ServiceButton 又依赖 IconMapButton.Left —— 必须打破这个环。
        l.ButtonsRowWidth = halfWidth * 2f + Gap + Gap + IconMapButtonWidth + TinyGap + TutorialButtonWidth;
        l.SaveButton = new SKRect(x, y, x + halfWidth, y + RowHeight);
        l.ServiceButton = new SKRect(l.SaveButton.Right + Gap, y, l.IconMapButton.Left - Gap, y + RowHeight);
        y += RowHeight + Gap;

        // —— 更新状态行：左侧状态文本（自动裁切），右侧「检查 / 复制链接 / 打开下载页」三个固定宽度小按钮 ——
        l.UpdateHeader = new SKRect(x, y, x + innerW, y + HeaderHeight);
        float updateTop = y + (HeaderHeight - SmallButtonHeight) / 2f;
        float updateRight = x + innerW;
        l.UpdateOpenButton = TakeRight(ref updateRight, UpdateOpenWidth, updateTop, SmallButtonHeight);
        l.UpdateCopyButton = TakeRight(ref updateRight, UpdateCopyWidth, updateTop, SmallButtonHeight);
        l.UpdateCheckButton = TakeRight(ref updateRight, UpdateCheckWidth, updateTop, SmallButtonHeight);
        y += HeaderHeight + TinyGap;

        // —— 更新提示模式行：左侧说明，右侧「不提示 / 只显示 / 可点击」三选一 ——
        l.UpdateModeRow = new SKRect(x, y, x + innerW, y + SmallButtonHeight);
        float modeRight = x + innerW;
        l.UpdateModeClickButton = TakeRight(ref modeRight, UpdateModeClickWidth, y, SmallButtonHeight);
        l.UpdateModeAutoButton = TakeRight(ref modeRight, UpdateModeAutoWidth, y, SmallButtonHeight);
        l.UpdateModeOffButton = TakeRight(ref modeRight, UpdateModeOffWidth, y, SmallButtonHeight);
        y += SmallButtonHeight + Gap;

        // —— 更新点击动作行：左侧说明，右侧三选一 ——
        l.UpdateActionRow = new SKRect(x, y, x + innerW, y + SmallButtonHeight);
        float actionRight = x + innerW;
        l.UpdateActionDownloadButton = TakeRight(ref actionRight, UpdateActionDownloadWidth, y, SmallButtonHeight);
        l.UpdateActionClipboardButton = TakeRight(ref actionRight, UpdateActionClipboardWidth, y, SmallButtonHeight);
        l.UpdateActionMarketButton = TakeRight(ref actionRight, UpdateActionMarketWidth, y, SmallButtonHeight);
        y += SmallButtonHeight + Gap;

        // —— 静默升级开关行：左侧说明，右侧「立即升级｜关闭｜开启」（从右往左依次排） ——
        l.UpgradeToggleRow = new SKRect(x, y, x + innerW, y + SmallButtonHeight);
        float upgradeRight = x + innerW;
        l.UpgradeNowButton = TakeRight(ref upgradeRight, UpgradeNowWidth, y, SmallButtonHeight);
        l.UpgradeToggleOffButton = TakeRight(ref upgradeRight, UpgradeToggleOffWidth, y, SmallButtonHeight);
        l.UpgradeToggleOnButton = TakeRight(ref upgradeRight, UpgradeToggleOnWidth, y, SmallButtonHeight);
        y += SmallButtonHeight + Gap;

        // —— 升级进度 / 状态行：整行文本，右侧不放按钮 ——
        l.UpgradeStatusRow = new SKRect(x, y, x + innerW, y + SmallButtonHeight);
        y += SmallButtonHeight + Gap;

        // —— 日志标题行：左侧标题与计数，右侧「复制日志 / 上翻 / 下翻」三个固定尺寸小按钮 ——
        l.LogHeader = new SKRect(x, y, x + innerW, y + HeaderHeight);
        float logButtonTop = y + (HeaderHeight - SmallButtonHeight) / 2f;
        float logRight = x + innerW;
        l.LogScrollDownButton = TakeRight(ref logRight, 20f, logButtonTop, SmallButtonHeight);
        l.LogScrollUpButton = TakeRight(ref logRight, 20f, logButtonTop, SmallButtonHeight);
        l.LogCopyButton = TakeRight(ref logRight, 64f, logButtonTop, SmallButtonHeight);
        y += HeaderHeight + TinyGap;

        // —— 日志框：宽度取剩余宽度、高度取剩余高度（均有最小值） ——
        float logHeight = Math.Max(LogMinHeight, l.ContentHeight - y - PadY);
        l.LogBox = new SKRect(x, y, x + innerW, y + logHeight);

        return l;

        static SKRect TakeRight(ref float right, float width, float top, float height)
        {
            right -= width;
            var rect = new SKRect(right, top, right + width, top + height);
            right -= TinyGap;
            return rect;
        }
    }

    public static bool Contains(SKRect r, float x, float y) => x >= r.Left && x <= r.Right && y >= r.Top && y <= r.Bottom;

    // ====================================================================
    // 图标映射页的布局（同尺寸整页替换主内容区）
    // ====================================================================

    /// <summary>
    /// 图标映射页的一整套矩形。
    ///
    /// ★ 为什么单独算、不塞进 <see cref="Compute"/> ★
    /// 主页面每一行的推进量都被烧进了 <see cref="FixedTopHeight"/> 与
    /// <see cref="MinRecommendedHeight"/> 两个必须手动同步的 const。
    /// 映射页是**整页替换**、纵向结构与主页面无关，走独立函数就不会牵动那两处
    /// —— 主页面几何保持逐像素不变，这是本次改动风险最低的做法。
    ///
    /// 纵向推进量（与本函数中的 y += 逐项对应）：
    ///   标题(HeaderHeight+TinyGap) → 说明1(LabelHeight) → 说明2(LabelHeight+TinyGap)
    ///   → 编辑框(固定高度) → 工具行(SmallButtonHeight+Gap)
    ///   → 内置标题(LabelHeight) → 内置行×3(LineHeight) → 溢出提示(LineHeight)。
    ///
    /// ⚠️ 工具行里加按钮时**只动横向分配**（见 ④ 的宽度账），不要增加行数 ——
    ///    纵向一改，上面那串推进量就全部失配。
    /// </summary>
    public static IconMapLayout ComputeIconMap(float contentWidth)
    {
        var l = new IconMapLayout
        {
            ContentWidth = Math.Max(contentWidth, MinContentWidth),
        };

        float innerW = l.ContentWidth - PadX * 2f;
        float x = PadX;
        float y = PadY;

        // ① 标题行：左侧标题，右侧「保存映射 / 返回」（从右往左排）
        l.Header = new SKRect(x, y, x + innerW, y + TitleHeight);
        float headerTop = y + (TitleHeight - SmallButtonHeight) / 2f;
        float headerRight = x + innerW;
        l.ApplyButton = TakeRight(ref headerRight, 84f, headerTop, SmallButtonHeight);
        l.BackButton = TakeRight(ref headerRight, 56f, headerTop, SmallButtonHeight);
        y += TitleHeight + TinyGap;

        // ② 两行说明
        l.Hint1 = new SKRect(x, y, x + innerW, y + LabelHeight);
        y += LabelHeight;
        l.Hint2 = new SKRect(x, y, x + innerW, y + LabelHeight);
        y += LabelHeight + TinyGap;

        // ⑤ 先把底部三块（内置标题 + 3 行内置 + 溢出提示）与工具行的高度预留出来，
        //    剩下的才是编辑框。编辑框取**固定高度**而非「吃掉剩余高度」：
        //    本函数只收到宽度（拿不到页面总高），固定值反而更可预期，
        //    也避免窗口高度变化时编辑框忽大忽小。
        float boxTop = y;
        l.Box = new SKRect(x, boxTop, x + innerW,
            boxTop + Math.Max(IconMapLayout.BoxMinHeight, IconMapLayout.IconBoxHeight));
        y = l.Box.Bottom + Gap;

        // ④ 工具行：＋添加 / 复制 / 粘贴 / 清空自定义（左），上翻 / 下翻（右）
        //
        // ★ 这一行是**固定行高、横向排布**，加按钮只动横向分配，不影响任何纵向几何 ★
        // （与 TutorialButton / IconMapButton 同样的「纯计算、不占纵向」思路：
        //  映射页的纵向推进量一旦改动就要连带同步两处 const，风险高而收益为零。）
        //
        // 宽度账（内宽 = 内容宽 - 左右内边距；窄窗口下 ContentWidth 有 MinContentWidth 兜底）：
        //   左组：添加 84 + TinyGap + 复制 60 + TinyGap + 粘贴 60 + TinyGap + 清空 76
        //   右组：▼ 20 + TinyGap + ▲ 20（从右往左 TakeRight）
        // 两组之间留出的空隙由「右组从右边界反向取」自然形成，不会与左组相撞
        // —— 前提是内容宽度足够，MinContentWidth 已保证这一点。
        l.Tools = new SKRect(x, y, x + innerW, y + SmallButtonHeight);
        float toolsRight = x + innerW;
        l.ScrollDownButton = TakeRight(ref toolsRight, 20f, y, SmallButtonHeight);
        l.ScrollUpButton = TakeRight(ref toolsRight, 20f, y, SmallButtonHeight);
        l.ResetButton = TakeRight(ref toolsRight, 76f, y, SmallButtonHeight);

        // 左组从左边依次排开
        float toolsLeft = x;
        l.AddButton = new SKRect(toolsLeft, y, toolsLeft + 84f, y + SmallButtonHeight);
        toolsLeft += 84f + TinyGap;
        l.CopyButton = new SKRect(toolsLeft, y, toolsLeft + 60f, y + SmallButtonHeight);
        toolsLeft += 60f + TinyGap;
        l.PasteButton = new SKRect(toolsLeft, y, toolsLeft + 60f, y + SmallButtonHeight);

        y += SmallButtonHeight + Gap;

        // ⑤ 内置一览
        l.BuiltinHeader = new SKRect(x, y, x + innerW, y + LabelHeight);
        y += LabelHeight;

        for (int i = 0; i < IconMapLayout.BuiltinMaxRows; i++)
        {
            l.BuiltinRows[i] = new SKRect(x, y, x + innerW, y + LineHeight);
            y += LineHeight;
        }

        l.BuiltinMore = new SKRect(x, y, x + innerW, y + LineHeight);

        return l;

        static SKRect TakeRight(ref float right, float width, float top, float height)
        {
            right -= width;
            var rect = new SKRect(right, top, right + width, top + height);
            right -= TinyGap;
            return rect;
        }
    }

    /// <summary>
    /// 「应用黑名单」页的一整套矩形。
    ///
    /// ★ 为什么走独立函数、不塞进 <see cref="Compute"/> ★
    /// 与 <see cref="ComputeIconMap"/> 同样的理由：主页面每一行的推进量都被烧进了
    /// <see cref="FixedTopHeight"/> 与 <see cref="MinRecommendedHeight"/> 两个必须手动同步的 const。
    /// 黑名单是**整页替换**、纵向结构与主页面无关，独立计算就不会牵动那两处 ——
    /// 主页面几何保持逐像素不变。
    ///
    /// 纵向推进量（与本函数中的 y += 逐项对应）：
    ///   标题(TitleHeight+TinyGap) → 说明1(LabelHeight) → 说明2(LabelHeight+TinyGap)
    ///   → 编辑框(固定高度) → 工具行(SmallButtonHeight+Gap) → 底部统计(LabelHeight)。
    /// </summary>
    public static BlacklistLayout ComputeBlacklist(float contentWidth)
    {
        var l = new BlacklistLayout
        {
            ContentWidth = Math.Max(contentWidth, MinContentWidth),
        };

        float innerW = l.ContentWidth - PadX * 2f;
        float x = PadX;
        float y = PadY;

        // ① 标题行：左侧标题，右侧「保存名单 / 返回」（从右往左排）
        l.Header = new SKRect(x, y, x + innerW, y + TitleHeight);
        float headerTop = y + (TitleHeight - SmallButtonHeight) / 2f;
        float headerRight = x + innerW;
        l.ApplyButton = TakeRight(ref headerRight, 84f, headerTop, SmallButtonHeight);
        l.BackButton = TakeRight(ref headerRight, 56f, headerTop, SmallButtonHeight);
        y += TitleHeight + TinyGap;

        // ② 两行说明：第一行讲写法，第二行讲命中后的行为（用户最需要先知道「拦了会怎样」）
        l.Hint1 = new SKRect(x, y, x + innerW, y + LabelHeight);
        y += LabelHeight;
        l.Hint2 = new SKRect(x, y, x + innerW, y + LabelHeight);
        y += LabelHeight + TinyGap;

        // ③ 编辑框取**固定高度** —— 本函数只收到宽度（拿不到页面总高），
        //    固定值更可预期，也避免窗口高度变化时编辑框忽大忽小（与图标映射页同一取舍）。
        float boxTop = y;
        l.Box = new SKRect(x, boxTop, x + innerW,
            boxTop + Math.Max(BlacklistLayout.BoxMinHeight, BlacklistLayout.BoxHeight));
        y = l.Box.Bottom + Gap;

        // ④ 工具行：＋添加（左）／ 上翻 下翻（右）。固定行高、横向排布，
        //    加按钮只动横向分配 —— 与图标映射页工具行同一纪律。
        l.Tools = new SKRect(x, y, x + innerW, y + SmallButtonHeight);
        float toolsRight = x + innerW;
        l.ScrollDownButton = TakeRight(ref toolsRight, 20f, y, SmallButtonHeight);
        l.ScrollUpButton = TakeRight(ref toolsRight, 20f, y, SmallButtonHeight);

        float toolsLeft = x;
        l.AddButton = new SKRect(toolsLeft, y, toolsLeft + 84f, y + SmallButtonHeight);
        y += SmallButtonHeight + Gap;

        // ⑤ 底部统计行（共 N 条 / 空名单的含义）
        l.Footer = new SKRect(x, y, x + innerW, y + LabelHeight);

        return l;

        static SKRect TakeRight(ref float right, float width, float top, float height)
        {
            right -= width;
            var rect = new SKRect(right, top, right + width, top + height);
            right -= TinyGap;
            return rect;
        }
    }

}

/// <summary>
/// 图标映射页的矩形集合（见 <see cref="ConfigLayout.ComputeIconMap"/>）。
/// 独立于主布局类，避免把两套无关的几何混在一起。
/// </summary>
internal sealed class IconMapLayout
{
    /// <summary>
    /// 编辑框高度。
    ///
    /// 2026-09-23 由 132 提到 162：每条映射从「一行文本」改成**两个输入框**
    /// （应用名 + 图标值），行距随之变大，162 刚好还放得下 5 条（见 <see cref="VisibleLines"/>）。
    /// 映射页是整页布局、下方只跟一行内置一览，纵向余量充足（整页约 420，窗口 620）。
    /// </summary>
    public const float IconBoxHeight = 162f;
    /// <summary>编辑框最小高度兜底。</summary>
    public const float BoxMinHeight = 72f;
    /// <summary>内置一览最多画几行。</summary>
    public const int BuiltinMaxRows = 3;

    /// <summary>每一条映射占的高度（两个输入框并排，高度同白名单条目）。</summary>
    public const float EntryHeight = ConfigLayout.WhitelistEntryHeight;
    /// <summary>条目之间的垂直间距。</summary>
    public const float EntryGap = ConfigLayout.WhitelistEntryGap;
    /// <summary>「应用名」框与「图标值」框之间的横向间距。</summary>
    public const float FieldGap = 6f;
    /// <summary>「应用名」框宽度的上下限（按可用宽度的比例取，再夹到这个区间）。</summary>
    public const float KeyFieldMinWidth = 92f;
    public const float KeyFieldMaxWidth = 220f;

    public float ContentWidth { get; set; }

    public SKRect Header { get; set; }
    public SKRect BackButton { get; set; }
    public SKRect ApplyButton { get; set; }
    public SKRect Hint1 { get; set; }
    public SKRect Hint2 { get; set; }
    public SKRect Box { get; set; }
    public SKRect Tools { get; set; }
    public SKRect AddButton { get; set; }
    /// <summary>「复制选中」按钮（工具行左组第二个）。</summary>
    public SKRect CopyButton { get; set; }
    /// <summary>「粘贴」按钮（工具行左组第三个）。</summary>
    public SKRect PasteButton { get; set; }
    public SKRect ResetButton { get; set; }
    public SKRect ScrollUpButton { get; set; }
    public SKRect ScrollDownButton { get; set; }
    public SKRect BuiltinHeader { get; set; }
    public SKRect BuiltinMore { get; set; }

    private readonly SKRect[] _builtinRows = new SKRect[BuiltinMaxRows];

    public SKRect BuiltinRowAt(int index) => _builtinRows[Math.Clamp(index, 0, BuiltinMaxRows - 1)];

    internal SKRect[] BuiltinRows => _builtinRows;

    /// <summary>编辑框里能完整显示多少条映射（条目比文本行高，算法同白名单框）。</summary>
    public int VisibleLines => Math.Max(1, (int)Math.Floor(
        (Box.Height - 12f + EntryGap) / (EntryHeight + EntryGap)));

    /// <summary>第 n 条（框内序号）的整行矩形（含左右内缩与右侧删除按钮的位置）。</summary>
    public SKRect RowAt(int indexInBox)
    {
        float top = Box.Top + 6f + indexInBox * (EntryHeight + EntryGap);
        return new SKRect(Box.Left + 6f, top, Box.Right - 6f, top + EntryHeight);
    }

    /// <summary>
    /// 第 n 条的「**应用名**」输入框 —— 左框，填推送消息里的应用名（或包名）。
    /// </summary>
    public SKRect KeyFieldAt(int indexInBox)
    {
        var row = RowAt(indexInBox);
        float available = row.Width - FieldGap - (RowRemoveWidth + 6f);
        float keyWidth = Math.Clamp(available * 0.34f, KeyFieldMinWidth, KeyFieldMaxWidth);
        // 极窄窗口下比例值可能超过可用宽度，这里再夹一次，保证右框至少还有一点宽度
        keyWidth = Math.Min(keyWidth, Math.Max(40f, available - 60f));
        return new SKRect(row.Left, row.Top, row.Left + keyWidth, row.Bottom);
    }

    /// <summary>
    /// 第 n 条的「**图标值**」输入框 —— 右框，填别名 / 图片链接 / base64 / 本地路径。
    /// 右边界给删除按钮让出位置。
    /// </summary>
    public SKRect ValueFieldAt(int indexInBox)
    {
        var row = RowAt(indexInBox);
        var key = KeyFieldAt(indexInBox);
        float right = row.Right - RowRemoveWidth - 6f;
        return new SKRect(key.Right + FieldGap, row.Top, right, row.Bottom);
    }

    /// <summary>第 n 行右侧「删除」按钮的宽度（与白名单一致）。</summary>
    public const float RowRemoveWidth = ConfigLayout.LineRemoveWidth;

    /// <summary>第 n 行的「删除」按钮（贴右边缘）。</summary>
    public SKRect RemoveButtonAt(int indexInBox)
    {
        var row = RowAt(indexInBox);
        float top = row.Top + (EntryHeight - RowRemoveWidth) / 2f;
        return new SKRect(row.Right - RowRemoveWidth, top, row.Right, top + RowRemoveWidth);
    }
}

/// <summary>
/// 「应用黑名单」页的矩形集合（见 <see cref="ConfigLayout.ComputeBlacklist"/>）。
/// 独立于主布局类，避免把两套无关的几何混在一起。
///
/// 每条只有一个框（应用名或包名），所以比图标映射页简单：没有「应用名 / 图标值」两个框，
/// 也没有内置一览。条目高度与间距直接复用白名单那一组常量 —— 逐条输入框的观感在全插件统一。
/// </summary>
internal sealed class BlacklistLayout
{
    /// <summary>
    /// 编辑框高度。与图标映射页同档（都是「逐条输入框」区，行高与间距一致），
    /// 162 放得下 5 条（见 <see cref="VisibleLines"/>）。
    /// </summary>
    public const float BoxHeight = 162f;
    /// <summary>编辑框最小高度兜底。</summary>
    public const float BoxMinHeight = 72f;

    /// <summary>每一条占的高度（单框，与白名单条目同高）。</summary>
    public const float EntryHeight = ConfigLayout.WhitelistEntryHeight;
    /// <summary>条目之间的垂直间距。</summary>
    public const float EntryGap = ConfigLayout.WhitelistEntryGap;

    public float ContentWidth { get; set; }

    public SKRect Header { get; set; }
    public SKRect BackButton { get; set; }
    public SKRect ApplyButton { get; set; }
    /// <summary>第一行说明：写法（每行一个应用名或包名 + 通配）。</summary>
    public SKRect Hint1 { get; set; }
    /// <summary>第二行说明：命中后的行为（不上岛、对发送端仍算成功）。</summary>
    public SKRect Hint2 { get; set; }
    public SKRect Box { get; set; }
    public SKRect Tools { get; set; }
    public SKRect AddButton { get; set; }
    public SKRect ScrollUpButton { get; set; }
    public SKRect ScrollDownButton { get; set; }
    /// <summary>底部统计行（共几条 / 空名单的含义）。</summary>
    public SKRect Footer { get; set; }

    /// <summary>编辑框里能完整显示多少条（算法同白名单框）。</summary>
    public int VisibleLines => Math.Max(1, (int)Math.Floor(
        (Box.Height - 12f + EntryGap) / (EntryHeight + EntryGap)));

    /// <summary>
    /// 第 n 条（框内序号）的输入框。左右缩进与右侧删除按钮的让位都与白名单逐像素一致
    /// —— 两处若各写一套，同一份观感会在两个页面之间漂移。
    /// </summary>
    public SKRect FieldAt(int indexInBox)
    {
        float top = Box.Top + 6f + indexInBox * (EntryHeight + EntryGap);
        float right = Box.Right - 8f - ConfigLayout.LineRemoveWidth - 6f;
        return new SKRect(Box.Left + 8f, top, right, top + EntryHeight);
    }

    /// <summary>第 n 条的「删除」按钮（贴在该行右端）。</summary>
    public SKRect RemoveButtonAt(int indexInBox)
    {
        var field = FieldAt(indexInBox);
        float size = ConfigLayout.LineRemoveWidth;
        float top = field.Top + (EntryHeight - size) / 2f;
        float right = Box.Right - 8f;
        return new SKRect(right - size, top, right, top + size);
    }

    /// <summary>输入框内文本可用宽度。</summary>
    public float TextWidth => FieldAt(0).Width - 12f;
}

