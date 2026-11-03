using System.Net;
using System.Net.Sockets;

namespace NpsPushBridge;

/// <summary>
/// 用户可输入项的**校验与归一化规则**（纯逻辑、零依赖、不碰文件系统与 SkiaSharp，可独立测试）。
///
/// ── 为什么必须单独抽一层 ────────────────────────────────────────────────
/// 配置界面里三个可输入的位置（白名单、端口、图标映射）都把文本交给**宽容的解析器**：
///   · <see cref="IpWhitelist.Parse"/> —— 认不出来的行**直接丢弃**；
///   · <see cref="IconOverrides.Parse"/> —— 没有 <c>=</c> 或键/值为空的行**直接丢弃**。
/// 这个宽容是**运行时**该有的（旧配置里一行坏了不能让整份配置失效），
/// 但用在**用户刚敲进来的字符**上就成了陷阱：用户打错一位（<c>192.168.1.256</c>）、
/// 或者写成反斜杠路径，界面毫无反应、保存也「成功」，实际那条规则**从未生效**，
/// 而用户无从知道。所以这里的原则是：
///
///   ★ 宽容留给「读旧配置」，严格留给「用户正在输入」★
///
/// 判定分三级（<see cref="Level"/>）：<c>Error</c> 阻止保存并说明原因；
/// <c>Warn</c> 允许保存但提示可能的意外；两者都不再让错误静默消失。
///
/// ⚠️ 这里**不做文件系统探测**（如「本地图标文件是否存在」）：那会让本类的输出依赖
/// 运行环境，测试不再可重复。需要在界面上补这类信息时，由调用方另行拼接。
/// </summary>
internal static class InputRules
{
    /// <summary>校验结论的严重程度。顺序有意义：<see cref="Error"/> 最严重。</summary>
    public enum Level
    {
        /// <summary>空行 / 注释 —— 既不是错误也没有内容，应当被跳过。</summary>
        Ignored,
        /// <summary>可以保存，且会按预期生效。</summary>
        Ok,
        /// <summary>可以保存，但可能不是你想要的（例如会意外匹配到别的网段）。</summary>
        Warn,
        /// <summary>**不允许保存**：保存后这条会静默失效，必须让用户先改对。</summary>
        Error,
    }

    /// <summary>一条输入的校验结论。</summary>
    /// <param name="Level">严重程度。</param>
    /// <param name="Message">给用户看的原因 / 说明（<see cref="Level.Ok"/> 时可能为空，也可能是有用的提示）。</param>
    /// <param name="Normalized">归一化后的值 —— **实际会被保存 / 生效的那个值**。</param>
    public readonly record struct Verdict(Level Level, string Message, string Normalized)
    {
        /// <summary>
        /// 「什么都没有」的那个结论。
        ///
        /// ⚠️ **不要用 <c>default(Verdict)</c>** —— <c>readonly record struct</c> 的 <c>default</c>
        /// 会把引用类型字段留成 <c>null</c>，于是 <see cref="Message"/> 是 null，
        /// 任何 <c>Message.Length</c> 之类的判断都会当场 NRE（本项目在别处踩过同一个坑）。
        /// 需要「空结论」时一律用这个常量。
        /// </summary>
        public static readonly Verdict None = new(Level.Ignored, "", "");

        /// <summary>是否必须阻止保存。</summary>
        public bool BlocksSave => Level == Level.Error;

        /// <summary>
        /// 是否有值得显示的字（界面据此决定要不要画提示）。
        /// 用 <c>IsNullOrEmpty</c> 而不是 <c>Length &gt; 0</c>：后者在 <c>default</c> 出来的
        /// 对象上会 NRE，而这个属性正是最可能被用在那种对象上的地方。
        /// </summary>
        public bool HasMessage => !string.IsNullOrEmpty(Message);

        /// <summary>是否应当被写进配置（<see cref="Level.Ignored"/> 与 <see cref="Level.Error"/> 都不写）。</summary>
        public bool IsStorable => Level is Level.Ok or Level.Warn;
    }

    // ====================================================================
    // 端口
    // ====================================================================

    /// <summary>端口取值范围 —— 与 <c>HttpBridgeServer</c> 的监听逻辑一致。</summary>
    public const int PortMin = 1;
    public const int PortMax = 65535;

    /// <summary>端口输入框的用途说明（界面直接显示，保证「用途」与代码同源）。</summary>
    public const string PortPurpose = "本机监听端口";

    /// <summary>
    /// 端口输入框的校验规则说明（界面直接显示）。
    ///
    /// ⚠️ 只能是 <c>static readonly</c> 而不是 <c>const</c>：C# 的插值字符串**不是**编译期常量，
    /// 即使里面全是 <c>const int</c>。写成 <c>const</c> 会直接编译不过（CS0133）。
    /// 值本来也是纯常量派生，静态初始化一次即可。
    /// </summary>
    public static readonly string PortRule = $"{PortMin}-{PortMax} 的整数，默认 1403";

    private const int WellKnownPortLimit = 1024;

    /// <summary>
    /// 校验端口文本。允许首尾空白（用户从别处复制常带空格）。
    ///
    /// 1024 以下只给 <see cref="Level.Warn"/> 而不是错误：那是**系统保留端口范围**，
    /// 确有可能被别的服务占用或需要更高权限，但并非绝对不可用 —— 拦下来反而是错的。
    /// </summary>
    public static Verdict CheckPort(string? text)
    {
        var value = (text ?? "").Trim();
        if (value.Length == 0)
            return new Verdict(Level.Error, $"请填写端口（{PortRule}）", "");

        if (!int.TryParse(value, out int port))
            return new Verdict(Level.Error, $"「{value}」不是整数；{PortRule}", value);

        if (port < PortMin || port > PortMax)
            return new Verdict(Level.Error, $"端口 {port} 超出范围；{PortRule}", value);

        if (port < WellKnownPortLimit)
            return new Verdict(Level.Warn,
                $"{port} 属系统保留端口（{WellKnownPortLimit} 以下），可能已被占用或需要管理员权限",
                port.ToString());

        return new Verdict(Level.Ok, "", port.ToString());
    }

    /// <summary>把端口文本按「能不能用」解析出来。给真正要保存 / 重启监听时用。</summary>
    public static bool TryResolvePort(string? text, out int port)
    {
        var verdict = CheckPort(text);
        if (verdict.Level == Level.Error) { port = 0; return false; }
        return int.TryParse(verdict.Normalized, out port);
    }

    // ====================================================================
    // 白名单（每条一个输入框）
    // ====================================================================

    /// <summary>白名单输入框的用途说明。</summary>
    public const string WhitelistPurpose = "允许连接的来源地址";

    /// <summary>白名单输入框的校验规则说明（短版，放在提示行里）。</summary>
    public const string WhitelistRule = "支持 192.168.1.23 / 192.168.1.* / 192.168.1.0/24";

    /// <summary>白名单单条的最长长度 —— 与界面输入上限一致（IPv6 全文也不到 64）。</summary>
    public const int WhitelistMaxLength = 120;

    /// <summary>
    /// 校验白名单里的**一条**。
    ///
    /// 判定顺序与 <see cref="IpWhitelist.Parse"/> **逐条对齐**（注释 → CIDR → 通配 → 精确），
    /// 否则会出现「界面说合法、运行时却按另一套规则解析」的最坏情况。
    /// 差别只在于：这里为每一种失败给出**原因**，而不是默默丢掉。
    /// </summary>
    public static Verdict CheckWhitelistEntry(string? text)
    {
        var value = (text ?? "").Trim();
        if (value.Length == 0) return new Verdict(Level.Ignored, "", "");

        if (value[0] == '#') return new Verdict(Level.Ignored, "", "");

        if (value.Length > WhitelistMaxLength)
            return new Verdict(Level.Error,
                $"太长了（{value.Length} 字符，上限 {WhitelistMaxLength}）", value);

        // ── ① CIDR：192.168.1.0/24 ──
        int slash = value.IndexOf('/');
        if (slash > 0)
        {
            var head = value[..slash].Trim();
            var tail = value[(slash + 1)..].Trim();

            if (!IPAddress.TryParse(head, out var netAddr))
                return new Verdict(Level.Error, $"「{head}」不是合法地址", value);

            if (netAddr.AddressFamily != AddressFamily.InterNetwork)
                return new Verdict(Level.Error, "网段（/位数）写法只支持 IPv4；IPv6 请直接写完整地址", value);

            if (!int.TryParse(tail, out int bits) || bits is < 0 or > 32)
                return new Verdict(Level.Error, $"「/{tail}」不是 0-32 的位数", value);

            return new Verdict(Level.Ok, "", $"{netAddr}/{bits}");
        }

        // ── ② 通配前缀：192.168.1.* ──
        int star = value.IndexOf('*');
        if (star >= 0)
        {
            var prefix = value[..star];
            var after = value[(star + 1)..];

            if (prefix.Length == 0)
                return new Verdict(Level.Error, "通配符前面要有 IP 前缀，如 192.168.1.*", value);

            // ⚠️ IpWhitelist 是 StartsWith(prefix) 匹配，所以「前缀本身是不是 IP 前缀」
            //    决定了它到底能不能匹配到任何东西 —— 写成 abc* 就永远匹配不到，
            //    与「写好了一条规则」在界面上长得一样，属于必须拦下的静默失效。
            if (!LooksLikeIPv4Prefix(prefix))
                return new Verdict(Level.Error,
                    $"「{prefix}」不是 IP 前缀（应形如 192.168.1.）", value);

            if (after.Length > 0)
                return new Verdict(Level.Error, "通配符后面不能再有内容，如 192.168.1.*", value);

            // 没有以「.」结尾的前缀会连 192.168.10.x 一起放行 —— 常常不是本意，提示但不拦。
            // ⚠️ 这里**不改写** <see cref="Verdict.Normalized"/>（原样返回用户写的），
            //    只在提示里给出建议：把用户输入悄悄改成另一个规则，是比「匹配得比想的多」
            //    更糟的行为 —— 后者至少还能从提示里看出来，前者连痕迹都没有。
            if (!prefix.EndsWith('.'))
            {
                var safe = prefix + ".";
                return new Verdict(Level.Warn,
                    $"没以「.」结尾：会同时匹配 {safe}0.1 这类地址；建议改成 {safe}*", value);
            }

            return new Verdict(Level.Ok, "", value);
        }

        // ── ③ 精确地址 ──
        if (IPAddress.TryParse(value, out _)) return new Verdict(Level.Ok, "", value);

        return new Verdict(Level.Error,
            "不是合法地址：应为 IP（192.168.1.23）、通配（192.168.1.*）或网段（192.168.1.0/24）",
            value);
    }

    /// <summary>形如 <c>192.168.1.</c> / <c>10.</c> / <c>192.168.</c> 的 IPv4 前缀（每段 0-255 的数字）。</summary>
    private static bool LooksLikeIPv4Prefix(string prefix)
    {
        var body = prefix.EndsWith('.') ? prefix[..^1] : prefix;
        if (body.Length == 0) return false;

        int segments = 0;
        foreach (var part in body.Split('.'))
        {
            if (part.Length == 0) return false;
            if (!int.TryParse(part, out int n) || n is < 0 or > 255) return false;
            segments++;
        }
        return segments is >= 1 and <= 4;
    }

    // ====================================================================
    // 图标映射 —— 应用名
    // ====================================================================

    /// <summary>应用名输入框的用途说明。</summary>
    public const string IconKeyPurpose = "消息里的应用名（或包名）";

    /// <summary>应用名输入框的校验规则说明。</summary>
    public const string IconKeyRule = "与推送消息里的应用名一致即可；大小写、空格、「(3条新消息)」这类尾巴都会被自动忽略";

    /// <summary>应用名长度上限（界面输入上限与之一致）。</summary>
    public const int IconKeyMaxLength = 64;

    /// <summary>
    /// 校验图标映射的**应用名**。
    ///
    /// 归一化必须走 <see cref="IconCatalog.Normalize"/> —— 与运行时查表**同一套规则**。
    /// 若归一化结果与用户输入不同，要在提示里**明确写出来**：用户打的是「微信 」，
    /// 实际存的是「微信」，不说明的话他会以为「必须一模一样才生效」。
    /// </summary>
    public static Verdict CheckIconKey(string? text)
    {
        var value = (text ?? "").Trim();
        if (value.Length == 0) return new Verdict(Level.Ignored, "", "");

        if (value.Length > IconKeyMaxLength)
            return new Verdict(Level.Error,
                $"太长了（{value.Length} 字符，上限 {IconKeyMaxLength}）", value);

        var normalized = IconCatalog.Normalize(value);
        if (normalized.Length == 0)
            return new Verdict(Level.Error, "应用名里没有可用的字符", value);

        if (!string.Equals(normalized, value, StringComparison.Ordinal))
            return new Verdict(Level.Ok, $"将按「{normalized}」匹配", normalized);

        if (IconCatalog.LookupBuiltin(normalized).Length > 0)
            return new Verdict(Level.Ok, "该应用内置已有图标，这条会覆盖内置的", normalized);

        return new Verdict(Level.Ok, "", normalized);
    }

    // ====================================================================
    // 图标映射 —— 图标值
    // ====================================================================

    /// <summary>图标值输入框的用途说明。</summary>
    public const string IconValuePurpose = "要显示的图标";

    /// <summary>图标值输入框的校验规则说明（四种写法）。</summary>
    public const string IconValueRule = "四种写法：内置别名（如 qq）· 图片链接（http/https）· base64（data:image/…;base64,…）· 本地路径（用正斜杠 /）";

    /// <summary>图标值长度上限 —— 与界面输入上限一致（icon 可能是长链接或 base64）。</summary>
    public const int IconValueMaxLength = 400;

    private const string Base64Prefix = "data:";

    /// <summary>
    /// 校验图标映射的**图标值**。
    ///
    /// 四种写法的识别顺序与宿主 <c>Notify/ToastIconProvider.cs</c> 的前缀判定保持一致
    /// （别名 → 链接 → base64 → 本地路径），否则界面会给出与运行时相反的结论。
    /// </summary>
    public static Verdict CheckIconValue(string? text)
    {
        var value = (text ?? "").Trim();
        if (value.Length == 0) return new Verdict(Level.Ignored, "", "");

        if (value.Length > IconValueMaxLength)
            return new Verdict(Level.Error,
                $"太长了（{value.Length} 字符，上限 {IconValueMaxLength}）", value);

        // ── 反斜杠：宿主解析前会把 \ 当转义符处理，最终必然找不到文件 ──
        //   这是本项目踩过的真实坑，所以放在最前面、直接判定为错误（而不是「警告」）。
        if (value.Contains('\\'))
            return new Verdict(Level.Error,
                "本地路径必须用正斜杠 /（反斜杠会被宿主当转义符处理，导致找不到图片）", value);

        // ── 网络链接 ──
        if (value.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return new Verdict(Level.Ok, "网络图片（宿主限 3 秒超时、单张 4 MB）", value);

        // ── 内嵌 base64 ──
        if (value.StartsWith(Base64Prefix, StringComparison.OrdinalIgnoreCase))
        {
            if (!value.Contains("base64,", StringComparison.OrdinalIgnoreCase))
                return new Verdict(Level.Error, "base64 写法应为 data:image/png;base64,<内容>", value);
            return new Verdict(Level.Ok, "内嵌 base64 图片", value);
        }

        // ── 本地路径（含斜杠或盘符） ──
        if (value.Contains('/') || value.Contains(':'))
        {
            var trimmed = value.TrimStart();
            bool driveForm = value.Length >= 3 && char.IsLetter(trimmed[0]) && trimmed[1] == ':' && trimmed[2] == '/';
            if (driveForm || trimmed.StartsWith('/'))
                return new Verdict(Level.Ok, "本地图片文件", value);

            return new Verdict(Level.Warn,
                "看起来是路径但不完整；本机文件应写完整路径（如 C:/pics/qq.png）", value);
        }

        // ── 剩下的一律按「内置别名」处理（与宿主一致） ──
        if (IconCatalog.LookupAlias(value).Length > 0)
            return new Verdict(Level.Ok, $"内置别名（{value}）", value);

        // 未收录的别名不会报错，但宿主找不到对应图片时会**回退预设图标** ——
        // 看着像「配了却没生效」，所以这里提醒，同时仍然允许保存。
        return new Verdict(Level.Warn,
            "未收录的别名：找不到对应图片时会回退成预设图标（可改用图片链接或本地路径）", value);
    }

    // ====================================================================
    // 图标映射 —— 整条（界面上的「应用名框 + 图标值框」合成一行）
    // ====================================================================

    /// <summary>
    /// 把一行编辑文本拆成「应用名」与「图标值」两半。
    ///
    /// 以**第一个** <c>=</c> 为界，与 <see cref="IconOverrides.Parse"/> 和界面上的两个输入框
    /// **完全同一套规则** —— 三处只要有一处不同，「界面显示的和实际存下来的」就会对不上。
    /// 没有 <c>=</c> 时整行都算应用名（用户可能正打到一半）。
    /// </summary>
    public static (string Key, string Value) SplitIconRow(string? line)
    {
        var text = (line ?? "").Trim();
        if (text.Length == 0) return ("", "");

        int eq = text.IndexOf('=');
        if (eq < 0) return (text, "");

        return (text[..eq].Trim(), text[(eq + 1)..].Trim());
    }

    /// <summary>
    /// 把「应用名」与「图标值」合成一行持久化文本（<see cref="SplitIconRow"/> 的逆运算）。
    ///
    /// 值还没填时**不补等号** —— 否则用户刚打完应用名就会被存成 <c>微信=</c>，
    /// 而 <see cref="IconOverrides.Parse"/> 会把它当成「键或值为空」直接丢弃，等于白写。
    /// </summary>
    public static string ComposeIconRow(string? key, string? value)
    {
        var k = (key ?? "").Trim();
        var v = (value ?? "").Trim();

        if (k.Length == 0) return v.Length == 0 ? "" : "=" + v;   // 理论不可达；保留内容而不是静默丢
        return v.Length == 0 ? k : $"{k}={v}";
    }

    /// <summary>
    /// 校验**一整条**映射（两个框合起来看）。
    ///
    /// 两框都空 → <see cref="Level.Ignored"/>（跳过这一条，不算错）；
    /// 只填了一半 → <see cref="Level.Error"/>，并**指明缺的是哪一半** ——
    /// 这正是「只填了应用名」这种情况以前会被静默丢弃的地方。
    /// 两框都有值 → 取更严重的那个结论，说明文字优先给图标值的（更可操作）。
    /// </summary>
    public static Verdict CheckIconRow(string? line)
    {
        var (key, value) = SplitIconRow(line);

        // ★ Normalized 必须是**整行**（Key=Value），而不是某一半 ★
        //    它是「实际会被保存的那一行」的契约。早先这里直接返回了某一半的结论，
        //    于是调用方拿 `CheckIconRow(...).Normalized` 拼持久化文本时把**应用名整批丢掉**，
        //    存出来只剩一串图标值 —— 由 InputRulesTests 的「保存后是否真的生效」当场逮到。
        var row = ComposeIconRow(key, value);

        if (key.Length == 0 && value.Length == 0) return Verdict.None;

        // 注释行（持久化文本里允许出现，见 IconOverrides.Parse）：原样保留，不参与校验
        if (key.StartsWith('#')) return new Verdict(Level.Ignored, "", line ?? "");

        if (key.Length == 0)
            return new Verdict(Level.Error, "缺少应用名（等号左边）", row);

        var keyVerdict = CheckIconKey(key);
        if (keyVerdict.Level == Level.Error) return keyVerdict with { Normalized = row };

        if (value.Length == 0)
            return new Verdict(Level.Error,
                $"「{keyVerdict.Normalized}」还缺图标值（等号右边）", row);

        var valueVerdict = CheckIconValue(value);
        if (valueVerdict.Level == Level.Error) return valueVerdict with { Normalized = row };

        // 优先显示图标值的说明（如「网络图片…」「未收录的别名…」）；
        // 它没话说时，用应用名的说明（如「将按「微信」匹配」），最后才是空结论。
        // ⚠️ 无论取哪一边的**说明**，Normalized 都换成整行。
        if (valueVerdict.HasMessage) return valueVerdict with { Normalized = row };
        if (keyVerdict.HasMessage) return keyVerdict with { Normalized = row };
        return valueVerdict with { Normalized = row };
    }

    // ====================================================================
    // 应用黑名单（每条一个输入框）
    // ====================================================================

    /// <summary>黑名单输入框的用途说明。</summary>
    public const string BlacklistPurpose = "不推送上岛的应用";

    /// <summary>黑名单输入框的校验规则说明（短版，放在提示行里）。</summary>
    public const string BlacklistRule =
        "每行一个应用名或包名；末尾加 * 表示前缀匹配（如 com.tencent.* 拦下一个公司的全部应用）";

    /// <summary>黑名单单条的最长长度 —— 与界面输入上限一致。</summary>
    public const int BlacklistMaxLength = 64;

    /// <summary>
    /// 校验黑名单里的**一条**。
    ///
    /// 归一化必须走 <see cref="IconCatalog.Normalize"/> —— 与运行时 <see cref="AppBlacklist.Parse"/>
    /// **同一套规则**（图标映射也用它）。否则会出现「界面说匹配、运行时匹配不到」的最坏情况。
    /// 归一化结果与用户输入不同时要**明确写出来**：用户打的是「微信 」，实际按「微信」匹配，
    /// 不说明的话他会以为「必须一模一样才生效」。
    /// </summary>
    public static Verdict CheckBlacklistEntry(string? text)
    {
        var value = (text ?? "").Trim();
        if (value.Length == 0) return new Verdict(Level.Ignored, "", "");
        if (value[0] == '#') return new Verdict(Level.Ignored, "", "");

        if (value.Length > BlacklistMaxLength)
            return new Verdict(Level.Error,
                $"太长了（{value.Length} 字符，上限 {BlacklistMaxLength}）", value);

        int star = value.IndexOf('*');
        if (star >= 0)
        {
            // 只支持**末尾**通配。中间带 * 的写法（com.*.mm）若按「取 * 之前当前缀」处理，
            // 会拦下比用户预期多得多的应用，而这种规则在界面上与正常规则长得一样 ——
            // 属于必须拦下的静默误伤，因此判为错误而不是警告。
            if (star != value.Length - 1 || value.IndexOf('*', star + 1) >= 0)
                return new Verdict(Level.Error, "通配符只能写在末尾，如 com.tencent.*", value);

            var prefix = IconCatalog.Normalize(value[..star]);
            if (prefix.Length == 0)
                return new Verdict(Level.Error, "通配符前面要有内容，如 com.tencent.*", value);

            return new Verdict(Level.Ok, $"将拦截所有以「{prefix}」开头的应用名 / 包名", prefix + "*");
        }

        var normalized = IconCatalog.Normalize(value);
        if (normalized.Length == 0)
            return new Verdict(Level.Error, "这里没有可用的字符", value);

        if (!string.Equals(normalized, value, StringComparison.Ordinal))
            return new Verdict(Level.Ok, $"将按「{normalized}」匹配", normalized);

        return new Verdict(Level.Ok, "", normalized);
    }
}
