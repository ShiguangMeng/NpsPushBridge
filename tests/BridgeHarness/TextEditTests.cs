using NpsPushBridge;

namespace BridgeHarness;

/// <summary>
/// <see cref="TextEdit"/>（编辑模型）与 <see cref="TextGeometry"/>（坐标换算）的验证，
/// 外加 <see cref="KeyRepeat"/>（长按连跳）。
///
/// ★ 为什么这些值得单独测 ★
/// 它们全都是「错了不会抛异常、只是行为不对」的东西：
///   · 光标多走一格 / 插入位置差一个字符 —— 用起来别扭但不会报错；
///   · 度量换算按「字符左边界」而不是「字符中点」判定 —— 点击落点系统性偏移半格；
///   · 长按方向键没做重复、或松手没复位 —— 表现为「按一下没反应」或「一按就飞到底」。
/// 这类问题靠人肉点界面几乎发现不了（人会自动适应半格偏移），必须钉成断言。
/// </summary>
internal static class TextEditTests
{
    private static int _passed, _failed;

    private static void Check(string name, bool ok, string detail = "")
    {
        if (ok) { _passed++; Console.WriteLine($"  [PASS] {name}"); }
        else { _failed++; Console.WriteLine($"  [FAIL] {name}{(detail.Length > 0 ? "  —— " + detail : "")}"); }
    }

    /// <summary>
    /// 造一个「等宽」的度量函数：每个字符 10px、空串 0px。
    /// 等宽让所有预期值都能一眼算出来，测试的可读性远好于用真实字体。
    /// </summary>
    private static float Measure(string s) => s.Length * 10f;

    public static (int Passed, int Failed) Run()
    {
        Console.WriteLine("【输入框：编辑模型与坐标换算】");

        EditingSection();
        SelectionSection();
        ClipboardSection();
        NavigationSection();
        LimitSection();
        GeometrySection();
        RepeatSection();
        InvariantSection();

        return (_passed, _failed);
    }

    // ====================================================================
    // 编辑
    // ====================================================================

    private static void EditingSection()
    {
        Console.WriteLine("  -- 编辑 --");

        var e = new TextEdit();
        e.SetText("abc");
        Check("绑定后光标默认在末尾（要改的多半是尾部）", e.Caret == 3, e.Caret.ToString());

        e.SetText("abc", 1);
        e.Insert("X");
        Check("在中间插入：光标处插入，其余内容顺延", e.Text == "aXbc", e.Text);
        Check("插入后光标跟在插入内容之后", e.Caret == 2, e.Caret.ToString());

        e.SetText("abc", 0);
        e.Insert('Z');
        Check("在开头插入", e.Text == "Zabc", e.Text);

        e.SetText("abc", 3);
        e.Insert('Z');
        Check("在末尾插入", e.Text == "abcZ", e.Text);

        e.SetText("abc", 2);
        e.Backspace();
        Check("退格删的是**光标前**一个字符", e.Text == "ac", e.Text);
        Check("退格后光标前移一格", e.Caret == 1, e.Caret.ToString());

        e.SetText("abc", 0);
        Check("光标在开头时退格无内容可删", !e.Backspace() && e.Text == "abc");

        e.SetText("abc", 1);
        e.DeleteForward();
        Check("Delete 删的是**光标处**那个字符（光标不动）", e.Text == "ac" && e.Caret == 1, $"{e.Text}@{e.Caret}");

        e.SetText("abc", 3);
        Check("光标在末尾时 Delete 无内容可删", !e.DeleteForward() && e.Text == "abc");

        // 空文本的边界：任何编辑都不该抛异常、也不该把下标搞越界
        e.SetText("");
        Check("空文本：退格 / Delete 都安全且无副作用", !e.Backspace() && !e.DeleteForward() && e.Text == "");

        // 单行：换行与制表不允许进入
        e.SetText("");
        e.Insert("a\nb\tc\rd");
        Check("换行 / 制表被过滤（这些框都是单行）", e.Text == "abcd", e.Text.Replace("\n", "\\n"));
    }

    // ====================================================================
    // 选区
    // ====================================================================

    private static void SelectionSection()
    {
        Console.WriteLine("  -- 选区 --");

        var e = new TextEdit();
        e.SetText("hello world");
        Check("刚绑定时没有选区", !e.HasSelection && e.SelectedText() == "");

        e.SetCaretByIndex(0);
        e.SetCaretByIndex(5, extend: true);
        Check("扩选：锚点固定、光标移动 ⇒ 形成选区",
            e.HasSelection && e.SelectionStart == 0 && e.SelectionEnd == 5 && e.SelectedText() == "hello",
            $"{e.SelectionStart}..{e.SelectionEnd}");

        // 反向扩选（往回拖）也要给出同一段选区
        e.SetCaretByIndex(5);
        e.SetCaretByIndex(2, extend: true);
        Check("反向往回扩选：起止仍按前小后大归一",
            e.SelectionStart == 2 && e.SelectionEnd == 5, $"{e.SelectionStart}..{e.SelectionEnd}");

        e.SetText("hello world");
        e.SelectAll();
        Check("全选", e.SelectionStart == 0 && e.SelectionEnd == 11 && e.SelectedText() == "hello world");

        // ★ 有选区时输入 = 替换选区（编辑框的通行语义）
        e.SelectAll();
        e.Insert("新");
        Check("★ 有选区时输入字符会**替换**选区", e.Text == "新" && !e.HasSelection, e.Text);

        e.SetText("hello world");
        e.SetCaretByIndex(0);
        e.SetCaretByIndex(6, extend: true);
        e.Backspace();
        Check("★ 有选区时退格删的是整个选区", e.Text == "world", e.Text);

        e.SetText("hello world");
        e.SetCaretByIndex(0);
        e.SetCaretByIndex(6, extend: true);
        e.DeleteForward();
        Check("★ 有选区时 Delete 也是删整个选区", e.Text == "world", e.Text);

        // 不扩选地移动光标 = 取消选区
        e.SetText("abcdef");
        e.SetCaretByIndex(2);
        e.SetCaretByIndex(4, extend: true);
        e.MoveLeft(extend: false);
        Check("不按 Shift 移动光标会取消选区，且退到选区**起点**（不是再左一格）",
            !e.HasSelection && e.Caret == 2, $"caret={e.Caret}");

        e.SetCaretByIndex(2);
        e.SetCaretByIndex(4, extend: true);
        e.MoveRight(extend: false);
        Check("向右同理：跳到选区**终点**", !e.HasSelection && e.Caret == 4, $"caret={e.Caret}");
    }

    // ====================================================================
    // 复制 / 剪切 / 粘贴
    // ====================================================================

    private static void ClipboardSection()
    {
        Console.WriteLine("  -- 复制 / 剪切 / 粘贴 --");

        var e = new TextEdit();
        e.SetText("192.168.1.23");
        Check("无选区时复制为空（「没有选中内容」由界面提示）", e.Copy() == "");

        e.SetCaretByIndex(0);
        e.SetCaretByIndex(9, extend: true);
        Check("复制返回选中文本", e.Copy() == "192.168.1");

        Check("剪切返回选中文本并从内容里删掉", e.Cut() == "192.168.1" && e.Text == ".23", e.Text);
        Check("剪切后光标停在原选区起点", e.Caret == 0 && !e.HasSelection, e.Caret.ToString());

        // 没有选区时剪切**不能**剪掉整行（容易让用户莫名丢内容）
        e.SetText("整行内容");
        Check("★ 无选区时剪切返回空且**不改内容**（不会整行剪掉）",
            e.Cut() == "" && e.Text == "整行内容", e.Text);

        // 粘贴：插在光标处 / 替换选区
        e.SetText("ab", 1);
        Check("粘贴插在光标处", e.Paste("XY") && e.Text == "aXYb", e.Text);

        e.SetText("abcdef");
        e.SelectAll();
        Check("★ 粘贴替换整个选区", e.Paste("Z") && e.Text == "Z", e.Text);

        e.SetText("ab");
        Check("粘贴空剪贴板 = 什么都没发生",
            !e.Paste("") && !e.Paste(null) && e.Text == "ab", e.Text);

        e.SetText("");
        e.Paste("a\nb\tc");
        Check("粘贴也会过滤换行 / 制表（保持单行）", e.Text == "abc", e.Text.Replace("\n", "\\n"));

        // 粘贴含中文（真实场景：从记事本整段粘 IP / 路径）
        e.SetText("");
        e.Paste("192.168.1.0/24");
        Check("粘贴 CIDR 地址", e.Text == "192.168.1.0/24", e.Text);
    }

    // ====================================================================
    // 光标导航（方向键 / Home / End / Ctrl+方向）
    // ====================================================================

    private static void NavigationSection()
    {
        Console.WriteLine("  -- 光标导航 --");

        var e = new TextEdit();
        e.SetText("abc", 1);

        e.MoveLeft(false);
        Check("左移一格", e.Caret == 0, e.Caret.ToString());
        e.MoveLeft(false);
        Check("已在开头：左移不越界", e.Caret == 0);

        e.MoveRight(false);
        Check("右移一格", e.Caret == 1, e.Caret.ToString());
        e.MoveEnd(false);
        Check("End 到末尾", e.Caret == 3);
        e.MoveRight(false);
        Check("已在末尾：右移不越界", e.Caret == 3);

        e.MoveHome(false);
        Check("Home 到开头", e.Caret == 0);

        e.MoveEnd(true);
        Check("Shift+End 从开头扩到末尾（形成选区）",
            e.SelectionStart == 0 && e.SelectionEnd == 3, $"{e.SelectionStart}..{e.SelectionEnd}");

        // 词跳转：地址里的 . - / : 都算词字符，所以一次跳过一整段。
        // 落点是「下一个词的**起点**」——即还要吃掉中间的空格（Windows 的 Ctrl+→ 就是这个语义）
        e.SetText("192.168.1.23 端口", 0);
        e.MoveWordRight(false);
        Check("★ Ctrl+→ 跳过整个 IP 并吃掉空格，停在下一个词的起点（`.` 不算边界，一次跳一段）",
            e.Caret == 13, e.Caret.ToString());

        e.MoveWordLeft(false);
        Check("★ Ctrl+← 跳回 IP 开头", e.Caret == 0, e.Caret.ToString());

        e.SetText("https://a.example.com/b.png", 0);
        e.MoveWordRight(false);
        Check("★ URL 也整段跳（/ 与 . 都是词字符）", e.Caret == 27, e.Caret.ToString());

        // 中文：连续汉字视为一段
        e.SetText("微信 QQ", 0);
        e.MoveWordRight(false);
        Check("★ 中文整段跳（不会一个字停一下）", e.Caret == 3, e.Caret.ToString());
        e.MoveWordRight(false);
        Check("再跳一次到末尾", e.Caret == 5, e.Caret.ToString());
    }

    // ====================================================================
    // 长度上限与字符过滤
    // ====================================================================

    private static void LimitSection()
    {
        Console.WriteLine("  -- 上限与过滤 --");

        var e = new TextEdit { MaxLength = 5 };
        e.SetText("");
        Check("未超限可插入", e.Insert("12345") && e.Text == "12345", e.Text);
        Check("★ 超限时**整体拒绝**（而不是静默截成半截地址）",
            !e.Insert("6") && e.Text == "12345", e.Text);

        // 替换选区时按「替换后的总长度」判断，而不是「是否插入过」
        e.SetText("12345");
        e.SetCaretByIndex(0);
        e.SetCaretByIndex(5, extend: true);
        Check("★ 用选区替换时按净长度判断：等长替换是允许的",
            e.Insert("abcde") && e.Text == "abcde", e.Text);

        e.SetText("12345");
        e.SelectAll();
        Check("替换后更长则拒绝", !e.Insert("abcdef") && e.Text == "12345", e.Text);

        // 粘贴超长：截断到刚好放满（外部内容，全丢掉更让人意外）
        e.SetText("12");
        e.Paste("3456789");
        Check("★ 粘贴超长时截断到上限（不整体丢弃）", e.Text == "12345", e.Text);

        // 端口那种「只要数字」的过滤
        var p = new TextEdit { MaxLength = 5, CharFilter = char.IsDigit };
        p.SetText("");
        p.Insert('1');
        p.Insert('a');
        p.Insert('4');
        Check("★ 字符过滤：非数字不进框", p.Text == "14", p.Text);

        p.Paste("80a8b0");
        // "80a8b0" 过滤后是 "8080"，但上限 5 位、已有 "14" 只放得下 3 个 ⇒ "14" + "808"
        Check("粘贴同样受过滤（只留数字）且按剩余额度截断", p.Text == "14808", p.Text);

        p.SetText("1403");
        p.SelectAll();
        p.Insert("70000");
        Check("端口上限 5 位生效", p.Text == "70000", p.Text);
    }

    // ====================================================================
    // 坐标换算（点击落点 / 光标位置 / 水平滚动）
    // ====================================================================

    private static void GeometrySection()
    {
        Console.WriteLine("  -- 坐标换算 --");

        Check("光标偏移：开头为 0", TextGeometry.CaretOffset(Measure, "abc", 0) == 0f);
        Check("光标偏移：第 2 个字符前 = 一个字宽", TextGeometry.CaretOffset(Measure, "abc", 2) == 20f);
        Check("光标偏移：末尾 = 整串宽度", TextGeometry.CaretOffset(Measure, "abc", 3) == 30f);

        Check("落点：x=0 → 下标 0", TextGeometry.IndexAt(Measure, "abc", 0f) == 0);
        Check("★ 落点：字符**左半**归前一个字符", TextGeometry.IndexAt(Measure, "abc", 4f) == 0,
            TextGeometry.IndexAt(Measure, "abc", 4f).ToString());
        Check("★ 落点：字符**右半**归下一个字符之前", TextGeometry.IndexAt(Measure, "abc", 6f) == 1,
            TextGeometry.IndexAt(Measure, "abc", 6f).ToString());
        Check("落点：超过末尾 → 末尾", TextGeometry.IndexAt(Measure, "abc", 999f) == 3);
        Check("落点：负数 → 开头", TextGeometry.IndexAt(Measure, "abc", -5f) == 0);
        Check("落点：空串一律 0", TextGeometry.IndexAt(Measure, "", 50f) == 0);
        Check("落点：恰好落在字符边界（10px）时归后一个字符",
            TextGeometry.IndexAt(Measure, "abc", 10f) == 1, TextGeometry.IndexAt(Measure, "abc", 10f).ToString());

        // 水平滚动：只在光标越界时移动，且右侧留出光标本身的宽度
        Check("光标可见时不滚动", TextGeometry.ScrollToShow(10f, 0f, 100f) == 0f);
        Check("光标在左侧视野外 → 视图左移（偏移 = 光标位置）",
            TextGeometry.ScrollToShow(20f, 50f, 100f) == 20f);
        Check("★ 光标在右侧视野外 → 视图右移并**留出光标宽度**（否则会被裁剪削掉一半）",
            Math.Abs(TextGeometry.ScrollToShow(150f, 0f, 100f, 2f) - 52f) < 0.001f,
            TextGeometry.ScrollToShow(150f, 0f, 100f, 2f).ToString());
        Check("宽度为 0 时不动（避免除零式的怪异偏移）", TextGeometry.ScrollToShow(10f, 5f, 0f) == 0f);

        var (sx, sw) = TextGeometry.SelectionBox(Measure, "abcdef", 1, 4);
        Check("选区矩形：x 与宽度按下标算", Math.Abs(sx - 10f) < 0.001f && Math.Abs(sw - 30f) < 0.001f,
            $"{sx}+{sw}");
    }

    // ====================================================================
    // 长按连跳
    // ====================================================================

    private static void RepeatSection()
    {
        Console.WriteLine("  -- 长按连跳 --");

        var r = new KeyRepeat { RepeatDelayMs = 400, RepeatIntervalMs = 50 };

        Check("首次按下立刻触发一次", r.ShouldFire(1, true, 1000));
        Check("刚按下的下一次采样不触发（还没到重复延时）", !r.ShouldFire(1, true, 1050));
        Check("到达「延时」之后仍未触发（要等满 400ms）", !r.ShouldFire(1, true, 1350));
        Check("★ 满 400ms 后开始重复", r.ShouldFire(1, true, 1400));
        Check("重复间隔之内不再触发", !r.ShouldFire(1, true, 1420));
        Check("★ 到下一个间隔点再次触发", r.ShouldFire(1, true, 1450));

        r.ShouldFire(1, false, 1460);
        Check("★ 松手即复位：再按一下是「立刻触发」而不是接着旧节奏",
            r.ShouldFire(1, true, 1470));

        // 两个键各自独立（左和右不能互相打断）
        var r2 = new KeyRepeat();
        Check("不同键的状态互不影响",
            r2.ShouldFire(10, true, 0) && r2.ShouldFire(11, true, 0) && !r2.ShouldFire(10, true, 1));

        r2.Reset();
        Check("Reset 之后一切从头开始", r2.ShouldFire(10, true, 2));
    }

    // ====================================================================
    // 不变量（越界防护）
    // ====================================================================

    private static void InvariantSection()
    {
        Console.WriteLine("  -- 不变量 --");

        // 任何操作序列之后，光标与锚点都必须落在 [0, Text.Length]
        var e = new TextEdit();
        var rnd = new Random(20260923);

        bool allInside = true;
        string? bad = null;

        for (int i = 0; i < 4000; i++)
        {
            switch (rnd.Next(10))
            {
                case 0: e.SetText(new string('x', rnd.Next(0, 12)), rnd.Next(-3, 15)); break;
                case 1: e.SetCaretByIndex(rnd.Next(-3, 15), rnd.Next(2) == 0); break;
                case 2: e.Insert((char)('a' + rnd.Next(26))); break;
                case 3: e.Backspace(); break;
                case 4: e.DeleteForward(); break;
                case 5: e.MoveLeft(rnd.Next(2) == 0); break;
                case 6: e.MoveRight(rnd.Next(2) == 0); break;
                case 7: e.MoveWordLeft(rnd.Next(2) == 0); break;
                case 8: e.MoveWordRight(rnd.Next(2) == 0); break;
                default: e.SelectAll(); break;
            }

            if (e.Caret < 0 || e.Caret > e.Text.Length
                || e.SelectionStart < 0 || e.SelectionEnd > e.Text.Length
                || e.SelectionStart > e.SelectionEnd)
            {
                allInside = false;
                bad = $"caret={e.Caret} sel={e.SelectionStart}..{e.SelectionEnd} len={e.Text.Length}";
                break;
            }
        }

        Check("★ 4000 次随机操作后：光标与选区**始终**落在 [0, 文本长度] 内（越界会让绘制崩）",
            allInside, bad ?? "");
    }
}
