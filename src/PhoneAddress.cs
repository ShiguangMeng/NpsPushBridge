namespace NpsPushBridge;

/// <summary>
/// 「手机端要填的地址」的拼接规则（纯逻辑、零依赖 ⇒ 可被验证工程逐条断言）。
///
/// 手机端 SMSForwarder 的 Webhook Server 里要填的是一个**完整 URL**：
/// <c>http://&lt;电脑局域网 IP&gt;:&lt;端口&gt;/</c>（见官网教程「手机端配置 → 发送通道」）。
/// 以前这一步是让用户自己拼 —— 教程里写的是 <c>http://&lt;电脑IP&gt;:&lt;端口&gt;/</c>，
/// 而「电脑 IP」得自己去 ipconfig 里找、端口得回到配置界面里看，
/// 拼错的代价是「手机点了没反应」，而那种现象与网络不通**完全一样**，用户很难自查。
/// 所以改成：插件直接把这个地址拼好显示出来，一键复制即可。
///
/// ★ 为什么单独成一个类，而不是写在面板里 ★
/// 面板类依赖宿主接口，进不了独立验证工程；而「怎么拼、端口取哪个值、尾斜杠要不要」
/// 恰恰是最该被钉死的部分。抽成纯函数之后，这些规则可以逐条断言（见 PhoneAddressTests）。
/// ——这与项目既有原则一致：**可算的部分下沉成纯函数才测得到**。
/// </summary>
internal static class PhoneAddress
{
    /// <summary>默认端口（与插件默认值一致，见 <see cref="ConfigPanelDefaults"/> 同源的 1403）。</summary>
    public const int DefaultPort = 1403;

    /// <summary>
    /// 用 IP 与端口拼出手机端要填的地址；**IP 为空时返回空串**（面板据此显示「未检测到」的提示，
    /// 而不是显示一个 <c>http://:1403/</c> 这种看起来能用、复制过去必然失败的假地址）。
    ///
    /// 端口越界时退回 <see cref="DefaultPort"/>：调用方拿到的永远是一个**可用**的地址。
    /// </summary>
    public static string Build(string? ip, int port)
    {
        var host = (ip ?? "").Trim();
        if (host.Length == 0) return "";

        if (port is < 1 or > 65535) port = DefaultPort;
        return $"http://{host}:{port}/";
    }

    /// <summary>
    /// 同上，但端口来自**输入框里的文本**（可能正在被编辑：空串 / 半截 / 带空格）。
    ///
    /// ⚠️ 解析不出来时回退到 <paramref name="fallbackPort"/>（= 上一份**已保存**的端口），
    /// 而不是直接跳回默认值：用户把端口从 <c>1403</c> 删空的那一瞬间，
    /// 地址若跳成 <c>:1403</c> 会让人以为「改了但没生效」；
    /// 用「上一个生效值」既能继续显示一个真实可用的地址，也符合「还没保存」的直觉。
    /// </summary>
    public static string Build(string? ip, string? portText, int fallbackPort)
        => Build(ip, TryParsePort(portText) ?? Normalize(fallbackPort));

    /// <summary>把端口文本解析成端口号；空 / 非数字 / 越界都返回 <c>null</c>（= 无法解析）。</summary>
    public static int? TryParsePort(string? portText)
    {
        var text = (portText ?? "").Trim();
        if (text.Length == 0) return null;
        if (!int.TryParse(text, out int value)) return null;
        return value is >= 1 and <= 65535 ? value : null;
    }

    /// <summary>把任意整数收敛成合法端口（越界则取默认值）。</summary>
    public static int Normalize(int port) => port is >= 1 and <= 65535 ? port : DefaultPort;

    /// <summary>
    /// 检测不到可用地址时面板显示的文案（与 <see cref="Build"/> 返回空串的情形对应）。
    /// 提取成常量是为了让「面板显示什么」与「断言里期望什么」共用同一份字面量。
    /// </summary>
    public const string NoAddressHint = "未检测到可用的局域网地址";
}
