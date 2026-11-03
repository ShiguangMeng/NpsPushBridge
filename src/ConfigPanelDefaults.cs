namespace NpsPushBridge;

/// <summary>
/// 配置窗口与面板共用的尺寸常量（**唯一真源**）。
///
/// ★ 为什么单独抽出来 ★
/// 「窗口宽 660」这个数原先硬编码在 <c>NpsPushBridgePlugin.OpenConfig</c> 里，
/// 后来分屏判定与窗口重建也都要用它。若各处各写一份，改一处忘了另一处就会出现
/// 「窗口重建成了 A 宽、判定用的是 B 宽」这种极难发现的不一致。
/// （分屏已在 2026-09-21 改为「导出到浏览器」，尺寸常量回归单一用途，
/// 但独立文件 + 可被验证工程断言的价值不变。）
///
/// ★ 2026-09-23：这不再是「唯一的窗口尺寸」★
/// 窗口改为**用户可缩放**（见 <c>PanelWindow</c>），实际尺寸记在
/// <see cref="WindowState"/> 里、随设置持久化。这里的值降级为
/// **首次打开时的默认尺寸**；用户拉过之后就以用户的选择为准。
/// ⚠️ **最小尺寸不在这里** —— 它是布局的属性，在
/// <c>ConfigLayout.MinContentWidth</c> / <c>MinRecommendedHeight</c>（面板被裁到更小就不可用）。
///
/// 放在独立文件里还有一个好处：这些常量是**纯数值**，可以被 <c>BridgeHarness</c> 直接断言。
/// （2026-09-23 更正：下面这句曾写着「ConfigPanel 依赖 WinForms、进不了验证工程」——
/// 那是错的。面板只依赖 SkiaSharp（原生绘制在验证工程里可用），
/// 对宿主 API 的唯一引用已在同日删除，因此 <c>ConfigPanel</c> 现在**确实**被编译进
/// <c>BridgeHarness</c>，并有端到端绘制/输入/保存的冒烟测试。）
/// </summary>
internal static class ConfigPanelDefaults
{
    /// <summary>配置窗口**首次打开**时的宽度（用户拉过之后以 <see cref="WindowState"/> 为准）。</summary>
    public const int WindowWidth = 660;

    /// <summary>配置窗口**首次打开**时的高度（同上）。</summary>
    public const int WindowHeight = 620;

    /// <summary>窗口标题。</summary>
    public const string WindowTitle = "推送网桥配置";
}
