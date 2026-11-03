using System.IO;
using System.Text;

namespace NpsPushBridge;

/// <summary>
/// 定长环形日志缓冲：后台网络线程写入，渲染线程与配置界面读取，全程加锁保证一致。
/// 只保留最近 <see cref="Capacity"/> 行，避免插件长跑时内存无限增长。
///
/// ★ 另有一条「宿主日志镜像」通道 <see cref="MirrorToHostLog"/> ★
/// 本缓冲只在插件内存里，**不会写进宿主的 app.log**，所以插件在用户机器上出问题时
/// 无法远程取证（宿主日志里只有 <c>PluginHost</c> 自己打的「插件提醒已投递」，
/// 看不到本插件「提示条为什么没上岛」的判定过程）。
/// 这里提供一个可选的文件镜像：把关键行同时追加到宿主同目录的 app.log，
/// 让「上岛四条件」卡在哪一步在日志里一目了然。
/// </summary>
internal sealed class BridgeLog
{
    public const int Capacity = 400;

    /// <summary>宿主日志文件（与本插件内存缓冲同格式落地），用于远程取证。</summary>
    private static readonly string? HostLogPath = ResolveHostLogPath();

    /// <summary>镜像写入的互斥锁（与内存缓冲的锁分开，避免磁盘 IO 拖住渲染线程读取）。</summary>
    private static readonly object MirrorGate = new();

    /// <summary>
    /// 解析宿主 app.log 的路径。与宿主 <c>Logger.ResolveLogPath</c> 保持同一位置，
    /// 这样排查时只需要看一个文件。
    /// </summary>
    private static string? ResolveHostLogPath()
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "NotchPeninsula");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "app.log");
        }
        catch
        {
            return null;   // 取不到路径就静默降级为「只写内存缓冲」
        }
    }

    private readonly object _gate = new();
    private readonly string[] _buffer = new string[Capacity];
    private int _start;   // 最旧一行所在下标
    private int _count;

    /// <summary>追加一行（自动加 "HH:mm:ss " 前缀）—— 只写内存缓冲，**不落宿主日志**。</summary>
    public void Add(string message)
    {
        var line = $"{DateTime.Now:HH:mm:ss} {message}";
        lock (_gate)
        {
            int index = (_start + _count) % Capacity;
            _buffer[index] = line;
            if (_count < Capacity) _count++;
            else _start = (_start + 1) % Capacity;   // 覆盖最旧一行
        }
    }

    /// <summary>
    /// ★ 更新相关的诊断行：既进内存缓冲，也**始终**写入宿主日志。
    ///
    /// 为什么这一类默认就要落文件：更新检查每 6 小时才跑一次，一次最多产出几行，
    /// 对日志体量的影响可以忽略；而它恰恰是最需要远程取证的部分 ——
    /// 「自动检查有没有判定出新版本 / 提示条有没有注册成功 / 是不是被预算挤掉」
    /// 这三个问题在 app.log 里必须有痕迹，否则用户复现一次也定不到卡在哪一步。
    /// 与之相对，消息推送、HTTP 转发这类高频日志**不**走这里，避免刷爆日志。
    /// </summary>
    public void AddUpdate(string message)
    {
        Add(message);
        WriteToHostLog(message);
    }

    /// <summary>
    /// 只写宿主日志、不进内存缓冲 —— 给「可能高频触发」的诊断点用
    /// （例如提示条因预算不足被跳过），避免把环形缓冲冲掉。
    /// 带 [NpsPushBridge] 前缀，便于在 app.log 里过滤本插件的行。
    /// </summary>
    public void AddHostOnly(string message)
    {
        WriteToHostLog(message);
    }

    private static void WriteToHostLog(string message)
    {
        var path = HostLogPath;
        if (path == null) return;
        try
        {
            lock (MirrorGate)
            {
                File.AppendAllText(path,
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [INFO] [NpsPushBridge] {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // 写不进去（权限 / 被占用）不能影响插件主流程
        }
    }

    /// <summary>按时间顺序返回当前全部日志行（快照，调用方可安全遍历）。</summary>
    public string[] Snapshot()
    {
        lock (_gate)
        {
            var result = new string[_count];
            for (int i = 0; i < _count; i++) result[i] = _buffer[(_start + i) % Capacity];
            return result;
        }
    }

    public int Count { get { lock (_gate) return _count; } }

    public void Clear()
    {
        lock (_gate)
        {
            Array.Clear(_buffer, 0, _buffer.Length);
            _start = 0;
            _count = 0;
        }
    }

    /// <summary>最后一行内容（供灵动岛组件显示"最近一条"摘要）。</summary>
    public string LastOrEmpty()
    {
        lock (_gate) return _count == 0 ? "" : _buffer[(_start + _count - 1) % Capacity];
    }

    /// <summary>把日志导出成整段文本（用于一键复制排查）。</summary>
    public string Dump()
    {
        var lines = Snapshot();
        var sb = new StringBuilder(lines.Length * 64);
        foreach (var l in lines) sb.AppendLine(l);
        return sb.ToString();
    }
}
