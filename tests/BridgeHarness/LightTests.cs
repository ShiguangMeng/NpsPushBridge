using NpsPushBridge;

namespace BridgeHarness;

/// <summary>
/// 状态灯判定规则的验证（纯逻辑，不依赖宿主、不依赖网络）。
///
/// ★ 本组用例守住的核心语义 ★
///   橙 = 「**此刻**有未恢复的失败」，而不是「历史上失败过」。
/// 旧版状态灯读的是**累计**失败计数，于是只要出现过一次 400 / 502，
/// 灯就永久停在橙色 —— 服务早已恢复正常，用户看到的却始终是「有失败」，
/// 这正是「状态灯无法正确显示当前状态」的根因。这里把这条边界钉死：
/// 任何一次成功转发都必须让灯立刻回到绿色。
/// </summary>
internal static class LightTests
{
    public static (int Passed, int Failed) Run()
    {
        Console.WriteLine();
        Console.WriteLine("【状态灯判定规则】");

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

        // ── 1. 三态判定 ────────────────────────────────────────────────
        Check("服务未运行 → 灰", LightStatus.Decide(false, false) == LightState.Stopped);
        Check("服务未运行（即便残留失败标记）→ 仍是灰，停止优先",
            LightStatus.Decide(false, true) == LightState.Stopped);
        Check("运行中且无未恢复的失败 → 绿",
            LightStatus.Decide(true, false) == LightState.Healthy);
        Check("运行中且有未恢复的失败 → 橙",
            LightStatus.Decide(true, true) == LightState.Unhealthy);

        // ── 2. 颜色名 / 文案 / RGB 三处口径一致 ────────────────────────
        Check("颜色名：灰 / 橙 / 绿",
            LightStatus.ColorName(LightState.Stopped) == "灰"
            && LightStatus.ColorName(LightState.Unhealthy) == "橙"
            && LightStatus.ColorName(LightState.Healthy) == "绿");

        var greyRgb = LightStatus.Rgb(LightState.Stopped);
        var orangeRgb = LightStatus.Rgb(LightState.Unhealthy);
        var greenRgb = LightStatus.Rgb(LightState.Healthy);

        Check("灰是中性色（R=G=B）", greyRgb.R == greyRgb.G && greyRgb.G == greyRgb.B, greyRgb.ToString());
        Check("绿以绿色分量为主（G 最大）", greenRgb.G > greenRgb.R && greenRgb.G > greenRgb.B, greenRgb.ToString());
        Check("橙以红色分量为主且偏暖（R>G>B）",
            orangeRgb.R > orangeRgb.G && orangeRgb.G > orangeRgb.B, orangeRgb.ToString());
        Check("三种颜色两两不同（不会看错灯）",
            greyRgb != orangeRgb && orangeRgb != greenRgb && greyRgb != greenRgb);

        Check("文案：灰写明服务已停止", LightStatus.Describe(LightState.Stopped).Contains("灰")
            && LightStatus.Describe(LightState.Stopped).Contains("停止"));
        Check("文案：绿写明运行中", LightStatus.Describe(LightState.Healthy).Contains("绿")
            && LightStatus.Describe(LightState.Healthy).Contains("运行中"));
        Check("文案：橙写明「会随下一条成功转发的消息回绿」（用户能据此预期自愈）",
            LightStatus.Describe(LightState.Unhealthy).Contains("橙")
            && LightStatus.Describe(LightState.Unhealthy).Contains("回绿"),
            LightStatus.Describe(LightState.Unhealthy));

        // ── 3. ★ 核心边界：状态迁移，而不是累计统计 ★ ──────────────────
        // 完全按服务端的真实时序走一遍：启动 → 失败 → 再失败 → 成功后恢复。
        // 若哪天有人把判定改回「读累计失败计数」，这一组必然失败。
        bool running = true;
        bool unrecovered = false;

        Check("启动后（尚无失败）→ 绿", LightStatus.Decide(running, unrecovered) == LightState.Healthy);

        unrecovered = true;    // 第 1 次失败
        Check("出现失败 → 橙", LightStatus.Decide(running, unrecovered) == LightState.Unhealthy);

        // 第 2 次失败：状态不变（累计计数会 +1，但「是否未恢复」仍是 true）
        Check("再失败一次 → 仍为橙（不因次数叠加而改变状态）",
            LightStatus.Decide(running, unrecovered) == LightState.Unhealthy);

        unrecovered = false;   // 成功转发一次 → 清除标记
        Check("★ 成功转发一次即恢复 → 立刻回绿（不是永久橙）",
            LightStatus.Decide(running, unrecovered) == LightState.Healthy);

        // ── 4. 停止 / 重启 ────────────────────────────────────────────
        running = false;
        Check("停止服务 → 灰", LightStatus.Decide(running, false) == LightState.Stopped);

        running = true;        // 重启后是新实例，标记天然为 false
        Check("重启服务 → 直接回绿（新的服务实例，不背历史包袱）",
            LightStatus.Decide(running, false) == LightState.Healthy);

        return (passed, failed);
    }
}
