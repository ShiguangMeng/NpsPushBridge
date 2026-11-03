using NpsPushBridge;

namespace BridgeHarness;

/// <summary>
/// 转发重试策略（<see cref="HttpBridgeServer.ShouldRetryForStatus"/>）的验证。
///
/// ★ 为什么这条边界值得单独钉 ★
/// 重试的判据一旦放宽（比如顺手写成「所有非 2xx 都重试」），后果是：
/// 400 / 403 这类**确定性**失败会被发两遍 —— 手机端日志里出现两条同样的错误、
/// 宿主计数翻倍、而真正的原因被淹没。反过来判据收得太紧（只重试 404），
/// 宿主 5xx 的临时故障就永远不会自愈。
///
/// 所以这里把三类分开断言：
///   · **404** —— HTTP.sys 的 URL 前缀尚未注册（请求没到宿主），必须重试；
///   · **5xx** —— 宿主侧临时故障，必须重试；
///   · **400 / 403 / 其他 4xx** —— 我方请求内容或来源有问题，重试无意义，**绝不重试**。
/// </summary>
internal static class ForwardRetryTests
{
    private static int _passed, _failed;

    private static void Check(string name, bool ok, string detail = "")
    {
        if (ok) { _passed++; Console.WriteLine($"  [PASS] {name}"); }
        else { _failed++; Console.WriteLine($"  [FAIL] {name}{(detail.Length > 0 ? "  —— " + detail : "")}"); }
    }

    public static (int Passed, int Failed) Run()
    {
        Console.WriteLine("【转发重试策略】");

        Check("★ 404 重试（HTTP.sys 前缀未注册时请求根本没到宿主）",
            HttpBridgeServer.ShouldRetryForStatus(404));

        Check("★ 5xx 重试（宿主侧临时故障）",
            HttpBridgeServer.ShouldRetryForStatus(500)
            && HttpBridgeServer.ShouldRetryForStatus(502)
            && HttpBridgeServer.ShouldRetryForStatus(503));

        Check("★★ 400 不重试（请求体本身有问题，重试只会把一条错误刷成两条）",
            !HttpBridgeServer.ShouldRetryForStatus(400));

        Check("★★ 403 不重试（来源被拒绝，重试也没用）",
            !HttpBridgeServer.ShouldRetryForStatus(403));

        Check("★ 其余 4xx（401 / 405 / 409 / 429）一律不重试",
            !HttpBridgeServer.ShouldRetryForStatus(401)
            && !HttpBridgeServer.ShouldRetryForStatus(405)
            && !HttpBridgeServer.ShouldRetryForStatus(409)
            && !HttpBridgeServer.ShouldRetryForStatus(429));

        return (_passed, _failed);
    }
}
