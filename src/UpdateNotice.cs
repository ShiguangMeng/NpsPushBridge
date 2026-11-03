namespace NpsPushBridge;

/// <summary>
/// 发现新版本时，灵动岛上「更新提示」的展示方式。
///
/// ── 为什么需要这一个开关（历史教训）──────────────────────────────────────
/// 早先本插件只用宿主的 <c>IPluginHost.PostReminder</c> 投一条提示，并在
/// <c>ReminderData.OnClick</c> 里挂上「打开下载页」的回调 —— 但**宿主根本不读这个字段**：
/// 宿主的 <c>PluginHost.PostReminder</c> 只把 Title/Body/AppName 搬进 <c>ToastData</c> 后
/// 就 <c>ReminderPosted?.Invoke(toast)</c>，<c>OnClick</c> 被整条丢弃；而 <c>ToastData</c> 里
/// 也没有任何回调字段，点击落点走 <c>NotchWindow.WM_LBUTTONDOWN → RaiseWindowClicked</c> →
/// <c>Program.cs</c> 的 <c>WindowClicked</c>，它对**所有** toast 一律执行
/// <c>AppActivator.active_app(CurrentToast.Aumid)</c>，插件提醒的 <c>Aumid</c> 是空串，
/// 于是点击静默失败 —— 表现就是「点了弹窗没任何反应」。
///
/// 宿主源码不能改（项目硬约束），因此「点击提示打开更新界面」必须由插件自建：
/// 本插件自己注册一个**宽度随内容变化的组件**当作提示条，用自己的命中区接管点击。
/// 这个枚举就是该提示条的三种行为，交由用户在配置界面里选。
/// </summary>
internal enum UpdateBannerMode
{
    /// <summary>不显示提示条：只在配置界面里能看到「有新版本」。</summary>
    Off = 0,

    /// <summary>只告诉我：提示条自动显示，但点它不做任何动作（纯告知，防误触）。</summary>
    AutoDismiss = 1,

    /// <summary>点击处理：提示条自动显示，点它按「点击后动作」打开下载页或复制链接。</summary>
    Clickable = 2,
}

/// <summary>用户点击更新提示条之后要执行的动作。</summary>
internal enum UpdateClickAction
{
    /// <summary>用默认浏览器打开「插件市场」页 —— 一定有内容可看，最稳。</summary>
    Market = 0,

    /// <summary>把更新信息（版本 + 下载地址）复制到系统剪贴板。</summary>
    Clipboard = 1,

    /// <summary>用默认浏览器直接打开市场记录里的 .zip 下载直链。</summary>
    Download = 2,
}

/// <summary>
/// 更新提示的**纯逻辑**部分：模式解析、该不该显示、点了做什么、提示文案。
///
/// 刻意不依赖宿主 API、不依赖 WinForms、不碰文件系统，因此能被
/// <c>tests/BridgeHarness</c> 直接编译进去做断言 —— 交互行为有了可回归的规格，
/// 不会因为后续改动悄悄走样。
/// </summary>
internal static class UpdateNotice
{
    /// <summary>提示条在灵动岛上要显示的文本。</summary>
    public const string BannerText = "有新版本";

    /// <summary>
    /// 预算紧张时改用的超短文案（只有箭头 + 「更新」两字）。
    ///
    /// ★ 为什么需要「两条文案」★ 宿主的插件行放行是**按注册顺序贪心**的
    /// （<c>Renderer.RecomputePluginVisibility</c>：<c>used + 16 + width ≤ 预算</c> 才放行，
    /// 放不下就整帧跳过它）。本插件先注册状态灯、后注册提示条，所以预算是灯先扣、提示条后分。
    /// 实测（组合模式 + 时钟 + 硬件 + 媒体全开、媒体块约 350px 时）：
    ///   预算 = 800 − 原生 ≈ 128px；灯占 16+19 = 35px；剩给提示条只有 ~93px，
    ///   而全量文案「↑ 有新版本 v1.2.0」要 ~101px → **提示条被整帧跳过**，
    ///   用户看到的就是「弹窗弹了、岛上一直没提示」。
    /// 所以按可用预算在两条文案间二选一：够就显示完整版，紧就退到短版，
    /// 实在放不下才放弃 —— 从「要么全有要么全无」变成「尽量都有」。
    /// </summary>
    public const string BannerTextCompact = "更新";

    /// <summary>
    /// 「有新版本」吐司的停留时长。
    ///
    /// ★ 这个数直接决定提示条多久才能露面 ★
    /// 宿主在通知激活期间执行 <c>SetPluginRowBudget(0f)</c>
    /// （见 <c>NotchWindow.cs</c> 的「通知 / 剪贴板 / 详情页 / 媒体面板展开」分支），
    /// 于是**整行插件（含更新提示条、含状态灯）本帧既不绘制也不登记命中区**。
    /// 结论：吐司停留多久，提示条就被压住多久。原先的 5 秒会让用户长时间只看到弹窗、
    /// 看不到岛上提示，主观上就是「没有上岛」。这里收到「够看清一行字」的长度，
    /// 把提示条的出场时间提前。
    /// </summary>
    public static readonly TimeSpan ToastDuration = TimeSpan.FromSeconds(3);

    /// <summary>
    /// 提示条左右留白。
    ///
    /// ★ 这两个数直接决定「灯与文字之间的空白」★ 宿主 <c>Renderer.DrawPluginWidgets</c> 是
    /// <c>x += w</c> **零间距**依次排布组件的，所以两个组件之间看起来的空白 =
    /// 左侧组件的右留白 + 本组件的左留白。原来的 9px 叠上状态灯的 7px 右留白，
    /// 中间就凭空多出 16px 空洞（实测：灯圆心到文字起笔约 28px，字距感很散）。
    /// 6px 让左留白收到与右留白一致且更紧凑的观感 —— 两侧仍对称，只是整体更收紧。
    /// </summary>
    public const float SidePadding = 6f;

    /// <summary>上游箭头图标占宽（绘制时用，参与宽度计算）。</summary>
    public const float IconWidth = 10f;

    /// <summary>图标与文字之间的间距（图标与文字是同一组件内的两块，靠这个贴紧）。</summary>
    public const float IconTextGap = 4f;

    /// <summary>
    /// 已作废（收起）的提示条对灵动岛布局的宽度贡献。
    ///
    /// ★ 必须是精确的 0，不能是「很小」★ 宿主 <c>Renderer.RecomputePluginVisibility</c> 的放行条件是
    /// <c>widths[i] > 0f</c>，且累加方式是 <c>used += PLUGIN_GAP + widths[i]</c> —— 只要宽度非 0，
    /// 就既会被计入已用宽度、又可能被绘制。作废后返回 0，宿主才会把它当「不可显示」，
    /// 既不预留宽度也不登记命中区，岛上才不会留下一块什么都不画的空白。
    /// 这条不变式被 <c>BridgeHarness</c> 断言守住 —— 它是「点击提示条后出现空白」的回归防线。
    /// </summary>
    public const float RetiredWidthContribution = 0f;

    /// <summary>
    /// 正常（未作废）的提示条对布局的宽度贡献：必须严格大于 0，否则组件永远上不了岛。
    ///
    /// 这里刻意取「留白 + 图标 + 间距 + 至少一个字符的宽度」的下界来断言正性 ——
    /// 具体宽度由 <c>UpdateBannerWidget.MeasureWidth</c> 按实际字体度量算出（那里必须与绘制同字体），
    /// 本常量只用于钉住「正性」这个不变式，不参与任何实际布局。
    /// </summary>
    public const float ActiveWidthContribution = SidePadding * 2f + IconWidth + IconTextGap + 1f;

    /// <summary>
    /// 提示条「已显示」的判定：<b>进程内组件引用存在</b> 即视为已显示，
    /// 与持久化的「已忽略 / 已弹过吐司」等记忆**完全无关**。
    ///
    /// ★ 为什么要单独抽出来钉住 ★
    /// 「要不要显示」是一个纯函数判定（<see cref="ShouldShowBanner"/>），
    /// 而「已经注册成功」是一份运行时状态（<c>_banner != null</c>）。两者曾各自演化，
    /// 结果出现「判定为要显示、实际却没注册」的静默断层 —— 用户看到的就是
    /// 「弹窗弹了，岛上什么都没出现」。本方法把两者的衔接条件写成可断言的形式：
    /// 只要判定为真且没有并发收起，就必须真的去注册；注册成功后可见性必须立刻为真。
    /// </summary>
    public static bool BannerIsVisible(bool widgetRegistered) => widgetRegistered;

    /// <summary>
    /// 提示条注册是否**必须**在本轮执行（用于钉住「注册不得被冷却 / 节流静默跳过」）。
    ///
    /// ★ 教训（本次 bug 的根因之一）★
    /// 原先注册路径上有一道「距上次注册不足 30 秒就 return」的冷却：
    /// 插件启动 20 秒时的首次自动检查发现新版本 → 判定要显示 → 但此时距 <c>_bannerRegisteredAt</c>
    /// 的初值（<see cref="DateTime.MinValue"/>）虽已远超冷却，问题出在**同一轮里换了新版本对象**：
    /// 判定为真却因冷却被跳过，而**吐司没有冷却、照弹不误** ——
    /// 于是必然出现「弹窗弹了、提示条没上岛」。两者行为不一致的根源就在这处守卫。
    ///
    /// 契约：判定为「要显示」且当前没有已注册组件时，注册**必须**执行，不得被任何节流拦下。
    /// 节流只允许用于「同一个已注册组件」，不能用于「从无到有」。
    /// </summary>
    public static bool MustRegisterNow(bool shouldShow, bool alreadyRegistered)
        => shouldShow && !alreadyRegistered;

    // ====================================================================
    // ★ 预算自适应：提示条宽度与文案在「宿主放得下」之间取平衡 ★
    // ====================================================================

    /// <summary>
    /// 宿主用于排布插件行的组件间距（<c>Renderer.PLUGIN_GAP</c>）。
    ///
    /// ★ 必须与宿主一致 ★ 宿主放行判据是 <c>used + PLUGIN_GAP + width ≤ 预算</c>，
    /// 本值错 1px 就会让「预算刚好够」的提示条被误判为放不下而整帧消失。
    /// 宿主的常量是 private，无法引用，故在此显式声明并靠 BridgeHarness 断言守住语义。
    /// </summary>
    public const float HostPluginGap = 16f;

    /// <summary>
    /// 预算充足时优先使用的文案；不足时退到的短文案。
    ///
    /// 顺序即优先级：先试完整版，再试短版，都不行才放弃。
    /// </summary>
    public static readonly string[] BannerTextCandidates = { BannerText, BannerTextCompact };

    /// <summary>
    /// 给定「可用预算」与「本组件的候选宽度」，选出第一个放得下的候选下标；都放不下返回 -1。
    ///
    /// ★ 判据必须与宿主逐字对齐 ★ <c>Renderer.RecomputePluginVisibility</c>：
    ///   <c>ok = !broken[i] &amp;&amp; widths[i] &gt; 0f &amp;&amp; used + PLUGIN_GAP + widths[i] &lt;= budget</c>
    /// 其中 <paramref name="availableBudget"/> 应当是宿主给出的「本插件所在位置的剩余宽度」
    /// （<c>IPluginHost.GetPluginRowBudget</c>，已扣掉排在前面且本帧放行的组件）。
    ///
    /// 边界一律取「≤」而不是「&lt;」：宿主就是 ≤，差一个等号会让恰好贴边的组件消失。
    /// </summary>
    public static int PickWidthCandidate(float availableBudget, IReadOnlyList<float> candidateWidths)
    {
        if (candidateWidths == null || candidateWidths.Count == 0) return -1;
        if (float.IsNaN(availableBudget) || availableBudget < 0f) return -1;
        // 预算为 +∞（宿主尚未算过，启动首帧）→ 一律放行第一个候选，避免首帧闪空
        if (float.IsPositiveInfinity(availableBudget)) return 0;

        for (int i = 0; i < candidateWidths.Count; i++)
        {
            float w = candidateWidths[i];
            if (float.IsNaN(w) || w <= 0f) continue;          // 与宿主 widths[i] > 0f 对齐
            if (HostPluginGap + w <= availableBudget) return i; // 与宿主 ≤ budget 对齐
        }
        return -1;
    }

    /// <summary>
    /// 提示条放不下时的「放弃」判定 —— 让调用方与测试都能用同一句话描述这个结果。
    /// </summary>
    public static bool BannerFits(int candidateIndex) => candidateIndex >= 0;

    /// <summary>持久化的注册表取值：提示模式。</summary>
    public const string DefaultModeRaw = "2";          // Clickable
    public const string DefaultClickActionRaw = "0";   // Market

    // ====================================================================
    // 模式解析 / 序列化
    // ====================================================================

    /// <summary>
    /// 解析持久化的提示模式。
    ///
    /// ★ 兼容迁移 ★ 早期版本只有一个布尔式的「提示点击后动作」（<c>updateAction</c>：
    /// 0 = 打开浏览器，1 = 复制链接）。该键在旧版里是**必存**的，因此它存在就说明是旧配置：
    /// 把 1 迁移成「复制到剪贴板」，0 迁移成「打开下载页」，
    /// 提示模式统一落到 <see cref="UpdateBannerMode.Clickable"/>（保持旧版「点了有用」的行为）。
    /// </summary>
    public static UpdateBannerMode ParseMode(string? raw, string? legacyClickActionRaw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            // 没有新键 → 看看有没有旧键；都没有就是全新安装，用默认值
            if (!string.IsNullOrWhiteSpace(legacyClickActionRaw))
                return UpdateBannerMode.Clickable;
            return (UpdateBannerMode)int.Parse(DefaultModeRaw);
        }

        return int.TryParse(raw.Trim(), out int value) && IsValidMode(value)
            ? (UpdateBannerMode)value
            : (UpdateBannerMode)int.Parse(DefaultModeRaw);
    }

    public static bool IsValidMode(int value)
        => value >= (int)UpdateBannerMode.Off && value <= (int)UpdateBannerMode.Clickable;

    public static string ModeToRaw(UpdateBannerMode mode) => ((int)mode).ToString();

    /// <summary>配置界面上三选一按钮的标题。</summary>
    public static string ModeLabel(UpdateBannerMode mode) => mode switch
    {
        UpdateBannerMode.Off => "不提示",
        UpdateBannerMode.AutoDismiss => "只显示",
        _ => "可点击",
    };

    /// <summary>
    /// 模式的一行说明（配置界面显示，让用户明白选了会发生什么）。
    ///
    /// ★ 关于长度 ★ 这一行右侧紧挨着三个模式按钮，留给文字的空间在 420px 宽的面板里只有
    /// 约 178px（含前缀「灵动岛提示：」的 ~61px）。措辞必须压到 178px 以内，否则会被截成
    /// 省略号 —— 用户看不到「点了有没有用」这个真正决定选择的点。因此：
    ///   · 去掉重复的「灵动岛」（前缀已写）；
    ///   · 「点它不做任何动作」→「点它无动作」；「执行右侧选定的动作」→「执行右侧动作」；
    ///   · 「不提示」态改用「岛外」来对比，比「只在配置界面里显示」短一半且意思一样。
    /// 每条都在 <c>ConfigPanel</c> 的实际可用宽度下实测过（见验证脚本）。
    /// </summary>
    public static string ModeDescribe(UpdateBannerMode mode) => mode switch
    {
        UpdateBannerMode.Off => "只在配置界面显示版本",
        UpdateBannerMode.AutoDismiss => "显示提示条，点它无动作",
        _ => "显示提示条，点它执行动作",
    };

    // ====================================================================
    // 点击后动作：解析 / 迁移 / 文案
    // ====================================================================

    /// <summary>
    /// 解析持久化的「点击后动作」。
    ///
    /// ★ 兼容迁移 ★ 旧键 <c>updateAction</c> 的语义是「提示点击后做什么」，取值 0/1；
    /// 新键 <c>updateClickAction</c> 取值 0/1/2。旧值 0 恰好等于新值 0（<see cref="UpdateClickAction.Market"/>，
    /// 打开市场页），旧值 1 等于新值 1（<see cref="UpdateClickAction.Clipboard"/>），
    /// 因此只需**换个键读**，取值映射天然一致，不需要任何数值改写。
    /// </summary>
    public static UpdateClickAction ParseClickAction(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return (UpdateClickAction)int.Parse(DefaultClickActionRaw);

        return int.TryParse(raw.Trim(), out int value) && IsValidClickAction(value)
            ? (UpdateClickAction)value
            : (UpdateClickAction)int.Parse(DefaultClickActionRaw);
    }

    public static bool IsValidClickAction(int value)
        => value >= (int)UpdateClickAction.Market && value <= (int)UpdateClickAction.Download;

    public static string ClickActionToRaw(UpdateClickAction action) => ((int)action).ToString();

    public static string ClickActionLabel(UpdateClickAction action) => action switch
    {
        UpdateClickAction.Market => "打开下载页",
        UpdateClickAction.Clipboard => "复制到剪贴板",
        _ => "下载更新包",
    };

    // ====================================================================
    // 该不该显示 / 点击了做什么
    // ====================================================================

    /// <summary>
    /// 当前是否应当在灵动岛上显示更新提示条。
    ///
    /// 三个条件缺一不可：
    ///   1. 这次检查**确实发现了新版本**（<see cref="UpdateCheckResult.HasUpdate"/>）；
    ///   2. 提示模式不是 <see cref="UpdateBannerMode.Off"/>；
    ///   3. 远端版本没有被用户「放行」过 —— 点过一次提示条后就不再骚扰，
    ///      直到市场出现**更高**的版本为止（见 <see cref="ShouldDismissBanner"/> 的说明）。
    /// </summary>
    public static bool ShouldShowBanner(UpdateCheckResult result, UpdateBannerMode mode, string? dismissedVersion)
    {
        if (!result.HasUpdate) return false;
        if (mode == UpdateBannerMode.Off) return false;
        if (result.RemoteVersion.Length == 0) return false;

        // 已被用户处理过的版本：不再显示。比较用语义化比较，避免 "1.2" 与 "1.2.0" 被当成两个版本反复弹。
        // ★ 注意这里只认「用户已忽略」这一个记忆 ★ 插件自己「已弹过吐司」的记忆绝不能进来 ——
        // 否则自动检查一弹吐司，提示条就再也上不了岛（这就是「自动检查不会像手动检查那样提示」的根因）。
        return !IsRemembered(dismissedVersion, result.RemoteVersion);
    }

    /// <summary>点击提示条是否应当执行动作（只有「可点击」模式才真做事）。</summary>
    public static bool ShouldRunClickAction(UpdateBannerMode mode)
        => mode == UpdateBannerMode.Clickable;

    // ====================================================================
    // 「已忽略」与「已弹过吐司」是两件事 —— 曾合成一个字段，是自动检查不提示的根因
    // ====================================================================

    /// <summary>
    /// 两个「已记住的版本」谁更「压得住」：返回 true 表示 <paramref name="remembered"/>
    /// 已经涵盖 <paramref name="remoteVersion"/>（即该版本已被记住，不必再处理）。
    ///
    /// 语义化比较（而不是字符串相等）：市场把 <c>1.2</c> 写成 <c>1.2.0</c> 不该被当成两个版本
    /// 反复提示。空记忆一律返回 false（没有任何记忆 = 什么都没记住）。
    ///
    /// ★ 之所以抽成函数 ★ 「用户已忽略」与「插件已弹过吐司」两个判断必须逐字一致，
    /// 否则会出现「吐司不再弹、提示条却也不上岛」这种自相矛盾的状态 —— 那正是本次修的 bug。
    /// </summary>
    public static bool IsRemembered(string? remembered, string? remoteVersion)
    {
        if (string.IsNullOrEmpty(remembered)) return false;
        if (string.IsNullOrEmpty(remoteVersion)) return false;
        return PluginUpdate.CompareVersion(remoteVersion, remembered) <= 0;
    }

    /// <summary>
    /// 自动（非手动）检查时，是否应该再弹一次宿主吐司。
    ///
    /// 与 <see cref="ShouldShowBanner"/> 的关键区别：吐司是**一次性通知**（弹过就该闭嘴），
    /// 提示条是**持续状态**（挂着直到用户点掉）。所以吐司看的是「已弹过吐司的版本」，
    /// 提示条看的是「用户已忽略的版本」—— 绝不能用同一个记忆。
    /// </summary>
    public static bool ShouldToast(UpdateCheckResult result, string? toastedVersion)
        => result.HasUpdate && !IsRemembered(toastedVersion, result.RemoteVersion);

    /// <summary>点击提示条之后，要打开 / 复制的地址。</summary>
    public static string ResolveClickTarget(UpdateCheckResult result, UpdateClickAction action) => action switch
    {
        // 下载直链：市场记录里没有 download 字段（或不是可用地址）时退回市场页，
        // 保证「点了总能看到东西」，不会因为站点结构微调就变成死按钮。
        UpdateClickAction.Download => string.IsNullOrWhiteSpace(result.DownloadUrl)
            ? PluginUpdate.MarketPageUrl
            : result.DownloadUrl,
        UpdateClickAction.Clipboard => UpdateSummary(result),
        _ => PluginUpdate.MarketPageUrl,
    };

    /// <summary>复制到剪贴板的内容（含版本与下载地址，用户可以直接粘给别人）。</summary>
    public static string UpdateSummary(UpdateCheckResult result)
    {
        var download = string.IsNullOrWhiteSpace(result.DownloadUrl)
            ? PluginUpdate.MarketPageUrl
            : result.DownloadUrl;

        return $"局域网推送网桥 有新版本：{pluginTitle(result)}"
             + $"{NL}当前版本：v{result.LocalVersion}"
             + $"{NL}最新版本：v{result.RemoteVersion}"
             + $"{NL}插件市场：{PluginUpdate.MarketPageUrl}"
             + $"{NL}下载地址：{download}";
    }

    private static string pluginTitle(UpdateCheckResult result)
        => string.IsNullOrEmpty(result.RemoteVersion) ? "（版本未知）" : $"v{result.RemoteVersion}";

    private static string NL => Environment.NewLine;

    // ====================================================================
    // 通知文案（宿主 Toast）
    // ====================================================================

    /// <summary>
    /// 宿主的原生 Toast 只用来「报一声」——它的点击被宿主绑死到活动应用唤醒上，
    /// 插件无法接管，因此文案里不再写「点击打开下载页」这种做不到的承诺，
    /// 而是把用户引导到**真正能点**的那条提示条上。
    /// </summary>
    public static string ToastTitle(UpdateCheckResult result) => $"推送网桥有新版本 v{result.RemoteVersion}";

    public static string ToastBody(UpdateCheckResult result, UpdateBannerMode mode)
    {
        // 复制模式：链接会在提示结束后自动进剪贴板，用户什么都不用做 —— 这条要在文案里说清
        var suffix = mode switch
        {
            UpdateBannerMode.Off => "可到配置界面查看",
            UpdateBannerMode.AutoDismiss => "岛右侧提示条仅供查看",
            _ => "点岛右侧提示条继续",
        };
        return $"当前 v{result.LocalVersion} · {suffix}";
    }
}
