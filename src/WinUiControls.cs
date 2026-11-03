using SkiaSharp;

namespace NpsPushBridge;

/// <summary>
/// WinUI 3（Fluent Design）控件绘制原语。
///
/// ★ 为什么单独抽一层 ★
/// 面板里画按钮 / 输入框 / 卡片的地方有十几处（主页面、图标映射页、更新区块……）。
/// 若每处各写一遍圆角、描边、悬停/按下配色，改一次规格就要改十几处，还必然漂移
/// （本项目已有「同一几何两处各写」的教训）。统一走这个类，规格只有一份。
///
/// 取值全部对齐 WinUI 3 的 ThemeResources（见 <see cref="PanelTheme.Window"/> 的注释），
/// 圆角用 WinUI 3 的 <c>ControlCornerRadius = 4</c> / <c>OverlayCornerRadius = 8</c>。
/// </summary>
internal static class WinUi
{
    // ==================== 规格常量 ====================

    /// <summary>控件圆角（WinUI 3 <c>ControlCornerRadius</c>）。</summary>
    public const float ControlRadius = 4f;

    /// <summary>卡片 / 覆盖层圆角（WinUI 3 <c>OverlayCornerRadius</c>）。</summary>
    public const float CardRadius = 8f;

    /// <summary>输入框底部强调线宽度（WinUI 3 TextBox 的默认态）。</summary>
    public const float FieldLineWidth = 1f;

    /// <summary>输入框聚焦态底部强调线宽度（WinUI 3 聚焦时加粗到 2px）。</summary>
    public const float FieldLineFocusedWidth = 2f;

    /// <summary>焦点矩形线宽（WinUI 3 的 FocusStrokeWidth）。</summary>
    public const float FocusRingWidth = 2f;

    /// <summary>滑块圆钮半径（WinUI 3 的 thumb 直径 20；面板行高 30，取 9 才够醒目）。</summary>
    public const float KnobRadius = 9f;

    /// <summary>滑块轨道高度（比 WinUI 3 的 4 略厚一点：深色底上太细会「看不见」）。</summary>
    public const float TrackHeight = 5f;

    /// <summary>交互状态 —— 对应 WinUI 3 的三种 ControlFill（Normal / Hover / Pressed）。</summary>
    public enum State { Normal, Hover, Pressed, Disabled }

    /// <summary>按钮风格。</summary>
    public enum Style
    {
        /// <summary>标准按钮：ControlFill 底 + 1px 描边（WinUI 3 默认按钮）。</summary>
        Standard,
        /// <summary>强调按钮：AccentFill 实底（WinUI 3 <c>AccentButtonStyle</c>）。</summary>
        Accent,
        /// <summary>轻量按钮：常态无底无边，仅悬停/按下浮出一层浅底（次要操作，如工具行）。</summary>
        Subtle,
        /// <summary>危险按钮：常态同 Standard，描边与悬停偏红（删除类操作）。</summary>
        Danger,
    }

    // ==================== 取色 ====================

    /// <summary>把颜色朝白色方向提亮（用于自定义强调色的悬停态）。<paramref name="amount"/> 0~1。</summary>
    public static SKColor Lighten(SKColor c, float amount)
        => new(
            (byte)Math.Min(255f, c.Red + (255 - c.Red) * amount),
            (byte)Math.Min(255f, c.Green + (255 - c.Green) * amount),
            (byte)Math.Min(255f, c.Blue + (255 - c.Blue) * amount),
            c.Alpha);

    /// <summary>把颜色朝黑色方向压暗（用于自定义强调色的按下态）。<paramref name="amount"/> 0~1。</summary>
    public static SKColor Darken(SKColor c, float amount)
        => new(
            (byte)Math.Max(0f, c.Red * (1 - amount)),
            (byte)Math.Max(0f, c.Green * (1 - amount)),
            (byte)Math.Max(0f, c.Blue * (1 - amount)),
            c.Alpha);

    /// <summary>控件填充色（WinUI 3 <c>ControlFillColor*</c> / <c>AccentFillColor*</c>）。</summary>
    public static SKColor FillOf(PanelTheme t, State s, Style style = Style.Standard)
    {
        if (s == State.Disabled) return new SKColor(255, 255, 255, 10);   // ControlFillColorDisabled

        if (style == Style.Accent)
            return s switch
            {
                State.Pressed => new SKColor(0, 103, 192),   // AccentFillColorTertiary
                State.Hover => new SKColor(26, 134, 217),    // AccentFillColorSecondary
                _ => t.AccentFill,                           // AccentFillColorDefault
            };

        if (style == Style.Danger && s != State.Normal)
            return new SKColor(255, 99, 113, (byte)(s == State.Pressed ? 60 : 40));

        return s switch
        {
            State.Pressed => t.ControlFillPressed,
            State.Hover => t.ControlFillHover,
            _ => style == Style.Subtle ? SKColors.Transparent : t.ControlFill,
        };
    }

    /// <summary>控件文字色（WinUI 3 <c>TextFillColor*</c> / <c>TextOnAccentFillColorPrimary</c>）。</summary>
    public static SKColor TextOf(PanelTheme t, State s, Style style = Style.Standard)
    {
        if (s == State.Disabled) return new SKColor(255, 255, 255, 93);      // 36%
        if (style == Style.Accent) return SKColors.White;                    // 深色 accent 上用白字
        if (style == Style.Danger) return s == State.Normal ? t.Text : new SKColor(255, 153, 164);
        if (style == Style.Subtle && s == State.Pressed) return t.SubText;
        return t.Text;
    }

    /// <summary>控件描边色（WinUI 3 <c>ControlStrokeColorDefault</c>；轻量风格无描边）。</summary>
    public static SKColor StrokeOf(PanelTheme t, State s, Style style = Style.Standard)
    {
        if (style == Style.Subtle || style == Style.Accent) return SKColors.Transparent;
        if (style == Style.Danger && s != State.Normal) return new SKColor(255, 99, 113, 90);
        return s == State.Hover ? new SKColor(255, 255, 255, 24) : t.Stroke;   // 悬停时描边略亮
    }

    /// <summary>
    /// 按 <b>0~1 的悬停进度</b>插值填充色 —— 实现 WinUI 3 悬停时的颜色过渡
    /// （Fluent 的「隐式动画」，约 150ms；进度由调用方按时间算，见 <c>ConfigPanel.HoverT</c>）。
    /// ⚠️ 插值必须在**颜色分量**上做，不能简单二选一，否则就是「啪」地跳色而不是渐变。
    /// </summary>
    public static SKColor FillLerp(PanelTheme t, float hoverT, Style style = Style.Standard)
    {
        var a = FillOf(t, State.Normal, style);
        var b = FillOf(t, State.Hover, style);
        float k = Math.Clamp(hoverT, 0f, 1f);
        return new SKColor(
            (byte)(a.Red + (b.Red - a.Red) * k),
            (byte)(a.Green + (b.Green - a.Green) * k),
            (byte)(a.Blue + (b.Blue - a.Blue) * k),
            (byte)(a.Alpha + (b.Alpha - a.Alpha) * k));
    }

    /// <summary>
    /// 按悬停进度画按钮：几何与 <see cref="Button"/> 完全一致，只是填充色随 <paramref name="hoverT"/>
    /// **连续**过渡（而不是在某个阈值上跳变）；描边同样随进度提亮。
    /// 禁用时忽略进度，直接画禁用态。
    /// </summary>
    public static void ButtonLerp(SKCanvas canvas, SKRect rect, string label, PanelTheme theme,
        SKPaint fill, SKPaint text, float hoverT, bool enabled,
        Style style = Style.Standard, SKColor? accentColor = null, float radius = ControlRadius,
        SKPaint? stroke = null)
    {
        if (!enabled)
        {
            Button(canvas, rect, label, theme, fill, text, State.Disabled, style, radius, stroke);
            return;
        }

        float k = Math.Clamp(hoverT, 0f, 1f);

        if (accentColor.HasValue)
        {
            // 自定义强调色（「开启服务」这类按状态换色的按钮）：底色随进度提亮
            fill.Color = Lighten(accentColor.Value, 0.12f * k);
            canvas.DrawRoundRect(rect, radius, radius, fill);
            text.Color = SKColors.White;
            PluginFonts.Fit(text, label);
            float w0 = text.MeasureText(label);
            canvas.DrawText(label, rect.MidX - w0 / 2f, TextLayout.VerticallyCenteredBaseline(text, rect, label), text);
            return;
        }

        fill.Color = FillLerp(theme, k, style);
        if (fill.Color.Alpha > 0) canvas.DrawRoundRect(rect, radius, radius, fill);

        var line0 = StrokeOf(theme, State.Normal, style);
        var line1 = StrokeOf(theme, State.Hover, style);
        var line = new SKColor(
            (byte)(line0.Red + (line1.Red - line0.Red) * k),
            (byte)(line0.Green + (line1.Green - line0.Green) * k),
            (byte)(line0.Blue + (line1.Blue - line0.Blue) * k),
            (byte)(line0.Alpha + (line1.Alpha - line0.Alpha) * k));
        if (line.Alpha > 0)
        {
            if (stroke is null)
            {
                using var own = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke };
                DrawInnerStroke(canvas, rect, radius, own, line);
            }
            else DrawInnerStroke(canvas, rect, radius, stroke, line);
        }

        text.Color = TextOf(theme, State.Normal, style);
        PluginFonts.Fit(text, label);
        float w = text.MeasureText(label);
        canvas.DrawText(label, rect.MidX - w / 2f, TextLayout.VerticallyCenteredBaseline(text, rect, label), text);
    }

    private static void DrawInnerStroke(SKCanvas canvas, SKRect rect, float radius, SKPaint stroke, SKColor color)
    {
        stroke.Color = color;
        stroke.StrokeWidth = 1f;
        var inner = new SKRect(rect.Left + 0.5f, rect.Top + 0.5f, rect.Right - 0.5f, rect.Bottom - 0.5f);
        canvas.DrawRoundRect(inner, radius, radius, stroke);
    }

    /// <summary>画 WinUI 3 的 <b>Slider</b>（轨道 + 已选填充 + 圆钮）。</summary>
    /// <param name="track">整条滑块的**交互矩形**（不是细轨道本身）—— 轨道画在它的垂直中点。</param>
    /// <param name="t">归一化值 0~1（调用方按 min/max 换算好）。</param>
    /// <param name="knobRadius">圆钮半径。</param>
    /// <returns>圆钮矩形（供调用方做命中判定，与 <c>DragSlider</c> 共用同一套换算）。</returns>
    public static SKRect Slider(SKCanvas canvas, SKRect track, float t, PanelTheme theme,
        SKPaint fill, SKPaint stroke, State state, float knobRadius = KnobRadius)
    {
        float k = Math.Clamp(t, 0f, 1f);
        float midY = track.MidY;
        // 悬停与**拖动中**都算「激活」—— 拖动时圆钮没有反馈会让人以为没抓住
        bool hot = state is State.Hover or State.Pressed;

        // 可用行程：两端各留一个圆钮半径，钮心才不会越出交互区
        float left = track.Left + knobRadius;
        float right = Math.Max(left, track.Right - knobRadius);
        float knobX = left + (right - left) * k;

        float barH = TrackHeight;
        var barTop = midY - barH / 2f;

        // ① 未选段：比 ControlFill 更亮一档 —— 深色底上太暗的轨道等于「看不见」
        fill.Color = state == State.Disabled ? new SKColor(255, 255, 255, 12) : new SKColor(255, 255, 255, 38);
        canvas.DrawRoundRect(new SKRect(left, barTop, right, barTop + barH), barH / 2f, barH / 2f, fill);

        // ② 已选段：强调色。WinUI 3 的 AccentFillColorDefault(#0078D4) 在深色底上偏暗，
        //    统一提亮一档（悬停再亮一档），否则「拖到哪了」不容易一眼看出。
        fill.Color = state == State.Disabled
            ? new SKColor(255, 255, 255, 30)
            : hot ? Lighten(theme.AccentFill, 0.30f) : Lighten(theme.AccentFill, 0.14f);
        if (knobX > left + 0.5f)
            canvas.DrawRoundRect(new SKRect(left, barTop, knobX, barTop + barH), barH / 2f, barH / 2f, fill);

        // ③ 圆钮：先垫一圈半透明深色（相当于投影，让白圆在深色底上「立起来」），再画白圆
        fill.Color = new SKColor(0, 0, 0, 100);
        canvas.DrawCircle(knobX, midY, knobRadius + 1.5f, fill);
        fill.Color = state == State.Disabled ? new SKColor(255, 255, 255, 140) : SKColors.White;
        canvas.DrawCircle(knobX, midY, knobRadius, fill);

        // 悬停/拖动时再套一圈强调色环，明确「它正被操作」
        if (hot)
        {
            stroke.Color = Lighten(theme.AccentFill, 0.2f).WithAlpha(200);
            stroke.StrokeWidth = 2f;
            canvas.DrawCircle(knobX, midY, knobRadius + 3f, stroke);
        }

        return new SKRect(knobX - knobRadius, midY - knobRadius, knobX + knobRadius, midY + knobRadius);
    }

    /// <summary>
    /// 由鼠标 x 反算归一化值 —— **必须与 <see cref="Slider"/> 用同一套换算**，
    /// 否则「拖到最右却差一点满」或「手指与圆钮对不上」。
    /// </summary>
    public static float SliderValueAt(SKRect track, float x, float knobRadius = KnobRadius)
    {
        float left = track.Left + knobRadius;
        float right = Math.Max(left, track.Right - knobRadius);
        if (right <= left) return 0f;
        return Math.Clamp((x - left) / (right - left), 0f, 1f);
    }

    // ==================== 绘制 ====================

    /// <summary>
    /// 画一个 WinUI 3 按钮：圆角 4、ControlFill 底、1px 描边（或 Accent 实底），文字居中。
    /// <paramref name="fill"/>/<paramref name="text"/> 由调用方提供画笔（每帧复用）；
    /// <paramref name="stroke"/> 可省略（省略时本方法临时建一支描边笔，画完即释放，符合「原生对象不跨帧缓存」的约定）。
    /// ⚠️ 字体必须在**量宽之前**定好（<see cref="PluginFonts.Fit"/>），否则居中会偏。
    /// </summary>
    public static void Button(SKCanvas canvas, SKRect rect, string label, PanelTheme theme,
        SKPaint fill, SKPaint text, State state, Style style = Style.Standard,
        float radius = ControlRadius, SKPaint? stroke = null)
    {
        if (stroke is not null)
        {
            ButtonCore(canvas, rect, label, theme, fill, stroke, text, state, style, radius);
            return;
        }
        using var own = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke };
        ButtonCore(canvas, rect, label, theme, fill, own, text, state, style, radius);
    }

    private static void ButtonCore(SKCanvas canvas, SKRect rect, string label, PanelTheme theme,
        SKPaint fill, SKPaint stroke, SKPaint text, State state, Style style, float radius)
    {
        var bg = FillOf(theme, state, style);
        if (bg.Alpha > 0)
        {
            fill.Color = bg;
            canvas.DrawRoundRect(rect, radius, radius, fill);
        }

        var line = StrokeOf(theme, state, style);
        if (line.Alpha > 0)
        {
            stroke.Color = line;
            stroke.StrokeWidth = 1f;
            // 内缩半线宽，描边才不会被圆角外沿裁掉
            var inner = new SKRect(rect.Left + 0.5f, rect.Top + 0.5f, rect.Right - 0.5f, rect.Bottom - 0.5f);
            canvas.DrawRoundRect(inner, radius, radius, stroke);
        }

        text.Color = TextOf(theme, state, style);
        PluginFonts.Fit(text, label);
        float w = text.MeasureText(label);
        canvas.DrawText(label, rect.MidX - w / 2f, TextLayout.VerticallyCenteredBaseline(text, rect, label), text);
    }

    /// <summary>
    /// 画 WinUI 3 的 <b>TextBox 输入框</b>：ControlFill 底 + 圆角（仅上两角）+ <b>底部强调线</b>。
    /// 底部线是该风格的辨识特征 —— 常态 <c>ControlStrokeColorDefault</c>，
    /// 悬停略亮，聚焦变强调色并加粗到 2px，错误时变红（<c>SystemFillColorCritical</c>）。
    /// </summary>
    public static void Field(SKCanvas canvas, SKRect rect, PanelTheme theme, SKPaint fill, SKPaint stroke,
        State state, bool focused, SKColor? lineColor = null)
    {
        // 底：圆角只作用于上半部分（WinUI 3 的 TextBox 底边是直角贴线的）
        fill.Color = state == State.Disabled ? new SKColor(255, 255, 255, 10) : theme.ControlFill;
        canvas.DrawRoundRect(new SKRect(rect.Left, rect.Top, rect.Right, rect.Bottom), ControlRadius, ControlRadius, fill);
        // 把下半部的圆角补成直角：再画一个只覆盖下半部分的实心矩形
        canvas.DrawRect(new SKRect(rect.Left, rect.MidY, rect.Right, rect.Bottom - ControlRadius), fill);

        // 底部强调线
        float lw = focused ? FieldLineFocusedWidth : FieldLineWidth;
        stroke.Color = lineColor
            ?? (focused ? theme.Accent
                : state == State.Hover ? new SKColor(255, 255, 255, 60) : theme.Stroke);
        stroke.StrokeWidth = lw;
        float y = rect.Bottom - lw / 2f;
        canvas.DrawLine(rect.Left, y, rect.Right, y, stroke);
    }

    /// <summary>画 WinUI 3 的卡片 / 分组底（圆角 8 + CardBackgroundFillColorDefault，可选 1px 描边）。</summary>
    public static void Card(SKCanvas canvas, SKRect rect, PanelTheme theme, SKPaint fill, SKPaint? stroke = null)
    {
        fill.Color = theme.Card;
        canvas.DrawRoundRect(rect, CardRadius, CardRadius, fill);

        if (stroke != null)
        {
            stroke.Color = theme.Stroke;
            stroke.StrokeWidth = 1f;
            var inner = new SKRect(rect.Left + 0.5f, rect.Top + 0.5f, rect.Right - 0.5f, rect.Bottom - 0.5f);
            canvas.DrawRoundRect(inner, CardRadius, CardRadius, stroke);
        }
    }

    /// <summary>画 WinUI 3 的焦点矩形（2px 强调色、贴合控件外沿）。键盘 Tab 到某个控件时用。</summary>
    public static void FocusRing(SKCanvas canvas, SKRect rect, PanelTheme theme, SKPaint stroke,
        float radius = ControlRadius)
    {
        stroke.Color = theme.Accent;
        stroke.StrokeWidth = FocusRingWidth;
        var r = new SKRect(rect.Left - 1f, rect.Top - 1f, rect.Right + 1f, rect.Bottom + 1f);
        canvas.DrawRoundRect(r, radius + 1f, radius + 1f, stroke);
    }

    /// <summary>画 WinUI 3 的分隔线（<c>DividerStrokeColorDefault</c>）。</summary>
    public static void Divider(SKCanvas canvas, float x1, float x2, float y, PanelTheme theme, SKPaint stroke)
    {
        stroke.Color = theme.Divider;
        stroke.StrokeWidth = 1f;
        canvas.DrawLine(x1, y, x2, y, stroke);
    }

    /// <summary>
    /// 画 WinUI 3 的 <b>ToggleSwitch</b>（小胶囊 + 圆钮）。
    /// <paramref name="on"/> 决定钮的位置；<paramref name="state"/> 决定轨道配色。
    /// </summary>
    public static void Toggle(SKCanvas canvas, SKRect rect, bool on, PanelTheme theme, SKPaint fill, State state)
    {
        float r = rect.Height / 2f;
        var track = on
            ? (state == State.Disabled ? new SKColor(255, 255, 255, 20)
                : state == State.Pressed ? new SKColor(0, 103, 192)
                : state == State.Hover ? new SKColor(26, 134, 217) : theme.AccentFill)
            : (state == State.Disabled ? new SKColor(255, 255, 255, 10)
                : state == State.Hover ? new SKColor(255, 255, 255, 24) : new SKColor(255, 255, 255, 16));

        fill.Color = track;
        canvas.DrawRoundRect(rect, r, r, fill);

        // 圆钮：WinUI 3 的 knob 比轨道略小、贴着开启那一侧
        float knobR = r - 3f;
        float knobX = on ? rect.Right - r : rect.Left + r;
        fill.Color = on ? SKColors.Black : new SKColor(255, 255, 255, 220);
        if (state == State.Disabled) fill.Color = new SKColor(255, 255, 255, 100);
        canvas.DrawCircle(knobX, rect.MidY, Math.Max(1.5f, knobR), fill);
    }
}
