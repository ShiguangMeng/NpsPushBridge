using SkiaSharp;

namespace NpsPushBridge;

/// <summary>
/// 文本度量与排版的小工具：**按宽度折行**与**按宽度截断**。
///
/// ★ 为什么单独成文件 ★
/// 这两件事原先写在 <see cref="ConfigPanel"/> 里（`private static`），而 ConfigPanel 依赖 WinForms、
/// 进不了验证工程。但这些逻辑又恰恰是「最容易写错、错了也最难看出来」的一类 ——
/// 折行边界差一个字符只会让日志看起来怪一点，不会有任何异常抛出来。
/// 抽成不依赖任何 UI 类型的纯函数后，就能在 <c>BridgeHarness</c> 里用真实 SKPaint 逐条验证。
///
/// ★ 为什么用二分查找找边界 ★
/// 直觉写法是「一个字一个字加、超宽了就退回一个字」，单个长行要测 O(n) 次，且每次都分配子串；
/// 在「每帧对每条可见日志都跑一遍」的场景下是实打实的 O(n²) 热点（长行如异常堆栈尤其明显）。
/// 二分查找把测量次数降到 O(log n)；配合 <c>MeasureText(ReadOnlySpan&lt;char&gt;)</c> 重载，
/// 中间过程**零字符串分配**。
///
/// 两者结果完全一致（都是「放得下就尽量多放」），只是找边界更快。
/// </summary>
internal static class TextLayout
{
    /// <summary>一行最多折成多少行（极端超长行的兜底，避免一次绘制生成上千行）。</summary>
    public const int MaxRowsPerLine = 40;

    /// <summary>小于这个宽度就认为「放不下任何东西」，直接不折（避免除零与死循环）。</summary>
    public const float MinUsableWidth = 8f;

    public const string Ellipsis = "…";

    /// <summary>
    /// 按可用宽度把一段文字折成若干行。文字本身就放得下时只返回一行、且只测一次。
    /// 超出 <see cref="MaxRowsPerLine"/> 行时，末行以 <see cref="Ellipsis"/> 标记「还有内容被截掉」。
    /// </summary>
    public static List<string> Wrap(SKPaint paint, string text, float maxWidth)
    {
        var result = new List<string>(1);
        if (text.Length == 0) { result.Add(""); return result; }
        if (maxWidth <= MinUsableWidth || paint.MeasureText(text) <= maxWidth) { result.Add(text); return result; }

        int start = 0;
        while (start < text.Length && result.Count < MaxRowsPerLine)
        {
            int length = FittingLength(paint, text, start, maxWidth);
            result.Add(text.Substring(start, length));
            start += length;
        }
        if (start < text.Length) result.Add(Ellipsis);
        return result;
    }

    /// <summary>
    /// 从 <paramref name="start"/> 起，最多能取多少个字符仍不超过 <paramref name="maxWidth"/>。
    /// 返回值恒 ≥ 1（否则调用方的循环会原地打转）。
    /// </summary>
    public static int FittingLength(SKPaint paint, string text, int start, float maxWidth)
    {
        int remaining = text.Length - start;
        if (remaining <= 0) return 1;

        // 常见情形（短行整条放得下）：一次测量就返回，走这条的占绝大多数
        if (paint.MeasureText(text.AsSpan(start)) <= maxWidth) return remaining;

        int lo = 1;          // 已知至少 1 个字符能放下
        int hi = remaining;  // 已知整体放不下（上一行已排除）
        while (lo + 1 < hi)
        {
            int mid = lo + (hi - lo) / 2;
            if (paint.MeasureText(text.AsSpan(start, mid)) <= maxWidth) lo = mid;
            else hi = mid;
        }
        return lo;
    }

    /// <summary>
    /// 截断到能放下的长度并加省略号（省略号自身也占宽度，所以可用宽度要先减掉它）。
    /// 放不下任何字符时只返回省略号本身。
    /// </summary>
    public static string Truncate(SKPaint paint, string value, float maxWidth)
    {
        if (value.Length == 0) return "";
        if (paint.MeasureText(value) <= maxWidth) return value;

        float budget = maxWidth - paint.MeasureText(Ellipsis);
        if (budget <= 0f) return Ellipsis;

        // 二分找「最长的、宽度 ≤ budget 的前缀」；lo 已知放得下（0 个字符），hi 已知放不下
        int lo = 0, hi = value.Length;
        while (lo + 1 < hi)
        {
            int mid = lo + (hi - lo) / 2;
            if (paint.MeasureText(value.AsSpan(0, mid)) <= budget) lo = mid;
            else hi = mid;
        }
        return lo <= 0 ? Ellipsis : value[..lo] + Ellipsis;
    }

    /// <summary>
    /// 让一行文字在矩形里**真正纵向居中**的基线 y。
    ///
    /// ★ 这里踩过两个坑，都被实测抓到 ★
    ///
    /// 坑 1（原始写法）：<c>rect.MidY + 4f</c> / <c>+5f</c>。按 12px 字号硬凑的常数，
    /// 字号一换（按钮 11px、标题 13.5px）就不再居中。
    ///
    /// 坑 2（上一版的「修复」）：<c>rect.MidY + (Ascent + Descent) / 2</c>。
    /// 这个式子在别处常见，但**符号搞反了**：SkiaSharp 的 <c>Ascent</c> 是**负数**
    /// （基线以上）、<c>Descent</c> 是**正数**。雅黑 11px 实测 Ascent=-11.172 / Descent=2.798，
    /// 于是 <c>(A+D)/2 = -4.187</c> —— 基线被**抬高** 4.19px。文字在 20px 高的按钮里
    /// 视觉中心只剩 1.813（应为 10），即整整偏上 **8.19px**，占按钮高度的四成 ——
    /// 这正是「按钮与文字位置错位」的根源，且字号越大偏得越多（13.5px 时偏 10.14px）。
    ///
    /// 正确做法：不用字体度量，直接用**这一行文字自己的字形包围盒**居中。
    /// <see cref="SKPaint.MeasureText(string, ref SKRect)"/> 会把该串的紧致外框写进
    /// <paramref name="bounds"/>（相对基线：Top 为负、Bottom 为正），于是
    ///     基线 = 矩形中心 − (bounds.Top + bounds.Bottom) / 2
    /// 实测该式对 11px / 12px / 13.5px 的中文、<c>▲</c>、<c>+10</c>、<c>×</c>
    /// 全部得到偏移 <c>0.000</c>。逐串计算比套固定常数更准：<c>▲</c> 这类符号的字形
    /// 比汉字矮一截也不会上浮 —— 它也遵循自己的包围盒。
    /// </summary>
    /// <param name="paint">已设好字号与字体的画笔（本函数只读度量，不改画笔状态）。</param>
    /// <param name="rect">文字要居中的矩形（只用到 <c>MidY</c>）。</param>
    /// <param name="text">将要绘制的**实际文本**（截断后、省略号也要算进去）。</param>
    public static float VerticallyCenteredBaseline(SKPaint paint, SKRect rect, string text)
    {
        if (string.IsNullOrEmpty(text)) return rect.MidY;

        var bounds = new SKRect();
        try
        {
            paint.MeasureText(text, ref bounds);
        }
        catch
        {
            // 极端情况下度量失败：退回「按字体度量估算」，至少方向是对的（不再反向偏移）
            var fm = paint.FontMetrics;
            return rect.MidY + (fm.Descent - fm.Ascent) / 2f - fm.Descent;
        }

        // 全空白串等情况下 bounds 全为 0，此时没有可居中的形体，按矩形中心画
        if (bounds.Top == 0f && bounds.Bottom == 0f) return rect.MidY;

        return rect.MidY - (bounds.Top + bounds.Bottom) / 2f;
    }

    /// <summary>
    /// 该字体能否**完整**画出某个字符串 —— 用来在测试里把「选到不含中文的字体」这类
    /// 环境依赖问题变成一条可回归的断言（豆腐块 = 字形 id 0 = <c>.notdef</c>）。
    /// </summary>
    public static bool CanRender(SKTypeface typeface, string text, float textSize = 12f)
    {
        if (string.IsNullOrEmpty(text)) return true;
        try
        {
            using var paint = new SKPaint { Typeface = typeface, TextSize = textSize };
            return paint.ContainsGlyphs(text);
        }
        catch
        {
            return false;
        }
    }
}
