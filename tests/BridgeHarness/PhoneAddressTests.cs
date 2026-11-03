using NpsPushBridge;

namespace BridgeHarness;

/// <summary>
/// <see cref="PhoneAddress"/>（手机端要填的地址 = <c>http://IP:端口/</c>）的验证。
///
/// ★ 为什么这几条值得钉死 ★
/// 这一行显示的东西会被用户**原样复制到手机上**，错了就是「手机点了没反应」——
/// 而那种现象与网络不通、防火墙没放行**完全一样**，用户几乎不可能自己查出来。
/// 所以拼接规则必须确定：IP 为空时宁可不显示（也不能显示 <c>http://:1403/</c> 这种假地址）、
/// 端口跟着输入框实时变、端口非法时退回**上一个生效值**而不是跳回默认值
/// （否则用户清空端口的一瞬间地址会跳变，看起来像「改了没生效」）。
/// </summary>
internal static class PhoneAddressTests
{
    private static int _passed, _failed;

    private static void Check(string name, bool ok, string detail = "")
    {
        if (ok) { _passed++; Console.WriteLine($"  [PASS] {name}"); }
        else { _failed++; Console.WriteLine($"  [FAIL] {name}{(detail.Length > 0 ? "  —— " + detail : "")}"); }
    }

    public static (int Passed, int Failed) Run()
    {
        Console.WriteLine("【手机端地址拼接】");

        BuildSection();
        PortTextSection();
        NormalizeSection();

        return (_passed, _failed);
    }

    private static void BuildSection()
    {
        Console.WriteLine("  -- 拼接 --");

        Check("常规地址：http://IP:端口/（与官网教程「发送通道」里要填的形式一致）",
            PhoneAddress.Build("192.168.1.10", 1403) == "http://192.168.1.10:1403/",
            PhoneAddress.Build("192.168.1.10", 1403));

        Check("★ 结尾必须带斜杠（教程写的是 http://<电脑IP>:<端口>/）",
            PhoneAddress.Build("10.0.0.5", 8080).EndsWith("/", StringComparison.Ordinal));

        Check("★ IP 为空 → 空串（不产生 http://:1403/ 这种看起来能用、复制过去必然失败的假地址）",
            PhoneAddress.Build("", 1403).Length == 0 && PhoneAddress.Build(null, 1403).Length == 0);

        Check("IP 两侧空白被清掉（宿主枚举出的地址可能带空格）",
            PhoneAddress.Build("  192.168.1.10  ", 1403) == "http://192.168.1.10:1403/",
            PhoneAddress.Build("  192.168.1.10  ", 1403));

        Check("端口越界（0 / 负数 / >65535）→ 退回默认端口，而不是拼出一个非法端口",
            PhoneAddress.Build("192.168.1.10", 0) == $"http://192.168.1.10:{PhoneAddress.DefaultPort}/"
            && PhoneAddress.Build("192.168.1.10", -1) == $"http://192.168.1.10:{PhoneAddress.DefaultPort}/"
            && PhoneAddress.Build("192.168.1.10", 70000) == $"http://192.168.1.10:{PhoneAddress.DefaultPort}/");

        Check("端口边界值 1 / 65535 都原样保留", 
            PhoneAddress.Build("192.168.1.10", 1).Contains(":1/")
            && PhoneAddress.Build("192.168.1.10", 65535).Contains(":65535/"));

        Check("提示文案非空（检测不到网卡时面板显示它）", PhoneAddress.NoAddressHint.Length > 0);
    }

    private static void PortTextSection()
    {
        Console.WriteLine("  -- 端口来自输入框（实时跟随）--");

        // 输入框里是合法端口 → 用它（用户改端口时地址立刻跟着变，无需先保存）
        Check("★ 端口文本合法 → 地址跟着输入框走（不用先点保存）",
            PhoneAddress.Build("192.168.1.10", "1405", 999) == "http://192.168.1.10:1405/",
            PhoneAddress.Build("192.168.1.10", "1405", 999));

        Check("端口文本带空格也能解析", 
            PhoneAddress.Build("192.168.1.10", " 1405 ", 999) == "http://192.168.1.10:1405/");

        // 输入框里是半截 / 非法值 → 退回**上一个生效值**（不是默认端口）
        Check("★ 端口输入框被清空 → 退回上一次生效的端口（不跳回默认 1403，避免看起来像没生效）",
            PhoneAddress.Build("192.168.1.10", "", 8080) == "http://192.168.1.10:8080/",
            PhoneAddress.Build("192.168.1.10", "", 8080));

        Check("★ 端口输入框是非数字 → 同样退回上一次生效的端口",
            PhoneAddress.Build("192.168.1.10", "abc", 8080) == "http://192.168.1.10:8080/",
            PhoneAddress.Build("192.168.1.10", "abc", 8080));

        Check("★ 端口写成 0（非法）→ 退回上一次生效的端口",
            PhoneAddress.Build("192.168.1.10", "0", 8080) == "http://192.168.1.10:8080/");

        Check("回退值本身也越界时，才用默认端口兜底",
            PhoneAddress.Build("192.168.1.10", "", 0) == $"http://192.168.1.10:{PhoneAddress.DefaultPort}/",
            PhoneAddress.Build("192.168.1.10", "", 0));

        Check("IP 为空时即便端口合法也返回空串（顺序无关）",
            PhoneAddress.Build("", "1405", 8080).Length == 0);
    }

    private static void NormalizeSection()
    {
        Console.WriteLine("  -- 端口解析 --");

        Check("TryParsePort：正常值", PhoneAddress.TryParsePort("1403") == 1403);
        Check("TryParsePort：边界 1 / 65535",
            PhoneAddress.TryParsePort("1") == 1 && PhoneAddress.TryParsePort("65535") == 65535);
        Check("TryParsePort：0 / 65536 / 负数 → null",
            PhoneAddress.TryParsePort("0") is null
            && PhoneAddress.TryParsePort("65536") is null
            && PhoneAddress.TryParsePort("-1") is null);
        Check("TryParsePort：空 / 空白 / 非数字 / null → null",
            PhoneAddress.TryParsePort("") is null
            && PhoneAddress.TryParsePort("   ") is null
            && PhoneAddress.TryParsePort("abc") is null
            && PhoneAddress.TryParsePort(null) is null);

        Check("Normalize：越界取默认，合法原样返回",
            PhoneAddress.Normalize(0) == PhoneAddress.DefaultPort
            && PhoneAddress.Normalize(70000) == PhoneAddress.DefaultPort
            && PhoneAddress.Normalize(8080) == 8080);

        Check("默认端口就是插件的默认端口（1403，与端口输入框的初始值一致）",
            PhoneAddress.DefaultPort == 1403);
    }
}
