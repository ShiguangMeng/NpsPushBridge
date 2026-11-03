namespace NpsPushBridge;

/// <summary>
/// 配置窗口的**尺寸 / 位置 / 置顶**状态，以及它的持久化与「摆回屏幕内」规则。
///
/// ★ 为什么把这些抽成纯逻辑 ★
/// 这部分唯一的难点不是读写，而是**边界**：
///   · 上次关窗的位置可能已经不在屏幕上了（换了显示器、改了分辨率、副屏被拔掉）——
///     若不校正，用户下次点开配置窗口会「什么都没出现」，而这类问题极难自查；
///   · 保存的尺寸可能小于布局下限或大于屏幕 —— 都要夹回合法区间；
///   · 存取的是字符串（宿主设置项），旧版本/被手改过的值必须能安全降级。
/// 这些都放在这里，就能被逐条断言，而不必真去开窗口试。
/// </summary>
internal sealed class WindowState
{
    /// <summary>窗口宽度（逻辑像素）。</summary>
    public int Width { get; set; }

    /// <summary>窗口高度（逻辑像素）。</summary>
    public int Height { get; set; }

    /// <summary>左上角 x（逻辑像素；仅当 <see cref="HasBounds"/> 为真时有意义）。</summary>
    public int X { get; set; }

    /// <summary>左上角 y。</summary>
    public int Y { get; set; }

    /// <summary>是否记录过位置。为 false 表示「第一次打开」——此时应当让窗口居中，而不是摆在 (0,0)。</summary>
    public bool HasBounds { get; set; }

    /// <summary>是否置顶。**默认 false**：需求明确要求「取消窗口置顶，改为点按钮才置顶」。</summary>
    public bool Topmost { get; set; }

    /// <summary>
    /// 「至少要有这么多像素的标题条留在屏幕上」——
    /// 用户必须还能**看见并拖动**它，否则窗口等于丢了。
    /// </summary>
    public const int MinVisibleTitle = 40;

    /// <summary>
    /// 校正尺寸与位置。规则（顺序即语义）：
    /// <list type="number">
    /// <item>尺寸夹进 <c>[minW, maxW]</c> / <c>[minH, maxH]</c>（<c>max*</c> 传 0 表示不限）；</item>
    /// <item>没记录过位置 → 居中于给定屏幕；</item>
    /// <item>记录过位置 → 若横向或纵向的**可见部分不足**，就把该轴**拉回屏幕内**（不是整体居中：
    ///       只需要修被挤出去的那个轴，另一个轴保留用户的选择）。</item>
    /// </list>
    /// </summary>
    public void Normalize(int screenX, int screenY, int screenWidth, int screenHeight,
        int minW, int minH, int maxW = 0, int maxH = 0)
    {
        Width = Clamp(Width, minW, maxW > 0 ? maxW : int.MaxValue);
        Height = Clamp(Height, minH, maxH > 0 ? maxH : int.MaxValue);

        if (screenWidth <= 0 || screenHeight <= 0) return;   // 取不到屏幕信息：不动位置

        if (!HasBounds)
        {
            Center(screenX, screenY, screenWidth, screenHeight);
            return;
        }

        int screenRight = screenX + screenWidth;
        int screenBottom = screenY + screenHeight;

        // 横向：窗口至少要露出 MinVisibleTitle 像素的宽度，
        //       且不能超出屏幕左边到「窗口右边界贴住屏幕右缘」的范围
        int minX = screenX - Width + MinVisibleTitle;
        int maxX = screenRight - MinVisibleTitle;
        X = Clamp(X, minX, maxX);

        // 纵向：标题条（窗口顶部那条）必须留在屏幕内
        int minY = screenY;
        int maxY = screenBottom - MinVisibleTitle;
        Y = Clamp(Y, minY, maxY);
    }

    /// <summary>把窗口居中于给定屏幕区域。</summary>
    public void Center(int screenX, int screenY, int screenWidth, int screenHeight)
    {
        X = screenX + Math.Max(0, (screenWidth - Width) / 2);
        Y = screenY + Math.Max(0, (screenHeight - Height) / 2);
        HasBounds = true;
    }

    /// <summary>
    /// 序列化成一行字符串（存进宿主设置）。
    ///
    /// 用 <c>k=v;k=v</c> 这种「自描述」格式而不是固定列数，是为了**向前兼容**：
    /// 将来加字段时旧版本读它只会忽略未知键，不会整体解析失败。
    /// </summary>
    public string Serialize()
        => $"w={Width};h={Height};x={X};y={Y};b={(HasBounds ? 1 : 0)};t={(Topmost ? 1 : 0)}";

    /// <summary>
    /// 从宿主设置里读回。**任何异常情况都降级为「没记过」**（宁可居中，也不要乱摆）：
    /// 空串 / 有未知键 / 值不是数字 / 被用户手改成半截 —— 一律返回默认状态。
    /// </summary>
    public static WindowState Parse(string? raw, int defaultWidth, int defaultHeight)
    {
        var state = new WindowState { Width = defaultWidth, Height = defaultHeight };
        if (string.IsNullOrWhiteSpace(raw)) return state;

        foreach (var part in raw.Split(';'))
        {
            int eq = part.IndexOf('=');
            if (eq <= 0) continue;

            var key = part[..eq].Trim();
            var value = part[(eq + 1)..].Trim();
            if (value.Length == 0) continue;

            switch (key)
            {
                case "w": if (int.TryParse(value, out int w)) state.Width = w; break;
                case "h": if (int.TryParse(value, out int h)) state.Height = h; break;
                case "x": if (int.TryParse(value, out int x)) state.X = x; break;
                case "y": if (int.TryParse(value, out int y)) state.Y = y; break;
                case "b": state.HasBounds = value == "1"; break;
                case "t": state.Topmost = value == "1"; break;
                // 未知键：忽略（向前兼容 —— 更新版本写的设置被旧版本读到时不炸）
            }
        }

        // 极端的脏数据（例如被手改成 w=999999999）不让它进入窗口创建流程
        if (state.Width <= 0 || state.Height <= 0)
        {
            state.Width = defaultWidth;
            state.Height = defaultHeight;
            state.HasBounds = false;
        }
        return state;
    }

    private static int Clamp(int value, int min, int max)
        => value < min ? min : value > max ? max : value;
}
