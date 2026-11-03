namespace NpsPushBridge;

/// <summary>状态灯的三态。</summary>
internal enum LightState
{
    /// <summary>服务已停止（灰）。</summary>
    Stopped,

    /// <summary>服务运行中且无未恢复的失败（绿）。</summary>
    Healthy,

    /// <summary>服务运行中，但最近有过失败且尚未恢复（橙）。</summary>
    Unhealthy,
}

/// <summary>
/// 状态灯的判定规则（纯逻辑，不依赖宿主，便于单测）。
///
/// ★ 关键语义：橙色表示「**当前**有未恢复的失败」，而不是「历史上失败过」★
/// 早先的实现读的是累计失败计数，于是只要出现过一次 400/502，灯就会**永久橙色** ——
/// 服务明明已经恢复正常，用户看到的却仍是「有失败」，这正是「状态灯无法正确显示当前状态」。
/// 现在改为：任何一次**成功转发**都会清除该标记，灯随即回到绿色；
/// 因此灯反映的始终是「此刻健不健康」。
/// </summary>
internal static class LightStatus
{
    /// <summary>按「服务是否运行」与「有无未恢复的失败」决定灯的颜色。</summary>
    public static LightState Decide(bool serviceRunning, bool hasUnrecoveredFailure)
        => !serviceRunning ? LightState.Stopped
         : hasUnrecoveredFailure ? LightState.Unhealthy
         : LightState.Healthy;

    /// <summary>灯色的中文名（日志 / 状态页 / 文档口径一致）。</summary>
    public static string ColorName(LightState state) => state switch
    {
        LightState.Stopped => "灰",
        LightState.Unhealthy => "橙",
        _ => "绿",
    };

    /// <summary>
    /// 灯的实际 RGB。**刻意与 <see cref="ColorName"/> 放在一起**：
    /// 组件绘制、状态页文字、日志文案共用同一份判定，不会出现「灯是绿的可日志写着橙」这类走样。
    /// 返回裸 RGB 而不是 SkiaSharp 的 SKColor，是为了让本文件保持零依赖、可被测试工程直接编译。
    /// </summary>
    public static (byte R, byte G, byte B) Rgb(LightState state) => state switch
    {
        LightState.Stopped => (130, 130, 130),      // 灰：服务已停止
        LightState.Unhealthy => (240, 173, 78),     // 橙：有未恢复的失败
        _ => (76, 175, 80),                         // 绿：运行中且健康
    };

    /// <summary>给日志用的一句话说明（用户可据此核对灯与实际状态是否一致）。</summary>
    public static string Describe(LightState state) => state switch
    {
        LightState.Stopped => "灰（服务已停止）",
        LightState.Unhealthy => "橙（有未恢复的失败，下一条成功转发的消息即回绿）",
        _ => "绿（服务运行中）",
    };
}
