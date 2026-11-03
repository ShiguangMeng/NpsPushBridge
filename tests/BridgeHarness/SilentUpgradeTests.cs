using NpsPushBridge;
using SkiaSharp;

namespace BridgeHarness;

/// <summary>
/// 一键静默升级规则的验证（纯逻辑，不联网、不写文件、不依赖宿主）。
///
/// ★ 本组用例守住的核心契约 ★
/// 「打开开关 → 后台自动下载并安装，失败自动回退、限次重试」这条链路里，
/// 最容易写错又最难手工复现的几处规则：
///   ① 开关的**默认值必须是关**（自动改本机文件，不能让用户"没动过就被升级"）；
///   ② 触发条件（开关 / 有新版本 / 有下载直链 / 该版本未试过 / 无升级在进行）；
///   ③ 状态机合法迁移（尤其是"替换阶段失败必须回退"）；
///   ④ 退避重试的次数与间隔（不能无限重试，也不能一次就放弃）；
///   ⑤ 进度与状态文案（不能承诺做不到的事，失败要说清停在哪一步）。
///
/// 真正的文件操作（下载 / 解压 / 替换 / 回退）由
/// <see cref="NpsPushBridge.SilentUpgrader"/> 承担，那部分在端到端用例里用真实临时文件验证。
/// </summary>
internal static class SilentUpgradeTests
{
    public static (int Passed, int Failed) Run()
    {
        Console.WriteLine();
        Console.WriteLine("【一键静默升级规则】");

        int passed = 0, failed = 0;

        void Check(string name, bool condition, string detail = "")
        {
            if (condition)
            {
                passed++;
                Console.WriteLine($"  [PASS] {name}");
            }
            else
            {
                failed++;
                Console.WriteLine($"  [FAIL] {name}{(detail.Length == 0 ? "" : $"  →  {detail}")}");
            }
        }

        // 三种典型检查结果
        var ready = new UpdateCheckResult(
            UpdateStatus.UpdateAvailable, "1.2.0", "1.3.0",
            PluginUpdate.MarketPageUrl, "发现新版本", "https://nps.georgewu.top/pkgs/NpsPushBridge.zip");
        var noLink = new UpdateCheckResult(
            UpdateStatus.UpdateAvailable, "1.2.0", "1.3.0", PluginUpdate.MarketPageUrl, "发现新版本");
        var upToDate = new UpdateCheckResult(
            UpdateStatus.UpToDate, "1.3.0", "1.3.0", PluginUpdate.MarketPageUrl, "已是最新");
        var checkFailed = new UpdateCheckResult(
            UpdateStatus.Failed, "1.2.0", "", PluginUpdate.MarketPageUrl, "网络错误");

        // ── 1. 开关默认值与持久化 ──────────────────────────────────────
        Check("开关默认关闭（不解析到任何值时）", !SilentUpgrade.ParseEnabled(""));
        Check("开关默认关闭（null）", !SilentUpgrade.ParseEnabled(null));
        Check("开关默认关闭（空白）", !SilentUpgrade.ParseEnabled("   "));
        Check("默认值常量本身就是关", !SilentUpgrade.DefaultEnabled);

        Check("解析 \"1\" → 开", SilentUpgrade.ParseEnabled("1"));
        Check("解析 \"0\" → 关", !SilentUpgrade.ParseEnabled("0"));
        Check("解析 \"true\"/\"false\"（兼容手写配置）",
            SilentUpgrade.ParseEnabled("true") && !SilentUpgrade.ParseEnabled("false"));
        Check("解析 \"on\"/\"off\"", SilentUpgrade.ParseEnabled("on") && !SilentUpgrade.ParseEnabled("off"));
        Check("解析带空白的 \" 1 \" → 开", SilentUpgrade.ParseEnabled(" 1 "));
        Check("脏数据不猜，落回默认（关）", !SilentUpgrade.ParseEnabled("yes-please-maybe"));
        Check("序列化往返：开 → \"1\" → 开", SilentUpgrade.ParseEnabled(SilentUpgrade.EnabledToRaw(true)));
        Check("序列化往返：关 → \"0\" → 关", !SilentUpgrade.ParseEnabled(SilentUpgrade.EnabledToRaw(false)));

        // ★ 文案断言 ★ 这两条测的是「说明是否传达了关键含义」，不是逐字比对 ——
        // 所以断言的是关键词而非整句，措辞调整（例如为了在窄面板里放得下而压缩长度）不会误报，
        // 但只要把「只提示」或「自动安装」的含义弄丢，立刻会红。
        Check("关闭时的说明文案明确说「只提示、不自动安装」",
            SilentUpgrade.SwitchDescribe(false).Contains("只提示", StringComparison.Ordinal)
            && SilentUpgrade.SwitchDescribe(false).Contains("不自动安装", StringComparison.Ordinal));
        Check("开启时的说明文案提到会自动安装与失败回退",
            SilentUpgrade.SwitchDescribe(true).Contains("自动安装", StringComparison.Ordinal)
            && SilentUpgrade.SwitchDescribe(true).Contains("回退", StringComparison.Ordinal));

        // ★ 长度上限 ★ 这一行右侧紧挨着三个按钮，420px 面板里只有约 198px 可用；
        // 一旦文案变长就会被截成省略号，用户看不到「失败会回退」这个关键保障。
        // 用真实字体度量把它钉住，并**留 12px 余量** —— 恰好贴边（实测过 198.00 == 198.00）
        // 太脆：换个字体或字号立刻截断，所以门槛设在 186px 而不是 198px。
        using (var probe = new SKPaint { Typeface = SKTypeface.FromFamilyName("Microsoft YaHei UI"), TextSize = 11f })
        {
            foreach (var on in new[] { true, false })
            {
                string s = "一键静默升级：" + SilentUpgrade.SwitchDescribe(on);
                float w = probe.MeasureText(s);
                Check($"说明文案（{(on ? "开启" : "关闭")}）在 420px 面板里有余量（实测 {w:F1}px ≤ 186px）", w <= 186f);
            }
        }

        // ── 2. 触发条件 ───────────────────────────────────────────────
        Check("开+有新版本+有直链+未试过+空闲 → 自动安装",
            SilentUpgrade.ShouldAutoInstall(ready, true, "", UpgradeStage.Idle));
        Check("关开关 → 不自动安装",
            !SilentUpgrade.ShouldAutoInstall(ready, false, "", UpgradeStage.Idle));
        Check("已是最新 → 不自动安装",
            !SilentUpgrade.ShouldAutoInstall(upToDate, true, "", UpgradeStage.Idle));
        Check("检查失败 → 不自动安装",
            !SilentUpgrade.ShouldAutoInstall(checkFailed, true, "", UpgradeStage.Idle));
        Check("有更新但市场没给直链 → 不自动安装（降级为提示）",
            !SilentUpgrade.ShouldAutoInstall(noLink, true, "", UpgradeStage.Idle));

        Check("同一个版本试过一次 → 不再试（避免下载死循环）",
            !SilentUpgrade.ShouldAutoInstall(ready, true, "1.3.0", UpgradeStage.Idle));
        Check("试过 1.3.0 后市场出了 1.4.0 → 可以再试",
            SilentUpgrade.ShouldAutoInstall(
                ready with { RemoteVersion = "1.4.0" }, true, "1.3.0", UpgradeStage.Idle));
        Check("空白试过版本视作没试过", SilentUpgrade.ShouldAutoInstall(ready, true, "  ", UpgradeStage.Idle));

        Check("升级进行中（下载）→ 不重入",
            !SilentUpgrade.ShouldAutoInstall(ready, true, "", UpgradeStage.Downloading));
        Check("升级进行中（校验）→ 不重入",
            !SilentUpgrade.ShouldAutoInstall(ready, true, "", UpgradeStage.Verifying));
        Check("升级进行中（安装）→ 不重入",
            !SilentUpgrade.ShouldAutoInstall(ready, true, "", UpgradeStage.Installing));
        Check("上一次失败后 → 允许重试",
            SilentUpgrade.ShouldAutoInstall(ready, true, "", UpgradeStage.Failed));
        Check("待重载状态 → 不再重复安装",
            !SilentUpgrade.ShouldAutoInstall(ready, true, "", UpgradeStage.PendingReload));
        Check("已完成状态 → 不再重复安装（宿主还没重载，重复装是白写盘）",
            !SilentUpgrade.ShouldAutoInstall(ready, true, "", UpgradeStage.Succeeded));

        Check("有更新但无直链 → 标记为 Blocked 场景",
            SilentUpgrade.IsBlockedByMissingDownload(noLink, true));
        Check("关开关时不报 Blocked（避免无意义的提示）",
            !SilentUpgrade.IsBlockedByMissingDownload(noLink, false));
        Check("一直有直链时不报 Blocked", !SilentUpgrade.IsBlockedByMissingDownload(ready, true));

        // ── 3. 状态机迁移 ─────────────────────────────────────────────
        Check("Idle → Checking", SilentUpgrade.CanTransition(UpgradeStage.Idle, UpgradeStage.Checking));
        Check("Checking → Downloading", SilentUpgrade.CanTransition(UpgradeStage.Checking, UpgradeStage.Downloading));
        Check("Checking → Idle（没有新版本时回到空闲）",
            SilentUpgrade.CanTransition(UpgradeStage.Checking, UpgradeStage.Idle));
        Check("Checking → Blocked（市场没直链）",
            SilentUpgrade.CanTransition(UpgradeStage.Checking, UpgradeStage.Blocked));
        Check("Downloading → Verifying", SilentUpgrade.CanTransition(UpgradeStage.Downloading, UpgradeStage.Verifying));
        Check("Downloading → Failed（下载中断）",
            SilentUpgrade.CanTransition(UpgradeStage.Downloading, UpgradeStage.Failed));
        Check("Verifying → Installing", SilentUpgrade.CanTransition(UpgradeStage.Verifying, UpgradeStage.Installing));
        Check("Verifying → Failed（校验不过）",
            SilentUpgrade.CanTransition(UpgradeStage.Verifying, UpgradeStage.Failed));
        Check("Installing → PendingReload",
            SilentUpgrade.CanTransition(UpgradeStage.Installing, UpgradeStage.PendingReload));
        Check("Installing → Failed（写盘失败）",
            SilentUpgrade.CanTransition(UpgradeStage.Installing, UpgradeStage.Failed));
        Check("PendingReload → Succeeded",
            SilentUpgrade.CanTransition(UpgradeStage.PendingReload, UpgradeStage.Succeeded));
        Check("Failed → Checking（重试）", SilentUpgrade.CanTransition(UpgradeStage.Failed, UpgradeStage.Checking));
        Check("Blocked → Checking（站点补上直链后仍能再试）",
            SilentUpgrade.CanTransition(UpgradeStage.Blocked, UpgradeStage.Checking));

        Check("禁止 Idle 直接跳到 Installing（必须先检查）",
            !SilentUpgrade.CanTransition(UpgradeStage.Idle, UpgradeStage.Installing));
        Check("禁止 Downloading 直接跳到 Installing（必须先校验）",
            !SilentUpgrade.CanTransition(UpgradeStage.Downloading, UpgradeStage.Installing));
        Check("禁止 Checking 直接跳到 Succeeded",
            !SilentUpgrade.CanTransition(UpgradeStage.Checking, UpgradeStage.Succeeded));
        Check("禁止 Succeeded 跳回 Downloading",
            !SilentUpgrade.CanTransition(UpgradeStage.Succeeded, UpgradeStage.Downloading));

        Check("进行中状态就是「忙」（下载 / 校验 / 安装 / 检查）",
            SilentUpgrade.IsBusy(UpgradeStage.Checking) && SilentUpgrade.IsBusy(UpgradeStage.Downloading)
            && SilentUpgrade.IsBusy(UpgradeStage.Verifying) && SilentUpgrade.IsBusy(UpgradeStage.Installing));
        Check("Idle / Succeeded / Failed / Blocked 都不算忙",
            !SilentUpgrade.IsBusy(UpgradeStage.Idle) && !SilentUpgrade.IsBusy(UpgradeStage.Succeeded)
            && !SilentUpgrade.IsBusy(UpgradeStage.Failed) && !SilentUpgrade.IsBusy(UpgradeStage.Blocked));
        Check("终态（可重新开始新一轮）：Idle / Failed / Blocked",
            SilentUpgrade.IsTerminal(UpgradeStage.Idle) && SilentUpgrade.IsTerminal(UpgradeStage.Failed)
            && SilentUpgrade.IsTerminal(UpgradeStage.Blocked));
        Check("非终态：Checking / Downloading / Verifying / Installing",
            !SilentUpgrade.IsTerminal(UpgradeStage.Checking) && !SilentUpgrade.IsTerminal(UpgradeStage.Downloading)
            && !SilentUpgrade.IsTerminal(UpgradeStage.Verifying) && !SilentUpgrade.IsTerminal(UpgradeStage.Installing));
        Check("Succeeded 不算终态（否则会每轮重装同一个版本）",
            !SilentUpgrade.IsTerminal(UpgradeStage.Succeeded));
        Check("PendingReload 不算终态", !SilentUpgrade.IsTerminal(UpgradeStage.PendingReload));

        Check("已落定状态：PendingReload / Succeeded",
            SilentUpgrade.IsSettled(UpgradeStage.PendingReload) && SilentUpgrade.IsSettled(UpgradeStage.Succeeded));
        Check("进行中状态不算「已落定」",
            !SilentUpgrade.IsSettled(UpgradeStage.Downloading) && !SilentUpgrade.IsSettled(UpgradeStage.Failed));

        // ── 4. 退避重试 ───────────────────────────────────────────────
        Check("失败 0 次（刚失败第一次）→ 还要重试", SilentUpgrade.ShouldRetry(1));
        Check("失败 1 次 → 还要重试", SilentUpgrade.ShouldRetry(1));
        Check("失败 2 次 → 还要重试", SilentUpgrade.ShouldRetry(2));
        Check("失败 3 次（达上限）→ 不再重试", !SilentUpgrade.ShouldRetry(3));
        Check("失败 4 次 → 不再重试（上限之外一律停）", !SilentUpgrade.ShouldRetry(4));

        Check("第 1 次重试等 1 分钟", SilentUpgrade.RetryDelay(1) == TimeSpan.FromMinutes(1),
            SilentUpgrade.RetryDelay(1).ToString());
        Check("第 2 次重试等 5 分钟", SilentUpgrade.RetryDelay(2) == TimeSpan.FromMinutes(5),
            SilentUpgrade.RetryDelay(2).ToString());
        Check("第 3 次重试等 15 分钟", SilentUpgrade.RetryDelay(3) == TimeSpan.FromMinutes(15),
            SilentUpgrade.RetryDelay(3).ToString());
        Check("退避间隔严格递增（不会越重试越急）",
            SilentUpgrade.RetryDelay(1) < SilentUpgrade.RetryDelay(2)
            && SilentUpgrade.RetryDelay(2) < SilentUpgrade.RetryDelay(3));
        Check("重试上限与退避档位数一致（每一次重试都有确定的等待）",
            SilentUpgrade.MaxRetries >= 1 && SilentUpgrade.RetryDelay(SilentUpgrade.MaxRetries) > TimeSpan.Zero);

        // ── 5. 进度计算与格式化 ───────────────────────────────────────
        Check("进度：0/100 → 0%", SilentUpgrade.Percent(0, 100) == 0);
        Check("进度：50/100 → 50%", SilentUpgrade.Percent(50, 100) == 50);
        Check("进度：100/100 → 100%", SilentUpgrade.Percent(100, 100) == 100);
        Check("进度：超出总长时夹到 100%（下载流偶有多算）", SilentUpgrade.Percent(120, 100) == 100);
        Check("总长未知 → 返回 -1（界面据此不画百分比）", SilentUpgrade.Percent(500, -1) == -1);
        Check("总长为 0 → 返回 -1（同样不画百分比）", SilentUpgrade.Percent(0, 0) == -1);

        Check("大小格式化：字节", SilentUpgrade.FormatSize(512) == "512 B", SilentUpgrade.FormatSize(512));
        Check("大小格式化：KB", SilentUpgrade.FormatSize(2048) == "2 KB", SilentUpgrade.FormatSize(2048));
        Check("大小格式化：MB 带两位小数",
            SilentUpgrade.FormatSize(3L * 1024 * 1024 / 2).Contains("1.5", StringComparison.Ordinal),
            SilentUpgrade.FormatSize(3L * 1024 * 1024 / 2));

        Check("时长格式化：分钟", SilentUpgrade.FormatDelay(TimeSpan.FromMinutes(15)) == "15 分钟",
            SilentUpgrade.FormatDelay(TimeSpan.FromMinutes(15)));
        Check("时长格式化：不足一分钟按秒",
            SilentUpgrade.FormatDelay(TimeSpan.FromSeconds(30)).Contains("秒", StringComparison.Ordinal));

        // ── 6. 状态文案 ───────────────────────────────────────────────
        Check("未开启时的文案说清「未开启」",
            SilentUpgrade.Describe(false, UpgradeStage.Idle, 0, -1, 0, "", ready).Contains("未开启", StringComparison.Ordinal));

        Check("下载中且有总长 → 文案含百分比",
            SilentUpgrade.Describe(true, UpgradeStage.Downloading, 50, 100, 0, "", ready).Contains("50%", StringComparison.Ordinal),
            SilentUpgrade.Describe(true, UpgradeStage.Downloading, 50, 100, 0, "", ready));
        Check("下载中但总长未知 → 文案退化成已下载量（不出现 -1%）",
            !SilentUpgrade.Describe(true, UpgradeStage.Downloading, 4096, -1, 0, "", ready).Contains("-1", StringComparison.Ordinal),
            SilentUpgrade.Describe(true, UpgradeStage.Downloading, 4096, -1, 0, "", ready));

        Check("校验中的文案提到「校验」",
            SilentUpgrade.Describe(true, UpgradeStage.Verifying, 0, -1, 0, "", ready).Contains("校验", StringComparison.Ordinal));
        Check("安装中的文案提到「安装」",
            SilentUpgrade.Describe(true, UpgradeStage.Installing, 0, -1, 0, "", ready).Contains("安装", StringComparison.Ordinal));
        Check("待重载的文案告诉用户要重载才生效",
            SilentUpgrade.Describe(true, UpgradeStage.PendingReload, 0, -1, 0, "", ready).Contains("重载", StringComparison.Ordinal));

        var failWithRetry = SilentUpgrade.Describe(true, UpgradeStage.Failed, 0, -1, 1, "网络错误", ready);
        Check("失败且还能重试 → 文案给出重试等待时间",
            failWithRetry.Contains("网络错误", StringComparison.Ordinal)
            && failWithRetry.Contains("重试", StringComparison.Ordinal), failWithRetry);

        var failFinal = SilentUpgrade.Describe(true, UpgradeStage.Failed, 0, -1, 3, "网络错误", ready);
        Check("失败达上限 → 文案说明已回退到当前版本",
            failFinal.Contains("回退", StringComparison.Ordinal), failFinal);
        Check("失败达上限 → 文案不再承诺还会重试",
            !failFinal.Contains("后将", StringComparison.Ordinal), failFinal);

        Check("Blocked 文案说明没有直链、可手动下载",
            SilentUpgrade.Describe(true, UpgradeStage.Blocked, 0, -1, 0, "", noLink).Contains("直链", StringComparison.Ordinal),
            SilentUpgrade.Describe(true, UpgradeStage.Blocked, 0, -1, 0, "", noLink));
        Check("空闲且有直链 → 提示等待下一轮自动安装",
            SilentUpgrade.Describe(true, UpgradeStage.Idle, 0, -1, 0, "", ready).Contains("自动安装", StringComparison.Ordinal),
            SilentUpgrade.Describe(true, UpgradeStage.Idle, 0, -1, 0, "", ready));
        Check("空闲且已是最新 → 说「已是最新」",
            SilentUpgrade.Describe(true, UpgradeStage.Idle, 0, -1, 0, "", upToDate).Contains("已是最新", StringComparison.Ordinal));

        Check("阶段名可读（不含枚举名）",
            SilentUpgrade.StageLabel(UpgradeStage.Downloading) == "下载中"
            && SilentUpgrade.StageLabel(UpgradeStage.PendingReload) == "待重载");

        Check("短语状态：下载中带百分比",
            SilentUpgrade.ShortStatus(true, UpgradeStage.Downloading, 42).Contains("42%", StringComparison.Ordinal));
        Check("短语状态：失败 → 已回退",
            SilentUpgrade.ShortStatus(true, UpgradeStage.Failed, -1).Contains("回退", StringComparison.Ordinal));

        // ── 7. 回退判定 ───────────────────────────────────────────────
        Check("替换阶段失败 → 必须回退", SilentUpgrade.ShouldRollback(UpgradeStage.Installing));
        Check("下载阶段失败 → 不动文件，无需回退", !SilentUpgrade.ShouldRollback(UpgradeStage.Downloading));
        Check("校验阶段失败 → 不动文件，无需回退", !SilentUpgrade.ShouldRollback(UpgradeStage.Verifying));
        Check("检查阶段失败 → 无需回退", !SilentUpgrade.ShouldRollback(UpgradeStage.Checking));

        Check("回退成功 → 文案说已回退到当前版本",
            SilentUpgrade.RollbackDescribe(true, "1.2.0").Contains("回退", StringComparison.Ordinal));
        Check("未改动文件 → 文案说仍是当前版本",
            SilentUpgrade.RollbackDescribe(false, "1.2.0").Contains("1.2.0", StringComparison.Ordinal));

        // ── 8. 触发时机常量 ───────────────────────────────────────────
        Check("自动检查间隔为正且不超过 24 小时",
            SilentUpgrade.CheckInterval > TimeSpan.Zero && SilentUpgrade.CheckInterval <= TimeSpan.FromHours(24),
            SilentUpgrade.CheckInterval.ToString());
        Check("首次检查有延迟（不跟启动抢资源）",
            SilentUpgrade.FirstCheckDelay > TimeSpan.Zero, SilentUpgrade.FirstCheckDelay.ToString());
        Check("下载超时为正值且够宽裕",
            SilentUpgrade.DownloadTimeout >= TimeSpan.FromSeconds(30), SilentUpgrade.DownloadTimeout.ToString());

        return (passed, failed);
    }
}
