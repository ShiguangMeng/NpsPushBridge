namespace NpsPushBridge;

/// <summary>
/// 应用黑名单：命中的应用，消息**不推送到灵动岛**（在服务端被静默丢弃）。
///
/// 每行一条（空行与 <c>#</c> 开头的行忽略），支持两种写法：
///   1. 精确名   微信          com.tencent.mm
///   2. 前缀通配 com.tencent.*  微信*
///
/// ★ 匹配对象是「两个名字」，任一中即拦 ★
/// 一条入站消息里有两个可用来识别应用的字符串：
///   · <c>Package</c> —— 发送端的**原始包名**（如 <c>com.tencent.mm</c>）；
///   · <c>Kind</c>    —— 界面上的**显示名**（如「微信」，可能经翻译或由发送端直接给出）。
/// 用户可能只知道其中一个（手机模板里 <c>from</c> 是包名，通知里显示的是应用名），
/// 所以两者都参与匹配 —— 只查一个会变成「明明写对了却拦不住」。
///
/// ★ 为什么复用 <see cref="IconCatalog.Normalize"/> 做归一化 ★
/// 「微信」「微信 」「微信(3条新消息)」在人看来是同一个应用，而它们字节不同。
/// 图标映射的查表已经用这套规则把三者归一，黑名单若另写一套，就会出现
/// 「图标能匹配上、黑名单匹配不上」这种自相矛盾的行为。归一化规则只留一处真源。
///
/// ★ 空名单 = 不拦截任何应用 ★
/// 这与 <see cref="IpWhitelist"/> 的「空名单 = 放行全部」方向相反，但对应用户的直觉：
/// 白名单是「只允许这些」，黑名单是「只拦住这些」，清空即回到不干预。
/// </summary>
internal sealed class AppBlacklist
{
    /// <summary>归一化后的精确条目。</summary>
    private readonly HashSet<string> _exact = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>归一化后的通配前缀（各条 <c>*</c> 之前的部分）。</summary>
    private readonly List<string> _prefixes = new();

    /// <summary>没有任何有效条目。此时 <see cref="IsBlocked"/> 恒为 false。</summary>
    public bool IsEmpty => _exact.Count == 0 && _prefixes.Count == 0;

    /// <summary>有效条目数（精确 + 通配），供界面与状态页显示。</summary>
    public int RuleCount => _exact.Count + _prefixes.Count;

    /// <summary>其中的通配条目数 —— 界面上单独提示「含 N 条通配」，因为通配更容易误伤。</summary>
    public int WildcardCount => _prefixes.Count;

    /// <summary>从持久化文本解析。认不出的行只是被跳过，绝不让整份配置失效。</summary>
    public static AppBlacklist Parse(string? raw)
    {
        var list = new AppBlacklist();
        if (string.IsNullOrWhiteSpace(raw)) return list;

        foreach (var rawLine in raw.Split('\n'))
        {
            var line = rawLine.Trim().TrimEnd('\r', ' ', '\t');
            if (line.Length == 0 || line[0] == '#') continue;

            int star = line.IndexOf('*');
            if (star >= 0)
            {
                // 只支持「末尾通配」：取 * 之前的部分当前缀。
                // 中间带 * 的写法（com.*.mm）在这里被当作「com. 前缀」处理会与用户预期不符，
                // 因此界面侧的校验会把它判为错误；解析层保持宽容（旧配置里真有这种行也不该整份失效）。
                var prefix = IconCatalog.Normalize(line[..star]);
                if (prefix.Length == 0) continue;
                if (!list._prefixes.Contains(prefix, StringComparer.Ordinal)) list._prefixes.Add(prefix);
                continue;
            }

            var key = IconCatalog.Normalize(line);
            if (key.Length > 0) list._exact.Add(key);
        }

        return list;
    }

    /// <summary>
    /// 这条消息的发送方是否在黑名单里。<paramref name="package"/> 与 <paramref name="kind"/>
    /// **任一命中即返回 true**（任一个为空串时该侧不参与匹配）。
    /// </summary>
    public bool IsBlocked(string? package, string? kind)
        => !IsEmpty && (Matches(package) || Matches(kind));

    /// <summary>单个名字是否命中（先精确、再通配前缀）。</summary>
    private bool Matches(string? value)
    {
        var key = IconCatalog.Normalize(value);
        if (key.Length == 0) return false;

        if (_exact.Contains(key)) return true;

        for (int i = 0; i < _prefixes.Count; i++)
            if (key.StartsWith(_prefixes[i], StringComparison.Ordinal)) return true;

        return false;
    }

    /// <summary>把条目数摘要成一句日志 / 状态页文案。</summary>
    public string Describe()
        => IsEmpty
            ? "未配置（不拦截任何应用）"
            : WildcardCount > 0
                ? $"{RuleCount} 条规则（含 {WildcardCount} 条通配）"
                : $"{RuleCount} 条规则";
}
