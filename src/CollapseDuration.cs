using System.Globalization;

namespace NpsPushBridge;

/// <summary>
/// 「面板显示时长」（宿主详情页的自动收起延迟，对应 <c>IDetailPage.AutoCollapseDelay</c>）
/// 的取值范围、换算与格式化。
///
/// ★ 为什么是「面板显示时长」而不是「提醒显示时长」★
/// 2026-09-27 实测确认：**宿主不读** <c>ReminderData.Duration</c> ——
/// <c>PluginHost.PostReminder</c> 构造 <c>ToastData</c> 时根本没传时长（<c>ToastData</c> 里也没有该字段），
/// 而 <c>NotchWindow.OnPluginReminder</c> 把结束时刻**硬编码成 4 秒**
/// （<c>_toastEndTime = DateTime.Now.AddSeconds(4)</c>）。
/// 所以插件对「提醒弹窗停多久」**无能力控制**，原先那个滑块因此「拖了没反应」。
///
/// 宿主**真正开放**给插件控制的时长是详情页的自动收起延迟（插件文档第七节）：
///  - <c>TimeSpan.Zero</c>（默认）→ 用宿主内置的 **900ms**；
///  - **正数** → 自定义，宿主会**夹到 0.5s ~ 60s**；
///  - **负数**（惯例 <c>Timeout.InfiniteTimeSpan</c>）→ **全局屏蔽自动收起**：
///    鼠标离开不收、点到岛外也不收。
///
/// ★ 刻度为什么用「对数」★
/// 0.5 ~ 60 秒跨度 120 倍。线性刻度下 0.9 秒（默认值）会贴在滑块最左端一动不动的样子，
/// 而用户最常调的恰恰是 0.5~5 秒这一段。对数刻度把低端拉开，手感好得多。
/// </summary>
internal static class CollapseDuration
{
    /// <summary>宿主内置的默认延迟（900ms）—— 也是本项的默认值。</summary>
    public const float HostDefaultSeconds = 0.9f;

    /// <summary>下限（秒）。宿主会把更小的正数夹上来，这里保持一致。</summary>
    public const float MinSeconds = 0.5f;

    /// <summary>上限（秒）。同样与宿主的夹取范围一致。</summary>
    public const float MaxSeconds = 60f;

    /// <summary>默认值（秒）= 宿主默认，升级后行为不变。</summary>
    public const float DefaultSeconds = HostDefaultSeconds;

    /// <summary>小数位数（需求：支持 2 位小数）。</summary>
    public const int Decimals = 2;

    /// <summary>
    /// 夹进 <c>[0.5, 60]</c> 并四舍五入到 2 位小数。
    /// <c>NaN</c> / 无穷大一律退回默认值 —— 配置被手改坏时不能把界面带崩。
    /// </summary>
    public static float Normalize(float seconds)
    {
        if (float.IsNaN(seconds) || float.IsInfinity(seconds)) return DefaultSeconds;
        return MathF.Round(Math.Clamp(seconds, MinSeconds, MaxSeconds), Decimals, MidpointRounding.AwayFromZero);
    }

    /// <summary>秒 → 滑块位置（0~1，**对数刻度**）。</summary>
    public static float ToRatio(float seconds)
    {
        float s = Normalize(seconds);
        return MathF.Log(s / MinSeconds) / MathF.Log(MaxSeconds / MinSeconds);
    }

    /// <summary>滑块位置（0~1）→ 秒（**对数刻度**，结果已规范化）。</summary>
    public static float FromRatio(float ratio)
    {
        float r = Math.Clamp(ratio, 0f, 1f);
        return Normalize(MinSeconds * MathF.Pow(MaxSeconds / MinSeconds, r));
    }

    /// <summary>格式化为「0.90 秒」（恒定 2 位小数，不变文化以免小数点被区域设置换成逗号）。</summary>
    public static string Format(float seconds)
        => Normalize(seconds).ToString("0.00", CultureInfo.InvariantCulture) + " 秒";

    /// <summary>持久化用（不带单位，恒定 2 位小数）。</summary>
    public static string Serialize(float seconds)
        => Normalize(seconds).ToString("0.00", CultureInfo.InvariantCulture);

    /// <summary>解析持久化的字符串。**宽容**：空 / 坏值退回默认，越界夹进范围。</summary>
    public static float Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return DefaultSeconds;
        return float.TryParse(raw.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
            ? Normalize(v)
            : DefaultSeconds;
    }

    /// <summary>
    /// 由「秒数 + 是否忽略外部鼠标」算出交给宿主的最终延迟 —— 也就是
    /// <c>IDetailPage.AutoCollapseDelay</c> 的返回值。
    ///
    /// 单独抽成纯函数有两个好处：① 换算规则只有一处（面板显示、插件透传都用它）；
    /// ② **它可以被验证工程直接断言** —— <c>ConfigDetailPage</c> 本身依赖宿主的
    /// <c>IDetailPage</c>，进不了独立测试工程，把规则放在这里才测得到。
    /// </summary>
    /// <param name="seconds">用户设的秒数（内部会规范化）。</param>
    /// <param name="ignoreMouse">是否勾了「忽略外部鼠标操作」。</param>
    public static TimeSpan ToAutoCollapseDelay(float seconds, bool ignoreMouse)
        => ignoreMouse ? Timeout.InfiniteTimeSpan : TimeSpan.FromSeconds(Normalize(seconds));

    /// <summary>「忽略外部鼠标操作（不自动收起）」的持久化解析：只有明确的 <c>1</c>/<c>true</c> 才算开。</summary>
    public static bool ParseIgnoreMouse(string? raw)
        => raw is not null && (raw == "1" || raw.Equals("true", StringComparison.OrdinalIgnoreCase));

    /// <summary>「忽略外部鼠标操作」的持久化序列化。</summary>
    public static string SerializeIgnoreMouse(bool value) => value ? "1" : "0";
}
