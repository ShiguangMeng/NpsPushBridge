using SkiaSharp;
using NpsPushBridge;

namespace BridgeHarness;

/// <summary>
/// 文本折行与截断的验证（<see cref="TextLayout"/>）。
///
/// ★ 为什么值得专门测 ★
/// 这两件事原先写在 WinForms 侧的 ConfigPanel 里，测不到；而它们恰恰属于
/// 「错了不会抛异常、只会让界面看着怪一点」的类型 —— 折行少一个字符、截断少一个省略号，
/// 没有任何报错，只有盯着日志肉眼才能发现。抽成纯函数后就能用**真实 SKPaint 度量**逐条断言。
///
/// ★ 核心不变式（比具体期望值更重要）★
///   ① **放得下原则**：折出的每一行都必须真的 ≤ 宽度（否则宿主/画布上会溢出）；
///   ② **不丢字原则**：折行结果拼起来必须等于原文（截断情形除外，它必然丢掉尾巴）——
///      这条能抓出「边界算错导致吞掉一个字符」这类最阴的错误；
///   ③ **不空转原则**：任何输入都必须终止（曾担心二分返回值 0 会死循环）；
///   ④ **退化安全**：宽度为 0 / 负数 / 空串时不能抛异常、不能返回空列表。
/// </summary>
internal static class TextLayoutTests
{
    public static (int Passed, int Failed) Run()
    {
        Console.WriteLine();
        Console.WriteLine("【文本折行与截断】");

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

        using var typeface = SKTypeface.FromFamilyName("Microsoft YaHei UI");
        using var paint = new SKPaint { IsAntialias = true, Typeface = typeface, TextSize = 11f };

        // ── 1. 放得下就不折（只返回一行）──────────────────────────────
        {
            var r = TextLayout.Wrap(paint, "短", 200f);
            Check("放得下的短文本 → 单行", r.Count == 1 && r[0] == "短", string.Join("|", r));

            var wide = TextLayout.Wrap(paint, "一条不太长但明确放得下的日志", 500f);
            Check("放得下的长文本 → 仍是单行", wide.Count == 1 && wide[0] == "一条不太长但明确放得下的日志",
                string.Join("|", wide));
        }

        // ── 2. 空串 / 空白宽度等退化输入 ──────────────────────────────
        {
            var empty = TextLayout.Wrap(paint, "", 100f);
            Check("空串 → 一行空串（不是零行）", empty.Count == 1 && empty[0].Length == 0, empty.Count.ToString());

            var zero = TextLayout.Wrap(paint, "放不下的一段文字", 0f);
            Check("宽度 0 → 不折、原样返回一行（不抛异常）", zero.Count == 1, string.Join("|", zero));

            var negative = TextLayout.Wrap(paint, "放不下的一段文字", -50f);
            Check("宽度为负 → 不折、原样返回一行（不抛异常）", negative.Count == 1, string.Join("|", negative));

            Check("最小可用宽度的常量是 8", Math.Abs(TextLayout.MinUsableWidth - 8f) < 0.01f);
        }

        // ── 3. ★ 核心不变式：不丢字 ★ ────────────────────────────────
        {
            string text = "2026-09-20 23:14:07 来自 192.168.1.23 → 手机消息：银行卡尾号1234余额变动提醒，请注意查收。";
            var rows = TextLayout.Wrap(paint, text, 120f);

            Check($"长中文串折成多行（{rows.Count} 行）", rows.Count > 1, string.Join(" | ", rows));
            Check("★ 不丢字：折行结果拼起来 == 原文", string.Concat(rows) == text,
                "拼回=" + string.Concat(rows));
        }

        // ── 4. ★ 核心不变式：每行都放得下 ★ ──────────────────────────
        {
            string text = "abcdefghij klmnopqrst uvwxyz 0123456789 ABCDEFGHIJ KLMNOPQRST UVWXYZ 中文混排测试内容";
            const float width = 90f;
            var rows = TextLayout.Wrap(paint, text, width);

            bool allFit = true;
            string offender = "";
            foreach (var row in rows)
            {
                if (paint.MeasureText(row) > width + 0.01f) { allFit = false; offender = row; break; }
            }
            Check($"★ 每行宽度都 ≤ {width}（无溢出）", allFit, offender);
            Check("★ 不丢字（英文混排）", string.Concat(rows) == text);
        }

        // ── 5. 逐行推进：不会死循环、不会产生空行 ─────────────────────
        {
            string text = new string('字', 300);   // 无空格的超长串：最考验边界推进
            var rows = TextLayout.Wrap(paint, text, 100f);
            Check($"超长无空格串能折行（{rows.Count} 行）", rows.Count > 1, rows.Count.ToString());
            Check("不会产生空行", rows.TrueForAll(r => r.Length > 0), "存在空行");
            Check("受 MaxRowsPerLine 兜底限制",
                rows.Count <= TextLayout.MaxRowsPerLine + 1, rows.Count.ToString());

            // 真正触顶：把宽度压到只放得下 1 个字，300 字必然超过 40 行上限。
            // ★ 这条才是「兜底真的生效」的证据 ★ —— 上一版用例用 100px 只能折 34 行，
            //   根本没碰到上限，断言末行是省略号自然失败（是**用例写错了**，不是实现有问题）。
            var capped = TextLayout.Wrap(paint, text, paint.MeasureText("字") + 0.5f);
            Check($"压到单字宽 → 正好触顶 {TextLayout.MaxRowsPerLine} 行 + 省略号",
                capped.Count == TextLayout.MaxRowsPerLine + 1, capped.Count.ToString());
            Check("触顶时末行为省略号（提示还有内容被截）",
                capped[^1] == TextLayout.Ellipsis, capped[^1]);
            Check("触顶时前面每一行都是单字",
                capped.Take(TextLayout.MaxRowsPerLine).All(r => r == "字"));
        }

        // ── 6. FittingLength 的边界 ──────────────────────────────────
        {
            Check("起点在串尾 → 返回 1（不死循环）",
                TextLayout.FittingLength(paint, "abc", 3, 100f) == 1);
            Check("起点越界 → 返回 1",
                TextLayout.FittingLength(paint, "abc", 99, 100f) == 1);

            // 宽度足够 → 一次拿到剩余全部
            Check("宽度足够 → 返回剩余全部长度",
                TextLayout.FittingLength(paint, "abcdef", 2, 500f) == 4);

            // 宽度只够一个字 → 至少返回 1
            float oneWidth = paint.MeasureText("a");
            int tiny = TextLayout.FittingLength(paint, "abcdef", 0, oneWidth + 0.5f);
            Check("宽度仅够一个字 → 返回 1", tiny >= 1, tiny.ToString());
        }

        // ── 7. 截断 ─────────────────────────────────────────────────
        {
            Check("放得下 → 原样返回（无省略号）",
                TextLayout.Truncate(paint, "短文本", 500f) == "短文本");

            Check("空串 → 空串", TextLayout.Truncate(paint, "", 100f) == "");

            var cut = TextLayout.Truncate(paint, "这是一条很长很长的日志内容需要被截断掉", 60f);
            Check("放不下 → 以省略号结尾", cut.EndsWith(TextLayout.Ellipsis), cut);
            Check("截断后宽度 ≤ 上限", paint.MeasureText(cut) <= 60f + 0.01f,
                paint.MeasureText(cut).ToString("F2"));
            Check("截断确实去掉了内容（比原文短）", cut.Length < "这是一条很长很长的日志内容需要被截断掉".Length, cut);

            // 宽度连省略号都放不下 → 只给省略号（绝不返回空串，否则界面上那格会「消失」）
            var nothing = TextLayout.Truncate(paint, "很长的内容", 1f);
            Check("宽度极窄 → 只给省略号（不返回空串）", nothing == TextLayout.Ellipsis, nothing);
        }

        // ── 8. ★ 新旧算法等价性（二分 vs 逐字增长）★ ─────────────────
        // 这是本次「性能优化」的正确性保证：改了找边界的方式，结果必须一模一样。
        {
            var samples = new[]
            {
                "2026-09-20 23:14:07 来自 192.168.1.23 → 手机消息：测试",
                "abcdefghijklmnopqrstuvwxyz0123456789",
                "中英混排 mixed 内容 content 12345",
                new string('x', 200),
                "短",
            };
            var widths = new[] { 40f, 80f, 120f, 260f };

            bool allMatch = true;
            string mismatch = "";
            foreach (var s in samples)
            {
                foreach (var w in widths)
                {
                    var fast = TextLayout.Wrap(paint, s, w);
                    var slow = WrapByGrowing(paint, s, w);   // 旧的「逐字增长」参考实现
                    if (fast.Count != slow.Count)
                    {
                        allMatch = false;
                        mismatch = $"[{s[..Math.Min(12, s.Length)]}…] w={w}: 二分{fast.Count}行 vs 逐个{slow.Count}行";
                        break;
                    }
                    for (int i = 0; i < fast.Count; i++)
                    {
                        if (fast[i] != slow[i])
                        {
                            allMatch = false;
                            mismatch = $"[{s[..Math.Min(12, s.Length)]}…] w={w} 第{i}行: 「{fast[i]}」 vs 「{slow[i]}」";
                            break;
                        }
                    }
                    if (!allMatch) break;
                }
                if (!allMatch) break;
            }
            Check("★ 二分查找与逐字增长结果完全一致（性能优化未改变行为）", allMatch, mismatch);
        }

        // ── 9. 截断的等价性 ──────────────────────────────────────────
        {
            bool allMatch = true;
            string mismatch = "";
            foreach (var s in new[] { "很长的一条测试文本内容需要截断处理", "abcdefghijklmnopqrstuvwxyz", "中等长度内容" })
            {
                foreach (var w in new[] { 30f, 60f, 100f, 200f })
                {
                    var fast = TextLayout.Truncate(paint, s, w);
                    var slow = TruncateByShrinking(paint, s, w);
                    if (fast != slow) { allMatch = false; mismatch = $"w={w}: 「{fast}」 vs 「{slow}」"; break; }
                }
                if (!allMatch) break;
            }
            Check("★ 二分截断与逐字缩短结果完全一致", allMatch, mismatch);
        }

        return (passed, failed);
    }

    /// <summary>
    /// 参考实现：旧版「每次多取一个字，直到超宽」的折行（只用于对照，不参与生产）。
    /// 保留它是为了让「性能优化不改变行为」这条断言有可执行的依据，而不是靠口头保证。
    /// </summary>
    private static List<string> WrapByGrowing(SKPaint paint, string text, float maxWidth)
    {
        var result = new List<string>(1);
        if (text.Length == 0) { result.Add(""); return result; }
        if (maxWidth <= 8f || paint.MeasureText(text) <= maxWidth) { result.Add(text); return result; }

        const int maxRowsPerLine = 40;
        int start = 0;
        while (start < text.Length && result.Count < maxRowsPerLine)
        {
            int length = 1;
            while (start + length <= text.Length && paint.MeasureText(text.Substring(start, length)) <= maxWidth)
                length++;
            length = Math.Max(1, length - 1);
            result.Add(text.Substring(start, length));
            start += length;
        }
        if (start < text.Length) result.Add("…");
        return result;
    }

    /// <summary>参考实现：旧版「一个字一个字缩短」的截断（只用于对照）。</summary>
    private static string TruncateByShrinking(SKPaint paint, string value, float maxWidth)
    {
        if (value.Length == 0) return "";
        if (paint.MeasureText(value) <= maxWidth) return value;

        const string ellipsis = "…";
        float ellipsisWidth = paint.MeasureText(ellipsis);
        for (int length = value.Length - 1; length > 0; length--)
        {
            var candidate = value[..length];
            if (paint.MeasureText(candidate) + ellipsisWidth <= maxWidth) return candidate + ellipsis;
        }
        return ellipsis;
    }
}
