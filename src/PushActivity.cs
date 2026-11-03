using System.Text;
using System.Text.Json;

namespace NpsPushBridge;

/// <summary>
/// 一条待推送到灵动岛的消息（对应宿主 HTTP 接口的 kind / title / subtitle 三个字段，
/// 外加 1.8.0 新增的可选 icon 字段）。
///
/// ★ <paramref name="Icon"/> 的语义：**最终要发出去的那个值**，不是「入站原样」★
/// 入站可能根本没带 icon，由插件按 kind 二次补全后才写进这个字段（见 <see cref="IconCatalog"/>）。
/// 保持默认值 <c>""</c>，因此所有既有构造点无需改动即可继续工作，
/// 且 <c>""</c> 会让转发时**不输出 icon 字段** —— 与升级前的请求体逐字节一致。
///
/// ★ <paramref name="Package"/> 的语义：发送端的**原始应用标识**（包名）★
/// <see cref="Kind"/> 是经 <c>FriendlyAppName</c> 翻译后的**显示名**（如「微信」），
/// 翻译之后就再也还原不回 <c>com.tencent.mm</c> 了。应用黑名单要求「包名与应用名都能匹配」，
/// 因此原始值必须单独留一份：它只在插件内部用于匹配，**不会**出现在转发给宿主的 JSON 里。
/// 取不到时为空串（此时黑名单只能按 <see cref="Kind"/> 匹配）。
/// </summary>
internal readonly record struct PushActivity(
    string Kind, string Title, string Subtitle, string Icon = "", string Package = "")
{
    public const string DefaultKind = "手机消息";

    /// <summary>日志用的一行摘要。</summary>
    public string Describe()
    {
        var title = OneLine(Title.Length > 0 ? Title : "(无标题)");
        var body = OneLine(Subtitle.Length > 0 ? Subtitle : "(无正文)");
        return $"[{Kind}] {title} — {body}";
    }

    /// <summary>
    /// 带图标补充说明的日志摘要。
    /// 无图标时**退化成 <see cref="Describe"/> 的原样输出**，保证既有日志格式不变。
    /// </summary>
    public string DescribeWithIcon()
    {
        var line = Describe();
        return Icon.Length > 0 ? $"{line} · 图标: {OneLine(Icon)}" : line;
    }

    /// <summary>
    /// 把正文里的换行 / 制表压成空格：日志是逐行显示的，多行正文若原样写进日志，
    /// 一条消息会被拆成好几行，与「一条日志 = 一行」的既有格式不符。
    /// （这里只压空白，不像 <c>Sanitize</c> 那样改写反斜杠 —— 日志要如实反映收到的内容。）
    /// </summary>
    private static string OneLine(string text)
        => text.IndexOfAny(new[] { '\r', '\n', '\t' }) < 0
            ? text
            : text.Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ');
}

/// <summary>
/// 宿主本地推送接口的客户端与请求解析器。
///
/// 支持两种请求体，自动嗅探：
///
/// ① 表单格式（当前手机端实际使用的格式）：
///       from=&lt;应用包名&gt;&amp;content=&lt;正文&gt;
///   其中 content 常被 URL 编码，例如 %7B%22… 其实就是 {"…。
///   本插件只保留 content= 后面的内容：先做百分号解码（含二次编码），
///   若解码结果本身是一段 JSON，再从中抽出 title / text 等可读字段，
///   因此界面上不会再出现 %7B%22 或 {"…"} 这类乱码/原始串。
///
/// ② JSON 格式（兼容旧写法）：{"kind":…,"title":…,"subtitle":…}
///   宿主 1.8.0 起还多认一个可选字段 icon（见 <see cref="IconCatalog"/>），本插件照实读出并透传。
///
/// ★ 两种格式共用一套「严格 → 修复 → 嗅探」的三级解析 ★
/// 手机端模板是 {"title":"{{TITLE}}","subtitle":"{{MSG}}","kind":"{{APP_NAME}}"}，
/// 而 {{MSG}} 是**通知正文原文**，可能含换行（多行消息 / 空行）、双引号、反斜杠：
///   · 换行会以裸 \n 直接出现在 JSON 字符串内部 —— 严格按 RFC 8259 这是**非法**的
///     （字符串里只允许 \n 这类转义，不允许字面控制字符），JsonDocument.Parse 直接抛异常；
///   · 正文里的裸双引号会被当成字符串结束符，反斜杠（Windows 路径）会被当成非法转义。
/// 旧版遇到这些情况要么返回 400（消息丢失），要么把整段原始 JSON 当作正文显示出来。
/// 现在的三级策略见 <see cref="ParseJsonLenient"/>：严格解析失败 → 修复字符串内部的
/// 裸控制字符/非法反斜杠/未转义引号再解析 → 仍不行则按 "key":"value" 嗅探字段，
/// 保证「永远不把 {\"title\":…} 这种原始 JSON 显示到灵动岛上」。
///
/// ★ 编码约束（必须遵守，否则内容会被宿主改坏）★
/// 宿主 toast.cs 在解析前会对「原始 JSON 文本」做一次替换：
///     rawJson.Replace("\\", "\\\\").Replace("\\\\\"", "\\\"");
/// 该变换只对 \"（转义引号）是无损的：
///   · 内容里的反斜杠（JSON 写作 \\）会被改写成孤立反斜杠，破坏 JSON 结构；
///   · \n / \t / \uXXXX 等其他转义会被改写成字面量文本。
/// 因此转发前统一净化：反斜杠 → "/"、换行（含空行）折叠成一个空格、制表 → 空格、控制字符丢弃；
/// 中文按原始 UTF-8 输出。
/// </summary>
internal static class NpsActivityEndpoint
{
    /// <summary>宿主在 toast.cs 中监听并注册的完整前缀，必须一字不差（含结尾斜杠）。</summary>
    public const string Url = "http://127.0.0.1:47300/api/activities/";

    /// <summary>把请求体解析成一条消息。字段缺失时回落到默认值（kind=手机消息、title/subtitle 为空）。</summary>
    /// <remarks>
    /// ★ <c>Icon</c> 的填法：这里只做「**照实读出**入站给的值」，**不做**补全 ★
    /// 补全需要读到用户配置（自定义映射），属插件的职责，发生在转发前一步（见
    /// <c>HttpBridgeServer</c> 的调用点）。解析层保持「纯函数、无外部状态」，
    /// 既好测，也不会因为配置读取失败而影响「消息本身能不能收到」。
    /// </remarks>
    public static bool TryParse(byte[] body, out PushActivity activity, out string error)
    {
        activity = new PushActivity(PushActivity.DefaultKind, "", "");
        error = "";

        if (body.Length == 0) { error = "请求体为空"; return false; }

        string text;
        try
        {
            text = new UTF8Encoding(false, true).GetString(body).Trim();
        }
        catch (DecoderFallbackException)
        {
            error = "请求体不是合法的 UTF-8 文本";
            return false;
        }

        if (text.Length == 0) { error = "请求体为空"; return false; }

        // ---------- ① 表单格式：from=<应用包名>&content=<正文> ----------
        if (IsFormBody(text))
        {
            var form = ParseForm(text);

            // ⚠️ from 的**原始值**必须单独留一份：它是包名（机器可读标识），
            //    而 FriendlyAppName 会把它翻成中文显示名，翻完就还原不回去了。
            //    应用黑名单要求「包名与应用名都能匹配」，靠的就是这个原值。
            string fromRawText = form.TryGetValue("from", out var fromRaw) ? DecodeDeep(fromRaw).Trim() : "";
            string from = fromRawText.Length > 0 ? FriendlyAppName(fromRawText) : "";
            string explicitTitle = form.TryGetValue("title", out var t) ? DecodeDeep(t) : "";
            string content = form.TryGetValue("content", out var c) ? DecodeDeep(c)
                           : form.TryGetValue("text", out var tx) ? DecodeDeep(tx) : "";
            // 表单形态也可能带 icon（如 &icon=qq）；手机端目前不发，读出来是为了以后能透传
            string formIcon = form.TryGetValue("icon", out var fi) ? DecodeDeep(fi) : "";

            var (parsedKind, parsedTitle, parsedBody, parsedPackage) = ParseContentPayload(content);

            // ★ kind 优先级：content JSON 里的 kind/app（发送端显式指定的**显示名**，如「快手」）
            //   > 表单 from（**包名**，如 com.smile.gifmaker）> 默认值。
            //
            // ⚠️ 这个顺序不能反：早期版本把 from 排在前面，于是「from=包名 & content 里带中文
            //   kind」的组合下，中文名**永远被包名盖掉** —— 用户手机模板明明传了「快手」，
            //   灵动岛上却显示 com.smile.gifmaker（且映射表里查不到 → 连图标都没有）。
            //   语义上 from 是「谁发的」（机器可读标识），content 里的 kind 才是「要显示成什么」
            //   （人可读名称）；显式指定高于推断，所以 content 优先。
            //   仅当 content 没给 kind 时，才回退到 from 包名（再经 FriendlyAppName 翻译成中文）。
            string kind = parsedKind.Length > 0 ? FriendlyAppName(parsedKind)
                        : from.Length > 0 ? from
                        : PushActivity.DefaultKind;

            // 包名：content JSON 里显式给了 package 就用它，否则退回表单 from 的原始值
            // （手机端的模板正是把包名放在 from 里）。
            string package = parsedPackage.Length > 0 ? parsedPackage : fromRawText;

            activity = new PushActivity(
                kind,
                explicitTitle.Length > 0 ? explicitTitle : parsedTitle,
                parsedBody,
                formIcon,
                package);
            return true;
        }

        // ---------- ② 兼容 JSON 格式（严格 → 修复 → 嗅探） ----------
        // reason 必须声明在 using 之外：using 会引入自己的作用域，out var 出了括号就不可见
        string reason;
        using (var doc = ParseJsonLenient(text, out reason))
        {
            if (doc != null)
            {
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                {
                    error = "JSON 请求体必须是对象";
                    return false;
                }

                var kind = ReadString(root, "kind", "app", "appName", "from");
                var title = ReadString(root, "title", "subject");
                var subtitle = ReadString(root, "subtitle", "body", "content", "text", "message", "msg");
                // iconUrl 是社区常见写法，宿主也兼容它，这里一并读
                var icon = ReadString(root, "icon", "iconUrl");
                // 包名（机器可读标识）：显式的 package 字段优先，退回 from
                var package = ReadString(root, "package", "packageName", "from");

                activity = new PushActivity(
                    kind.Length > 0 ? FriendlyAppName(kind) : PushActivity.DefaultKind,
                    title,
                    subtitle,
                    icon,
                    package);
                return true;
            }
        }

        // 修复后仍不成形：按字段嗅探兜底 —— 只要认出任意一个已知字段就当作消息收下，
        // 避免「正文里有个没转义的引号」就把整条消息判为非法而丢弃。
        if (text[0] == '{')
        {
            var fields = SniffFields(text);
            var kind = PickField(fields, "kind", "app", "appname", "from");
            var title = PickField(fields, "title", "subject", "name");
            var subtitle = PickField(fields, "subtitle", "text", "body", "content", "message", "msg", "desc");
            var icon = PickField(fields, "icon", "iconurl");
            var package = PickField(fields, "package", "packagename", "from");

            if (kind.Length > 0 || title.Length > 0 || subtitle.Length > 0)
            {
                activity = new PushActivity(
                    kind.Length > 0 ? FriendlyAppName(kind) : PushActivity.DefaultKind,
                    title,
                    subtitle,
                    icon,
                    package);
                return true;
            }
        }

        error = $"请求体既不是表单（from=…&content=…）也不是合法 JSON：{reason}";
        return false;
    }

    /// <summary>
    /// 生成宿主能安全解析的 JSON。
    ///
    /// ★ 兼容性：<c>Icon</c> 为空时**不输出 icon 字段** ★
    /// 于是「没有图标可补」的消息，其请求体与升级前**逐字节一致**。
    /// 实现委托给 <see cref="IconCatalog.BuildJson"/>，保证与补全逻辑共用同一套转义规则。
    /// </summary>
    public static string BuildJson(PushActivity a)
        => IconCatalog.BuildJson(Sanitize(a.Kind), Sanitize(a.Title), Sanitize(a.Subtitle), SanitizeIcon(a.Icon));

    /// <summary>
    /// icon 值的净化。
    ///
    /// ⚠️ **不能直接套用 <see cref="Sanitize"/>**：那个函数会把反斜杠改写成 "/"，
    /// 对文本是对的（宿主解析器会被反斜杠搞坏），但对 base64 是**破坏性的** ——
    /// base64 字母表含 <c>/</c> 与 <c>+</c>，而改写只影响反斜杠，看似无害；
    /// 真正的问题是 <see cref="Sanitize"/> 还会丢弃控制字符并折叠换行，
    /// 而 base64 串在传输中**经常带换行**（MIME 折行），换行是合法的、可被 <c>Convert.FromBase64String</c> 接受。
    /// 所以这里只做两件事：去首尾空白、把 CRLF 折叠掉（避免破坏 JSON），其余原样保留。
    /// </summary>
    private static string SanitizeIcon(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";

        var trimmed = s.Trim();
        if (trimmed.IndexOfAny(new[] { '\r', '\n', '\t' }) < 0) return trimmed;

        var sb = new StringBuilder(trimmed.Length);
        foreach (char ch in trimmed)
            if (ch is not ('\r' or '\n' or '\t')) sb.Append(ch);
        return sb.ToString();
    }

    /// <summary>净化：保证内容里不出现反斜杠与需转义字符，同时保持 UTF-8 合法。</summary>
    public static string Sanitize(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";

        var sb = new StringBuilder(s.Length + 8);
        for (int i = 0; i < s.Length; i++)
        {
            char ch = s[i];

            if (ch == '\\') { sb.Append('/'); continue; }                    // 反斜杠会破坏宿主解析器，改写为 /

            // 换行/制表 → 空格（否则会显示成字面量 \n）。
            // 连续换行（多行正文、含空行）折叠成**一个**空格：灵动岛提示只有一行，
            // 若按字符逐个替换，一个 CRLF 会变成两个空格、空行会变成四个，显示很难看。
            if (ch is '\r' or '\n')
            {
                while (i + 1 < s.Length && (s[i + 1] == '\r' || s[i + 1] == '\n')) i++;
                sb.Append(' ');
                continue;
            }
            if (ch == '\t') { sb.Append(' '); continue; }
            if (ch < ' ') continue;                                          // 其余控制字符直接丢弃

            if (char.IsHighSurrogate(ch))
            {
                if (i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
                {
                    sb.Append(ch).Append(s[i + 1]);                          // 合法代理对（如 emoji）整体保留
                    i++;
                }
                else sb.Append('?');
                continue;
            }
            if (char.IsLowSurrogate(ch)) { sb.Append('?'); continue; }

            sb.Append(ch);
        }
        return sb.ToString();
    }

    // ====================================================================
    // 表单解析
    // ====================================================================

    /// <summary>粗判：不以 { 开头且含 "=" 即视为表单体。</summary>
    private static bool IsFormBody(string text)
        => text[0] != '{' && text.IndexOf('=') > 0;

    private static Dictionary<string, string> ParseForm(string text)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in text.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = pair.IndexOf('=');
            if (eq <= 0) continue;
            var key = pair[..eq].Trim().ToLowerInvariant();
            if (key.Length == 0) continue;
            result[key] = pair[(eq + 1)..];   // 同名键：后值覆盖前值
        }
        return result;
    }

    /// <summary>
    /// 反复做百分号解码，直到看起来不再被编码（上限 3 轮）。
    /// 注意：不只处理 %XX —— 表单里 "+" 也代表空格，因此只要出现 "+" 或 "%XX" 就要解一次。
    /// （曾踩过：只在含 %XX 时才解码，导致 "a+b" 这种正文把加号原样显示出来。）
    /// </summary>
    private static string DecodeDeep(string value)
    {
        var result = value;
        for (int round = 0; round < 3; round++)
        {
            bool encoded = LooksPercentEncoded(result) || result.IndexOf('+') >= 0;
            if (!encoded) break;
            result = UrlDecode(result);
        }
        return result;
    }

    private static bool LooksPercentEncoded(string s)
    {
        for (int i = 0; i + 2 < s.Length; i++)
            if (s[i] == '%' && Uri.IsHexDigit(s[i + 1]) && Uri.IsHexDigit(s[i + 2])) return true;
        return false;
    }

    /// <summary>表单风格的百分号解码（+ 视为空格）。解码失败时原样返回，绝不抛异常。</summary>
    private static string UrlDecode(string s)
    {
        var replaced = s.IndexOf('+') >= 0 ? s.Replace('+', ' ') : s;
        try { return Uri.UnescapeDataString(replaced); }
        catch { return replaced; }
    }

    /// <summary>
    /// 解析 content= 后面的内容：若是一段 JSON，抽出 kind / 标题 / 正文；否则整段当作正文。
    ///
    /// ★ 这里是「手机模板 + 多行正文」出问题的主战场 ★
    /// 手机把 {"title": "{{TITLE}}", "subtitle": "{{MSG}}", "kind": "{{APP_NAME}}"} 整个当作 content 发过来，
    /// {{MSG}} 里的换行（多行消息、空行）在被百分号解码成真实换行后，直接落进 JSON 字符串内部 →
    /// 严格解析失败。旧版此时走「解析不了就整段当正文」的兜底，于是灵动岛上显示了
    /// <c>{"title": "标题", "subtitle": "第一行…</c> 这一整串原始 JSON。
    /// 现在同样走三级解析；只有连字段嗅探都认不出东西时，才退回「整段当正文」。
    /// 返回值是 JSON 里的 kind / title / 正文 / 包名四项
    /// （kind 供表单没有 from= 时使用；package 供应用黑名单按包名匹配）。
    /// </summary>
    private static (string Kind, string Title, string Body, string Package) ParseContentPayload(string content)
    {
        if (string.IsNullOrEmpty(content)) return ("", "", "", "");

        var trimmed = content.Trim();
        if (trimmed.Length == 0) return ("", "", "", "");
        if (trimmed[0] != '{' && trimmed[0] != '[') return ("", "", trimmed, "");

        using (var doc = ParseJsonLenient(trimmed, out _))
        {
            if (doc != null)
            {
                var root = doc.RootElement;

                if (root.ValueKind == JsonValueKind.Object)
                {
                    string kind = ReadString(root, "kind", "app", "appName", "from");
                    string title = ReadString(root, "title", "subject", "name");
                    string body = ReadString(root, "text", "body", "subtitle", "content", "message", "msg", "desc");
                    string package = ReadString(root, "package", "packageName", "from");
                    if (body.Length == 0) { body = title; title = ""; }   // 只有一个字段时整体当正文
                    if (body.Length == 0 && title.Length == 0) body = trimmed;
                    return (kind, title, body, package);
                }

                if (root.ValueKind == JsonValueKind.String) return ("", "", root.GetString() ?? "", "");
            }
        }

        // 兜底：结构坏到无法解析，也按 "key":"value" 嗅探字段，绝不把原始 JSON 显示出去
        var fields = SniffFields(trimmed);
        string sniffedKind = PickField(fields, "kind", "app", "appname", "from");
        string sniffedTitle = PickField(fields, "title", "subject", "name");
        string sniffedBody = PickField(fields, "subtitle", "text", "body", "content", "message", "msg", "desc");
        string sniffedPackage = PickField(fields, "package", "packagename", "from");
        if (sniffedBody.Length == 0) { sniffedBody = sniffedTitle; sniffedTitle = ""; }
        if (sniffedKind.Length > 0 || sniffedTitle.Length > 0 || sniffedBody.Length > 0)
            return (sniffedKind, sniffedTitle, sniffedBody, sniffedPackage);

        return ("", "", trimmed, "");
    }

    // ====================================================================
    // 宽松 JSON：严格 → 修复 → 嗅探
    // 起因：手机模板把通知正文直接塞进 JSON 字符串，正文里的换行（多行消息、空行）、
    //       双引号、反斜杠都会让 JSON 变成非法文本，旧版因此丢掉消息或显示原始 JSON。
    // ====================================================================

    /// <summary>
    /// 三级解析：
    ///   1) 严格解析（RFC 8259），能过就说明原文没问题；
    ///   2) 按 <see cref="RepairJson"/> 修复后再解析；
    ///   3) 返回 null，交给调用方走 <see cref="SniffFields"/> 字段嗅探。
    /// <paramref name="reason"/> 带回严格解析的失败原因，便于写进日志。调用方负责 Dispose 返回值。
    /// </summary>
    private static JsonDocument? ParseJsonLenient(string text, out string reason)
    {
        reason = "";
        try
        {
            return JsonDocument.Parse(text);
        }
        catch (JsonException ex)
        {
            reason = ex.Message;
        }

        var repaired = RepairJson(text);
        if (repaired == null) return null;

        try
        {
            return JsonDocument.Parse(repaired);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// 修复「拼接 JSON 时没有转义正文」造成的语法损坏。**只动字符串字面量内部，字符串外一字不改**：
    ///   · 裸控制字符（换行 \n、回车 \r、制表 \t 等）→ 写成 \n / \r / \t / \uXXXX
    ///     —— 多行正文（含空行）就是靠这一步救回来的；
    ///   · 不构成合法转义的反斜杠（如 C:\Users 中的 \U）→ 写成 \\；
    ///   · 字符串内部未转义的双引号 → 写成 \"。
    /// 返回 null 表示无需修复（原文本来就合法，或这一轮找不到可修之处）。
    /// </summary>
    private static string? RepairJson(string text)
    {
        var sb = new StringBuilder(text.Length + 16);
        bool inString = false;
        bool changed = false;

        for (int i = 0; i < text.Length; i++)
        {
            char ch = text[i];

            if (!inString)
            {
                sb.Append(ch);
                if (ch == '"') inString = true;
                continue;
            }

            if (ch == '\\')
            {
                int consumed = TryCopyEscape(text, i, sb);
                if (consumed > 0) { i += consumed - 1; continue; }

                sb.Append("\\\\");          // 孤立反斜杠（如 Windows 路径）→ 补成合法转义
                changed = true;
                continue;
            }

            if (ch == '"')
            {
                if (IsStringEnd(text, i)) { sb.Append('"'); inString = false; }
                else { sb.Append("\\\""); changed = true; }   // 正文里的裸引号 → 转义
                continue;
            }

            if (ch < ' ')
            {
                sb.Append(EscapeControl(ch));   // 裸换行 / 回车 / 制表 → 标准转义
                changed = true;
                continue;
            }

            sb.Append(ch);
        }

        return changed ? sb.ToString() : null;
    }

    /// <summary>
    /// text[quoteIndex] 是字符串内部遇到的 <c>"</c>：判断它是**合法的字符串结束符**还是正文里的裸引号。
    /// 判据是「这个引号之后（跳过空白）紧跟的字符」：是 <c>, } ] :</c> 或已到末尾 → 合法结尾；
    /// 否则说明它是正文内容（例如 <c>"他说"你好""</c> 中间那个引号），必须转义。
    /// </summary>
    private static bool IsStringEnd(string text, int quoteIndex)
    {
        for (int i = quoteIndex + 1; i < text.Length; i++)
        {
            char ch = text[i];
            if (char.IsWhiteSpace(ch)) continue;
            return ch is ',' or '}' or ']' or ':';
        }
        return true;   // 已到末尾 → 视为字符串结尾
    }

    /// <summary>
    /// text[i] 是反斜杠：若它与其后字符构成合法 JSON 转义则原样抄入 <paramref name="sb"/> 并返回占用长度，
    /// 否则返回 0（表示这是个孤立反斜杠，需要补一个反斜杠）。
    /// </summary>
    private static int TryCopyEscape(string text, int i, StringBuilder sb)
    {
        if (i + 1 >= text.Length) return 0;
        char next = text[i + 1];

        if (next is '"' or '\\' or '/' or 'b' or 'f' or 'n' or 'r' or 't')
        {
            sb.Append(text, i, 2);
            return 2;
        }

        if (next == 'u' && i + 5 < text.Length && IsHex4(text, i + 2))
        {
            sb.Append(text, i, 6);
            return 6;
        }

        return 0;
    }

    /// <summary>text[start..start+4) 是否为 4 位十六进制（\uXXXX 的合法性判据）。</summary>
    private static bool IsHex4(string text, int start)
    {
        for (int i = start; i < start + 4; i++)
            if (!Uri.IsHexDigit(text[i])) return false;
        return true;
    }

    /// <summary>控制字符的标准 JSON 转义写法。</summary>
    private static string EscapeControl(char ch) => ch switch
    {
        '\n' => "\\n",
        '\r' => "\\r",
        '\t' => "\\t",
        '\b' => "\\b",
        '\f' => "\\f",
        _ => "\\u" + ((int)ch).ToString("x4"),
    };

    /// <summary>
    /// 最后兜底：把文本扫成一串「字符串令牌」，识别 <c>"键" : "值"</c> 结构，收集键值映射
    /// （只收字符串，同名键取第一次出现）。
    /// 它不关心 JSON 语法是否完整（少个括号、多个逗号都无所谓），因此一定能从
    /// <c>{"title":"标题","subtitle":"正文</c> 这种残缺文本里把字段捞出来 ——
    /// 目的就是「任何情况下都不把原始 JSON 显示到灵动岛」。
    /// </summary>
    private static Dictionary<string, string> SniffFields(string text)
    {
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var tokens = new List<(string Value, bool IsKey)>();
        ScanStringTokens(RepairJson(text) ?? text, tokens);

        for (int i = 0; i + 1 < tokens.Count; i++)
        {
            if (!tokens[i].IsKey || tokens[i + 1].IsKey) continue;   // 后面紧跟另一个键 → 这个键没有值
            var key = tokens[i].Value.Trim();
            if (key.Length == 0) continue;
            if (!fields.ContainsKey(key)) fields[key] = tokens[i + 1].Value;
        }

        return fields;
    }

    /// <summary>
    /// 扫描文本里所有「引号字符串」，并记录每个字符串后面是否紧跟冒号（即它是不是一个键）。
    /// 输入假定已过 <see cref="RepairJson"/>（转义已合法）；未闭合的残片直接丢弃。
    /// </summary>
    private static void ScanStringTokens(string text, List<(string Value, bool IsKey)> tokens)
    {
        var sb = new StringBuilder(64);

        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] != '"') continue;

            sb.Clear();
            int j = i + 1;
            bool closed = false;

            while (j < text.Length)
            {
                char ch = text[j];

                if (ch == '\\' && j + 1 < text.Length)
                {
                    char next = text[j + 1];
                    switch (next)
                    {
                        case '"': sb.Append('"'); j += 2; continue;
                        case '\\': sb.Append('\\'); j += 2; continue;
                        case '/': sb.Append('/'); j += 2; continue;
                        case 'b': sb.Append('\b'); j += 2; continue;
                        case 'f': sb.Append('\f'); j += 2; continue;
                        case 'n': sb.Append('\n'); j += 2; continue;
                        case 'r': sb.Append('\r'); j += 2; continue;
                        case 't': sb.Append('\t'); j += 2; continue;
                        case 'u' when j + 5 < text.Length && IsHex4(text, j + 2):
                            sb.Append((char)Convert.ToInt32(text.Substring(j + 2, 4), 16));
                            j += 6;
                            continue;
                    }
                    sb.Append(next);   // 未知转义：保留其字面字符
                    j += 2;
                    continue;
                }

                if (ch == '"') { closed = true; break; }
                sb.Append(ch);
                j++;
            }

            if (!closed) break;        // 未闭合 → 后面的内容不可信，就此收手
            tokens.Add((sb.ToString(), NextNonSpaceIsColon(text, j + 1)));
            i = j;
        }
    }

    /// <summary>从 start 起跳过空白后，下一个字符是不是冒号（用于判定刚读到的字符串是键还是值）。</summary>
    private static bool NextNonSpaceIsColon(string text, int start)
    {
        for (int i = start; i < text.Length; i++)
        {
            char ch = text[i];
            if (char.IsWhiteSpace(ch)) continue;
            return ch == ':';
        }
        return false;
    }

    /// <summary>按优先级从嗅探结果取值：名字没命中或取到空串就试下一个。</summary>
    private static string PickField(Dictionary<string, string> fields, params string[] names)
    {
        foreach (var name in names)
            if (fields.TryGetValue(name, out var value) && value.Length > 0) return value;
        return "";
    }

    /// <summary>把常见应用包名换成中文名；未知包名原样保留，便于辨认来源。</summary>
    private static string FriendlyAppName(string package)
    {
        var name = package.Trim();
        return name.ToLowerInvariant() switch
        {
            "com.tencent.mm" => "微信",
            "com.tencent.wework" => "企业微信",
            "com.tencent.mobileqq" or "com.tencent.qqlite" => "QQ",
            "com.tencent.tim" => "TIM",
            "com.android.mms" or "com.google.android.apps.messaging" => "短信",
            "com.android.dialer" or "com.android.phone" => "电话",
            "com.alibaba.android.rimet" => "钉钉",
            "com.eg.android.alipaygphone" => "支付宝",
            "com.taobao.taobao" => "淘宝",
            "com.ss.android.ugc.aweme" => "抖音",
            "com.sina.weibo" => "微博",
            "com.xingin.xhs" => "小红书",
            "com.netease.cloudmusic" => "网易云音乐",
            "tv.danmaku.bili" => "哔哩哔哩",
            "com.microsoft.office.outlook" => "Outlook",
            _ => name,
        };
    }

    // ====================================================================
    // 工具
    // ====================================================================

    /// <summary>只转义双引号，非 ASCII 字符按原样输出（保证不会被写成 \uXXXX）。</summary>
    private static void AppendJsonString(StringBuilder sb, string value)
    {
        sb.Append('"');
        foreach (char ch in value)
        {
            if (ch == '"') sb.Append('\\').Append('"');
            else sb.Append(ch);
        }
        sb.Append('"');
    }

    private static string ReadString(JsonElement root, params string[] names)
    {
        foreach (var name in names)
        {
            if (!root.TryGetProperty(name, out var el)) continue;
            switch (el.ValueKind)
            {
                case JsonValueKind.String: return el.GetString() ?? "";
                case JsonValueKind.Number: return el.ToString();
                case JsonValueKind.True:
                case JsonValueKind.False: return el.GetBoolean() ? "true" : "false";
            }
        }
        return "";
    }
}
