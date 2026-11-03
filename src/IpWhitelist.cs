using System.Net;
using System.Net.Sockets;

namespace NpsPushBridge;

/// <summary>
/// 局域网来源白名单匹配器。
///
/// 支持三种写法，每行一条（空行与 # 开头的行会被忽略）：
///   1. 精确地址     192.168.1.23        fe80::1
///   2. 通配前缀     192.168.1.*         10.*
///   3. CIDR 网段    192.168.1.0/24
///
/// 白名单为空 = 不限制来源（放行所有能连上本机端口的设备），此时会在日志里给出明确提示。
/// </summary>
internal sealed class IpWhitelist
{
    private readonly HashSet<string> _exact = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _prefixes = new();
    private readonly List<(uint Network, uint Mask)> _cidr = new();

    /// <summary>没有任何有效规则。此时 <see cref="IsAllowed"/> 恒为 true。</summary>
    public bool IsEmpty => _exact.Count == 0 && _prefixes.Count == 0 && _cidr.Count == 0;

    public int RuleCount => _exact.Count + _prefixes.Count + _cidr.Count;

    public static IpWhitelist Parse(string? raw)
    {
        var w = new IpWhitelist();
        if (string.IsNullOrWhiteSpace(raw)) return w;

        foreach (var rawLine in raw.Split('\n'))
        {
            var line = rawLine.Trim().TrimEnd('\r', ' ', '\t');
            if (line.Length == 0 || line[0] == '#') continue;

            // 3) CIDR：192.168.1.0/24
            int slash = line.IndexOf('/');
            if (slash > 0
                && int.TryParse(line[(slash + 1)..], out int bits) && bits is >= 0 and <= 32
                && IPAddress.TryParse(line[..slash], out var netAddr)
                && netAddr.AddressFamily == AddressFamily.InterNetwork)
            {
                uint mask = bits == 0 ? 0u : uint.MaxValue << (32 - bits);
                w._cidr.Add((ToUInt(netAddr) & mask, mask));
                continue;
            }

            // 2) 通配前缀：192.168.1.*
            int star = line.IndexOf('*');
            if (star >= 0)
            {
                var prefix = line[..star];
                if (prefix.Length > 0) w._prefixes.Add(prefix);
                continue;
            }

            // 1) 精确地址
            if (IPAddress.TryParse(line, out _)) w._exact.Add(line);
        }
        return w;
    }

    /// <summary>判断远端地址是否被放行。白名单为空时视为不限制来源，放行全部。</summary>
    public bool IsAllowed(IPAddress remote)
    {
        if (IsEmpty) return true;   // 未配置规则 = 不限制来源（界面与状态页都会给出提示）

        if (remote.AddressFamily == AddressFamily.InterNetworkV6 && remote.IsIPv4MappedToIPv6)
            remote = remote.MapToIPv4();

        string text = remote.ToString();

        if (_exact.Contains(text)) return true;

        for (int i = 0; i < _prefixes.Count; i++)
            if (text.StartsWith(_prefixes[i], StringComparison.OrdinalIgnoreCase)) return true;

        if (remote.AddressFamily == AddressFamily.InterNetwork)
        {
            uint v = ToUInt(remote);
            for (int i = 0; i < _cidr.Count; i++)
                if ((v & _cidr[i].Mask) == _cidr[i].Network) return true;
        }

        return false;
    }

    /// <summary>把规则数量摘要成一句日志文案。</summary>
    public string Describe() => IsEmpty ? "未配置（放行全部来源）" : $"{RuleCount} 条规则";

    private static uint ToUInt(IPAddress v4)
    {
        var b = v4.GetAddressBytes();
        return ((uint)b[0] << 24) | ((uint)b[1] << 16) | ((uint)b[2] << 8) | b[3];
    }
}
