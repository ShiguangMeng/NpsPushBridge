namespace NpsPushBridge;

/// <summary>
/// 单行文本编辑的**纯逻辑模型**：文本 + 光标(caret) + 锚点(anchor，与光标一起表达选区)。
///
/// ── 为什么必须有它（用户的原话）────────────────────────────────────────
/// 「知不知道什么叫做输入框？有光标显示、能正常输入文字内容以及基本复制粘贴剪切功能的输入框」
/// —— 之前那版只是「带边框、只能往末尾追加字符的标签」：没有光标、不能在中间插入、
/// 不能选中、也谈不上剪切。所以这里把「什么叫做编辑」按标准语义完整实现一遍：
/// <list type="bullet">
/// <item>光标可在任意位置（含首尾），插入/删除都发生在光标处；</item>
/// <item>选区用「光标 + 锚点」两个位置表达（Windows 的标准做法），
///       于是「拖选 / Shift+方向键 / 全选」都归结为移动锚点或光标；</item>
/// <item>选区非空时，输入字符与粘贴都**替换选区**（这是编辑框的通行语义）；</item>
/// <item>复制 / 剪切 / 粘贴 / 全选（Ctrl+C / X / V / A）。</item>
/// </list>
///
/// ── 为什么单独一个类、而且不碰 SkiaSharp ────────────────────────────────
/// 「光标该画在哪、点鼠标该落在第几个字符上」需要**文本度量**，而度量要用 SKPaint。
/// 若把两者混在一起，这套编辑语义就只能靠人肉点界面来验。因此这里只保留
/// **与字体无关**的部分，度量通过 <see cref="TextGeometry"/> 的回调注入，
/// 于是「点哪儿落在哪个字符」「光标画在哪个 x」也能被逐条断言（见 TextEditTests）。
///
/// ⚠️ 不变量：<see cref="Caret"/> 与 <see cref="Anchor"/> 永远落在 <c>[0, Text.Length]</c> 内，
///    任何操作之后都成立 —— 否则绘制会算出越界下标（历史上 `_lines[_activeLine]` 就踩过越界）。
/// </summary>
internal sealed class TextEdit
{
    private string _text = "";
    private int _caret;
    private int _anchor;

    /// <summary>
    /// 允许插入的最大长度（0 = 不限）。由调用方在聚焦时按字段设置
    /// （白名单 120 / 应用名 64 / 图标值 400 / 端口 5 位）。
    /// 超限时 <see cref="Insert"/> 直接拒绝（而不是静默截断成半截地址）。
    /// </summary>
    public int MaxLength { get; set; }

    /// <summary>额外字符过滤（端口只要数字）。为 null 表示接受所有可打印字符。</summary>
    public Func<char, bool>? CharFilter { get; set; }

    public string Text => _text;

    public int Caret => _caret;

    /// <summary>选区起点（光标与锚点中靠前的那个）。</summary>
    public int SelectionStart => Math.Min(_caret, _anchor);

    /// <summary>选区终点（不含）。等于起点时表示没有选区。</summary>
    public int SelectionEnd => Math.Max(_caret, _anchor);

    public bool HasSelection => _caret != _anchor;

    public int Length => _text.Length;

    /// <summary>
    /// 绑定一份新文本并决定光标位置。
    ///
    /// ⚠️ 默认把光标放在**末尾**（<paramref name="caretAt"/> 为负时）：
    /// 用户「点一下框就把原来的内容改掉」是最常见的意图，而「点一下却把光标扔到最前面」
    /// 会让他接着输入时把内容插到开头 —— 那是最容易让人困惑的默认值。
    /// 需要精确位置时由鼠标点击传入（见 <see cref="SetCaretByIndex"/>）。
    /// </summary>
    public void SetText(string? text, int caretAt = -1)
    {
        _text = text ?? "";
        _caret = caretAt < 0 ? _text.Length : Clamp(caretAt);
        _anchor = _caret;
    }

    /// <summary>把光标移到指定下标（<paramref name="extend"/> = 保持锚点 ⇒ 扩出选区）。</summary>
    public void SetCaretByIndex(int index, bool extend = false)
    {
        _caret = Clamp(index);
        if (!extend) _anchor = _caret;
    }

    public void SelectAll()
    {
        _anchor = 0;
        _caret = _text.Length;
    }

    /// <summary>取消选区（光标不动）。</summary>
    public void Deselect() => _anchor = _caret;

    /// <summary>选区内的文本（无选区时为空串）。</summary>
    public string SelectedText()
        => HasSelection ? _text[SelectionStart..SelectionEnd] : "";

    // ====================================================================
    // 编辑
    // ====================================================================

    /// <summary>
    /// 在光标处插入文本（有选区则先替换选区）。返回是否真的改了内容。
    /// 超出 <see cref="MaxLength"/> 时**整体拒绝**并返回 false，由调用方给出提示。
    /// </summary>
    public bool Insert(string? value)
    {
        if (string.IsNullOrEmpty(value)) return false;

        // 过滤 + 单行化：换行/制表不进单行框（回车在界面上另有含义，见 ConfigPanel）
        var filtered = new System.Text.StringBuilder(value.Length);
        foreach (var ch in value)
        {
            if (ch is '\r' or '\n' or '\t') continue;
            if (ch < ' ') continue;
            if (CharFilter != null && !CharFilter(ch)) continue;
            filtered.Append(ch);
        }
        if (filtered.Length == 0) return false;

        int selStart = SelectionStart, selEnd = SelectionEnd;
        int resultLength = _text.Length - (selEnd - selStart) + filtered.Length;
        if (MaxLength > 0 && resultLength > MaxLength) return false;

        _text = _text[..selStart] + filtered + _text[selEnd..];
        _caret = selStart + filtered.Length;
        _anchor = _caret;
        return true;
    }

    /// <summary>插入单个字符（输入法/键盘走这里）。</summary>
    public bool Insert(char ch) => Insert(ch.ToString());

    /// <summary>退格：有选区先删选区，否则删光标前一个字符。</summary>
    public bool Backspace()
    {
        if (HasSelection) return DeleteSelection();
        if (_caret <= 0) return false;

        _text = _text.Remove(_caret - 1, 1);
        _caret--;
        _anchor = _caret;
        return true;
    }

    /// <summary>Delete 键：有选区先删选区，否则删光标处那个字符。</summary>
    public bool DeleteForward()
    {
        if (HasSelection) return DeleteSelection();
        if (_caret >= _text.Length) return false;

        _text = _text.Remove(_caret, 1);
        _anchor = _caret;   // 光标不动
        return true;
    }

    /// <summary>删除选区（无选区时返回 false）。</summary>
    public bool DeleteSelection()
    {
        if (!HasSelection) return false;

        int start = SelectionStart, end = SelectionEnd;
        _text = _text.Remove(start, end - start);
        _caret = start;
        _anchor = start;
        return true;
    }

    // ====================================================================
    // 复制 / 剪切 / 粘贴
    // ====================================================================

    /// <summary>复制：返回选区文本（无选区时返回空串，由调用方提示「没有选中内容」）。</summary>
    public string Copy() => SelectedText();

    /// <summary>
    /// 剪切：返回被剪掉的选区文本并从内容里删除。
    /// 无选区时返回空串且不改内容 —— **不做「没有选区就剪整行」**，
    /// 那会让用户在没选中任何东西的情况下意外丢掉整行内容。
    /// </summary>
    public string Cut()
    {
        var taken = SelectedText();
        if (taken.Length > 0) DeleteSelection();
        return taken;
    }

    /// <summary>
    /// 粘贴：替换选区或插在光标处。返回是否改了内容（剪贴板为空 ⇒ false）。
    /// 超长时**截断到上限**而不是整体拒绝：粘贴通常来自外部，全丢掉更让人意外。
    /// </summary>
    public bool Paste(string? clipboard)
    {
        if (string.IsNullOrEmpty(clipboard)) return false;

        var cleaned = new System.Text.StringBuilder(clipboard.Length);
        foreach (var ch in clipboard)
        {
            if (ch is '\r' or '\n' or '\t') continue;
            if (ch < ' ') continue;
            if (CharFilter != null && !CharFilter(ch)) continue;
            cleaned.Append(ch);
        }
        if (cleaned.Length == 0) return false;

        if (MaxLength > 0)
        {
            int room = MaxLength - (_text.Length - (SelectionEnd - SelectionStart));
            if (room <= 0) return false;
            if (cleaned.Length > room) cleaned.Length = room;
        }

        return Insert(cleaned.ToString());
    }

    // ====================================================================
    // 光标移动（含选区扩展）
    // ====================================================================

    public void MoveLeft(bool extend)
    {
        // 有选区且不扩展时：先退到选区起点（标准编辑框行为），而不是再往左一格
        int target = HasSelection && !extend ? SelectionStart : _caret - 1;
        SetCaretByIndex(target, extend);
    }

    public void MoveRight(bool extend)
    {
        int target = HasSelection && !extend ? SelectionEnd : _caret + 1;
        SetCaretByIndex(target, extend);
    }

    public void MoveHome(bool extend) => SetCaretByIndex(0, extend);

    public void MoveEnd(bool extend) => SetCaretByIndex(_text.Length, extend);

    /// <summary>Ctrl+←：跳到上一个词边界。</summary>
    public void MoveWordLeft(bool extend)
    {
        int i = _caret;
        while (i > 0 && !IsWordChar(_text[i - 1])) i--;
        while (i > 0 && IsWordChar(_text[i - 1])) i--;
        SetCaretByIndex(i, extend);
    }

    /// <summary>Ctrl+→：跳到下一个词边界。</summary>
    public void MoveWordRight(bool extend)
    {
        int i = _caret;
        while (i < _text.Length && IsWordChar(_text[i])) i++;
        while (i < _text.Length && !IsWordChar(_text[i])) i++;
        SetCaretByIndex(i, extend);
    }

    /// <summary>
    /// 词字符：字母 / 数字 / 地址里常见的连接符 / CJK。
    ///
    /// ★ 为什么把 `.-_/` 也算词字符 ★ 这个框里填的是 IP、路径、URL、包名
    /// （`192.168.1.*`、`com.tencent.mobileqq`、`https://a/b.png`）。
    /// 若按纯字母数字分界，Ctrl+← 会在这类内容里跳十几下才有反应 ——
    /// 把这些连接符并进词里，一次跳一段，才是这些数据上「一个词」的直觉。
    /// </summary>
    private static bool IsWordChar(char ch)
    {
        if (char.IsLetterOrDigit(ch)) return true;
        if (ch is '.' or '-' or '_' or '/' or ':' or '@' or '*') return true;
        return ch >= '\u2E80';   // CJK / 中日韩标点等
    }

    private int Clamp(int index) => index < 0 ? 0 : index > _text.Length ? _text.Length : index;
}

/// <summary>
/// 文本与屏幕坐标之间的换算（把「度量」以回调注入，因此与 SkiaSharp 解耦、可测试）。
///
/// ⚠️ 一条不可违反的约定：**量宽与画宽必须用同一个 Typeface**。
/// 调用方传入的 <c>measure</c> 必须绑定到「将要用来绘制这段文本的那支画笔」，
/// 并且**先用整串文本 <c>PluginFonts.Fit</c> 一次**（不要在量前缀时另择字体），
/// 否则会出现「按甲字体算光标位置、按乙字体画字」→ 光标与文字错位。
/// </summary>
internal static class TextGeometry
{
    /// <summary>光标落在第 <paramref name="index"/> 个字符之前时的 x 偏移（相对文本起点）。</summary>
    public static float CaretOffset(Func<string, float> measure, string text, int index)
    {
        if (index <= 0) return 0f;
        if (index >= text.Length) return measure(text);
        return measure(text[..index]);
    }

    /// <summary>
    /// 由点击的 x 反推光标下标。
    ///
    /// 用**二分查找**而不是逐字符累加：日志/映射框可以很长，
    /// 逐字符是 O(n) 次原生度量、还每帧都跑（项目里已有一条「凡边界一律二分」的约定）。
    /// 判定规则是标准写法：落在某字符的**中点右侧**就算到下一个字符之前。
    /// </summary>
    public static int IndexAt(Func<string, float> measure, string text, float x)
    {
        if (string.IsNullOrEmpty(text) || x <= 0f) return 0;
        if (x >= measure(text)) return text.Length;

        int lo = 0, hi = text.Length;           // 求最小的 i 使 CaretOffset(i) >= x
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (CaretOffset(measure, text, mid) < x) lo = mid + 1;
            else hi = mid;
        }

        // lo 是第一个「右边界 ≥ x」的位置；再比较它前一个字符的中点，决定归谁
        if (lo > 0)
        {
            float prev = CaretOffset(measure, text, lo - 1);
            float cur = CaretOffset(measure, text, lo);
            if (x - prev < (cur - prev) / 2f) return lo - 1;
        }
        return lo;
    }

    /// <summary>
    /// 让光标可见所需的水平滚动偏移。
    ///
    /// 只在必要时移动（尽量少滚动），并且**右侧留出光标本身的宽度**，
    /// 否则光标会正好贴在裁剪边界上被削掉一半。
    /// </summary>
    public static float ScrollToShow(float caretX, float scroll, float innerWidth, float caretWidth = 1.5f)
    {
        if (innerWidth <= 0f) return 0f;

        if (caretX - scroll < 0f) return caretX;                                  // 光标在左边看不见 → 左移视图
        if (caretX - scroll > innerWidth - caretWidth)
            return caretX - innerWidth + caretWidth;                              // 右侧超出 → 右移视图
        return scroll;
    }

    /// <summary>选区的绘制矩形（相对文本起点的 x 与宽度）。</summary>
    public static (float X, float Width) SelectionBox(
        Func<string, float> measure, string text, int start, int end)
    {
        float x0 = CaretOffset(measure, text, start);
        float x1 = CaretOffset(measure, text, end);
        return (x0, Math.Max(0f, x1 - x0));
    }
}
