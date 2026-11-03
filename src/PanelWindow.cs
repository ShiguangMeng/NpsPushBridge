using SkiaSharp;

namespace NpsPushBridge;

/// <summary>
/// **插件自建**的配置窗口：SkiaSharp 分层窗口（圆角 + 边框 + 关闭按钮），支持
/// **拖动移动**、**拖边缩放**、**按需置顶**，并直接接收完整键盘 / 滚轮 / 双击消息。
///
/// ── 为什么不用宿主的 <c>IPluginWindow</c>（这是本类存在的全部理由）────────────
/// 1. 宿主窗口**每次重绘都会 <c>UpdateLayeredWindow(..., ref pptDst, ref psize, ...)</c>** ——
///    这两个参数会把窗口**移动并缩放**回宿主创建时记下的值。因此对它做
///    <c>SetWindowPos</c> 会在下一帧被抹掉，**移动/缩放根本留不住**；
/// 2. 宿主窗口的尺寸是 <c>readonly</c> 且绘制缓冲按创建尺寸分配，**没有任何 resize 接口**；
/// 3. 宿主窗口类**没有 <c>CS_DBLCLKS</c>**，收不到 <c>WM_LBUTTONDBLCLK</c>；
/// 4. 宿主只把 <c>WM_CHAR</c> 转给插件，**方向键等收不到**（此前只能靠只读轮询补）。
/// 自建窗口把这四条一次性解决，而且**不修改宿主任何源码** —— 只是不再使用它的窗口工厂。
///
/// ── 与宿主窗口在观感上保持一致 ─────────────────────────────────────────
/// 背景色、圆角、边框、右上角关闭按钮都照宿主 <c>PluginWindow</c> 的画法绘制（同样的常量），
/// 用户不会觉得「换了个窗口」。
///
/// ── 生命周期约定 ───────────────────────────────────────────────────────
/// 消息由**本进程的 UI 线程消息循环**派发（插件跑在宿主进程内，与宿主同一线程），
/// 因此不需要也不应该自建 <c>GetMessage</c> 循环。WndProc 必须是**静态方法**
/// （委托被 GC 回收后仍收到消息就会崩），用字典按 hwnd 找回实例 —— 与宿主的做法一致。
/// </summary>
internal sealed class PanelWindow : IDisposable
{
    // ★ 类名带一个「本次插件加载」的唯一后缀 ★ 不是固定的 ——
    //   宿主会热重载插件（重载 / 静默升级），旧 DLL 卸载后它注册的窗口类仍留在进程里、
    //   但其 WndProc 已指向被卸载的内存。若新 DLL 沿用同一个固定类名，`RegisterClassEx`
    //   会报 ERROR_CLASS_ALREADY_EXISTS 而代码把它当成功，`CreateWindowEx` 就会复用那个
    //   **悬空 WndProc** 的旧类 —— 第一条消息一到就是访问违例，整个宿主闪退。
    //   用 GUID 后缀让每次加载都注册一个全新类（`static` 字段随 DLL 重载重新初始化）。
    private static readonly string ClassName = "NpsPushBridgePanelWindow." + Guid.NewGuid().ToString("N");

    // ── 外观常量（与宿主 PluginWindow 保持一致）──
    private const float CornerRadius = 14f;
    private const float BorderInset = 0.75f;
    private const float ChromeStroke = 1.5f;

    // 关闭按钮的位置**取自 ConfigLayout**（而不是在这里再写一份数值）：
    // 面板的「状态胶囊」等右对齐元素必须为它让路，两处若各写一份就迟早会漂移。
    private const float CloseButtonX = ConfigLayout.CloseButtonRightInset;   // 距右边缘
    private const float CloseButtonY = 16f;                                   // 距上边缘
    private const float CloseButtonR = ConfigLayout.CloseButtonRadius;

    /// <summary>标题条高度（逻辑像素）：这一条用于**拖动窗口**，也是「至少露出这么多才算没丢」的判据。</summary>
    private const float TitleStripHeight = 30f;

    /// <summary>拖边缩放的命中宽度（逻辑像素）。</summary>
    private const float ResizeBorder = 6f;

    private static readonly Dictionary<IntPtr, PanelWindow> Windows = new();
    private static readonly NativeWindowApi.WndProcDelegate WndProcRef = WndProcStatic;
    private static bool _classRegistered;

    private readonly string _title;
    private readonly Action<float, float>? _mouseDown, _mouseMove, _mouseUp;
    private readonly Action<char>? _char;
    private readonly Action<int, bool, bool>? _key;          // (virtualKey, shift, ctrl)
    private readonly Action<float, float, float>? _wheel;
    private readonly Action<SKCanvas, int, int>? _draw;

    private IntPtr _hwnd;
    private float _dpiScale = 1f;
    private int _scaledWidth, _scaledHeight;
    private bool _disposed;

    // 绘制缓冲（按当前窗口尺寸分配，缩放时重建）
    private IntPtr _memDc, _hBitmap, _oldBitmap, _pBits;
    private SKSurface? _surface;

    /// <summary>窗口的最小逻辑尺寸（由外部按面板布局下限给出）。</summary>
    private readonly int _minWidth, _minHeight;

    /// <summary>当前是否置顶。</summary>
    public bool Topmost { get; private set; }

    /// <summary>窗口客户区尺寸变化后回调（面板可据此重算布局；传逻辑尺寸）。</summary>
    public Action<int, int>? Resized { get; set; }

    /// <summary>窗口获得 / 失去键盘焦点（面板据此显示/隐藏光标）。</summary>
    public Action<bool>? FocusChanged { get; set; }

    /// <summary>请求关闭（用户点了关闭按钮或按 Esc）。</summary>
    public Action? CloseRequested { get; set; }

    /// <summary>
    /// 「这一点必须由面板接收鼠标」的判定（返回 true 就**不**当作标题条拖动区）。
    ///
    /// ★ 为什么需要它 ★ 标题条整条都能拖动窗口（<c>HTCAPTION</c>），
    /// 而面板的「置顶」按钮正好落在标题条的纵向范围内 —— 不排除的话点它就变成拖窗口，
    /// 按钮等于半死。几何知识归面板（它才有 <see cref="ConfigLayout"/>），
    /// 所以这里只提供钩子，由插件接上 <c>ConfigPanel.WantsMouseAt</c>。
    /// </summary>
    public Func<float, float, bool>? ClientAreaOverride { get; set; }

    /// <summary>当前客户区逻辑尺寸。</summary>
    public (int Width, int Height) LogicalSize
        => ((int)(_scaledWidth / _dpiScale), (int)(_scaledHeight / _dpiScale));

    public PanelWindow(string title, WindowState state, int minWidth, int minHeight,
        Action<SKCanvas, int, int>? draw,
        Action<float, float>? mouseDown, Action<float, float>? mouseMove, Action<float, float>? mouseUp,
        Action<char>? onChar, Action<int, bool, bool>? onKey, Action<float, float, float>? onWheel)
    {
        _title = title;
        _draw = draw;
        _mouseDown = mouseDown;
        _mouseMove = mouseMove;
        _mouseUp = mouseUp;
        _char = onChar;
        _key = onKey;
        _wheel = onWheel;
        _minWidth = minWidth;
        _minHeight = minHeight;
        State = state;
        Topmost = state.Topmost;
    }

    /// <summary>与本窗口绑定的尺寸/位置/置顶状态（关闭时由调用方读回并持久化）。</summary>
    public WindowState State { get; }

    public bool IsOpen => _hwnd != IntPtr.Zero && !_disposed;

    // ====================================================================
    // 创建 / 销毁
    // ====================================================================

    public bool Show()
    {
        try
        {
            if (!RegisterClass()) return false;

            _dpiScale = Math.Max(1f, NativeWindowApi.GetDpiForSystem() / 96f);

            var state = State;
            state.Normalize(ScreenLeft, ScreenTop, ScreenWidth, ScreenHeight,
                _minWidth, _minHeight,
                ScreenWidth, ScreenHeight);   // 上限 = 屏幕尺寸：防止手改配置塞进超大宽高 → 分配巨大缓冲/越界
            _scaledWidth = Math.Max(1, (int)Math.Round(state.Width * _dpiScale));
            _scaledHeight = Math.Max(1, (int)Math.Round(state.Height * _dpiScale));

            // ★ 默认**不加** WS_EX_TOPMOST ★ 需求要求「取消置顶，改为点按钮才置顶」。
            //   TOPMOST 只在一开始就想要置顶时才加，之后由 SetTopmost 动态切换。
            uint exStyle = NativeWindowApi.WS_EX_TOOLWINDOW | NativeWindowApi.WS_EX_LAYERED;
            if (Topmost) exStyle |= NativeWindowApi.WS_EX_TOPMOST;

            _hwnd = NativeWindowApi.CreateWindowEx(
                exStyle, ClassName, _title,
                NativeWindowApi.WS_POPUP | NativeWindowApi.WS_VISIBLE,
                (int)Math.Round(state.X * _dpiScale), (int)Math.Round(state.Y * _dpiScale),
                _scaledWidth, _scaledHeight,
                IntPtr.Zero, IntPtr.Zero, CurrentModule(), IntPtr.Zero);

            if (_hwnd == IntPtr.Zero) return false;

            // 用窗口**实际所在显示器**的 DPI 修正（多屏不同缩放时 `GetDpiForSystem` 只反映主屏）。
            // 窗口初始位置由 Normalize 居中于主屏，此处通常与 GetDpiForSystem 一致；
            // 但一旦将来改成「跟随岛/鼠标所在屏」、或缩放中途变化，这里保证窗口尺寸与
            // 缓冲永远按**它真正待的那块屏**计算，否则坐标错位（按钮偏）甚至贴图越界。
            int actualDpi = NativeWindowApi.GetDpiForWindow(_hwnd);
            if (actualDpi > 0)
            {
                float actualScale = Math.Max(1f, actualDpi / 96f);
                if (Math.Abs(actualScale - _dpiScale) > 0.001f)
                {
                    _dpiScale = actualScale;
                    _scaledWidth = Math.Max(1, (int)Math.Round(state.Width * _dpiScale));
                    _scaledHeight = Math.Max(1, (int)Math.Round(state.Height * _dpiScale));
                    NativeWindowApi.SetWindowPos(_hwnd, IntPtr.Zero,
                        (int)Math.Round(state.X * _dpiScale), (int)Math.Round(state.Y * _dpiScale),
                        _scaledWidth, _scaledHeight,
                        NativeWindowApi.SWP_NOZORDER | NativeWindowApi.SWP_NOACTIVATE);
                }
            }

            lock (Windows) Windows[_hwnd] = this;

            NativeWindowApi.SetForegroundWindow(_hwnd);
            if (Topmost) SetTopmost(true);   // 应用一次，确保 exStyle 与 z 序一致

            CreateBuffer();
            Redraw();
            return true;
        }
        catch
        {
            return false;
        }
    }

    public void Close()
    {
        if (_hwnd != IntPtr.Zero) NativeWindowApi.PostMessage(_hwnd, NativeWindowApi.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
    }

    public void RequestRedraw()
    {
        if (IsOpen) NativeWindowApi.PostMessage(_hwnd, NativeWindowApi.WM_APP_REDRAW, IntPtr.Zero, IntPtr.Zero);
    }

    /// <summary>
    /// 切换置顶。用 <c>SetWindowPos(HWND_TOPMOST/HWND_NOTOPMOST)</c> ——
    /// 这是**运行期切换**置顶的标准做法（改 exStyle 后还需 SetWindowPos 让 z 序生效）。
    /// </summary>
    public void SetTopmost(bool value)
    {
        Topmost = value;
        State.Topmost = value;
        if (!IsOpen) return;

        try
        {
            NativeWindowApi.SetWindowPos(_hwnd,
                value ? NativeWindowApi.HWND_TOPMOST : NativeWindowApi.HWND_NOTOPMOST,
                0, 0, 0, 0,
                NativeWindowApi.SWP_NOMOVE | NativeWindowApi.SWP_NOSIZE | NativeWindowApi.SWP_NOACTIVATE);
        }
        catch
        {
            // 置顶只是观感，失败不影响功能
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try { DestroyBuffer(); } catch { /* 已释放 */ }

        var hwnd = _hwnd;
        _hwnd = IntPtr.Zero;
        if (hwnd != IntPtr.Zero)
        {
            try { NativeWindowApi.DestroyWindow(hwnd); } catch { /* 已销毁 */ }
            lock (Windows) Windows.Remove(hwnd);
        }
    }

    // ====================================================================
    // 窗口过程
    // ====================================================================

    private static IntPtr WndProcStatic(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        PanelWindow? self;
        lock (Windows) Windows.TryGetValue(hwnd, out self);

        if (self == null || self._disposed)
            return NativeWindowApi.DefWindowProc(hwnd, msg, wParam, lParam);

        try
        {
            return self.Handle(hwnd, msg, wParam, lParam);
        }
        catch
        {
            // 任何异常都不该让消息处理链断掉（断掉的表现是窗口失去响应）
            return NativeWindowApi.DefWindowProc(hwnd, msg, wParam, lParam);
        }
    }

    private IntPtr Handle(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case NativeWindowApi.WM_APP_REDRAW:
                Redraw();
                return IntPtr.Zero;

            // 分层窗口自己绘制全部内容：挡住系统的擦除与重绘（否则会闪白底）
            case NativeWindowApi.WM_PAINT:
                Redraw();
                return IntPtr.Zero;
            case NativeWindowApi.WM_ERASEBKGND:
                return new IntPtr(1);

            case NativeWindowApi.WM_NCHITTEST:
                return new IntPtr(HitTest(lParam));

            case NativeWindowApi.WM_GETMINMAXINFO:
                ApplyMinMaxInfo(lParam);
                return IntPtr.Zero;

            case NativeWindowApi.WM_DPICHANGED:
                // 窗口被拖到另一块不同缩放的屏 → 用新的 DPI 重算缩放与物理尺寸。
                // 不处理的话，坐标仍按旧缩放换算，按钮会「偏位」、贴图尺寸也对不上。
                OnDpiChanged(wParam);
                return IntPtr.Zero;

            case NativeWindowApi.WM_SIZE:
                OnSize();
                return IntPtr.Zero;

            case NativeWindowApi.WM_MOVE:
                RememberBounds();
                return IntPtr.Zero;

            case NativeWindowApi.WM_MOUSEMOVE:
                _mouseMove?.Invoke(Lo(lParam) / _dpiScale, Hi(lParam) / _dpiScale);
                return IntPtr.Zero;

            case NativeWindowApi.WM_LBUTTONDOWN:
            {
                // 关闭按钮自己处理（和宿主窗口一样，画在右上角的小圆）
                float lx = Lo(lParam) / _dpiScale, ly = Hi(lParam) / _dpiScale;
                if (IsCloseHit(lx, ly)) { CloseRequested?.Invoke(); return IntPtr.Zero; }
                _mouseDown?.Invoke(lx, ly);
                return IntPtr.Zero;
            }

            case NativeWindowApi.WM_LBUTTONUP:
                _mouseUp?.Invoke(Lo(lParam) / _dpiScale, Hi(lParam) / _dpiScale);
                return IntPtr.Zero;

            case NativeWindowApi.WM_LBUTTONDBLCLK:
                // ★ 有了 CS_DBLCLKS 才收得到这条 ★ 双击＝「按下 + 抬起」，
                //   若只转一次 Down，面板的双击判定会缺一次点击。
                _mouseDown?.Invoke(Lo(lParam) / _dpiScale, Hi(lParam) / _dpiScale);
                _mouseUp?.Invoke(Lo(lParam) / _dpiScale, Hi(lParam) / _dpiScale);
                return IntPtr.Zero;

            case NativeWindowApi.WM_MOUSEWHEEL:
            {
                // ⚠️ 滚轮的坐标是**屏幕坐标**（其它鼠标消息给的是客户区坐标）——
                //    必须先 ScreenToClient 再除 DPI。少了这一步，坐标会落在屏幕左上角附近，
                //    面板就永远判定「光标不在任何列表上」，滚轮等于失效（或滚错列表）。
                var pt = new NativeWindowApi.POINT(Lo(lParam), Hi(lParam));
                ScreenToClientLocal(ref pt);
                float sx = pt.x / _dpiScale, sy = pt.y / _dpiScale;
                float delta = Hi(wParam) / 120f;
                _wheel?.Invoke(sx, sy, delta);
                return IntPtr.Zero;
            }

            case NativeWindowApi.WM_CHAR:
                _char?.Invoke((char)(wParam.ToInt64() & 0xFFFF));
                return IntPtr.Zero;

            case NativeWindowApi.WM_KEYDOWN:
            {
                int vk = (int)wParam.ToInt64();
                if (vk == NativeWindowApi.VK_ESCAPE)
                {
                    // 焦点在输入框里时 Esc 不该关窗口？—— 这里保持与宿主一致：Esc 关窗。
                    // （输入框里想「取消编辑」可以点别处，或 Ctrl+Z 之类留给将来）
                    CloseRequested?.Invoke();
                    return IntPtr.Zero;
                }

                bool shift = IsDown(NativeWindowApi.VK_SHIFT);
                bool ctrl = IsDown(NativeWindowApi.VK_CONTROL);
                _key?.Invoke(vk, shift, ctrl);
                return IntPtr.Zero;
            }

            case NativeWindowApi.WM_SETFOCUS:
                FocusChanged?.Invoke(true);
                return IntPtr.Zero;
            case NativeWindowApi.WM_KILLFOCUS:
                FocusChanged?.Invoke(false);
                return IntPtr.Zero;

            case NativeWindowApi.WM_CLOSE:
                DestroyBuffer();
                NativeWindowApi.DestroyWindow(hwnd);
                return IntPtr.Zero;

            case NativeWindowApi.WM_DESTROY:
                lock (Windows) Windows.Remove(hwnd);
                _hwnd = IntPtr.Zero;
                return IntPtr.Zero;
        }

        return NativeWindowApi.DefWindowProc(hwnd, msg, wParam, lParam);
    }

    /// <summary>
    /// 命中测试：这一处是「能拖动 / 能缩放」的全部秘密。
    ///
    /// 返回 <c>HTCAPTION</c> 时系统会替我们进入**拖动**模态循环；
    /// 返回 <c>HT*</c>（边角）时进入**缩放**模态循环。
    /// 好处是这些交互由系统实现，手感和任何原生窗口一致（吸附、双击最大化等都没被破坏），
    /// 我们不必自己跟踪鼠标。
    /// </summary>
    private int HitTest(IntPtr lParam)
    {
        // WM_NCHITTEST 的坐标是**屏幕坐标**，要先换算到客户区
        var pt = new NativeWindowApi.POINT(Lo(lParam), Hi(lParam));
        ScreenToClientLocal(ref pt);

        float x = pt.x / _dpiScale, y = pt.y / _dpiScale;
        float w = _scaledWidth / _dpiScale, h = _scaledHeight / _dpiScale;

        // ⚠️ 关闭按钮必须**先**判：它在标题条内，若先去判 HTCAPTION 就永远点不到它
        if (IsCloseHit(x, y)) return NativeWindowApi.HTCLIENT;

        try
        {
            if (ClientAreaOverride != null && ClientAreaOverride(x, y)) return NativeWindowApi.HTCLIENT;
        }
        catch
        {
            // 判定失败就退回默认行为（宁可让按钮难点，也不要让窗口拖不动）
        }

        // 四边与四角
        bool left = x <= ResizeBorder, right = x >= w - ResizeBorder;
        bool top = y <= ResizeBorder, bottom = y >= h - ResizeBorder;

        if (top && left) return NativeWindowApi.HTTOPLEFT;
        if (top && right) return NativeWindowApi.HTTOPRIGHT;
        if (bottom && left) return NativeWindowApi.HTBOTTOMLEFT;
        if (bottom && right) return NativeWindowApi.HTBOTTOMRIGHT;
        if (left) return NativeWindowApi.HTLEFT;
        if (right) return NativeWindowApi.HTRIGHT;
        if (top) return NativeWindowApi.HTTOP;
        if (bottom) return NativeWindowApi.HTBOTTOM;

        // 标题条：整条都可以拖动
        if (y <= TitleStripHeight) return NativeWindowApi.HTCAPTION;

        return NativeWindowApi.HTCLIENT;
    }

    private void ApplyMinMaxInfo(IntPtr lParam)
    {
        var info = System.Runtime.InteropServices.Marshal.PtrToStructure<NativeWindowApi.MINMAXINFO>(lParam);
        info.ptMinTrackSize = new NativeWindowApi.POINT(
            (int)Math.Round(_minWidth * _dpiScale),
            (int)Math.Round(_minHeight * _dpiScale));
        System.Runtime.InteropServices.Marshal.StructureToPtr(info, lParam, false);
    }

    /// <summary>
    /// 窗口的显示器缩放变化（拖到另一块屏 / 系统改缩放）时：更新缩放、按**逻辑尺寸不变**
    /// 重算物理尺寸，并立刻重建缓冲重画。
    ///
    /// ★ 为什么必须处理 ★ 本进程是 Per-Monitor V2 感知（宿主启动时设的），
    /// 窗口一跨到不同缩放的屏，鼠标消息/GetWindowRect 就都换成**那块屏**的物理像素；
    /// 若还拿旧 `_dpiScale` 去除，坐标错位（按钮偏位）、`UpdateLayeredWindow` 的
    /// `psize` 也可能和缓冲对不上（越界读取 → 闪退）。
    /// </summary>
    private void OnDpiChanged(IntPtr wParam)
    {
        int newDpi = (short)(wParam.ToInt64() & 0xFFFF);
        if (newDpi < 96) newDpi = 96;
        float newScale = newDpi / 96f;
        if (Math.Abs(newScale - _dpiScale) < 0.001f) return;

        // 逻辑尺寸不变，物理尺寸按新缩放重算
        int logicalW = Math.Max(1, (int)Math.Round(_scaledWidth / _dpiScale));
        int logicalH = Math.Max(1, (int)Math.Round(_scaledHeight / _dpiScale));
        _dpiScale = newScale;
        int newW = Math.Max(1, (int)Math.Round(logicalW * newScale));
        int newH = Math.Max(1, (int)Math.Round(logicalH * newScale));
        _scaledWidth = newW;
        _scaledHeight = newH;

        NativeWindowApi.SetWindowPos(_hwnd, IntPtr.Zero, 0, 0, newW, newH,
            NativeWindowApi.SWP_NOMOVE | NativeWindowApi.SWP_NOZORDER | NativeWindowApi.SWP_NOACTIVATE);

        // 不等 SetWindowPos 触发的 WM_SIZE（它看到尺寸已一致会提前返回），直接同步重建一次，
        // 避免「窗口物理尺寸已变、缓冲还是旧尺寸」的中间态 —— 那正是跨屏时贴图越界的风险点。
        DestroyBuffer();
        CreateBuffer();
        Redraw();
    }

    private void OnSize()
    {
        if (!IsOpen) return;

        var rect = new NativeWindowApi.RECT();
        NativeWindowApi.GetClientRect(_hwnd, out rect);
        int w = Math.Max(1, rect.right - rect.left);
        int h = Math.Max(1, rect.bottom - rect.top);
        if (w == _scaledWidth && h == _scaledHeight) return;

        _scaledWidth = w;
        _scaledHeight = h;

        // 尺寸变了 ⇒ 绘制缓冲必须按新尺寸重建（否则 UpdateLayeredWindow 尺寸与缓冲不符）
        DestroyBuffer();
        CreateBuffer();

        RememberBounds();
        Redraw();

        try { Resized?.Invoke((int)(w / _dpiScale), (int)(h / _dpiScale)); } catch { /* 回调不该影响窗口 */ }
    }

    /// <summary>把当前真实的窗口位置与尺寸记进 <see cref="State"/>（移动/缩放后调用）。</summary>
    private void RememberBounds()
    {
        if (!IsOpen) return;
        var rect = new NativeWindowApi.RECT();
        if (!NativeWindowApi.GetWindowRect(_hwnd, out rect)) return;

        State.X = (int)Math.Round(rect.left / _dpiScale);
        State.Y = (int)Math.Round(rect.top / _dpiScale);
        State.Width = Math.Max(1, (int)Math.Round((rect.right - rect.left) / _dpiScale));
        State.Height = Math.Max(1, (int)Math.Round((rect.bottom - rect.top) / _dpiScale));
        State.HasBounds = true;
    }

    // ====================================================================
    // 绘制
    // ====================================================================

    private void Redraw()
    {
        if (_surface == null || _draw == null || !IsOpen) return;

        var canvas = _surface.Canvas;
        canvas.Clear(SKColors.Transparent);
        canvas.Save();
        canvas.Scale(_dpiScale);

        float w = _scaledWidth / _dpiScale;
        float h = _scaledHeight / _dpiScale;

        // 圆角背景（与宿主窗口同一套观感）
        using var bgPaint = new SKPaint { Color = new SKColor(30, 30, 30), IsAntialias = true };
        using var borderPaint = new SKPaint
        {
            Color = new SKColor(255, 255, 255, 95),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = ChromeStroke,
            IsAntialias = true,
        };

        var bg = new SKRoundRect(new SKRect(0, 0, w, h), CornerRadius);

        canvas.DrawRoundRect(bg, bgPaint);

        canvas.Save();
        canvas.ClipRoundRect(bg, antialias: true);
        _draw(canvas, (int)w, (int)h);
        canvas.Restore();

        var border = new SKRoundRect(new SKRect(BorderInset, BorderInset, w - BorderInset, h - BorderInset), CornerRadius - 1f);
        canvas.DrawRoundRect(border, borderPaint);
        DrawCloseButton(canvas, w);

        canvas.Restore();

        PushToScreen();
    }

    private static void DrawCloseButton(SKCanvas canvas, float w)
    {
        using var disc = new SKPaint { Color = new SKColor(255, 255, 255, 30), IsAntialias = true };
        using var cross = new SKPaint
        {
            Color = new SKColor(255, 255, 255, 210),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1.5f,
            IsAntialias = true,
        };

        float cx = w - CloseButtonX, cy = CloseButtonY, r = CloseButtonR;
        canvas.DrawCircle(cx, cy, r, disc);
        canvas.DrawLine(cx - 4, cy - 4, cx + 4, cy + 4, cross);
        canvas.DrawLine(cx + 4, cy - 4, cx - 4, cy + 4, cross);
    }

    private bool IsCloseHit(float x, float y)
    {
        float w = _scaledWidth / _dpiScale;
        float dx = x - (w - CloseButtonX), dy = y - CloseButtonY;
        return dx * dx + dy * dy <= CloseButtonR * CloseButtonR;
    }

    /// <summary>
    /// 把后台缓冲贴到屏幕上。
    ///
    /// ⚠️ 位置取**当前真实窗口矩形**（否则拖动后会被弹回原处），
    /// 但**尺寸必须取缓冲尺寸 `_scaledWidth × _scaledHeight`**（不是窗口矩形的尺寸）。
    /// 原因：`UpdateLayeredWindow` 的 `psize` 必须与 `hdcSrc` 里那张位图**严格一致**，
    /// 否则它会从缓冲末尾之外继续读像素 —— 访问违例，整个宿主闪退。
    /// 用 `_scaledWidth/_scaledHeight`（缓冲就是按它分配的），两者永不脱节。
    /// </summary>
    private void PushToScreen()
    {
        var rect = new NativeWindowApi.RECT();
        if (!NativeWindowApi.GetWindowRect(_hwnd, out rect)) return;

        var screenDc = NativeWindowApi.GetDC(IntPtr.Zero);
        try
        {
            var src = new NativeWindowApi.POINT(0, 0);
            var dst = new NativeWindowApi.POINT(rect.left, rect.top);
            var size = new NativeWindowApi.SIZE(_scaledWidth, _scaledHeight);
            var blend = new NativeWindowApi.BLENDFUNCTION
            {
                BlendOp = NativeWindowApi.AC_SRC_OVER,
                BlendFlags = 0,
                SourceConstantAlpha = 255,
                AlphaFormat = NativeWindowApi.AC_SRC_ALPHA,
            };

            NativeWindowApi.UpdateLayeredWindow(_hwnd, screenDc, ref dst, ref size, _memDc, ref src, 0,
                ref blend, NativeWindowApi.ULW_ALPHA);
        }
        finally
        {
            NativeWindowApi.ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    private void CreateBuffer()
    {
        var screenDc = NativeWindowApi.GetDC(IntPtr.Zero);
        try
        {
            _memDc = NativeWindowApi.CreateCompatibleDC(screenDc);

            var bmi = new NativeWindowApi.BITMAPINFO
            {
                bmiHeader = new NativeWindowApi.BITMAPINFOHEADER
                {
                    biSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf(typeof(NativeWindowApi.BITMAPINFOHEADER)),
                    biWidth = _scaledWidth,
                    biHeight = -_scaledHeight,   // 负高度 = 自上而下，与 Skia 的坐标方向一致
                    biPlanes = 1,
                    biBitCount = 32,
                    biCompression = 0,
                },
            };

            _hBitmap = NativeWindowApi.CreateDIBSection(screenDc, ref bmi, NativeWindowApi.DIB_RGB_COLORS,
                out _pBits, IntPtr.Zero, 0);
            _oldBitmap = NativeWindowApi.SelectObject(_memDc, _hBitmap);

            var info = new SKImageInfo(_scaledWidth, _scaledHeight, SKColorType.Bgra8888, SKAlphaType.Premul);
            _surface = SKSurface.Create(info, _pBits, _scaledWidth * 4);
        }
        finally
        {
            NativeWindowApi.ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    private void DestroyBuffer()
    {
        try { _surface?.Dispose(); } catch { /* 已释放 */ }
        _surface = null;

        if (_memDc != IntPtr.Zero && _oldBitmap != IntPtr.Zero)
            NativeWindowApi.SelectObject(_memDc, _oldBitmap);
        if (_hBitmap != IntPtr.Zero) NativeWindowApi.DeleteObject(_hBitmap);
        if (_memDc != IntPtr.Zero) NativeWindowApi.DeleteDC(_memDc);

        _memDc = _hBitmap = _oldBitmap = _pBits = IntPtr.Zero;
    }

    // ====================================================================
    // 辅助
    // ====================================================================

    private static bool RegisterClass()
    {
        lock (Windows)
        {
            if (_classRegistered) return true;

            var wc = new NativeWindowApi.WNDCLASSEX
            {
                cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf(typeof(NativeWindowApi.WNDCLASSEX)),
                // ★ CS_DBLCLKS ★ 有了它才会收到 WM_LBUTTONDBLCLK，
                //   否则双击只能靠「两次点击的时间+位移」自己猜（宿主窗口就是那样）。
                style = NativeWindowApi.CS_DBLCLKS,
                lpfnWndProc = System.Runtime.InteropServices.Marshal.GetFunctionPointerForDelegate(WndProcRef),
                hInstance = CurrentModule(),
                lpszClassName = ClassName,
                hCursor = NativeWindowApi.LoadCursor(IntPtr.Zero, NativeWindowApi.IDC_ARROW),
            };

            ushort atom = NativeWindowApi.RegisterClassEx(ref wc);
            const int ERROR_CLASS_ALREADY_EXISTS = 1410;
            if (atom == 0 && System.Runtime.InteropServices.Marshal.GetLastWin32Error() != ERROR_CLASS_ALREADY_EXISTS)
                return false;

            _classRegistered = true;
            return true;
        }
    }

    private static IntPtr CurrentModule()
        => System.Diagnostics.Process.GetCurrentProcess().MainModule?.BaseAddress ?? IntPtr.Zero;

    private static bool IsDown(int vk) => (NativeWindowApi.GetKeyState(vk) & 0x8000) != 0;

    private void ScreenToClientLocal(ref NativeWindowApi.POINT pt)
    {
        var rect = new NativeWindowApi.RECT();
        if (!NativeWindowApi.GetWindowRect(_hwnd, out rect)) return;
        pt.x -= rect.left;
        pt.y -= rect.top;
    }

    private static int Lo(IntPtr value) => (short)(value.ToInt64() & 0xFFFF);
    private static int Hi(IntPtr value) => (short)((value.ToInt64() >> 16) & 0xFFFF);

    // 主屏工作区（取法与本机 DPI 一致地交给 WinForms，避免自造 monitor 枚举）
    private static int ScreenLeft => System.Windows.Forms.Screen.PrimaryScreen?.WorkingArea.Left ?? 0;
    private static int ScreenTop => System.Windows.Forms.Screen.PrimaryScreen?.WorkingArea.Top ?? 0;
    private static int ScreenWidth => System.Windows.Forms.Screen.PrimaryScreen?.WorkingArea.Width ?? 1920;
    private static int ScreenHeight => System.Windows.Forms.Screen.PrimaryScreen?.WorkingArea.Height ?? 1080;
}
