using SkiaSharp;

namespace NpsPushBridge;

/// <summary>
/// 配置面板使用的配色（设置页由宿主主题映射而来，独立窗口使用固定深色）。
///
/// ★ 本类型曾经有一个 <c>FromRender(RenderTheme)</c> ★
/// 它把宿主的 <c>NotchPeninsula.Plugins.RenderTheme</c> 映射成本配色，但**从未被调用过**
/// （设置页用的是下面的 <see cref="Window"/>：宿主设置窗口固定深色，不跟随岛主题）。
/// 2026-09-23 删除 —— 它是本文件对宿主 API 的**唯一**引用，去掉之后
/// <c>ConfigPanel</c> 就完全不依赖宿主程序集了，于是可以被验证工程直接编译与实例化。
/// 这件事的价值不是「少一个方法」，而是**端到端测试成为可能**：
/// 面板的绘制 / 命中 / 键盘输入 / 保存链路现在能在 <c>BridgeHarness</c> 里用假宿主跑通
/// （见 <c>PanelSmokeTests</c>），不必靠人工开窗口点。
/// </summary>
internal readonly record struct PanelTheme(
    SKColor Text, SKColor SubText, SKColor Background, SKColor Accent, SKColor Box,
    // ── WinUI 3 / Fluent 扩展槽（2026-09-25 加入，取值对齐 WinUI 3 深色主题的 ThemeResources）──
    SKColor Card, SKColor ControlFill, SKColor ControlFillHover, SKColor ControlFillPressed,
    SKColor Stroke, SKColor Divider, SKColor AccentFill, SKColor Error, SKColor TextTertiary)
{
    /// <summary>
    /// 独立窗口使用：**WinUI 3 深色主题**（Fluent Design）。
    ///
    /// 语义与前 5 槽保持兼容（面板各处已在用），新增 9 槽供控件样式使用。
    /// 取值对照 WinUI 3 的 ThemeResources（深色）：
    /// <list type="bullet">
    /// <item><c>SolidBackgroundFillColorBase</c> = #202020 → <see cref="Background"/></item>
    /// <item><c>CardBackgroundFillColorDefault</c> = #FFFFFF0D → <see cref="Card"/></item>
    /// <item><c>ControlFillColorDefault/Secondary/Tertiary</c> = #FFFFFF0F / 15 / 0A → 三个 ControlFill</item>
    /// <item><c>ControlStrokeColorDefault</c> = #FFFFFF12 → <see cref="Stroke"/></item>
    /// <item><c>DividerStrokeColorDefault</c> = #FFFFFF15 → <see cref="Divider"/></item>
    /// <item><c>TextFillColorPrimary/Secondary/Tertiary</c> = #FFFFFF / C8 / 8B → Text / SubText / <see cref="TextTertiary"/></item>
    /// <item><c>AccentTextFillColorPrimary</c> = #60CDFF → <see cref="Accent"/></item>
    /// <item><c>AccentFillColorDefault</c> = #0078D4 → <see cref="AccentFill"/></item>
    /// <item><c>SystemFillColorCritical</c> = #FF99A4 → <see cref="Error"/></item>
    /// </list>
    /// ⚠️ 面板底色是**实色** #202020（不是 Mica），所以这些「半透明白」会正确地叠出层次：
    /// 6% ≈ #2D2D2D、8% ≈ #303030 —— 与 WinUI 3 在实底上的观感一致。
    /// </summary>
    public static PanelTheme Window => new(
        Text: new SKColor(255, 255, 255),                    // TextFillColorPrimary
        SubText: new SKColor(255, 255, 255, 200),            // TextFillColorSecondary（78%）
        Background: new SKColor(32, 32, 32),                 // #202020 SolidBackgroundFillColorBase
        Accent: new SKColor(96, 205, 255),                   // #60CDFF AccentTextFillColorPrimary
        Box: new SKColor(255, 255, 255, 15),                 // ControlFillColorDefault（兼容旧引用）
        Card: new SKColor(255, 255, 255, 13),                // CardBackgroundFillColorDefault
        ControlFill: new SKColor(255, 255, 255, 15),         // 6%
        ControlFillHover: new SKColor(255, 255, 255, 21),    // 8%
        ControlFillPressed: new SKColor(255, 255, 255, 10),  // 4%
        Stroke: new SKColor(255, 255, 255, 18),              // 7%
        Divider: new SKColor(255, 255, 255, 21),             // 8%
        AccentFill: new SKColor(0, 120, 212),                // #0078D4 AccentFillColorDefault
        Error: new SKColor(255, 153, 164),                   // #FF99A4 SystemFillColorCritical
        TextTertiary: new SKColor(255, 255, 255, 139));      // TextFillColorTertiary（54%）
}

/// <summary>配置面板绘制所需的一份数据快照（由插件在每次绘制时提供，线程安全）。</summary>
internal readonly record struct ConfigSnapshot(
    string WhitelistRaw,
    int Port,
    bool ServiceRunning,
    string WhitelistDescription,
    long Received,
    long Forwarded,
    long Rejected,
    long Failed,
    string[] LogLines,
    string? LastError,
    /// <summary>面板（详情页）显示时长，秒（已由 <see cref="CollapseDuration.Normalize"/> 规范化）。</summary>
    float CollapseSeconds,
    /// <summary>是否「忽略外部鼠标操作（不自动收起）」。默认 false = 宿主默认行为。</summary>
    bool CollapseIgnoreMouse);

/// <summary>配置面板与插件之间的接口，避免面板直接依赖插件实现。</summary>
internal interface IBridgePanelHost
{
    ConfigSnapshot Snapshot();
    void SaveConfig(string whitelistRaw, int port);

    /// <summary>
    /// 设置「面板显示时长」（秒）。面板拖动滑块时**实时**调用（值已规范化到 0.5~60、2 位小数）。
    ///
    /// ★ 为什么实时写、而不像端口那样「点保存才生效」★
    /// 滑块是**连续**控件，用户拖动时就应该能立刻在下一次展开详情页时看到效果；
    /// 而且它只有一个数值、不存在「改了一半」的中间态，实时落盘不会产生不一致。
    /// </summary>
    void SetCollapseSeconds(float seconds);

    /// <summary>
    /// 设置「忽略外部鼠标操作（不自动收起）」。面板点开关时立即调用。
    /// 开启后详情页不再自动收起（鼠标离开不收、点到岛外也不收）。
    /// </summary>
    void SetCollapseIgnoreMouse(bool ignore);
    void ToggleService();
    /// <summary>最近一次建立连接的来源 IP（含被拒绝的），用于一键加入白名单。</summary>
    string? LastRemoteIp();
    /// <summary>本机局域网 IPv4 地址（用于日志提示与排查）。</summary>
    string[] DetectLocalIPv4();
    /// <summary>把文本写入系统剪贴板（日志复制用），返回是否成功。</summary>
    bool CopyText(string text);
    /// <summary>
    /// 读出系统剪贴板里的文本（图标映射粘贴用）；剪贴板为空或没有文本时返回空串。
    /// 读取同样受 STA 与「剪贴板被占用」约束，实现方须自行处理（见插件侧的 <c>PasteText</c>）。
    /// </summary>
    string PasteText();
    void Note(string message);

    /// <summary>
    /// 配置窗口当前是否**置顶**。
    ///
    /// 需求是「取消窗口置顶，改为点击按钮才置顶」—— 因此默认是 false。
    /// 面板只据此画按钮的选中态，**状态由真正的窗口给出**（不在面板里自记一份），
    /// 否则按钮显示的状态可能与窗口实际行为不一致。
    /// </summary>
    bool Topmost { get; }

    /// <summary>切换配置窗口置顶。</summary>
    void SetTopmost(bool value);

    // ── 自动更新 ──
    /// <summary>当前更新状态（当前版本 / 最新版本 / 结果 / 市场页 / 下载直链）。</summary>
    UpdateCheckResult UpdateInfo();
    /// <summary>立即触发一次检查（异步执行，不阻塞界面）。</summary>
    void CheckUpdateNow();
    /// <summary>把「更新信息」（版本 + 市场页 + 下载地址）复制到剪贴板。</summary>
    void CopyUpdateInfo();
    /// <summary>用默认浏览器打开插件市场页。</summary>
    void OpenMarketPage();
    /// <summary>用默认浏览器打开下载直链；无直链时退回市场页。</summary>
    void OpenDownload();
    /// <summary>灵动岛更新提示条的显示方式。</summary>
    UpdateBannerMode CurrentBannerMode { get; }
    /// <summary>设置提示条显示方式（持久化）。</summary>
    void SetBannerMode(UpdateBannerMode mode);
    /// <summary>点击提示条后要执行的动作。</summary>
    UpdateClickAction CurrentClickAction { get; }
    /// <summary>设置点击提示条后的动作（持久化）。</summary>
    void SetClickAction(UpdateClickAction action);

    // ── 一键静默升级 ──
    /// <summary>「一键静默升级」开关是否开启（持久化，默认关）。</summary>
    bool SilentUpgradeEnabled { get; }
    /// <summary>设置静默升级开关（持久化）。开启后会立刻尝试安装已有的新版本。</summary>
    void SetSilentUpgrade(bool enabled);
    /// <summary>当前升级阶段（界面按它显示进度 / 状态）。</summary>
    UpgradeStage CurrentUpgradeStage { get; }
    /// <summary>升级状态的一整行说明（含进度百分比 / 失败原因 / 重试倒计时语义）。</summary>
    string UpgradeStatusText();
    /// <summary>立即执行一次静默升级（会先清掉重试计数与「试过该版本」的记忆）。</summary>
    void UpgradeNow();

    // ── 在线文档（配置教程 / 更新日志）──
    /// <summary>
    /// 用默认浏览器打开**官网的配置教程页**（<see cref="PluginUpdate.GuidePageUrl"/>）。
    ///
    /// ★ 为什么由宿主实现而不是面板自己做 ★
    /// 起进程唤起浏览器是有副作用的系统动作，且要写日志（用户得知道打开了哪个网址、失败为什么）。
    /// 面板不持有日志与进程知识，所以这里承担全部实际动作，面板只上报「用户点了」这一件事。
    ///
    /// ★ 文档为什么不放在插件里 ★
    /// 教程与更新日志统一维护在网站仓库，插件只给一个入口（见 <see cref="PluginUpdate.GuidePageUrl"/> 的说明）。
    /// 于是文档改动**不需要再发一次插件**，也不存在「dll 里的副本过期」这种问题。
    /// </summary>
    void OpenTutorial();

    /// <summary>
    /// 用默认浏览器打开**官网的更新日志页**（<see cref="PluginUpdate.ChangelogPageUrl"/>）。
    ///
    /// 与 <see cref="OpenTutorial"/> 同一套做法（同一个站点、同一个理由）。
    /// 独立成一个成员而不是复用 <see cref="OpenTutorial"/>：两处打开的地址不同，
    /// 界面上的按钮也各有各的日志文案，混在一起以后加第三个入口时就没法区分了。
    /// </summary>
    void OpenChangelog();

    // ── 图标映射 ──
    /// <summary>
    /// 当前生效的自定义「应用 → icon」映射原文（一行一条 <c>应用名=icon值</c>），供配置界面编辑。
    /// </summary>
    string IconOverridesRaw();
    /// <summary>保存自定义图标映射（一行一条）。空串表示清空自定义、只用内置映射。</summary>
    void SaveIconOverrides(string raw);

    // ── 应用黑名单 ──
    /// <summary>
    /// 当前生效的应用黑名单原文（一行一个应用名或包名），供配置界面的「黑名单」页编辑。
    ///
    /// 与 <see cref="IconOverridesRaw"/> 同样是**独立入口**而不是塞进 <see cref="ConfigSnapshot"/>：
    /// 子页有自己的「保存」按钮，读写成对出现才不至于「主页面的保存顺手改掉了子页的配置」。
    /// </summary>
    string AppBlacklistRaw();
    /// <summary>保存应用黑名单（一行一条）。空串表示不拦截任何应用。</summary>
    void SaveAppBlacklist(string raw);
}

/// <summary>
/// 插件配置面板：负责「白名单 / 端口 / 保存 / 开启服务 / 日志」五个区块的绘制与交互。
///
/// 同一份面板同时被两处复用：
///   · <see cref="PushBridgeSettingsPage"/> —— 宿主设置窗口里的自定义设置页（仅鼠标事件）；
///   · 插件自有窗口（宿主尚未接线设置页时兜底可用，鼠标 + 键盘）。
///
/// 因此这里刻意做成「不依赖键盘也能完成全部操作」：白名单用「＋最近来源 / 逐行删除 / 清空」维护，
/// 端口用「−10 / −1 / +1 / +10」微调；键盘（在自有窗口中可用）只是额外的便捷输入方式。
/// </summary>
internal sealed class ConfigPanel
{
    /// <summary>
    /// 当前键盘输入落在哪个输入框上。
    ///
    /// ★ 为什么「图标映射」要分成两个 ★ 那一页的每条映射现在有**两个**输入框
    /// （应用名 / 图标值），敲键盘时必须知道往哪一个里写字。宿主只给字符、不给
    /// Tab 之类的按键，所以「在哪个框」只能由**鼠标点选**或**回车递进**来定
    /// （见 <see cref="OnCharCore"/> 的回车分支）。
    /// </summary>
    private enum Focus { None, Whitelist, Port, IconMapKey, IconMapValue, Blacklist }

    /// <summary>图标映射页是否有输入焦点（两个框都算）。</summary>
    private bool IsIconMapFocused => _focus is Focus.IconMapKey or Focus.IconMapValue;

    private enum HitId
    {
        None, WhitelistBox, LineRemove, LastSource, Clear, ScrollUp, ScrollDown,
        /// <summary>白名单「＋添加」：新增一个空的输入框。</summary>
        WhitelistAdd,
        PortField, PortMinus10, PortMinus1, PortPlus1, PortPlus10, Save, Service, LogBox,
        /// <summary>
        /// 「手机端填」地址行右侧的「复制」按钮 —— 把拼好的 <c>http://IP:端口/</c> 写进剪贴板，
        /// 用户直接粘到手机端 SMSForwarder 的 Webhook Server 里。
        /// </summary>
        PhoneCopy,
        /// <summary>「面板显示时长」滑块（按下即跳到该位置并**持续跟随**拖动）。</summary>
        CollapseSlider,
        /// <summary>「忽略外部鼠标操作（不自动收起）」开关（整行都可点）。</summary>
        CollapseIgnore,
        LogCopy, LogScrollUp, LogScrollDown,
        /// <summary>标题行的「置顶」开关（取消默认置顶后，这是把它置顶的唯一入口）。</summary>
        TopmostToggle,
        UpdateCheck, UpdateCopy, UpdateOpen,
        UpdateModeOff, UpdateModeAuto, UpdateModeClick,
        UpdateActionMarket, UpdateActionClipboard, UpdateActionDownload,
        UpgradeToggleOn, UpgradeToggleOff, UpgradeNow,
        TutorialOpen,
        /// <summary>标题行的「更新日志」入口 —— 用浏览器打开官网的更新日志页（不再在窗口内分屏看 PNG）。</summary>
        ChangelogOpen,
        // ── 图标映射页 ──
        IconMapOpen, IconMapBack, IconMapBox, IconMapLineRemove, IconMapAdd, IconMapReset,
        IconMapScrollUp, IconMapScrollDown,
        /// <summary>映射行的「应用名」输入框。</summary>
        IconMapKeyField,
        /// <summary>映射行的「图标值」输入框。</summary>
        IconMapValueField,
        /// <summary>工具行「复制选中」按钮。</summary>
        IconMapCopy,
        /// <summary>工具行「粘贴」按钮。</summary>
        IconMapPaste,
        IconMapApply,
        // ── 应用黑名单页 ──
        /// <summary>标题行的「黑名单」入口 —— 切到独立的应用黑名单页。</summary>
        BlacklistOpen,
        BlacklistBack,
        BlacklistBox,
        BlacklistLineRemove,
        BlacklistAdd,
        BlacklistScrollUp,
        BlacklistScrollDown,
        /// <summary>黑名单某一行的输入框（点它开始编辑）。</summary>
        BlacklistLine,
        /// <summary>黑名单页的「保存名单」按钮。</summary>
        BlacklistApply,
    }

    /// <summary>
    /// 面板当前显示哪一页。
    ///
    /// ★ 为什么用「同尺寸切页」而不是往主页面里再塞几行 ★
    /// 主页面纵向每一行的推进量都被写进了 <see cref="ConfigLayout.FixedTopHeight"/> 与
    /// <see cref="ConfigLayout.MinRecommendedHeight"/> 两个**必须手动同步**的 const，
    /// 而窗口高度又是固定的（<c>ConfigPanelDefaults</c>）。再加一行就要同时改三处常量、
    /// 并有把日志区挤到最小高度以下的风险。图标映射本质是「低频的编辑工作」，
    /// 用整页替换主内容区既给了它足够的编辑空间，也**完全不触碰既有布局算术** ——
    /// 主页面仍然是原样，改动风险降到最低。
    /// </summary>
    /// <summary>
    /// 面板当前显示哪一页。两页共用同一套纵向布局（**整页替换、同尺寸**），
    /// 切页不改变任何行的位置 —— 所以都不牵动 <c>FixedTopHeight</c>。
    /// </summary>
    private enum PanelPage { Main, IconMap, Blacklist }

    /// <summary>
    /// 逐行删除按钮上画的字符。
    ///
    /// ★ 必须选一个「所有中文字体都有的字形」★ 因为面板字体是通过
    /// <see cref="PluginFonts"/> 动态挑出来的（本机通常是雅黑，但别的系统可能是宋体等），
    /// 字形一旦缺失，Skia 不会报错、只会画一个空心方格（.notdef），用户看到的就是「按钮上是个方框」。
    /// 实测对比：<c>✕</c>(U+2715) 雅黑里没有；<c>×</c>(U+00D7) 属拉丁 1 区，
    /// 踩过坑的雅黑 / 宋体 / Segoe UI 全都有 —— 所以用 ×。
    /// 这也是为什么它是常量而不是就地写死的字面量：字体一旦再调整，只需要改这一个地方。
    /// </summary>
    private const string RemoveGlyph = "×";

    private readonly IBridgePanelHost _host;
    private readonly Action _invalidate;
    /// <summary>面板状态可能被 UI 线程（鼠标/键盘）与渲染线程（设置页绘制）同时访问，统一加锁。</summary>
    private readonly object _sync = new();

    private readonly List<string> _lines = new();
    private string _portText = "1403";

    /// <summary>
    /// 上一份**已保存**的端口 —— 只用于「端口输入框正在被编辑成非法值时」给地址行兜底
    /// （见 <see cref="PhoneAddress.Build(string?, string?, int)"/> 里关于回退值的说明）。
    /// </summary>
    private int _savedPort = PhoneAddress.DefaultPort;

    // ── 手机端地址行 ──
    /// <summary>
    /// 本机可用的局域网 IPv4（宿主负责枚举与排序，第一个就是「最可能能上网的那张网卡」）。
    ///
    /// ★ 为什么要缓存 ★
    /// 面板**每帧重绘**，而枚举网卡要遍历 <c>NetworkInterface</c>（几十微秒~毫秒级，还可能触发系统调用）。
    /// 每帧枚举是纯浪费，但这行又必须跟得上「换 Wi-Fi / 插网线」——
    /// 所以按 1 秒的节奏刷新：用户改端口的**同一帧**就能看到地址变化，换网卡最多 1 秒后更新。
    /// </summary>
    private string[] _localIps = Array.Empty<string>();
    /// <summary>上一次取 IP 的时刻（<see cref="Environment.TickCount64"/>）。</summary>
    private long _localIpsAt;

    /// <summary>IP 刷新间隔（毫秒）。见 <see cref="_localIps"/> 的说明。</summary>
    private const int LocalIpsRefreshMs = 1000;
    private Focus _focus = Focus.None;
    private int _activeLine = -1;
    private int _scroll;
    /// <summary>日志从最新一行往回上翻的可视行数（0 = 跟随最新）。单位是**折行后**的行。</summary>
    private int _logScroll;

    /// <summary>
    /// 上一帧日志绘制时实际收集到的**折行后**总行数（用于判断上翻按钮是否可用）。
    /// ⚠️ 与 <see cref="_logScroll"/> 必须同口径 —— 见 <see cref="MaxLogScroll"/> 的说明。
    /// 初值 0：首帧绘制前按钮自然置灰。
    /// </summary>
    private int _lastWrappedRowCount;
    private bool _dirty;

    private ConfigLayout _layout = ConfigLayout.Compute(ConfigLayout.MinContentWidth, ConfigLayout.MinRecommendedHeight);
    private (HitId Id, int Index) _hover = (HitId.None, -1);

    // ── 交互动画（对齐 WinUI 3 的隐式过渡）───────────────────────────────
    /// <summary>悬停过渡时长：Fluent 的交互反馈约 150ms。设为 0 可关闭过渡（退化为即时变色）。</summary>
    /// <remarks>刻意用 <c>static readonly</c> 而不是 <c>const</c>：让下面那个「<= 0 就跳过动画」的分支
    /// 保持可达 —— 写成 <c>const</c> 会被编译器折叠成恒假，产生 CS0162 无法访问代码的警告。</remarks>
    private static readonly float HoverAnimMs = 150f;
    /// <summary>悬停目标最近一次改变的时刻，用于把颜色从旧值**连续**渐变到新值。</summary>
    private long _hoverChangedAt;
    /// <summary>上一个悬停目标：鼠标移开时由它负责「渐出」（从悬停色退回常态）。</summary>
    private (HitId Id, int Index) _hoverPrev = (HitId.None, -1);
    private (HitId Id, int Index) _pressed = (HitId.None, -1);

    // ── 输入框的编辑态（光标 / 选区 / 滚动 / 闪烁 / 按键连跳）──
    //
    // ★ 只保留**一个** _edit，绑定到「当前聚焦的那个框」★
    // 面板里同时在屏幕上的是 N 个白名单框 + 2 个映射框，但**任何时刻只有一个是焦点**
    // （宿主不给 Tab，焦点完全由鼠标决定）。为每个框各存一个编辑态既浪费又容易不同步
    // （改了模型忘了改编辑态），所以：**聚焦时把模型文本装进来，每次改动立刻写回模型**。
    /// <summary>当前聚焦框的编辑模型（文本 + 光标 + 选区）。</summary>
    private readonly TextEdit _edit = new();
    /// <summary>聚焦框的水平滚动偏移（内容比框宽时，让光标始终可见）。切换焦点时归零。</summary>
    private float _editScroll;
    /// <summary>左键已按下且落在输入框内 —— 拖动即为「扩选」。宿主不给按键状态，只能自己记。</summary>
    private bool _dragging;
    /// <summary>上一次左键按下的时刻与位置，用于**自己实现双击**（宿主窗口类没有 CS_DBLCLKS，收不到 WM_LBUTTONDBLCLK）。</summary>
    private long _lastClickAt;
    private float _lastClickX, _lastClickY;
    /// <summary>方向键长按的自动重复（系统那份重复事件我们拿不到，见 <see cref="KeyRepeat"/>）。</summary>
    private readonly KeyRepeat _repeat = new();
    /// <summary>光标当前是否处于「可见」相位（由 <see cref="Tick"/> 翻转，绘制照它决定画不画）。</summary>
    private bool _caretOn = true;
    /// <summary>
    /// 在这时刻之前强制显示光标（毫秒）。
    ///
    /// ★ 为什么需要它 ★ 光标闪烁是按绝对时间取相位算的（<c>now / 周期 % 2</c>）。
    /// 若刚打完字恰好落在「灭」的相位里，光标会**立刻消失**，看起来像「打字把光标弄丢了」。
    /// 所以任何编辑动作都把「强制可见」的时刻往后推一个周期 ——
    /// 这是所有编辑框的标准做法（活动即重置闪烁时钟）。
    /// </summary>
    private long _caretHoldUntil;
    /// <summary>
    /// 待解析的「点击落点」（相对文本起点的 x；NaN = 无）。
    ///
    /// ★ 为什么要延迟到绘制时再解析 ★ 把 x 换算成字符下标需要**文本度量**，而度量要用画笔 ——
    /// 画笔只存在于绘制过程中。与其在鼠标事件里另造一支画笔（还得保证与绘制用的字体一致），
    /// 不如把 x 存下来、在真正绘制这个框时用**同一次绘制的那支画笔**换算：
    /// 既保证了「量宽与画宽同一字体」这条硬约定，也避免了两处字体不一致导致的光标错位。
    /// </summary>
    private float _caretSeekX = float.NaN;
    /// <summary>待解析的「按下点」（锚点，相对文本起点的 x；NaN = 无）。</summary>
    private float _anchorSeekX = float.NaN;
    /// <summary>解析这次的落点时是否扩选（拖选 / Shift+点击）。</summary>
    private bool _caretSeekExtend;

    // ── 当前页 ──
    /// <summary>当前页（主页面 / 图标映射页）。</summary>
    private PanelPage _page = PanelPage.Main;

    // ── 提醒时长滑块 ──
    /// <summary>
    /// 滑块当前值（秒）。拖动过程中**只改这里、不每帧写盘**，抬手时才落盘一次 ——
    /// 拖动每帧都会产生鼠标移动，若每帧都持久化就是几十次无谓的写。
    /// </summary>
    private float _collapseSeconds = CollapseDuration.DefaultSeconds;
    /// <summary>是否「忽略外部鼠标操作（不自动收起）」。同样以宿主配置为准（点开关时立刻落盘）。</summary>
    private bool _collapseIgnoreMouse;
    /// <summary>是否正在拖动滑块（按下后移动即持续跟随；抬手结束）。</summary>
    private bool _sliderDragging;
    /// <summary>图标映射的编辑缓冲（一行一条「应用名=icon值」）。</summary>
    private readonly List<string> _iconLines = new();
    private int _iconActiveLine = -1;
    /// <summary>图标映射页的滚动位置 —— 映射可能几十条，一屏放不下。</summary>
    private int _iconScroll;
    /// <summary>图标映射是否有未保存改动（决定「保存映射」按钮是否加 *）。</summary>
    private bool _iconDirty;

    /// <summary>应用黑名单的编辑缓冲（一行一个应用名或包名）。</summary>
    private readonly List<string> _blacklistLines = new();
    private int _blacklistActiveLine = -1;
    /// <summary>黑名单页的滚动位置 —— 名单可能几十条，一屏放不下。</summary>
    private int _blacklistScroll;
    /// <summary>黑名单是否有未保存改动（决定「保存名单」按钮是否加 *）。</summary>
    private bool _blacklistDirty;

    /// <summary>
    /// 待用户二次确认的粘贴计划（null = 当前没有待确认的粘贴）。
    ///
    /// ★ 为什么要把计划**存下来**而不是「发现冲突就重新问一次」★
    /// 用户点「确认」时，中间可能已经改过缓冲（甚至又粘了一次）。若确认时重新解析剪贴板，
    /// 用的就是**那一刻的剪贴板内容**——用户确认的是「刚才看到的那份冲突清单」，
    /// 执行的却可能是另一份数据。把计划钉在字段里，保证「所见即所执行」。
    /// 任何会改动缓冲的操作都必须把它清空（见 <see cref="CancelPendingPaste"/>）。
    /// </summary>
    private (IconClipboard.PastePlan Plan, int ActiveLine)? _iconPendingPaste;

    /// <summary>图标映射页的状态提示（显示在标题行右侧，如「已复制」「N 条冲突待确认」）。</summary>
    private string _iconFlash = "";

    /// <summary>清掉待确认的粘贴计划（缓冲被改动、或用户取消时调用）。</summary>
    private void CancelPendingPaste() => _iconPendingPaste = null;

    public ConfigPanel(IBridgePanelHost host, Action invalidate)
    {
        _host = host;
        _invalidate = invalidate;
        ReloadCore();
    }

    // ====================================================================
    // 对外入口：统一加锁（设置页在宿主渲染线程绘制，鼠标/键盘在 UI 线程派发）
    // ====================================================================

    public void Reload() { lock (_sync) ReloadCore(); }

    public void Draw(SKCanvas canvas, SKRect rect, PanelTheme theme, bool fillBackground)
    { lock (_sync) DrawCore(canvas, rect, theme, fillBackground); }

    public void OnMouseMove(float x, float y) { lock (_sync) OnMouseMoveCore(x, y); }

    public void OnMouseDown(float x, float y) { lock (_sync) OnMouseDownCore(x, y); }

    public void OnMouseUp(float x, float y) { lock (_sync) OnMouseUpCore(x, y); }

    public void OnChar(char ch) { lock (_sync) OnCharCore(ch); }

    // ====================================================================
    // 编辑缓冲
    // ====================================================================

    /// <summary>从插件当前配置重新载入编辑缓冲（打开配置界面时、保存成功后调用）。</summary>
    /// <summary>
    /// 丢弃编辑态：模型被整体重载（保存后 / 从插件重读配置）或离开当前页时调用。
    ///
    /// ⚠️ 必须与模型一起复位 ★ 编辑模型里存的是**某一行文本的一份拷贝**，
    /// 模型被整体重建后（比如保存后从注册表重读）那份拷贝就过期了；
    /// 若不清掉，用户下一次输入会把**旧文本**写回刚载入的新配置 —— 等于用陈旧内容覆盖刚保存的结果。
    /// </summary>
    private void ResetEditState()
    {
        _focus = Focus.None;
        _edit.SetText("");
        _edit.MaxLength = 0;
        _edit.CharFilter = null;
        _editScroll = 0f;
        _dragging = false;
        _caretSeekX = float.NaN;
        _anchorSeekX = float.NaN;
        _repeat.Reset();
    }

    private void ReloadCore()
    {
        ResetEditState();
        var snapshot = _host.Snapshot();

        _lines.Clear();
        foreach (var raw in snapshot.WhitelistRaw.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length > 0) _lines.Add(line);
        }
        if (_lines.Count == 0) _lines.Add("");

        _portText = snapshot.Port.ToString();
        _savedPort = snapshot.Port;
        _scroll = 0;
        _activeLine = 0;
        _dirty = false;

        // 图标映射缓冲：从插件当前配置重建
        _iconLines.Clear();
        foreach (var raw in _host.IconOverridesRaw().Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length > 0) _iconLines.Add(line);
        }
        _iconScroll = 0;
        _iconActiveLine = _iconLines.Count > 0 ? 0 : -1;
        _iconDirty = false;

        // 应用黑名单缓冲：同样从插件当前配置重建
        ReloadBlacklistBuffer();
    }

    // ====================================================================
    // 绘制
    // ====================================================================

    private void DrawCore(SKCanvas canvas, SKRect rect, PanelTheme theme, bool fillBackground)
    {
        // 教程已改为「导出到浏览器」呈现，面板不再有分屏态：
        // 左侧布局恒按窗口实际宽度计算（与引入教程之前逐像素一致）。
        var layout = ConfigLayout.Compute(rect.Width, rect.Height);
        _layout = layout;
        // 窗口可缩放 ⇒ 每次布局重算后都要把滚动位置夹回合法范围（缩小时曾滚到条数之外会让列表看起来是空的）
        ClampScrollToLayout();

        var snapshot = _host.Snapshot();

        // 滑块值以宿主里的配置为准 —— **但拖动过程中除外**：
        // 拖一次会触发很多帧重绘，若每帧都从宿主回读，就会把我们刚拖到的值弹回旧值（表现为「滑块拽不动」）。
        if (!_sliderDragging) _collapseSeconds = snapshot.CollapseSeconds;
        _collapseIgnoreMouse = snapshot.CollapseIgnoreMouse;

        canvas.Save();
        canvas.Translate(rect.Left, rect.Top);
        if (layout.WidthClamped) canvas.ClipRect(new SKRect(0, 0, rect.Width, rect.Height));

        // 每帧临时创建画笔、画完即释放 —— 绝不做跨线程缓存（见文档「踩坑」第 1 条）。
        // 字体走 PluginFonts 的共享缓存：SKTypeface 不可变、可跨线程共享，且只查一次，
        // 比每帧 FromFamilyName 少一次原生字体查找，也避免「找不到时静默退化成方框」。
        //
        // ⚠️ **这里刻意不写 `using`** ⚠️ PluginFonts.Cjk 返回的是**进程内共享的缓存实例**
        //    （见 PluginFonts 的「从不 Dispose 缓存里的实例」约定）。若写成
        //    `using var typeface = PluginFonts.Cjk;`，本方法每返回一次就把那个共享实例
        //    释放一次 —— 之后每一帧、每一处取到的都是**已释放**的对象，
        //    所有文字会一起失效（而且它长得像「资源管理得当」，极难看出）。
        //    下面三个 paint 是每帧新建的、归本方法所有，用 using 释放它们才是对的。
        var typeface = PluginFonts.Cjk;
        using var fill = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill };
        using var stroke = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1f };
        using var text = new SKPaint { IsAntialias = true, Typeface = typeface, TextSize = 12f };
        using var textSmall = new SKPaint { IsAntialias = true, Typeface = typeface, TextSize = 11f };
        using var textTitle = new SKPaint { IsAntialias = true, Typeface = typeface, TextSize = 13.5f };

        if (fillBackground)
        {
            fill.Color = theme.Background;
            canvas.DrawRect(new SKRect(0, 0, rect.Width, Math.Max(layout.ContentHeight, rect.Height)), fill);
        }

        // 图标映射页 / 应用黑名单页都整页替换主内容区（同尺寸，见 PanelPage 的说明）
        if (_page == PanelPage.IconMap)
        {
            DrawIconMapPage(canvas, layout, theme, fill, stroke, text, textSmall, textTitle);
            canvas.Restore();
            return;
        }

        if (_page == PanelPage.Blacklist)
        {
            DrawBlacklistPage(canvas, layout, theme, fill, stroke, text, textSmall, textTitle);
            canvas.Restore();
            return;
        }

        DrawTitle(canvas, layout, snapshot, theme, fill, text, textTitle);
        DrawStatusSummary(canvas, layout, snapshot, theme, fill, stroke, textSmall);
        DrawWhitelist(canvas, layout, theme, fill, stroke, textSmall);
        DrawPort(canvas, layout, theme, fill, stroke, text, textSmall);
        DrawPhoneAddress(canvas, layout, theme, fill, stroke, text, textSmall);
        DrawCollapse(canvas, layout, theme, fill, stroke, text, textSmall);
        DrawButtons(canvas, layout, snapshot, theme, fill, text);
        DrawUpdate(canvas, layout, theme, fill, stroke, text, textSmall);
        DrawLog(canvas, layout, snapshot, theme, fill, stroke, textSmall);

        canvas.Restore();
    }

    private void DrawTitle(SKCanvas canvas, ConfigLayout layout, ConfigSnapshot snapshot, PanelTheme theme,
        SKPaint fill, SKPaint text, SKPaint textTitle)
    {
        textTitle.Color = theme.Text;
        // 标题文字的可用宽度**由右侧按钮的位置反推**（见 ConfigLayout.TitleTextWidth）——
        // 标题行右侧后来又加了「更新日志」入口，写死留白（原来的 Width - 200）已经不够，
        // 会让标题压到按钮底下。
        DrawTextInRect(canvas, "局域网推送网桥 · 配置", layout.Title.Left, layout.Title, textTitle,
            ConfigLayout.TitleTextWidth(layout));

        // 右上角（从右往左排）：运行状态胶囊 ← 置顶开关 ←「更新日志」←「黑名单」，
        // 几何全部取自 ConfigLayout（标题行是纯计算区，加按钮不占纵向）
        DrawButton(canvas, ConfigLayout.BlacklistButtonOf(layout), "黑名单", theme, fill, text,
            HoverT(HitId.BlacklistOpen), true);

        DrawButton(canvas, ConfigLayout.ChangelogButtonOf(layout), "更新日志", theme, fill, text,
            HoverT(HitId.ChangelogOpen), true);

        // 右上角：「置顶」开关 + 运行状态胶囊（两者几何都取自 ConfigLayout，避免各写一份宽度）
        bool topmost = _host.Topmost;
        DrawButton(canvas, ConfigLayout.TopmostButtonOf(layout),
            topmost ? "已置顶" : "置顶", theme, fill, text,
            HoverT(HitId.TopmostToggle), true,
            accent: topmost, accentColor: new SKColor(120, 150, 200));

        // 右上角状态胶囊：运行中 / 已停止
        bool running = snapshot.ServiceRunning;
        var pillColor = running ? new SKColor(76, 175, 80) : new SKColor(150, 150, 150);
        var pill = ConfigLayout.StatusPillOf(layout);

        fill.Color = pillColor.WithAlpha(38);
        canvas.DrawRoundRect(pill, 9f, 9f, fill);
        fill.Color = pillColor;
        canvas.DrawCircle(pill.Left + 11f, pill.MidY, 3.5f, fill);

        text.Color = theme.Text;
        text.TextSize = 11f;
        DrawTextInRect(canvas, running ? "运行中" : "已停止", pill.Left + 19f, pill, text, pill.Width - 22f);
        text.TextSize = 12f;
    }

    /// <summary>
    /// 状态摘要行：承接原先显示在灵动岛上的信息（端口 / 已转发条数），外加最近一次连接的来源 IP。
    /// 岛内现在只剩一盏状态灯，所有数值都在这里看。
    /// </summary>
    private void DrawStatusSummary(SKCanvas canvas, ConfigLayout layout, ConfigSnapshot snapshot, PanelTheme theme,
        SKPaint fill, SKPaint stroke, SKPaint textSmall)
    {
        // 卡片底：WinUI 3 的 CardBackgroundFillColorDefault + OverlayCornerRadius(8) + 1px 描边
        WinUi.Card(canvas, layout.StatusSummary, theme, fill, stroke);

        var lastSource = _host.LastRemoteIp() ?? "—";
        textSmall.Color = theme.Text;

        // 右侧常驻显示**当前实际在用的字体**（见 PluginFonts.UiDescribe）。
        // 用户「字体怎么和宿主不一致」这类问题，只有把真正生效的字体写在界面上才可自查；
        // 文案与 <c>PluginFonts.TypefaceFor</c> 同源，不会出现「显示的是一套、画的是另一套」。
        string font = PluginFonts.UiDescribe();
        PluginFonts.Fit(textSmall, font);
        float fontWidth = Math.Min(textSmall.MeasureText(font), layout.StatusSummary.Width * 0.45f);

        textSmall.Color = theme.Text;
        DrawTextInRect(canvas,
            $"端口 {snapshot.Port} · 已转发 {snapshot.Forwarded} · 最近来源 {lastSource}",
            layout.StatusSummary.Left + 10f, layout.StatusSummary, textSmall,
            layout.StatusSummary.Width - 20f - fontWidth - 10f);

        textSmall.Color = theme.SubText;
        DrawTextInRect(canvas, font, layout.StatusSummary.Right - 10f - fontWidth, layout.StatusSummary, textSmall, fontWidth);
    }

    /// <summary>
    /// 校验等级 → 边框颜色。只给 Error / Warn 上色，Ok 用中性细边。
    ///
    /// ★ 为什么「合法」也上绿色 ★ 被否掉了：白名单一屏 3 条、映射一屏 5 条，
    /// 满屏绿框比满屏灰框更吵，反而看不出哪一条有问题。表单的通行做法是
    /// **只把问题标出来**，合法由「没有标记」表达。
    /// </summary>
    private static SKColor BorderFor(InputRules.Level level, PanelTheme theme) => level switch
    {
        InputRules.Level.Error => theme.Error,                        // #FF99A4 SystemFillColorCritical
        InputRules.Level.Warn => new SKColor(252, 225, 0),            // #FCE100 SystemFillColorCaution
        _ => theme.SubText.WithAlpha(90),
    };

    /// <summary>校验等级 → 提示文字颜色。</summary>
    private static SKColor TextFor(InputRules.Level level, PanelTheme theme) => level switch
    {
        InputRules.Level.Error => theme.Error,                        // 与底边线同源，不各写一份
        InputRules.Level.Warn => new SKColor(252, 225, 0),
        _ => theme.SubText,
    };

    /// <summary>
    /// 画**一个输入框** —— 白名单每条、端口、以及图标映射的「应用名 / 图标值」都走这里。
    ///
    /// 统一在一处画，是为了让所有输入框长成同一个样子、有同一套反馈：
    ///   · 边框颜色表达校验等级（见 <see cref="BorderFor"/>），聚焦时换成强调色；
    ///   · 空框显示**占位说明**（用途就写在用户要填的地方，不用去猜）；
    ///   · 有问题时把**原因**贴在框内右侧（就地解释，不用翻日志）；
    ///   · **选中高亮 + 闪烁光标 + 水平滚动** —— 也就是「它是个输入框」的那三件事。
    /// </summary>
    /// <param name="showReason">
    /// 是否在框内右侧画校验原因。白名单的框够宽（一整行）→ true；
    /// 图标映射的两个框很窄（应用名框约 200px）→ false，原因改由页面上方的动态说明行承载，
    /// 否则原因会把用户填的内容挤没。
    /// </param>
    private void DrawEntryField(SKCanvas canvas, SKRect field, string value, string placeholder,
        InputRules.Verdict verdict, bool focused, PanelTheme theme,
        SKPaint fill, SKPaint stroke, SKPaint text, bool showReason = true)
    {
        // ── 输入框底与描边：走 WinUI 3 的 TextBox 规格 ──
        //   ControlFill 底 + 圆角 4 + **底部强调线**（这是 Fluent 输入框最明显的辨识特征）：
        //   常态是 ControlStrokeColorDefault，聚焦变强调色并加粗到 2px，校验不通过时换成等级色。
        //   比原来的「整圈边框」更贴近 WinUI 3 的观感。
        var lineColor = verdict.Level is InputRules.Level.Error or InputRules.Level.Warn
            ? BorderFor(verdict.Level, theme)
            : (SKColor?)null;
        WinUi.Field(canvas, field, theme, fill, stroke, WinUi.State.Normal, focused, lineColor);

        // 先把「原因」要占的宽度量出来，值就只能用剩下的宽度 —— 否则两者会叠在一起。
        string reason = showReason && verdict.HasMessage ? verdict.Message : "";
        float reasonWidth = 0f;
        if (reason.Length > 0)
        {
            PluginFonts.Fit(text, reason);
            reasonWidth = Math.Min(text.MeasureText(reason), field.Width * 0.5f);
        }

        float textLeft = field.Left + FieldPadX;
        float textWidth = field.Width - 2 * FieldPadX - (reasonWidth > 0f ? reasonWidth + 8f : 0f);
        if (textWidth < 4f) textWidth = 4f;

        // ⚠️ 顺序要紧：<c>Fit</c> 必须发生在**量宽与落光标之前**。
        //   焦点框里正在进行编辑，光标位置要靠度量换算；若先落光标再用另一支字体画字，
        //   就会出现「光标与文字错位」—— 这是 <see cref="TextGeometry"/> 里点明的硬约定。
        PluginFonts.Fit(text, value);
        float Measure(string s) => text.MeasureText(s);

        bool editing = focused && _focus != Focus.None;
        if (editing)
        {
            ResolveCaretSeek(Measure);
            _editScroll = TextGeometry.ScrollToShow(
                TextGeometry.CaretOffset(Measure, _edit.Text, _edit.Caret), _editScroll, textWidth);
        }

        // 内容整体裁剪在框内（水平滚动后，超出的部分必须被切掉，不能画到框外去）
        canvas.Save();
        canvas.ClipRect(new SKRect(textLeft, field.Top + 2f, textLeft + textWidth, field.Bottom - 2f));

        float drawX = textLeft - (editing ? _editScroll : 0f);

        if (editing && _edit.HasSelection)
        {
            var (sx, sw) = TextGeometry.SelectionBox(Measure, _edit.Text, _edit.SelectionStart, _edit.SelectionEnd);
            fill.Color = theme.Accent.WithAlpha(76);
            canvas.DrawRect(new SKRect(drawX + sx, field.Top + 2.5f, drawX + sx + sw, field.Bottom - 2.5f), fill);
        }

        if (value.Length == 0)
        {
            text.Color = theme.SubText.WithAlpha(150);
            // 占位文字不参与滚动：它只是提示，滚动反而会让它跑到框外
            DrawTextInRect(canvas, placeholder, textLeft, field, text, textWidth);
        }
        else
        {
            text.Color = theme.Text;
            canvas.DrawText(value, drawX, VerticallyCenteredBaseline(text, field, value), text);
        }

        // ★ 光标 ★ 聚焦且处于「亮」的相位时画一根竖线。
        //   它是「这是个真输入框」最直接的证据，所以即使内容为空、只有占位文字，也要画。
        if (editing && _caretOn)
        {
            float caretX = drawX + TextGeometry.CaretOffset(Measure, _edit.Text, _edit.Caret);
            stroke.Color = theme.Text;
            canvas.DrawRect(new SKRect(caretX - 0.75f, field.Top + 3f, caretX + 0.75f, field.Bottom - 3f), stroke);
        }

        canvas.Restore();

        if (reasonWidth > 0f)
        {
            text.Color = TextFor(verdict.Level, theme);
            // x 取「右边界 - 量出的宽度」，配合 maxWidth = 该宽度，效果就是右对齐
            DrawTextInRect(canvas, reason, field.Right - FieldPadX - reasonWidth, field, text, reasonWidth);
        }
    }

    /// <summary>
    /// 图标映射页「当前选中条目」的校验结论 —— 用于页面上方的动态说明行。
    ///
    /// ⚠️ 没有选中条目时返回 <see cref="InputRules.Verdict.None"/> 而**不是**
    /// <c>default(Verdict)</c>：后者会让 <c>Message</c> 变成 null（见那个常量的说明）。
    /// </summary>
    private InputRules.Verdict ActiveIconVerdict()
    {
        if (_iconActiveLine < 0 || _iconActiveLine >= _iconLines.Count) return InputRules.Verdict.None;
        return InputRules.CheckIconRow(_iconLines[_iconActiveLine]);
    }

    /// <summary>
    /// 白名单区块：标题行（＋添加 / ＋最近来源 / 清空 / 上下翻）→ 常驻的规则提示行 → 逐条输入框。
    ///
    /// ★ 由「一个多行文本框」改成「每条一个输入框」★
    /// 原来是往一个框里敲多行文本、由回车自己分行；用户的反馈是「不知道每条代表什么、也不知道
    /// 写错了会怎样」。逐条独立成框之后：每条有自己的校验结果与原因、能单独删、
    /// 也能一眼看出「一共允许了哪几个来源」。规则写在上方常驻提示行里（取自
    /// <see cref="InputRules.WhitelistRule"/>，与真正做校验的那份文案同源）。
    /// </summary>
    private void DrawWhitelist(SKCanvas canvas, ConfigLayout layout, PanelTheme theme,
        SKPaint fill, SKPaint stroke, SKPaint textSmall)
    {
        // —— ① 标题行 ——
        // 标题只写「白名单 IP」：完整规则在下一行，不必挤在这里（曾因标题过长紧贴按钮）。
        textSmall.Color = theme.SubText;
        DrawTextInRect(canvas, "白名单 IP",
            layout.WhitelistHeaderLabel.Left, layout.WhitelistHeaderLabel, textSmall,
            layout.WhitelistAddButton.Left - layout.WhitelistHeaderLabel.Left - 6f);

        DrawButton(canvas, layout.WhitelistAddButton, "＋添加", theme, fill, textSmall,
            HoverT(HitId.WhitelistAdd), true);
        DrawButton(canvas, layout.LastSourceButton, "＋最近来源", theme, fill, textSmall,
            HoverT(HitId.LastSource), true);
        DrawButton(canvas, layout.ClearButton, "清空", theme, fill, textSmall, HoverT(HitId.Clear), true);
        // 用真正的三角箭头字形，而不是 ASCII 的 "^" / "v"：
        // ASCII 近似字形宽度窄、基线偏，居中后看着是「漂浮的小符号」；
        // ▲▼ 在 YaHei UI 里是等宽实心三角，视觉重心稳定，也更符合 Windows 原生观感。
        DrawButton(canvas, layout.ScrollUpButton, "▲", theme, fill, textSmall,
            HoverT(HitId.ScrollUp), _scroll > 0);
        DrawButton(canvas, layout.ScrollDownButton, "▼", theme, fill, textSmall,
            HoverT(HitId.ScrollDown), _scroll + layout.VisibleWhitelistEntries < _lines.Count);

        // —— ② 规则提示行（常驻）——
        // 右侧给「有未保存的修改」留位（原来它贴在框内左下角，现在框里已排满输入框）。
        float markerWidth = _dirty ? 104f : 0f;
        textSmall.Color = theme.SubText;
        DrawTextInRect(canvas,
            $"每条一个框：{InputRules.WhitelistRule}；全部留空 = 放行所有来源",
            layout.WhitelistHint.Left, layout.WhitelistHint, textSmall,
            layout.WhitelistHint.Width - markerWidth - 8f);

        if (_dirty)
        {
            textSmall.Color = new SKColor(240, 173, 78);
            DrawTextInRect(canvas, "● 有未保存的修改",
                layout.WhitelistHint.Right - markerWidth, layout.WhitelistHint, textSmall, markerWidth);
        }

        // —— ③ 输入框区 ——
        // WinUI 3 卡片规格：CardBackgroundFillColorDefault + 圆角 8 + 1px 描边
        WinUi.Card(canvas, layout.WhitelistBox, theme, fill, stroke);

        int visible = layout.VisibleWhitelistEntries;
        for (int i = 0; i < visible; i++)
        {
            int index = _scroll + i;
            if (index >= _lines.Count) break;

            var verdict = InputRules.CheckWhitelistEntry(_lines[index]);
            bool focused = _focus == Focus.Whitelist && index == _activeLine;

            DrawEntryField(canvas, layout.WhitelistEntryField(i), _lines[index],
                "输入允许的来源地址，如 192.168.1.23", verdict, focused, theme, fill, stroke, textSmall);

            // 逐行删除按钮
            var remove = layout.LineRemoveButton(i);
            bool hovered = (_pressed.Id == HitId.LineRemove && _pressed.Index == index)
                        || (_pressed.Id == HitId.None && _hover.Id == HitId.LineRemove && _hover.Index == index);
            DrawRemoveGlyph(canvas, remove, theme, fill, textSmall, hovered);
            textSmall.Color = theme.Text;
        }
    }

    /// <summary>
    /// 端口区块：一个带标签与规则说明的输入框，外加四个微调按钮。
    ///
    /// 微调按钮**保留**：宿主设置页（<c>ICustomSettingsPage</c>）拿不到任何键盘事件，
    /// 只有鼠标可用的场景下没有它们就改不了端口。输入框是主入口，按钮是辅助与兜底。
    private void DrawPort(SKCanvas canvas, ConfigLayout layout, PanelTheme theme,
        SKPaint fill, SKPaint stroke, SKPaint text, SKPaint textSmall)
    {
        var verdict = InputRules.CheckPort(_portText);

        // 标签与规则同源：文案取自 InputRules，避免「界面写 1-65535、代码其实另有一套范围」
        textSmall.Color = theme.SubText;
        DrawTextInRect(canvas, $"端口 —— {InputRules.PortPurpose}（{InputRules.PortRule}）",
            layout.PortHeader.Left, layout.PortHeader, textSmall, layout.PortHeader.Width);

        DrawButton(canvas, layout.PortMinus10, "-10", theme, fill, textSmall, HoverT(HitId.PortMinus10), true);
        DrawButton(canvas, layout.PortMinus1, "-1", theme, fill, textSmall, HoverT(HitId.PortMinus1), true);

        // 输入框：边框按校验等级着色，原因贴在框内右侧。
        // ⚠️ 传**未 trim** 的原文：绘制的内容必须与 <see cref="_edit"/> 里的文本逐字一致，
        //    否则光标位置是按 _edit 算的、字是按另一个串画的，两者会错位。
        DrawEntryField(canvas, layout.PortField, _portText, "1-65535，默认 1403", verdict,
            _focus == Focus.Port, theme, fill, stroke, text);

        DrawButton(canvas, layout.PortPlus1, "+1", theme, fill, textSmall, HoverT(HitId.PortPlus1), true);
        DrawButton(canvas, layout.PortPlus10, "+10", theme, fill, textSmall, HoverT(HitId.PortPlus10), true);
    }

    /// <summary>
    /// 「手机端填」地址行：直接给出**手机端 Webhook Server 里要填的完整地址**（`http://IP:端口/`）+ 一键复制。
    ///
    /// ★ 为什么要专门做这一行 ★
    /// 手机端配置里最容易卡住的一步就是「填地址」：用户得先去 ipconfig 找电脑 IP、
    /// 再回配置界面看端口，自己拼成 `http://192.168.x.x:1403/`。
    /// 拼错的表现是「手机点了没反应」—— 与网络不通、防火墙没放行**完全一样**，很难自查。
    /// 这里把地址拼好摆在他眼前，且**跟随端口输入即时变化**，复制即用。
    ///
    /// 拼接规则（含端口非法时的回退）在 <see cref="PhoneAddress"/>，那部分是纯逻辑、有断言盯着。
    /// </summary>
    private void DrawPhoneAddress(SKCanvas canvas, ConfigLayout layout, PanelTheme theme,
        SKPaint fill, SKPaint stroke, SKPaint text, SKPaint textSmall)
    {
        string address = CurrentPhoneAddress();
        bool available = address.Length > 0;

        textSmall.Color = theme.SubText;
        DrawTextInRect(canvas, "手机端填", layout.PhoneLabel.Left, layout.PhoneLabel, textSmall,
            layout.PhoneLabel.Width);

        // 地址本身：可用时用正常文字色（这是「要照着填的东西」，应当醒目）；
        // 检测不到网卡时用次要色给出原因，而不是留一片空白让人猜。
        text.Color = available ? theme.Text : theme.SubText;
        DrawTextInRect(canvas, available ? address : PhoneAddress.NoAddressHint,
            layout.PhoneValue.Left, layout.PhoneValue, text, layout.PhoneValue.Width);

        // 没有地址时按钮置灰：一个点不出效果的按钮比没有按钮更让人困惑
        DrawButton(canvas, layout.PhoneCopyButton, "复制", theme, fill, textSmall,
            available ? HoverT(HitId.PhoneCopy) : 0f, available);
    }

    /// <summary>
    /// 当前应该显示（并被复制）的手机端地址；检测不到可用网卡时返回空串。
    ///
    /// 端口取**编辑框里的当前值**（所以改端口时地址立刻跟着变），
    /// 解析不出来时退回上一份已保存的端口（见 <see cref="PhoneAddress.Build(string?, string?, int)"/>）。
    /// </summary>
    private string CurrentPhoneAddress()
    {
        var ips = CurrentLocalIps();
        return ips.Length == 0 ? "" : PhoneAddress.Build(ips[0], _portText, _savedPort);
    }

    /// <summary>取本机可用 IPv4（带 1 秒节流，见 <see cref="_localIps"/>）；宿主抛异常时当作「没检测到」。</summary>
    private string[] CurrentLocalIps()
    {
        long now = Environment.TickCount64;
        if (now - _localIpsAt < LocalIpsRefreshMs) return _localIps;

        try
        {
            _localIps = _host.DetectLocalIPv4() ?? Array.Empty<string>();
        }
        catch
        {
            _localIps = Array.Empty<string>();   // 枚举失败不该把面板带崩，只是这一行显示「未检测到」
        }
        _localIpsAt = now;
        return _localIps;
    }

    /// <summary>
    /// 丢弃本机 IP 的缓存，下一次绘制立刻重新枚举。
    ///
    /// 用于「需要马上反映网卡变化」的场合（例如窗口刚打开）：平时依赖 <see cref="LocalIpsRefreshMs"/>
    /// 的节流就够了，但**首帧**与**窗口重新显示**这两处值得立刻取一次，
    /// 否则用户刚插上网线打开窗口，会先看到 1 秒的「未检测到」。
    /// 测试也用它绕开节流（否则「改 IP 后重画」要真的等 1 秒）。
    /// </summary>
    internal void InvalidateLocalIps() => _localIpsAt = 0;

    /// <summary>
    /// 画「面板显示时长」块：标签 + 滑块 + 恒定 2 位小数的数值，下面一行「忽略外部鼠标操作」开关。
    ///
    /// ⚠️ 它控制的是**详情页**（右键状态灯展开的配置摘要卡片）的自动收起延迟，
    /// **不是**提醒弹窗停多久 —— 后者由宿主硬编码 4 秒、插件改不了（见 <see cref="CollapseDuration"/>）。
    /// 界面上把这一点写进标签文字里，免得用户以为「拖了没反应」。
    /// </summary>
    private void DrawCollapse(SKCanvas canvas, ConfigLayout layout, PanelTheme theme,
        SKPaint fill, SKPaint stroke, SKPaint text, SKPaint textSmall)
    {
        // ── 时长滑块 ──
        textSmall.Color = theme.SubText;
        DrawTextInRect(canvas, "面板显示时长（鼠标移开多久后收起）", layout.CollapseLabel.Left,
            layout.CollapseLabel, textSmall, layout.CollapseLabel.Width);

        // 悬停或正在拖动都算「激活」态 —— 圆钮与已选段才有反馈
        var state = _sliderDragging ? WinUi.State.Pressed
            : HoverT(HitId.CollapseSlider) >= 0.5f ? WinUi.State.Hover
            : WinUi.State.Normal;
        WinUi.Slider(canvas, layout.CollapseSlider, CollapseDuration.ToRatio(_collapseSeconds),
            theme, fill, stroke, state);

        // 右侧数值：右对齐、恒定 2 位小数（如「0.90 秒」）
        string shown = CollapseDuration.Format(_collapseSeconds);
        text.Color = _sliderDragging ? theme.Accent : theme.Text;   // 拖动时高亮，明确「这个数在变」
        DrawTextInRect(canvas, shown, RightAlignedX(textSmall, shown, layout.CollapseValue),
            layout.CollapseValue, text, layout.CollapseValue.Width);

        // ── 「忽略外部鼠标操作」开关 ──
        // 勾上后面板永不自动收起，滑块那一档就不再生效 —— 文字随之变亮以示「已由开关接管」。
        bool ignore = _collapseIgnoreMouse;
        var tstate = HoverT(HitId.CollapseIgnore) >= 0.5f ? WinUi.State.Hover : WinUi.State.Normal;
        WinUi.Toggle(canvas, layout.CollapseIgnoreToggle, ignore, theme, fill, tstate);

        textSmall.Color = ignore ? theme.Text : theme.SubText;
        var label = new SKRect(layout.CollapseIgnoreToggle.Right + 8f, layout.CollapseIgnoreRow.Top,
            layout.CollapseIgnoreRow.Right, layout.CollapseIgnoreRow.Bottom);
        DrawTextInRect(canvas, ignore ? "已忽略外部鼠标操作：面板不自动收起" : "忽略外部鼠标操作（不自动收起）",
            label.Left, label, textSmall, label.Width);
    }

    /// <summary>
    /// 让文字**右对齐**到矩形右边界时，应当传给 <see cref="DrawTextInRect"/> 的 x。
    /// （那个方法的 x 是左边界，所以要先按实际字体量宽再减回去。）
    /// </summary>
    private static float RightAlignedX(SKPaint paint, string value, SKRect box)
    {
        PluginFonts.Fit(paint, value);
        return box.Right - Math.Min(paint.MeasureText(value), box.Width);
    }

    private void DrawButtons(SKCanvas canvas, ConfigLayout layout, ConfigSnapshot snapshot, PanelTheme theme, SKPaint fill, SKPaint text)
    {
        // 「图标映射」按钮：紧跟在「配置教程」左侧，同样占用按钮行最右的固定宽度区。
        // 两者都由 ConfigLayout 统一排版（IconMapButton / TutorialButton），
        // 保证在任意窗口宽度下都不重叠、也不会挤掉「保存配置 / 开启服务」。
        var iconMap = ConfigLayout.IconMapButtonOf(layout);
        DrawButton(canvas, iconMap, "图标映射", theme, fill, text,
            HoverT(HitId.IconMapOpen), true, accentColor: new SKColor(90, 150, 170));

        // 「配置教程」按钮与「保存配置 / 开启服务」同一行、最右侧。
        // 位置由 ConfigLayout 给出（TutorialButton），保证三者在任意窗口宽度下都不重叠。
        // 点击后不再是「面板内分屏」，而是「导出 Markdown + 编译 HTML + 用浏览器打开」，
        // 因此按钮文字恒定，不再有「关闭教程」这个状态。
        var tut = ConfigLayout.TutorialButtonOf(layout);
        DrawButton(canvas, tut, "配置教程", theme, fill, text, HoverT(HitId.TutorialOpen), true,
            accentColor: new SKColor(110, 110, 190));

        DrawButton(canvas, layout.SaveButton, _dirty ? "保存配置 *" : "保存配置", theme, fill, text,
            HoverT(HitId.Save), true, accent: true);

        bool running = snapshot.ServiceRunning;
        DrawButton(canvas, layout.ServiceButton, running ? "停止服务" : "开启服务", theme, fill, text,
            HoverT(HitId.Service), true,
            accentColor: running ? new SKColor(224, 120, 82) : new SKColor(76, 175, 80));
    }

    /// <summary>
    /// 更新区块：五行。
    ///   ① 状态行 —— 检查结果 + 「检查 / 复制信息 / 打开下载页」三个按钮；
    ///   ② 提示模式行 —— 灵动岛更新提示条「不提示 / 只显示 / 可点击」（三选一，选中的用主色实心）；
    ///   ③ 点击动作行 —— 点提示条后「打开下载页 / 复制到剪贴板 / 下载更新包」（三选一）；
    ///   ④ 静默升级行 —— 「一键静默升级」开关（开启 / 关闭）+「立即升级」；
    ///   ⑤ 升级进度行 —— 当前处在哪一步（下载百分比 / 校验 / 安装 / 待重载 / 失败原因与重试）。
    ///
    /// 前面两组按钮直接给到操作入口，所以**不必依赖灵动岛提示也能完成更新**；
    /// 提示模式与点击动作只决定「灵动岛上会不会出现提示条、点它做什么」。
    /// ④⑤ 只决定「要不要在后台自动下载并替换插件文件」，关掉时行为与升级前完全一致。
    /// 全部设置都持久化在注册表里，插件自动重载后不丢。
    /// </summary>
    private void DrawUpdate(SKCanvas canvas, ConfigLayout layout, PanelTheme theme,
        SKPaint fill, SKPaint stroke, SKPaint text, SKPaint textSmall)
    {
        var update = _host.UpdateInfo();

        textSmall.Color = update.Status switch
        {
            UpdateStatus.UpdateAvailable => new SKColor(240, 173, 78),   // 橙：有新版本
            UpdateStatus.UpToDate => new SKColor(76, 175, 80),           // 绿：已是最新
            UpdateStatus.Failed => new SKColor(224, 120, 82),            // 红：检查失败
            _ => theme.SubText,
        };

        string summary = update.Status switch
        {
            // ★ 措辞尽量短 ★ 这一行左侧文字只分到约 134px（右侧是三个固定宽度的按钮）。
            // 原来的「更新  新版本 v1.2.0（当前 v1.1.1）」要 155px，会被截成省略号 ——
            // 而「当前版本」正是用户最想看的信息。改用箭头表示「从 → 到」，语义更清楚也更省宽度。
            UpdateStatus.UpdateAvailable => $"v{update.LocalVersion} → v{update.RemoteVersion} 有新版本",
            UpdateStatus.UpToDate => $"已是最新 v{update.LocalVersion}",
            UpdateStatus.Failed => $"检查失败 · {update.Message}",
            _ => $"当前 v{update.LocalVersion} · 尚未检查",
        };

        DrawTextInRect(canvas, summary, layout.UpdateHeader.Left, layout.UpdateHeader, textSmall,
            layout.UpdateCheckButton.Left - layout.UpdateHeader.Left - 6f);

        DrawButton(canvas, layout.UpdateCheckButton, "检查", theme, fill, textSmall,
            HoverT(HitId.UpdateCheck), true);
        DrawButton(canvas, layout.UpdateCopyButton, "复制信息", theme, fill, textSmall,
            HoverT(HitId.UpdateCopy), true);
        DrawButton(canvas, layout.UpdateOpenButton, "打开下载页", theme, fill, textSmall,
            HoverT(HitId.UpdateOpen), true);

        // —— 提示模式行 ——
        // ★ 前缀只留「提示条：」★ 这一行左侧文字只分到约 178px（右侧是三个模式按钮）。
        // 原来的前缀「灵动岛提示：」占 ~61px，挤得说明必然被截断。前缀只承担「这行在讲什么」
        // 的作用，用「提示条：」同样清楚；真正区分三个模式的文字（显示 / 点击行为）完整保留。
        var mode = _host.CurrentBannerMode;
        textSmall.Color = theme.SubText;
        DrawTextInRect(canvas, $"提示条：{UpdateNotice.ModeDescribe(mode)}", layout.UpdateModeRow.Left,
            layout.UpdateModeRow, textSmall,
            layout.UpdateModeOffButton.Left - layout.UpdateModeRow.Left - 6f);

        DrawButton(canvas, layout.UpdateModeOffButton, UpdateNotice.ModeLabel(UpdateBannerMode.Off),
            theme, fill, textSmall, HoverT(HitId.UpdateModeOff), true,
            accent: mode == UpdateBannerMode.Off);
        DrawButton(canvas, layout.UpdateModeAutoButton, UpdateNotice.ModeLabel(UpdateBannerMode.AutoDismiss),
            theme, fill, textSmall, HoverT(HitId.UpdateModeAuto), true,
            accent: mode == UpdateBannerMode.AutoDismiss);
        DrawButton(canvas, layout.UpdateModeClickButton, UpdateNotice.ModeLabel(UpdateBannerMode.Clickable),
            theme, fill, textSmall, HoverT(HitId.UpdateModeClick), true,
            accent: mode == UpdateBannerMode.Clickable);

        // —— 点击动作行 ——
        var action = _host.CurrentClickAction;
        textSmall.Color = theme.SubText;
        DrawTextInRect(canvas, "点击提示条后：", layout.UpdateActionRow.Left,
            layout.UpdateActionRow, textSmall,
            layout.UpdateActionMarketButton.Left - layout.UpdateActionRow.Left - 6f);

        DrawButton(canvas, layout.UpdateActionMarketButton, UpdateNotice.ClickActionLabel(UpdateClickAction.Market),
            theme, fill, textSmall, HoverT(HitId.UpdateActionMarket), true,
            accent: action == UpdateClickAction.Market);
        DrawButton(canvas, layout.UpdateActionClipboardButton, UpdateNotice.ClickActionLabel(UpdateClickAction.Clipboard),
            theme, fill, textSmall, HoverT(HitId.UpdateActionClipboard), true,
            accent: action == UpdateClickAction.Clipboard);
        DrawButton(canvas, layout.UpdateActionDownloadButton, UpdateNotice.ClickActionLabel(UpdateClickAction.Download),
            theme, fill, textSmall, HoverT(HitId.UpdateActionDownload), true,
            accent: action == UpdateClickAction.Download);

        // —— 静默升级开关行 ——
        // 「一键静默升级」是会**自动写本机文件**的行为，所以这里除了开关本身，
        // 还要在左侧把当前状态（在哪一步 / 失败几次 / 为什么没自动装）完整说清楚。
        bool silent = _host.SilentUpgradeEnabled;
        var stage = _host.CurrentUpgradeStage;
        bool busy = SilentUpgrade.IsBusy(stage);

        textSmall.Color = theme.SubText;
        DrawTextInRect(canvas, "一键静默升级：" + SilentUpgrade.SwitchDescribe(silent), layout.UpgradeToggleRow.Left,
            layout.UpgradeToggleRow, textSmall,
            layout.UpgradeToggleOnButton.Left - layout.UpgradeToggleRow.Left - 6f);

        DrawButton(canvas, layout.UpgradeToggleOnButton, "开启", theme, fill, textSmall,
            HoverT(HitId.UpgradeToggleOn), true, accent: silent);
        DrawButton(canvas, layout.UpgradeToggleOffButton, "关闭", theme, fill, textSmall,
            HoverT(HitId.UpgradeToggleOff), true, accent: !silent);
        // 「立即升级」在忙碌 / 未开启时置灰，但不清空按钮 —— 让用户看得见入口在哪
        DrawButton(canvas, layout.UpgradeNowButton, busy ? "进行中" : "立即升级", theme, fill, textSmall,
            HoverT(HitId.UpgradeNow), !busy && silent);

        // —— 升级进度 / 状态行：颜色随阶段变化，与上面的状态灯同一套语义（绿=好、橙=进行中、红=失败）——
        textSmall.Color = stage switch
        {
            UpgradeStage.Downloading or UpgradeStage.Verifying or UpgradeStage.Installing => new SKColor(240, 173, 78),
            UpgradeStage.PendingReload or UpgradeStage.Succeeded => new SKColor(76, 175, 80),
            UpgradeStage.Failed => new SKColor(224, 120, 82),
            _ => theme.SubText,
        };
        DrawTextInRect(canvas, _host.UpgradeStatusText(), layout.UpgradeStatusRow.Left,
            layout.UpgradeStatusRow, textSmall, layout.UpgradeStatusRow.Width);
    }

    // ====================================================================
    // 图标映射页
    // ====================================================================

    /// <summary>
    /// 图标映射页：整页替换主内容区（尺寸与主页面完全一致，见 <see cref="PanelPage"/>）。
    ///
    /// 从上到下五行：
    ///   ① 标题行 —— 「图标映射」+ 返回 / 保存映射；
    ///   ② 说明行 —— 这个页干什么、格式是什么；
    ///   ③ 编辑框 —— 一行一条「应用名=icon值」，带逐行删除与滚动；
    ///   ④ 工具行 —— ＋添加 / 清空 / 上翻 / 下翻；
    ///   ⑤ 内置映射一览 —— 只读展示内置表，让用户知道「哪些已经不用自己配」。
    ///
    /// ★ 为什么编辑框要自己做滚动 ★
    /// 与白名单框同样的理由：整页高度固定，映射条目可能几十条，
    /// 而这里刻意不去动 <see cref="ConfigLayout"/> 的行推进量（见 PanelPage 说明），
    /// 因此复用白名单那套「可视行 + 滚动偏移」的做法。
    /// </summary>
    private void DrawIconMapPage(SKCanvas canvas, ConfigLayout layout, PanelTheme theme,
        SKPaint fill, SKPaint stroke, SKPaint text, SKPaint textSmall, SKPaint textTitle)
    {
        var l = ConfigLayout.ComputeIconMap(layout.ContentWidth);
        float innerW = layout.ContentWidth - ConfigLayout.PadX * 2f;
        float x = ConfigLayout.PadX;

        // —— ① 标题行 ——
        textTitle.Color = theme.Text;
        DrawTextInRect(canvas, "图标映射", x, l.Header, textTitle, 90f);
        DrawButton(canvas, l.BackButton, "返回", theme, fill, textSmall, HoverT(HitId.IconMapBack), true);
        DrawButton(canvas, l.ApplyButton, _iconDirty ? "保存映射 *" : "保存映射", theme, fill, textSmall,
            HoverT(HitId.IconMapApply), true, accent: true);

        // 状态提示：贴在标题右侧空档里。「已复制」/「已粘贴 N 条」/「N 条冲突待确认」都在这里，
        // 而不是用弹窗 —— 图标页面宽本来就不大，弹层会挡住正在编辑的内容。
        if (_iconFlash.Length > 0)
        {
            var flashRect = new SKRect(l.Header.Left + 92f, l.Header.Top, l.BackButton.Left - 8f, l.Header.Bottom);
            textSmall.Color = _iconPendingPaste != null
                ? new SKColor(214, 148, 58)      // 待确认 → 橙色（与提示条同色系，表示「需要你操作」）
                : theme.SubText;
            DrawTextInRect(canvas, _iconFlash, flashRect.Left, flashRect, textSmall, flashRect.Width);
        }

        // —— ② 说明行 ——
        // Hint1 = **用途**（静态）：两个框分别填什么。
        // Hint2 = **校验规则 / 当前条目的校验结果**（动态）：有话说时优先显示它，
        //         一切正常才退回静态的「图标值四种写法」。
        // 两行都用的既有几何，不额外占纵向空间。
        textSmall.Color = theme.SubText;
        DrawTextInRect(canvas,
            "左框=应用名（消息里的应用名或包名）；右框=图标值。按消息的 kind 自动补 icon，发送端已带 icon 时不覆盖。",
            x, l.Hint1, textSmall, innerW);

        var activeVerdict = ActiveIconVerdict();
        textSmall.Color = activeVerdict.HasMessage ? TextFor(activeVerdict.Level, theme) : theme.SubText;
        DrawTextInRect(canvas,
            activeVerdict.HasMessage ? activeVerdict.Message : InputRules.IconValueRule,
            x, l.Hint2, textSmall, innerW);

        // —— ③ 编辑框 ——
        // WinUI 3 卡片规格（与白名单框、日志框同源，圆角/描边只有一份）
        WinUi.Card(canvas, l.Box, theme, fill, stroke);

        int visible = l.VisibleLines;
        if (_iconLines.Count == 0)
        {
            // 空态：给一个可直接照抄的例子，而不是只显示「（空）」
            textSmall.Color = theme.SubText.WithAlpha(190);
            DrawTextInRect(canvas, "（还没有自定义映射）点「＋添加一行」后：左框写「钉钉」，右框写图片链接或本地路径",
                l.Box.Left + 10f, l.Box, textSmall, l.Box.Width - 20f);
        }
        else
        {
            _iconScroll = Math.Clamp(_iconScroll, 0, Math.Max(0, _iconLines.Count - visible));

            // 只画可视范围内的行 —— 不做整表绘制，避免几十条映射时的每帧浪费
            for (int i = 0; i < visible; i++)
            {
                int index = _iconScroll + i;
                if (index >= _iconLines.Count) break;

                var (key, value) = InputRules.SplitIconRow(_iconLines[index]);
                bool active = index == _iconActiveLine && IsIconMapFocused;

                var rowRect = l.RowAt(i);
                if (active)
                {
                    fill.Color = theme.Text.WithAlpha(16);
                    canvas.DrawRoundRect(new SKRect(l.Box.Left + 3f, rowRect.Top - 1f, l.Box.Right - 3f, rowRect.Bottom + 1f), 4f, 4f, fill);
                }

                // ⚠️ 两个框都**不在框内画校验原因**：这里的框比白名单窄得多
                //   （应用名框约 200px），塞进去会把用户填的内容挤没。
                //   完整原因统一走 Hint2 —— 点一下那一行就能看到。
                DrawEntryField(canvas, l.KeyFieldAt(i), key, "应用名",
                    InputRules.CheckIconKey(key), active && _focus == Focus.IconMapKey,
                    theme, fill, stroke, textSmall, showReason: false);
                DrawEntryField(canvas, l.ValueFieldAt(i), value, "图标值（别名 / 链接 / 路径）",
                    InputRules.CheckIconValue(value), active && _focus == Focus.IconMapValue,
                    theme, fill, stroke, textSmall, showReason: false);

                DrawRemoveGlyph(canvas, l.RemoveButtonAt(i), theme, fill, textSmall,
                    _hover.Id == HitId.IconMapLineRemove && _hover.Index == index);
            }
        }

        // —— ④ 工具行 ——
        // 「复制 / 粘贴」的可用性（enabled）遵循「点了真会有事发生」：
        //   · 复制：活动行必须是一条**有效的映射行**（空行/半成品复制出去没有意义）；
        //   · 粘贴：待确认时恒可用（那是「继续」的入口）；否则要求选中了某一行 ——
        //     因为粘贴目标是「活动行下方」，没有活动行就无处安放。
        DrawButton(canvas, l.AddButton, "＋添加一行", theme, fill, textSmall, HoverT(HitId.IconMapAdd), true);

        bool canCopy = IsIconMapFocused
                       && _iconActiveLine >= 0 && _iconActiveLine < _iconLines.Count
                       && IconClipboard.TryParseLine(_iconLines[_iconActiveLine].Trim(), out _);
        string copyLabel = _hover.Id == HitId.IconMapCopy && canCopy ? "复制 ✓" : "复制";
        DrawButton(canvas, l.CopyButton, copyLabel, theme, fill, textSmall,
            HoverT(HitId.IconMapCopy), canCopy);

        bool pending = _iconPendingPaste != null;
        bool canPaste = pending || (IsIconMapFocused && _iconActiveLine >= 0);
        // 待确认时按钮文案变成「确认粘贴」，让用户一眼看出「再点一次就是继续」
        string pasteLabel = pending ? "确认粘贴"
                          : _hover.Id == HitId.IconMapPaste && canPaste ? "粘贴 ✓" : "粘贴";
        DrawButton(canvas, l.PasteButton, pasteLabel, theme, fill, textSmall,
            HoverT(HitId.IconMapPaste), canPaste, accent: pending);

        DrawButton(canvas, l.ResetButton, "清空自定义", theme, fill, textSmall, HoverT(HitId.IconMapReset),
            _iconLines.Count > 0);
        DrawButton(canvas, l.ScrollUpButton, "▲", theme, fill, textSmall, HoverT(HitId.IconMapScrollUp),
            _iconScroll > 0);
        DrawButton(canvas, l.ScrollDownButton, "▼", theme, fill, textSmall, HoverT(HitId.IconMapScrollDown),
            _iconScroll < Math.Max(0, _iconLines.Count - visible));

        // —— ⑤ 内置别名一览（只读） ——
        // 明确区分「内置」与「自定义」，用户才知道哪些不需要自己配。
        // 只列 Showcase 里挑出来的代表性条目，不把同义词全列（会刷屏）。
        //
        // ⚠️ 只列**别名**类（宿主读本地 data/image 的图，零网络、没有超时）；
        //    靠网络图片链接的那几个（微信 / 快手 / 抖音 / 网易云）**不在这里展示** ——
        //    它们的映射照常生效，但混进这张表会让人误以为「都已经配稳了」，
        //    而实际上那几张图要联网、宿主只给 3 秒超时，偶尔失败是常态。
        //    因此标题里的条数也用别名数，与列表内容严格对应。
        textSmall.Color = theme.SubText;
        DrawTextInRect(canvas, $"内置别名（宿主自带图，无需自己配，共 {IconCatalog.BuiltinAliasCount} 条键）",
            x, l.BuiltinHeader, textSmall, innerW);

        var showcase = IconCatalog.Showcase();
        // 每行放 2 条，3 行足够展示 6 条；超出部分以「…」提示
        const int perRow = 2;
        const int maxRows = 3;
        int shown = 0;
        for (int row = 0; row < maxRows && shown < showcase.Count; row++)
        {
            var rowRect = l.BuiltinRowAt(row);
            float colWidth = (innerW - 8f) / perRow;

            for (int col = 0; col < perRow && shown < showcase.Count; col++, shown++)
            {
                var (key, icon) = showcase[shown];
                textSmall.Color = theme.SubText;
                DrawTextInRect(canvas, $"{key} → 宿主自带 {icon}",
                    x + col * (colWidth + 8f), rowRect, textSmall, colWidth);
            }
        }

        int remaining = showcase.Count - shown;
        if (remaining > 0)
        {
            textSmall.Color = theme.SubText.WithAlpha(170);
            DrawTextInRect(canvas, $"…另有 {remaining} 条内置别名（含包名同义词）", x, l.BuiltinMore, textSmall, innerW);
        }
    }

    /// <summary>当前活动黑名单条目的校验结论（用于把完整原因显示在说明行第二行）。</summary>
    private InputRules.Verdict ActiveBlacklistVerdict()
    {
        if (_blacklistActiveLine < 0 || _blacklistActiveLine >= _blacklistLines.Count)
            return InputRules.Verdict.None;
        return InputRules.CheckBlacklistEntry(_blacklistLines[_blacklistActiveLine]);
    }

    /// <summary>
    /// 应用黑名单页：维护「不推送上岛的应用」名单。
    ///
    /// ★ 为什么是独立子页 ★ 与图标映射页同样的理由（见 <see cref="PanelPage"/> 的说明）：
    /// 主页面纵向每一行的推进量都写进了两个必须手动同步的 const，而黑名单本质是
    /// 「低频的编辑工作」—— 整页替换既给了它足够的编辑空间，也完全不触碰既有布局算术。
    ///
    /// 每条一个输入框（与白名单同款）：每条有自己的校验结果与原因、能单独删，
    /// 也能一眼看出「一共拦了哪几个应用」。完整原因走第二行说明（点中某一行即可看到），
    /// 不在框内画 —— 长原因会把用户填的内容挤没。
    /// </summary>
    private void DrawBlacklistPage(SKCanvas canvas, ConfigLayout layout, PanelTheme theme,
        SKPaint fill, SKPaint stroke, SKPaint text, SKPaint textSmall, SKPaint textTitle)
    {
        var l = ConfigLayout.ComputeBlacklist(layout.ContentWidth);
        float innerW = layout.ContentWidth - ConfigLayout.PadX * 2f;
        float x = ConfigLayout.PadX;

        // —— ① 标题行 ——
        textTitle.Color = theme.Text;
        DrawTextInRect(canvas, "应用黑名单", x, l.Header, textTitle, 90f);
        DrawButton(canvas, l.BackButton, "返回", theme, fill, textSmall, HoverT(HitId.BlacklistBack), true);
        DrawButton(canvas, l.ApplyButton, _blacklistDirty ? "保存名单 *" : "保存名单", theme, fill, textSmall,
            HoverT(HitId.BlacklistApply), true, accent: true);

        // —— ② 说明行 ——
        // Hint1 = 写法（静态）；Hint2 = 当前条目的校验结果（有话说时优先显示），
        // 一切正常才退回静态的「命中后会怎样」—— 后者是用户按下保存前最该知道的一件事。
        textSmall.Color = theme.SubText;
        DrawTextInRect(canvas, InputRules.BlacklistRule, x, l.Hint1, textSmall, innerW);

        var activeVerdict = ActiveBlacklistVerdict();
        textSmall.Color = activeVerdict.HasMessage ? TextFor(activeVerdict.Level, theme) : theme.SubText;
        DrawTextInRect(canvas,
            activeVerdict.HasMessage
                ? activeVerdict.Message
                : "命中的消息不上岛；对发送端仍算成功（不会反复重试，也不影响状态灯）。",
            x, l.Hint2, textSmall, innerW);

        // —— ③ 编辑框 ——
        WinUi.Card(canvas, l.Box, theme, fill, stroke);

        int visible = l.VisibleLines;
        if (_blacklistLines.Count == 0)
        {
            // 空态：说明「空 = 不拦截」，否则用户会以为这一页坏了
            textSmall.Color = theme.SubText.WithAlpha(190);
            DrawTextInRect(canvas, "（名单为空 = 不拦截任何应用）点「＋添加一行」写下要屏蔽的应用名或包名",
                l.Box.Left + 10f, l.Box, textSmall, l.Box.Width - 20f);
        }
        else
        {
            _blacklistScroll = Math.Clamp(_blacklistScroll, 0, Math.Max(0, _blacklistLines.Count - visible));

            // 只画可视范围 —— 名单可能几十条，不做整表绘制
            for (int i = 0; i < visible; i++)
            {
                int index = _blacklistScroll + i;
                if (index >= _blacklistLines.Count) break;

                var value = _blacklistLines[index];
                bool active = index == _blacklistActiveLine && _focus == Focus.Blacklist;

                var field = l.FieldAt(i);
                if (active)
                {
                    fill.Color = theme.Text.WithAlpha(16);
                    canvas.DrawRoundRect(
                        new SKRect(l.Box.Left + 3f, field.Top - 1f, l.Box.Right - 3f, field.Bottom + 1f),
                        4f, 4f, fill);
                }

                DrawEntryField(canvas, field, value, "应用名 或 包名，如 微信 / com.tencent.mm",
                    InputRules.CheckBlacklistEntry(value), active,
                    theme, fill, stroke, textSmall, showReason: false);

                DrawRemoveGlyph(canvas, l.RemoveButtonAt(i), theme, fill, textSmall,
                    _hover.Id == HitId.BlacklistLineRemove && _hover.Index == index);
            }
        }

        // —— ④ 工具行 ——
        DrawButton(canvas, l.AddButton, "＋添加一行", theme, fill, textSmall, HoverT(HitId.BlacklistAdd), true);
        DrawButton(canvas, l.ScrollUpButton, "▲", theme, fill, textSmall, HoverT(HitId.BlacklistScrollUp),
            _blacklistScroll > 0);
        DrawButton(canvas, l.ScrollDownButton, "▼", theme, fill, textSmall, HoverT(HitId.BlacklistScrollDown),
            _blacklistScroll < Math.Max(0, _blacklistLines.Count - visible));

        // —— ⑤ 底部统计 ——
        // 用**真正的解析器**跑一遍缓冲，让用户在保存前就看到「到底会拦几条」：
        // 校验层能过、但解析层认不出的行（例如整行只有空白）不会被计入，这种差异必须显式暴露，
        // 否则用户会以为「写了三条就拦三条」。
        var pending = AppBlacklist.Parse(string.Join("\n", _blacklistLines));
        textSmall.Color = theme.SubText;
        DrawTextInRect(canvas,
            pending.IsEmpty
                ? "当前名单为空：所有应用的消息都会照常上岛"
                : pending.WildcardCount > 0
                    ? $"保存后共 {pending.RuleCount} 条规则生效，其中 {pending.WildcardCount} 条是通配（同前缀的应用都会被拦下）"
                    : $"保存后共 {pending.RuleCount} 条规则生效",
            x, l.Footer, textSmall, innerW);
    }

    private void DrawLog(SKCanvas canvas, ConfigLayout layout, ConfigSnapshot snapshot, PanelTheme theme,
        SKPaint fill, SKPaint stroke, SKPaint textSmall)
    {
        // —— 标题行：左侧标题与计数，右侧「复制日志 / 上翻 / 下翻」 ——
        textSmall.Color = theme.SubText;
        DrawTextInRect(canvas, "日志", layout.LogHeader.Left, layout.LogHeader, textSmall, 30f);
        DrawTextInRect(canvas,
            $"接收 {snapshot.Received} · 转发 {snapshot.Forwarded} · 拒绝 {snapshot.Rejected} · 失败 {snapshot.Failed}"
            + (_logScroll > 0 ? $" · 已上翻 {_logScroll} 行" : " · 显示最新"),
            layout.LogHeader.Left + 34f, layout.LogHeader, textSmall,
            Math.Max(0f, layout.LogCopyButton.Left - layout.LogHeader.Left - 40f));

        DrawButton(canvas, layout.LogCopyButton, "复制日志", theme, fill, textSmall, HoverT(HitId.LogCopy), true);
        DrawButton(canvas, layout.LogScrollUpButton, "▲", theme, fill, textSmall,
            HoverT(HitId.LogScrollUp), _logScroll < MaxLogScroll(snapshot));
        DrawButton(canvas, layout.LogScrollDownButton, "▼", theme, fill, textSmall,
            HoverT(HitId.LogScrollDown), _logScroll > 0);

        // —— 日志框：显示全部日志内容（超长行自动折行），可上下翻阅 ——
        // WinUI 3 卡片规格（与白名单框、映射编辑框统一）
        WinUi.Card(canvas, layout.LogBox, theme, fill, stroke);

        int maxRows = layout.VisibleLogLines;
        if (maxRows <= 0) { _lastWrappedRowCount = 0; return; }

        // 从最新一行往回收集可视行（含折行），只处理看得见的部分：日志再多也不会拖慢绘制。
        // ★ 多收集 maxRows 行作为「提前量」★：上翻按钮是否可用要一并知道当前视口之上还有没有内容，
        //   所以这里在「一屏 + 已上翻」之外再往前多读一屏 —— 仍然是常数级，与日志总长无关。
        var lines = snapshot.LogLines;
        var rows = new List<(string Text, bool Newest)>();
        int needed = maxRows + _logScroll;
        int probe = needed + maxRows;   // 多读一屏，用来判断「还能不能继续上翻」
        for (int i = lines.Length - 1; i >= 0 && rows.Count < probe; i--)
        {
            var wrapped = WrapText(textSmall, lines[i], layout.LogTextWidth);
            for (int k = wrapped.Count - 1; k >= 0; k--)
                rows.Add((wrapped[k], i == lines.Length - 1 && k == wrapped.Count - 1));
        }

        // 本次实际收集到的总行数（可能被 probe 截断）：供上翻按钮判断「还能不能再翻」。
        // 只要收集数 > 一屏 + 已上翻，就说明上面还有内容。
        _lastWrappedRowCount = rows.Count;
        _logScroll = Math.Clamp(_logScroll, 0, Math.Max(0, rows.Count - maxRows));

        if (rows.Count == 0)
        {
            textSmall.Color = theme.SubText;
            DrawText(canvas, "（暂无日志）", layout.LogBox.Left + 8f, layout.LogLineBaseline(0), textSmall, layout.LogTextWidth);
            return;
        }

        for (int i = 0; i < maxRows; i++)
        {
            int rowIndex = _logScroll + i;
            if (rowIndex >= rows.Count) break;
            var (text, newest) = rows[rowIndex];
            textSmall.Color = newest ? theme.Text : theme.SubText;
            DrawText(canvas, text, layout.LogBox.Left + 8f, layout.LogLineBaseline(i), textSmall, layout.LogTextWidth);
        }
    }

    /// <summary>
    /// 已上翻的上限（折行后的可视行总数 − 一屏）。
    ///
    /// ⚠️ **必须与 <see cref="DrawLog"/> 里 clamp <c>_logScroll</c> 的口径一致**：
    /// <c>_logScroll</c> 的单位是「**折行后**的行」（见 DrawLog 里 rows 的构造），
    /// 不是「日志原始行」。若按 <c>LogLines.Length</c>（原始行数）来算，
    /// 一条超长日志折成十几行却只按 1 行计数 → 上限被算小 → **上翻按钮提前置灰、
    /// 更早的日志翻不到**。所以这里直接读 <see cref="DrawLog"/> 上一帧实测出的行数。
    /// </summary>
    private int MaxLogScroll(ConfigSnapshot snapshot)
        => Math.Max(0, _lastWrappedRowCount - _layout.VisibleLogLines);

    /// <summary>
    /// 按可用宽度折行。实现见 <see cref="TextLayout.Wrap"/>（抽成纯函数是为了能在验证工程里逐条测）。
    /// </summary>
    private static List<string> WrapText(SKPaint paint, string text, float maxWidth)
        => TextLayout.Wrap(paint, text, maxWidth);

    /// <summary>复制全部日志到剪贴板（含当前未显示在屏幕上的行，且不做截断）。</summary>
    private void CopyLogClicked()
    {
        var snapshot = _host.Snapshot();
        if (snapshot.LogLines.Length == 0)
        {
            _host.Note("暂无日志可复制");
            return;
        }

        var text = string.Join(Environment.NewLine, snapshot.LogLines);
        if (_host.CopyText(text))
            _host.Note($"已复制 {snapshot.LogLines.Length} 行日志到剪贴板");
        else
            _host.Note("复制失败：剪贴板被其它程序占用，请稍后再试");
    }

    // ====================================================================
    // 绘制工具
    // ====================================================================

    /// <param name="hoverT">
    /// 悬停强度 0~1（由调用方用 <see cref="HoverT"/> 算）。传 <c>1</c> 表示「已完全悬停」、
    /// 不做过渡；传 <see cref="HoverT"/> 的实时值则颜色会**连续**渐变（WinUI 3 的悬停动画）。
    /// </param>
    private static void DrawButton(SKCanvas canvas, SKRect rect, string label, PanelTheme theme,
        SKPaint fill, SKPaint text, float hoverT, bool enabled,
        bool accent = false, SKColor? accentColor = null)
    {
        // ★ 统一走 WinUI 3 按钮规格 ★（圆角 4 / ControlFill 底 + 1px 描边 / 悬停连续提亮 / 强调按钮 AccentFill）
        WinUi.ButtonLerp(canvas, rect, label, theme, fill, text, hoverT, enabled,
            accent ? WinUi.Style.Accent : WinUi.Style.Standard, accentColor);
    }

    /// <summary>
    /// 让一行文字在矩形里**真正纵向居中**的基线 y。
    /// 实现与完整的原因说明见 <see cref="TextLayout.VerticallyCenteredBaseline"/> ——
    /// 抽到那里是为了能在验证工程里用真实字号逐条测（历史原因：早先面板被认为进不了测试）。
    /// </summary>
    private static float VerticallyCenteredBaseline(SKPaint paint, SKRect rect, string text = "国")
        => TextLayout.VerticallyCenteredBaseline(paint, rect, text);

    /// <summary>单行绘制，超出宽度自动以 … 截断。基线由调用方给出（见 <see cref="VerticallyCenteredBaseline"/>）。</summary>
    private static void DrawText(SKCanvas canvas, string value, float x, float y, SKPaint paint, float maxWidth)
    {
        if (maxWidth <= 6f) return;
        // ★ 先按**整串**选定字体，再截断 ★ 反过来的话截断过程用的是上一串残留的字体，
        // 量出来的宽度与实际绘制的不一致，省略号位置会错。
        PluginFonts.Fit(paint, value);
        var shown = Truncate(paint, value, maxWidth);
        if (shown.Length == 0) return;
        canvas.DrawText(shown, x, y, paint);
    }

    /// <summary>在矩形内纵向居中地画一行文字（自动算基线，与字号无关）。</summary>
    private static void DrawTextInRect(SKCanvas canvas, string value, float x, SKRect rect, SKPaint paint, float maxWidth)
    {
        // 用**实际会被画出来的那串**（截断后的）来定基线：截断会改变包围盒，
        // 拿原文算会让换行/省略号后的行略微偏一点。
        PluginFonts.Fit(paint, value);   // 同样必须先定字体再截断 / 算基线
        var shown = maxWidth > 6f ? Truncate(paint, value, maxWidth) : string.Empty;
        if (shown.Length == 0) return;
        canvas.DrawText(shown, x, VerticallyCenteredBaseline(paint, rect, shown), paint);
    }

    /// <summary>
    /// 画「删除」那个 × 号按钮（白名单条目与图标映射条目共用）。
    ///
    /// 单独包一层是因为它绕过了 <see cref="DrawText"/> —— 需要水平居中而不是左对齐。
    /// 同样必须先 <see cref="PluginFonts.Fit"/> 再量宽，否则居中会偏；
    /// 而「× 属拉丁 1 区、覆盖最稳」的选字理由见 <see cref="RemoveGlyph"/>。
    /// </summary>
    private static void DrawRemoveGlyph(SKCanvas canvas, SKRect rect, PanelTheme theme,
        SKPaint fill, SKPaint text, bool hovered)
    {
        // WinUI 3 观感：常态是普通控件（ControlFill + 描边），**悬停才泛红**（危险色）。
        // 比原来「一直红着」克制得多 —— 一屏十几个红块会把内容本身压下去。
        var previous = text.Color;
        WinUi.Button(canvas, rect, RemoveGlyph, theme, fill, text,
            hovered ? WinUi.State.Hover : WinUi.State.Normal, WinUi.Style.Danger);
        text.Color = previous;   // 画笔逐帧共用，改完颜色要还回去
    }

    /// <summary>
    /// 截断到能放下的长度并加省略号。实现见 <see cref="TextLayout.Truncate"/>
    /// （抽成纯函数是为了能在验证工程里逐条测）。
    /// </summary>
    private static string Truncate(SKPaint paint, string value, float maxWidth)
        => TextLayout.Truncate(paint, value, maxWidth);

    // ====================================================================
    // 交互
    // ====================================================================

    /// <summary>
    /// 某个交互元素**此刻的悬停强度**（0 = 常态，1 = 完全悬停）。
    ///
    /// ★ 为什么不是简单的 true/false ★ WinUI 3 的控件在悬停 / 移开时有约 150ms 的
    /// 颜色过渡（Fluent 的「隐式动画」）。把悬停表示成 0~1 的进度、绘制时按它插值配色，
    /// 就有了同样的手感 —— 鼠标扫过一排按钮时不会「啪」地整片跳色。
    /// 目标刚改变后的这一小段时间里，旧目标走「渐出」、新目标走「渐入」。
    /// </summary>
    private float HoverT(HitId id, int index = -1)
    {
        bool isCurrent = _hover.Id == id && _hover.Index == index;
        bool isPrev = _hoverPrev.Id == id && _hoverPrev.Index == index;
        if (!isCurrent && !isPrev) return 0f;
        if (HoverAnimMs <= 0f) return isCurrent ? 1f : 0f;

        float t = Math.Clamp((Environment.TickCount64 - _hoverChangedAt) / HoverAnimMs, 0f, 1f);
        return isCurrent ? t : 1f - t;
    }

    /// <summary>
    /// 切换悬停目标：记下旧目标与时刻，供 <see cref="HoverT"/> 做渐入 / 渐出，并保证动画期间持续重绘。
    /// </summary>
    private void SetHover((HitId Id, int Index) hit)
    {
        if (hit.Id == _hover.Id && hit.Index == _hover.Index) return;
        _hoverPrev = _hover;
        _hover = hit;
        _hoverChangedAt = Environment.TickCount64;
    }

    private void OnMouseMoveCore(float x, float y)
    {
        // ★ 拖动滑块 ★ 按下后持续跟随（「点哪跳哪」已由 OnMouseDown 完成）。
        //   它与输入框拖选是**两套独立的拖动**，所以这里直接 return：
        //   拖着滑块时不必再做悬停判定，否则拖动途中经过的按钮会一路乱亮。
        if (_sliderDragging)
        {
            var next = CollapseDuration.FromRatio(WinUi.SliderValueAt(_layout.CollapseSlider, x));
            if (next != _collapseSeconds)
            {
                _collapseSeconds = next;
                _invalidate();
            }
            return;
        }

        // ★ 拖选 ★ 左键按着在输入框里移动 = 扩选，与任何编辑框一致。
        //   宿主只给「移动」的坐标、不给按键状态，所以按下时自己置 _dragging，抬起时清掉。
        //   移动到了框外也照样扩选（光标会停在被拖到的字符上），这样「往回拖」不会断。
        if (_dragging && _focus != Focus.None)
        {
            if (TryGetFocusedField(out var field))
            {
                _caretSeekX = x - (field.Left + FieldPadX) + _editScroll;
                _caretSeekExtend = true;
                _invalidate();
                return;
            }
        }

        var hit = HitTest(x, y);
        if (hit.Id == _hover.Id && hit.Index == _hover.Index) return;
        SetHover(hit);
        _invalidate();
    }

    private void OnMouseDownCore(float x, float y)
    {
        var hit = HitTest(x, y);
        _pressed = hit;
        SetHover(hit);

        // 这次点击是否「进入了某个输入框」：进入的话由 ClickIntoField 完成绑定（含按点击位置落光标），
        // 没进入的话说明是按钮/结构性操作 —— 那些会绕过编辑模型直接改缓冲，
        // 必须在末尾重新绑定，否则编辑模型里那份拷贝就过期了（见 RebindFocusedField）。
        bool enteredField = false;

        switch (hit.Id)
        {
            case HitId.LastSource:
                AddLastSource();
                break;

            case HitId.Clear:
                _lines.Clear();
                _lines.Add("");
                _activeLine = 0;
                _scroll = 0;
                _dirty = true;
                _host.Note("已清空白名单编辑区（记得点“保存配置”生效）");
                break;

            case HitId.WhitelistAdd:
                // 新增一个空输入框并聚焦到它 —— 与「回车递进」等价，只是用鼠标也能做。
                // 注意**不要**在这里动图标映射的待确认粘贴计划：白名单与它是两套缓冲，
                // 互不影响（早先误加过一次 CancelPendingPaste，会让用户莫名丢掉图标页的确认态）。
                if (_lines.Count == 1 && _lines[0].Length == 0) _lines.Clear();
                _lines.Add("");
                _activeLine = _lines.Count - 1;
                _focus = Focus.Whitelist;
                if (_activeLine >= _scroll + _layout.VisibleWhitelistEntries)
                    _scroll = _activeLine - _layout.VisibleWhitelistEntries + 1;
                _dirty = true;
                break;

            case HitId.ScrollUp:
                _scroll = Math.Max(0, _scroll - 1);
                break;

            case HitId.ScrollDown:
                _scroll = Math.Min(Math.Max(0, _lines.Count - _layout.VisibleWhitelistEntries), _scroll + 1);
                break;

            case HitId.LineRemove:
                if (hit.Index >= 0 && hit.Index < _lines.Count)
                {
                    _lines.RemoveAt(hit.Index);
                    if (_lines.Count == 0) _lines.Add("");
                    _activeLine = Math.Min(_activeLine, _lines.Count - 1);
                    _scroll = Math.Min(_scroll, Math.Max(0, _lines.Count - _layout.VisibleWhitelistEntries));
                    _dirty = true;
                    _host.Note("已删除一条白名单（记得点“保存配置”生效）");
                }
                break;

            case HitId.WhitelistBox:
                if (hit.Index >= 0 && hit.Index < _lines.Count)
                {
                    CommitActiveField();                       // 先提交原来那个框（顺序见其说明）
                    int visible = hit.Index - _scroll;
                    BindField(Focus.Whitelist, hit.Index, _layout.WhitelistEntryField(visible),
                        x, y, InputRules.WhitelistMaxLength, null);
                    enteredField = true;
                }
                break;

            case HitId.CollapseSlider:
                // 按在滑块上：**先跳到该位置**（Windows 滑块就是「点哪跳哪」，不是「点一下挪一格」），
                // 随后进入拖动态、每次移动都跟随。
                _sliderDragging = true;
                _collapseSeconds = CollapseDuration.FromRatio(WinUi.SliderValueAt(_layout.CollapseSlider, x));
                _invalidate();
                break;

            case HitId.CollapseIgnore:
                // 开关是一次性动作（没有「拖到一半」的中间态），所以**立刻落盘**，不像滑块要等抬手。
                _collapseIgnoreMouse = !_collapseIgnoreMouse;
                try { _host.SetCollapseIgnoreMouse(_collapseIgnoreMouse); }
                catch { /* 落盘失败不该让面板崩；界面仍按新状态显示 */ }
                _invalidate();
                break;

            case HitId.PortField:
                CommitActiveField();
                BindField(Focus.Port, -1, _layout.PortField, x, y, 5, char.IsDigit);
                enteredField = true;
                break;

            case HitId.PortMinus10: StepPort(-10); break;
            case HitId.PortMinus1: StepPort(-1); break;
            case HitId.PortPlus1: StepPort(1); break;
            case HitId.PortPlus10: StepPort(10); break;

            case HitId.PhoneCopy:
            {
                // 复制的是**当前显示的那个地址**（含正在编辑的端口）——
                // 与用户眼睛看到的必须一致，否则「复制粘贴过去连不上」会让人怀疑自己看错了。
                string address = CurrentPhoneAddress();
                if (address.Length == 0)
                {
                    _host.Note($"复制失败：{PhoneAddress.NoAddressHint}");
                    break;
                }
                _host.Note(_host.CopyText(address)
                    ? $"已复制手机端地址：{address}"
                    : "复制失败：剪贴板被其它程序占用，请稍后再试");
                break;
            }

            case HitId.TopmostToggle:
                // 真正的置顶切换由窗口完成（面板不自己记状态，避免按钮与实际行为不一致）
                _host.SetTopmost(!_host.Topmost);
                _host.Note(_host.Topmost ? "配置窗口已置顶" : "已取消置顶");
                break;

            case HitId.LogCopy:
                CopyLogClicked();
                break;

            case HitId.LogScrollUp:
                _logScroll++;
                break;

            case HitId.LogScrollDown:
                _logScroll = Math.Max(0, _logScroll - 1);
                break;

            case HitId.Save:
                SaveClicked();
                break;

            case HitId.Service:
                _host.ToggleService();
                break;

            // ── 更新区块 ──
            case HitId.UpdateCheck:
                _host.Note("已请求检查更新（需要几秒，结果会写进日志）");
                _host.CheckUpdateNow();
                break;

            case HitId.UpdateCopy:
                _host.CopyUpdateInfo();
                break;

            case HitId.UpdateOpen:
                _host.OpenMarketPage();
                break;

            case HitId.UpdateModeOff:
                _host.SetBannerMode(UpdateBannerMode.Off);
                break;

            case HitId.UpdateModeAuto:
                _host.SetBannerMode(UpdateBannerMode.AutoDismiss);
                break;

            case HitId.UpdateModeClick:
                _host.SetBannerMode(UpdateBannerMode.Clickable);
                break;

            case HitId.UpdateActionMarket:
                _host.SetClickAction(UpdateClickAction.Market);
                break;

            case HitId.UpdateActionClipboard:
                _host.SetClickAction(UpdateClickAction.Clipboard);
                break;

            case HitId.UpdateActionDownload:
                _host.SetClickAction(UpdateClickAction.Download);
                break;

            // ── 一键静默升级 ──
            case HitId.UpgradeToggleOn:
                _host.SetSilentUpgrade(true);
                break;

            case HitId.UpgradeToggleOff:
                _host.SetSilentUpgrade(false);
                break;

            case HitId.UpgradeNow:
                _host.UpgradeNow();
                break;

            // ── 在线文档 ──
            // 两个按钮都用浏览器打开官网对应页面（文档真源在网站仓库，插件不内嵌副本）
            case HitId.TutorialOpen:
                OpenTutorial();
                break;

            case HitId.ChangelogOpen:
                OpenChangelog();
                break;

            case HitId.IconMapOpen:
                // 进入前刷新一次缓冲，避免显示的还是别处改过的旧值
                ReloadIconBuffer();
                _page = PanelPage.IconMap;
                _focus = Focus.None;
                break;

            case HitId.IconMapBack:
                // 返回不丢弃改动：映射缓冲与主页面缓冲一样是「未保存的编辑态」，
                // 提示用户去点「保存映射」即可（与白名单的行为保持一致）。
                if (_iconDirty) _host.Note("图标映射有未保存的改动；点“保存映射”才会生效");
                _page = PanelPage.Main;
                _focus = Focus.None;
                break;

            case HitId.IconMapApply:
                SaveIconMapClicked();
                break;

            case HitId.IconMapAdd:
                CancelPendingPaste();   // 缓冲即将变化，之前那份「所见即所执行」的承诺失效
                AddIconLine();
                break;

            case HitId.IconMapCopy:
                CopyActiveIconLine();
                break;

            case HitId.IconMapPaste:
                PasteIntoIconLines();
                break;

            case HitId.IconMapReset:
                CancelPendingPaste();
                _iconLines.Clear();
                _iconScroll = 0;
                _iconActiveLine = -1;
                _iconDirty = true;
                FlashIcon("");
                _host.Note("已清空自定义映射（点“保存映射”生效；内置映射不受影响）");
                break;

            case HitId.IconMapLineRemove:
                if (hit.Index >= 0 && hit.Index < _iconLines.Count)
                {
                    CancelPendingPaste();
                    _iconLines.RemoveAt(hit.Index);
                    _iconActiveLine = Math.Min(_iconActiveLine, _iconLines.Count - 1);
                    _iconScroll = Math.Min(_iconScroll, Math.Max(0, _iconLines.Count - VisibleIconLines()));
                    _iconDirty = true;
                    FlashIcon("");
                    _host.Note("已删除一条映射（点“保存映射”生效）");
                }
                break;

            case HitId.IconMapBox:
                // ⚠️ 点选**不**清待确认计划：用户很可能就是「看到冲突提示 → 点一下别的行确认一下 → 再回来点确认」。
                //    真正让计划失效的是「缓冲被改动」（增行/删行/清空/输入字符）。
                //
                // ★ 空映射时要**先造一条**再聚焦 ★
                //   `_iconLines` 为空时，命中判定给不出行号（返回 Index = -1，因为那一行根本不存在），
                //   若此处不建行，用户点进编辑区就会「打了字却没反应」——最难查的一类问题。
                //   旧版把这条「按需创建」藏在按键处理里，这里提到点击时做，行为更直白：点一下就能打字。
                CommitActiveField();   // 缓冲即将变动 → 先把上一个框的改动写回（此时行号还是旧的）

                if (_iconLines.Count == 0)
                {
                    _iconLines.Add("");
                    _iconActiveLine = 0;
                    _iconScroll = 0;
                }
                if (_iconActiveLine < 0 || _iconActiveLine >= _iconLines.Count)
                    _iconActiveLine = 0;
                {
                    var mBox = ConfigLayout.ComputeIconMap(_layout.ContentWidth);
                    int visBox = _iconActiveLine - _iconScroll;
                    BindField(Focus.IconMapKey, _iconActiveLine, mBox.KeyFieldAt(visBox), x, y,
                        InputRules.IconKeyMaxLength, null);
                    enteredField = true;
                }
                break;

            // 点中具体哪个框，焦点就落在哪个框 —— 鼠标是直接换框的手段（Tab 也支持，见 OnCharCore）
            case HitId.IconMapKeyField:
            case HitId.IconMapValueField:
                if (hit.Index >= 0 && hit.Index < _iconLines.Count)
                {
                    CommitActiveField();
                    var mField = ConfigLayout.ComputeIconMap(_layout.ContentWidth);
                    int vis = hit.Index - _iconScroll;
                    bool isKey = hit.Id == HitId.IconMapKeyField;
                    BindField(isKey ? Focus.IconMapKey : Focus.IconMapValue, hit.Index,
                        isKey ? mField.KeyFieldAt(vis) : mField.ValueFieldAt(vis), x, y,
                        isKey ? InputRules.IconKeyMaxLength : InputRules.IconValueMaxLength, null);
                    enteredField = true;
                }
                break;

            case HitId.IconMapScrollUp:
                _iconScroll = Math.Max(0, _iconScroll - 1);
                break;

            case HitId.IconMapScrollDown:
                _iconScroll = Math.Min(Math.Max(0, _iconLines.Count - VisibleIconLines()), _iconScroll + 1);
                break;

            // ── 应用黑名单页 ──
            case HitId.BlacklistOpen:
                // 进入前刷新一次缓冲，避免显示的还是别处改过的旧值
                ReloadBlacklistBuffer();
                _page = PanelPage.Blacklist;
                _focus = Focus.None;
                break;

            case HitId.BlacklistBack:
                // 返回**不丢弃**改动（与图标映射页一致）：缓冲仍是「未保存的编辑态」，
                // 提示用户去点「保存名单」即可。
                if (_blacklistDirty) _host.Note("应用黑名单有未保存的改动；点“保存名单”才会生效");
                _page = PanelPage.Main;
                _focus = Focus.None;
                break;

            case HitId.BlacklistApply:
                SaveBlacklistClicked();
                break;

            case HitId.BlacklistAdd:
                AddBlacklistLine();
                break;

            case HitId.BlacklistLineRemove:
                if (hit.Index >= 0 && hit.Index < _blacklistLines.Count)
                {
                    _blacklistLines.RemoveAt(hit.Index);
                    // 删空时留一个空行：否则编辑区变成「什么都没有」的空白卡片，
                    // 用户看不出下一步该点哪里（而「＋添加一行」在下方工具行里）。
                    if (_blacklistLines.Count == 0) _blacklistLines.Add("");
                    _blacklistActiveLine = Math.Min(_blacklistActiveLine, _blacklistLines.Count - 1);
                    _blacklistScroll = Math.Min(_blacklistScroll,
                        Math.Max(0, _blacklistLines.Count - VisibleBlacklistLines()));
                    _blacklistDirty = true;
                    _host.Note("已删除一条黑名单（点“保存名单”生效）");
                }
                break;

            case HitId.BlacklistBox:
            case HitId.BlacklistLine:
                // ★ 空名单时要**先造一条**再聚焦 ★
                //   缓冲为空时命中判定给不出行号（那一行根本不存在），若此处不建行，
                //   用户点进编辑区就会「打了字却没反应」——最难查的一类问题（与图标映射页同款）。
                CommitActiveField();   // 缓冲即将变动 → 先把上一个框的改动写回（此时行号还是旧的）

                if (hit.Id == HitId.BlacklistLine && hit.Index >= 0 && hit.Index < _blacklistLines.Count)
                    _blacklistActiveLine = hit.Index;

                if (_blacklistLines.Count == 0)
                {
                    _blacklistLines.Add("");
                    _blacklistActiveLine = 0;
                    _blacklistScroll = 0;
                }
                if (_blacklistActiveLine < 0 || _blacklistActiveLine >= _blacklistLines.Count)
                    _blacklistActiveLine = 0;
                {
                    var blBox = ConfigLayout.ComputeBlacklist(_layout.ContentWidth);
                    int visBox = _blacklistActiveLine - _blacklistScroll;
                    BindField(Focus.Blacklist, _blacklistActiveLine, blBox.FieldAt(visBox), x, y,
                        InputRules.BlacklistMaxLength, null);
                    enteredField = true;
                }
                break;

            case HitId.BlacklistScrollUp:
                _blacklistScroll = Math.Max(0, _blacklistScroll - 1);
                break;

            case HitId.BlacklistScrollDown:
                _blacklistScroll = Math.Min(Math.Max(0, _blacklistLines.Count - VisibleBlacklistLines()),
                    _blacklistScroll + 1);
                break;

            default:
                _focus = Focus.None;
                break;
        }

        // 没进输入框 ⇒ 这次点的是按钮/结构性操作，缓冲可能已被直接改动 → 重新装载编辑模型
        if (!enteredField) RebindFocusedField();

        _invalidate();
    }

    /// <summary>
    /// 点「配置教程」：用浏览器打开**官网的配置教程页**。
    ///
    /// 面板**不自己起进程、也不自己落盘** —— 那是有副作用的系统动作，
    /// 交给宿主执行并记录日志（见 <see cref="IBridgePanelHost.OpenTutorial"/>）。
    /// 这里只做一件事：把「用户点了」这个意图转达出去。
    /// </summary>
    private void OpenTutorial() => _host.OpenTutorial();

    /// <summary>
    /// 点「更新日志」：用浏览器打开**官网的更新日志页**。
    /// 与 <see cref="OpenTutorial"/> 同一条约定（面板只上报点击，宿主负责打开与记日志）。
    /// </summary>
    private void OpenChangelog() => _host.OpenChangelog();

    private void OnMouseUpCore(float x, float y)
    {
        _pressed = (HitId.None, -1);

        // ★ 滑块抬笔 ★ 到这里才落盘一次 —— 拖动期间每帧都在改值，
        //   若每帧都写设置就是几十次无谓的持久化（见 _collapseSeconds 的说明）。
        if (_sliderDragging)
        {
            _sliderDragging = false;
            try { _host.SetCollapseSeconds(_collapseSeconds); }
            catch { /* 落盘失败不该让面板崩；值仍显示在界面上 */ }
            _invalidate();
            return;
        }

        // 松开左键 = 拖选结束。选区**保留**（用户接下来可能要复制或直接输入覆盖），
        // 只是不再跟随鼠标移动。
        _dragging = false;
        _invalidate();
    }

    /// <summary>
    /// 鼠标滚轮。**自建窗口会真的派发它**（宿主窗口过程里没有 <c>WM_MOUSEWHEEL</c> 分支，
    /// 所以走宿主窗口那条路时永远收不到 —— 那正是过去滚轮形同不存在的原因）。
    ///
    /// 现在窗口可缩放、列表可长可短，滚轮是**最自然的滚动手段**：按指针所在区域分派 ——
    /// 指针在白名单框里滚白名单、在映射框里滚映射、在日志区滚日志。
    /// 不做「按全局位置猜」：那样鼠标停在白名单上却把日志滚走了，是最让人恼火的一类交互。
    /// </summary>
    public void OnWheel(float x, float y, float delta)
    {
        lock (_sync)
        {
            // delta > 0 = 向上滚（内容往上走，看更靠前的内容）
            int steps = delta > 0 ? 1 : delta < 0 ? -1 : 0;
            if (steps == 0) return;

            if (_page == PanelPage.IconMap)
            {
                var m = ConfigLayout.ComputeIconMap(_layout.ContentWidth);
                if (!ConfigLayout.Contains(m.Box, x, y)) return;
                int max = Math.Max(0, _iconLines.Count - m.VisibleLines);
                int next = Math.Clamp(_iconScroll - steps, 0, max);
                if (next == _iconScroll) return;
                _iconScroll = next;
            }
            else if (_page == PanelPage.Blacklist)
            {
                var b = ConfigLayout.ComputeBlacklist(_layout.ContentWidth);
                if (!ConfigLayout.Contains(b.Box, x, y)) return;
                int max = Math.Max(0, _blacklistLines.Count - b.VisibleLines);
                int next = Math.Clamp(_blacklistScroll - steps, 0, max);
                if (next == _blacklistScroll) return;
                _blacklistScroll = next;
            }
            else if (ConfigLayout.Contains(_layout.WhitelistBox, x, y))
            {
                int max = Math.Max(0, _lines.Count - _layout.VisibleWhitelistEntries);
                int next = Math.Clamp(_scroll - steps, 0, max);
                if (next == _scroll) return;
                _scroll = next;
            }
            else if (ConfigLayout.Contains(_layout.LogBox, x, y))
            {
                // 日志的「上翻」是往回看历史，所以向上滚 = 增大 _logScroll
                int max = MaxLogScroll(_host.Snapshot());
                int next = Math.Clamp(_logScroll + steps, 0, max);
                if (next == _logScroll) return;
                _logScroll = next;
            }
            else
            {
                return;
            }

            _invalidate();
        }
    }

    /// <summary>
    /// 键盘字符输入的**分发**（仅自有窗口可用；宿主设置页接口不提供键盘事件）。
    ///
    /// 分成三个独立方法而不是一长串 if：三处的输入规则完全不同
    /// （端口只吃数字、白名单每条一个框、映射每条两个框），混在一起最容易改坏一处而不自知。
    /// </summary>
    // ====================================================================
    // 输入框：焦点绑定 / 提交 / 特殊键 / 心跳
    // ====================================================================

    /// <summary>光标闪烁周期（毫秒，一亮一灭各占一半）。</summary>
    private const long CaretBlinkMs = 530;

    /// <summary>输入框内的文字左右内边距 —— **绘制与命中必须用同一个值**，否则点到的位置与画出的位置会差几像素。</summary>
    private const float FieldPadX = 6f;

    /// <summary>输入框命中判定的键标识（用 VK 码；Ctrl 组合另加偏移以免与单独按键共用重复状态）。</summary>
    private const int KeyIdLeft = 0x25, KeyIdRight = 0x27, KeyIdHome = 0x24, KeyIdEnd = 0x23, KeyIdDelete = 0x2E;
    private const int KeyIdWordOffset = 0x1000;

    /// <summary>
    /// 承载面板的窗口**尺寸变化**后调用：丢掉与旧几何绑定的暂存值并请求重绘。
    ///
    /// ★ 为什么必须丢 ★ 窗口一变大变小，**每个框的矩形都变了**；而「待解析的点击落点」
    /// 是按**旧几何**算出来的 x（见 <see cref="_caretSeekX"/>）。若留着它，
    /// 下一帧会拿旧 x 去套新框 → 光标落到莫名其妙的位置。
    /// 同理，正在进行的拖选也必须结束（鼠标与框的相对关系已经变了）。
    /// </summary>
    public void RequestRedrawForResize()
    {
        lock (_sync)
        {
            _caretSeekX = float.NaN;
            _anchorSeekX = float.NaN;
            _dragging = false;
            _invalidate();
        }
    }

    /// <summary>
    /// 让竖向滚动位置落在当前可视条数之内。
    ///
    /// 窗口缩小后，「可见条数」会变少：原来合法的 `_scroll` 可能已经指到最后一条之外，
    /// 表现为**白名单/映射看起来空了**（其实只是滚过头）。每次布局重算后夹一次即可。
    /// </summary>
    private void ClampScrollToLayout()
    {
        _scroll = Math.Clamp(_scroll, 0, Math.Max(0, _lines.Count - _layout.VisibleWhitelistEntries));
        _iconScroll = Math.Clamp(_iconScroll, 0, Math.Max(0, _iconLines.Count - VisibleIconLines()));
    }

    /// <summary>
    /// 某个点是否应当**由面板接收鼠标**，而不是被窗口当成「标题条拖动区」。
    ///
    /// ★ 为什么需要这个 ★ 窗口把顶部一条当作拖动区（<c>HTCAPTION</c>，这是「窗口可移动」的实现），
    /// 但标题行右侧的「置顶」按钮**正好压在那条里** —— 若不排除，
    /// 用户点它就变成了拖动窗口，按钮等于半死。
    /// 判定放在面板这边，是因为**几何知识归面板**（按钮矩形来自 <see cref="ConfigLayout"/>）；
    /// 窗口只负责问一句「这一点你想要吗」。
    /// </summary>
    public bool WantsMouseAt(float x, float y)
    {
        lock (_sync)
        {
            // ★ 标题行（y 14..38）的一部分落在窗口拖动条（y ≤ 30）里 ⇒
            //   标题行里的**每一个可点击按钮**都必须在这里声明「这一点归面板」，
            //   否则它落在拖动条内的那一段会被当拖动、点不动
            //   （症状：按钮上半部分点不中，要挪到下半截才生效）。
            //
            // ⚠️⚠️ 这是**按页面逐个列出**的：本方法漏掉哪个按钮，哪个按钮就点不中。
            //   2026-09-25 就因此让新加的「更新日志」入口复现了同样的毛病 ——
            //   **加标题行按钮时必须同时改这里**（回归断言见 PanelSmokeTests 的窗口节）。
            switch (_page)
            {
                case PanelPage.Main:
                    return ConfigLayout.Contains(ConfigLayout.BlacklistButtonOf(_layout), x, y)
                        || ConfigLayout.Contains(ConfigLayout.ChangelogButtonOf(_layout), x, y)
                        || ConfigLayout.Contains(ConfigLayout.TopmostButtonOf(_layout), x, y);

                case PanelPage.IconMap:
                {
                    var m = ConfigLayout.ComputeIconMap(_layout.ContentWidth);
                    return ConfigLayout.Contains(m.BackButton, x, y)
                        || ConfigLayout.Contains(m.ApplyButton, x, y);
                }

                case PanelPage.Blacklist:
                {
                    var b = ConfigLayout.ComputeBlacklist(_layout.ContentWidth);
                    return ConfigLayout.Contains(b.BackButton, x, y)
                        || ConfigLayout.Contains(b.ApplyButton, x, y);
                }

                default:
                    return false;
            }
        }
    }

    /// <summary>是否有输入框聚焦 —— 插件据此决定心跳定时器要不要做实际工作。</summary>
    /// <summary>
    /// 是否仍在**悬停过渡**中（鼠标刚移入 / 移出某个控件后的约 150ms）。
    /// 心跳据此在动画期间持续重绘 —— 否则渐变只画出第一帧就停住，看起来像「只亮了一下」。
    /// </summary>
    private bool HoverAnimating
        => HoverAnimMs > 0f && Environment.TickCount64 - _hoverChangedAt < HoverAnimMs;

    /// <summary>
    /// 心跳是否需要做实际工作：**有输入框聚焦**（光标闪烁 + 方向键轮询）**或悬停动画还在跑**。
    /// 空闲时两者皆否 —— 插件侧的回调体就只是一次布尔判断，不产生额外开销。
    /// </summary>
    public bool NeedsTicks => _focus != Focus.None || HoverAnimating;

    /// <summary>
    /// 承载面板的窗口是否**直接提供特殊键事件**（自建窗口为 true）。
    ///
    /// ★ 为什么要有这个开关 ★ 自建窗口能收到真正的 <c>WM_KEYDOWN</c>（方向键 / Home / End / Delete），
    /// 这是最好的通路；而宿主窗口只能靠 <c>GetAsyncKeyState</c> 只读轮询来补。
    /// 两者**不能同时生效** —— 否则按一次方向键会让光标走两格。
    /// 所以由窗口侧声明「我能给按键事件」，面板据此关掉轮询。
    /// </summary>
    public bool NativeKeyEvents { get; set; }

    /// <summary>
    /// 处理来自窗口的**特殊键**（方向键 / Home / End / Delete）。
    /// 字符类输入仍走 <see cref="OnChar"/>（宿主与自建窗口都提供 <c>WM_CHAR</c>）。
    /// </summary>
    /// <param name="virtualKey">Win32 虚拟键码。</param>
    /// <param name="shift">按下时是否按住 Shift（决定是移动光标还是扩选）。</param>
    /// <param name="ctrl">按下时是否按住 Ctrl（方向键按词跳）。</param>
    public void OnSpecialKey(int virtualKey, bool shift, bool ctrl)
    {
        lock (_sync)
        {
            if (_focus == Focus.None) return;

            bool acted = false;
            switch (virtualKey)
            {
                case 0x25:   // VK_LEFT
                    if (ctrl) _edit.MoveWordLeft(shift); else _edit.MoveLeft(shift);
                    acted = true;
                    break;
                case 0x27:   // VK_RIGHT
                    if (ctrl) _edit.MoveWordRight(shift); else _edit.MoveRight(shift);
                    acted = true;
                    break;
                case 0x24:   // VK_HOME
                    _edit.MoveHome(shift);
                    acted = true;
                    break;
                case 0x23:   // VK_END
                    _edit.MoveEnd(shift);
                    acted = true;
                    break;
                case 0x2E:   // VK_DELETE
                    acted = _edit.DeleteForward();
                    break;
            }

            if (!acted) return;

            CommitEdit();
            ShowCaret();
            _invalidate();
        }
    }

    // ── 测试探针（只读）────────────────────────────────────────────────
    // 「光标落在第几个字符」「选区是哪一段」「聚焦的是哪个框」这些状态**无法从像素上可靠断言**
    // （去数画布上的像素既脆又难读）。这几个只读属性把编辑状态直接暴露给测试，
    // 让「点在第 5 个字符上 → 光标就在第 5 个字符」这类断言成为可能。
    internal int ProbeCaretIndex => _edit.Caret;
    internal int ProbeSelectionStart => _edit.SelectionStart;
    internal int ProbeSelectionEnd => _edit.SelectionEnd;
    internal string ProbeFieldText => _edit.Text;
    internal bool ProbeCaretVisible => _focus != Focus.None && _caretOn;
    internal bool ProbeDragging => _dragging;
    internal string ProbeFocus => _focus.ToString();

    /// <summary>
    /// 承载窗口是否**直接提供特殊键事件**（见 <see cref="NativeKeyEvents"/>）。
    ///
    /// ★ 为什么要断言它 ★ 它是「原生按键」与「轮询」两条通路的总闸门：
    /// 一旦两条同时生效，按一次方向键会走两格（用户体验上像是按键失灵/跳字）。
    /// 默认必须是 false（宿主设置页环境），由自建窗口显式打开。
    /// </summary>
    internal bool ProbeNativeKeyEvents => NativeKeyEvents;

    /// <summary>白名单滚动位置（滚轮测试用）。</summary>
    internal int ProbeWhitelistScroll => _scroll;
    /// <summary>图标映射页滚动位置。</summary>
    internal int ProbeIconScroll => _iconScroll;
    /// <summary>应用黑名单页滚动位置。</summary>
    internal int ProbeBlacklistScroll => _blacklistScroll;
    /// <summary>应用黑名单页的编辑缓冲（只读）—— 测试用它断言「增行 / 删行 / 输入」是否真的落到缓冲上。</summary>
    internal IReadOnlyList<string> ProbeBlacklistLines => _blacklistLines;
    /// <summary>应用黑名单是否有未保存改动（决定「保存名单」按钮是否带 *）。</summary>
    internal bool ProbeBlacklistDirty => _blacklistDirty;
    /// <summary>当前显示的是哪一页（Main / IconMap / Blacklist）—— 测试用它验证切页与返回。</summary>
    internal string ProbePage => _page.ToString();
    /// <summary>日志上翻行数（0 = 跟随最新）。</summary>
    internal int ProbeLogScroll => _logScroll;
    /// <summary>滑块当前值（秒）—— 测试用它验证「点哪跳哪 / 拖动跟随」。</summary>
    internal float ProbeCollapseSeconds => _collapseSeconds;
    /// <summary>是否正处于拖动中 —— 测试用它验证「拖动期间不落盘」。</summary>
    internal bool ProbeSliderDragging => _sliderDragging;

    /// <summary>
    /// 心跳（约 30Hz，由插件侧定时器驱动）：① 光标闪烁；② 方向键 / Home / End / Delete。
    ///
    /// ★ 为什么需要心跳 ★ 宿主不转发 <c>WM_KEYDOWN</c>（只有 Esc），所以方向键拿不到**事件**，
    /// 只能周期性地**读键状态**（见 <see cref="NativeKeys"/>）。
    /// 心跳只在有输入框聚焦时才有实际动作，其余时间只是一次布尔判断。
    /// </summary>
    public void Tick()
    {
        lock (_sync)
        {
            long now = Environment.TickCount64;

            // ⓪ 悬停过渡动画：进行中就持续重绘（颜色由 HoverT 按经过时间插值）。
            //    没有输入框聚焦时也要跑 —— 否则鼠标划过按钮不会有渐变色。
            if (HoverAnimating) _invalidate();

            if (_focus == Focus.None)
            {
                _repeat.Reset();
                if (!_caretOn) { _caretOn = true; _invalidate(); }
                return;
            }

            // ① 光标闪烁：**相位变了才重绘**，否则就是白刷 30fps
            bool on = now < _caretHoldUntil || (now / CaretBlinkMs) % 2 == 0;
            if (on != _caretOn) { _caretOn = on; _invalidate(); }

            // ② 特殊键
            // ⚠️ 窗口能直接给按键事件时**必须跳过轮询**：两条通路同时生效会让一次按键走两格
            if (NativeKeyEvents) return;

            var keys = NativeKeys.Poll();
            if (!keys.Usable) return;

            bool acted = false;

            void Fire(int keyId, bool down, Action move)
            {
                if (!_repeat.ShouldFire(keyId, down, now)) return;
                move();
                acted = true;
            }

            // Ctrl + 方向 = 按词跳（与 Windows 一致）
            if (keys.Ctrl)
            {
                Fire(KeyIdLeft + KeyIdWordOffset, keys.Left, () => _edit.MoveWordLeft(keys.Shift));
                Fire(KeyIdRight + KeyIdWordOffset, keys.Right, () => _edit.MoveWordRight(keys.Shift));
            }
            else
            {
                Fire(KeyIdLeft, keys.Left, () => _edit.MoveLeft(keys.Shift));
                Fire(KeyIdRight, keys.Right, () => _edit.MoveRight(keys.Shift));
            }

            Fire(KeyIdHome, keys.Home, () => _edit.MoveHome(keys.Shift));
            Fire(KeyIdEnd, keys.End, () => _edit.MoveEnd(keys.Shift));

            if (keys.Delete && _repeat.ShouldFire(KeyIdDelete, true, now))
            {
                if (_edit.DeleteForward()) acted = true;
                else _repeat.ShouldFire(KeyIdDelete, false, now);   // 没得删也别卡在按下态
            }

            if (acted)
            {
                CommitEdit();
                ShowCaret();
                _invalidate();
            }
        }
    }

    /// <summary>双击判定窗口（毫秒）与允许的位移（像素）。</summary>
    private const long DoubleClickMs = 400;
    private const float DoubleClickSlop = 4f;

    /// <summary>
    /// ① 提交**当前**聚焦框的改动。
    ///
    /// ⚠️ **必须在改动 <c>_activeLine</c> / <c>_iconActiveLine</c> 或缓冲之前调用** ⚠️
    /// <see cref="CommitEdit"/> 是按「当前行号」写回的；若先把行号改成新选中的那一行，
    /// 提交就会把**上一个框的文本写进新那行** —— 表现为「点一下另一条，新那条的内容被替换掉了」。
    /// 这个顺序错误曾被 <c>PanelSmokeTests</c> 的「点回第 1 条内容仍是 1.1.1.1」当场逮到。
    /// </summary>
    private void CommitActiveField()
    {
        if (_focus != Focus.None) CommitEdit();
    }

    /// <summary>
    /// ② 切换焦点到某个框并装载编辑模型（**不含提交**，提交由 <see cref="CommitActiveField"/> 先做）。
    ///
    /// ★ 双击为什么要自己实现 ★ 宿主窗口类**没有 <c>CS_DBLCLKS</c>**，
    /// 所以收不到 <c>WM_LBUTTONDBLCLK</c>；用「两次点击的时间 + 位移」自己判定即可，
    /// 这也是托盘图标、绘图类应用通用的做法。
    /// </summary>
    private void BindField(Focus focus, int lineIndex, SKRect field, float x, float y,
        int maxLength, Func<char, bool>? filter)
    {
        if (focus == Focus.Whitelist) _activeLine = lineIndex;
        else if (focus is Focus.IconMapKey or Focus.IconMapValue) _iconActiveLine = lineIndex;
        else if (focus == Focus.Blacklist) _blacklistActiveLine = lineIndex;

        _focus = focus;
        _dragging = true;

        BindEdit(CurrentFieldText(focus), maxLength, filter, x - (field.Left + FieldPadX));

        long now = Environment.TickCount64;
        bool doubleClick = now - _lastClickAt <= DoubleClickMs
                           && Math.Abs(x - _lastClickX) <= DoubleClickSlop
                           && Math.Abs(y - _lastClickY) <= DoubleClickSlop;
        _lastClickAt = now;
        _lastClickX = x;
        _lastClickY = y;

        if (doubleClick)
        {
            _edit.SelectAll();
            _caretSeekX = float.NaN;      // 双击＝全选，不要再按落点覆盖
            _anchorSeekX = float.NaN;
        }
    }

    /// <summary>当前聚焦框对应的模型文本（绑定编辑模型时用）。</summary>
    private string CurrentFieldText(Focus focus) => focus switch
    {
        Focus.Whitelist => _activeLine >= 0 && _activeLine < _lines.Count ? _lines[_activeLine] : "",
        Focus.Port => _portText,
        Focus.IconMapKey => ValidIconLine ? InputRules.SplitIconRow(_iconLines[_iconActiveLine]).Key : "",
        Focus.IconMapValue => ValidIconLine ? InputRules.SplitIconRow(_iconLines[_iconActiveLine]).Value : "",
        Focus.Blacklist => ValidBlacklistLine ? _blacklistLines[_blacklistActiveLine] : "",
        _ => "",
    };

    /// <summary>图标映射页的活动行是否有效（越界时一律不碰缓冲，避免 IndexOutOfRange）。</summary>
    private bool ValidIconLine => _iconActiveLine >= 0 && _iconActiveLine < _iconLines.Count;

    /// <summary>黑名单页的活动行是否有效（同 <see cref="ValidIconLine"/>）。</summary>
    private bool ValidBlacklistLine => _blacklistActiveLine >= 0 && _blacklistActiveLine < _blacklistLines.Count;

    /// <summary>取当前聚焦框的矩形（拖选时用来把鼠标 x 换算成框内坐标）。</summary>
    private bool TryGetFocusedField(out SKRect field)
    {
        field = default;
        switch (_focus)
        {
            case Focus.Whitelist:
                if (_activeLine < 0 || _activeLine >= _lines.Count) return false;
                int vis = _activeLine - _scroll;
                if (vis < 0 || vis >= _layout.VisibleWhitelistEntries) return false;
                field = _layout.WhitelistEntryField(vis);
                return true;

            case Focus.Port:
                field = _layout.PortField;
                return true;

            case Focus.IconMapKey:
            case Focus.IconMapValue:
                if (!ValidIconLine) return false;
                var m = ConfigLayout.ComputeIconMap(_layout.ContentWidth);
                int v = _iconActiveLine - _iconScroll;
                if (v < 0 || v >= m.VisibleLines) return false;
                field = _focus == Focus.IconMapKey ? m.KeyFieldAt(v) : m.ValueFieldAt(v);
                return true;

            case Focus.Blacklist:
                if (!ValidBlacklistLine) return false;
                var bl = ConfigLayout.ComputeBlacklist(_layout.ContentWidth);
                int blVis = _blacklistActiveLine - _blacklistScroll;
                if (blVis < 0 || blVis >= bl.VisibleLines) return false;
                field = bl.FieldAt(blVis);
                return true;

            default:
                return false;
        }
    }

    /// <summary>
    /// 结构性改动（增行 / 删行 / 清空 / 一键加入来源 / 粘贴多行）之后，把编辑模型**重新绑定**到
    /// 当前聚焦的那一行。
    ///
    /// ★ 为什么必须做 ★ <see cref="_edit"/> 里存的是「某一行的文本拷贝」。上面那些操作会
    /// **绕过编辑模型**直接改 <c>_lines</c> / <c>_iconLines</c>，那份拷贝随即过期；
    /// 若不管它，用户下一次敲键就会把**旧一行的文本**写回当前行 ——
    /// 比如「点了＋最近来源，再打一个字，刚加的那条就被上一行的内容覆盖了」。
    /// 这类错位极难复现，所以一律在这里重新装载，成本只是一次字符串赋值。
    /// </summary>
    private void RebindFocusedField()
    {
        switch (_focus)
        {
            case Focus.Whitelist:
                if (_activeLine >= 0 && _activeLine < _lines.Count)
                    BindEdit(_lines[_activeLine], InputRules.WhitelistMaxLength, null, float.NaN);
                else
                    ResetEditState();
                break;

            case Focus.Port:
                BindEdit(_portText, 5, char.IsDigit, float.NaN);
                break;

            case Focus.IconMapKey:
                if (ValidIconLine)
                    BindEdit(InputRules.SplitIconRow(_iconLines[_iconActiveLine]).Key,
                        InputRules.IconKeyMaxLength, null, float.NaN);
                else
                    ResetEditState();
                break;

            case Focus.IconMapValue:
                if (ValidIconLine)
                    BindEdit(InputRules.SplitIconRow(_iconLines[_iconActiveLine]).Value,
                        InputRules.IconValueMaxLength, null, float.NaN);
                else
                    ResetEditState();
                break;

            case Focus.Blacklist:
                if (ValidBlacklistLine)
                    BindEdit(_blacklistLines[_blacklistActiveLine],
                        InputRules.BlacklistMaxLength, null, float.NaN);
                else
                    ResetEditState();
                break;

            default:
                ResetEditState();
                break;
        }
    }

    /// <summary>把光标安排到「这一小段时间内保持可见」（任何编辑动作都该调它）。</summary>
    private void ShowCaret()
    {
        _caretOn = true;
        _caretHoldUntil = Environment.TickCount64 + CaretBlinkMs;
    }

    /// <summary>
    /// 把某个框的文本装进编辑模型、并把焦点交给它。
    ///
    /// ★ 只保留一个编辑态 ★ 见 <see cref="_edit"/> 的说明：任何时刻只有一个焦点，
    /// 聚焦时装载、每次改动立刻写回模型（<see cref="CommitEdit"/>）。
    /// </summary>
    /// <param name="text">该框当前的文本。</param>
    /// <param name="maxLength">该框的长度上限（0 = 不限）。</param>
    /// <param name="filter">额外字符过滤（端口只要数字）；null = 接受所有可打印字符。</param>
    /// <param name="localX">
    /// 点击落点相对**文本起点**的 x（NaN = 无点击，光标放到末尾）。
    /// 之所以存下来而不是当场换算成下标：换算需要文本度量，而度量要用画笔 ——
    /// 画笔只在绘制过程中存在。留到绘制那个框时用**同一支画笔**换算，
    /// 才能满足「量宽与画宽必须同一字体」这条硬约定（见 <see cref="TextGeometry"/>）。
    /// </param>
    /// <param name="extend">这次落点是否用于扩选（Shift+点击 / 拖选）。</param>
    private void BindEdit(string text, int maxLength, Func<char, bool>? filter, float localX, bool extend = false)
    {
        _edit.MaxLength = maxLength;
        _edit.CharFilter = filter;
        _edit.SetText(text);
        _editScroll = 0f;
        _caretSeekX = localX;
        _anchorSeekX = localX;
        _caretSeekExtend = extend;
        _repeat.Reset();
        ShowCaret();
    }

    /// <summary>绘制某个框之前调用：用**这支画笔**把待定的点击落点换算成光标下标。</summary>
    private void ResolveCaretSeek(Func<string, float> measure)
    {
        if (float.IsNaN(_caretSeekX)) return;

        // ★ 先把「按下时那个落点」定为锚点，再落光标 —— 顺序不能反 ★
        // 反向扩选（从右往左拖）时若先落光标，锚点会被一起带到新位置，选区当场变空。
        // 另：锚点必须与光标**分别**待定，因为鼠标按下之后可能连来好几个移动事件才轮到一次重绘；
        // 若共用一个待定值，后一个移动会覆盖掉锚点，拖选就会「选不中」。
        if (!float.IsNaN(_anchorSeekX))
        {
            _edit.SetCaretByIndex(TextGeometry.IndexAt(measure, _edit.Text, _anchorSeekX));
            _anchorSeekX = float.NaN;
        }

        _edit.SetCaretByIndex(TextGeometry.IndexAt(measure, _edit.Text, _caretSeekX), _caretSeekExtend);
        _caretSeekX = float.NaN;
    }

    /// <summary>
    /// 把编辑模型的内容写回**当前聚焦的那个框**对应的模型字段。
    ///
    /// 每次改动立刻写回（而不是等失焦）：这样「保存配置」读到的永远是最新内容，
    /// 不必依赖「用户有没有点到别处」这种巧合。同时作废待确认的粘贴计划 ——
    /// 缓冲已经变了，之前登记的那份「所见即所执行」不再成立。
    /// </summary>
    private void CommitEdit()
    {
        switch (_focus)
        {
            case Focus.Whitelist:
                if (_activeLine >= 0 && _activeLine < _lines.Count) _lines[_activeLine] = _edit.Text;
                _dirty = true;
                EnsureWhitelistVisible(_activeLine);
                break;

            case Focus.Blacklist:
                if (ValidBlacklistLine) _blacklistLines[_blacklistActiveLine] = _edit.Text;
                _blacklistDirty = true;
                EnsureBlacklistVisible(_blacklistActiveLine);
                break;

            case Focus.Port:
                _portText = _edit.Text;
                _dirty = true;
                break;

            case Focus.IconMapKey:
            case Focus.IconMapValue:
                if (_iconActiveLine >= 0 && _iconActiveLine < _iconLines.Count)
                {
                    var (k, v) = InputRules.SplitIconRow(_iconLines[_iconActiveLine]);
                    _iconLines[_iconActiveLine] = _focus == Focus.IconMapKey
                        ? InputRules.ComposeIconRow(_edit.Text, v)
                        : InputRules.ComposeIconRow(k, _edit.Text);
                    _iconDirty = true;
                }
                break;
        }

        CancelPendingPaste();
    }

    /// <summary>字符分发。
    ///
    /// ⚠️ <c>ch &lt; ' '</c> 的分支**必须**写在可打印字符判断之前：
    /// Backspace(0x08)、Ctrl+A/C/V/X(0x01/0x03/0x16/0x18) 都小于空格，
    /// 放在后面会被 <c>ch &gt;= ' '</c> 的门静默挡掉。
    /// </summary>
    private void OnCharCore(char ch)
    {
        if (_focus == Focus.None) return;

        switch (ch)
        {
            case '\u0001':                                  // Ctrl+A —— 全选
                _edit.SelectAll();
                ShowCaret();
                _invalidate();
                return;

            case '\u0003': CopyFromField(); return;          // Ctrl+C
            case '\u0018': CutFromField(); return;           // Ctrl+X
            case '\u0016': PasteIntoField(); return;         // Ctrl+V

            case '\b':                                      // Backspace
                if (_edit.Backspace()) AfterFieldEdit();
                return;

            case '\t': MoveFieldByTab(); return;             // Tab —— 换到下一个框
            case '\r' or '\n': OnEnterInField(); return;     // Enter —— 递进
        }

        if (ch < ' ') return;

        if (_edit.CharFilter != null && !_edit.CharFilter(ch))
        {
            NoteCharRejected();
            return;
        }

        if (_edit.Insert(ch)) AfterFieldEdit();
        else NoteTooLong();
    }

    /// <summary>一次改动之后：写回模型 → 光标复位显示 → 重绘。</summary>
    private void AfterFieldEdit()
    {
        CommitEdit();
        ShowCaret();
        _invalidate();
    }

    // ── 复制 / 剪切 / 粘贴（真正的系统剪贴板，走宿主的 CopyText / PasteText）──

    /// <summary>
    /// Ctrl+C：有选区复制选区，**没有选区则复制整个框**。
    ///
    /// 无选区时复制全部是有意的：这些框里装的是 IP / 路径 / URL，逐字符拖选很麻烦，
    /// 「点进来按 Ctrl+C 就拿走整条」是这类数据上最有用的默认行为。
    /// （图标映射页「搬移整行（应用名=图标值）」仍由工具行的「复制」按钮承担，
    /// 两者分工：框内剪贴板操作针对**这一格**，按钮针对**整行**。）
    /// </summary>
    private void CopyFromField()
    {
        bool whole = !_edit.HasSelection;
        var text = whole ? _edit.Text : _edit.Copy();
        if (text.Length == 0) { _host.Note("没有可复制的内容"); return; }

        _host.Note(_host.CopyText(text)
            ? (whole ? $"已复制整格内容（{text.Length} 字符）" : $"已复制选中内容（{text.Length} 字符）")
            : "复制失败（剪贴板被占用）");
        _invalidate();
    }

    /// <summary>Ctrl+X：把选区（无选区时整格）剪走并写入剪贴板。</summary>
    private void CutFromField()
    {
        bool whole = !_edit.HasSelection;
        var text = whole ? _edit.Text : _edit.Cut();
        if (text.Length == 0) { _host.Note("没有可剪切的内容"); return; }

        if (_host.CopyText(text))
        {
            if (whole) { _edit.SelectAll(); _edit.DeleteSelection(); }
            AfterFieldEdit();
            _host.Note($"已剪切{(whole ? "整格内容" : "选中内容")}（{text.Length} 字符）");
        }
        else
        {
            _host.Note("剪切失败（剪贴板被占用），内容未改动");
            _invalidate();
        }
    }

    /// <summary>Ctrl+V：把剪贴板文本粘贴到光标处（替换选区）。</summary>
    private void PasteIntoField()
    {
        var clip = _host.PasteText();
        if (string.IsNullOrEmpty(clip)) { _host.Note("剪贴板为空"); _invalidate(); return; }

        if (_edit.Paste(clip)) AfterFieldEdit();
        else NoteTooLong();
    }

    // ── 焦点在框之间移动 ──

    /// <summary>Tab：移到下一个框（白名单逐条、映射页「应用名→图标值→下一行」）。</summary>
    private void MoveFieldByTab()
    {
        switch (_focus)
        {
            case Focus.Whitelist:
                if (_activeLine + 1 < _lines.Count)
                {
                    CommitEdit();
                    _activeLine++;
                    _focus = Focus.Whitelist;
                    EnsureWhitelistVisible(_activeLine);
                    BindEdit(_lines[_activeLine], InputRules.WhitelistMaxLength, null, float.NaN);
                }
                break;

            case Focus.Blacklist:
                if (_blacklistActiveLine + 1 < _blacklistLines.Count)
                {
                    CommitEdit();
                    _blacklistActiveLine++;
                    _focus = Focus.Blacklist;
                    EnsureBlacklistVisible(_blacklistActiveLine);
                    BindEdit(_blacklistLines[_blacklistActiveLine], InputRules.BlacklistMaxLength, null, float.NaN);
                }
                break;

            case Focus.Port:
                // 端口后面没有更多框了（白名单在它上方）—— 光标留在原处，不做无意义的跳转
                break;

            case Focus.IconMapKey:
                CommitEdit();
                _focus = Focus.IconMapValue;
                BindEdit(InputRules.SplitIconRow(_iconLines[_iconActiveLine]).Value,
                    InputRules.IconValueMaxLength, null, float.NaN);
                break;

            case Focus.IconMapValue:
                CommitEdit();
                if (_iconActiveLine + 1 < _iconLines.Count)
                {
                    _iconActiveLine++;
                    _focus = Focus.IconMapKey;
                    BindEdit(InputRules.SplitIconRow(_iconLines[_iconActiveLine]).Key,
                        InputRules.IconKeyMaxLength, null, float.NaN);
                }
                break;
        }
        _invalidate();
    }

    /// <summary>
    /// Enter 在框里的含义（**不是插入换行** —— 这些框都是单行）。
    /// 白名单：在下方插入一条空框并把焦点移过去；映射页：应用名 → 图标值 → 新建一行。
    /// </summary>
    private void OnEnterInField()
    {
        CommitEdit();

        switch (_focus)
        {
            case Focus.Whitelist:
                _lines.Insert(_activeLine + 1, "");
                _activeLine++;
                EnsureWhitelistVisible(_activeLine);
                BindEdit("", InputRules.WhitelistMaxLength, null, float.NaN);
                _host.Note("已新增一条白名单");
                break;

            case Focus.Blacklist:
                _blacklistLines.Insert(_blacklistActiveLine + 1, "");
                _blacklistActiveLine++;
                _blacklistDirty = true;
                EnsureBlacklistVisible(_blacklistActiveLine);
                BindEdit("", InputRules.BlacklistMaxLength, null, float.NaN);
                _host.Note("已新增一条黑名单");
                break;

            case Focus.Port:
                break;   // 端口只有一个框，回车不做任何事（保持内容不变）

            case Focus.IconMapKey:
                _focus = Focus.IconMapValue;
                BindEdit(InputRules.SplitIconRow(_iconLines[_iconActiveLine]).Value, InputRules.IconValueMaxLength, null, float.NaN);
                break;

            case Focus.IconMapValue:
                _iconLines.Insert(_iconActiveLine + 1, "");
                _iconActiveLine++;
                _focus = Focus.IconMapKey;
                BindEdit("", InputRules.IconKeyMaxLength, null, float.NaN);
                break;
        }
        _invalidate();
    }

    private void NoteCharRejected()
    {
        _host.Note(_focus == Focus.Port
            ? "端口只能填数字（范围由保存时校验并说明原因）"
            : "这个输入框不接受该字符");
        _invalidate();
    }

    private void NoteTooLong()
    {
        string what = _focus switch
        {
            Focus.Port => "端口最多 5 位数字",
            Focus.IconMapKey => $"应用名最多 {InputRules.IconKeyMaxLength} 个字符",
            Focus.IconMapValue => $"图标值最多 {InputRules.IconValueMaxLength} 个字符",
            Focus.Blacklist => $"这一条最多 {InputRules.BlacklistMaxLength} 个字符（应用名或包名都不会这么长，请检查是否粘错了）",
            _ => $"这一条最多 {InputRules.WhitelistMaxLength} 个字符（地址不会这么长，请检查是否粘错了）",
        };
        _host.Note(what);
        _invalidate();
    }

    // ====================================================================
    // 图标映射页的编辑操作
    // ====================================================================

    /// <summary>图标映射页编辑框的可视行数（与绘制用的是同一份几何）。</summary>
    private int VisibleIconLines()
        => ConfigLayout.ComputeIconMap(_layout.ContentWidth).VisibleLines;

    /// <summary>从插件当前配置重新载入图标映射编辑缓冲。</summary>
    private void ReloadIconBuffer()
    {
        ResetEditState();
        _iconLines.Clear();
        foreach (var raw in _host.IconOverridesRaw().Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length > 0) _iconLines.Add(line);
        }
        _iconScroll = 0;
        _iconActiveLine = _iconLines.Count > 0 ? 0 : -1;
        _iconDirty = false;
    }

    /// <summary>
    /// 重建应用黑名单的编辑缓冲。
    ///
    /// 进入黑名单页时调用：与「图标映射」进页前刷新缓冲同样的理由 ——
    /// 配置窗口可能在两处被打开（宿主的设置窗口 / 插件自有窗口），
    /// 不刷新就会显示**别处改过的旧值**，而用户一点保存就把它写回去。
    ///
    /// ★ 注释行（<c>#</c> 开头）原样进缓冲，与图标映射页一致 ★
    /// 保存时落盘文本是**由缓冲生成**的（不走解析器的序列化），所以只要注释留在缓冲里就不会被抹掉；
    /// 反过来，若这里把注释过滤掉，用户手写的注释会在下一次保存后**悄悄消失**。
    /// 拦截判定不受影响 —— <see cref="AppBlacklist.Parse"/> 本身会跳过注释行。
    ///
    /// ⚠️ 必须先 <see cref="ResetEditState"/>：它清掉编辑模型的当前框与文本，
    /// 否则下一次输入会把**旧文本**写回刚载入的新缓冲（等于用陈旧内容覆盖刚保存的结果）。
    /// </summary>
    private void ReloadBlacklistBuffer()
    {
        ResetEditState();
        _blacklistLines.Clear();
        foreach (var raw in _host.AppBlacklistRaw().Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length > 0) _blacklistLines.Add(line);
        }
        if (_blacklistLines.Count == 0) _blacklistLines.Add("");

        _blacklistScroll = 0;
        _blacklistActiveLine = 0;
        _blacklistDirty = false;
    }

    /// <summary>新增一行黑名单并聚焦到它，方便直接开始输入。</summary>
    private void AddBlacklistLine()
    {
        _blacklistLines.Add("");
        _blacklistActiveLine = _blacklistLines.Count - 1;
        _focus = Focus.Blacklist;
        _blacklistDirty = true;

        int visible = VisibleBlacklistLines();
        if (_blacklistActiveLine >= _blacklistScroll + visible)
            _blacklistScroll = _blacklistActiveLine - visible + 1;
    }

    /// <summary>黑名单页当前能显示多少行（只与编辑框高度有关）。</summary>
    private int VisibleBlacklistLines()
        => ConfigLayout.ComputeBlacklist(_layout.ContentWidth).VisibleLines;

    /// <summary>把某一条黑名单滚进可视范围（保存失败时用来把出错的那条露出来）。</summary>
    private void EnsureBlacklistVisible(int index)
    {
        if (index < 0) return;
        int visible = VisibleBlacklistLines();
        if (index < _blacklistScroll) _blacklistScroll = index;
        else if (index >= _blacklistScroll + visible) _blacklistScroll = index - visible + 1;
    }

    /// <summary>新增一行映射并聚焦到它的「应用名」框，方便直接开始输入。</summary>
    private void AddIconLine()
    {
        _iconLines.Add("");
        _iconActiveLine = _iconLines.Count - 1;
        _focus = Focus.IconMapKey;
        _iconDirty = true;

        int visible = VisibleIconLines();
        if (_iconActiveLine >= _iconScroll + visible) _iconScroll = _iconActiveLine - visible + 1;
    }

    // ====================================================================
    // 图标映射的复制 / 粘贴
    //
    // ★ 两条通路 ★
    //   · 鼠标：工具行的「复制 / 粘贴」按钮（可靠，且让功能可被发现）；
    //   · 键盘：Ctrl+C / Ctrl+V。⚠️ 宿主 IPluginWindow.SetKey 只转发 WM_CHAR、
    //     只给 Action<char>，**拿不到修饰键** —— Ctrl+C 到达时就是字符 0x03（ETX）、
    //     Ctrl+V 是 0x16（SYN）。因此这里只能按「控制字符」识别，无法区分
    //     Ctrl+C 与 Ctrl+Shift+C（两者都是 0x03）。这属于宿主接口的硬限制：
    //     界面上有按钮可用，键盘通路只是**便利**，不承担唯一入口的职责。
    //
    // ★ 为什么复制的是「整行原文」★ 见 IconClipboard 的类注释。
    // ====================================================================

    /// <summary>Ctrl+C：把活动行原文写进系统剪贴板。</summary>
    private void CopyActiveIconLine()
    {
        var line = IconClipboard.CopyLine(_iconLines, _iconActiveLine);
        if (line.Length == 0)
        {
            // 空行 / 半成品（没有 =）复制出去毫无意义，明确告知而不是「静默成功」
            _host.Note("请先选中一行有内容的映射再复制（格式：应用名=icon值）");
            FlashIcon("无可复制的内容");
            return;
        }

        if (_host.CopyText(line))
        {
            FlashIcon("已复制 ✓");
            _host.Note($"已复制映射：{Trim(line, 60)}");
        }
        else
        {
            FlashIcon("复制失败");
        }
    }

    /// <summary>
    /// Ctrl+V / 点「粘贴」：把剪贴板内容并入映射缓冲。
    ///
    /// ★ 冲突处理 = 先提示、二次确认（用户选定）★
    /// 第一次点：发现同名键 → 只显示冲突清单与按钮文案变化，**不动缓冲**；
    /// 再点一次（按钮已变成「确认粘贴」）：才真正执行。
    ///
    /// ⚠️ 计划存在 <see cref="_iconPendingPaste"/> 里，确认时执行的是**那份计划**，
    ///   而不是「再看一眼当前剪贴板」。否则用户确认的和他实际执行的可能不是同一份数据。
    /// </summary>
    private void PasteIntoIconLines()
    {
        // —— 已有待确认计划：这次点击就是「确认」——
        if (_iconPendingPaste is { } pending)
        {
            _iconPendingPaste = null;
            ExecutePaste(pending.Plan, pending.ActiveLine);
            return;
        }

        var clipboard = _host.PasteText();
        if (string.IsNullOrEmpty(clipboard))
        {
            _host.Note("剪贴板里没有文本可粘贴");
            FlashIcon("剪贴板为空");
            return;
        }

        var plan = IconClipboard.Plan(clipboard, _iconLines);

        if (!plan.HasAnything)
        {
            // 有内容但一行都解析不出来 —— 最常见的成因是用户复制了一段普通文字。
            // 明确告诉他期望的格式，而不是含糊地说「粘贴失败」。
            _host.Note("剪贴板内容不是有效的映射（每行应为「应用名=icon值」）");
            FlashIcon("格式不正确");
            return;
        }

        if (plan.Invalid.Count > 0)
            _host.Note($"粘贴时跳过 {plan.Invalid.Count} 行无法解析的内容（每行应为「应用名=icon值」）");

        if (plan.Mode == IconClipboard.ConflictMode.NeedsConfirm)
        {
            // 只登记计划、**不改缓冲**；按钮文案与橙色提示会告诉用户「再点一次就继续」
            _iconPendingPaste = (plan, _iconActiveLine);
            FlashIcon($"{plan.Conflicts.Count} 条冲突 · 再点「确认粘贴」继续");
            _host.Note(plan.ConfirmPrompt());
            _host.Note("再点一次「粘贴」（或按 Ctrl+V）即继续；点其它任意编辑动作则取消");
            return;
        }

        ExecutePaste(plan, _iconActiveLine);
    }

    /// <summary>按计划真正改动缓冲（冲突项原地替换、新增项插到活动行下方）。</summary>
    private void ExecutePaste(IconClipboard.PastePlan plan, int activeLine)
    {
        var (lines, overwritten, added) = IconClipboard.Apply(_iconLines, activeLine, plan);

        _iconLines.Clear();
        _iconLines.AddRange(lines);

        // 焦点落到「最后一条新增项」上，方便紧接着继续编辑；
        // 若全是覆盖（没有新增），则保持原来的活动行不动。
        if (added > 0)
        {
            _iconActiveLine = activeLine >= 0 && activeLine < _iconLines.Count
                ? activeLine + added
                : _iconLines.Count - 1;
        }
        else
        {
            _iconActiveLine = Math.Clamp(activeLine, 0, Math.Max(0, _iconLines.Count - 1));
        }

        // 焦点落到「最后一条新增项」的**应用名框**上（粘贴进来的多是完整行，
        // 但用户接着常要改应用名；回车即可递进到图标值框）。
        // 若全是覆盖（没有新增），则保持原来的活动行不动。
        _focus = Focus.IconMapKey;
        _iconDirty = true;
        EnsureIconLineVisible(_iconActiveLine);

        FlashIcon($"已粘贴 {plan.Entries.Count} 条" + (overwritten > 0 ? $"（覆盖 {overwritten}）" : ""));
        _host.Note(plan.DoneMessage() + "；点“保存映射”生效");
    }

    /// <summary>设置图标页的状态提示（空串 = 清除）。</summary>
    private void FlashIcon(string message) => _iconFlash = message;

    /// <summary>把指定行滚进可视范围。</summary>
    private void EnsureIconLineVisible(int index)
    {
        if (index < 0) return;
        int visible = VisibleIconLines();
        if (index < _iconScroll) _iconScroll = index;
        else if (index >= _iconScroll + visible) _iconScroll = index - visible + 1;
    }

    /// <summary>日志用的短文本截断（避免把整串 base64 灌进日志）。</summary>
    private static string Trim(string text, int max)
        => text.Length <= max ? text : text[..max] + "…";

    /// <summary>
    /// 保存映射。
    ///
    /// ★ 保存前逐条校验，有 Error 就**拒绝保存并指出是第几条** ★
    /// 这是「保证输入内容能够正确保存与生效」的关键一步：运行时解析器
    /// （<see cref="IconOverrides.Parse"/>）对写坏的行是**静默丢弃**的 ——
    /// 旧版这里只检查「有没有等号」，于是「只填了应用名」「应用名打成空白」这类情况
    /// 会照样提示保存成功，而那条映射永远不会生效、用户也无从知道。
    /// Warn（如未收录的别名）不拦：那可能正是用户的本意，只如实汇报。
    /// </summary>
    private void SaveIconMapClicked()
    {
        for (int i = 0; i < _iconLines.Count; i++)
        {
            var verdict = InputRules.CheckIconRow(_iconLines[i]);
            if (!verdict.BlocksSave) continue;

            _iconActiveLine = i;
            EnsureIconLineVisible(i);
            _focus = Focus.IconMapKey;
            FlashIcon($"第 {i + 1} 条：{verdict.Message}");
            _host.Note($"保存失败：第 {i + 1} 条 {verdict.Message}");
            _invalidate();
            return;
        }

        // ⚠️ 落盘文本直接由缓冲生成，**不能**走 IconOverrides.Serialize：
        //   它会把「键或值为空」的行丢掉，连注释行（# 开头，键为 "#…"、值为空）也一并丢 ——
        //   那样用户手写的注释会在每次保存后被悄悄抹掉。
        //   走到这里已保证没有 Error 行，剩下的要么是合法的「应用名=图标值」，要么是注释。
        var raw = string.Join("\n", _iconLines.Select(l => l.Trim()).Where(l => l.Length > 0));

        int warns = 0;
        foreach (var line in _iconLines)
            if (InputRules.CheckIconRow(line).Level == InputRules.Level.Warn) warns++;

        _host.SaveIconOverrides(raw);
        ReloadIconBuffer();
        _focus = Focus.None;

        if (warns > 0)
        {
            // 如实说明「存下去了、但可能不会按你想的那样生效」，而不是笼统报「已保存」
            FlashIcon($"已保存，{warns} 条有提醒");
            _host.Note($"图标映射已保存；其中 {warns} 条可能不会生效（选中该条看上方说明）");
        }
        else
        {
            FlashIcon("已保存 ✓");
        }
    }

    /// <summary>
    /// 保存应用黑名单。
    ///
    /// 与主页面白名单的保存同一套纪律：逐条校验，任一条 Error 就**拒绝保存**并指出是第几条 ——
    /// 运行时 <see cref="AppBlacklist.Parse"/> 对认不出的行是**静默丢弃**的，
    /// 放它进去等于「提示保存成功、规则却从未生效」（正是本项目对「用户输入要严格」的一贯要求）。
    ///
    /// 落盘文本直接由缓冲生成（与图标映射页同样的理由）：缓冲里允许出现注释行，
    /// 若走解析器的序列化，用户手写的注释会在每次保存后被悄悄抹掉。
    /// </summary>
    private void SaveBlacklistClicked()
    {
        for (int i = 0; i < _blacklistLines.Count; i++)
        {
            var verdict = InputRules.CheckBlacklistEntry(_blacklistLines[i]);
            if (!verdict.BlocksSave) continue;

            _blacklistActiveLine = i;
            EnsureBlacklistVisible(i);
            _focus = Focus.Blacklist;
            _host.Note($"保存失败：第 {i + 1} 条黑名单 {verdict.Message}");
            _invalidate();
            return;
        }

        var raw = string.Join("\n", _blacklistLines.Select(l => l.Trim()).Where(l => l.Length > 0));

        _host.SaveAppBlacklist(raw);
        ReloadBlacklistBuffer();
        _focus = Focus.None;

        var parsed = AppBlacklist.Parse(raw);
        _host.Note(parsed.IsEmpty
            ? "黑名单已保存（名单为空：不拦截任何应用）"
            : $"黑名单已保存：{parsed.Describe()}");
    }

    /// <summary>
    /// 端口 ± 微调。
    /// 解析与范围都走 <see cref="InputRules"/>，与保存时的校验**同一份规则** ——
    /// 否则会出现「微调能到某个值、保存却说非法」的错位。
    /// </summary>
    private void StepPort(int delta)
    {
        // 当前文本不可用时（空 / 非数字）以配置里真正在用的端口为基准，而不是写死 1403
        int current = InputRules.TryResolvePort(_portText, out int parsed)
            ? parsed
            : _host.Snapshot().Port;

        int next = Math.Clamp(current + delta, InputRules.PortMin, InputRules.PortMax);
        _portText = next.ToString();
        _focus = Focus.Port;
        _dirty = true;
    }

    /// <summary>把最近一次连接的来源 IP 加入白名单 —— 这是「未知设备如何快速授权」的最短路径。</summary>
    private void AddLastSource()
    {
        var address = _host.LastRemoteIp();
        if (string.IsNullOrWhiteSpace(address))
        {
            _host.Note("还没有收到过任何连接，无法加入来源 IP；可先在手机端发一条试试");
            return;
        }
        AddLine(address.Trim());
    }

    private void AddLine(string value)
    {
        if (_lines.Any(l => string.Equals(l.Trim(), value, StringComparison.OrdinalIgnoreCase)))
        {
            _host.Note($"白名单里已经有 {value} 了");
            return;
        }

        if (_lines.Count == 1 && _lines[0].Length == 0) _lines.Clear();
        _lines.Add(value);
        _activeLine = _lines.Count - 1;
        if (_activeLine >= _scroll + _layout.VisibleWhitelistEntries)
            _scroll = _activeLine - _layout.VisibleWhitelistEntries + 1;
        _dirty = true;
        _host.Note($"已加入 {value}（记得点“保存配置”生效）");
    }

    private void SaveClicked()
    {
        // ① 端口：范围与格式都走同一份规则，报错时把原因写清楚并聚焦到该框
        if (!InputRules.TryResolvePort(_portText, out int port))
        {
            _focus = Focus.Port;
            _host.Note($"保存失败：端口「{_portText.Trim()}」{InputRules.CheckPort(_portText).Message}");
            _invalidate();
            return;
        }

        // ② 白名单：逐条校验。有 Error 就**拒绝保存**并指出是第几条 ——
        //    运行时 IpWhitelist.Parse 对认不出的行是静默丢弃的，
        //    放它进去等于「提示保存成功、规则却从未生效」。
        var storable = new List<string>();
        int warns = 0;
        for (int i = 0; i < _lines.Count; i++)
        {
            var verdict = InputRules.CheckWhitelistEntry(_lines[i]);

            if (verdict.BlocksSave)
            {
                _activeLine = i;
                _focus = Focus.Whitelist;
                EnsureWhitelistVisible(i);
                _host.Note($"保存失败：第 {i + 1} 条白名单 {verdict.Message}");
                _invalidate();
                return;
            }

            if (!verdict.IsStorable) continue;      // 空行 / 注释不写进配置
            if (verdict.Level == InputRules.Level.Warn) warns++;
            storable.Add(verdict.Normalized);
        }

        _host.SaveConfig(string.Join("\n", storable), port);
        ReloadCore();

        if (warns > 0)
            _host.Note($"配置已保存（{storable.Count} 条白名单，其中 {warns} 条有提醒，见上方黄字说明）");
        else
            _host.Note($"配置已保存（{storable.Count} 条白名单，端口 {port}）");
    }

    /// <summary>把某一条白名单滚进可视范围（保存失败时用来把出错的那条露出来）。</summary>
    private void EnsureWhitelistVisible(int index)
    {
        if (index < 0) return;
        int visible = _layout.VisibleWhitelistEntries;
        if (index < _scroll) _scroll = index;
        else if (index >= _scroll + visible) _scroll = index - visible + 1;
    }

    private (HitId Id, int Index) HitTest(float x, float y)
    {
        var l = _layout;

        // ★ 图标映射页 / 黑名单页都是整页替换，命中判定必须先走它们 ★
        // 否则会落到主页面那些「位置相同」的矩形上（例如子页的编辑框正好压在日志框的区域）。
        if (_page == PanelPage.IconMap) return HitTestIconMap(x, y);
        if (_page == PanelPage.Blacklist) return HitTestBlacklist(x, y);

        if (ConfigLayout.Contains(ConfigLayout.BlacklistButtonOf(l), x, y)) return (HitId.BlacklistOpen, -1);
        if (ConfigLayout.Contains(ConfigLayout.ChangelogButtonOf(l), x, y)) return (HitId.ChangelogOpen, -1);

        if (ConfigLayout.Contains(l.TutorialButton, x, y)) return (HitId.TutorialOpen, -1);
        if (ConfigLayout.Contains(l.IconMapButton, x, y)) return (HitId.IconMapOpen, -1);
        if (ConfigLayout.Contains(ConfigLayout.TopmostButtonOf(l), x, y)) return (HitId.TopmostToggle, -1);

        if (ConfigLayout.Contains(l.LastSourceButton, x, y)) return (HitId.LastSource, -1);
        if (ConfigLayout.Contains(l.WhitelistAddButton, x, y)) return (HitId.WhitelistAdd, -1);
        if (ConfigLayout.Contains(l.ClearButton, x, y)) return (HitId.Clear, -1);
        if (ConfigLayout.Contains(l.ScrollUpButton, x, y)) return (HitId.ScrollUp, -1);
        if (ConfigLayout.Contains(l.ScrollDownButton, x, y)) return (HitId.ScrollDown, -1);

        if (ConfigLayout.Contains(l.WhitelistBox, x, y))
        {
            // 按「条目间距」折算第几个框 —— 与绘制用的是同一对常量（条目高 + 间距），
            // 两边算法必须一致，否则会出现「点到的框 ≠ 高亮的框」。
            float pitch = ConfigLayout.WhitelistEntryHeight + ConfigLayout.WhitelistEntryGap;
            int indexInBox = (int)Math.Floor((y - (l.WhitelistBox.Top + 6f)) / pitch);
            if (indexInBox >= 0 && indexInBox < l.VisibleWhitelistEntries)
            {
                int absolute = _scroll + indexInBox;
                if (absolute < _lines.Count && ConfigLayout.Contains(l.LineRemoveButton(indexInBox), x, y))
                    return (HitId.LineRemove, absolute);
                return (HitId.WhitelistBox, absolute);
            }
            return (HitId.WhitelistBox, -1);
        }

        if (ConfigLayout.Contains(l.PortMinus10, x, y)) return (HitId.PortMinus10, -1);
        if (ConfigLayout.Contains(l.PortMinus1, x, y)) return (HitId.PortMinus1, -1);
        if (ConfigLayout.Contains(l.CollapseSlider, x, y)) return (HitId.CollapseSlider, -1);
        if (ConfigLayout.Contains(l.CollapseIgnoreRow, x, y)) return (HitId.CollapseIgnore, -1);
        if (ConfigLayout.Contains(l.PortField, x, y)) return (HitId.PortField, -1);
        if (ConfigLayout.Contains(l.PortPlus1, x, y)) return (HitId.PortPlus1, -1);
        if (ConfigLayout.Contains(l.PortPlus10, x, y)) return (HitId.PortPlus10, -1);
        if (ConfigLayout.Contains(l.PhoneCopyButton, x, y)) return (HitId.PhoneCopy, -1);

        if (ConfigLayout.Contains(l.LogCopyButton, x, y)) return (HitId.LogCopy, -1);
        if (ConfigLayout.Contains(l.LogScrollUpButton, x, y)) return (HitId.LogScrollUp, -1);
        if (ConfigLayout.Contains(l.LogScrollDownButton, x, y)) return (HitId.LogScrollDown, -1);

        if (ConfigLayout.Contains(l.SaveButton, x, y)) return (HitId.Save, -1);
        if (ConfigLayout.Contains(l.ServiceButton, x, y)) return (HitId.Service, -1);

        if (ConfigLayout.Contains(l.UpdateCheckButton, x, y)) return (HitId.UpdateCheck, -1);
        if (ConfigLayout.Contains(l.UpdateCopyButton, x, y)) return (HitId.UpdateCopy, -1);
        if (ConfigLayout.Contains(l.UpdateOpenButton, x, y)) return (HitId.UpdateOpen, -1);
        if (ConfigLayout.Contains(l.UpdateModeOffButton, x, y)) return (HitId.UpdateModeOff, -1);
        if (ConfigLayout.Contains(l.UpdateModeAutoButton, x, y)) return (HitId.UpdateModeAuto, -1);
        if (ConfigLayout.Contains(l.UpdateModeClickButton, x, y)) return (HitId.UpdateModeClick, -1);
        if (ConfigLayout.Contains(l.UpdateActionMarketButton, x, y)) return (HitId.UpdateActionMarket, -1);
        if (ConfigLayout.Contains(l.UpdateActionClipboardButton, x, y)) return (HitId.UpdateActionClipboard, -1);
        if (ConfigLayout.Contains(l.UpdateActionDownloadButton, x, y)) return (HitId.UpdateActionDownload, -1);
        if (ConfigLayout.Contains(l.UpgradeToggleOnButton, x, y)) return (HitId.UpgradeToggleOn, -1);
        if (ConfigLayout.Contains(l.UpgradeToggleOffButton, x, y)) return (HitId.UpgradeToggleOff, -1);
        if (ConfigLayout.Contains(l.UpgradeNowButton, x, y)) return (HitId.UpgradeNow, -1);

        if (ConfigLayout.Contains(l.LogBox, x, y)) return (HitId.LogBox, -1);

        return (HitId.None, -1);
    }

    /// <summary>图标映射页的命中判定（与主页面完全独立，见 <see cref="PanelPage"/>）。</summary>
    private (HitId Id, int Index) HitTestIconMap(float x, float y)
    {
        var m = ConfigLayout.ComputeIconMap(_layout.ContentWidth);

        if (ConfigLayout.Contains(m.BackButton, x, y)) return (HitId.IconMapBack, -1);
        if (ConfigLayout.Contains(m.ApplyButton, x, y)) return (HitId.IconMapApply, -1);
        if (ConfigLayout.Contains(m.AddButton, x, y)) return (HitId.IconMapAdd, -1);
        if (ConfigLayout.Contains(m.CopyButton, x, y)) return (HitId.IconMapCopy, -1);
        if (ConfigLayout.Contains(m.PasteButton, x, y)) return (HitId.IconMapPaste, -1);
        if (ConfigLayout.Contains(m.ResetButton, x, y)) return (HitId.IconMapReset, -1);
        if (ConfigLayout.Contains(m.ScrollUpButton, x, y)) return (HitId.IconMapScrollUp, -1);
        if (ConfigLayout.Contains(m.ScrollDownButton, x, y)) return (HitId.IconMapScrollDown, -1);

        if (ConfigLayout.Contains(m.Box, x, y))
        {
            float pitch = IconMapLayout.EntryHeight + IconMapLayout.EntryGap;
            int indexInBox = (int)Math.Floor((y - (m.Box.Top + 6f)) / pitch);
            if (indexInBox >= 0 && indexInBox < m.VisibleLines)
            {
                int absolute = _iconScroll + indexInBox;
                if (absolute < _iconLines.Count)
                {
                    // 顺序有意为之：删除按钮压在行尾、图标值框压在应用名框右侧，
                    // 先判更「靠外」的那个，避免重叠区域被误判成里面的框。
                    if (ConfigLayout.Contains(m.RemoveButtonAt(indexInBox), x, y))
                        return (HitId.IconMapLineRemove, absolute);
                    if (ConfigLayout.Contains(m.ValueFieldAt(indexInBox), x, y))
                        return (HitId.IconMapValueField, absolute);
                    if (ConfigLayout.Contains(m.KeyFieldAt(indexInBox), x, y))
                        return (HitId.IconMapKeyField, absolute);
                }
                return (HitId.IconMapBox, -1);
            }
            return (HitId.IconMapBox, -1);
        }

        return (HitId.None, -1);
    }

    /// <summary>
    /// 应用黑名单页的命中判定（与主页面完全独立，见 <see cref="PanelPage"/>）。
    /// </summary>
    private (HitId Id, int Index) HitTestBlacklist(float x, float y)
    {
        var b = ConfigLayout.ComputeBlacklist(_layout.ContentWidth);

        if (ConfigLayout.Contains(b.BackButton, x, y)) return (HitId.BlacklistBack, -1);
        if (ConfigLayout.Contains(b.ApplyButton, x, y)) return (HitId.BlacklistApply, -1);
        if (ConfigLayout.Contains(b.AddButton, x, y)) return (HitId.BlacklistAdd, -1);
        if (ConfigLayout.Contains(b.ScrollUpButton, x, y)) return (HitId.BlacklistScrollUp, -1);
        if (ConfigLayout.Contains(b.ScrollDownButton, x, y)) return (HitId.BlacklistScrollDown, -1);

        if (ConfigLayout.Contains(b.Box, x, y))
        {
            float pitch = BlacklistLayout.EntryHeight + BlacklistLayout.EntryGap;
            int indexInBox = (int)Math.Floor((y - (b.Box.Top + 6f)) / pitch);
            if (indexInBox >= 0 && indexInBox < b.VisibleLines)
            {
                int absolute = _blacklistScroll + indexInBox;
                if (absolute < _blacklistLines.Count)
                {
                    // 删除按钮压在行尾 —— 先判它，避免重叠区域被误判成里面的输入框
                    if (ConfigLayout.Contains(b.RemoveButtonAt(indexInBox), x, y))
                        return (HitId.BlacklistLineRemove, absolute);
                    return (HitId.BlacklistLine, absolute);
                }
            }
            // 落在框内但没有对应条目（空白区 / 超过最后一条）→ 按「框」处理：
            // 点击时会把焦点放到当前活动行（没有活动行就补一行），与图标映射页同款兜底。
            return (HitId.BlacklistBox, -1);
        }

        return (HitId.None, -1);
    }

    // ====================================================================
    // 仅供验证工程使用的只读访问器（见 tests/BridgeHarness/PanelSmokeTests.cs）
    //
    // ★ 为什么允许这几个成员存在 ★
    // 本次改动的主体（逐条输入框、两个框的映射行、校验拦截）全部落在**绘制与事件处理**里。
    // 没有这些访问器，「点进某个框 → 打字 → 保存」这条链路就只能靠人工开窗口点：
    // 出问题时说不清是哪一步，回归时也没人能验。有了它们，面板可以在验证工程里
    // 被真的实例化、真的绘制、真的投递鼠标键盘，然后断言落盘结果。
    //
    // 约束（保持这三条，这层访问器就不会腐化）：
    //   ① 全部 <c>internal</c> —— 不进交付的公开表面；
    //   ② 全部**只读**，不提供任何「直接改内部状态」的入口 ——
    //      测试必须走真实交互（OnMouseDown / OnChar）才能改变状态，
    //      否则测出来的就不是用户路径；
    //   ③ 不写产品逻辑 —— 只做取值与转述。
    // ====================================================================

    /// <summary>命中结果的<strong>名字</strong>（不暴露私有枚举 <see cref="HitId"/>）。</summary>
    internal string HitTestName(float x, float y) => HitTest(x, y).Id.ToString();

    /// <summary>白名单是否有未保存改动。</summary>
    internal bool HasWhitelistDirty => _dirty;

    /// <summary>白名单条目数（含空条目）。</summary>
    internal int WhitelistLineCount => _lines.Count;

    /// <summary>读某一条白名单的当前内容。</summary>
    internal string WhitelistLineAt(int index)
        => index >= 0 && index < _lines.Count ? _lines[index] : "";

    /// <summary>键盘焦点是否落在白名单的某个输入框上。</summary>
    internal bool IsWhitelistFocused => _focus == Focus.Whitelist;

    /// <summary>图标映射的条目数。</summary>
    internal int IconLineCount => _iconLines.Count;

    /// <summary>读某一条映射的原始行（<c>应用名=图标值</c>）。</summary>
    internal string IconRowAt(int index)
        => index >= 0 && index < _iconLines.Count ? _iconLines[index] : "";

    /// <summary>映射页的可见条数（滚动/命中判定用的是同一份几何）。</summary>
    internal int VisibleIconRows => VisibleIconLines();
}
