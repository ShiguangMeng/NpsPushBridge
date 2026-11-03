using NotchPeninsula.Plugins;
using SkiaSharp;

namespace NpsPushBridge;

/// <summary>
/// 主设置窗口里的插件配置页：同时实现声明式 <see cref="ISettingsPage"/> 与自定义 <see cref="ICustomSettingsPage"/>。
///
/// · 声明式部分只放一个「随主程序启动自动开启」开关，交给宿主绘制与持久化；
/// · 自定义部分即需求中的配置界面（白名单 / 端口 / 保存 / 开启服务 / 日志），由 <see cref="ConfigPanel"/> 绘制。
///
/// 说明：上游宿主（1.6.0 / 1.7.0）虽已提供本接口，但尚未接线渲染插件设置页；
/// 本仓库附带了一份打过补丁的宿主（在 ConsoleWindow 左侧列表末尾生成本页条目、
/// 内容区交给本页自绘并转发鼠标），编译该宿主后本页即在「设置窗口 → 左侧列表」可见。
/// 未使用补丁宿主时，仍可用灵动岛组件的右键菜单打开同一份配置面板。
/// </summary>
internal sealed class PushBridgeSettingsPage : ISettingsPage, ICustomSettingsPage
{
    private readonly ConfigPanel _panel;

    public PushBridgeSettingsPage(ConfigPanel panel) => _panel = panel;

    public string Title => "局域网推送网桥";

    /// <summary>
    /// 本插件不提供声明式控件：全部配置都由 <see cref="ICustomSettingsPage"/> 自绘，
    /// 这样「日志可翻阅 + 一键复制」这类交互才做得出来（声明式控件无法表达）。
    /// </summary>
    public IReadOnlyList<SettingControl> Controls { get; } = Array.Empty<SettingControl>();

    /// <summary>自定义内容所需高度：固定行区域 + 日志最小高度 + 余量。</summary>
    float ICustomSettingsPage.MeasureHeight() => ConfigLayout.MinRecommendedHeight + 160f;

    /// <summary>
    /// 宿主设置窗口固定是深色底（ConsoleWindow 的卡片为深灰），
    /// 因此这里固定使用深色配色，避免跟随灵动岛主题（浅色）时出现深底深字。
    /// </summary>
    void ICustomSettingsPage.Draw(SKCanvas canvas, SKRect rect, RenderTheme theme)
        => _panel.Draw(canvas, rect, PanelTheme.Window, false);

    void ICustomSettingsPage.OnMouseDown(float x, float y) => _panel.OnMouseDown(x, y);

    void ICustomSettingsPage.OnMouseMove(float x, float y) => _panel.OnMouseMove(x, y);

    void ICustomSettingsPage.OnMouseUp(float x, float y) => _panel.OnMouseUp(x, y);
}
