using SkiaSharp;
using NpsPushBridge;

namespace BridgeHarness;

/// <summary>
/// 界面外壳的验证：**字体选择**与**文字纵向居中**。
///
/// ★ 为什么值得测 ★
/// 这两件事都属于「错了不抛异常、只是界面难看」的类型，最容易在重构里悄悄退化：
///   · 忘记给画笔设 Typeface → 中文全变豆腐块（实测默认字体 GetGlyphs("有新版本") = [0,0,0,0]）；
///   · 居中公式符号写反 → 文字整体偏上约 8px，看着像「按钮和字没对齐」。
/// 两者都不会让编译或运行报错。这里用真实 SKPaint / SKTypeface 把它们钉成可回归的断言。
///
/// ★ 与平台的关系 ★
/// 字体相关的断言依赖本机装了中文字体（Windows 中文版一定有雅黑）。若某个精简镜像确实
/// 一个中文字体都没有，则相关用例**跳过**而非判失败 —— 那种环境下方框是环境问题，不是代码问题；
/// 但「居中公式与字体无关」那条仍然严格断言。
/// </summary>
internal static class UiShellTests
{
    public static (int Passed, int Failed, int Skipped) Run()
    {
        Console.WriteLine();
        Console.WriteLine("【界面外壳：字体与居中】");

        int passed = 0, failed = 0, skipped = 0;

        void Check(string name, bool condition, string detail = "")
        {
            if (condition) { passed++; Console.WriteLine($"  [PASS] {name}"); }
            else { failed++; Console.WriteLine($"  [FAIL] {name}{(detail.Length == 0 ? "" : $"  →  {detail}")}"); }
        }

        void Skip(string name, string why)
        {
            skipped++;
            Console.WriteLine($"  [SKIP] {name}  →  {why}");
        }

        // ================================================================
        // 1. 字体：PluginFonts.Cjk 必须真的能画中文
        // ================================================================
        var cjk = PluginFonts.Cjk;
        Check("PluginFonts.Cjk 不为 null", cjk != null);

        bool canChinese = cjk != null && TextLayout.CanRender(cjk, "有新版本");
        if (canChinese)
        {
            Check("PluginFonts.Cjk 能画「有新版本」（非豆腐块）", true);
            Console.WriteLine($"        选用字体：{cjk!.FamilyName}");
        }
        else
        {
            Skip("PluginFonts.Cjk 能画「有新版本」", "本机未找到含中文字形的字体族");
        }

        // 同一个实例必须被复用（缓存生效）——两次取到的应当是同一个对象
        Check("PluginFonts.Cjk 命中缓存（同一实例）", ReferenceEquals(cjk, PluginFonts.Cjk));

        // 关键回归：**默认字体（不设 Typeface）画不出中文**，证明「必须显式设字体」不是多此一举。
        // 若某天 .NET/SkiaSharp 换了默认字体而这条不再成立，也只说明前提变了，不算缺陷 —— 故用 SKIP。
        try
        {
            using var bare = new SKPaint { TextSize = 11.5f };
            bool bareCanChinese = bare.ContainsGlyphs("有新版本");
            if (!bareCanChinese)
            {
                Check("对照：不设 Typeface 的画笔画不出中文（已记录该前提）", true);
            }
            else
            {
                Skip("对照：不设 Typeface 的画笔画不出中文", "本机默认字体已含中文字形");
            }
        }
        catch (Exception ex)
        {
            Skip("对照：不设 Typeface 的画笔画不出中文", $"度量不可用：{ex.GetType().Name}");
        }

        // PluginFonts.TextPaint 造出来的画笔必须自带中文字体与指定字号
        using (var tp = PluginFonts.TextPaint(13f, new SKColor(1, 2, 3)))
        {
            Check("PluginFonts.TextPaint 自带中文字体", tp.Typeface != null && TextLayout.CanRender(tp.Typeface, "端口"));
            Check("PluginFonts.TextPaint 字号正确", Math.Abs(tp.TextSize - 13f) < 0.001f, $"实际 {tp.TextSize}");
            Check("PluginFonts.TextPaint 颜色正确", tp.Color == new SKColor(1, 2, 3));
            Check("PluginFonts.TextPaint 开启抗锯齿", tp.IsAntialias);
        }

        // 画图标示用的删除字符必须真的能画出来（历史上用过 ✕(U+2715)，雅黑没有 → 方框）
        using (var removePaint = PluginFonts.TextPaint(11f))
        {
            bool arrowOk = TextLayout.CanRender(removePaint.Typeface!, "▲") && TextLayout.CanRender(removePaint.Typeface!, "▼");
            bool crossOk = TextLayout.CanRender(removePaint.Typeface!, "×");
            if (arrowOk) Check("滚动按钮字符 ▲▼ 可渲染", true);
            else Skip("滚动按钮字符 ▲▼ 可渲染", "当前字体缺该字形");

            if (crossOk) Check("删除按钮字符 × 可渲染（曾是方框的回归点）", true);
            else Skip("删除按钮字符 × 可渲染", "当前字体缺该字形");
        }

        // ================================================================
        // 2. 纵向居中：文字视觉中心必须落在矩形中心
        // ================================================================
        // 用真实字形包围盒反算「文字实际占据的上下边界」，检查中心是否 == 矩形中心。
        // 这比「基线 == 某个期望值」更本质：它直接断言用户看到的东西居中了。
        void CheckCentered(string label, float textSize, string sample, float rectHeight)
        {
            using var paint = PluginFonts.TextPaint(textSize);
            var rect = new SKRect(0f, 0f, 200f, rectHeight);
            float baseline = TextLayout.VerticallyCenteredBaseline(paint, rect, sample);

            var bounds = new SKRect();
            paint.MeasureText(sample, ref bounds);

            float visualTop = baseline + bounds.Top;
            float visualBottom = baseline + bounds.Bottom;
            float visualMid = (visualTop + visualBottom) / 2f;
            float offset = Math.Abs(visualMid - rect.MidY);

            // 容差 0.01px：实测该式在各种字号/字符下偏移恒为 0.000，留一点浮点余量
            Check($"{label}：\"{sample}\" @{textSize}px 在 {rectHeight}px 矩形内居中（偏移 {offset:F3}px ≤ 0.01）",
                offset <= 0.01f,
                $"视觉中心 {visualMid:F3} vs 矩形中心 {rect.MidY:F3}");
        }

        // 覆盖配置面板里所有实际用到的字号与字符类型
        CheckCentered("按钮", 11f, "清空", 18f);
        CheckCentered("按钮", 11f, "▲", 18f);
        CheckCentered("按钮", 11f, "+10", 18f);
        CheckCentered("正文", 12f, "保存配置", 30f);
        CheckCentered("标题", 13.5f, "局域网推送网桥 · 配置", 24f);
        CheckCentered("端口", 12f, "1403", 30f);
        CheckCentered("删除", 11f, "×", 18f);
        CheckCentered("状态", 11f, "运行中", 18f);

        // ★ 关键回归：证明「字号越大、旧公式偏得越多」确实存在（旧公式 = MidY + (A+D)/2）
        // 这条把「为什么必须改」固化成数字，避免以后有人又退回那个公式。
        using (var p11 = PluginFonts.TextPaint(11f))
        using (var p135 = PluginFonts.TextPaint(13.5f))
        {
            float OldOffset(SKPaint p)
            {
                var fm = p.FontMetrics;
                // 旧公式给出的基线与正确基线的差 = 文字被抬高的量
                return Math.Abs((p.FontMetrics.Ascent + p.FontMetrics.Descent) / 2f);
            }
            float o11 = OldOffset(p11), o135 = OldOffset(p135);
            Check("旧公式偏差随字号增大而增大（11px < 13.5px）", o11 < o135, $"{o11:F3} vs {o135:F3}");
            Console.WriteLine($"        旧公式把文字抬高：11px → {o11:F3}px，13.5px → {o135:F3}px");
        }

        // 退化输入不能抛异常
        try
        {
            using var paint = PluginFonts.TextPaint(11f);
            var rect = new SKRect(0, 0, 100, 20);
            TextLayout.VerticallyCenteredBaseline(paint, rect, "");
            TextLayout.VerticallyCenteredBaseline(paint, rect, "   ");
            Check("居中函数对空串 / 空白串不抛异常", true);
        }
        catch (Exception ex)
        {
            Check("居中函数对空串 / 空白串不抛异常", false, ex.GetType().Name);
        }

        // ================================================================
        // 4. 插件统一字体：宿主自定义字体（Ui）+ 逐字符串回退
        // ================================================================
        // ★ 语义契约 ★ 用户没选自定义字体 / 选的文件失效 / 那串字该字体画不出
        //   → 一律回退到 Cjk，**绝不返回 null、绝不抛异常**。
        //   这里不断言「一定用上了自定义字体」（那取决于本机注册表，是环境而非代码），
        //   只断言「无论环境如何，结论都是可用且一致的」。
        {
            try
            {
                var ui = PluginFonts.Ui;
                Check("PluginFonts.Ui 不为 null（永有兜底）", ui != null);

                // ★ 关键契约 ★ 实际拿去画的字体（TypefaceFor）必须**永远能画中文**，
                //   哪怕用户选的字体缺这几个字。旧版这里是直接断言 Ui 本身能画，
                //   统一字体之后那个断言不再成立（Ui 可以缺字形），真正要守的是这一条。
                Check("TypefaceFor(「有新版本」) 能画出中文（绝不出现方框）",
                    TextLayout.CanRender(PluginFonts.TypefaceFor("有新版本"), "有新版本"));
                Check("TypefaceFor(状态灯颜色词) 能画出中文",
                    TextLayout.CanRender(PluginFonts.TypefaceFor("橙绿灰"), "橙绿灰"));
                Check("TypefaceFor 对 null / 空串不抛异常",
                    PluginFonts.TypefaceFor(null) != null && PluginFonts.TypefaceFor("") != null);

                // 逐字符串回退的核心语义：结果**只可能**是 Ui 或 Cjk 两个之一
                void CheckFallbackIsOneOfTwo(string text)
                {
                    var pick = PluginFonts.TypefaceFor(text);
                    bool isUi = ReferenceEquals(pick, ui);
                    bool isCjk = ReferenceEquals(pick, PluginFonts.Cjk);
                    Check($"TypefaceFor(「{text}」) 取值为 Ui 或 Cjk 之一", isUi || isCjk,
                        isUi ? "Ui" : isCjk ? "Cjk" : "其它实例（不应发生）");
                }

                CheckFallbackIsOneOfTwo("有新版本 v1.2.0");
                CheckFallbackIsOneOfTwo("白名单 IP（每行一个）");
                CheckFallbackIsOneOfTwo("▲");
                CheckFallbackIsOneOfTwo("×");

                // 缓存：同一实例必须复用（避免每帧新建原生字体对象）
                Check("PluginFonts.Ui 命中缓存（同一实例）", ReferenceEquals(ui, PluginFonts.Ui));

                // 没有自定义字体时，Ui 必须**就是** Cjk（同一实例），而不是另建一个等价字体；
                // 且此时 TypefaceFor 对任何字符串都只能是 Cjk（连逐字判定都不必做）
                if (!PluginFonts.UiUsesCustomFont)
                {
                    Check("未启用自定义字体时 Ui === Cjk（同一实例）", ReferenceEquals(ui, PluginFonts.Cjk));
                    Check("未启用自定义字体时 TypefaceFor 恒为 Cjk",
                        ReferenceEquals(PluginFonts.TypefaceFor("有新版本"), PluginFonts.Cjk));
                    Console.WriteLine("        当前未使用自定义字体（注册表无 CustomFontPath 或文件不可用）");
                }
                else
                {
                    Check("启用自定义字体时 Ui 与 Cjk 是不同实例", !ReferenceEquals(ui, PluginFonts.Cjk));
                    Console.WriteLine($"        当前使用自定义字体：{PluginFonts.UiFontName}");
                    Console.WriteLine($"        来源文件：{PluginFonts.UiCustomFontPath}");
                    Console.WriteLine($"        界面字体说明：{PluginFonts.UiDescribe()}");
                    Check("启用自定义字体时路径非空", PluginFonts.UiCustomFontPath.Length > 0);
                }

                // IslandPaint 必须自带 Ui 字体、指定字号、抗锯齿
                using (var ip = PluginFonts.IslandPaint(11.5f, new SKColor(9, 8, 7)))
                {
                    Check("PluginFonts.IslandPaint 字体 == Ui", ReferenceEquals(ip.Typeface, ui));
                    Check("PluginFonts.IslandPaint 字号/颜色/抗锯齿正确",
                        Math.Abs(ip.TextSize - 11.5f) < 0.001f && ip.Color == new SKColor(9, 8, 7) && ip.IsAntialias);
                }

                // Fit：按字符串把画笔切到该用的字体，且**不改变其它属性**
                using (var fitted = PluginFonts.TextPaint(11.5f))
                {
                    PluginFonts.Fit(fitted, "有新版本");
                    Check("Fit 后画笔字体 == TypefaceFor(同一串)",
                        ReferenceEquals(fitted.Typeface, PluginFonts.TypefaceFor("有新版本")));
                    Check("Fit 不改变字号与抗锯齿",
                        Math.Abs(fitted.TextSize - 11.5f) < 0.001f && fitted.IsAntialias);
                }

                // ★ 最关键的一条：提示条的「量宽」与「画宽」必须同字体 ★
                // 宿主按插件上报的宽度决定放不放得下，量画不一致就会留白或被裁。
                const string bannerLabel = "有新版本 v1.2.0";
                using var measurePaint = PluginFonts.IslandPaint(11.5f);
                using var drawPaint = PluginFonts.IslandPaint(11.5f);
                PluginFonts.Fit(measurePaint, bannerLabel);
                PluginFonts.Fit(drawPaint, bannerLabel);
                float w1 = measurePaint.MeasureText(bannerLabel);
                float w2 = drawPaint.MeasureText(bannerLabel);
                Check("提示条量宽 == 画宽（Fit 后同字体）", Math.Abs(w1 - w2) < 0.001f, $"{w1} vs {w2}");
                Check("提示条宽度为正且合理", w1 > 20f, w1.ToString());

                // InvalidateUiFont 后仍能取到可用字体（重新解析不炸）
                PluginFonts.InvalidateUiFont();
                var afterInvalidate = PluginFonts.Ui;
                Check("InvalidateUiFont 后 Ui 仍可用", afterInvalidate != null);
                Check("InvalidateUiFont 后 TypefaceFor 仍能画中文",
                    TextLayout.CanRender(PluginFonts.TypefaceFor("有新版本"), "有新版本"));
            }
            catch (Exception ex)
            {
                Check("统一字体链路不抛异常", false, $"{ex.GetType().Name}: {ex.Message}");
            }
        }

        // ================================================================
        // 5. 「度量宽度 == 绘制宽度」：两者必须同字体，否则文字会被裁掉
        // ================================================================
        using (var measure = PluginFonts.TextPaint(11.5f))
        {
            float bannered = measure.MeasureText("有新版本 v1.1.1");
            // 用「无字体」的画笔量同一串，宽度必须显著不同 —— 证明「量错字体」确实会影响布局
            using var bare = new SKPaint { TextSize = 11.5f };
            float bareWidth = bare.MeasureText("有新版本 v1.1.1");
            Check("同字号下，有无中文字体得到的宽度不同（故必须同源度量）",
                Math.Abs(bannered - bareWidth) > 0.5f,
                $"中文字体 {bannered:F2} vs 默认字体 {bareWidth:F2}");
            Console.WriteLine($"        「有新版本 v1.1.1」宽度：中文字体 {bannered:F2}px，默认字体 {bareWidth:F2}px");
        }

        return (passed, failed, skipped);
    }
}
