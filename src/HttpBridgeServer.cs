using System.Net;
using System.Net.Sockets;
using System.Text;

namespace NpsPushBridge;

/// <summary>
/// 一个极简的局域网 HTTP 服务，负责：接收手机/其它设备推送的消息 → 校验来源 IP
/// → 按应用黑名单过滤 → 转发给灵动岛。
///
/// 为什么用 TcpListener 而不是 System.Net.HttpListener：
/// HttpListener 监听非回环地址（http://+:1403/）需要管理员权限或预先 netsh http add urlacl 注册，
/// 而插件不应该要求用户提权。裸 TcpListener 绑定 0.0.0.0 普通用户即可，因此这里自行实现最小 HTTP/1.1 解析。
/// （首次运行时 Windows 防火墙可能弹出放行提示，属正常现象。）
/// </summary>
internal sealed class HttpBridgeServer : IDisposable
{
    private readonly object _gate = new();
    /// <summary>白名单以委托形式实时读取，配置改动无需重启服务即可生效。</summary>
    private readonly Func<IpWhitelist> _whitelist;
    /// <summary>应用黑名单，同样以委托实时读取 —— 在配置界面加一条即生效，无需重启服务。</summary>
    private readonly Func<AppBlacklist> _blacklist;
    /// <summary>
    /// 用户自定义的「应用 → icon」映射，同样以委托实时读取 —— 在配置界面加一条即生效，无需重启服务。
    /// </summary>
    private readonly Func<IconOverrides> _iconOverrides;
    private readonly Action<string> _log;
    private readonly Action<PushActivity> _onForwarded;
    /// <summary>
    /// 识别到验证码时的回调（参数：消息 + 提取到的验证码）。
    ///
    /// ★ 为什么这类消息要走另一条通道 ★
    /// 宿主 <c>/api/activities/</c> 创建的是纯 <c>ToastData</c>，**不读 onClick**
    /// （见宿主 <c>Notify/Toast.cs</c>），所以插件永远拿不到「用户点了这条通知」。
    /// 而 <c>IPluginHost.PostReminder</c> 的 <c>ReminderData.OnClick</c> 会被宿主原样传给
    /// <c>ToastData</c> 并在点击时执行（<c>PluginHost.cs</c> + <c>NotchWindow.cs</c>）。
    /// 因此「点击复制验证码」只能改走插件提醒 —— 代价是显示形态变化，换来的是可点击。
    /// 为 null 时（如验证工程）不启用，消息照旧走 HTTP 转发。
    /// </summary>
    private readonly Action<PushActivity, string>? _onVerificationCode;

    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private HttpClient? _client;
    private volatile bool _stopping;
    private volatile string _lastRemoteIp = "";

    private long _received, _forwarded, _rejected, _failed, _blocked;

    /// <summary>
    /// 「此刻健不健康」标记（状态灯据此决定橙 / 绿）：
    /// **请求无效（400）或转发失败（502）时置位，之后任意一次成功转发即清除**。
    ///
    /// ★ 为什么不直接读累计计数 <see cref="Failed"/> ★
    /// 旧版状态灯读的正是累计失败数，于是「历史上失败过一次」就等于「灯永久变橙」：
    /// 服务明明已经恢复正常，用户看到的却始终是「有失败」—— 这就是「状态灯无法正确显示当前状态」。
    /// 累计计数是**统计**，本标记是**状态**，两者必须分开维护。
    ///
    /// ★ 白名单拒绝（403）故意**不**置位 ★
    /// 403 表示「来源不被信任」，不是服务本身出问题；最常见的触发场景恰恰是用户用电脑浏览器
    /// 打开状态页自检（回环地址不在白名单内，见《配置教程》的已知坑）。
    /// 若把 403 也算作不健康，用户每自检一次灯就变橙，反而误导。403 只累加到 <see cref="Rejected"/>。
    /// </summary>
    private volatile bool _unhealthy;

    /// <summary>
    /// 最近一次失败的可读原因（400 的解析原因 / 502 的转发错误）。
    /// 只用于展示 —— 状态灯变橙时，用户需要知道「为什么橙」；空串表示本次运行内没失败过。
    /// </summary>
    private volatile string _lastFailure = "";

    /// <param name="port">局域网监听端口。</param>
    /// <param name="whitelist">来源 IP 白名单提供者（每次连接实时读取；无规则表示不限制）。</param>
    /// <param name="blacklist">应用黑名单提供者（每条消息实时读取；无规则表示不拦截任何应用）。</param>
    /// <param name="iconOverrides">自定义图标映射提供者（每次转发实时读取；可为 null 表示只用内置映射）。</param>
    /// <param name="log">日志回调（会在后台线程被调用）。</param>
    /// <param name="onForwarded">转发成功回调，用于计数与灵动岛摘要。</param>
    /// <param name="onVerificationCode">识别到验证码时的回调（改由插件提醒发出，可点击复制）；null = 不启用。</param>
    public HttpBridgeServer(int port, Func<IpWhitelist> whitelist, Func<AppBlacklist> blacklist,
        Func<IconOverrides>? iconOverrides, Action<string> log, Action<PushActivity> onForwarded,
        Action<PushActivity, string>? onVerificationCode = null)
    {
        Port = port;
        _whitelist = whitelist;
        _blacklist = blacklist;
        _iconOverrides = iconOverrides ?? (static () => IconOverrides.Parse(null));
        _log = log;
        _onForwarded = onForwarded;
        _onVerificationCode = onVerificationCode;
    }

    public int Port { get; }

    public bool IsRunning { get { lock (_gate) return _listener != null; } }

    public long Received => Interlocked.Read(ref _received);
    public long Forwarded => Interlocked.Read(ref _forwarded);
    /// <summary>被**应用黑名单**拦下、因而没有推送上岛的消息数（属预期行为，不影响状态灯）。</summary>
    public long Blocked => Interlocked.Read(ref _blocked);
    public long Rejected => Interlocked.Read(ref _rejected);
    public long Failed => Interlocked.Read(ref _failed);

    /// <summary>是否存在**尚未恢复**的失败（成功转发一次即清除）——状态灯读它决定橙/绿。</summary>
    public bool Unhealthy => _unhealthy;

    /// <summary>最近一次失败的可读原因（空串 = 本次运行内没有失败）——状态页/日志据此说明灯为何是橙。</summary>
    public string LastFailure => _lastFailure;

    /// <summary>最近一次建立连接的来源 IP（含被拒绝的），供一键加入白名单。</summary>
    public string LastRemoteIp => _lastRemoteIp;

    // ====================================================================
    // 生命周期
    // ====================================================================

    /// <summary>启动监听。返回 false 时 <paramref name="error"/> 给出可读原因（如端口被占用）。</summary>
    public bool Start(out string error)
    {
        lock (_gate)
        {
            if (_listener != null) { error = ""; return true; }

            try
            {
                var listener = new TcpListener(IPAddress.Any, Port);
                listener.Start();   // 绑定 0.0.0.0:Port，普通用户权限即可，无需 urlacl

                var cts = new CancellationTokenSource();
                _listener = listener;
                _cts = cts;
                _stopping = false;
                _client = new HttpClient(new SocketsHttpHandler
                {
                    // ★★ 绝不允许走系统代理 ★★
                    // 这是本机回环地址，请求只该在两段用户态之间直连。而 HttpClient 默认
                    // 会读 Windows 的代理设置（Clash / v2ray 这类工具常驻时几乎必然存在），
                    // 一旦被代理接手，代理不认识 127.0.0.1:47300 就会**直接回 404** ——
                    // 表现正是「插件说宿主返回 HTTP 404，但宿主日志里根本没有这条请求」。
                    // 关掉代理后请求才真正落在宿主的 HttpListener 上。
                    UseProxy = false,
                    // 回环通信不需要压缩协商，省掉一次无意义的往返
                    AutomaticDecompression = System.Net.DecompressionMethods.None,
                })
                {
                    BaseAddress = new Uri("http://127.0.0.1:47300/"),
                    Timeout = TimeSpan.FromSeconds(5),
                };

                _ = Task.Run(() => AcceptLoopAsync(listener, cts.Token));

                _log($"服务已启动：监听 0.0.0.0:{Port}，白名单 {_whitelist().Describe()}，黑名单 {_blacklist().Describe()}");
                error = "";
                return true;
            }
            catch (SocketException ex)
            {
                error = ex.SocketErrorCode == SocketError.AddressAlreadyInUse
                    ? $"端口 {Port} 已被占用，请在配置里换一个端口"
                    : $"监听失败：{ex.Message}";
                Cleanup();
                return false;
            }
            catch (Exception ex)
            {
                error = $"监听失败：{ex.Message}";
                Cleanup();
                return false;
            }
        }
    }

    /// <summary>停止监听并释放网络资源（可反复调用）。</summary>
    public void Stop()
    {
        lock (_gate)
        {
            if (_listener == null && _cts == null) return;
            _stopping = true;
            Cleanup();
        }
        _log($"服务已停止（端口 {Port}）");
    }

    private void Cleanup()
    {
        try { _cts?.Cancel(); } catch { /* 忽略 */ }
        try { _listener?.Stop(); } catch { /* 忽略 */ }
        try { _client?.Dispose(); } catch { /* 忽略 */ }
        try { _cts?.Dispose(); } catch { /* 忽略 */ }
        _cts = null;
        _listener = null;
        _client = null;
    }

    public void Dispose() => Stop();

    // ====================================================================
    // 接入循环
    // ====================================================================

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(token);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (Exception ex)
            {
                if (!_stopping) _log($"接受连接失败：{ex.Message}");
                continue;
            }

            _ = Task.Run(() => HandleClientAsync(client, token), CancellationToken.None);
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken token)
    {
        try
        {
            using (client)
            {
                client.ReceiveTimeout = 8000;
                client.SendTimeout = 8000;

                if (client.Client.RemoteEndPoint is not IPEndPoint endpoint) return;
                var remote = endpoint.Address;
                _lastRemoteIp = remote.ToString();   // 先记录来源，即使随后被拒绝也能一键加白

                using var stream = client.GetStream();

                // ① 白名单拦截（白名单为空 = 放行全部）
                //    注意：拒绝前必须把请求读完再回应 —— 带着未读数据关闭连接会触发 TCP RST，
                //    客户端只会看到 “response ended prematurely”，拿不到 403 与错误原因。
                if (!_whitelist().IsAllowed(remote))
                {
                    // 只累加「拒绝」，**不动 _unhealthy**：来源不被信任 ≠ 服务不健康。
                    // 否则用户每次用浏览器打开状态页自检（回环地址不在白名单里）都会把灯弄橙。
                    Interlocked.Increment(ref _rejected);
                    _log($"拒绝 {remote}：不在白名单内");
                    await DrainRequestAsync(stream, token);
                    await WriteResponseAsync(stream, 403, "application/json; charset=utf-8",
                        "{\"ok\":false,\"error\":\"ip_not_allowed\"}", token);
                    return;
                }

                var head = await ReadHeadAsync(stream, token);
                var body = await ReadBodyAsync(stream, head, token);
                var request = new HttpRequest(head.Method, head.Path, body);

                // ② POST：把消息转发给灵动岛
                if (request.Method == "POST")
                {
                    if (!NpsActivityEndpoint.TryParse(request.Body, out var activity, out var parseError))
                    {
                        Interlocked.Increment(ref _failed);
                        _lastFailure = $"请求无效（来自 {remote}）：{parseError}";
                        _unhealthy = true;          // 灯转橙：有消息没能送达
                        _log($"请求无效（来自 {remote}）：{parseError}");
                        await WriteResponseAsync(stream, 400, "application/json; charset=utf-8",
                            "{\"ok\":false,\"error\":\"bad_request\"}", token);
                        return;
                    }

                    Interlocked.Increment(ref _received);

                    // ★ ② 应用黑名单：命中则**不转发**（静默丢弃）★
                    // 为什么对发送端返回成功而不是 4xx：手机端的 Webhook 收到非 2xx 通常会重试、
                    // 或在通知栏留一条「发送失败」。而「这个应用别上岛」是用户的正常意图，
                    // 不该在手机上表现成故障。丢弃是**结果**，不是**错误**。
                    // 因此这里只加 Blocked 计数、只记日志（用户能确认到底拦了什么），
                    // **不置 _unhealthy** —— 状态灯反映「服务健不健康」，拦截时它依然健康。
                    // 判定放在补图标之前：省掉一次查表与可能的别名探测，也避免为一条不会发出的消息准备图标。
                    if (_blacklist().IsBlocked(activity.Package, activity.Kind))
                    {
                        Interlocked.Increment(ref _blocked);
                        _log($"已拦截（应用黑名单）：来自 {remote} → {activity.Describe()}");
                        await WriteResponseAsync(stream, 200, "application/json; charset=utf-8",
                            "{\"ok\":true,\"blocked\":true}", token);
                        return;
                    }

                    // ★ ③ 验证码消息：改由「插件提醒」发出，用户点一下即可复制 ★
                    // 通道选择的理由见 _onVerificationCode 的说明。
                    // 回调失败时**降级为普通转发** —— 宁可少一个可点击的提醒，也不能把消息丢掉。
                    var code = VerificationCode.Extract(activity.Title, activity.Subtitle);
                    if (code.Length > 0 && _onVerificationCode != null)
                    {
                        try
                        {
                            _onVerificationCode(activity, code);
                            Interlocked.Increment(ref _forwarded);   // 对用户而言「已送达」
                            _unhealthy = false;
                            _log($"来自 {remote} → {activity.Describe()}（识别到验证码 {code}，已用提醒发出，点击可复制）");
                            await WriteResponseAsync(stream, 200, "application/json; charset=utf-8",
                                "{\"ok\":true}", token);
                            return;
                        }
                        catch (Exception ex)
                        {
                            _log($"验证码提醒发送失败，改走普通转发：{ex.GetType().Name} {ex.Message}");
                        }
                    }

                    // ★ 按 kind 二次补全 icon ★
                    // 解析层只「照实读出」入站 icon，真正的补全在这里做 —— 因为补全要读用户配置，
                    // 而解析层刻意保持无外部状态（见 NpsActivityEndpoint.TryParse 的说明）。
                    // Decide 内部：入站已有 icon → 原样保留；否则查自定义映射 → 内置别名 → 内置链接。
                    var decision = IconCatalog.Decide(activity.Kind, activity.Icon, _iconOverrides());
                    if (decision.HasIcon) activity = activity with { Icon = decision.Icon };

                    var (ok, message) = await ForwardAsync(activity, token);
                    if (ok)
                    {
                        Interlocked.Increment(ref _forwarded);
                        // ★ 一次成功投递即视为「已恢复」→ 灯立刻回绿 ★
                        // 灯反映的必须是「此刻健不健康」，不能因为历史上失败过就永久变橙。
                        _unhealthy = false;
                        try { _onForwarded(activity); } catch { /* 回调异常不影响响应 */ }
                        _log($"来自 {remote} → {activity.DescribeWithIcon()}");
                        // 补全过程的说明单独一行：既让用户确认「图标到底补上没」，
                        // 也把「为什么没补」写清楚（未收录 / 未命中 / 入站已指定）。
                        _log($"图标：{decision.Source} · {decision.Reason}");
                        await WriteResponseAsync(stream, 200, "application/json; charset=utf-8", "{\"ok\":true}", token);
                    }
                    else
                    {
                        Interlocked.Increment(ref _failed);
                        _lastFailure = $"转发失败：{message}";
                        _unhealthy = true;          // 灯转橙：宿主接口没接住
                        _log($"转发失败：{message}");
                        await WriteResponseAsync(stream, 502, "application/json; charset=utf-8",
                            "{\"ok\":false,\"error\":\"forward_failed\"}", token);
                    }
                    return;
                }

                // ③ 其它方法（含浏览器 GET /）：返回状态页，便于自检
                await WriteResponseAsync(stream, 200, "text/plain; charset=utf-8", BuildStatusPage(), token);
            }
        }
        catch (OperationCanceledException) { /* 停止服务时的正常中断 */ }
        catch (IOException) { /* 客户端提前断开，静默 */ }
        catch (Exception ex)
        {
            if (!_stopping) _log($"连接处理异常：{ex.Message}");
        }
    }

    /// <summary>
    /// 转发到宿主本地接口 <c>http://127.0.0.1:47300/api/activities/</c>。
    ///
    /// ★ 为什么 404 / 5xx 要重试一次 ★
    /// 宿主的监听跑在 HTTP.sys 上，**URL 前缀是在宿主启动过程中异步注册的**。
    /// 宿主刚起来（或刚重建监听）的那一瞬间发来的请求，会被 HTTP.sys **直接**回 404 ——
    /// 这条请求根本不会到达宿主，所以在宿主日志里查不到任何痕迹，
    /// 表现就是「插件说转发失败，但消息其实丢了」。
    /// 等 250ms 再发一次就能越过这个窗口。
    ///
    /// ⚠️ 只对 **404 与 5xx** 重试：其余 4xx（如 400）说明是**我方请求内容**有问题，
    /// 重试多少次都一样，只会把日志刷满、把一条真实错误拖成两条。
    /// 重试成功时会**明确记一行**「转发重试成功（首次：…）」—— 不掩盖问题，反而证明它发生过。
    /// </summary>
    private async Task<(bool Ok, string Message)> ForwardAsync(PushActivity activity, CancellationToken token)
    {
        var client = _client;
        if (client == null) return (false, "服务未启动");

        string json = NpsActivityEndpoint.BuildJson(activity);

        var (ok, message, retryable) = await PostOnceAsync(client, json, token);
        if (ok) return (true, "");
        if (!retryable) return (false, message);

        // 250ms 足够越过 HTTP.sys 的前缀注册窗口，又不会让手机端等出超时
        try { await Task.Delay(RetryDelayMs, token); }
        catch (OperationCanceledException) { return (false, message); }

        var (okRetry, messageRetry, _) = await PostOnceAsync(client, json, token);
        if (okRetry)
        {
            _log($"转发重试成功（首次失败：{message}）");
            return (true, "");
        }

        return (false, $"两次都失败 —— 首次 {message}；重试 {messageRetry}");
    }

    /// <summary>重试间隔（毫秒）。见 <see cref="ForwardAsync"/> 的说明。</summary>
    private const int RetryDelayMs = 250;

    /// <summary>
    /// 这个状态码是否值得再试一次。
    ///
    /// 只有两类：**404**（HTTP.sys 的 URL 前缀还没注册好，请求没到宿主）与 **5xx**（宿主侧临时故障）。
    /// 其余 4xx 说明**本插件的请求内容**有问题，重试只是把一条真实错误刷成两条。
    /// 抽成静态方法是为了让这条边界能被断言钉住（见 <c>ForwardRetryTests</c>）。
    /// </summary>
    internal static bool ShouldRetryForStatus(int status) => status == 404 || status >= 500;

    /// <summary>
    /// 发一次请求。<see cref="Retry"/> 为 true 表示「这个状态码值得再试一次」。
    /// 失败时把**响应体前 120 字**也带上：宿主 404/500 时正文里通常有原因，
    /// 而日志里只剩一个状态码的话，远程排查等于盲猜。
    /// </summary>
    private async Task<(bool Ok, string Message, bool Retry)> PostOnceAsync(
        HttpClient client, string json, CancellationToken token)
    {
        try
        {
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var response = await client.PostAsync("api/activities/", content, token);
            if (response.IsSuccessStatusCode) return (true, "", false);

            int status = (int)response.StatusCode;

            string body = "";
            try { body = await response.Content.ReadAsStringAsync(token); } catch { /* 读不到就算了 */ }
            body = NpsActivityEndpoint.Sanitize(body);          // 折行/控制字符会刷爆日志
            if (body.Length > 120) body = body[..120] + "…";

            // ⚠️ 记**实际发出的 URI**（不是那个常量）：请求若被代理 / 重定向接走，
            //    只有这里能看出来。常量永远显示「本该是那个地址」，对排查毫无帮助。
            string actual = response.RequestMessage?.RequestUri?.ToString() ?? "(未知)";

            string detail = body.Length > 0
                ? $"（POST {actual} → 响应：{body}）"
                : $"（POST {actual}，响应体为空，HTTP {response.Version}）";

            bool retry = ShouldRetryForStatus(status);
            return (false, $"宿主返回 HTTP {status}{detail}", retry);
        }
        catch (TaskCanceledException) { return (false, "请求宿主超时（灵动岛未响应）", false); }
        catch (HttpRequestException ex) { return (false, $"无法连接灵动岛接口，程序是否在运行？{ex.Message}", false); }
        catch (ObjectDisposedException) { return (false, "服务已停止", false); }
        catch (Exception ex) { return (false, ex.Message, false); }
    }

    // ====================================================================
    // 极简 HTTP/1.1 解析
    // ====================================================================

    private readonly record struct HttpRequest(string Method, string Path, byte[] Body);

    /// <summary>已解析的请求头（请求体尚未读取）。</summary>
    private readonly record struct RequestHead(string Method, string Path, int ContentLength, byte[] Leftover);

    private static async Task<RequestHead> ReadHeadAsync(NetworkStream stream, CancellationToken token)
    {
        var buffer = new byte[4096];
        using var accumulated = new MemoryStream(4096);

        int headerEnd = -1, terminator = 0;
        while (headerEnd < 0)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), token);
            if (read <= 0) throw new IOException("连接在请求头读完前关闭");
            accumulated.Write(buffer, 0, read);
            if (accumulated.Length > 128 * 1024) throw new IOException("请求头过大");
            TryFindHeaderEnd(accumulated.GetBuffer(), (int)accumulated.Length, out headerEnd, out terminator);
        }

        var headerText = Encoding.ASCII.GetString(accumulated.GetBuffer(), 0, headerEnd);
        var lines = headerText.Split('\n');
        var requestLine = lines.Length > 0 ? lines[0].TrimEnd('\r') : "";

        var parts = requestLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string method = parts.Length > 0 ? parts[0].ToUpperInvariant() : "";
        string path = parts.Length > 1 ? parts[1] : "/";

        int contentLength = 0;
        for (int i = 1; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd('\r');
            int colon = line.IndexOf(':');
            if (colon <= 0) continue;
            if (!line.AsSpan(0, colon).Trim().Equals("Content-Length", StringComparison.OrdinalIgnoreCase)) continue;
            int.TryParse(line.AsSpan(colon + 1).Trim(), out contentLength);
        }

        if (contentLength < 0) contentLength = 0;
        if (contentLength > 1024 * 1024) throw new IOException("请求体过大");

        int bodyStart = headerEnd + terminator;
        int leftoverLength = Math.Max(0, (int)accumulated.Length - bodyStart);
        var leftover = new byte[leftoverLength];
        if (leftoverLength > 0) Buffer.BlockCopy(accumulated.GetBuffer(), bodyStart, leftover, 0, leftoverLength);

        return new RequestHead(method, path, contentLength, leftover);
    }

    private static async Task<byte[]> ReadBodyAsync(NetworkStream stream, RequestHead head, CancellationToken token)
    {
        var body = new byte[head.ContentLength];
        int have = Math.Min(body.Length, head.Leftover.Length);
        if (have > 0) Buffer.BlockCopy(head.Leftover, 0, body, 0, have);

        while (have < body.Length)
        {
            int read = await stream.ReadAsync(body.AsMemory(have, body.Length - have), token);
            if (read <= 0) break;
            have += read;
        }
        if (have < body.Length) throw new IOException("请求体不完整");

        return body;
    }

    /// <summary>
    /// 在被拒绝的连接上尽力把请求读完再回应。
    /// TCP 连接只要带着未读数据关闭就会发 RST，客户端便拿不到我们写出的 403；
    /// 这里只读有限长度（256 KB 上限），避免被非白名单来源拖住。
    /// </summary>
    private static async Task DrainRequestAsync(NetworkStream stream, CancellationToken token)
    {
        try
        {
            var head = await ReadHeadAsync(stream, token);
            int remaining = Math.Min(head.ContentLength, 256 * 1024) - head.Leftover.Length;
            var scratch = new byte[8192];
            while (remaining > 0)
            {
                int read = await stream.ReadAsync(scratch.AsMemory(0, Math.Min(scratch.Length, remaining)), token);
                if (read <= 0) break;
                remaining -= read;
            }
        }
        catch { /* 尽力而为：即使读取失败也照常返回 403 */ }
    }

    private static bool TryFindHeaderEnd(byte[] buffer, int length, out int index, out int terminator)
    {
        for (int i = 0; i + 3 < length; i++)
        {
            if (buffer[i] == (byte)'\r' && buffer[i + 1] == (byte)'\n' && buffer[i + 2] == (byte)'\r' && buffer[i + 3] == (byte)'\n')
            {
                index = i; terminator = 4; return true;
            }
        }
        for (int i = 0; i + 1 < length; i++)
        {
            if (buffer[i] == (byte)'\n' && buffer[i + 1] == (byte)'\n')
            {
                index = i; terminator = 2; return true;
            }
        }
        index = -1; terminator = 0; return false;
    }

    private static async Task WriteResponseAsync(NetworkStream stream, int status, string contentType, string body, CancellationToken token)
    {
        var bodyBytes = Encoding.UTF8.GetBytes(body);
        var head = new StringBuilder(192)
            .Append("HTTP/1.1 ").Append(status).Append(' ').Append(ReasonOf(status)).Append("\r\n")
            .Append("Content-Type: ").Append(contentType).Append("\r\n")
            .Append("Content-Length: ").Append(bodyBytes.Length).Append("\r\n")
            .Append("Access-Control-Allow-Origin: *\r\n")
            .Append("Connection: close\r\n\r\n")
            .ToString();

        await stream.WriteAsync(Encoding.ASCII.GetBytes(head), token);
        await stream.WriteAsync(bodyBytes, token);
        await stream.FlushAsync(token);
    }

    private static string ReasonOf(int status) => status switch
    {
        200 => "OK",
        400 => "Bad Request",
        403 => "Forbidden",
        502 => "Bad Gateway",
        _ => "OK",
    };

    /// <summary>
    /// 状态页文本：浏览器打开 http://本机IP:端口/ 即可自检。
    ///
    /// ★ 「状态灯」一行与灵动岛那盏灯**同源**（同一个 <see cref="Unhealthy"/> 经
    /// <see cref="LightStatus.Decide"/> 判定）—— 因此「浏览器里读到的颜色」就等于「岛上灯的颜色」，
    /// 用户不必盯着屏幕角落就能核对灯是否与真实状态一致。
    /// ⚠️ 状态页本身走回环地址：白名单非空时 127.0.0.1 会被 403 挡掉（已知坑），
    ///    此时请改用本机的局域网 IP 打开，或往白名单里补一行 127.0.0.1。
    /// </summary>
    public string BuildStatusPage()
    {
        var state = LightStatus.Decide(IsRunning, _unhealthy);

        var sb = new StringBuilder(512);
        sb.AppendLine("NpsPushBridge — 局域网推送网桥");
        sb.AppendLine($"状态    : {(IsRunning ? "运行中" : "已停止")}");
        sb.AppendLine($"状态灯  : {LightStatus.Describe(state)}");
        sb.AppendLine($"监听端口: {Port}");
        sb.AppendLine($"白名单  : {_whitelist().Describe()}");
        sb.AppendLine($"黑名单  : {_blacklist().Describe()}");
        sb.AppendLine($"已接收  : {Received}  已转发: {Forwarded}  已拦截: {Blocked}  已拒绝: {Rejected}  失败: {Failed}");
        if (_lastFailure.Length > 0)
            sb.AppendLine($"最近失败: {_lastFailure}{(_unhealthy ? "" : "（此后已有消息成功转发，灯已回绿）")}");
        sb.AppendLine();
        sb.AppendLine("推送方式: POST /  Content-Type: application/json");
        sb.AppendLine("          {\"kind\":\"手机消息\",\"title\":\"标题\",\"subtitle\":\"正文\"}");
        sb.AppendLine("          subtitle 亦可写作 body / content / text");
        sb.AppendLine("          icon 可选：发送端可自带，缺失时本插件按 kind 自动补全");
        sb.AppendLine($"          内置图标映射 {IconCatalog.BuiltinCount} 条，可自定义 {_iconOverrides().Count} 条");
        // 别名依赖宿主 data/image 下的散文件，单文件发布版不会带 → 明确写出来，
        // 免得用户看到「QQ 图标没出现」时无处排查
        sb.AppendLine($"          {IconCatalog.DescribeAliasProbe()}");
        // 别名的前提是「宿主目录下真的有那 6 张官方图」。插件启动时会自动补齐，
        // 这里把补齐结果也写出来 —— 「别名可用吗」与「图到底在不在」是两件事，
        // 分开显示才能一眼看出问题出在「目录缺图」还是「别名表没配」。
        sb.AppendLine($"          {HostImageSeeder.DescribeStatus()}");
        return sb.ToString();
    }
}
