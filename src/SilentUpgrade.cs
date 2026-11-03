using System.Globalization;

namespace NpsPushBridge;

/// <summary>
/// 静默升级的执行阶段。
///
/// ★ 为什么要有这个「门（gate）」而不是一个 bool ★
/// 「静默升级」要在后台依次做四件可能失败的事：下载 → 校验 → 落盘 → 等待宿主重载。
/// 任何一步失败都必须能停下来、说清楚卡在哪、并且**不能把插件本体弄坏**。
/// 用一个显式状态机把「现在允许做什么」写清楚，比散落的几个布尔量可靠得多 ——
/// 也让测试可以逐条断言迁移规则（见 tests/BridgeHarness/SilentUpgradeTests.cs）。
/// </summary>
internal enum UpgradeStage
{
    /// <summary>空闲：没有正在进行的升级。</summary>
    Idle = 0,

    /// <summary>正在检查市场版本。</summary>
    Checking = 1,

    /// <summary>正在下载安装包。</summary>
    Downloading = 2,

    /// <summary>正在校验安装包（结构 / 必需文件 / 版本号）。</summary>
    Verifying = 3,

    /// <summary>正在落盘（备份旧版本 → 原子替换 dll）。</summary>
    Installing = 4,

    /// <summary>安装包已就位，等待宿主重新加载插件使其生效。</summary>
    PendingReload = 5,

    /// <summary>升级成功结束（宿主已重载，或已提示用户重载）。</summary>
    Succeeded = 6,

    /// <summary>本次升级失败（已回退，旧版本仍可用）。</summary>
    Failed = 7,

    /// <summary>被前置条件挡住，不会重试（例如市场没有提供下载直链）。</summary>
    Blocked = 8,
}

/// <summary>
/// 静默升级的**纯逻辑**部分：开关解析、状态机迁移、退避重试、进度与状态文案。
///
/// 与 <see cref="UpdateNotice"/> 一样刻意不依赖宿主 API / 网络 / 文件系统，
/// 从而能被测试工程直接编译进去做断言 —— 「什么时候会重试、失败几次放弃」
/// 这种最容易写错又最难手工复现的规则，必须有可回归的规格。
/// </summary>
internal static class SilentUpgrade
{
    // ====================================================================
    // 开关：默认值与持久化
    // ====================================================================

    /// <summary>
    /// 开关的默认值。
    ///
    /// ★ 刻意默认「关」★ 静默升级会**自动下载并替换插件文件**，属于对用户机器的写操作。
    /// 本插件的既有约定是「任何会改变本机状态的自动行为都要用户显式开启」
    /// （参考 autostart 虽然默认开，但那只是开一个本地监听端口，不写文件、不改程序）。
    /// 因此这里默认关闭：用户不动它，行为与升级前完全一致（只提示、不自动装）。
    /// </summary>
    public const bool DefaultEnabled = false;

    /// <summary>持久化取值：开。</summary>
    public const string EnabledRaw = "1";

    /// <summary>持久化取值：关。</summary>
    public const string DisabledRaw = "0";

    /// <summary>解析持久化的开关值。认不出来一律回落默认值（关）。</summary>
    public static bool ParseEnabled(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return DefaultEnabled;
        return raw.Trim() switch
        {
            "1" or "true" or "True" or "TRUE" or "on" or "On" or "yes" => true,
            "0" or "false" or "False" or "FALSE" or "off" or "Off" or "no" => false,
            _ => DefaultEnabled,      // 脏数据不猜，落回默认
        };
    }

    public static string EnabledToRaw(bool enabled) => enabled ? EnabledRaw : DisabledRaw;

    /// <summary>
    /// 开关的一行说明（配置界面显示，让用户明白打开后会发生什么）。
    ///
    /// ★ 关于长度 ★ 这一行右侧紧挨着「开启 / 关闭 / 立即升级」三个按钮，420px 宽的面板里
    /// 留给文字的空间约 198px。原先开启态的措辞「自动下载并安装新版本（失败会自动回退到当前版本）」
    /// 需要 264px，会被截成省略号，用户看不到「会回退」这个最要紧的保障。
    /// 因此压到 198px 以内：保留「自动 / 失败回退」两个关键信息，去掉赘字。
    /// 「重启宿主后生效」的信息由旁边的升级状态行承载，不必在这里重复。
    /// </summary>
    public static string SwitchDescribe(bool enabled)
        => enabled
            ? "自动安装，失败回退"
            : "只提示不自动安装";

    // ====================================================================
    // 触发时机与频率
    // ====================================================================

    /// <summary>自动检查间隔（与更新检查共用同一次结果，不额外发请求）。</summary>
    public static readonly TimeSpan CheckInterval = TimeSpan.FromHours(6);

    /// <summary>启动后首次检查的延迟：避开启动瞬间的渲染高峰。</summary>
    public static readonly TimeSpan FirstCheckDelay = TimeSpan.FromSeconds(20);

    /// <summary>失败后的重试次数上限（不含首次尝试）。</summary>
    public const int MaxRetries = 3;

    /// <summary>单次下载的超时时间。</summary>
    public static readonly TimeSpan DownloadTimeout = TimeSpan.FromSeconds(60);

    /// <summary>
    /// 第 <paramref name="attempt"/> 次失败后，隔多久再试（attempt 从 1 开始）。
    ///
    /// 指数退避：1 分钟 → 5 分钟 → 15 分钟，然后放弃（下一次窗口在 6 小时后的常规检查）。
    /// 为什么不一直重试：升级失败的常见原因是网络不通 / 站点不可达，
    /// 密集重试只会持续占用带宽与日志，对用户毫无帮助 —— 退到常规检查节奏即可。
    /// </summary>
    public static TimeSpan RetryDelay(int attempt) => attempt switch
    {
        <= 1 => TimeSpan.FromMinutes(1),
        2 => TimeSpan.FromMinutes(5),
        _ => TimeSpan.FromMinutes(15),
    };

    /// <summary>该次失败是否还应该重试。</summary>
    public static bool ShouldRetry(int failureCount) => failureCount < MaxRetries;

    // ====================================================================
    // 状态机
    // ====================================================================

    /// <summary>从 <paramref name="from"/> 能否迁移到 <paramref name="to"/>。</summary>
    public static bool CanTransition(UpgradeStage from, UpgradeStage to) => (from, to) switch
    {
        (UpgradeStage.Idle, UpgradeStage.Checking) => true,
        (UpgradeStage.Checking, UpgradeStage.Idle) => true,          // 无新版本
        (UpgradeStage.Checking, UpgradeStage.Blocked) => true,       // 没有下载直链
        (UpgradeStage.Checking, UpgradeStage.Downloading) => true,
        (UpgradeStage.Downloading, UpgradeStage.Verifying) => true,
        (UpgradeStage.Downloading, UpgradeStage.Failed) => true,
        (UpgradeStage.Verifying, UpgradeStage.Installing) => true,
        (UpgradeStage.Verifying, UpgradeStage.Failed) => true,
        (UpgradeStage.Installing, UpgradeStage.PendingReload) => true,
        (UpgradeStage.Installing, UpgradeStage.Failed) => true,
        // 任何活动态都可以被失败中断（下载中断 / 用户关掉开关 / 进程退出前的兜底）
        (UpgradeStage.Checking, UpgradeStage.Failed) => true,
        // 终态 → 重新开始（下一轮重试或下一次常规检查）
        (UpgradeStage.Failed, UpgradeStage.Checking) => true,
        (UpgradeStage.Failed, UpgradeStage.Idle) => true,
        (UpgradeStage.Blocked, UpgradeStage.Checking) => true,       // 站点补上直链后仍要能再试
        (UpgradeStage.PendingReload, UpgradeStage.Succeeded) => true,
        (UpgradeStage.PendingReload, UpgradeStage.Failed) => true,   // 重载后版本没变上来 → 算失败
        (UpgradeStage.Succeeded, UpgradeStage.Idle) => true,
        _ => false,
    };

    /// <summary>该状态是否表示「有活正在干」（用于禁用界面按钮 / 防重入）。</summary>
    public static bool IsBusy(UpgradeStage stage) => stage is
        UpgradeStage.Checking or UpgradeStage.Downloading
        or UpgradeStage.Verifying or UpgradeStage.Installing;

    /// <summary>
    /// 该状态是否是「本轮已尘埃落定」——不会有自动推进，也不会再自动重装。
    ///
    /// ⚠️ Succeeded **不**算终态 ⚠ 它只表示"新文件已就位、结果也报给用户了"，
    /// 但宿主还没重载，本地跑的仍是旧代码。这时如果把它当作"完事"而放行再次安装，
    /// 就会出现「每 6 小时重装一次同一个版本」的无意义写盘 —— 所以它必须继续拦住重入。
    /// 只有 <see cref="Idle"/>（用户关开关 / 手动复位）和 <see cref="Failed"/>（该重试了）
    /// 才重新放行，<see cref="Blocked"/> 也放行是为了让站点补上直链后还能再试一次。
    /// </summary>
    public static bool IsTerminal(UpgradeStage stage) => stage is
        UpgradeStage.Idle or UpgradeStage.Failed or UpgradeStage.Blocked;

    /// <summary>该状态是否表示「这一轮的升级已经完成（成功落盘）」，用于界面文案与日志。</summary>
    public static bool IsSettled(UpgradeStage stage) => stage is
        UpgradeStage.PendingReload or UpgradeStage.Succeeded;

    // ====================================================================
    // 触发判定
    // ====================================================================

    /// <summary>
    /// 这次更新检查的结果是否应该触发静默升级。
    ///
    /// 三个条件缺一不可：
    ///   1. 开关是开的；
    ///   2. 确实发现了新版本；
    ///   3. 市场记录里给了**可用的安装包直链** —— 没有直链就无从"静默"，
    ///      这时降级为「提示」而不是报错（<see cref="UpgradeStage.Blocked"/> 只用于日志/界面说明）。
    /// 另外，同一个远端版本只尝试一次：装完还是这个版本（说明包有问题）就不再循环重试下载。
    /// 状态必须是"可以开始新一轮"的（见 <see cref="IsTerminal"/>）。
    /// </summary>
    public static bool ShouldAutoInstall(UpdateCheckResult result, bool enabled,
        string? attemptedVersion, UpgradeStage stage)
    {
        if (!enabled) return false;
        if (!IsTerminal(stage)) return false;                 // 已有升级在跑 / 已装好待重载，不重入
        if (!result.HasUpdate) return false;
        if (result.RemoteVersion.Length == 0) return false;
        if (string.IsNullOrWhiteSpace(result.DownloadUrl)) return false;

        // 已经为这个版本尝试过 → 不重复（避免"下载-失败-下载"死循环）
        if (!string.IsNullOrEmpty(attemptedVersion)
            && PluginUpdate.CompareVersion(result.RemoteVersion, attemptedVersion) <= 0)
            return false;

        return true;
    }

    /// <summary>有更新但没有可用直链 —— 界面上一句人话解释为什么没有静默安装。</summary>
    public static bool IsBlockedByMissingDownload(UpdateCheckResult result, bool enabled)
        => enabled && result.HasUpdate && string.IsNullOrWhiteSpace(result.DownloadUrl);

    // ====================================================================
    // 进度与状态文案
    // ====================================================================

    /// <summary>阶段名（配置界面左侧那一小行用）。</summary>
    public static string StageLabel(UpgradeStage stage) => stage switch
    {
        UpgradeStage.Idle => "待命",
        UpgradeStage.Checking => "检查中",
        UpgradeStage.Downloading => "下载中",
        UpgradeStage.Verifying => "校验中",
        UpgradeStage.Installing => "安装中",
        UpgradeStage.PendingReload => "待重载",
        UpgradeStage.Succeeded => "已完成",
        UpgradeStage.Failed => "失败",
        _ => "已跳过",
    };

    /// <summary>
    /// 下载进度的百分比（0–100）。总长未知时返回 -1 —— 界面据此改成「已下载 x.x MB」
    /// 这种不带百分比的文案，而不是画一个永远不动的 0%。
    /// </summary>
    public static int Percent(long received, long total)
        => total <= 0 ? -1 : (int)Math.Clamp(received * 100 / total, 0, 100);

    /// <summary>把字节数格式化成人看的大小（B / KB / MB）。</summary>
    public static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024L * 1024L) return $"{(bytes / 1024.0).ToString("0.#", CultureInfo.InvariantCulture)} KB";
        return $"{(bytes / (1024.0 * 1024.0)).ToString("0.##", CultureInfo.InvariantCulture)} MB";
    }

    /// <summary>
    /// 配置界面里显示的「静默升级」一整行说明。
    ///
    /// 这条文案要同时回答用户四个问题：现在在做什么、做到哪一步、上次结果如何、为什么没在自动装。
    /// 因此按状态分派，每个分支都写成人话，不暴露内部枚举名。
    /// </summary>
    public static string Describe(bool enabled, UpgradeStage stage, long received, long total,
        int failureCount, string lastError, UpdateCheckResult result)
    {
        if (!enabled)
            return "未开启（只提示不自动安装）";

        switch (stage)
        {
            case UpgradeStage.Checking:
                return "正在检查市场版本…";

            case UpgradeStage.Downloading:
            {
                int percent = Percent(received, total);
                return percent >= 0
                    ? $"正在下载 v{result.RemoteVersion} · {percent}%（{FormatSize(received)} / {FormatSize(total)}）"
                    : $"正在下载 v{result.RemoteVersion} · 已下载 {FormatSize(received)}";
            }

            case UpgradeStage.Verifying:
                return $"正在校验安装包（v{result.RemoteVersion}）…";

            case UpgradeStage.Installing:
                return $"正在安装 v{result.RemoteVersion}（已备份当前版本）…";

            case UpgradeStage.PendingReload:
                // 用「重载」而不是「重新加载」：插件中心那个按钮上写的就是「重载」，
                // 文案里用词一致，用户才知道要去点哪里。
                return $"v{result.RemoteVersion} 已就位 —— 到插件中心点「重载」即刻生效";

            case UpgradeStage.Succeeded:
                return $"已升级到 v{result.RemoteVersion}（在插件中心点「重载」后生效）";

            case UpgradeStage.Failed:
            {
                var reason = lastError.Length > 0 ? lastError : "未知原因";
                if (ShouldRetry(failureCount))
                {
                    var wait = RetryDelay(failureCount + 1);
                    return $"升级失败（第 {failureCount} 次）：{reason} · "
                         + $"将于 {FormatDelay(wait)} 后重试";
                }
                return $"升级失败（已达 {MaxRetries} 次上限）：{reason} · 已回退到 v{PluginUpdate.LocalVersion}";
            }

            case UpgradeStage.Blocked:
                return "市场未提供安装包直链，无法静默安装 —— 可点「打开下载页」手动下载";

            default:
                if (IsBlockedByMissingDownload(result, enabled))
                    return "市场未提供安装包直链，无法静默安装 —— 可点「打开下载页」手动下载";
                return result.HasUpdate
                    ? $"有新版本 v{result.RemoteVersion}，等待下一轮自动安装"
                    : "已是最新，无需升级";
        }
    }

    /// <summary>把时长写成人话（1 分钟 / 5 分钟 / 15 分钟）。</summary>
    public static string FormatDelay(TimeSpan delay)
        => delay.TotalMinutes < 1
            ? $"{Math.Max(1, (int)delay.TotalSeconds)} 秒"
            : $"{(int)delay.TotalMinutes} 分钟";

    /// <summary>灵动岛 / Toast 用的一句话状态（简短，不带原因）。</summary>
    public static string ShortStatus(bool enabled, UpgradeStage stage, int percent)
        => stage switch
        {
            UpgradeStage.Downloading when percent >= 0 => $"正在下载更新 {percent}%",
            UpgradeStage.Downloading => "正在下载更新",
            UpgradeStage.Verifying => "正在校验安装包",
            UpgradeStage.Installing => "正在安装更新",
            UpgradeStage.PendingReload => "更新已就位，待重载",
            UpgradeStage.Succeeded => "更新已完成",
            UpgradeStage.Failed => "更新失败，已回退",
            _ => enabled ? "静默升级已开启" : "静默升级未开启",
        };

    // ====================================================================
    // 回退
    // ====================================================================

    /// <summary>
    /// 安装失败时是否应该回退。
    ///
    /// 规则：**只要「替换 dll」这一步动过手，就必须回退**。
    /// 下载 / 校验阶段的失败发生在临时目录里，插件本体一个字节都没改，回退是多余动作；
    /// 而替换阶段可能只完成了「删旧、写新」的一半（进程被杀 / 磁盘满），
    /// 这时如果放着不管，插件目录里可能就是一个不完整或缺失的 dll —— 必须用备份盖回去。
    /// </summary>
    public static bool ShouldRollback(UpgradeStage stage) => stage == UpgradeStage.Installing;

    /// <summary>回退结果的说明文案。</summary>
    public static string RollbackDescribe(bool rolledBack, string localVersion)
        => rolledBack
            ? $"已回退到 v{localVersion}，插件可继续正常使用"
            : "未改动任何文件，仍是 v" + localVersion;
}
