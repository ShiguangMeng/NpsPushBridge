using NpsPushBridge;

namespace BridgeHarness;

/// <summary>
/// <see cref="VerificationCode"/>（验证码识别与提取）的验证。
///
/// ★ 为什么这一组的「反证」比正例更重要 ★
/// 提取错一个数字的代价比提取不到高得多：用户会拿它去登录、失败一次、再回来翻消息，
/// 而那时原通知可能已经被下一条顶掉了。所以这里刻意把**必须拒绝**的情况单独列一节：
/// 「验证码 5 分钟内有效」必须返回空（不能把 5 当验证码）、
/// 「只有日期」「只有关键词没有数字」也必须返回空。
///
/// 时间与日期是最主要的干扰源：`2026-10-17` 里的 `2026` 正好是四位数，
/// 与四位验证码在长度上无法区分 —— 只能靠**上下文**判掉。
/// </summary>
internal static class VerificationCodeTests
{
    private static int _passed, _failed;

    private static void Check(string name, bool ok, string detail = "")
    {
        if (ok) { _passed++; Console.WriteLine($"  [PASS] {name}"); }
        else { _failed++; Console.WriteLine($"  [FAIL] {name}{(detail.Length > 0 ? "  —— " + detail : "")}"); }
    }

    public static (int Passed, int Failed) Run()
    {
        Console.WriteLine("【验证码识别与提取】");

        ExtractSection();
        TimeNoiseSection();
        RefusalSection();

        return (_passed, _failed);
    }

    // ── 常见写法都要认得 ─────────────────────────────────────────────────

    private static void ExtractSection()
    {
        Check("淘宝式：「验证码 8842，5分钟内有效」→ 8842",
            VerificationCode.Extract("【淘宝】", "验证码 8842，5分钟内有效。") == "8842",
            VerificationCode.Extract("【淘宝】", "验证码 8842，5分钟内有效。"));

        Check("「是」句式：「验证码是 123456，10 分钟内有效」→ 123456",
            VerificationCode.Extract("", "您的验证码是 123456，10 分钟内有效") == "123456",
            VerificationCode.Extract("", "您的验证码是 123456，10 分钟内有效"));

        Check("冒号式：「验证码：6688（有效期 5 分钟）」→ 6688",
            VerificationCode.Extract("", "验证码：6688（有效期 5 分钟）") == "6688",
            VerificationCode.Extract("", "验证码：6688（有效期 5 分钟）"));

        Check("前置式：「8842 是您的验证码」→ 8842",
            VerificationCode.Extract("", "8842 是您的验证码，请勿泄露") == "8842",
            VerificationCode.Extract("", "8842 是您的验证码，请勿泄露"));

        Check("无分隔：「验证码8842」→ 8842",
            VerificationCode.Extract("", "验证码8842") == "8842",
            VerificationCode.Extract("", "验证码8842"));

        Check("变体关键词：动态码 6688 → 6688",
            VerificationCode.Extract("", "动态码 6688，请勿告诉他人") == "6688",
            VerificationCode.Extract("", "动态码 6688，请勿告诉他人"));

        Check("变体关键词：校验码 1234 → 1234",
            VerificationCode.Extract("", "校验码 1234") == "1234",
            VerificationCode.Extract("", "校验码 1234"));

        Check("英文：Your verification code is 456789 → 456789",
            VerificationCode.Extract("", "Your verification code is 456789") == "456789",
            VerificationCode.Extract("", "Your verification code is 456789"));

        // ★ 长数字串整体不产生候选（订单号里的任意 4 位都被前后数字夹住）★
        Check("★ 订单号不干扰：验证码 8842（另有一个 14 位订单号）→ 8842",
            VerificationCode.Extract("", "订单号 20251017123456，验证码 8842") == "8842",
            VerificationCode.Extract("", "订单号 20251017123456，验证码 8842"));

        Check("多候选取最近：验证码 8842，客服 40012345 → 8842",
            VerificationCode.Extract("", "验证码 8842，客服 40012345") == "8842",
            VerificationCode.Extract("", "验证码 8842，客服 40012345"));

        Check("标题里带验证码也能取到",
            VerificationCode.Extract("验证码 9951", "5分钟内有效") == "9951",
            VerificationCode.Extract("验证码 9951", "5分钟内有效"));
    }

    // ── 时间 / 日期干扰 ─────────────────────────────────────────────────

    private static void TimeNoiseSection()
    {
        Check("★ 时刻干扰：验证码 8842，11:30 前使用 → 8842",
            VerificationCode.Extract("", "验证码 8842，请在 11:30 前使用") == "8842",
            VerificationCode.Extract("", "验证码 8842，请在 11:30 前使用"));

        Check("★ 日期干扰：2026-10-17 您的验证码 668899 → 668899",
            VerificationCode.Extract("", "2026-10-17 11:30 您的验证码 668899") == "668899",
            VerificationCode.Extract("", "2026-10-17 11:30 您的验证码 668899"));

        Check("★ 年份本身可以是验证码：「验证码 2024」→ 2024（不能因为形似年份就拒绝）",
            VerificationCode.Extract("", "验证码 2024") == "2024",
            VerificationCode.Extract("", "验证码 2024"));

        Check("★ 带「年」的才是年份：「验证码 2024年」不采纳 2024",
            VerificationCode.Extract("", "验证码 2024年有效") == "",
            VerificationCode.Extract("", "验证码 2024年有效"));

        Check("★ 时长单位：验证码 8842，有效期 30 秒 → 8842",
            VerificationCode.Extract("", "验证码 8842，有效期 30 秒") == "8842",
            VerificationCode.Extract("", "验证码 8842，有效期 30 秒"));

        Check("★ 银行式长句：验证码为 123456，请于 2026-10-17 前使用 → 123456",
            VerificationCode.Extract("【银行】", "您的验证码为 123456，请于 2026-10-17 前使用") == "123456",
            VerificationCode.Extract("【银行】", "您的验证码为 123456，请于 2026-10-17 前使用"));
    }

    // ── ★ 必须拒绝的情况（宁可复制不到，也不能复制错）────────────────────

    private static void RefusalSection()
    {
        Check("★ 只有时长数字：「验证码 5 分钟内有效」→ 空（绝不能把 5 当验证码）",
            VerificationCode.Extract("", "验证码 5 分钟内有效") == "",
            VerificationCode.Extract("", "验证码 5 分钟内有效"));

        Check("★ 只有日期：「验证码 2026-10-17 有效」→ 空",
            VerificationCode.Extract("", "验证码 2026-10-17 有效") == "",
            VerificationCode.Extract("", "验证码 2026-10-17 有效"));

        Check("★ 没有关键词：「今晚 8842 吃饭」→ 空",
            VerificationCode.Extract("", "今晚 8842 吃饭") == "");

        Check("★ 有词无数字：「验证码已发送到您的手机」→ 空",
            VerificationCode.Extract("", "验证码已发送到您的手机") == "");

        Check("空输入安全",
            VerificationCode.Extract(null, null) == ""
            && VerificationCode.Extract("", "") == ""
            && VerificationCode.Extract(null, "验证码") == "");

        Check("★ 反证：把时间数字喂进去，提取器也不会「随手挑一个」",
            VerificationCode.Extract("", "验证码，10 分钟内有效，超时请重发") == "");

        Check("HasKeyword：认得「验证码」与英文写法",
            VerificationCode.HasKeyword("您的验证码是 1234")
            && VerificationCode.HasKeyword("Your verification code")
            && !VerificationCode.HasKeyword("今晚吃饭"));
    }
}
