using System.Text.RegularExpressions;

namespace NpsPushBridge;

/// <summary>
/// 从推送消息里识别并提取**验证码**。
///
/// ★ 只做「有把握的提取」，宁可返回空，也绝不给出一个错的数字 ★
/// 复制错的验证码比不复制更糟：用户会拿它去登录、失败一次、再回来检查，
/// 而那时候原消息可能已经被下一条通知顶掉了。所以整条规则是**收紧**的：
/// 只有「离关键词足够近、长度像验证码、且不在时间/日期上下文里」的候选才会被采纳。
///
/// ★ 两类最主要的干扰 ★
///   1. **有效期**：「5 分钟内有效」「10 分钟」「有效期 30 秒」—— 数字本身的位数与验证码重叠；
///   2. **时间与日期**：「2026-10-17」「11:30」—— 「2026」是四位数，正好落在验证码的长度区间里。
/// 处理办法不是「猜哪个更像」，而是**先给候选打分、再设一个下限**：
/// 时间/日期上下文直接扣到负分，离关键词远的候选也拿不到分。
///
/// ★ 关键词不限于「验证码」★
/// 短信与 App 通知里还常见「校验码」「动态码」「动态密码」「确认码」「授权码」，
/// 以及英文的 verification code。只认「验证码」会漏掉相当一部分真实短信。
/// </summary>
internal static class VerificationCode
{
    /// <summary>用于界面/日志展示的主关键词。</summary>
    public const string Keyword = "验证码";

    /// <summary>认可的关键词（按长度从长到短匹配，避免「动态密码」被「动态码」截断）。</summary>
    private static readonly string[] Keywords =
        ["验证码", "校验码", "动态码", "动态密码", "确认码", "授权码", "verification code", "验证代码"];

    /// <summary>
    /// 候选数字：4~8 位连续数字，且**左右都不是数字**。
    /// 这条边界断言是排除长数字串（订单号、流水号）的关键 ——
    /// 14 位订单号里的任意 4 位都会被前后数字夹住，因此整体不产生候选。
    /// </summary>
    private static readonly Regex Candidate = new(@"(?<!\d)(\d{4,8})(?!\d)", RegexOptions.Compiled);

    /// <summary>形如 2026-10-17 / 2026/10/17 / 2026.10.17 的日期。</summary>
    private static readonly Regex DateLike = new(@"\d{4}\s*[-/.年]\s*\d{1,2}\s*[-/.月]\s*\d{1,2}", RegexOptions.Compiled);

    /// <summary>形如 11:30 / 11:30:05 的时刻。</summary>
    private static readonly Regex ClockLike = new(@"\d{1,2}\s*:\s*\d{2}", RegexOptions.Compiled);

    /// <summary>时长单位：紧跟在数字后面的「分钟 / 秒 / 小时 / 天 / 日 / 个月 / 年」。</summary>
    private static readonly string[] DurationUnits =
        ["分钟", "分", "秒钟", "秒", "小时", "天", "日", "个月", "月", "年"];

    /// <summary>采纳一个候选所需的最低分（见 <see cref="Extract"/> 的打分规则）。</summary>
    private const int AcceptThreshold = 100;

    /// <summary>文本里是否出现了验证码类关键词（用于快速判断要不要走提取流程）。</summary>
    public static bool HasKeyword(string? text)
        => IndexOfKeyword(text ?? "") >= 0;

    /// <summary>
    /// 从标题与正文里提取验证码。返回空串表示「没有把握」，调用方应当**什么都不做**。
    ///
    /// 打分（分数越高越像验证码）：
    ///   · 距关键词 0~16 字符：+100；16~30：+60；关键词**之前** 12 字符内：+80；更远：−20
    ///   · 长度 4/5/6 位：+30/+25/+35；7/8 位：+10/+8
    ///   · 紧跟在「: ： = 为 是 码」之后：+40
    ///   · 落在日期 / 时刻 / 时长上下文里：−300
    /// 总分 ≥ 100 才采纳 —— 也就是说**必须离关键词足够近**，单纯「看起来像数字」不够。
    ///
    /// ⚠️ 这里**没有**「形似年份就扣分」这条规则：验证码完全可能就是 `2024`，
    /// 而真正的年份一定带着 `年/月/日` 或 `2026-10-17` 这样的上下文，
    /// 那已经由 <see cref="IsTimeContext"/> 判掉了。多扣一次只会误伤真验证码。
    /// </summary>
    public static string Extract(string? title, string? body)
    {
        string text = ((title ?? "") + " " + (body ?? "")).Trim();
        if (text.Length == 0) return "";

        int keywordStart = IndexOfKeyword(text);
        if (keywordStart < 0) return "";

        int keywordEnd = keywordStart + MatchedKeywordLength(text, keywordStart);

        string best = "";
        int bestScore = int.MinValue;

        foreach (Match m in Candidate.Matches(text))
        {
            string digits = m.Value;
            int start = m.Index;
            int score = 0;

            // ── ① 与关键词的相对位置 ──
            // 验证码最常见的位置就是「验证码」之后紧跟的那一串（可能隔着空格/冒号/「是」）；
            // 「8842 是您的验证码」这种前置写法也真实存在，所以前侧的权重同样给足。
            int distance = start - keywordEnd;
            if (distance >= -2 && distance <= 16) score += 100;
            else if (distance > 16 && distance <= 30) score += 60;
            else if (distance >= -12 && distance < -2) score += 80;
            else score -= 20;

            // ── ② 长度：4~6 位最常见 ──
            score += digits.Length switch
            {
                4 => 30,
                5 => 25,
                6 => 35,
                7 => 10,
                8 => 8,
                _ => 0,
            };

            // ── ③ 紧邻分隔符（「验证码：8842」「验证码是 8842」「验证码8842」）──
            string before = text[..start].TrimEnd();
            if (before.Length > 0 && before[^1] is ':' or '：' or '=' or '为' or '是' or '码')
                score += 40;

            // ── ④ 时间 / 日期 / 时长上下文：重罚 ──
            if (IsTimeContext(text, start, digits.Length)) score -= 300;

            if (score > bestScore)
            {
                bestScore = score;
                best = digits;
            }
        }

        return bestScore >= AcceptThreshold ? best : "";
    }

    /// <summary>关键词在文本里的位置（不区分大小写）；找不到返回 -1。</summary>
    private static int IndexOfKeyword(string text)
    {
        int best = -1;
        foreach (var kw in Keywords)
        {
            int i = text.IndexOf(kw, StringComparison.OrdinalIgnoreCase);
            if (i >= 0 && (best < 0 || i < best)) best = i;
        }
        return best;
    }

    /// <summary>在 <paramref name="start"/> 处命中的关键词长度（用于算「距关键词多远」）。</summary>
    private static int MatchedKeywordLength(string text, int start)
    {
        foreach (var kw in Keywords)
        {
            if (start + kw.Length <= text.Length
                && string.Compare(text, start, kw, 0, kw.Length, StringComparison.OrdinalIgnoreCase) == 0)
                return kw.Length;
        }
        return 0;
    }

    /// <summary>
    /// 这个候选是不是出现在「时间 / 日期 / 时长」的语境里。
    ///
    /// 三种判据，任一命中即算：
    ///   ① 它整体落在某个日期串或时刻串内部（2026-10-17、11:30）；
    ///   ② 它紧跟着时长单位（「5分钟」「30秒」「2小时」—— 中间允许有空格）；
    ///   ③ 它后面紧跟冒号且再后面还有数字（「11:30」的 11）。
    /// </summary>
    private static bool IsTimeContext(string text, int start, int length)
    {
        int end = start + length;

        foreach (Match m in DateLike.Matches(text))
            if (start >= m.Index && end <= m.Index + m.Length) return true;

        foreach (Match m in ClockLike.Matches(text))
            if (start >= m.Index && end <= m.Index + m.Length) return true;

        // 紧跟时长单位：「8842 分钟内」这种极少见，所以只看紧邻（允许 1 个空格）
        int i = end;
        while (i < text.Length && text[i] == ' ') i++;
        foreach (var unit in DurationUnits)
            if (i + unit.Length <= text.Length
                && string.Compare(text, i, unit, 0, unit.Length, StringComparison.Ordinal) == 0)
                return true;

        return false;
    }
}
