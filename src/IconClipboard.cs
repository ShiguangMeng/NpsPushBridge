using System.Text;

namespace NpsPushBridge;

/// <summary>
/// 图标映射的**复制 / 粘贴规则**（纯逻辑、零依赖，可独立测试）。
///
/// ★ 为什么单独抽一个类 ★
/// 图标映射页的编辑缓冲是「一行一条 <c>应用名=icon值</c>」的纯文本，复制粘贴看似只是
/// 两个字符串操作，但真正需要想清楚的是**冲突语义**：
///   · 粘贴一条「微信=新图」到已有「微信=旧图」的列表上，到底该覆盖、跳过还是问用户？
///   · 一次粘多行时，其中一部分冲突、一部分不冲突，又该怎么办？
/// 这些判断与界面无关（不碰 SkiaSharp、不碰窗口），放进 <c>ConfigPanel</c> 就只能靠人肉点界面来验，
/// 极易在重构里退化。抽在这里之后，<c>BridgeHarness</c> 可以逐条断言，
/// 而界面只管「把计划画出来 / 按计划改缓冲」。
///
/// ★ 复制的格式：**整行原文** ★
/// 复制出去的是缓冲里那一行的原样文本（如 <c>微信=https://…/w.png</c>），不做任何加工。
/// 理由：这是「文本编辑器」的心智模型 —— 用户可以粘到记事本里改完再粘回来，
/// 也可以用它在两处配置间搬运。带标题的说明块（如「【图标映射】共 3 条」）看着漂亮，
/// 但粘回来还要再解析一层、极易出错，收益为负。
///
/// ★ 与「内置一览」的关系 ★
/// 内置一览是**只读**展示（宿主自带、用户无需自己配），因此**不参与复制粘贴** ——
/// 把内置条目粘进自定义区等于用自定义覆盖内置，没有任何实际收益，只会让用户困惑
/// 「为什么我什么都没做，内置的那条就被改了」。
/// 所以复制的**唯一来源**是自定义编辑缓冲里的行。
/// </summary>
internal static class IconClipboard
{
    /// <summary>
    /// 粘贴冲突的处理方式。当前只有「二次确认」一种语义 ——
    /// 之所以不做「静默覆盖 / 静默跳过」，是因为这两者都会**悄悄改掉用户已有配置**：
    /// 覆盖会丢掉用户手写的旧值，跳过则让用户以为「粘成功了」而实际没生效。
    /// 映射表是用户会反复调的东西，任何静默变更都会让他无法判断界面当前到底是什么状态。
    /// </summary>
    public enum ConflictMode
    {
        /// <summary>无冲突：直接插入，无需确认。</summary>
        None,
        /// <summary>有冲突：先给用户看清单，等他确认后再执行。</summary>
        NeedsConfirm,
    }

    /// <summary>
    /// 一条待粘贴的映射。解析失败的输入（没有 <c>=</c>、键或值为空）不会进入列表，
    /// 而是记在 <see cref="PastePlan.Invalid"/> 里如实汇报 —— 静默丢弃会让用户
    /// 「粘了没反应」而不知为何。
    /// </summary>
    public readonly record struct Entry(string Key, string Icon)
    {
        /// <summary>还原成编辑缓冲里的一行（<c>键=值</c>，无空格）。</summary>
        public string ToLine() => $"{Key}={Icon}";
    }

    /// <summary>
    /// 一次粘贴的**执行计划**：把「用户给的文本」翻译成「缓冲该怎么改」。
    ///
    /// 界面拿到计划后只做两件事：① 有冲突且未确认时弹提示；② 确认后按 <see cref="OrderedLines"/>
    /// 逐行落进缓冲。所有判断都在这里，界面不重复实现一遍规则。
    /// </summary>
    /// <param name="Entries">解析成功的条目（保持文本里的原始顺序）。</param>
    /// <param name="Invalid">解析失败的行（原样保留，用于提示）。</param>
    /// <param name="Conflicts">与现有缓冲**键相同**的条目（即执行时会覆盖掉的那几条）。</param>
    /// <param name="Mode">是否需要二次确认。</param>
    public readonly record struct PastePlan(
        IReadOnlyList<Entry> Entries,
        IReadOnlyList<string> Invalid,
        IReadOnlyList<Entry> Conflicts,
        ConflictMode Mode)
    {
        /// <summary>是否有任何可粘贴的内容。</summary>
        public bool HasAnything => Entries.Count > 0;

        /// <summary>全部条目还原成的行（供「确认后插入」使用，也便于测试逐行比对）。</summary>
        public IReadOnlyList<string> OrderedLines => Entries.Select(e => e.ToLine()).ToList();

        /// <summary>
        /// 给用户看的确认提示。
        ///
        /// ⚠️ 文案要**同时给出「会发生什么」与「怎么办」**：
        /// 只说「有 2 条冲突」用户无法判断该不该继续；必须点明「将以剪贴板内容为准覆盖」，
        /// 并告诉他不想覆盖就点「取消」。
        /// </summary>
        public string ConfirmPrompt()
        {
            var keys = string.Join("、", Conflicts.Select(c => c.Key));
            int n = Conflicts.Count;
            return n == 1
                ? $"「{keys}」已存在。继续粘贴会用剪贴板里的内容覆盖它。"
                : $"有 {n} 条已存在：{keys}。继续粘贴会用剪贴板里的内容覆盖它们。";
        }

        /// <summary>执行后的结果说明（写进日志，便于事后追溯「我刚才粘贴改了什么」）。</summary>
        public string DoneMessage()
        {
            int overwritten = Conflicts.Count;
            int added = Entries.Count - overwritten;
            if (overwritten == 0) return $"已粘贴 {added} 条映射";
            return added == 0
                ? $"已覆盖 {overwritten} 条映射"
                : $"已粘贴 {Entries.Count} 条（其中 {overwritten} 条覆盖了同名项）";
        }
    }

    /// <summary>
    /// 解析剪贴板文本，并对现有缓冲**预演**一次粘贴。
    ///
    /// 接受多行（用户可能从记事本整段粘过来）。空行忽略；无法解析的行记入
    /// <see cref="PastePlan.Invalid"/>，**不影响其余行照常粘贴** ——
    /// 「一行坏了就整段不粘」对用户更不友好。
    /// </summary>
    /// <param name="clipboardText">剪贴板原文。</param>
    /// <param name="existingLines">当前编辑缓冲（每行 <c>应用名=icon值</c>）。</param>
    public static PastePlan Plan(string? clipboardText, IReadOnlyList<string> existingLines)
    {
        var entries = new List<Entry>();
        var invalid = new List<string>();

        foreach (var raw in (clipboardText ?? "").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;

            if (TryParseLine(line, out var entry)) entries.Add(entry);
            else invalid.Add(line);
        }

        if (entries.Count == 0)
            return new PastePlan(entries, invalid, [], ConflictMode.None);

        // ★ 同一次粘贴里的重复键只保留最后一条 ★
        // 若两条都留着，Apply 会把它们**各插一行**：处理第二条时它查的是**原始缓冲**，
        // 那时缓冲里还没有这个键（第一条是之后才一起 InsertRange 进去的）→ 查不到冲突 → 两行同名并排。
        // 界面上就出现了「同一个应用两行配置」，而实际生效的只有最后一行
        //（IconOverrides.Parse 是后值覆盖前值）—— 正是本类要消灭的那种骗人界面。
        // 保留**首次出现的顺序位置**、取**最后一次的值**，与 Parse 的语义一致。
        var deduped = new List<Entry>(entries.Count);
        var firstSeenAt = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            if (firstSeenAt.TryGetValue(entry.Key, out int at)) deduped[at] = entry;
            else
            {
                firstSeenAt[entry.Key] = deduped.Count;
                deduped.Add(entry);
            }
        }
        entries = deduped;

        // 现有缓冲里已有哪些键（大小写不敏感，与 IconOverrides 的查表规则一致 ——
        // 否则「微信」和「微信 」在界面上看着一样、冲突判定却不同）
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in existingLines)
            if (TryParseLine(line.Trim(), out var e)) existing.Add(e.Key);

        var conflicts = entries.Where(e => existing.Contains(e.Key)).ToList();
        var mode = conflicts.Count > 0 ? ConflictMode.NeedsConfirm : ConflictMode.None;

        return new PastePlan(entries, invalid, conflicts, mode);
    }

    /// <summary>
    /// 拆一行 <c>应用名=icon值</c>。与 <c>ConfigPanel.ParseIconLine</c> / <c>IconOverrides.Parse</c>
    /// 同一套判据：以**第一个** <c>=</c> 为界（icon 值本身可能是 URL，含 <c>=</c> 的情况很少，
    /// 但 base64 串里可能出现 <c>=</c> 补位 —— 那时必须归到值里，不能当分隔符）。
    /// </summary>
    public static bool TryParseLine(string line, out Entry entry)
    {
        entry = default;
        if (string.IsNullOrWhiteSpace(line)) return false;

        int eq = line.IndexOf('=');
        if (eq <= 0) return false;                       // 没有 = ，或 = 在最前面（键为空）

        var key = line[..eq].Trim();
        var icon = line[(eq + 1)..].Trim();
        if (key.Length == 0 || icon.Length == 0) return false;

        entry = new Entry(key, icon);
        return true;
    }

    /// <summary>
    /// 从编辑缓冲里取出「要复制的那一行」。
    ///
    /// ★ 只复制**有效行** ★ 若活动行是刚加进来的空行（用户点了「＋添加一行」但还没输入），
    /// 复制它会往剪贴板塞一个空串 —— 粘贴回来毫无意义，还会让用户以为复制成功了。
    /// 这种情况返回空串，由界面提示「请先选中一行有内容的映射」。
    /// </summary>
    public static string CopyLine(IReadOnlyList<string> lines, int activeIndex)
    {
        if (activeIndex < 0 || activeIndex >= lines.Count) return "";
        var line = lines[activeIndex].Trim();
        return TryParseLine(line, out _) ? line : "";
    }

    /// <summary>
    /// 把待粘贴的行**按计划**合进缓冲：冲突的**原地替换**（保住用户在列表里的位置），
    /// 不冲突的**插到活动行下方**（与回车换行的手感一致）。
    ///
    /// ★ 为什么冲突要原地替换而不是一律插到下方 ★
    /// 若统一插到下方，用户会看到「微信=旧图」和「微信=新图」并排存在 —— 两份同名配置摆在眼前，
    /// 而实际生效的只有最后一份（<c>IconOverrides.Parse</c> 是后值覆盖前值）。
    /// 那种界面是在骗人。原地替换让「一条键只有一行」这个不变式始终成立。
    /// </summary>
    /// <returns>执行后的新缓冲 + 实际覆盖了几条。</returns>
    public static (List<string> Lines, int Overwritten, int Added) Apply(
        IReadOnlyList<string> existingLines, int activeIndex, PastePlan plan)
    {
        var lines = new List<string>(existingLines);
        int overwritten = 0;

        // ① 冲突项：找到同名键那一行原地替换
        var remaining = new List<Entry>();
        foreach (var entry in plan.Entries)
        {
            int hit = -1;
            for (int i = 0; i < lines.Count; i++)
            {
                // 注意：这里必须对 lines[i] 也做解析，而不能直接比字符串 ——
                // 缓冲里的行可能写成「微信 = qq」（等号两侧有空格），与剪贴板里的
                // 「微信=qq」字面不同但语义相同，直接比字符串会漏判冲突。
                if (TryParseLine(lines[i].Trim(), out var e)
                    && string.Equals(e.Key, entry.Key, StringComparison.OrdinalIgnoreCase))
                {
                    hit = i;
                    break;
                }
            }

            if (hit >= 0)
            {
                lines[hit] = entry.ToLine();
                overwritten++;
            }
            else
            {
                remaining.Add(entry);
            }
        }

        // ② 非冲突项：从活动行下方开始顺序插入
        //    基准位置若越界（空列表 / 未选中）→ 退化为「追加到末尾」，这是最不意外的行为
        int insertAt = activeIndex >= 0 && activeIndex < lines.Count ? activeIndex + 1 : lines.Count;
        if (remaining.Count > 0)
            lines.InsertRange(insertAt, remaining.Select(e => e.ToLine()));

        return (lines, overwritten, remaining.Count);
    }

    /// <summary>把缓冲还原成持久化文本（与 <c>IconOverrides.Serialize</c> 的落盘格式一致）。</summary>
    public static string ToPersistText(IReadOnlyList<string> lines)
    {
        var sb = new StringBuilder();
        foreach (var line in lines)
        {
            var t = line.Trim();
            if (t.Length == 0) continue;
            if (sb.Length > 0) sb.Append('\n');
            sb.Append(t);
        }
        return sb.ToString();
    }
}
