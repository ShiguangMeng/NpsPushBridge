using NpsPushBridge;

namespace BridgeHarness;

/// <summary>
/// 更新提示交互规则的验证（纯逻辑，不依赖宿主、不依赖网络、不依赖剪贴板）。
///
/// ★ 本组用例守住的核心契约 ★
///   「点击提示 → 打开更新界面 / 复制更新信息」必须在**插件侧**成立。
/// 背景：宿主 <c>PluginHost.PostReminder</c> 会把 <c>ReminderData.OnClick</c> 整条丢弃 ——
/// 它不读该字段，<c>ToastData</c> 里也没有回调字段；所有 toast 的点击都被
/// <c>Program.cs</c> 绑死到 <c>AppActivator.active_app(CurrentToast.Aumid)</c>，
/// 而插件提醒的 Aumid 是空串，于是点击必然无反应（这就是「点了弹窗没反应」的根因）。
/// 宿主源码不可改，因此提示条改由插件自建组件承担点击，规则就落在本文件钉住的这些函数里：
///   ① 什么时候该显示提示条（ShouldShowBanner）；
///   ② 点了之后打开 / 复制哪个地址（ResolveClickTarget）；
///   ③ 复制到剪贴板的内容长什么样（UpdateSummary）；
///   ④ 旧版注册表设置能否平滑迁移（ParseMode / ParseClickAction）。
/// </summary>
internal static class UpdateNoticeTests
{
    public static (int Passed, int Failed) Run()
    {
        Console.WriteLine();
        Console.WriteLine("【更新提示交互规则】");

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

        // 三种典型结果：有新版本 / 已是最新 / 检查失败
        var available = new UpdateCheckResult(
            UpdateStatus.UpdateAvailable, "1.2.0", "1.3.0",
            PluginUpdate.MarketPageUrl, "发现新版本", "https://nps.georgewu.top/pkgs/NpsPushBridge.zip");
        var availableNoDownload = new UpdateCheckResult(
            UpdateStatus.UpdateAvailable, "1.2.0", "1.3.0", PluginUpdate.MarketPageUrl, "发现新版本");
        var upToDate = new UpdateCheckResult(
            UpdateStatus.UpToDate, "1.3.0", "1.3.0", PluginUpdate.MarketPageUrl, "已是最新");
        var checkFailed = new UpdateCheckResult(
            UpdateStatus.Failed, "1.2.0", "", PluginUpdate.MarketPageUrl, "网络错误");

        // ── 1. 什么时候该显示提示条 ────────────────────────────────────
        Check("有新版本 + 可点击 → 显示",
            UpdateNotice.ShouldShowBanner(available, UpdateBannerMode.Clickable, ""));
        Check("有新版本 + 只显示 → 显示",
            UpdateNotice.ShouldShowBanner(available, UpdateBannerMode.AutoDismiss, ""));
        Check("有新版本 + 不提示 → 不显示",
            !UpdateNotice.ShouldShowBanner(available, UpdateBannerMode.Off, ""));

        Check("已是最新 → 不显示（无论什么模式）",
            !UpdateNotice.ShouldShowBanner(upToDate, UpdateBannerMode.Clickable, "")
            && !UpdateNotice.ShouldShowBanner(upToDate, UpdateBannerMode.AutoDismiss, ""));
        Check("检查失败 → 不显示（失败不该当成「有更新」打扰用户）",
            !UpdateNotice.ShouldShowBanner(checkFailed, UpdateBannerMode.Clickable, ""));

        // ── 2. 同一版本只提示一次（去重）──────────────────────────────
        Check("已被用户点掉的同一版本 → 不再显示",
            !UpdateNotice.ShouldShowBanner(available, UpdateBannerMode.Clickable, "1.3.0"));

        Check("★ 「1.3」与「1.3.0」视为同一版本（不会因写法差异反复弹）",
            !UpdateNotice.ShouldShowBanner(available, UpdateBannerMode.Clickable, "1.3"),
            "dismissed=1.3, remote=1.3.0");

        Check("★ 市场出现更高的 1.4.0 → 重新显示（去重不会把真更新挡掉）",
            UpdateNotice.ShouldShowBanner(
                available with { RemoteVersion = "1.4.0" }, UpdateBannerMode.Clickable, "1.3.0"));

        Check("去重记录比当前远端更高（如降级场景）→ 不显示",
            !UpdateNotice.ShouldShowBanner(available, UpdateBannerMode.Clickable, "9.9.9"));

        // ── 2b. ★ 自动检查与手动检查必须表现一致（本次修复的核心契约）★ ──────
        // 两者都「先弹吐司、再留提示条」。差异只允许有一条：自动检查对同一版本
        // 只弹一次吐司（防骚扰），手动检查每次都给反馈。
        //
        // ★ 关键不变式：吐司记忆（_toastedVersion）绝不影响提示条能否上岛 ★
        // 若这两个记忆被合成一个，自动检查一弹吐司就会让提示条永远上不了岛 ——
        // 那正是「自动检测不像手动检查那样提示」的老 bug。
        Check("★ 已弹过吐司 ≠ 提示条不再显示（两个记忆必须彼此独立）",
            UpdateNotice.ShouldToast(available, "1.3.0") == false
            && UpdateNotice.ShouldShowBanner(available, UpdateBannerMode.Clickable, "") == true,
            "toasted=1.3.0 时吐司应闭嘴、提示条仍应上岛");
        Check("★ 吐司去重按语义化版本比较（1.3 与 1.3.0 是同一版本）",
            !UpdateNotice.ShouldToast(available, "1.3"),
            "toasted=1.3, remote=1.3.0");
        Check("★ 市场出现更高版本 → 吐司重新可弹（去重不挡真更新）",
            UpdateNotice.ShouldToast(available with { RemoteVersion = "1.4.0" }, "1.3.0"));
        Check("没有新版本时不弹吐司（与提示条判定一致）",
            !UpdateNotice.ShouldToast(upToDate, "")
            && !UpdateNotice.ShouldToast(checkFailed, ""));
        Check("未弹过的版本 → 允许弹吐司",
            UpdateNotice.ShouldToast(available, ""));

        // ── 3. 点击后是否执行动作 ──────────────────────────────────────
        Check("可点击模式 → 执行动作", UpdateNotice.ShouldRunClickAction(UpdateBannerMode.Clickable));
        Check("只显示模式 → 不执行动作（纯告知，防误触）",
            !UpdateNotice.ShouldRunClickAction(UpdateBannerMode.AutoDismiss));
        Check("不提示模式 → 不执行动作", !UpdateNotice.ShouldRunClickAction(UpdateBannerMode.Off));

        // ── 4. ★ 点击后打开哪个地址（这是「能打开更新界面」的关键）★ ──
        Check("默认动作（打开下载页）→ 插件市场页（一定有内容，最稳）",
            UpdateNotice.ResolveClickTarget(available, UpdateClickAction.Market) == PluginUpdate.MarketPageUrl,
            UpdateNotice.ResolveClickTarget(available, UpdateClickAction.Market));

        Check("下载更新包 + 有直链 → 打开 .zip 直链",
            UpdateNotice.ResolveClickTarget(available, UpdateClickAction.Download)
                .EndsWith("NpsPushBridge.zip", StringComparison.Ordinal),
            UpdateNotice.ResolveClickTarget(available, UpdateClickAction.Download));

        Check("★ 下载更新包 + 无直链 → 退回市场页（绝不产生死按钮）",
            UpdateNotice.ResolveClickTarget(availableNoDownload, UpdateClickAction.Download) == PluginUpdate.MarketPageUrl,
            UpdateNotice.ResolveClickTarget(availableNoDownload, UpdateClickAction.Download));

        Check("复制到剪贴板 → 复制的是「更新信息」而不是一个裸链接",
            UpdateNotice.ResolveClickTarget(available, UpdateClickAction.Clipboard).Contains("最新版本"),
            UpdateNotice.ResolveClickTarget(available, UpdateClickAction.Clipboard));

        // ── 5. 复制内容必须包含用户真正需要的四项 ─────────────────────
        var summary = UpdateNotice.UpdateSummary(available);
        Check("复制内容含当前版本", summary.Contains("v1.2.0"), summary);
        Check("复制内容含最新版本", summary.Contains("v1.3.0"), summary);
        Check("复制内容含市场页（用户能自己去逛）", summary.Contains(PluginUpdate.MarketPageUrl), summary);
        Check("复制内容含下载直链", summary.Contains("NpsPushBridge.zip"), summary);
        Check("复制内容多行（贴到聊天窗口里可读）", summary.Contains(Environment.NewLine), summary);

        var summaryNoDownload = UpdateNotice.UpdateSummary(availableNoDownload);
        Check("无下载直链时，复制内容用市场页兜底而不是留空",
            summaryNoDownload.Contains(PluginUpdate.MarketPageUrl)
            && !summaryNoDownload.Contains("下载地址：\r\n")
            && !summaryNoDownload.Contains("下载地址：\n"),
            summaryNoDownload);

        // ── 6. 模式 / 动作的解析与旧配置迁移 ───────────────────────────
        // 全新安装（新旧键都没有）→ 默认「可点击」+「打开下载页」
        Check("全新安装 → 默认可点击", UpdateNotice.ParseMode("", "") == UpdateBannerMode.Clickable);
        Check("全新安装 → 默认打开下载页",
            UpdateNotice.ParseClickAction("") == UpdateClickAction.Market);

        Check("正常解析三种模式",
            UpdateNotice.ParseMode("0", "") == UpdateBannerMode.Off
            && UpdateNotice.ParseMode("1", "") == UpdateBannerMode.AutoDismiss
            && UpdateNotice.ParseMode("2", "") == UpdateBannerMode.Clickable);

        Check("★ 旧配置（只有 updateAction 键）迁移后仍为「可点击」",
            UpdateNotice.ParseMode("", "0") == UpdateBannerMode.Clickable
            && UpdateNotice.ParseMode("", "1") == UpdateBannerMode.Clickable);

        Check("★ 旧配置的「复制链接」（updateAction=1）迁移为「复制到剪贴板」",
            UpdateNotice.ParseClickAction("1") == UpdateClickAction.Clipboard);
        Check("★ 旧配置的「打开浏览器」（updateAction=0）迁移为「打开下载页」",
            UpdateNotice.ParseClickAction("0") == UpdateClickAction.Market);

        Check("脏数据（越界 / 非数字）→ 回落到默认值而不是抛异常",
            UpdateNotice.ParseMode("99", "") == UpdateBannerMode.Clickable
            && UpdateNotice.ParseMode("abc", "") == UpdateBannerMode.Clickable
            && UpdateNotice.ParseClickAction("77") == UpdateClickAction.Market
            && UpdateNotice.ParseClickAction("xyz") == UpdateClickAction.Market);

        // ── 9. ★ 收起提示条后不得留下空白（本次修复）★ ──────────────────
        // 宿主为插件行分配宽度是「按组件所需宽度 + 16px 间距」逐个累加
        // （Renderer.RecomputePluginVisibility 的 used += PLUGIN_GAP + widths[i]），
        // 并且组件宽度按注册表版本缓存。于是「组件已不再绘制、宽度却还按非零值预留」
        // 就会在岛上留下一个 90×岛高 的纯空白块 —— 这就是点击提示条后的空白。
        // 契约：组件一旦作废（MeasureWidth 返回 0），宿主重测后该组件对布局的贡献必须恰好为 0。
        Check("★ 作废的提示条不得占用任何宽度（否则岛上留空白）",
            UpdateNotice.RetiredWidthContribution == 0f,
            UpdateNotice.RetiredWidthContribution.ToString());
        Check("★ 未作废的提示条宽度必须为正（否则它永远不会显示）",
            UpdateNotice.ActiveWidthContribution > 0f,
            UpdateNotice.ActiveWidthContribution.ToString());

        // ── 10. ★ 判定 → 注册的衔接：本次「弹窗弹了、提示条没上岛」的回归防线 ★ ──
        // 病灶：注册路径上曾有一道 30 秒冷却守卫，命中时直接 return 且不写日志，
        // 而吐司没有这道守卫 —— 于是判定为真、注册却被静默吞掉，两者行为分叉。
        // 下面把「从无到有必须注册」与「判定了就该可见」钉成可回归的契约。
        Check("★ 判定要显示且岛上没有组件 → 必须注册（不得被任何节流拦下）",
            UpdateNotice.MustRegisterNow(shouldShow: true, alreadyRegistered: false),
            "这是「弹窗弹了、岛上空着」的直接防线");
        Check("★ 判定要显示且已有组件 → 不重复注册（幂等，避免刷爆宿主重建快照）",
            !UpdateNotice.MustRegisterNow(shouldShow: true, alreadyRegistered: true));
        Check("判定不要显示 → 任何情况都不注册",
            !UpdateNotice.MustRegisterNow(shouldShow: false, alreadyRegistered: false)
            && !UpdateNotice.MustRegisterNow(shouldShow: false, alreadyRegistered: true));

        // 「判定为真」⇒「可见性为真」：一旦注册成功，提示条就应当被认定为已显示。
        // 注意可见性只认进程内的组件引用，**与「已弹过吐司」这类记忆无关** ——
        // 否则自动检查一弹吐司，用户就会看到「弹窗弹了、岛上一直空着」。
        Check("★ 组件已注册 → 提示条判定为可见（与吐司记忆无关）",
            UpdateNotice.BannerIsVisible(widgetRegistered: true)
            && !UpdateNotice.BannerIsVisible(widgetRegistered: false));

        // 组合场景复现：自动检查发现新版本、吐司刚弹过、用户没点掉过任何东西。
        // 期望 = 提示条必须上岛（吐司记忆不该压制它），且吐司不再重复弹（防骚扰）。
        Check("★ 复现用户场景：吐司已弹过 + 用户未忽略 → 提示条仍必须上岛",
            UpdateNotice.ShouldToast(available, "1.3.0") == false
            && UpdateNotice.ShouldShowBanner(available, UpdateBannerMode.Clickable, "") == true
            && UpdateNotice.MustRegisterNow(
                UpdateNotice.ShouldShowBanner(available, UpdateBannerMode.Clickable, ""),
                alreadyRegistered: false),
            "toasted=1.3.0, dismissed=(空)");

        // 吐司时长必须短于「用户会觉得没反应」的量级：它期间提示条被完全压住。
        Check("★ 吐司时长收敛到 3 秒以内（吐司期间提示条被整行隐藏）",
            UpdateNotice.ToastDuration <= TimeSpan.FromSeconds(3),
            UpdateNotice.ToastDuration.TotalSeconds.ToString());

        Check("模式读写往返一致",
            UpdateNotice.ParseMode(UpdateNotice.ModeToRaw(UpdateBannerMode.AutoDismiss), "")
                == UpdateBannerMode.AutoDismiss);        Check("动作读写往返一致",
            UpdateNotice.ParseClickAction(UpdateNotice.ClickActionToRaw(UpdateClickAction.Download))
                == UpdateClickAction.Download);

        // ── 7. 通知文案不再承诺做不到的事 ─────────────────────────────
        var toastBody = UpdateNotice.ToastBody(available, UpdateBannerMode.Clickable);
        Check("★ 通知文案不再写「点击打开下载页」（宿主不消费 OnClick，那是做不到的承诺）",
            !toastBody.Contains("点击打开下载页"), toastBody);
        Check("通知文案把用户引导到真正能点的提示条上",
            toastBody.Contains("提示条"), toastBody);
        Check("「不提示」模式下不把用户往岛上引",
            !UpdateNotice.ToastBody(available, UpdateBannerMode.Off).Contains("提示条"),
            UpdateNotice.ToastBody(available, UpdateBannerMode.Off));
        Check("通知标题带新版本号",
            UpdateNotice.ToastTitle(available).Contains("1.3.0"), UpdateNotice.ToastTitle(available));

        // ── 8. 下载直链识别（避免把市场页当成安装包）──────────────────
        Check("识别 .zip 直链", PluginUpdate.ResolveDownloadUrl("https://x/p/NpsPushBridge.zip").Length > 0);
        Check("识别相对路径直链（补成绝对地址）",
            PluginUpdate.ResolveDownloadUrl("/pkgs/a.zip") == "https://nps.georgewu.top/pkgs/a.zip",
            PluginUpdate.ResolveDownloadUrl("/pkgs/a.zip"));
        Check("★ 市场页自身不算下载直链（否则「下载更新包」只是打开网页）",
            PluginUpdate.ResolveDownloadUrl(PluginUpdate.MarketPageUrl) == "",
            PluginUpdate.ResolveDownloadUrl(PluginUpdate.MarketPageUrl));
        Check("空值 / 非 http 协议 → 判为非直链",
            PluginUpdate.ResolveDownloadUrl("") == ""
            && PluginUpdate.ResolveDownloadUrl("ftp://x/a.zip") == "");

        // ── 9. ★ 预算自适应：提示条在「原生模块偏宽」时不许整帧消失 ──────────
        // 病灶：宿主按注册顺序贪心放行组件（used + 16 + width ≤ 预算），提示条排在状态灯之后。
        // 组合模式 + 时钟/硬件/媒体全开时，原生一行就吃掉 600~800px（满额 800），
        // 留给提示条的不足百像素 —— 完整文案「↑ 有新版本 v1.2.0」装不下就被**整帧跳过**，
        // 用户看到的就是「弹窗弹了、岛上一直没提示」。以下用例把「退到短文案」这条退路钉住。

        // 用「真实量级」的两个宽度做候选，而不是拿 ActiveWidthContribution 那个下界常量 ——
        // 那个常量只钉「正性」，两条文案在它下面会算出同一个数（都是 31），
        // 用它做候选会让「退到短版」这条路径永远测不到。
        float full = 101f;      // 「↑ 有新版本 v1.2.0」实测量级
        float compact = 55f;    // 「↑ 更新 v1.2.0」实测量级

        Check("★ 候选文案顺序：完整版在前、短版在后（优先级不可颠倒）",
            UpdateNotice.BannerTextCandidates.Length == 2
            && UpdateNotice.BannerTextCandidates[0] == UpdateNotice.BannerText
            && UpdateNotice.BannerTextCandidates[1] == UpdateNotice.BannerTextCompact,
            string.Join(",", UpdateNotice.BannerTextCandidates));

        Check("短文案严格短于完整文案（否则「退一步」没有意义）",
            UpdateNotice.BannerTextCompact.Length < UpdateNotice.BannerText.Length,
            $"{UpdateNotice.BannerTextCompact.Length} vs {UpdateNotice.BannerText.Length}");

        // 预算充裕 → 选完整版（下标 0）
        Check("★ 预算充裕 → 选完整文案",
            UpdateNotice.PickWidthCandidate(400f, new[] { full, compact }) == 0);

        // 预算只够短版 → 退到下标 1（而不是放弃）
        Check("★ 预算只够短版 → 退到短文案（提示条仍留在岛上，不再整帧消失）",
            UpdateNotice.PickWidthCandidate(UpdateNotice.HostPluginGap + compact + 1f,
                new[] { full, compact }) == 1,
            $"预算 {UpdateNotice.HostPluginGap + compact + 1f:F0}");

        // 预算连短版都放不下 → 放弃（-1）
        Check("预算连短版都放不下 → 返回 -1（放弃，交由调用方返回 0 宽度）",
            UpdateNotice.PickWidthCandidate(UpdateNotice.HostPluginGap + compact - 1f,
                new[] { full, compact }) == -1);

        // 边界：恰好等于（含 16px 间距）必须放行 —— 与宿主 `<= budget` 严格对齐
        Check("★ 边界取 ≤：预算恰好等于「间距 + 宽度」时必须放行（差一个等号就会消失）",
            UpdateNotice.PickWidthCandidate(UpdateNotice.HostPluginGap + compact,
                new[] { float.MaxValue, compact }) == 1);

        // 宿主尚未算过（首帧 +∞）→ 一律放行第一个，避免首帧闪空
        Check("★ 预算为 +∞（宿主首帧尚未计算）→ 放行完整文案，不允许首帧闪空",
            UpdateNotice.PickWidthCandidate(float.PositiveInfinity, new[] { full, compact }) == 0);

        Check("预算为负 / NaN → 一律不放行（防御性，宿主亦按 0 处理）",
            UpdateNotice.PickWidthCandidate(-1f, new[] { full }) == -1
            && UpdateNotice.PickWidthCandidate(float.NaN, new[] { full }) == -1);

        Check("宽度 ≤ 0 的候选被跳过（与宿主 widths[i] > 0f 对齐）",
            UpdateNotice.PickWidthCandidate(1000f, new[] { 0f, compact }) == 1);

        Check("空候选列表 → -1（无候选可放行）",
            UpdateNotice.PickWidthCandidate(1000f, Array.Empty<float>()) == -1);

        Check("★ HostPluginGap 必须与宿主 Renderer.PLUGIN_GAP 一致（16px）",
            Math.Abs(UpdateNotice.HostPluginGap - 16f) < 0.001f,
            UpdateNotice.HostPluginGap.ToString());

        Check("BannerFits：有候选下标 = 放行，-1 = 放弃",
            UpdateNotice.BannerFits(0) && UpdateNotice.BannerFits(1) && !UpdateNotice.BannerFits(-1));

        // ── 10. ★ 复现用户现场：组合模式下提示条必须能退让求生 ──────────────
        // 现场实测（2026-09-21）：CompositeMode_Enabled=1、时钟+硬件+媒体全开、
        // 媒体块约 350px → 原生总宽 ≈672px → 插件行预算 ≈128px；
        // 状态灯占 16+19=35px → 提示条只剩 ≈93px，而完整文案要 ≈101px。
        float lightCost = UpdateNotice.HostPluginGap + 19f;      // StatusWidget 实测宽 19px
        float budgetAtMedia350 = 800f - 672f;
        float leftForBanner = budgetAtMedia350 - lightCost;
        var widths = new[] { full, compact };
        int pickInField = UpdateNotice.PickWidthCandidate(leftForBanner, widths);

        Check($"★ 复现现场（预算 {budgetAtMedia350:F0} − 灯 {lightCost:F0} = {leftForBanner:F0}px）："
            + "提示条不得整帧消失",
            pickInField >= 0,
            $"pick={pickInField}, 完整需 {UpdateNotice.HostPluginGap + full:F0}px");

        // 同一现场下：完整文案确实放不下（这正是用户报「没上岛」的那个数），短文案必须放得下
        Check("★ 同一现场：完整文案放不下、短文案放得下 —— 修复的确切收益点",
            UpdateNotice.PickWidthCandidate(leftForBanner, new[] { full }) == -1
            && UpdateNotice.PickWidthCandidate(leftForBanner, new[] { compact }) == 0,
            $"剩余 {leftForBanner:F0}px");

        return (passed, failed);
    }
}
