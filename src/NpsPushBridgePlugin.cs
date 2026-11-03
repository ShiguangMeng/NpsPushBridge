using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using NotchPeninsula.Plugins;
using SkiaSharp;

namespace NpsPushBridge;

/// <summary>
/// 局域网推送网桥（NpsPushBridge）插件入口。
///
/// 它把「局域网内任意设备（手机 / 平板 / 树莓派 / 另一台电脑）」与「灵动岛」连接起来：
///
///   手机 ──POST──▶ http://本机IP:1403/  ──插件校验白名单──▶ http://127.0.0.1:47300/api/activities/  ──▶ 灵动岛顶部通知
///
/// 关键设计：
///   1. 用裸 <see cref="TcpListener"/> 而非 HttpListener —— 监听非回环地址无需管理员权限 / urlacl；
///   2. 白名单实时生效（服务持有委托而非快照），改配置不必重启服务；只有换端口才需要重启；
///   3. 配置持久化走宿主 <see cref="IPluginHost.GetSetting"/>（键自动加 Plugin.&lt;id&gt;. 前缀），
///      因此插件被禁用/卸载后重新启用仍记得上次的白名单与端口；
///   4. 一切跨线程共享状态都由 <c>_gate</c> 保护：网络线程写、渲染线程每帧读。
/// </summary>
public sealed class NpsPushBridgePlugin : INotchPlugin, IDisposable, IBridgePanelHost, IWidgetStatus, IUpdateBannerHost
{
    // 持久化键（宿主会自动加 "Plugin.<Id>." 前缀隔离）
    private const string KeyWhitelist = "whitelist";
    /// <summary>
    /// 应用黑名单的持久化文本（每行一个应用名或包名）。默认空 = 不拦截任何应用。
    /// 与白名单**分开存**：白名单管「谁能连上本机端口」，黑名单管「谁发的消息不上岛」，
    /// 两者判断的对象与时机都不同，合成一个键会让「只想改一个」变成必须重写另一个。
    /// </summary>
    private const string KeyAppBlacklist = "appBlacklist";
    private const string KeyPort = "port";
    private const string KeyAutoStart = "autostart";

    /// <summary>
    /// 「面板显示时长」（秒，2 位小数）：右键状态灯展开的配置摘要卡片，**鼠标离开后多久自动收起**。
    ///
    /// ⚠️ 它**不是**「提醒弹窗停多久」—— 宿主的通知时长硬编码 4 秒、**不读** <c>ReminderData.Duration</c>
    /// （见 <see cref="CollapseDuration"/> 的说明）。插件能控制的时长只有这一个。
    /// 由配置界面的滑块写入（拖动抬手即落盘），直接对应 <c>IDetailPage.AutoCollapseDelay</c>。
    /// </summary>
    private const string KeyCollapseSeconds = "collapseSeconds";

    /// <summary>
    /// 「忽略外部鼠标操作（不自动收起）」= <c>1</c>；缺省 / 其它值 = 关（**默认关**）。
    /// 勾上后详情页完全不再自动收起（<c>AutoCollapseDelay</c> 返回负数）：
    /// 鼠标离开不收、点到岛外也不收，只能靠岛内右键或插件自己关。
    /// </summary>
    private const string KeyCollapseIgnoreMouse = "collapseIgnoreMouse";

    /// <summary>灵动岛更新提示条的显示方式（<see cref="UpdateBannerMode"/>）。</summary>
    private const string KeyBannerMode = "updateBannerMode";
    /// <summary>点击提示条后执行的动作（<see cref="UpdateClickAction"/>）。</summary>
    private const string KeyClickAction = "updateClickAction";
    /// <summary>
    /// 已被**用户点掉**的远端版本号 —— 只由点击提示条 / 切到「不提示」写入，用来避免用户处理过之后继续骚扰。
    ///
    /// ★ 与 <see cref="KeyToastedVersion"/> 的区别（曾混用同一个字段，是「自动检查不提示」的根因）★
    ///   · 本键 = 用户的决定：他点过提示条了 → 岛上不该再有提示条；
    ///   · <see cref="KeyToastedVersion"/> = 插件自己的记忆：这个版本我已经弹过吐司了 → 别再弹第二次。
    /// 两者需求相反：吐司要「弹过就不再弹」，提示条要「挂着直到用户点掉」。合成一个字段必然有一方坏掉。
    /// </summary>
    private const string KeyDismissedVersion = "updateDismissedVersion";
    /// <summary>已经弹过吐司的远端版本号（插件自身记忆，用户看不到也不该影响提示条）。</summary>
    private const string KeyToastedVersion = "updateToastedVersion";
    /// <summary>旧版遗留键：早期只有一个「提示点击后动作」的布尔式设置，读取时做一次迁移。</summary>
    private const string KeyLegacyUpdateAction = "updateAction";
    /// <summary>「一键静默升级」开关（<see cref="SilentUpgrade.ParseEnabled"/>），默认关。</summary>
    private const string KeySilentUpgrade = "silentUpgrade";
    /// <summary>已经为哪个远端版本尝试过静默升级 —— 同一个版本不重复下载安装。</summary>
    private const string KeyUpgradeAttemptedVersion = "upgradeAttemptedVersion";
    /// <summary>
    /// 用户自定义的「应用 → icon」映射（一行一条 <c>应用名=icon值</c>，见 <see cref="IconOverrides"/>）。
    /// 默认为空 —— 此时行为与升级前完全一致（只用内置映射）。
    /// </summary>
    private const string KeyIconOverrides = "iconOverrides";

    /// <summary>
    /// 配置窗口的**尺寸 / 位置 / 置顶**状态（一行自描述字符串，见 <see cref="WindowState.Serialize"/>）。
    ///
    /// 之所以要持久化：用户把窗口拖到哪、拉多大、要不要置顶，是他**做过的一次选择**——
    /// 下次打开又弹回屏幕中央，等于每次都要重摆一遍。
    /// </summary>
    private const string KeyWindowState = "windowState";

    private const int DefaultPort = 1403;
    // 配置窗口尺寸已统一到 ConfigPanelDefaults（含分屏宽度）—— 见 ConfigPanelDefaults.cs 的说明。

    /// <summary>自动更新检查间隔。</summary>
    private static readonly TimeSpan UpdateCheckInterval = SilentUpgrade.CheckInterval;

    /// <summary>启动后延迟多久做首次检查：避开启动瞬间的渲染高峰，也不与「插件已加载」提示撞车。</summary>
    private static readonly TimeSpan UpdateFirstCheckDelay = SilentUpgrade.FirstCheckDelay;

    /// <summary>
    /// 动态注册提示条组件的最小间隔。
    ///
    /// ★ 这张票不再用于「从无到有」的首次注册 ★（本次 bug 的根因之一）
    /// 原先 <c>ApplyBannerState</c> 里有一道「距上次注册不足本间隔就直接 return」的守卫，
    /// 而吐司没有这道守卫 —— 于是出现「弹窗弹了、提示条没上岛」：判定本来为真，
    /// 注册却被这道节流静默吞掉，两条路径的行为就此分叉。
    /// 现在它只作为**兜底防抖**（防止极端情况下同一瞬间反复注册/注销刷爆宿主重建快照），
    /// 且只在「已有组件在岛上」时才有意义 —— 从无到有的注册一律立刻执行。
    /// </summary>
    private static readonly TimeSpan UpdateBannerRegistrarCooldown = TimeSpan.FromSeconds(30);

    /// <summary>
    /// 收起提示条后补一次「宽度缓存失效」的延迟：要越过宿主当前这一帧，见 <see cref="RetireBanner"/>。
    /// 取一个渲染周期量级（约 2 帧 @60FPS）足够，又不至于让用户察觉岛体宽度收得慢。
    /// </summary>
    private static readonly TimeSpan BannerReinvalidateDelay = TimeSpan.FromMilliseconds(32);

    private readonly object _gate = new();
    private readonly BridgeLog _log = new();

    private IPluginHost? _host;

    private string _whitelistRaw = "";
    private IpWhitelist _whitelist = IpWhitelist.Parse(null);
    private string _appBlacklistRaw = "";
    private AppBlacklist _appBlacklist = AppBlacklist.Parse(null);
    private int _port = DefaultPort;

    /// <summary>
    /// 「面板显示时长」（秒）：详情页在鼠标离开后**多久自动收起**。由配置界面的滑块写入，
    /// 经 <see cref="IWidgetStatus.DetailAutoCollapseDelay"/> 交给宿主的 <c>AutoCollapseDelay</c>。
    /// 规范化（0.5~60、2 位小数）统一由 <see cref="CollapseDuration"/> 负责，这里只存当前值。
    /// </summary>
    private float _collapseSeconds = CollapseDuration.DefaultSeconds;

    /// <summary>
    /// 是否「忽略外部鼠标操作（不自动收起）」。**默认 false** = 宿主的默认行为（点岛外即收起）；
    /// 勾上后详情页不再自动收起，只能靠岛内右键或 <c>CloseDetailPage</c> 关掉。
    /// </summary>
    private bool _collapseIgnoreMouse;

    /// <summary>
    /// 提醒（吐司）的时长。
    ///
    /// ⚠️ **这个值实际上不起作用** —— 宿主 <c>NotchWindow.OnPluginReminder</c> 把结束时刻硬编码成
    /// <c>DateTime.Now.AddSeconds(4)</c>，而 <c>ToastData</c> 里**根本没有时长字段**、
    /// <c>PluginHost.PostReminder</c> 构造时也没传（<c>ReminderData.Duration</c> 被整条丢掉）。
    ///
    /// 仍然如实填 4 秒有两个理由：① 与宿主的**真实行为**一致，免得读代码的人误以为可调；
    /// ② 宿主将来若支持该字段，这里能自动接上。
    /// **插件真正能控制的时长是详情页的自动收起延迟**，见 <see cref=CollapseDuration/>。
    /// </summary>
    private static readonly TimeSpan ReminderToastDuration = TimeSpan.FromSeconds(4);
    private HttpBridgeServer? _server;

    /// <summary>
    /// 自定义「应用 → icon」映射。与白名单一样，服务持有的是**委托**而非快照，
    /// 因此用户在配置界面加一条映射后，下一条消息立刻生效，不需要重启服务。
    /// </summary>
    private IconOverrides _iconOverrides = IconOverrides.Parse(null);

    // 服务停止后保留最后一组计数，界面仍能显示累计值
    private long _idleReceived, _idleForwarded, _idleRejected, _idleFailed;

    private ConfigPanel? _panel;
    private StatusWidget? _widget;
    /// <summary>
    /// 配置窗口。**由插件自己创建**（<see cref="PanelWindow"/>），不是宿主的窗口 ——
    /// 原因见 <see cref="PanelWindow"/> 的类注释：宿主窗口每次重绘都会把位置与尺寸写回创建时的值，
    /// 因此它**无法移动、无法缩放**，且拿不到方向键与双击。
    /// </summary>
    private PanelWindow? _configWindow;
    private string? _lastError;

    /// <summary>
    /// 面板心跳定时器（光标闪烁 + 方向键轮询，见 <see cref="StartPanelHeartbeat"/>）。
    /// 整个生命周期只建一个：反复开关配置窗口不该反复创建定时器。
    /// ⚠️ 必须写全名 —— 本文件同时 using 了 System.Windows.Forms，`Timer` 会二义（CS0104）。
    /// </summary>
    private System.Threading.Timer? _panelTimer;


    // ── 自动更新 ──
    private IDisposable? _updateTimer;
    private UpdateCheckResult _updateResult;
    /// <summary>灵动岛提示条显示方式。</summary>
    private UpdateBannerMode _bannerMode = UpdateBannerMode.Clickable;
    /// <summary>点击提示条后的动作。</summary>
    private UpdateClickAction _clickAction = UpdateClickAction.Market;
    /// <summary>当前已注册的提示条组件；为 null 表示岛上没有提示条。</summary>
    private UpdateBannerWidget? _banner;
    /// <summary>已提示过 / 已被点掉的远端版本号（持久化，热重载后仍记得）。</summary>
    private string _dismissedVersion = "";
    /// <summary>已经弹过吐司的远端版本号（插件自身记忆，只用于「同一版本不反复弹吐司」）。</summary>
    private string _toastedVersion = "";
    /// <summary>防重入：检查进行中时忽略新的检查请求。</summary>
    private int _checkingFlag;
    /// <summary>防重入：注册提示条组件进行中。</summary>
    private int _bannerRegistering;
    /// <summary>上次注册提示条的时刻，用于冷却（避免极端情况下反复注册/注销）。</summary>
    private DateTime _bannerRegisteredAt = DateTime.MinValue;

    // ── 一键静默升级 ──
    /// <summary>用户是否开启了「一键静默升级」（持久化，默认关）。</summary>
    private bool _silentUpgradeEnabled;
    /// <summary>静默升级的状态机位置。</summary>
    private UpgradeStage _upgradeStage = UpgradeStage.Idle;
    /// <summary>升级过程中的字节进度（下载阶段用）。</summary>
    private long _upgradeReceived, _upgradeTotal = -1;
    /// <summary>本轮连续失败次数（成功后清零）。</summary>
    private int _upgradeFailures;
    /// <summary>最近一次失败的简短原因（配置界面显示）。</summary>
    private string _upgradeError = "";
    /// <summary>已经为哪个远端版本尝试过安装 —— 持久化，避免同一版本反复下载。</summary>
    private string _upgradeAttemptedVersion = "";
    /// <summary>防重入：升级流程进行中标记。</summary>
    private int _upgradingFlag;
    /// <summary>待执行的重试；换版本 / 关开关 / 成功后作废。</summary>
    private CancellationTokenSource? _retryCts;

    // ====================================================================
    // INotchPlugin
    // ====================================================================

    public string Id => "com.nps.pushbridge";
    public string DisplayName => "局域网推送网桥";
    /// <summary>本地版本号：唯一真源是 csproj 的 &lt;Version&gt;（见 <see cref="PluginUpdate.LocalVersion"/>）。</summary>
    public string Version => PluginUpdate.LocalVersion;

    /// <summary>
    /// 作者名。显示在宿主「插件中心」的列表里（`运行中 · v1.4.0 · 拾光梦`）。
    ///
    /// ★ 为什么「属性」与「程序集元数据」两处都填 ★
    /// 新版插件契约给 <c>INotchPlugin</c> 增加了 <c>Author</c> 成员，并规定：
    /// ① 该成员**带默认实现（返回空串）、可以不写**（写成必填会让所有已编译好的老插件直接加载失败）；
    /// ② 不写（或为空）时，宿主**回退读程序集的 <c>Company</c> / <c>Authors</c> 元数据**
    /// （即 csproj 的 &lt;Authors&gt; / &lt;Company&gt;）；③ 再取不到才用插件标识兜底。
    /// 因此这里与 csproj 元数据**两处同时填** —— 无论宿主升级到哪一版、走哪条回退路径，都能取到同一个作者名。
    /// ⚠️ 当前宿主源码的 <c>INotchPlugin</c> 尚未包含该成员，所以它此刻只是一个多余的公共属性，
    /// **不会影响编译**；等宿主升级后它会自动隐式实现接口成员（C# 公共成员即满足接口实现）。
    /// </summary>
    public string Author => "拾光梦";

    public void Initialize(IPluginHost host)
    {
        _host = host;
        LoadSettings();

        // ★ 第一步就把宿主缺失的官方图标补进 data/image（详见 HostImageSeeder）★
        // 必须放在**任何图标判定之前**：IconCatalog 的别名探测会缓存「目录里有哪些文件」，
        // 若先探测后释放，本次进程内所有别名都会被判为「失效」而白白降级到网络链接。
        SeedHostImages(host);

        // 注册灵动岛组件（渲染接线已完成，真的会显示）
        _widget = new StatusWidget(this);
        host.RegisterWidget(_widget);

        // 注册设置页（宿主尚未接线渲染，接口先按契约写好；兜底窗口见 OpenConfig）
        _panel = new ConfigPanel(this, InvalidateSurfaces);
        host.RegisterSettingsPage(new PushBridgeSettingsPage(_panel));

        // 面板心跳：光标闪烁 + （必要时）方向键轮询。早先挂在「打开窗口」时创建，
        // 现在窗口可能反复开关，故改成插件加载时建一次、活到 Dispose。
        StartPanelHeartbeat();

        host.SettingsChanged += OnSettingsChanged;

        // 卸载清理（反射注册：该接口成员是宿主 2.0.1 才加的，见方法说明）
        TryRegisterUninstallCleanup(host);

        _log.Add($"插件已加载：{DisplayName} v{Version}");
        foreach (var address in DetectLocalIPv4()) _log.Add($"本机局域网地址：{address}:{_port}");

        if (host.GetSetting(KeyAutoStart, "1") != "0") StartService();
        else _log.Add("自动开启已关闭（设置项 autostart=0）");

        host.PostReminder(new ReminderData
        {
            Title = "推送网桥已加载",
            Body = $"灵动岛出现一盏状态灯（监听 {_port}）：左键开关服务，右键打开配置",
            // 时长由宿主决定，改这里没用（见 ReminderToastDuration 的说明）
            Duration = ReminderToastDuration,
        });

        StartUpdateChecks(host);
    }

    /// <summary>卸载 / 热重载时释放：停服务、停定时器、关窗口、摘事件订阅。</summary>
    public void Dispose()
    {
        try { _host?.SettingsChanged -= OnSettingsChanged; } catch { /* 宿主可能已摘除 */ }

        try { _updateTimer?.Dispose(); } catch { /* 定时器可能已释放 */ }
        _updateTimer = null;

        // 面板心跳：插件都要走了，停掉它（否则它还会拿着已失效的面板继续跑）
        try { _panelTimer?.Dispose(); } catch { /* 已释放 */ }
        _panelTimer = null;

        // 取消排队中的升级重试，并让正在跑的下载尽快停下 —— 进程都要走了，写文件毫无意义
        try { _retryCts?.Cancel(); } catch { /* 已取消 */ }
        try { _retryCts?.Dispose(); } catch { /* 同上 */ }
        _retryCts = null;

        // 提示条组件随插件一起消失；把它标记为作废，避免残留引用继续被绘制
        try { _banner?.Retire(); } catch { /* 组件可能已被回收 */ }
        lock (_gate) _banner = null;

        StopServiceCore(persistAutoStart: false);

        // 关窗时一并持久化尺寸/位置/置顶，再释放窗口（见 CloseConfigWindow）
        CloseConfigWindow();

        // ★ 每次关闭都清掉升级残留的临时文件 ★
        // 这些是「下载 → 解压 → 替换」过程中的中间产物（解压出来的 dll 与宿主目录里那份重复），
        // 流程走完即无用。放在这里而非只在卸载时清，是因为「关一次」就该少一点垃圾；
        // ⚠️ 这里**只动临时文件**，绝不碰注册表与升级备份 —— 重载也会走到这里，
        //    在那个时机删配置或删回滚备份都是不可接受的（详见 ResidueCleaner 的说明）。
        try { ResidueCleaner.CleanTemp(_log.Add); } catch { /* 清理失败不影响卸载 */ }

        _log.Add("插件已卸载");
        _widget = null;
        _panel = null;
        _host = null;
        lock (_gate) _server = null;
    }

    /// <summary>
    /// 注册「卸载前清理」。
    ///
    /// ★ 为什么用反射而不是直接调 ★
    /// <c>RegisterUninstallAction</c> 是宿主 2.0.1 才加的接口成员。旧宿主上直接调用会在
    /// 接口分派时抛 <c>MissingMethodException</c>，而它发生在 <see cref="Initialize"/> 里 ——
    /// 一次「清理能力缺失」会升级成「整个插件加载失败」。反射拿不到方法就静默跳过，
    /// 插件其余功能完全不受影响（这是本项目对宿主新能力的统一处理方式）。
    ///
    /// ★ 它只在「真正卸载」时执行 ★
    /// 宿主 <c>PluginManager.Remove</c> 会先跑它、再 <c>Unload</c>；禁用 / 重载 / 退出宿主都不走这条路。
    /// 这个边界是对的：重载时把用户配置删掉，谁都受不了。
    /// </summary>
    private void TryRegisterUninstallCleanup(IPluginHost host)
    {
        try
        {
            var method = host.GetType().GetMethod(
                "RegisterUninstallAction",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);

            if (method == null)
            {
                _log.Add("宿主未提供卸载清理能力，已跳过（不影响使用）");
                return;
            }

            method.Invoke(host, new object[] { new Action(OnUninstallCleanup) });
            _log.Add("已登记卸载清理：注册表设置 / 升级备份 / 临时文件");
        }
        catch (Exception ex)
        {
            // 反射失败（旧宿主 / 被裁剪 / 目标抛异常）都只是少一次清理
            _log.Add($"登记卸载清理失败（不影响功能）：{ex.GetType().Name} {ex.Message}");
        }
    }

    /// <summary>
    /// 卸载前真正执行的清理：把宿主看不见的三处残留一次清掉。
    ///
    /// 它在宿主的 UI 线程上**同步**执行，所以只做删文件与删注册表值这类毫秒级操作；
    /// 任何一步失败都只记日志 —— 清理不能把卸载卡住，否则用户看到的是「点了没反应」。
    /// </summary>
    private void OnUninstallCleanup()
    {
        try
        {
            int settings = ResidueCleaner.CleanSettings(_log.Add);
            int backups = ResidueCleaner.CleanUpgradeBackups(PluginDllDirectory(), _log.Add);
            int temp = ResidueCleaner.CleanTemp(_log.Add);

            _log.Add($"卸载清理完成：设置 {settings} 项 / 升级备份 {backups} 个 / 临时条目 {temp} 个");
        }
        catch (Exception ex)
        {
            _log.Add($"卸载清理出错（不影响卸载）：{ex.GetType().Name} {ex.Message}");
        }
    }

    /// <summary>
    /// 本插件 dll 所在目录 —— 升级备份（<c>NpsPushBridge.dll.bak-*</c>）就在它旁边。
    /// 单文件发布下 <c>Assembly.Location</c> 可能为空，退回 <see cref="AppContext.BaseDirectory"/>。
    /// </summary>
    private static string PluginDllDirectory()
    {
        try
        {
            var location = typeof(NpsPushBridgePlugin).Assembly.Location;
            if (!string.IsNullOrEmpty(location))
            {
                var dir = System.IO.Path.GetDirectoryName(location);
                if (!string.IsNullOrEmpty(dir)) return dir;
            }
        }
        catch { /* 取不到就退回基目录 */ }

        return AppContext.BaseDirectory;
    }

    /// <summary>
    /// 把宿主缺失的官方图标补进 <c>data/image/</c>（单文件发布版宿主不带这些散文件）。
    ///
    /// ★ 为什么要在这里做而不是让用户手动放图 ★
    /// 单文件发布版宿主**永远**不会自带这些图，而宿主的所有图标路径都以该目录为根 ——
    /// 缺了它，用户看到的就是「QQ 图标不显示 / 媒体封面没有」，但界面上没有任何提示，
    /// 宿主日志里也只有一串 <c>DirectoryNotFoundException</c>，普通用户无从排查。
    /// 插件手里正好有这些官方图（内嵌资源），释放一次即可根治。
    ///
    /// ★ 失败绝不影响插件启动 ★
    /// 目标目录可能是只读的（如装在 Program Files）。此时只记日志，
    /// 图标改走「网络链接」兜底（<see cref="IconCatalog.Decide"/> 会自动降级），
    /// 功能可用、只是不如本地图稳 —— 绝不能因为补不上图就让插件起不来。
    /// </summary>
    private void SeedHostImages(IPluginHost host)
    {
        try
        {
            var result = HostImageSeeder.Seed(_log.Add);

            // ⚠️ 释放完必须清掉别名探测缓存：探测结果是按目录缓存的，
            //   若在释放前已有任何代码读过（例如上一次热重载残留、或未来的启动顺序调整），
            //   缓存里就是「目录为空」 → 别名会持续被判为失效，直到进程重启。
            //   清一次的成本可以忽略（下次探测会重新读目录），但漏掉的代价是「补了图也不生效」。
            IconCatalog.ResetAliasProbeCache();

            if (result.ChangedAnything)
            {
                // 宿主图标是**进程内一次性加载**的（Renderer._iconsLoaded 是静态字段），
                // 所以本次进程里刚写进去的图**不会**被采用 —— 必须重启宿主。
                // 这里用提醒明确告知，避免用户以为「补了图却没效果」。
                host.PostReminder(new ReminderData
                {
                    Title = "已补全宿主图标文件",
                    Body = "宿主缺少 data/image 图标（单文件发布版常见）。已写入 6 张官方图标，重启灵动岛后 QQ 等图标将直接可用。",
                    Duration = ReminderToastDuration,
                });
            }
        }
        catch (Exception ex)
        {
            // 兜底再兜一层：Seed 内部已经吞掉绝大多数异常，这里只防「日志本身抛异常」之类的极端情况
            try { _log.Add($"宿主图标释放异常：{ex.GetType().Name}: {ex.Message}"); } catch { /* 日志都写不了就只能放弃 */ }
        }
    }

    // ====================================================================
    // 配置持久化
    // ====================================================================

    private void LoadSettings()
    {
        var host = _host;
        if (host == null) return;

        string raw = host.GetSetting(KeyWhitelist, "");
        string blacklistRaw = host.GetSetting(KeyAppBlacklist, "");
        int port = DefaultPort;
        var portText = host.GetSetting(KeyPort, DefaultPort.ToString());
        if (int.TryParse(portText, out int parsed) && parsed is >= 1 and <= 65535) port = parsed;

        // ── 更新相关设置 ──
        // 旧版只有一个布尔式的 "updateAction"（0 = 打开浏览器 / 1 = 复制链接）。它的语义与新的
        // UpdateClickAction 前两个取值完全一致，所以「换个键读」即可完成迁移，数值无需改写；
        // 提示模式则统一落到 Clickable，保持用户升级前后「点提示条有反应」的既有体验。
        var legacy = host.GetSetting(KeyLegacyUpdateAction, "");
        _bannerMode = UpdateNotice.ParseMode(host.GetSetting(KeyBannerMode, ""), legacy);
        _clickAction = UpdateNotice.ParseClickAction(
            host.GetSetting(KeyClickAction, string.IsNullOrWhiteSpace(legacy) ? "" : legacy));
        _dismissedVersion = host.GetSetting(KeyDismissedVersion, "");
        _toastedVersion = host.GetSetting(KeyToastedVersion, "");

        // ── 静默升级设置 ──
        // 默认关（见 SilentUpgrade.DefaultEnabled 的说明）：用户不动它，行为与升级前完全一致。
        _silentUpgradeEnabled = SilentUpgrade.ParseEnabled(host.GetSetting(KeySilentUpgrade, ""));

        // 面板显示时长：**宽容解析** —— 空 / 坏值 / 越界分别退回默认值或夹进 [0.5,60]，
        // 用户手改配置写坏了不该让插件起不来（见 CollapseDuration.Parse）。
        _collapseSeconds = CollapseDuration.Parse(host.GetSetting(KeyCollapseSeconds, ""));
        _collapseIgnoreMouse = CollapseDuration.ParseIgnoreMouse(host.GetSetting(KeyCollapseIgnoreMouse, "0"));
        _upgradeAttemptedVersion = host.GetSetting(KeyUpgradeAttemptedVersion, "");

        // ── 自定义图标映射 ──
        // 默认空 → 只用内置映射，行为与升级前一致（那时根本没有 icon 概念）。
        var iconRaw = host.GetSetting(KeyIconOverrides, "");

        lock (_gate)
        {
            _whitelistRaw = raw;
            _whitelist = IpWhitelist.Parse(raw);
            _appBlacklistRaw = blacklistRaw;
            _appBlacklist = AppBlacklist.Parse(blacklistRaw);
            _port = port;
            _iconOverrides = IconOverrides.Parse(iconRaw);
        }
    }

    private void OnSettingsChanged()
    {
        LoadSettings();
        _log.Add($"设置已变更：端口 {_port}，白名单 {_whitelist.Describe()}，黑名单 {_appBlacklist.Describe()}");
        InvalidateSurfaces();
    }

    // ====================================================================
    // 服务开关
    // ====================================================================

    private bool StartService()
    {
        var host = _host;

        lock (_gate)
        {
            if (_server is { IsRunning: true }) return true;

            // 白名单、应用黑名单与图标映射都用委托实时读取：改配置无需重启；只有换端口才需要重启
            var server = new HttpBridgeServer(_port, () => { lock (_gate) return _whitelist; },
                () => { lock (_gate) return _appBlacklist; },
                () => { lock (_gate) return _iconOverrides; },
                LogFromServer, _ => { }, OnVerificationCode);
            if (!server.Start(out var error))
            {
                _lastError = error;
                _log.Add($"服务启动失败：{error}");
                server.Dispose();
                return false;
            }

            _server = server;
            _lastError = null;
        }

        host?.SetSetting(KeyAutoStart, "1");
        InvalidateSurfaces();
        return true;
    }

    private void StopServiceCore(bool persistAutoStart)
    {
        HttpBridgeServer? server;
        lock (_gate)
        {
            server = _server;
            if (server != null)
            {
                _idleReceived = server.Received;
                _idleForwarded = server.Forwarded;
                _idleRejected = server.Rejected;
                _idleFailed = server.Failed;
                _server = null;
            }
        }

        // 停监听放在锁外，避免与网络回调互相等待
        server?.Dispose();

        if (persistAutoStart) _host?.SetSetting(KeyAutoStart, "0");
        InvalidateSurfaces();
    }

    public void ToggleService()
    {
        bool running;
        lock (_gate) running = _server is { IsRunning: true };

        if (running) StopServiceCore(persistAutoStart: true);
        else StartService();
    }

    // ====================================================================
    // IBridgePanelHost
    // ====================================================================

    /// <summary>
    /// 配置窗口当前是否置顶。**没有窗口时恒为 false** ——
    /// 宿主设置页里渲染同一份面板时也会读它，那时按钮应显示「未置顶」而不是崩溃。
    /// </summary>
    bool IBridgePanelHost.Topmost => _configWindow?.Topmost ?? false;

    /// <summary>
    /// 切换配置窗口置顶。
    ///
    /// 真正的动作在 <see cref="PanelWindow.SetTopmost"/>（<c>SetWindowPos</c> 到
    /// <c>HWND_TOPMOST</c> / <c>HWND_NOTOPMOST</c>）；这里只负责转达并刷新界面，
    /// **面板不自己记置顶状态**（否则按钮显示的状态可能与窗口实际行为不一致）。
    /// </summary>
    void IBridgePanelHost.SetTopmost(bool value)
    {
        try { _configWindow?.SetTopmost(value); } catch { /* 置顶失败不影响功能 */ }
        try { _configWindow?.RequestRedraw(); } catch { /* 窗口可能已关闭 */ }
    }

    ConfigSnapshot IBridgePanelHost.Snapshot()
    {
        lock (_gate)
        {
            var (received, forwarded, rejected, failed) = CountersLocked();
            return new ConfigSnapshot(
                _whitelistRaw,
                _port,
                _server is { IsRunning: true },
                _whitelist.Describe(),
                received,
                forwarded,
                rejected,
                failed,
                _log.Snapshot(),
                _lastError,
                _collapseSeconds,
                _collapseIgnoreMouse);
        }
    }

    /// <summary>
    /// 写「面板显示时长」。面板拖动滑块**抬手时**调用（值已规范化到 0.5~60、2 位小数）。
    /// 落盘后立刻对宿主的 <c>AutoCollapseDelay</c> 生效 —— 不需要重启或重载插件。
    /// </summary>
    void IBridgePanelHost.SetCollapseSeconds(float seconds)
    {
        var value = CollapseDuration.Normalize(seconds);
        lock (_gate)
        {
            _collapseSeconds = value;
            _host?.SetSetting(KeyCollapseSeconds, CollapseDuration.Serialize(value));
        }
        _log.Add($"面板显示时长已设为 {CollapseDuration.Format(value)}");
    }

    /// <summary>
    /// 写「忽略外部鼠标操作（不自动收起）」。面板点开关时立即调用，落盘即生效。
    /// </summary>
    void IBridgePanelHost.SetCollapseIgnoreMouse(bool ignore)
    {
        lock (_gate)
        {
            _collapseIgnoreMouse = ignore;
            _host?.SetSetting(KeyCollapseIgnoreMouse, CollapseDuration.SerializeIgnoreMouse(ignore));
        }
        _log.Add(ignore
            ? "已开启「忽略外部鼠标操作」：详情页不再自动收起（靠岛内右键或面板里的关闭入口关掉）"
            : "已关闭「忽略外部鼠标操作」：恢复宿主默认的自动收起行为");
    }

    /// <summary>
    /// 详情页的自动收起延迟。宿主在鼠标离开时可能每帧读一次，所以这里只做一次纯换算、
    /// 不加锁不做 IO（换算规则见 <see cref="CollapseDuration.ToAutoCollapseDelay"/>）。
    /// 勾了「忽略外部鼠标操作」时返回负数 = 宿主**完全不自动收起**。
    /// </summary>
    TimeSpan IWidgetStatus.DetailAutoCollapseDelay
        => CollapseDuration.ToAutoCollapseDelay(_collapseSeconds, _collapseIgnoreMouse);

    public void SaveConfig(string whitelistRaw, int port)
    {
        var host = _host;
        if (host == null) return;

        int previousPort;
        bool running;
        lock (_gate)
        {
            running = _server is { IsRunning: true };
            previousPort = _server?.Port ?? _port;

            _whitelistRaw = whitelistRaw;
            _whitelist = IpWhitelist.Parse(whitelistRaw);
            _port = port;
        }

        host.SetSetting(KeyWhitelist, whitelistRaw);
        host.SetSetting(KeyPort, port.ToString());
        _log.Add($"配置已保存：端口 {port}，白名单 {_whitelist.Describe()}");

        // 白名单实时生效；端口变化需要重启监听
        if (running && previousPort != port)
        {
            _log.Add($"端口由 {previousPort} 改为 {port}，正在重启服务");
            StopServiceCore(persistAutoStart: false);
            StartService();
        }

        InvalidateSurfaces();
    }

    public string? LastRemoteIp()
    {
        lock (_gate)
        {
            var ip = _server?.LastRemoteIp;
            return string.IsNullOrEmpty(ip) ? null : ip;
        }
    }

    /// <summary>上一次枚举到的本机 IPv4（见 <see cref="DetectLocalIPv4"/> 的缓存说明）。</summary>
    private string[] _localIpCache = Array.Empty<string>();
    /// <summary>上一次枚举的时刻（<see cref="Environment.TickCount64"/>）。</summary>
    private long _localIpAt;
    /// <summary>网卡枚举的节流间隔（毫秒）。与配置面板的刷新节奏一致（见 <c>ConfigPanel.LocalIpsRefreshMs</c>）。</summary>
    private const int LocalIpCacheMs = 1000;

    /// <summary>
    /// 本机可用的局域网 IPv4 地址，**第一个就是最可能「正在上网」的那张网卡**。
    ///
    /// ★ 排序依据（这是「供手机端填写」这个用途的关键）★    /// 一台机器常有多个 IPv4：真网卡（以太网 / Wi-Fi）、虚拟网卡（VMware / Hyper-V / VPN）、
    /// 甚至 Docker 的网桥。它们都是「私有网段」，只看地址分不出该填哪个 ——
    /// 但**手机上要填的是能通到这台机器的那个地址**，填错的表现是「手机点了没反应」，
    /// 与网络不通完全一样。所以这里用一条硬规则排序：
    ///   0. 有默认网关 且 私有网段   ← 系统真的会把流量交给它 ⇒ 就是它
    ///   1. 有默认网关 且 非私有     （少见，但比没有网关的更像「在用」的）
    ///   2. 无默认网关 且 私有       （虚拟网卡 / 未接线的网卡常见在这里）
    ///   3. 其余
    /// ⚠️ 这是**排序**而不是过滤：全部地址仍然保留（状态页与日志可能用得上），
    ///    只是把最可能可用的排到最前面，面板取 <c>[0]</c> 拼手机端地址。
    ///
    /// ★ 为什么带缓存 ★
    /// 配置面板每帧重绘、并按 1 秒的节奏请求它；而枚举网卡要遍历
    /// <c>NetworkInterface</c> 并读取每张网卡的属性（几十微秒~毫秒级，可能触发系统调用）。
    /// 所以这里做 1 秒节流：**返回同一个数组实例**，过期才重新枚举
    /// （换 Wi-Fi / 插网线后最多 1 秒就在界面上更新）。
    /// </summary>
    public string[] DetectLocalIPv4()
    {
        long now = Environment.TickCount64;
        if (now - _localIpAt < LocalIpCacheMs) return _localIpCache;

        var found = new List<(string Ip, int Rank)>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

                var props = nic.GetIPProperties();

                // 「有默认网关」= 系统会把出网流量交给它 —— 这才叫「能上网的网卡」。
                bool hasGateway = props.GatewayAddresses.Any(g =>
                    g.Address is not null
                    && g.Address.AddressFamily == AddressFamily.InterNetwork
                    && !g.Address.Equals(IPAddress.Any));

                foreach (var unicast in props.UnicastAddresses)
                {
                    var address = unicast.Address;
                    if (address.AddressFamily != AddressFamily.InterNetwork) continue;
                    if (IPAddress.IsLoopback(address)) continue;
                    var text = address.ToString();
                    if (text.StartsWith("169.254.", StringComparison.Ordinal)) continue;  // APIPA 自动私有地址

                    bool priv = IsPrivate(text);
                    found.Add((text, hasGateway ? (priv ? 0 : 1) : (priv ? 2 : 3)));
                }
            }
        }
        catch (Exception ex)
        {
            _log.Add($"枚举本机网卡失败：{ex.Message}");
        }

        _localIpCache = found
            .GroupBy(f => f.Ip, StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Min(f => f.Rank))
            .Select(g => g.Key)
            .ToArray();
        _localIpAt = now;
        return _localIpCache;
    }

    public void Note(string message) => _log.AddUpdate(message);

    /// <summary>
    /// 把日志文本写入系统剪贴板（实现在 <see cref="TrySetClipboard"/>，更新链接复制共用同一份逻辑）。
    /// </summary>
    public bool CopyText(string text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        if (TrySetClipboard(text)) return true;

        _log.Add("写入剪贴板失败：剪贴板被其它程序占用，请稍后再试");
        return false;
    }

    /// <summary>
    /// 读出系统剪贴板里的文本（图标映射粘贴用）。
    ///
    /// 与 <see cref="CopyText"/> 一样的两个现实问题，处理方式保持一致：
    ///   1. <c>Clipboard.GetText</c> 同样**要求 STA** → 自己起 STA 工作线程；
    ///   2. 剪贴板可能被别的进程独占（<c>OpenClipboard</c> 失败）→ 带间隔重试几次。
    ///
    /// ★ 为什么用 <c>ContainsText</c> 先探一下 ★
    /// 剪贴板里可能是图片、文件列表等非文本内容，此时 <c>GetText</c> 会抛异常。
    /// 先探类型既能避免异常开销，也让「剪贴板里没有文本」这个正常情况走一条干净的路径。
    /// 返回空串表示「没有可用文本」，调用方据此给出提示。
    /// </summary>
    public string PasteText()
    {
        string result = "";

        var worker = new Thread(() =>
        {
            // 最多重试 5 次（与写入侧同样的理由：剪贴板随时可能被别的进程占着）
            for (int attempt = 0; attempt < 5; attempt++)
            {
                try
                {
                    if (!System.Windows.Forms.Clipboard.ContainsText())
                    {
                        result = "";       // 明确「没有文本」→ 不必再重试
                        return;
                    }
                    result = System.Windows.Forms.Clipboard.GetText() ?? "";
                    return;
                }
                catch
                {
                    Thread.Sleep(60);
                }
            }
        })
        {
            IsBackground = true,
            Name = "NpsPushBridge.ClipboardRead",
        };

        try
        {
            worker.SetApartmentState(ApartmentState.STA);
            worker.Start();
            worker.Join(TimeSpan.FromSeconds(3));
        }
        catch
        {
            return "";
        }

        return result;
    }

    /// <summary>
    /// 写入系统剪贴板（带重试），日志复制与更新链接复制共用这一份实现。
    ///
    /// 两个必须处理的现实问题：
    ///   1. <c>Clipboard.SetText</c> 要求调用线程是 **STA**，而更新检查的延后复制跑在线程池（MTA）上；
    ///      所以这里**自己起一个 STA 工作线程**去写，写完整合回来 —— 这样任何线程调用都安全，
    ///      也不必假设宿主在哪个线程上调我们。
    ///   2. 剪贴板随时可能被别的进程占用（<c>OpenClipboard</c> 失败抛异常），
    ///      因此做几次带间隔的重试，并对线程做超时 Join，避免万一卡住拖死调用方。
    /// </summary>
    private static bool TrySetClipboard(string text)
    {
        if (string.IsNullOrEmpty(text)) return false;

        bool ok = false;
        var worker = new Thread(() =>
        {
            for (int attempt = 0; attempt < 5; attempt++)
            {
                try
                {
                    // ⚠️ 用 SetDataObject 而不是 SetText：前者能指定重试次数与间隔。
                    //    WinForms 默认是 10 次 × 100ms（≈1 秒），再叠上外层 5 轮循环最坏要 5 秒；
                    //    而剪贴板历史类插件会在**每次变化时**抢着读，两边一撞就会长时间互相等，
                    //    用户看到的就是「复制一下卡好几秒」。这里压到 2 次 × 30ms（≈60ms/轮）。
                    System.Windows.Forms.Clipboard.SetDataObject(text, true, 2, 30);
                    ok = true;
                    return;
                }
                catch
                {
                    Thread.Sleep(60);
                }
            }
        })
        {
            IsBackground = true,
            Name = "NpsPushBridge.Clipboard",
        };

        try
        {
            worker.SetApartmentState(ApartmentState.STA);
            worker.Start();
            worker.Join(TimeSpan.FromSeconds(3));
        }
        catch
        {
            return false;
        }

        return ok;
    }

    // ====================================================================
    // 自动更新
    //
    // ★ 「点击提示打开更新界面」是怎么做出来的 ★
    // 宿主 PluginHost.PostReminder 会把 ReminderData.OnClick 整条丢掉（不读该字段，
    // ToastData 里也没有回调），所有 toast 的点击都被 Program.cs 绑死到
    // AppActivator.active_app(CurrentToast.Aumid) 上，而插件提醒的 Aumid 是空串 ——
    // 所以「点弹窗」在宿主侧无解。宿主源码不可改，于是改为：
    //   ① 用宿主的原生 Toast 只负责「报一声有新版本」（它是真的会弹出来的）；
    //   ② 提示条本身由**本插件自己注册的组件**承担 —— 组件的 HitTest/OnLeftClick 是宿主
    //      唯一确实会派发给插件的点击通道（Renderer.DispatchPluginLeftClick）；
    //   ③ 由于宿主按「注册表版本」缓存组件宽度，宽度不能原地变，所以提示条注册为**独立组件**，
    //      只在「决定要提示」时注册、被点掉后注销 —— 宽度永远在注册那一刻测量，天然准确。
    // 通知弹出期间宿主会 SetPluginRowBudget(0) 整行隐藏插件，于是顺序天然是
    //   Toast 报一声 → Toast 消失 → 提示条可点，不会两个面板抢位置。
    // ====================================================================

    /// <summary>
    /// 挂上周期检查 + 启动后延迟一次的首次检查。
    ///
    /// 触发条件（三处，互不冲突）：
    ///   ① 插件加载后 <see cref="UpdateFirstCheckDelay"/> 秒 —— 首次自动检查；
    ///   ② 之后每 <see cref="UpdateCheckInterval"/> 一次 —— 由宿主 ScheduleRefresh 驱动（后台线程）；
    ///   ③ 用户在配置界面点「检查」—— 手动检查（即使该版本已提示过也会重新提示）。
    /// </summary>
    private void StartUpdateChecks(IPluginHost host)
    {
        try
        {
            _updateTimer = host.ScheduleRefresh(UpdateCheckInterval,
                () => _ = CheckForUpdateAsync(manual: false));
        }
        catch (Exception ex)
        {
            _log.Add($"注册更新检查定时器失败：{ex.Message}");
        }

        // 首次检查走独立任务并先延迟，避免与启动渲染抢资源
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(UpdateFirstCheckDelay).ConfigureAwait(false);
                await CheckForUpdateAsync(manual: false).ConfigureAwait(false);
            }
            catch { /* 首次检查失败不影响任何功能 */ }
        });
    }

    /// <summary>
    /// 执行一次更新检查。**本方法不向调用方抛异常**；结果写入 <c>_updateResult</c> 并刷新界面。
    /// <paramref name="manual"/> = true（用户点「检查」）时即使该版本已提示过也会重新提示。
    /// </summary>
    private async Task CheckForUpdateAsync(bool manual)
    {
        if (Interlocked.CompareExchange(ref _checkingFlag, 1, 0) != 0) return;   // 防重入
        try
        {
            if (_host == null) return;
            if (manual) _log.AddUpdate("开始检查更新（手动）…");

            var result = await PluginUpdate.CheckAsync(CancellationToken.None).ConfigureAwait(false);
            _updateResult = result;

            // ★ 更新诊断一律走 AddUpdate：既进配置窗口的内存日志，也落进宿主 app.log ★
            // 更新检查 6 小时才一次、每次几行，不会污染日志；但它是唯一能远程取证的通道 ——
            // 插件自己的 _log 不写 app.log，用户复现一次却查不到卡在哪一步。
            _log.AddUpdate($"[更新检查{(manual ? "·手动" : "·自动")}] "
                + $"本地 v{PluginUpdate.LocalVersion} · 远端 v{(result.RemoteVersion.Length > 0 ? result.RemoteVersion : "?")} · "
                + $"判定 {(result.HasUpdate ? "有新版本" : "已是最新")} · {result.Message}");
            InvalidateSurfaces();

            // 手动检查：用户此刻就在界面前，把「已被点掉」的记忆清掉，让他能再次看到提示
            if (manual && result.HasUpdate) ClearDismissed();

            // ★ 顺序很重要：先决定「要不要把提示条挂上岛」，再决定「要不要弹吐司」★
            // 原先这里是「先 ApplyBannerState → 再 MarkDismissed → 再 NotifyUpdate」，
            // 于是自动检查的第一次就把远端版本写进 _dismissedVersion（= 用户还没点过任何东西，
            // 却已经被当成「这个版本我处理过了」），此后所有 ApplyBannerState 都返回 false，
            // 提示条永远上不了岛 —— 这正是「自动检查不会像手动检查那样提示」的根因。
            // 现在改成：先在「未忽略」的前提下决定一次显示意图，再记「本轮已提示过」，
            // 最后弹吐司。记「已提示过」只是为了**同一个版本不反复弹**，
            // 它不该、也不能阻止提示条出现在岛上（两者是不同的问题，见下面 ApplyBannerState 的说明）。
            ApplyBannerState(publish: true);
            TryAutoUpgrade(result);

            if (!result.HasUpdate) return;

            // ★ 自动检查与手动检查必须表现一致 ★
            // 两种检查都「先弹吐司、再留提示条」：用户无论是在启动/周期自动检查时发现新版本，
            // 还是自己点「检查」发现新版本，看到的都是同一套反馈（弹窗 + 岛上提示条）。
            //
            // 这里唯一保留的差异是**去重**：自动检查对同一个远端版本只弹一次吐司
            // （避免每 6 小时重复骚扰），而手动检查是用户主动点的，必须每次都给反馈。
            // 去重只影响「吐司弹几次」，绝不影响提示条是否上岛 —— 提示条的去重另用
            // _dismissedVersion（只在用户真的点掉它时才写入），见 UpdateNotice.ShouldShowBanner。
            if (!manual && !UpdateNotice.ShouldToast(result, _toastedVersion))
            {
                // 明确记下「为什么这次没弹吐司」——用户报「自动检查没弹窗」时，先看这一行
                _log.AddUpdate($"[更新检查·自动] 不弹吐司：v{result.RemoteVersion} 本轮已经弹过"
                    + "（提示条不受此影响，仍在岛上；点「立即检查」可强制再弹一次）");
                return;
            }

            // ★ 记忆必须在「确实要弹」之后才写 ★ 若提前无条件 MarkToastShown，
            // 自动检查时一个「未达弹吐司条件」的分支也会把版本记成已弹过，
            // 之后手动检查同样被这层记忆拦掉 —— 那正是「自动与手动不一致」的来源之一。
            MarkToastShown(result.RemoteVersion);
            NotifyUpdate(result);
        }
        catch (Exception ex)
        {
            _log.AddUpdate($"更新检查异常：{ex.Message}");
        }
        finally
        {
            Interlocked.Exchange(ref _checkingFlag, 0);
        }
    }

    /// <summary>用户是否已经「放过」这个版本（点过提示条 / 切到不提示）→ 岛上不再显示提示条。</summary>
    private bool IsDismissed(string remoteVersion)
        => UpdateNotice.IsRemembered(_dismissedVersion, remoteVersion);

    /// <summary>这个版本是否已经弹过吐司 —— 只为「同一个版本不反复弹」，与提示条无关。</summary>
    private bool IsToastShown(string remoteVersion)
        => UpdateNotice.IsRemembered(_toastedVersion, remoteVersion);

    private void MarkDismissed(string remoteVersion)
    {
        _dismissedVersion = remoteVersion;
        try { _host?.SetSetting(KeyDismissedVersion, remoteVersion); } catch { /* 写不进去只是下次可能重复提示 */ }
    }

    private void MarkToastShown(string remoteVersion)
    {
        _toastedVersion = remoteVersion;
        try { _host?.SetSetting(KeyToastedVersion, remoteVersion); } catch { /* 同上：写不进去最坏是下次重弹一次吐司 */ }
    }

    private void ClearDismissed()
    {
        if (_dismissedVersion.Length == 0) return;
        _dismissedVersion = "";
        try { _host?.SetSetting(KeyDismissedVersion, ""); } catch { /* 同上 */ }
    }

    /// <summary>
    /// 用宿主的原生 Toast「报一声」有新版本。
    ///
    /// ⚠️ 这里**不再**写「点击打开下载页」这类承诺 —— 宿主不消费 <c>OnClick</c>，
    /// 这么写就是骗用户去点一个不会有反应的弹窗。文案改为把用户引导到**真正能点**的
    /// 提示条上（<see cref="UpdateBannerWidget"/>）。见 <see cref="UpdateNotice.ToastBody"/>。
    ///
    /// ⚠️ 时长不要随意调大：吐司弹出期间宿主执行 <c>SetPluginRowBudget(0)</c>，
    /// <b>整行插件（含更新提示条）会被一并隐藏</b> —— 吐司停留多久，提示条就被压住多久。
    /// 用户报的「弹窗弹了但岛上迟迟没有提示条」有一半就是这条规则造成的。
    /// 取一个「能看清一行字」的短时长，让提示条尽早露面。
    /// </summary>
    private void NotifyUpdate(UpdateCheckResult result)
    {
        var host = _host;
        if (host == null) return;

        try
        {
            host.PostReminder(new ReminderData
            {
                Title = UpdateNotice.ToastTitle(result),
                Body = UpdateNotice.ToastBody(result, _bannerMode),
                Duration = ReminderToastDuration,
            });
        }
        catch (Exception ex)
        {
            _log.Add($"弹出更新提示失败：{ex.Message}");
        }
    }

    // ── 更新提示条（灵动岛）─────────────────────────────────────────────

    // IUpdateBannerHost
    bool IUpdateBannerHost.BannerVisible => _banner != null;

    string IUpdateBannerHost.BannerLabel
        => _updateResult.RemoteVersion.Length > 0
            ? $"{UpdateNotice.BannerText} v{_updateResult.RemoteVersion}"
            : UpdateNotice.BannerText;

    /// <summary>
    /// 预算紧张时的短文案：只有箭头 + 「更新」，不带版本号。
    ///
    /// ★ 为什么必须存在 ★ 宿主按注册顺序贪心放行组件，提示条排在状态灯之后；
    /// 组合模式下原生模块（时钟 / 硬件 / 媒体）一多，剩余预算就装不下完整文案，
    /// 提示条会被**整帧跳过**（用户主观就是「没上岛」）。退到短版能保住提示条本身在岛上。
    /// </summary>
    string IUpdateBannerHost.BannerLabelCompact
        => _updateResult.RemoteVersion.Length > 0
            ? $"{UpdateNotice.BannerTextCompact} v{_updateResult.RemoteVersion}"
            : UpdateNotice.BannerTextCompact;

    /// <summary>
    /// 宿主给出的「本插件位置的剩余可用宽度」。取不到时返回 +∞（按「都放得下」处理），
    /// 避免首帧因读取失败而白白让提示条消失。
    /// </summary>
    float IUpdateBannerHost.AvailableRowWidth
    {
        get
        {
            try
            {
                var host = _host;
                if (host == null) return float.PositiveInfinity;
                return host.GetPluginRowBudget();
            }
            catch
            {
                // 宿主未接线 / 插件正在卸载 → 保守放行，宁可显示也不静默消失
                return float.PositiveInfinity;
            }
        }
    }

    string IUpdateBannerHost.BannerHint => UpdateNotice.ModeDescribe(_bannerMode)
        + " → " + UpdateNotice.ClickActionLabel(_clickAction);

    void IUpdateBannerHost.OnBannerClick() => OnUpdateBannerClicked();

    /// <summary>
    /// 按当前模式 / 结果决定岛上要不要有提示条，并把变更落到宿主的组件注册表上。
    /// 注册与注销都放在后台任务里做：宿主重建组件快照会重测全部组件宽度，
    /// 不该压在渲染线程或点击回调上。
    ///
    /// ★★ 本次修复的核心：把「判定」与「注册」之间的静默断层补上 ★★
    /// 曾经的病灶（用户报「弹窗弹了、提示条没上岛」）：
    ///   ① 注册路径上有 30 秒冷却守卫，命中时**直接 return 且不写日志** ——
    ///      判定明明为真，注册却被悄悄吞掉；而吐司没有这道守卫、照弹不误，两者行为分叉；
    ///   ② 提示条只能在 60FPS 渲染时被宿主绘制，而吐司弹出期间宿主会
    ///      <c>SetPluginRowBudget(0)</c> 把整行插件隐藏 —— 吐司停留多久，提示条就被压住多久；
    ///   ③ 注册成功与否没有任何日志留痕，出问题时无从判断卡在哪一步。
    /// 现在依次收口：冷却不再拦「从无到有」；每条分支都写明结果；成功时明确记一条日志。
    /// </summary>
    private void ApplyBannerState(bool publish)
    {
        var host = _host;
        if (host == null) return;

        bool want = UpdateNotice.ShouldShowBanner(_updateResult, _bannerMode, _dismissedVersion);

        lock (_gate)
        {
            bool have = _banner != null;
            if (want == have) return;

            // 只放行「第一次注册」；注销交给点击 / 切模式时显式调用，
            // 避免在渲染线程里连续改注册表。
            if (!want) { _banner = null; }
        }

        if (!want)
        {
            // 明确记下「为什么不显示」——这条日志是排查上岛问题的第一现场
            _log.AddUpdate($"更新提示条不上岛：{DescribeBannerBlock()}");
            return;
        }

        // ★★ 冷却守卫已从这里移除 ★★（本次 bug 的直接病灶）
        // 走到这里必然满足 want && !have —— 也就是说岛上此刻**确实没有**提示条组件，
        // 这正是「必须立刻注册」的情形。原先这里有一道
        //   if (DateTime.UtcNow - _bannerRegisteredAt < UpdateBannerRegistrarCooldown) return;
        // 且**不写任何日志**：判定明明为真，注册却被静默吞掉；而吐司 NotifyUpdate 没有这道守卫、
        // 照弹不误 —— 两者行为就此分叉，用户看到的就是「弹窗弹了、岛上一直空着」。
        // 现在把「要不要注册」完全交给纯函数判定（UpdateNotice.MustRegisterNow 的可断言形式），
        // 节流只保留 UpdateBannerRegistrarCooldown 这个常量作为设计说明，不再拦任何路径。
        // 防抖职责改由下面的 _bannerRegistering 互斥锁 + 注册成功后才认领 _banner 来承担 ——
        // 那两项都是「有则跳过、无则执行」，不会让本该显示的提示条消失。
        System.Diagnostics.Debug.Assert(
            UpdateNotice.MustRegisterNow(want, alreadyRegistered: false),
            "走到注册分支时应当满足「判定要显示且当前未注册」");

        if (Interlocked.CompareExchange(ref _bannerRegistering, 1, 0) != 0)
        {
            _log.AddUpdate("更新提示条正在注册中，本轮跳过");
            return;
        }

        var widget = new UpdateBannerWidget(this);

        // ★ 先把组件挂进宿主注册表，再登记到 _banner ★
        // 若像原来那样先写 _banner、后在 Task.Run 里注册，存在一个会产生**永久空白**的窗口：
        //   _banner = widget →（此刻 RetireBanner 被调用，把 _banner 置空、并 Retire 这个 widget）
        //   → Task.Run 才真正 RegisterWidget(widget) —— 于是被作废的组件仍被注册进宿主，
        //     且 _banner 已为 null，RetireBanner 再也找不到它、无法把它作废，
        //     宿主按注册那一刻的宽度永久预留，那一块就永远是空白。
        // 现在改成「注册成功后才写 _banner，且写之前再确认一次仍然 want」，
        // 上述交错就不可能出现：任何时刻 _banner 非空 ⇒ 该组件确实在宿主注册表里且未作废。
        Task.Run(() =>
        {
            try
            {
                if (!UpdateNotice.ShouldShowBanner(_updateResult, _bannerMode, _dismissedVersion))
                {
                    // 排队期间用户已点掉 / 切到「不提示」→ 根本不该注册，作废后直接退出
                    widget.Retire();
                    _log.AddUpdate("更新提示条注册取消（排队期间已被收起或切到「不提示」）");
                    return;
                }

                _host?.RegisterWidget(widget);
                _bannerRegisteredAt = DateTime.UtcNow;

                bool adopted;
                lock (_gate)
                {
                    // 与 RetireBanner 互斥：只有当 _banner 仍为空（期间没人收起/没被别的注册顶掉）
                    // 才认领这个组件；否则说明它已被放弃，必须立刻作废，绝不能留在宿主注册表里当幽灵。
                    adopted = _banner == null;
                    if (adopted) _banner = widget;
                }

                if (!adopted)
                {
                    // 已被放弃：让宿主下一帧重测出 0 宽度，这一帧的预留随即消失
                    widget.Retire();
                    InvalidateWidgetLayout();
                    _log.AddUpdate("更新提示条注册后未被认领，已作废");
                    return;
                }

                // ★ 记录「注册那一刻的剩余预算」★ 这是判断提示条会不会被挤掉的关键数据：
                // 宿主按注册顺序贪心放行，提示条排在状态灯之后；若注册时剩余预算已很紧，
                // 下一帧原生模块一变宽（媒体开始播放 / 歌词变长）就可能被整帧跳过。
                float remaining = float.PositiveInfinity;
                try { remaining = _host?.GetPluginRowBudget() ?? float.PositiveInfinity; }
                catch { /* 取不到就当充裕 */ }

                _log.AddUpdate($"已注册更新提示条：{((IUpdateBannerHost)this).BannerLabel}"
                    + $"（{UpdateNotice.ModeDescribe(_bannerMode)}）"
                    + $"· 注册时该位置剩余预算 {(float.IsInfinity(remaining) ? "∞" : remaining.ToString("F0") + "px")}"
                    + " · 完整文案需 " + UpdateNotice.HostPluginGap.ToString("F0") + "+"
                    + UpdateBannerWidget.EstimateFullWidth(((IUpdateBannerHost)this).BannerLabel).ToString("F0") + "px"
                    + "，不足时会自动退成短文案「" + UpdateNotice.BannerTextCompact + "」");
            }
            catch (Exception ex)
            {
                _log.Add($"注册更新提示条失败：{ex.Message}");
            }
            finally
            {
                Interlocked.Exchange(ref _bannerRegistering, 0);
            }
        });

        if (publish) InvalidateSurfaces();
    }

    /// <summary>
    /// 给出「提示条为什么没上岛」的一句话原因，供日志与用户自查。
    /// 判定顺序与 <see cref="UpdateNotice.ShouldShowBanner"/> 严格一致 ——
    /// 否则日志会与实际行为对不上，反而误导排查。
    /// </summary>
    private string DescribeBannerBlock()
    {
        if (!_updateResult.HasUpdate) return "本次检查未发现新版本";
        if (_bannerMode == UpdateBannerMode.Off) return "提示模式为「不提示」";
        if (_updateResult.RemoteVersion.Length == 0) return "远端版本号为空";
        if (IsDismissed(_updateResult.RemoteVersion))
            return $"版本 v{_updateResult.RemoteVersion} 已被点掉过（点「立即检查」可强制重现）";
        return "原因未知";
    }

    /// <summary>
    /// 点击提示条：这是「点击后能正常打开更新界面」真正落地的地方。
    ///
    /// 交互行为：
    ///   · 任何模式下被点击都会**收起提示条**（用户已经看到了，不该继续占着岛体）；
    ///   · 只有「可点击」模式才真的执行动作：
    ///       - 打开下载页 → 浏览器打开插件市场页（一定有内容可看）；
    ///       - 下载更新包 → 浏览器打开市场记录里的 .zip 直链，没有直链则退回市场页；
    ///       - 复制到剪贴板 → 把「版本 + 市场页 + 下载地址」写进剪贴板；
    ///   · 「只显示」模式点击后什么都不做 —— 纯告知，防误触。
    /// </summary>
    private void OnUpdateBannerClicked()
    {
        var result = _updateResult;

        // 先收起提示条：无论走哪个分支，用户点过就不该再看到它
        DismissBanner(result);

        if (!UpdateNotice.ShouldRunClickAction(_bannerMode))
        {
            _log.Add("更新提示条被点击（当前为「只显示」模式，不执行动作）");
            return;
        }

        var target = UpdateNotice.ResolveClickTarget(result, _clickAction);

        if (_clickAction == UpdateClickAction.Clipboard)
        {
            _log.Add(TrySetClipboard(target)
                ? "更新信息已复制到剪贴板"
                : "复制更新信息失败（剪贴板被占用）");
            return;
        }

        var error = PluginUpdate.OpenInBrowser(target);
        _log.Add(error.Length == 0 ? $"已打开：{target}" : error);
    }

    /// <summary>
    /// 收起提示条：记「用户已忽略这个版本」并把它从岛上摘掉。**只在用户真的点过提示条时调用**
    /// （或「检查」按钮把记忆清掉后重新提示时）—— 它带持久化副作用，会阻止该版本再次上岛。
    /// </summary>
    private void DismissBanner(UpdateCheckResult result)
    {
        if (result.HasUpdate) MarkDismissed(result.RemoteVersion);
        RetireBanner();
    }

    /// <summary>
    /// 只把提示条从岛上摘掉，**不写「已忽略」记忆**。切显示模式时用它 ——
    /// 用户换的是显示方式，不代表他处理过这个版本（见 <see cref="IBridgePanelHost.SetBannerMode"/>）。
    /// </summary>
    private void RetireBanner()
    {
        UpdateBannerWidget? banner;
        lock (_gate)
        {
            banner = _banner;
            _banner = null;
        }

        if (banner == null) return;

        // 宿主没有「注销组件」的插件 API，但组件自己可以自我失效：
        // 让 MeasureWidth 返回 0 —— 宿主会把宽度为 0 的组件判定为不可显示
        // （RecomputePluginVisibility 里的 widths[i] > 0f），于是它下一帧起就不再绘制、不再有命中区。
        banner.Retire();

        // ★ 顺序：先让宿主失效宽度缓存，再清界面。反之会有一帧「缓存仍是旧宽、组件已不画」★
        InvalidateWidgetLayout();

        // ★ 为什么要连做两次（第二次在后台稍微延迟）★
        // 这是本 bug「点击后出现一块空白」的收口。宽度缓存失效靠 WidgetsVersion 自增，
        // 而宿主重建快照的时机是**渲染线程下一帧**。上面那次调用是同步的，正常情况下够用；
        // 但存在一个极窄的竞态窗口：宿主此刻可能正处在「已读完旧版本号、尚未写入新快照」之间，
        // 于是这次自增被它吞掉 —— 缓存里仍是提示条的非零宽度，而组件已经从 Draw 里返回，
        // 那一块 90×岛高 的矩形就成了纯空白（且因为是零内容，用户只看到"空"）。
        // 延迟再补一次自增，必然落在它重建快照之后，缓存一定能被刷成 0。
        // 代价只有一次极轻量的版本自增（宿主下一帧重测一次宽度），远小于留一块空白的代价。
        ScheduleLayoutReinvalidate();
        InvalidateSurfaces();
    }

    /// <summary>
    /// 延迟补一次宽度缓存失效（见 <see cref="RetireBanner"/> 的竞态说明）。
    /// 用线程池延迟而不是 Task.Run 立即执行：要的就是「越过宿主当前这一帧」。
    /// 任何异常都被吞掉 —— 最坏只是少一次兜底，不影响功能。
    /// </summary>
    private void ScheduleLayoutReinvalidate()
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(BannerReinvalidateDelay).ConfigureAwait(false);
                InvalidateWidgetLayout();
            }
            catch { /* 宿主可能已卸载本插件 */ }
        });
    }

    /// <summary>
    /// 请求宿主重新测量本插件的组件宽度（宿主 <c>IPluginHost.InvalidateWidgetLayout</c> 的薄封装）。
    ///
    /// 凡是让 <see cref="UpdateBannerWidget.MeasureWidth"/> 的返回值发生变化的时机都必须调它：
    /// 宿主只在注册表版本变化时重测，不调就等于「插件改了宽度、宿主还在用老值」。
    /// 失败不影响功能（最坏是宽度停留在旧值一帧），所以吞掉异常。
    /// </summary>
    private void InvalidateWidgetLayout()
    {
        try { _host?.InvalidateWidgetLayout(); }
        catch { /* 宿主可能正在卸载本插件 */ }
    }

    // ════════════════════════════════════════════════════════════════════
    // 一键静默升级
    //
    // ★ 它到底"静默"在哪 ★
    // 用户打开开关后，整条链路——检查 → 下载 → 校验 → 备份 → 替换 ——全部在后台线程完成，
    // 中间不需要用户点任何按钮。之所以本插件做得到而宿主自己的更新器做不到：
    //   ① 宿主是「影子拷贝」加载插件的（先复制到 %TEMP% 再 LoadFromAssemblyPath），
    //      因此**进程不持有 plugins 目录里那个 dll 的句柄**，插件可以自己覆盖自己；
    //   ② 市场记录里带 download 直链，插件直接 HTTP 下载即可，不必拉起浏览器。
    // 唯一不能自动完成的是「让新代码生效」：宿主没有暴露"重载我"的插件 API，
    // 所以替换完只到 PendingReload 为止，由用户在插件中心点一次「重载」（或重启程序）生效。
    // 这是刻意保留的人工确认点，不是没做完 —— 见 自动更新说明.md 第 5 章。
    // ════════════════════════════════════════════════════════════════════

    /// <summary>更新检查结束后调用：决定要不要立刻开始静默升级。</summary>
    private void TryAutoUpgrade(UpdateCheckResult result)
    {
        if (!_silentUpgradeEnabled) return;

        // 有更新但市场没给直链：不是错误，只是无从"静默"—— 记一条日志说明，界面会显示原因
        if (SilentUpgrade.IsBlockedByMissingDownload(result, enabled: true))
        {
            if (_upgradeStage != UpgradeStage.Blocked)
            {
                SetUpgradeStage(UpgradeStage.Blocked, "");
                _log.Add("静默升级：市场未提供安装包直链，本轮跳过（可点「打开下载页」手动下载）");
            }
            return;
        }

        if (!SilentUpgrade.ShouldAutoInstall(result, _silentUpgradeEnabled,
                _upgradeAttemptedVersion, _upgradeStage))
            return;

        _ = RunUpgradeAsync(result);
    }

    /// <summary>执行一次完整的静默升级；任何失败都收敛成状态 + 日志，不向调用方抛异常。</summary>
    private async Task RunUpgradeAsync(UpdateCheckResult result)
    {
        if (Interlocked.CompareExchange(ref _upgradingFlag, 1, 0) != 0) return;   // 防重入

        var version = result.RemoteVersion;
        var package = "";
        var extractDir = "";

        try
        {
            MarkUpgradeAttempted(version);
            SetUpgradeStage(UpgradeStage.Downloading, "");
            _log.Add($"静默升级：开始下载 v{version} 安装包");

            // ── 下载 ──
            var progress = new Progress<(long Received, long Total)>(p =>
            {
                _upgradeReceived = p.Received;
                _upgradeTotal = p.Total;
                InvalidateSurfaces();
            });

            var (path, downloadError) = await SilentUpgrader
                .DownloadAsync(result.DownloadUrl, version, progress, CancellationToken.None)
                .ConfigureAwait(false);

            if (downloadError.Length > 0)
            {
                FailUpgrade(UpgradeStage.Downloading, downloadError);
                return;
            }
            package = path;
            _log.Add($"静默升级：下载完成（{SilentUpgrade.FormatSize(new FileInfo(package).Length)}），开始校验");

            // ── 校验 ──
            SetUpgradeStage(UpgradeStage.Verifying, "");
            var verify = SilentUpgrader.VerifyAndExtract(package, PluginUpdate.LocalVersion, Id);
            if (!verify.Ok)
            {
                FailUpgrade(UpgradeStage.Verifying, verify.Error);
                return;
            }
            extractDir = verify.ExtractDir;
            _log.Add($"静默升级：校验通过（包内版本 v{verify.Version}）");

            // ── 定位目标并安装 ──
            var target = SilentUpgrader.ResolveInstalledDllPath();
            if (target.Length == 0)
            {
                FailUpgrade(UpgradeStage.Verifying,
                    "没有在宿主 plugins 目录里找到本插件的 dll（可能是自定义布局），已跳过自动安装");
                return;
            }

            SetUpgradeStage(UpgradeStage.Installing, "");
            var install = SilentUpgrader.Install(Path.Combine(extractDir, verify.DllName), target);

            if (!install.Ok)
            {
                // 替换阶段出过手 → 必须把备份盖回去，绝不能让插件目录停在一半状态
                bool rolled = SilentUpgrader.Rollback(install.BackupPath, target);
                FailUpgrade(UpgradeStage.Installing,
                    install.Error + " · " + SilentUpgrade.RollbackDescribe(rolled, PluginUpdate.LocalVersion));
                if (rolled) _log.Add($"静默升级：已回退到 v{PluginUpdate.LocalVersion}（{target}）");
                return;
            }

            // ── 成功（等宿主重载生效）──
            SucceedUpgrade(verify.Version, install.BackupPath, target);
        }
        catch (Exception ex)
        {
            FailUpgrade(_upgradeStage, $"升级异常：{ex.GetType().Name} {ex.Message}");
        }
        finally
        {
            SilentUpgrader.CleanupStaging(extractDir, package);
            Interlocked.Exchange(ref _upgradingFlag, 0);
        }
    }

    /// <summary>升级成功：进入「待重载」，弹一条提醒告诉用户怎么让它生效。</summary>
    private void SucceedUpgrade(string newVersion, string backupPath, string target)
    {
        _upgradeFailures = 0;
        _upgradeError = "";
        _upgradeReceived = _upgradeTotal = 0;
        SetUpgradeStage(UpgradeStage.PendingReload, "");

        if (backupPath.Length > 0)
            _log.Add($"静默升级：已备份旧版本到 {Path.GetFileName(backupPath)}");
        _log.Add($"静默升级：v{newVersion} 已写入 {target}");

        // 先用「待重载」的文案弹一次；真实生效由用户重载后本地版本变化体现
        PostUpgradeReminder(
            $"已下载并安装 v{newVersion}",
            "到插件中心点「重载」即刻生效（不点也不影响当前使用）");

        SetUpgradeStage(UpgradeStage.Succeeded, "");
    }

    /// <summary>
    /// 升级失败：记录原因、决定是否退避重试、必要时回退，并把结果告诉用户。
    /// 失败**绝不影响正在运行的旧版本** —— 这是本功能的硬性要求。
    /// </summary>
    private void FailUpgrade(UpgradeStage stage, string reason)
    {
        _upgradeFailures++;
        _upgradeError = reason;
        _upgradeReceived = _upgradeTotal = -1;
        SetUpgradeStage(UpgradeStage.Failed, reason);

        _log.Add($"静默升级失败（第 {_upgradeFailures} 次，停在「{SilentUpgrade.StageLabel(stage)}」）：{reason}");

        if (!SilentUpgrade.ShouldRetry(_upgradeFailures))
        {
            PostUpgradeReminder("插件自动升级失败", $"已重试 {SilentUpgrade.MaxRetries} 次，"
                + $"当前仍为 v{PluginUpdate.LocalVersion}，可到插件市场手动下载");
            return;
        }

        var delay = SilentUpgrade.RetryDelay(_upgradeFailures);
        _log.Add($"静默升级：将于 {SilentUpgrade.FormatDelay(delay)} 后自动重试");
        ScheduleUpgradeRetry(delay);
    }

    /// <summary>
    /// 安排一次退避重试。用可取消的延迟任务而不是宿主 ScheduleRefresh：
    /// 退避间隔是「一次性的」，而 ScheduleRefresh 是固定周期；而且关掉开关 / 换了目标版本时
    /// 必须能把排队的重试立刻作废（见 <see cref="CancelUpgradeRetry"/>）。
    /// </summary>
    private void ScheduleUpgradeRetry(TimeSpan delay)
    {
        CancelUpgradeRetry();

        var cts = new CancellationTokenSource();
        _retryCts = cts;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delay, cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { return; }   // 被取消：本次重试作废
            catch { return; }

            // 重试前重新确认前置条件（用户可能已关开关 / 已升级成功）
            var result = _updateResult;
            if (!SilentUpgrade.ShouldAutoInstall(result, _silentUpgradeEnabled,
                    _upgradeAttemptedVersion, UpgradeStage.Idle))
                return;

            SetUpgradeStage(UpgradeStage.Checking, "");
            _log.Add("静默升级：开始重试");
            await RunUpgradeAsync(result).ConfigureAwait(false);
        });
    }

    private void CancelUpgradeRetry()
    {
        var cts = _retryCts;
        _retryCts = null;

        if (cts == null) return;
        try { cts.Cancel(); } catch { /* 已取消 */ }
        try { cts.Dispose(); } catch { /* 已释放 */ }
    }

    private void SetUpgradeStage(UpgradeStage stage, string error)
    {
        _upgradeStage = stage;
        _upgradeError = error;
        InvalidateSurfaces();
    }

    private void MarkUpgradeAttempted(string version)
    {
        _upgradeAttemptedVersion = version;
        try { _host?.SetSetting(KeyUpgradeAttemptedVersion, version); } catch { /* 写不进去只是可能重复下载 */ }
    }

    /// <summary>弹一条升级结果提醒（走宿主原生 Toast —— 它只负责"报一声"，点击本来就没用）。</summary>
    private void PostUpgradeReminder(string title, string body)
    {
        try
        {
            _host?.PostReminder(new ReminderData
            {
                Title = title,
                Body = body,
                Duration = ReminderToastDuration,
            });
        }
        catch (Exception ex)
        {
            _log.Add($"升级结果提醒失败：{ex.Message}");
        }
    }

    // ── IBridgePanelHost：静默升级区块 ──

    bool IBridgePanelHost.SilentUpgradeEnabled => _silentUpgradeEnabled;

    void IBridgePanelHost.SetSilentUpgrade(bool enabled)
    {
        _silentUpgradeEnabled = enabled;
        _host?.SetSetting(KeySilentUpgrade, SilentUpgrade.EnabledToRaw(enabled));
        _log.Add($"一键静默升级已{(enabled ? "开启" : "关闭")}：{SilentUpgrade.SwitchDescribe(enabled)}");

        if (enabled)
        {
            // 立刻评估一次：如果界面上已经有"有新版本"的结果，不必等下一个 6 小时窗口
            _upgradeFailures = 0;
            _upgradeError = "";
            SetUpgradeStage(UpgradeStage.Idle, "");
            TryAutoUpgrade(_updateResult);
        }
        else
        {
            // 关开关：作废排队中的重试，并把状态复位成一个诚实的"待命"
            CancelUpgradeRetry();
            _upgradeReceived = _upgradeTotal = -1;
            SetUpgradeStage(UpgradeStage.Idle, "");
        }

        InvalidateSurfaces();
    }

    UpgradeStage IBridgePanelHost.CurrentUpgradeStage => _upgradeStage;

    string IBridgePanelHost.UpgradeStatusText()
        => SilentUpgrade.Describe(_silentUpgradeEnabled, _upgradeStage,
            _upgradeReceived, _upgradeTotal, _upgradeFailures, _upgradeError, _updateResult);

    void IBridgePanelHost.UpgradeNow()
    {
        // 手动触发：清掉重试计数与「试过这个版本」的记忆，让用户能强制重来一次
        _upgradeFailures = 0;
        _upgradeError = "";
        _upgradeAttemptedVersion = "";
        CancelUpgradeRetry();

        if (!_silentUpgradeEnabled)
        {
            _log.Add("请先打开「一键静默升级」开关");
            return;
        }

        if (SilentUpgrade.IsBusy(_upgradeStage))
        {
            _log.Add("静默升级正在进行中，请稍候");
            return;
        }

        if (!_updateResult.HasUpdate)
        {
            _log.Add("当前没有可用更新，正在重新检查…");
            _ = CheckForUpdateAsync(manual: true);
            return;
        }

        if (string.IsNullOrWhiteSpace(_updateResult.DownloadUrl))
        {
            _log.Add("市场未提供安装包直链，无法自动安装 —— 可点「打开下载页」手动下载");
            return;
        }

        _ = RunUpgradeAsync(_updateResult);
    }

    // ── IBridgePanelHost：更新区块 ──

    UpdateCheckResult IBridgePanelHost.UpdateInfo() => _updateResult;

    void IBridgePanelHost.CheckUpdateNow() => _ = CheckForUpdateAsync(manual: true);

    void IBridgePanelHost.CopyUpdateInfo()
    {
        var text = UpdateNotice.UpdateSummary(_updateResult);
        _log.Add(TrySetClipboard(text) ? "更新信息已复制到剪贴板" : "复制更新信息失败（剪贴板被占用）");
    }

    void IBridgePanelHost.OpenMarketPage() => OpenUrl(PluginUpdate.MarketPageUrl);

    void IBridgePanelHost.OpenDownload()
        => OpenUrl(UpdateNotice.ResolveClickTarget(_updateResult, UpdateClickAction.Download));

    UpdateBannerMode IBridgePanelHost.CurrentBannerMode => _bannerMode;

    void IBridgePanelHost.SetBannerMode(UpdateBannerMode mode)
    {
        _bannerMode = mode;
        _host?.SetSetting(KeyBannerMode, UpdateNotice.ModeToRaw(mode));
        _log.Add($"灵动岛更新提示已设为：{UpdateNotice.ModeDescribe(mode)}");

        // 切到「不提示」要立刻收起已有提示条；切回来时若仍有新版本则重新出现。
        // ★ 这里只「收起」，**不记「已忽略」** ★：用户只是换了个显示模式，并没有表示
        // 「这个版本我处理过了」。若在这里写入 updateDismissedVersion，之后再切回「可点击」
        // 就永远看不到提示条了 —— 而且「不提示」模式本来就该是「换个模式还能再看到」。
        if (mode == UpdateBannerMode.Off) RetireBanner();
        else ApplyBannerState(publish: true);

        InvalidateSurfaces();
    }

    UpdateClickAction IBridgePanelHost.CurrentClickAction => _clickAction;

    void IBridgePanelHost.SetClickAction(UpdateClickAction action)
    {
        _clickAction = action;
        _host?.SetSetting(KeyClickAction, UpdateNotice.ClickActionToRaw(action));
        _log.Add($"点击更新提示条后：{UpdateNotice.ClickActionLabel(action)}");
        InvalidateSurfaces();
    }

    /// <summary>打开一个网址并记录结果（失败收敛成日志，不抛异常）。</summary>
    private void OpenUrl(string url)
    {
        var error = PluginUpdate.OpenInBrowser(url);
        _log.Add(error.Length == 0 ? $"已打开：{url}" : error);
    }

    // ====================================================================
    // IWidgetStatus
    // ====================================================================

    /// <summary>状态灯详情页读取运行快照（复用 <see cref="IBridgePanelHost.Snapshot"/> 的同一份实现）。</summary>
    ConfigSnapshot IWidgetStatus.Snapshot() => ((IBridgePanelHost)this).Snapshot();

    public bool ServiceRunning { get { lock (_gate) return _server is { IsRunning: true }; } }

    /// <summary>
    /// 是否有**尚未恢复**的失败（转发失败 / 请求无效）——成功转发一次即清除，状态灯据此由绿转橙、再回绿。
    ///
    /// ★ 不要改回读累计失败计数 ★
    /// 累计计数只增不减：只要历史上出现过一次 400/502，读它就会让灯**永久变橙**，
    /// 服务早已恢复正常却仍显示「有失败」—— 这正是「状态灯无法正确显示当前状态」的老毛病。
    /// 判定标记在 <see cref="HttpBridgeServer.Unhealthy"/> 里随请求实时维护。
    ///
    /// 服务已停止时恒为 false：此时灯是灰色（停止），橙色没有意义。
    /// </summary>
    public bool Unhealthy
    {
        get
        {
            lock (_gate) return _server is { } server && server.Unhealthy;
        }
    }

    /// <summary>右键组件时打开配置窗口（宿主设置页未接线期间的可用入口）。</summary>
    public void OpenConfig() => OpenConfigCore();

    /// <summary>
    /// 打开配置窗口。
    ///
    /// ★ 为什么是「插件自建窗口」而不是宿主的 <c>IPluginWindow</c> ★
    /// 宿主 <c>PluginWindow</c> 每次重绘都会 <c>UpdateLayeredWindow(..., pptDst, psize, ...)</c>，
    /// 而这两个参数会把窗口**移动并缩放**回创建时记下的值 —— 所以对它做 <c>SetWindowPos</c>
    /// 会在下一帧被抹掉，**移动与缩放都留不住**；再加上它有 <c>readonly</c> 尺寸、
    /// 没有 <c>CS_DBLCLKS</c>、只转发 <c>WM_CHAR</c>，等于把「可调节大小位置 / 真双击 / 方向键」
    /// 全部堵死。自建窗口一次性解决这四条，且**不修改宿主任何源码**。
    ///
    /// 尺寸 / 位置 / 置顶状态从宿主设置读回（首次打开则居中），关闭时写回 ——
    /// 于是用户把它摆在哪、拉多大、要不要置顶，下次打开都还在。
    /// </summary>
    private void OpenConfigCore()
    {
        // ⚠️ 整体兜底 ★ 打开窗口跑在宿主回调里（WPF 的 Dispatcher 线程），
        // 任何未捕获异常都会被 WPF 当成「应用未处理异常」直接**闪退整个宿主**。
        // 所以这里无论哪一步抛异常，都只能记日志、绝不能让它往外冒。
        try
        {
            OpenConfigCoreUnsafe();
        }
        catch (Exception ex)
        {
            try { _log.Add($"配置窗口打开失败（{ex.GetType().Name}：{ex.Message}）"); }
            catch { /* 日志都不让写也不能崩 */ }
        }
    }

    private void OpenConfigCoreUnsafe()
    {
        var host = _host;
        var panel = _panel;
        if (host == null || panel == null) return;

        // 先关旧窗再开新窗（重复点击打开时替换旧窗，而不是叠出第二个）
        CloseConfigWindow();

        panel.Reload();

        var state = WindowState.Parse(host.GetSetting(KeyWindowState, ""),
            ConfigPanelDefaults.WindowWidth, ConfigPanelDefaults.WindowHeight);

        var window = new PanelWindow(
            ConfigPanelDefaults.WindowTitle, state,
            (int)ConfigLayout.MinContentWidth, (int)ConfigLayout.MinRecommendedHeight,
            draw: (canvas, w, h) => panel.Draw(canvas, new SKRect(0, 0, w, h), PanelTheme.Window, false),
            mouseDown: panel.OnMouseDown,
            mouseMove: panel.OnMouseMove,
            mouseUp: panel.OnMouseUp,
            onChar: panel.OnChar,
            onKey: panel.OnSpecialKey,
            onWheel: panel.OnWheel);

        // ★ 自建窗口能给出真正的 WM_KEYDOWN ⇒ 关掉 GetAsyncKeyState 轮询，
        //   否则一次方向键会被处理两次（走两格）。
        panel.NativeKeyEvents = true;

        window.CloseRequested = CloseConfigWindow;
        window.Resized = (_, _) => panel.RequestRedrawForResize();
        window.FocusChanged = _ => { /* 焦点变化目前只影响窗口，面板的光标由聚焦框决定 */ };

        // 标题行里的「置顶」按钮落在窗口的拖动条内 → 告诉窗口「这一点归面板」，
        // 否则点它会变成拖窗口（几何知识在面板那边，见 ConfigPanel.WantsMouseAt）。
        window.ClientAreaOverride = panel.WantsMouseAt;

        if (!window.Show())
        {
            // 创建失败（极少见）：如实记日志，并退回让面板知道自己没有按键通路
            panel.NativeKeyEvents = false;
            window.Dispose();
            _log.Add("配置窗口创建失败（自建窗口无法建立）");
            return;
        }

        _configWindow = window;
        _log.Add($"已打开配置窗口（{state.Width}×{state.Height}，置顶 {(state.Topmost ? "开" : "关")}）");

        // 打开完整配置后，收起灵动岛上的「配置详情页」—— 它的使命（提供入口 + 预览状态）
        // 到此完成，岛体恢复成状态灯，避免详情页长期占着岛体。
        // 未展开时是空操作，安全。
        try { host.CloseDetailPage(); } catch { /* 收起失败不影响窗口已打开 */ }
    }

    /// <summary>
    /// 关闭并释放配置窗口，同时把尺寸 / 位置 / 置顶**持久化**。
    ///
    /// ★ 为什么要在这里存 ★ 只有窗口活着时才知道它最后被拖到哪、拉到多大；
    /// 用户点关闭（或按 Esc）那一刻是唯一能读到这些值的时机。
    /// </summary>
    private void CloseConfigWindow()
    {
        var window = _configWindow;
        _configWindow = null;
        if (window == null) return;

        try
        {
            var host = _host;
            if (host != null) host.SetSetting(KeyWindowState, window.State.Serialize());
        }
        catch
        {
            // 设置写入失败不该影响关窗
        }

        if (_panel != null) _panel.NativeKeyEvents = false;

        try { window.Dispose(); } catch { /* 已释放 */ }
    }

    /// <summary>
    /// 面板心跳（约 30Hz）：驱动**光标闪烁**，并在「窗口不提供按键事件」时兜底**轮询方向键**
    /// （见 <see cref="ConfigPanel.Tick"/>）。
    ///
    /// ★ 为什么仍然需要它 ★
    /// 光标闪烁本来就需要一个时钟；而方向键在**自建窗口**上虽然能直接收到 <c>WM_KEYDOWN</c>
    /// （此时轮询被 <see cref="ConfigPanel.NativeKeyEvents"/> 关掉），
    /// 但若将来回到「宿主窗口」那条通路，轮询就是唯一手段 —— 保留它是为了不把这条路堵死。
    ///
    /// ★ 成本几乎为零 ★ 回调体只是 `if (panel.NeedsTicks) panel.Tick()` ——
    /// **没有输入框聚焦时**（也就是绝大部分时间）只有一次布尔读取，不做任何系统调用、不重绘。
    /// 定时器在整个插件生命周期内只建一个，避免反复开关窗口时泄漏。
    /// </summary>
    private void StartPanelHeartbeat()
    {
        if (_panel == null) return;
        if (Interlocked.Exchange(ref _panelTimer, new System.Threading.Timer(
            _ =>
            {
                try
                {
                    var panel = _panel;
                    if (panel != null && panel.NeedsTicks) panel.Tick();
                }
                catch
                {
                    // 心跳绝不能把宿主拖下水：任何异常都吞掉（下一次心跳还会照常跑）
                }
            }, null, PanelHeartbeatMs, PanelHeartbeatMs)) != null)
        {
            return;   // 已经建过了（面板可能被开合多次，只保留一个定时器）
        }
    }

    /// <summary>心跳周期（毫秒）。30Hz 足够让方向键长按连跳跟手，也不至于白耗 CPU。</summary>
    private const int PanelHeartbeatMs = 33;

    // ====================================================================
    // IBridgePanelHost：在线文档（配置教程 / 更新日志）
    // ====================================================================

    /// <summary>
    /// 用默认浏览器打开**官网的配置教程页**（<see cref="PluginUpdate.GuidePageUrl"/>）。
    ///
    /// ★ 为什么由宿主（本类）做、而不是面板自己做 ★
    /// 起进程唤起浏览器是有副作用的系统动作，且要写日志（用户得知道打开了哪个网址、失败为什么）。
    /// 面板不持有日志与进程知识，因此这里承担全部实际动作，面板只上报「用户点了」这一件事。
    ///
    /// ★ 为什么不再导出本地文件 ★
    /// 2026-10-09 起教程与更新日志统一维护在**网站仓库**（`ShiguangMeng-NPS-Plugins-Website/docs`），
    /// 插件只提供入口。两个直接收益：文档改动**不必再发一次插件**；
    /// 也不会再出现「dll 里内嵌的副本是旧的」这类问题（内嵌与相应的缓存比对已整体退役）。
    ///
    /// 打开失败**不抛异常** —— <see cref="PluginUpdate.OpenInBrowser"/> 已把任何问题收敛为可读字符串，
    /// 这里只负责写进日志（用户点完能确认到底打开了什么、失败原因是什么）。
    /// </summary>
    void IBridgePanelHost.OpenTutorial()
    {
        string error = PluginUpdate.OpenInBrowser(PluginUpdate.GuidePageUrl);
        _log.Add(error.Length == 0
            ? $"已在浏览器打开配置教程：{PluginUpdate.GuidePageUrl}"
            : $"打开配置教程失败：{error}");
    }

    /// <summary>
    /// 用默认浏览器打开**官网的更新日志页**（<see cref="PluginUpdate.ChangelogPageUrl"/>）。
    /// 与 <see cref="IBridgePanelHost.OpenTutorial"/> 同一套做法（同一个站点、同一个理由）。
    /// </summary>
    void IBridgePanelHost.OpenChangelog()
    {
        string error = PluginUpdate.OpenInBrowser(PluginUpdate.ChangelogPageUrl);
        _log.Add(error.Length == 0
            ? $"已在浏览器打开更新日志：{PluginUpdate.ChangelogPageUrl}"
            : $"打开更新日志失败：{error}");
    }


    // ====================================================================
    // 图标映射（IBridgePanelHost）
    // ====================================================================

    /// <summary>当前生效的自定义映射原文，供配置界面编辑。</summary>
    string IBridgePanelHost.IconOverridesRaw()
    {
        lock (_gate) return _iconOverrides.Raw;
    }

    /// <summary>
    /// 保存自定义映射。
    ///
    /// ★ 写盘后不需要重启服务 ★
    /// 服务持有的是 <c>() =&gt; _iconOverrides</c> 这个**委托**（见 <see cref="StartService"/>），
    /// 每次转发都会实时读取，因此下一条消息就生效 —— 与白名单同样的机制。
    /// </summary>
    void IBridgePanelHost.SaveIconOverrides(string raw)
    {
        var parsed = IconOverrides.Parse(raw);
        lock (_gate) _iconOverrides = parsed;

        _host?.SetSetting(KeyIconOverrides, raw);
        _log.Add($"图标映射已保存：自定义 {parsed.Count} 条（内置 {IconCatalog.BuiltinCount} 条键仍然生效）");
        InvalidateSurfaces();
    }

    // ====================================================================
    // 应用黑名单（IBridgePanelHost）
    // ====================================================================

    /// <summary>当前生效的应用黑名单原文，供配置界面的「黑名单」页编辑。</summary>
    string IBridgePanelHost.AppBlacklistRaw()
    {
        lock (_gate) return _appBlacklistRaw;
    }

    /// <summary>
    /// 保存应用黑名单（一行一个应用名或包名）。
    ///
    /// ★ 写盘后不需要重启服务 ★
    /// 服务持有的是 <c>() =&gt; _appBlacklist</c> 这个**委托**（见 <see cref="StartService"/>），
    /// 每条消息都会实时读取，因此下一次推送就按新名单过滤 —— 与白名单、图标映射同一套机制。
    ///
    /// 日志里点出「几条规则、含几条通配」：通配比精确更容易误伤，
    /// 用户看到条数才能确认自己写的那几条确实生效了（而不是被静默丢弃）。
    /// </summary>
    void IBridgePanelHost.SaveAppBlacklist(string raw)
    {
        var parsed = AppBlacklist.Parse(raw);
        lock (_gate)
        {
            _appBlacklistRaw = raw;
            _appBlacklist = parsed;
        }

        _host?.SetSetting(KeyAppBlacklist, raw);
        _log.Add(parsed.IsEmpty
            ? "应用黑名单已清空：不再拦截任何应用"
            : $"应用黑名单已保存：{parsed.Describe()}");
        InvalidateSurfaces();
    }

    /// <summary>
    /// 服务端日志的出口。
    ///
    /// 「转发失败」这类**低频但必须远程取证**的行，除了进内存日志，还镜像一份到宿主 app.log。
    /// 理由：用户报「转发失败」时内存日志读不到，而 app.log 里带时间戳，
    /// 能与宿主自己的「<c>[HTTP接口] 收到发送端消息推送</c>」逐条对照 ——
    /// 「请求压根没到宿主」还是「宿主收到了却回错」一眼就能分开。
    /// ✅ 推送成功的高频行**不**镜像，否则会把 app.log 刷爆（见 <see cref="BridgeLog"/> 的取舍说明）。
    /// </summary>
    private void LogFromServer(string message)
    {
        _log.Add(message);
        if (message.StartsWith("转发失败", StringComparison.Ordinal))
            _log.AddHostOnly(message);
    }

    // ====================================================================
    // 验证码：点击提醒即复制
    // ====================================================================
    /// <summary>
    /// 把一条含验证码的消息改由**插件提醒**发出，并给这条提醒挂上「点击即复制」。
    ///
    /// ★ 为什么不能继续走 HTTP 转发 ★
    /// 宿主 <c>/api/activities/</c> 建的是纯 <c>ToastData</c>，**不读 <c>onClick</c>**，
    /// 插件永远拿不到那次点击；而 <c>PostReminder</c> 的 <c>OnClick</c> 会被宿主原样存进
    /// <c>ToastData</c> 并在点击时执行。也就是说「点击复制」与「沿用原来的通知形态」
    /// 二者只能选一个 —— 这里选前者。
    ///
    /// ★ 为什么给 20 秒 ★
    /// 宿主允许 1~60 秒。默认 4 秒对「看清验证码 → 点击」太短（短信往往还有一行有效期说明）；
    /// 20 秒足够从容，又不会长时间占着灵动岛的提醒位。
    /// </summary>
    private void OnVerificationCode(PushActivity activity, string code)
    {
        var host = _host;
        if (host == null) return;

        // 图标沿用普通消息的补全规则（别名零成本、链接会联网），保证同一应用两种通道图标一致
        string icon = activity.Icon;
        if (icon.Length == 0)
        {
            IconOverrides overrides;
            lock (_gate) overrides = _iconOverrides;
            var decision = IconCatalog.Decide(activity.Kind, "", overrides);
            if (decision.HasIcon) icon = decision.Icon;
        }

        host.PostReminder(new ReminderData
        {
            Source = activity.Kind,                       // 左上角标明是哪个应用来的
            Title = activity.Title,
            Body = $"{activity.Subtitle}\n\n点一下复制验证码 {code}",
            Duration = TimeSpan.FromSeconds(20),
            IconPath = icon.Length > 0 ? icon : null,
            OnClick = () => CopyVerificationCode(code, activity.Kind),
        });

        _log.Add($"验证码消息已用提醒发出（{activity.Kind}）：{code} · 20 秒内点击提醒可复制");
    }

    /// <summary>
    /// 点击验证码提醒后的动作：复制到剪贴板。
    ///
    /// ★ 必须放到后台线程 ★
    /// 这个回调在**宿主的 UI 线程**上执行（点击派发发生在 NotchWindow 的消息处理里），
    /// 而写剪贴板要起 STA 线程并最多 Join 3 秒 —— 直接在 UI 线程里做会把灵动岛卡住。
    /// </summary>
    private void CopyVerificationCode(string code, string appName)
    {
        _ = Task.Run(() =>
        {
            if (TrySetClipboard(code))
            {
                _log.Add($"已复制验证码 {code}（{appName}）");
                return;
            }

            _log.Add($"复制验证码 {code} 失败：剪贴板被其它程序占用" +
                     "（若有剪贴板历史类插件，请检查它 OpenClipboard 后是否漏了 CloseClipboard）");
        });
    }

    // ====================================================================
    // 内部工具
    // ====================================================================

    private (long Received, long Forwarded, long Rejected, long Failed) CountersLocked()
        => _server is { } server
            ? (server.Received, server.Forwarded, server.Rejected, server.Failed)
            : (_idleReceived, _idleForwarded, _idleRejected, _idleFailed);

    private void InvalidateSurfaces()
    {
        // 设置页由宿主按自己的节奏重绘，插件无法主动请求；这里刷新自有窗口
        try { _configWindow?.RequestRedraw(); } catch { /* 窗口可能已销毁 */ }
    }

    private static bool IsPrivate(string ip)
        => ip.StartsWith("192.168.", StringComparison.Ordinal)
        || ip.StartsWith("10.", StringComparison.Ordinal)
        || Is172Private(ip);

    private static bool Is172Private(string ip)
    {
        if (!ip.StartsWith("172.", StringComparison.Ordinal)) return false;
        var parts = ip.Split('.');
        return parts.Length > 1 && int.TryParse(parts[1], out int second) && second is >= 16 and <= 31;
    }
}
