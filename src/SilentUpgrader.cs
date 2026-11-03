using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.Json;

namespace NpsPushBridge;

/// <summary>一次静默升级尝试的最终结果（供插件侧更新状态与日志）。</summary>
/// <param name="Ok">是否成功（含"已就位待重载"）。</param>
/// <param name="Stage">结束时停在哪一步。</param>
/// <param name="Message">给人看的一句话说明（成功或失败原因）。</param>
/// <param name="RolledBack">是否执行了回退（真 = 备份被盖回去过）。</param>
/// <param name="NewVersion">安装包里的版本号；失败时为空串。</param>
internal readonly record struct UpgradeOutcome(
    bool Ok, UpgradeStage Stage, string Message, bool RolledBack, string NewVersion);

/// <summary>
/// 静默升级的**执行器**：下载安装包 → 校验 → 备份 → 原子替换 → （可选）回退。
///
/// ══ 为什么插件能替换自己的 dll ═══════════════════════════════════════════
/// 宿主 <c>PluginManager</c> 是「影子拷贝」加载的：它把插件目录里的 dll 复制到
/// <c>%TEMP%\NotchPeninsula\plugins\&lt;guid&gt;\</c> 再从那里 LoadFromAssemblyPath。
/// 因此**运行中的进程并不持有插件目录里那个文件的句柄**，我们可以在插件自己的代码里
/// 把 plugins 目录下的 dll 覆盖掉 —— 这正是「无需人工干预完成更新」得以实现的物理基础。
/// 下一次宿主重载 / 重启时，读到的就是新版本。
///
/// ══ 为什么不直接写自己的 dll ═════════════════════════════════════════════
/// 不能想当然地用 <c>Assembly.Location</c>：在影子拷贝模型下它指向的是**临时目录**那一份，
/// 写它只会污染缓存，下次重载又被覆盖回来，表现为"升级成功但版本没变"。
/// 所以这里显式从 <see cref="Environment.ProcessPath"/>（宿主 exe）推出
/// <c>&lt;exe 目录&gt;\plugins\</c>，再按「目录型布局优先」定位真正生效的那个 dll。
/// 宿主侧 <c>PluginManager.GetAppDirectory()</c> 用的是同一套规则（单文件发布时
/// ProcessPath 就是 exe 本身，不是 dotnet 宿主），所以两边看到的目录一定一致。
///
/// ══ 安装包格式（两种形态都接受）═══════════════════════════════════════════
///   · **裸 dll**（2026-09-27 起的主形态）：整个安装包就是一个 <c>NpsPushBridge.dll</c>。
///     导入文档（配置教程 / 更新日志）已内嵌进 dll，再套一层 zip 已无必要；
///     用户市场那边也只放这个 dll。
///   · **zip 包**（历史形态，继续兼容）：根目录下含 <c>NpsPushBridge.dll</c>（必需）
///     与 <c>plugin.json</c>（可选，里面可写 dll 文件名）。
/// 形态按**内容魔数**判定，不看扩展名（下载地址可能压根没有扩展名）。
/// 校验必须做，否则一个 404 的 HTML 页面会被当成"安装包"写下去，直接把插件弄坏。
/// </summary>
internal static class SilentUpgrader
{
    /// <summary>分发包里入口 dll 的文件名（也是 <see cref="plugin.json"/> 的默认值）。</summary>
    public const string DefaultDllName = "NpsPushBridge.dll";

    /// <summary>目录型布局里的清单文件名。</summary>
    public const string LayoutFileName = "plugin.json";

    /// <summary>宿主的插件目录名（相对宿主 exe）。</summary>
    public const string PluginsFolderName = "plugins";

    /// <summary>单个安装包的大小上限：超出直接判为异常，避免磁盘被塞满。</summary>
    public const long MaxPackageBytes = 200L * 1024L * 1024L;

    // ====================================================================
    // 路径解析
    // ====================================================================

    /// <summary>
    /// 宿主 exe 所在目录：单文件发布时 <see cref="Environment.ProcessPath"/> 是 exe 本身，
    /// 但如果是以 <c>dotnet NotchPeninsula.dll</c> 方式启动，ProcessPath 会是 dotnet.exe ——
    /// 这时退回 <see cref="AppContext.BaseDirectory"/>。判据与宿主保持一致（文件名是不是 dotnet）。
    /// </summary>
    public static string ResolveHostDirectory()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exe)
                && !Path.GetFileNameWithoutExtension(exe).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            {
                var dir = Path.GetDirectoryName(exe);
                if (!string.IsNullOrEmpty(dir)) return dir;
            }
        }
        catch { /* 取不到就退回下面的兜底 */ }

        return AppContext.BaseDirectory;
    }

    /// <summary>宿主的插件根目录（<c>&lt;宿主目录&gt;\plugins</c>）。</summary>
    public static string ResolvePluginsRoot() => Path.Combine(ResolveHostDirectory(), PluginsFolderName);

    /// <summary>
    /// 定位**当前正在生效**的那个插件文件/目录。
    ///
    /// 判定顺序（与宿主 PluginManager 的目录型布局识别一致）：
    ///   ① <c>&lt;plugins&gt;\NpsPushBridge\</c> 存在 → 目录型，取其中的入口 dll；
    ///   ② 否则 <c>&lt;plugins&gt;\NpsPushBridge.dll</c> → 单文件型；
    ///   ③ 再否则返回空串（说明当前不是宿主标准布局，静默升级直接放弃，绝不乱写）。
    ///
    /// 只看这两个固定位置、**不做目录扫描**：扫描会在 <c>plugins\</c> 下挑到一个
    /// 用户手工导入时被改名归档的旧 dll（例如 <c>NpsPushBridge_20260920....dll</c>），
    /// 那属于别人的文件，改错地方比不升级糟糕得多。
    /// </summary>
    public static string ResolveInstalledDllPath()
    {
        try
        {
            var root = ResolvePluginsRoot();
            if (!Directory.Exists(root)) return "";

            // ① 目录型布局
            var folder = Path.Combine(root, "NpsPushBridge");
            if (Directory.Exists(folder))
            {
                var layout = TryReadLayoutEntry(folder);
                if (layout.Length == 0) layout = DefaultDllName;

                var candidate = Path.Combine(folder, layout);
                if (File.Exists(candidate)) return candidate;

                // 清单指的文件不存在时，退回「与文件夹同名的 dll」
                var sameName = Path.Combine(folder, "NpsPushBridge.dll");
                if (File.Exists(sameName)) return sameName;
            }

            // ② 单文件布局
            var flat = Path.Combine(root, DefaultDllName);
            if (File.Exists(flat)) return flat;
        }
        catch { /* 任何异常都当作「定位失败」，不升级比乱写安全 */ }

        return "";
    }

    /// <summary>读目录型布局的 plugin.json，取出 dll 文件名（读不到返回空串）。</summary>
    private static string TryReadLayoutEntry(string folder)
    {
        try
        {
            var file = Path.Combine(folder, LayoutFileName);
            if (!File.Exists(file)) return "";

            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return "";

            return doc.RootElement.TryGetProperty("dll", out var el) && el.ValueKind == JsonValueKind.String
                ? (el.GetString() ?? "").Trim()
                : "";
        }
        catch { return ""; }
    }

    // ====================================================================
    // 下载
    // ====================================================================

    /// <summary>
    /// 把安装包下载到临时目录。返回下载成功的文件路径（失败返回空串与原因）。
    /// <paramref name="onProgress"/> 会在下载过程中被反复调用（已收字节, 总字节；总长未知时 total = -1）。
    /// </summary>
    public static async Task<(string Path, string Error)> DownloadAsync(
        string url, string version, IProgress<(long Received, long Total)>? onProgress,
        CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(url)) return ("", "下载地址为空");

        try
        {
            using var client = new HttpClient { Timeout = SilentUpgrade.DownloadTimeout };
            try
            {
                client.DefaultRequestHeaders.UserAgent.ParseAdd("NpsPushBridge-SilentUpgrade/1.0");
            }
            catch { /* 头部不被接受也不影响下载 */ }

            using var response = await client
                .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
                return ("", $"服务器返回 {(int)response.StatusCode} {response.ReasonPhrase}");

            long total = response.Content.Headers.ContentLength ?? -1;
            if (total > MaxPackageBytes)
                return ("", $"安装包过大（{SilentUpgrade.FormatSize(total)}），已中止");

            var dir = Path.Combine(Path.GetTempPath(), "NpsPushBridge", "upgrade");
            Directory.CreateDirectory(dir);
            // 扩展名只为**可读性**（出问题时一眼看出下到的是 dll 还是 zip）：
            // 校验一律按**内容魔数**判形态（见 SniffPackageForm），所以这里猜错也不影响正确性
            // —— 站点完全可能用 `/download.php?id=npspushbridge` 这种没有扩展名的地址。
            var target = Path.Combine(dir, $"NpsPushBridge-{SanitizeVersion(version)}{PackageExtensionOf(url)}");

            long received = 0;
            var buffer = new byte[81920];

            await using (var source = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false))
            await using (var sink = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                int read;
                while ((read = await source.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
                {
                    await sink.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                    received += read;

                    if (received > MaxPackageBytes)
                        return ("", $"安装包超过上限 {SilentUpgrade.FormatSize(MaxPackageBytes)}，已中止");

                    try { onProgress?.Report((received, total)); } catch { /* 进度回调失败不影响下载 */ }
                }
            }

            if (received == 0) return ("", "下载到的文件是空的");

            // 尽快把音量压下来：把总长补成实际收到的字节，界面百分比才能落到 100%
            try { onProgress?.Report((received, received)); } catch { /* 同上 */ }

            return (target, "");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return ("", "下载已取消");
        }
        catch (TaskCanceledException)
        {
            return ("", $"下载超时（{SilentUpgrade.FormatDelay(SilentUpgrade.DownloadTimeout)}）");
        }
        catch (HttpRequestException ex)
        {
            return ("", $"网络错误：{ex.Message}");
        }
        catch (Exception ex)
        {
            return ("", $"下载失败：{ex.GetType().Name} {ex.Message}");
        }
    }

    private static string SanitizeVersion(string version)
    {
        var clean = new string((version ?? "").Where(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_').ToArray());
        return clean.Length == 0 ? "unknown" : clean;
    }

    /// <summary>
    /// 从下载地址推断保存用的扩展名（只为可读性，不参与任何判定）。
    /// 取不到或不像扩展名时退回 <c>.zip</c>（历史默认值）。
    /// </summary>
    private static string PackageExtensionOf(string url)
    {
        try
        {
            var ext = Path.GetExtension(new Uri(url).AbsolutePath);
            if (ext.Length is > 1 and <= 6 && ext.Skip(1).All(char.IsLetterOrDigit)) return ext.ToLowerInvariant();
        }
        catch
        {
            // 地址不合法 ⇒ 用默认值（下载本身会失败并给出更有意义的原因）
        }
        return ".zip";
    }

    // ====================================================================
    // 校验
    // ====================================================================

    /// <summary>校验结果：解包目录（失败时为空串）与错误原因。</summary>
    public readonly record struct VerifyResult(bool Ok, string ExtractDir, string DllName, string Version, string Error);

    /// <summary>
    /// 校验安装包并解压到临时目录。
    ///
    /// 必须逐项确认（任一项不过就整包丢弃，绝不"尽力而为"地写下去）：
    ///   ① 是合法的**安装包** —— 两种形态都接受（按**内容魔数**判，不看扩展名）：
    ///      · **zip 包**（历史形态：根层放 dll / plugin.json / 说明文档）；
    ///      · **裸 dll**（2026-09-27 起的单文件形态：整包就是一个 dll —— 因为配置教程与
    ///        更新日志已内嵌进 dll，再套一层 zip 已无必要，用户市场那边也只放 dll）。
    ///      404 页面 / 半截下载 / 别的什么文件会在这一步被挡住；
    ///   ② 根目录下有入口 dll（裸 dll 形态下由本方法自己拷成约定入口名）；
    ///   ③ 该 dll 能被读成 .NET 程序集，且**确实是本插件**（含有 INotchPlugin 实现且 Id 对得上）
    ///      —— 防止下载到别的插件或一个空壳 dll 覆盖掉自己；
    ///   ④ 读出的版本号**严格大于**当前版本 —— 市场发错包、或者我们自己在 beta 阶段
    ///      把版本号回调到市场版本时，这一条会挡住"用同一个版本覆盖自己"的无意义写入。
    /// </summary>
    public static VerifyResult VerifyAndExtract(string packagePath, string localVersion, string expectedPluginId)
    {
        string extractDir = "";
        try
        {
            if (!File.Exists(packagePath))
                return new VerifyResult(false, "", "", "", "安装包文件不存在");

            // ① 先判形态：**只看内容魔数**。
            //    ⚠️ 不看扩展名 —— 扩展名由下载地址决定，而地址完全由站点决定：
            //    `/download.php?id=npspushbridge` 根本没有扩展名，错误页却可能被命名成 .zip。
            //    魔数是内容本身的证据，与"校验这个包"的目的才一致。
            var form = SniffPackageForm(packagePath);
            if (form == PackageForm.Unknown)
                return new VerifyResult(false, "", "", "",
                    "下载到的文件既不是 zip 安装包也不是 dll（可能是错误页面）");

            extractDir = Path.Combine(Path.GetTempPath(), "NpsPushBridge", "upgrade",
                "x" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(extractDir);

            if (form == PackageForm.BareDll)
            {
                // ★ 单文件形态：把包本身拷成**约定入口名**再往下走 ★
                // 这样"定位入口 dll → 读版本 → 校验插件 Id → 安装"这条链路一个字都不用改，
                // 只多了一段"把包当成内容"的动作。也顺手避免了包文件名带版本号
                // （如 NpsPushBridge-1.5.0.dll）导致后面按约定名找不到文件。
                File.Copy(packagePath, Path.Combine(extractDir, DefaultDllName), true);
            }
            else
            {
                // ② zip 形态：解压（限流：条目数 / 单条目大小，防"解压炸弹"）
                int entries = 0;
                using var archive = ZipFile.OpenRead(packagePath);
                foreach (var entry in archive.Entries)
                {
                    if (++entries > 64) return Fail(extractDir, "安装包条目过多，已拒绝");

                    var name = entry.FullName.Replace('\\', '/');
                    if (name.Contains("..", StringComparison.Ordinal)) return Fail(extractDir, "安装包包含非法路径");

                    // 只解根层的文件：安装包契约就是「根目录下放 dll / plugin.json / 说明文档」
                    if (name.Contains('/') && !name.EndsWith('/')) continue;
                    if (name.EndsWith('/')) continue;

                    var dest = Path.Combine(extractDir, Path.GetFileName(name));
                    entry.ExtractToFile(dest, true);
                }
            }

            // ③ 定位入口 dll：先看 plugin.json，再退回约定文件名
            var dllName = TryReadLayoutEntry(extractDir);
            if (dllName.Length == 0) dllName = DefaultDllName;

            var dllPath = Path.Combine(extractDir, Path.GetFileName(dllName));
            if (!File.Exists(dllPath))
                return Fail(extractDir, $"安装包里没有入口 dll（{dllName}）");

            // ④ 元数据校验：必须真是本插件
            var version = ReadPluginVersion(dllPath);
            if (version.Length == 0)
                return Fail(extractDir, "入口 dll 不是有效的插件程序集");

            if (!ContainsPluginId(dllPath, expectedPluginId))
                return Fail(extractDir, "安装包里的 dll 不是本插件，已拒绝安装");

            // ⑤ 版本必须更高
            if (PluginUpdate.CompareVersion(version, localVersion) <= 0)
                return Fail(extractDir, $"安装包版本 v{version} 不高于当前 v{localVersion}，已跳过");

            return new VerifyResult(true, extractDir, Path.GetFileName(dllName), version, "");
        }
        catch (InvalidDataException)
        {
            // ⚠️ 异常路径同样要清掉半解压的目录：校验失败的包一律不留痕
            TryDeleteDir(extractDir);
            return new VerifyResult(false, "", "", "", "下载到的文件不是有效的 zip 安装包（可能是错误页面）");
        }
        catch (Exception ex)
        {
            TryDeleteDir(extractDir);
            return new VerifyResult(false, "", "", "", $"校验安装包失败：{ex.GetType().Name} {ex.Message}");
        }

        static VerifyResult Fail(string dir, string error)
        {
            TryDeleteDir(dir);
            return new VerifyResult(false, "", "", "", error);
        }
    }

    /// <summary>安装包的形态（由内容魔数判定）。</summary>
    private enum PackageForm
    {
        /// <summary>认不出来 —— 错误页面 / 半截下载 / 别的什么文件。</summary>
        Unknown,

        /// <summary>zip 压缩包（历史形态）。</summary>
        Zip,

        /// <summary>裸 dll（2026-09-27 起的单文件形态）。</summary>
        BareDll,
    }

    /// <summary>
    /// 按**内容魔数**判断包形态：`MZ` = PE（裸 dll）；`PK` = zip。
    ///
    /// zip 的三个签名都要认：`PK\x03\x04`（普通条目）、`PK\x05\x06`（空包 / 只有中央目录）、
    /// `PK\x07\x08`（分卷）—— 只认第一个的话，"空 zip"会被判成"不是安装包"，
    /// 错误提示就会从「包里没有入口 dll」变成「不是 zip 也不是 dll」，让人以为下载坏了。
    /// </summary>
    private static PackageForm SniffPackageForm(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            Span<byte> head = stackalloc byte[4];
            int read = fs.Read(head);
            if (read < 2) return PackageForm.Unknown;

            if (head[0] == (byte)'M' && head[1] == (byte)'Z') return PackageForm.BareDll;

            if (read >= 4 && head[0] == (byte)'P' && head[1] == (byte)'K'
                && (head[2] == 0x03 || head[2] == 0x05 || head[2] == 0x07))
            {
                return PackageForm.Zip;
            }

            return PackageForm.Unknown;
        }
        catch
        {
            return PackageForm.Unknown;
        }
    }

    /// <summary>
    /// 从一个 dll 里读出版本号 —— 纯元数据读取，**不加载、不执行**任何代码。
    ///
    /// 实现要点：<see cref="AssemblyName.GetAssemblyName(string)"/> 只能给出四段式
    /// AssemblyVersion，拿不到 csproj 的 &lt;Version&gt;（它落在 AssemblyInformationalVersion 特性里）。
    /// 而 <see cref="AssemblyName"/> 本身没有任何读自定义特性的 API，所以这里用
    /// <see cref="MetadataReader"/> 直接扫元数据表：找到 Assembly 行上的
    /// AssemblyInformationalVersionAttribute 自定义特性，读它的构造参数（就是版本字符串）。
    ///
    /// 优先 InformationalVersion（本插件的唯一真源），取不到再退回 AssemblyVersion 三段式。
    /// 任何异常都收敛成空串，由调用方判定"这不是一个可用安装包"。
    /// </summary>
    public static string ReadPluginVersion(string dllPath)
    {
        try
        {
            using var stream = new FileStream(dllPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var pe = new PEReader(stream);
            if (!pe.HasMetadata) return "";

            var reader = pe.GetMetadataReader();

            // ① 先在字符串堆里找 InformationalVersion 的值（特性构造参数是 ldstr 进 #US 堆的）
            var info = ReadInformationalVersionFromMetadata(reader);
            var normalized = PluginUpdate.NormalizeVersion(info);
            if (normalized != null) return normalized;

            // ② 退回 AssemblyVersion
            if (reader.IsAssembly)
            {
                var def = reader.GetAssemblyDefinition();
                var v = def.Version;
                return $"{v.Major}.{v.Minor}.{Math.Max(v.Build, 0)}";
            }

            return "";
        }
        catch { return ""; }
    }

    /// <summary>
    /// 在元数据里找 AssemblyInformationalVersionAttribute 的参数值。
    ///
    /// 两条路都试，因为不同编译器 / 不同写法落点不同：
    ///   ① 自定义特性表（CustomAttribute）：最常见的情形 —— 特性构造参数直接存在 blob 里；
    ///   ② 字符串堆扫描兜底：万一特性写法特殊读不出来，退一步在 #US 堆里找
    ///      "看起来像版本号且不等于 AssemblyVersion" 的那一条。
    /// 拿不到就返回空串。
    /// </summary>
    private static string ReadInformationalVersionFromMetadata(MetadataReader reader)
    {
        try
        {
            foreach (var handle in reader.CustomAttributes)
            {
                var attr = reader.GetCustomAttribute(handle);
                if (attr.Parent.Kind != HandleKind.AssemblyDefinition) continue;

                var ctor = attr.Constructor;
                string? typeName = null;

                if (ctor.Kind == HandleKind.MemberReference)
                {
                    var mr = reader.GetMemberReference((MemberReferenceHandle)ctor);
                    if (mr.Parent.Kind == HandleKind.TypeReference)
                    {
                        var tr = reader.GetTypeReference((TypeReferenceHandle)mr.Parent);
                        typeName = reader.GetString(tr.Namespace) + "." + reader.GetString(tr.Name);
                    }
                }
                else if (ctor.Kind == HandleKind.MethodDefinition)
                {
                    var md = reader.GetMethodDefinition((MethodDefinitionHandle)ctor);
                    var td = reader.GetTypeDefinition(md.GetDeclaringType());
                    typeName = reader.GetString(td.Namespace) + "." + reader.GetString(td.Name);
                }

                if (typeName == null
                    || !typeName.EndsWith("AssemblyInformationalVersionAttribute", StringComparison.Ordinal))
                    continue;

                // 构造参数的 blob 布局：prolog(2 字节 0x0001) + 一个 SerString
                var blob = reader.GetBlobReader(attr.Value);
                if (blob.ReadUInt16() != 0x0001) continue;

                var value = blob.ReadSerializedString();
                if (!string.IsNullOrWhiteSpace(value)) return value!;
            }
        }
        catch { /* 读不出来就让调用方退回 AssemblyVersion */ }

        return "";
    }

    /// <summary>
    /// 确认这个 dll **就是本插件**：直接扫它的用户字符串堆，看有没有本插件的 Id 字面量。
    ///
    /// ★ 为什么不反射出 Id 属性的值 ★
    /// 反射要求把 dll 载入进程（污染 + 卸载不掉），或用 MetadataLoadContext（需要一整套
    /// 依赖解析器，插件在影子上下文里跑时还得把宿主目录也喂进去）—— 为了一次校验引入这么多
    /// 会失败的环节不划算。而 <c>Id</c> 在本插件里的写法是编译期常量字符串，
    /// **必然作为字面量存在于元数据的 #US 堆里**，所以「文件里有没有这段字节」就足以判定。
    ///
    /// 这是一个**宽松校验**（假阳性的唯一可能是别的插件的字符串里恰好含这串 Id，
    /// 概率极低；而假阴性为零）。它的作用是把「下载到错误页面 / 别的插件 / 空壳 dll」
    /// 这类最糟糕的情况挡在外面，配合版本号校验已经足够。
    /// </summary>
    public static bool ContainsPluginId(string dllPath, string pluginId)
    {
        if (string.IsNullOrEmpty(pluginId)) return false;

        try
        {
            // 字面量在 #US 堆里是 UTF-16，所以直接按 Unicode 编码搜字节
            var needle = System.Text.Encoding.Unicode.GetBytes(pluginId);
            var haystack = File.ReadAllBytes(dllPath);

            if (needle.Length == 0 || haystack.Length < needle.Length) return false;

            for (int i = 0; i <= haystack.Length - needle.Length; i++)
            {
                int j = 0;
                while (j < needle.Length && haystack[i + j] == needle[j]) j++;
                if (j == needle.Length) return true;
            }
            return false;
        }
        catch { return false; }
    }

    // ====================================================================
    // 安装（备份 → 原子替换）
    // ====================================================================

    /// <summary>安装结果。</summary>
    public readonly record struct InstallResult(bool Ok, string BackupPath, string Error);

    /// <summary>
    /// 把解包出来的 dll 装到目标位置。步骤刻意分成「先备份、再写临时文件、最后原子替换」，
    /// 使任何一步失败都能回退到一份完整的旧文件：
    ///
    ///   ① 备份：<c>NpsPushBridge.dll</c> → <c>NpsPushBridge.dll.bak-&lt;时间戳&gt;</c>
    ///      （同目录，保证跨卷也不会退化成"复制+删除"）；
    ///   ② 写临时：新 dll 先写成 <c>NpsPushBridge.dll.new</c>，写完 flush 到磁盘；
    ///   ③ 原子替换：用 <see cref="File.Replace(string,string,string?)"/> 一次完成
    ///      「旧 → 备份、新 → 正式」，在 NTFS 上是原子的；不支持时退回 Move 覆盖。
    /// </summary>
    public static InstallResult Install(string sourceDll, string targetDll)
    {
        try
        {
            var dir = Path.GetDirectoryName(targetDll);
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
                return new InstallResult(false, "", "插件目录不存在，无法安装");

            var stamp = DateTime.Now.ToString("yyyyMMddHHmmss");
            var backup = targetDll + $".bak-{stamp}";
            var staged = targetDll + ".new";

            // ① 备份旧版本（旧文件不存在时是首次安装，允许没有备份）
            bool hadOld = File.Exists(targetDll);
            if (hadOld) File.Copy(targetDll, backup, true);

            // ② 写临时文件并落盘
            File.Copy(sourceDll, staged, true);
            try
            {
                using var fs = new FileStream(staged, FileMode.Open, FileAccess.Write, FileShare.None);
                fs.Flush(true);
            }
            catch { /* Flush(true) 在个别文件系统上不可用，从流已经关闭同理，忽略 */ }

            // ③ 原子替换
            if (hadOld)
            {
                try { File.Replace(staged, targetDll, null); }
                catch { File.Move(staged, targetDll, true); }   // 跨卷 / 非 NTFS 时退回覆盖
            }
            else
            {
                File.Move(staged, targetDll, true);
            }

            TryDelete(staged);
            return new InstallResult(true, hadOld ? backup : "", "");
        }
        catch (Exception ex)
        {
            TryDelete(targetDll + ".new");
            return new InstallResult(false, "", $"写入插件文件失败：{ex.GetType().Name} {ex.Message}");
        }
    }

    /// <summary>
    /// 回退：把备份盖回目标位置。只在<a>替换阶段失败</a>时调用（见 <see cref="SilentUpgrade.ShouldRollback"/>）。
    /// 成功返回 true（此时插件目录里是一个完整可用的旧版本）。
    /// </summary>
    public static bool Rollback(string backupPath, string targetDll)
    {
        try
        {
            if (string.IsNullOrEmpty(backupPath) || !File.Exists(backupPath)) return false;

            var staged = targetDll + ".rollback";
            File.Copy(backupPath, staged, true);

            if (File.Exists(targetDll))
            {
                try { File.Replace(staged, targetDll, null); }
                catch { File.Move(staged, targetDll, true); }
            }
            else
            {
                File.Move(staged, targetDll, true);
            }

            TryDelete(staged);
            return true;
        }
        catch { return false; }
    }

    /// <summary>清理临时目录里的一次升级残留（解包目录 / 下载的包）。</summary>
    public static void CleanupStaging(string extractDir, string packagePath = "")
    {
        TryDeleteDir(extractDir);
        if (packagePath.Length > 0) TryDelete(packagePath);
    }

    private static void TryDelete(string file)
    {
        try { if (File.Exists(file)) File.Delete(file); } catch { /* 占用中留待下次清理 */ }
    }

    private static void TryDeleteDir(string? dir)
    {
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;
        try { Directory.Delete(dir, true); } catch { /* 同上 */ }
    }
}
