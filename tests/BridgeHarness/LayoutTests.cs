namespace BridgeHarness;

/// <summary>
/// 配置界面布局规则的验证：直接对 <see cref="NpsPushBridge.ConfigLayout"/> 施加不同窗口尺寸，
/// 断言需求里的三条尺寸约定确实成立 ——
///   ① 白名单框 / 端口行 / 保存按钮 / 开启服务按钮：高度为固定值；
///   ② 上述各行：宽度随窗口缩放，且存在最小宽度（窄于此值则按最小宽度布局并裁切）；
///   ③ 日志区域：宽高均可变（跟随剩余空间），并分别有最小宽度与最小高度。
/// </summary>
internal static class LayoutTests
{
    private static int _passed, _failed;

    public static (int Passed, int Failed) Run()
    {
        Console.WriteLine("【配置界面布局规则】");

        float[] widths = { 340f, 500f, 900f };
        foreach (var width in widths)
        {
            var layout = NpsPushBridge.ConfigLayout.Compute(width, 620f);
            float inner = width - 32f;   // 左右各 16pt 留白

            Check($"宽度 {width}: 白名单框高度固定 = 96", Near(layout.WhitelistBox.Height, 96f), layout.WhitelistBox.Height.ToString());
            Check($"宽度 {width}: 端口行高度固定 = 30", Near(layout.PortMinus10.Height, 30f) && Near(layout.PortField.Height, 30f), layout.PortField.Height.ToString());
            Check($"宽度 {width}: 保存按钮高度固定 = 30", Near(layout.SaveButton.Height, 30f), layout.SaveButton.Height.ToString());
            Check($"宽度 {width}: 开启服务按钮高度固定 = 30", Near(layout.ServiceButton.Height, 30f), layout.ServiceButton.Height.ToString());

            Check($"宽度 {width}: 白名单框宽度随窗口 = {inner}", Near(layout.WhitelistBox.Width, inner), layout.WhitelistBox.Width.ToString());
            Check($"宽度 {width}: 端口行随窗口占满内容宽", Near(layout.PortPlus10.Right, inner + 16f), layout.PortPlus10.Right.ToString());
            if (width > 340f)
                Check($"宽度 {width}: 端口输入框随窗口变宽", layout.PortField.Width > 156f, layout.PortField.Width.ToString());
            Check($"宽度 {width}: 保存/服务按钮等分宽度", Near(layout.SaveButton.Width, layout.ServiceButton.Width), $"{layout.SaveButton.Width} / {layout.ServiceButton.Width}");
            Check($"宽度 {width}: 图标映射按钮紧邻服务按钮右侧且右边界贴配置教程左侧",
                Near(layout.ServiceButton.Right + NpsPushBridge.ConfigLayout.Gap, layout.IconMapButton.Left)
                && Near(layout.IconMapButton.Right + NpsPushBridge.ConfigLayout.TinyGap, layout.TutorialButton.Left),
                $"{layout.ServiceButton.Right} → {layout.IconMapButton.Left} → {layout.TutorialButton.Left}");
            Check($"宽度 {width}: 配置教程按钮右边界贴内容右边界",
                Near(layout.TutorialButton.Right, layout.WhitelistBox.Right),
                $"{layout.TutorialButton.Right} vs {layout.WhitelistBox.Right}");
            Check($"宽度 {width}: 四个按钮在同一行（顶/底对齐）",
                Near(layout.SaveButton.Top, layout.TutorialButton.Top) && Near(layout.SaveButton.Bottom, layout.TutorialButton.Bottom)
                && Near(layout.IconMapButton.Top, layout.TutorialButton.Top) && Near(layout.IconMapButton.Bottom, layout.TutorialButton.Bottom));
            Check($"宽度 {width}: 日志区宽度随窗口 = {inner}", Near(layout.LogBox.Width, inner), layout.LogBox.Width.ToString());
        }

        // ② 最小宽度
        var narrow = NpsPushBridge.ConfigLayout.Compute(200f, 620f);
        Check("窄于最小宽度: 标记为已裁切", narrow.WidthClamped);
        Check("窄于最小宽度: 内容宽按最小值 340 布局", Near(narrow.ContentWidth, 340f), narrow.ContentWidth.ToString());
        Check("窄于最小宽度: 白名单框宽度 = 340-32 = 308", Near(narrow.WhitelistBox.Width, 308f), narrow.WhitelistBox.Width.ToString());
        // 按钮行现在有四段：保存 / 开启服务 / 图标映射 / 配置教程（后两个固定宽）。保存与服务仍等分「扣掉固定宽按钮后」的剩余宽度。
        // 308(内容宽) − 76(图标映射) − 76(教程) − 8(图标映射与教程之间) − 10(服务与图标映射之间) = 138 → 再扣一个 10 的间隙后对半分 = 64
        float expectedSave = (308f - NpsPushBridge.ConfigLayout.IconMapButtonWidth
            - NpsPushBridge.ConfigLayout.TutorialButtonWidth - NpsPushBridge.ConfigLayout.TinyGap
            - NpsPushBridge.ConfigLayout.Gap - NpsPushBridge.ConfigLayout.Gap) / 2f;
        Check($"窄于最小宽度: 保存按钮宽度 = {expectedSave}", Near(narrow.SaveButton.Width, expectedSave), narrow.SaveButton.Width.ToString());
        Check("窄于最小宽度: 配置教程按钮宽度固定",
            Near(narrow.TutorialButton.Width, NpsPushBridge.ConfigLayout.TutorialButtonWidth), narrow.TutorialButton.Width.ToString());
        Check("窄于最小宽度: 配置教程按钮右边界贴内容右边（右对齐收口）",
            Near(narrow.TutorialButton.Right, narrow.WhitelistBox.Right),
            $"{narrow.TutorialButton.Right} vs {narrow.WhitelistBox.Right}");
        Check("窄于最小宽度: 四个按钮互不重叠",
            narrow.SaveButton.Right <= narrow.ServiceButton.Left + 0.01f
            && narrow.ServiceButton.Right <= narrow.IconMapButton.Left + 0.01f
            && narrow.IconMapButton.Right <= narrow.TutorialButton.Left + 0.01f,
            $"{narrow.SaveButton.Right} / {narrow.ServiceButton.Left} / {narrow.ServiceButton.Right} / {narrow.IconMapButton.Left} / {narrow.IconMapButton.Right} / {narrow.TutorialButton.Left}");

        // ③ 日志区：高度可变 + 最小高度
        var atMin = NpsPushBridge.ConfigLayout.Compute(600f, NpsPushBridge.ConfigLayout.MinRecommendedHeight);
        Check("最小推荐高度下: 日志区正好等于最小高度 96", Near(atMin.LogBox.Height, 96f), atMin.LogBox.Height.ToString());

        var shorter = NpsPushBridge.ConfigLayout.Compute(600f, NpsPushBridge.ConfigLayout.MinRecommendedHeight - 100f);
        Check("窗口再矮也不小于日志最小高度", shorter.LogBox.Height >= 96f - 0.01f, shorter.LogBox.Height.ToString());

        var taller = NpsPushBridge.ConfigLayout.Compute(600f, NpsPushBridge.ConfigLayout.MinRecommendedHeight + 286f);
        Check("窗口变高: 日志区高度随之增大", taller.LogBox.Height > atMin.LogBox.Height);
        float delta = taller.LogBox.Height - atMin.LogBox.Height;
        Check($"窗口变高: 日志区吞掉全部增量 ({delta})", Near(delta, 286f), delta.ToString());
        Check("窗口变高: 日志区宽度不变（只吃高度）", Near(taller.LogBox.Width, atMin.LogBox.Width));

        // ④ 几何自洽
        const float padX = 16f;
        Check("所有区块左对齐于 16pt 留白",
            Near(taller.Title.Left, padX) && Near(taller.WhitelistBox.Left, padX)
            && Near(taller.PortField.Left - 76f, padX) && Near(taller.SaveButton.Left, padX) && Near(taller.LogBox.Left, padX));

        Check("日志区起点与 FixedTopHeight 常量精确一致",
            Near(taller.LogBox.Top, NpsPushBridge.ConfigLayout.FixedTopHeight),
            $"{taller.LogBox.Top} / {NpsPushBridge.ConfigLayout.FixedTopHeight}");

        Check("状态摘要行紧跟在标题之后",
            Near(taller.StatusSummary.Top, taller.Title.Bottom + 10f), $"{taller.StatusSummary.Top} / {taller.Title.Bottom}");

        Check("自上而下各固定区块互不重叠",
            taller.Title.Bottom <= taller.StatusSummary.Top + 0.01f
            && taller.StatusSummary.Bottom <= taller.WhitelistHeaderLabel.Top + 0.01f
            && taller.WhitelistHeaderLabel.Bottom <= taller.WhitelistBox.Top + 0.01f
            && taller.WhitelistBox.Bottom <= taller.PortHeader.Top + 0.01f
            && taller.PortHeader.Bottom <= taller.PortField.Top + 0.01f
            && taller.PortField.Bottom <= taller.SaveButton.Top + 0.01f
            && taller.SaveButton.Bottom <= taller.UpdateHeader.Top + 0.01f
            && taller.UpdateHeader.Bottom <= taller.UpdateModeRow.Top + 0.01f
            && taller.UpdateModeRow.Bottom <= taller.UpdateActionRow.Top + 0.01f
            && taller.UpdateActionRow.Bottom <= taller.UpgradeToggleRow.Top + 0.01f
            && taller.UpgradeToggleRow.Bottom <= taller.UpgradeStatusRow.Top + 0.01f
            && taller.UpgradeStatusRow.Bottom <= taller.LogHeader.Top + 0.01f
            && taller.LogHeader.Bottom <= taller.LogBox.Top + 0.01f);

        // ⑤ 更新区块（与其它行同样的尺寸约定：高度固定、宽度随窗口缩放）
        foreach (var width in widths)
        {
            var layout = NpsPushBridge.ConfigLayout.Compute(width, 620f);
            Check($"宽度 {width}: 更新状态行高度固定 = 22", Near(layout.UpdateHeader.Height, 22f), layout.UpdateHeader.Height.ToString());
            Check($"宽度 {width}: 提示模式行高度固定 = 18", Near(layout.UpdateModeRow.Height, 18f), layout.UpdateModeRow.Height.ToString());
            Check($"宽度 {width}: 更新动作行高度固定 = 18", Near(layout.UpdateActionRow.Height, 18f), layout.UpdateActionRow.Height.ToString());
            Check($"宽度 {width}: 更新三个按钮右对齐到内容右边",
                Near(layout.UpdateOpenButton.Right, layout.UpdateHeader.Right), layout.UpdateOpenButton.Right.ToString());
            Check($"宽度 {width}: 提示模式三个按钮右对齐到内容右边",
                Near(layout.UpdateModeClickButton.Right, layout.UpdateModeRow.Right), layout.UpdateModeClickButton.Right.ToString());
            Check($"宽度 {width}: 更新动作行三个按钮右对齐到内容右边",
                Near(layout.UpdateActionDownloadButton.Right, layout.UpdateActionRow.Right), layout.UpdateActionDownloadButton.Right.ToString());
            Check($"宽度 {width}: 更新区块宽度随窗口缩放 = {width - 32f}",
                Near(layout.UpdateHeader.Width, width - 32f), layout.UpdateHeader.Width.ToString());

            Check($"宽度 {width}: 静默升级行高度固定 = 18", Near(layout.UpgradeToggleRow.Height, 18f), layout.UpgradeToggleRow.Height.ToString());
            Check($"宽度 {width}: 升级进度行高度固定 = 18", Near(layout.UpgradeStatusRow.Height, 18f), layout.UpgradeStatusRow.Height.ToString());
            Check($"宽度 {width}: 静默升级「立即升级」右对齐到内容右边",
                Near(layout.UpgradeNowButton.Right, layout.UpgradeToggleRow.Right), layout.UpgradeNowButton.Right.ToString());
            Check($"宽度 {width}: 「开启 / 关闭」在「立即升级」左侧且不重叠",
                layout.UpgradeToggleOnButton.Right < layout.UpgradeNowButton.Left + 0.01f
                && layout.UpgradeToggleOnButton.Left < layout.UpgradeToggleOffButton.Left,
                $"{layout.UpgradeToggleOnButton.Left}~{layout.UpgradeToggleOnButton.Right} / "
                + $"{layout.UpgradeToggleOffButton.Left}~{layout.UpgradeToggleOffButton.Right}");
        }

        int lines = taller.VisibleLogLines;
        Check("日志区行数随高度变化且足够显示内容", lines >= 6, lines.ToString());

        RunInputBoxSection();

        return (_passed, _failed);
    }

    /// <summary>
    /// 输入框几何（2026-09-23 新增：白名单改为「每条一个输入框」，图标映射拆成「应用名 + 图标值」两框）。
    ///
    /// ★ 为什么这些断言值得单独写一节 ★
    /// 输入框是**手绘**的（宿主只提供画布），没有布局引擎替我们兜底：
    /// 一个矩形算错就会「框叠框」「删按钮压在框上」「最后一条永远滚不出来」，
    /// 而这些错在肉眼看截图时都像「样式问题」，很容易被当成正常。用数值钉住，改几何时必然报错。
    /// </summary>
    private static void RunInputBoxSection()
    {
        Console.WriteLine("  -- 输入框几何 --");

        // 真实窗口尺寸：660×620（见 ConfigPanelDefaults）
        var real = NpsPushBridge.ConfigLayout.Compute(660f, 620f);
        Check("真实窗口 660×620：日志区仍不小于最小高度 96",
            real.LogBox.Height >= 96f - 0.01f, real.LogBox.Height.ToString());
        Check("真实窗口 660×620：日志区起点与 FixedTopHeight 常量一致",
            Near(real.LogBox.Top, NpsPushBridge.ConfigLayout.FixedTopHeight),
            $"{real.LogBox.Top} / {NpsPushBridge.ConfigLayout.FixedTopHeight}");

        // ── 白名单 ──
        Check("白名单规则提示行在白名单标题与输入框区之间",
            real.WhitelistHeaderLabel.Bottom <= real.WhitelistHint.Top + 0.01f
            && real.WhitelistHint.Top < real.WhitelistHint.Bottom + 0.01f
            && real.WhitelistHint.Bottom <= real.WhitelistBox.Top + 0.01f);
        Check("白名单规则提示行高度 = LabelHeight 18",
            Near(real.WhitelistHint.Height, 18f), real.WhitelistHint.Height.ToString());
        Check("★ 白名单固定高度 96 里正好放得下 3 条输入框",
            real.VisibleWhitelistEntries == 3, real.VisibleWhitelistEntries.ToString());

        for (int i = 0; i < real.VisibleWhitelistEntries; i++)
        {
            var field = real.WhitelistEntryField(i);
            var remove = real.LineRemoveButton(i);

            Check($"白名单第 {i + 1} 条：输入框高度 = 24",
                Near(field.Height, NpsPushBridge.ConfigLayout.WhitelistEntryHeight), field.Height.ToString());
            Check($"白名单第 {i + 1} 条：输入框在框内（左右上下都不越界）",
                field.Left >= real.WhitelistBox.Left - 0.01f && field.Right <= real.WhitelistBox.Right + 0.01f
                && field.Top >= real.WhitelistBox.Top - 0.01f && field.Bottom <= real.WhitelistBox.Bottom + 0.01f,
                $"{field} in {real.WhitelistBox}");
            Check($"白名单第 {i + 1} 条：删除按钮不与输入框重叠（在它右侧）",
                remove.Left >= field.Right - 0.01f, $"{remove.Left} vs {field.Right}");

            // 下一条不能压到这一条上（pitch 必须 ≥ 条目高）
            var nextTop = real.WhitelistEntryTop(i + 1);
            Check($"白名单第 {i + 1}/{i + 2} 条之间不重叠",
                nextTop >= field.Bottom - 0.01f, $"{nextTop} vs {field.Bottom}");
        }

        Check("＋添加按钮与「＋最近来源 / 清空 / 上下翻」互不重叠（从左到右依次排开）",
            real.WhitelistAddButton.Left >= real.WhitelistHeaderLabel.Left
            && real.WhitelistAddButton.Right <= real.LastSourceButton.Left + 0.01f
            && real.LastSourceButton.Right <= real.ClearButton.Left + 0.01f
            && real.ClearButton.Right <= real.ScrollUpButton.Left + 0.01f
            && real.ScrollUpButton.Right <= real.ScrollDownButton.Left + 0.01f,
            $"{real.WhitelistAddButton.Right} / {real.LastSourceButton.Left}");

        // ── 图标映射 ──
        var map = NpsPushBridge.ConfigLayout.ComputeIconMap(660f);
        Check("★ 图标映射编辑框里放得下 5 条映射（每条是两个输入框）",
            map.VisibleLines == 5, map.VisibleLines.ToString());

        for (int i = 0; i < map.VisibleLines; i++)
        {
            var key = map.KeyFieldAt(i);
            var value = map.ValueFieldAt(i);
            var remove = map.RemoveButtonAt(i);

            Check($"映射第 {i + 1} 条：应用名框与图标值框不重叠（左右相接，中间留缝）",
                value.Left >= key.Right - 0.01f, $"{key} / {value}");
            Check($"映射第 {i + 1} 条：两个框都在编辑框内",
                key.Left >= map.Box.Left - 0.01f && value.Right <= map.Box.Right + 0.01f
                && key.Top >= map.Box.Top - 0.01f && key.Bottom <= map.Box.Bottom + 0.01f,
                $"{key} / {value} in {map.Box}");
            Check($"映射第 {i + 1} 条：删除按钮在图标值框右侧且不重叠",
                remove.Left >= value.Right - 0.01f, $"{remove.Left} vs {value.Right}");
            Check($"映射第 {i + 1} 条：图标值框宽度足够显示内容（> 150）",
                value.Width > 150f, value.Width.ToString());
        }

        // 窄窗口（340）下也不能崩
        var narrowMap = NpsPushBridge.ConfigLayout.ComputeIconMap(340f);
        var narrowKey = narrowMap.KeyFieldAt(0);
        var narrowValue = narrowMap.ValueFieldAt(0);
        Check("窄窗口 340：两个框仍不重叠且都有正宽度",
            narrowKey.Width > 0f && narrowValue.Width > 0f && narrowValue.Left >= narrowKey.Right - 0.01f,
            $"{narrowKey.Width} / {narrowValue.Width}");

        var narrowMain = NpsPushBridge.ConfigLayout.Compute(340f, 620f);
        Check("窄窗口 340：＋添加按钮仍与标题不重叠（标题被裁但几何不越界）",
            narrowMain.WhitelistAddButton.Right <= narrowMain.LastSourceButton.Left + 0.01f,
            $"{narrowMain.WhitelistAddButton.Right} / {narrowMain.LastSourceButton.Left}");
        Check("窄窗口 340：白名单输入框仍有正宽度",
            narrowMain.WhitelistEntryField(0).Width > 100f,
            narrowMain.WhitelistEntryField(0).Width.ToString());
    }

    private static bool Near(float a, float b) => Math.Abs(a - b) < 0.01f;

    private static void Check(string name, bool condition, string? detail = null)
    {
        if (condition)
        {
            _passed++;
            Console.WriteLine($"  [PASS] {name}");
        }
        else
        {
            _failed++;
            Console.WriteLine($"  [FAIL] {name}{(string.IsNullOrEmpty(detail) ? "" : $"  →  {detail}")}");
        }
    }
}
