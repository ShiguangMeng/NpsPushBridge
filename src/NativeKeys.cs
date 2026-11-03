using System.Diagnostics;
using System.Runtime.InteropServices;

namespace NpsPushBridge;

/// <summary>
/// **只读地**读取「方向键 / Home / End / Delete / Shift / Ctrl」的按下状态。
///
/// ── 为什么需要它（宿主接口的天花板）──────────────────────────────────────
/// 宿主的插件窗口只把 <c>WM_CHAR</c> 转给插件（见 <c>PluginWindow.WndProc</c>：
/// <c>_key?.Invoke((char)(wParam &amp; 0xFFFF))</c>），<c>WM_KEYDOWN</c> 仅用于 Esc 关窗。
/// 因此插件**收不到方向键** —— 它们不产生字符。结果就是「输入框里改不了中间的字」。
///
/// ── 为什么用「读键状态」而不是「子类化宿主窗口」──────────────────────────
/// 子类化（<c>SetWindowLongPtr(GWLP_WNDPROC)</c>）能拿到完整按键，但它会**改变宿主窗口的
/// 消息处理流程** —— 一旦消息链或委托生命周期出错，坏掉的是宿主的窗口，属于把风险转嫁出去。
/// 本项目的底线是「不改宿主」；运行期挂钩虽不算改源码，也偏离了这条线的精神。
/// <c>GetAsyncKeyState</c> 是**纯读取**：读不到就是读不到，绝不会影响宿主分毫。
///
/// ── 已知代价（诚实说明）──────────────────────────────────────────────────
/// 1. 它是**轮询**，所以要求有一个周期性的心跳（见 <see cref="ConfigPanel.Tick"/>，
///    只在有输入框聚焦时才跑，其余时间零开销）；
/// 2. 它读的是**全局**键状态，所以必须用「前台窗口是否属于本进程」做闸门（<see cref="OwnForeground"/>），
///    否则用户在别的程序里按方向键也会被我们吃掉；
/// 3. 长按的**自动重复**要自己做（<see cref="KeyRepeat"/>）。
/// 换句话说：这是「在拿不到 WM_KEYDOWN 的前提下，能做到的最好结果」。
/// 它的失效方式是**静默降级**（读不到就不动光标），绝不会破坏别的功能；
/// 即便整个类都失效，光标定位仍可用鼠标点击与拖选完成。
/// </summary>
internal static class NativeKeys
{
    private const int VK_SHIFT = 0x10;
    private const int VK_CONTROL = 0x11;
    private const int VK_END = 0x23;
    private const int VK_HOME = 0x24;
    private const int VK_LEFT = 0x25;
    private const int VK_RIGHT = 0x27;
    private const int VK_DELETE = 0x2E;

    /// <summary>一次采样得到的「此刻哪些编辑键是按下的」。<see cref="Usable"/> 为 false 表示这次采样无效。</summary>
    public readonly record struct Snapshot(
        bool Left, bool Right, bool Home, bool End, bool Delete, bool Shift, bool Ctrl, bool Usable)
    {
        /// <summary>「什么都没按」的采样（同时也表示不可用）。</summary>
        public static readonly Snapshot None = new(false, false, false, false, false, false, false, false);
    }

    /// <summary>
    /// 采样一次。任何异常（P/Invoke 不可用、被安全策略拦下等）都收敛为
    /// <see cref="Snapshot.None"/> —— 本类**永不抛异常**，因为它的失败只是「没有额外能力」。
    /// </summary>
    public static Snapshot Poll()
    {
        try
        {
            if (!OwnForeground()) return Snapshot.None;

            return new Snapshot(
                Down(VK_LEFT), Down(VK_RIGHT), Down(VK_HOME), Down(VK_END), Down(VK_DELETE),
                Down(VK_SHIFT), Down(VK_CONTROL), Usable: true);
        }
        catch
        {
            return Snapshot.None;
        }
    }

    /// <summary>前台窗口是否属于本进程（插件的配置窗口与宿主设置窗口都在本进程内）。</summary>
    private static bool OwnForeground()
    {
        IntPtr foreground = GetForegroundWindow();
        if (foreground == IntPtr.Zero) return false;

        GetWindowThreadProcessId(foreground, out uint pid);
        return pid != 0 && pid == (uint)Environment.ProcessId;
    }

    private static bool Down(int virtualKey)
    {
        // 高位置位 = 此刻处于按下状态。低位的「自上次调用以来按过」在这里刻意不用：
        // 轮询间隔可能大于两次快速点按的间隔，靠低位会漏；靠高位 + 自己的重复计时更稳。
        return (GetAsyncKeyState(virtualKey) & 0x8000) != 0;
    }

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
}

/// <summary>
/// 「按住不放要连续触发」的状态机（方向键长按连跳）。
///
/// 键盘的自动重复本来是系统做的，但我们走的是「轮询按下状态」，系统那份重复事件拿不到，
/// 所以自己实现：**首次立刻触发**，按住超过 <see cref="RepeatDelayMs"/> 后每
/// <see cref="RepeatIntervalMs"/> 触发一次；松手即复位。
///
/// 纯逻辑、无 P/Invoke、无 Skia —— 时间由调用方传入（<c>nowMs</c>），因此可以逐条断言。
/// </summary>
internal sealed class KeyRepeat
{
    /// <summary>按住多久之后开始重复（与系统默认的约 500ms 接近，取略快一点更跟手）。</summary>
    public long RepeatDelayMs { get; init; } = 400;

    /// <summary>重复间隔。</summary>
    public long RepeatIntervalMs { get; init; } = 45;

    /// <summary>每个键的状态：是否按着 + 下一次允许触发的时刻。</summary>
    private readonly Dictionary<int, (bool Down, long NextFire)> _state = new();

    /// <summary>
    /// 问一次「这个键现在该触发吗」。
    /// </summary>
    /// <param name="keyId">键的标识（用 VK 码即可）。</param>
    /// <param name="isDown">此刻是否按下。</param>
    /// <param name="nowMs">当前时刻（毫秒，单调递增即可）。</param>
    public bool ShouldFire(int keyId, bool isDown, long nowMs)
    {
        if (!isDown)
        {
            // 松手：清掉状态，下次按下会立刻触发（符合「按一下走一格」的直觉）
            _state.Remove(keyId);
            return false;
        }

        if (!_state.TryGetValue(keyId, out var entry))
        {
            _state[keyId] = (true, nowMs + RepeatDelayMs);
            return true;
        }

        if (nowMs < entry.NextFire) return false;

        _state[keyId] = (true, nowMs + RepeatIntervalMs);
        return true;
    }

    /// <summary>全部复位（失焦 / 切换输入框时调用，避免「松手事件丢了之后一直连跳」）。</summary>
    public void Reset() => _state.Clear();
}
