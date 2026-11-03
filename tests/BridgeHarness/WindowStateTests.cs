using NpsPushBridge;

namespace BridgeHarness;

/// <summary>
/// <see cref="WindowState"/>（窗口尺寸 / 位置 / 置顶的持久化与「摆回屏幕内」）的验证。
///
/// ★ 为什么这些值得测 ★
/// 唯一容易出错的地方是**边界**，而且出错时现象都是「窗口不见了」或「位置莫名其妙」——
/// 靠手点界面几乎无法覆盖（要么拔显示器、要么改分辨率才复现）：
///   · 上次的位置可能完全在屏幕之外（换了显示器 / 副屏被拔掉）；
///   · 保存的尺寸可能小于布局下限或大于屏幕；
///   · 存的是字符串，旧值 / 手改过的半截值必须能安全降级。
/// </summary>
internal static class WindowStateTests
{
    private static int _passed, _failed;

    private static void Check(string name, bool ok, string detail = "")
    {
        if (ok) { _passed++; Console.WriteLine($"  [PASS] {name}"); }
        else { _failed++; Console.WriteLine($"  [FAIL] {name}{(detail.Length > 0 ? "  —— " + detail : "")}"); }
    }

    /// <summary>一块 1920×1080 的屏幕（左上角在 0,0），便于手算预期值。</summary>
    private static WindowState Fresh(int w = 660, int h = 620)
        => new() { Width = w, Height = h };

    public static (int Passed, int Failed) Run()
    {
        Console.WriteLine("【配置窗口：尺寸 / 位置 / 置顶状态】");

        NormalizeSection();
        PersistSection();
        TopmostSection();

        return (_passed, _failed);
    }

    private static void NormalizeSection()
    {
        Console.WriteLine("  -- 摆回屏幕内 --");

        // 没记过位置 → 居中
        var centered = Fresh();
        centered.Normalize(0, 0, 1920, 1080, 340, 526);
        Check("★ 首次打开（没记过位置）→ 居中",
            centered.X == (1920 - 660) / 2 && centered.Y == (1080 - 620) / 2,
            $"{centered.X},{centered.Y}");
        Check("居中之后就算「记过位置」了（否则每次打开都重新居中）", centered.HasBounds);

        // 位置完全在屏幕外（副屏被拔掉）→ 拉回来
        var offscreen = Fresh();
        offscreen.X = 3000;
        offscreen.Y = 2000;
        offscreen.HasBounds = true;
        offscreen.Normalize(0, 0, 1920, 1080, 340, 526);
        Check("★ 位置在屏幕右下方之外 → 拉回屏幕内（否则用户「什么都看不到」）",
            offscreen.X <= 1920 - WindowState.MinVisibleTitle && offscreen.Y <= 1080 - WindowState.MinVisibleTitle,
            $"{offscreen.X},{offscreen.Y}");

        // 负方向出界
        var negative = Fresh();
        negative.X = -5000;
        negative.Y = -5000;
        negative.HasBounds = true;
        negative.Normalize(0, 0, 1920, 1080, 340, 526);
        Check("★ 位置在屏幕左上方之外 → 保证标题条仍可见",
            negative.X >= 0 - negative.Width + WindowState.MinVisibleTitle && negative.Y >= 0,
            $"{negative.X},{negative.Y}");

        // 只越界一个轴时应只修那个轴（不要整体居中，否则用户摆好的另一个轴也丢了）
        var oneAxis = Fresh();
        oneAxis.X = 100;
        oneAxis.Y = 99999;
        oneAxis.HasBounds = true;
        oneAxis.Normalize(0, 0, 1920, 1080, 340, 526);
        Check("★ 只修越界的那个轴：x 保持用户的选择，y 被拉回",
            oneAxis.X == 100 && oneAxis.Y <= 1080 - WindowState.MinVisibleTitle,
            $"{oneAxis.X},{oneAxis.Y}");

        // 尺寸下限
        var tiny = Fresh(100, 80);
        tiny.Normalize(0, 0, 1920, 1080, 340, 526);
        Check("★ 尺寸小于布局下限 → 抬到下限（否则面板会被裁成不可用）",
            tiny.Width == 340 && tiny.Height == 526, $"{tiny.Width}×{tiny.Height}");

        // 尺寸大于屏幕
        var huge = Fresh(9999, 9999);
        huge.Normalize(0, 0, 1920, 1080, 340, 526);
        Check("尺寸大于屏幕但不传上限时原样保留（上限由调用方决定是否给）",
            huge.Width == 9999 && huge.Height == 9999, $"{huge.Width}×{huge.Height}");

        var capped = Fresh(9999, 9999);
        capped.Normalize(0, 0, 1920, 1080, 340, 526, maxW: 1920, maxH: 1080);
        Check("★ 传了屏幕上限时夹到屏幕尺寸",
            capped.Width == 1920 && capped.Height == 1080, $"{capped.Width}×{capped.Height}");

        // 取不到屏幕信息（多显示器枚举失败等）时不该乱动位置
        var noScreen = Fresh();
        noScreen.X = 123;
        noScreen.Y = 45;
        noScreen.HasBounds = true;
        noScreen.Normalize(0, 0, 0, 0, 340, 526);
        Check("取不到屏幕信息时不改位置（宁可保持原样，也不要把用户的位置抹掉）",
            noScreen.X == 123 && noScreen.Y == 45, $"{noScreen.X},{noScreen.Y}");
    }

    private static void PersistSection()
    {
        Console.WriteLine("  -- 持久化 --");

        var state = new WindowState { Width = 800, Height = 700, X = 120, Y = 60, HasBounds = true, Topmost = true };
        var text = state.Serialize();
        var back = WindowState.Parse(text, 660, 620);

        Check("★ 往返一致（尺寸 / 位置 / 是否记过位置 / 置顶）",
            back.Width == 800 && back.Height == 700 && back.X == 120 && back.Y == 60
            && back.HasBounds && back.Topmost,
            $"{back.Width}×{back.Height}@{back.X},{back.Y} b={back.HasBounds} t={back.Topmost}");

        // 空 / null → 默认值且「没记过位置」（这样会居中，而不是摆在 0,0）
        var empty = WindowState.Parse("", 660, 620);
        Check("空串 → 默认尺寸且未记过位置（下次打开居中）",
            empty.Width == 660 && empty.Height == 620 && !empty.HasBounds && !empty.Topmost);

        Check("null 也安全", WindowState.Parse(null, 660, 620).Width == 660);

        // 被手改坏的值
        var garbled = WindowState.Parse("w=abc;h=;;x=;y=9;b=maybe;t=yes", 660, 620);
        Check("★ 脏数据不炸：解析不出数字的键保持默认，能认出的键照常生效",
            garbled.Width == 660 && garbled.Height == 620 && garbled.Y == 9
            && !garbled.HasBounds && !garbled.Topmost,
            $"w={garbled.Width} h={garbled.Height} y={garbled.Y}");

        // 未知键（将来版本写的设置被旧版本读到）
        var future = WindowState.Parse("w=700;h=600;x=10;y=20;b=1;t=0;newKey=whatever", 660, 620);
        Check("★ 含未知键时仍能正常读（向前兼容：加字段不会让旧版本读不懂整串）",
            future.Width == 700 && future.X == 10 && future.Y == 20 && future.HasBounds && !future.Topmost);

        // 极端脏值：正整数之外
        var evil = WindowState.Parse("w=999999999999;h=-5;x=0;y=0;b=1", 660, 620);
        Check("★ 极端尺寸回落到默认并放弃位置（不让创建窗口时拿到荒谬的尺寸）",
            evil.Width == 660 && evil.Height == 620 && !evil.HasBounds,
            $"{evil.Width}×{evil.Height} b={evil.HasBounds}");

        Check("格式与字段都可读（自描述，便于人工排查）",
            text.Contains("w=800") && text.Contains("t=1"), text);
    }

    private static void TopmostSection()
    {
        Console.WriteLine("  -- 置顶 --");

        var fresh = new WindowState();
        Check("★ 默认**不置顶**（需求：取消窗口置顶，改为点按钮才置顶）", !fresh.Topmost);

        var parsed = WindowState.Parse("w=660;h=620;x=0;y=0;b=1;t=1", 660, 620);
        Check("置顶状态能持久化并被读回", parsed.Topmost);

        var legacy = WindowState.Parse("w=660;h=620;x=0;y=0;b=1", 660, 620);
        Check("旧记录（还没有 t 键）读出来是「不置顶」而不是报错", !legacy.Topmost);
    }
}
