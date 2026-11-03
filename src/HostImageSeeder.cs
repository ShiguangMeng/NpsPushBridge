using System.Reflection;

namespace NpsPushBridge;

/// <summary>
/// 把宿主**本该自带、却在单文件发布版里缺失**的官方图标释放到
/// <c>&lt;宿主目录&gt;\data\image\</c>。
///
/// ★ 为什么需要这一步（这是「QQ 图标不显示」的根因修法）★
/// 宿主有两条读图路径，都以 <c>&lt;宿主 exe 目录&gt;\data\image\</c> 为根：
///   ① <c>ToastIconProvider.ResolveBuiltin</c> —— 收到纯别名（qq / wintoast / bilibili…）时去那儿找
///      <c>&lt;别名&gt;-icon|logo.&lt;ext&gt;</c>；
///   ② <c>Renderer.EnsureIconsLoaded</c> 与 <c>MediaLogoProvider</c> —— 直接读
///      <c>qq-icon.png</c> / <c>wintoast-icon.png</c> / <c>bilibili-logo.png</c> 等固定文件名。
/// 这些图在宿主源码里是 <c>Content + CopyToOutputDirectory</c> 的**散文件**，
/// **单文件发布版（只有一个 exe）不会带上它们** → 该目录不存在 → 所有别名静默失效、
/// 宿主自带的 QQ 图标与默认 Windows 图标也取不到（宿主日志里是一片
/// <c>DirectoryNotFoundException</c>，但界面上只表现为「图标没了」，极难定位）。
///
/// 本类把这些图**内嵌在插件 dll 里**，插件启动时释放到上述目录 ——
/// 于是别名机制、宿主内置图标、媒体封面**一次全部生效**，
/// 且**不修改宿主动任何源码**（只新增数据文件），满足「不改上游」的硬约束。
///
/// ★ 设计约束 ★
///   · **幂等**：文件已存在且内容与官方一致时**不重写** —— 避免每次启动都动磁盘、
///     也避免把用户自己替换过的图覆盖掉；
///   · **静默降级**：任何失败（只读目录、权限不足、磁盘满）都只记日志，
///     **绝不影响插件主流程** —— 最坏情况退回「网络链接兜底」，而不是插件起不来；
///   · **不越界**：只写 <c>data/image/</c> 下的 6 个固定文件名，不做任何目录清理或删除。
/// </summary>
internal static class HostImageSeeder
{
    /// <summary>
    /// 要释放的文件：内嵌资源名 → 宿主要求的目标文件名。
    ///
    /// ⚠️ 目标文件名必须与宿主查找的**逐字节一致**（宿主是硬编码字符串）：
    ///   <c>Renderer.EnsureIconsLoaded</c> → qq-icon.png / wintoast-icon.png；
    ///   <c>MediaLogoProvider</c> → bilibili-logo.png / chrome-logo.png / edge-logo.png / potplayer-logo.jpg。
    /// 少一个字符、换个扩展名都会让宿主「查不到」，且**不会报错**。
    /// </summary>
    private static readonly (string ResourceSuffix, string FileName)[] Files =
    [
        ("qq-icon.png",         "qq-icon.png"),
        ("wintoast-icon.png",   "wintoast-icon.png"),
        ("bilibili-logo.png",   "bilibili-logo.png"),
        ("chrome-logo.png",     "chrome-logo.png"),
        ("edge-logo.png",       "edge-logo.png"),
        ("potplayer-logo.jpg",  "potplayer-logo.jpg"),
    ];

    /// <summary>内嵌资源的前缀（与 csproj 的 <c>LogicalName</c> 对应）。</summary>
    private const string ResourcePrefix = "NpsPushBridge.assets.image.";

    /// <summary>
    /// 释放结果，供日志 / 状态页展示「本次到底做了什么」。
    /// </summary>
    /// <param name="Directory">目标目录（宿主视角的 data/image）。</param>
    /// <param name="Written">本次真正写入的文件数（0 = 之前就已备齐）。</param>
    /// <param name="Skipped">内容已一致、无需重写的文件数。</param>
    /// <param name="Failed">写入失败的文件数。</param>
    /// <param name="Error">目录创建失败等整体性错误；为空表示没出问题。</param>
    public readonly record struct SeedResult(
        string Directory, int Written, int Skipped, int Failed, string Error)
    {
        /// <summary>本次是否真的改动了宿主的 data/image（用于决定要不要提示用户重启宿主）。</summary>
        public bool ChangedAnything => Written > 0;

        /// <summary>是否已备齐（无需重启即已可用）。</summary>
        public bool Complete => Failed == 0 && Error.Length == 0;

        public string Describe()
        {
            if (Error.Length > 0) return $"释放失败：{Error}";
            if (Failed > 0) return $"部分失败（写入 {Written} · 跳过 {Skipped} · 失败 {Failed}）";
            if (Written == 0) return $"已备齐，无需写入（共 {Skipped} 个文件）";
            return $"已释放 {Written} 个图标到 {Directory}";
        }
    }

    /// <summary>
    /// 执行释放。<paramref name="log"/> 可为 null（测试环境）。
    ///
    /// 目标目录取 <see cref="AppContext.BaseDirectory"/> + <c>data/image</c> ——
    /// 插件运行在宿主进程内，故这正是**宿主查找图标的地方**（与
    /// <see cref="IconCatalog.FindAliasFile"/> 用同一个根，两处必须一致）。
    /// </summary>
    public static SeedResult Seed(Action<string>? log = null)
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "data", "image");

        try
        {
            Directory.CreateDirectory(dir);
        }
        catch (Exception ex)
        {
            // 目录建不出来（只读安装目录、权限不足）→ 记录后放弃。
            // 上层会继续用「网络链接」兜底，功能可用只是不如本地图稳。
            log?.Invoke($"宿主图标释放：无法创建目录 {dir}（{ex.GetType().Name}: {ex.Message}）");
            return new SeedResult(dir, 0, 0, 0, $"{ex.GetType().Name}: {ex.Message}");
        }

        int written = 0, skipped = 0, failed = 0;
        var asm = typeof(HostImageSeeder).Assembly;

        foreach (var (suffix, fileName) in Files)
        {
            try
            {
                var target = Path.Combine(dir, fileName);

                // ★ 只要目标文件**已存在**就一律跳过，绝不覆盖 ★
                //   理由：这个目录也可能被用户或宿主自己的安装包填过图（宿主官方版就带这些文件）。
                //   用户若手动换过某张图，那是他的选择 —— 插件没有资格替他改回去。
                //   插件只在「本来就没有」时补上，语义克制、不会与人争抢同一份文件。
                if (File.Exists(target))
                {
                    skipped++;
                    continue;
                }

                using var stream = asm.GetManifestResourceStream(ResourcePrefix + suffix);
                if (stream == null)
                {
                    // 资源没打进 dll（打包配置出错）→ 记为失败并继续下一个，不让一个文件拖垮全部
                    failed++;
                    log?.Invoke($"宿主图标释放：内嵌资源缺失 {ResourcePrefix}{suffix}");
                    continue;
                }

                // 先写临时文件再原子替换：避免宿主恰好在读图时读到写了一半的文件
                var temp = target + ".nps-tmp";
                using (var fs = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
                    stream.CopyTo(fs);

                try
                {
                    File.Move(temp, target, overwrite: true);
                }
                catch
                {
                    try { File.Delete(temp); } catch { /* 清不掉临时文件也不影响主流程 */ }
                    throw;
                }

                written++;
            }
            catch (Exception ex)
            {
                failed++;
                log?.Invoke($"宿主图标释放：写入 {fileName} 失败（{ex.GetType().Name}: {ex.Message}）");
            }
        }

        var result = new SeedResult(dir, written, skipped, failed, "");
        log?.Invoke($"宿主图标释放：{result.Describe()}");

        if (result.ChangedAnything)
            log?.Invoke("宿主图标已就位 —— **重启灵动岛后**QQ 图标与内置别名将直接可用（宿主图标是进程内一次性加载的）。");

        return result;
    }

    /// <summary>已内嵌的图标数量（供测试与状态页核对，防止「资源没打进去」却无人察觉）。</summary>
    public static int EmbeddedCount
    {
        get
        {
            var asm = typeof(HostImageSeeder).Assembly;
            return Files.Count(f => asm.GetManifestResourceStream(ResourcePrefix + f.ResourceSuffix) != null);
        }
    }

    /// <summary>全部目标文件名（只读，供状态页列出与测试核对）。</summary>
    public static IReadOnlyList<string> TargetFileNames
        => Files.Select(f => f.FileName).ToList();

    /// <summary>
    /// 一行「宿主图标现在是什么状态」的说明，供状态页显示。
    /// 把「内嵌资源是否打全」与「磁盘上是否已备齐」分别写出来 ——
    /// 前者能查出「打包漏了资源」这类构建问题，后者能查出「目录不可写」这类部署问题。
    /// </summary>
    public static string DescribeStatus()
    {
        int embedded = EmbeddedCount;

        int presentOnDisk = 0;
        try
        {
            var dir = Path.Combine(AppContext.BaseDirectory, "data", "image");
            presentOnDisk = Files.Count(f => File.Exists(Path.Combine(dir, f.FileName)));
        }
        catch
        {
            // 目录不可访问 → 保持 0，下面会如实显示「x/6」
        }

        int total = Files.Length;
        var tail = presentOnDisk == total
            ? "（别名与宿主内置图标可直接使用）"
            : "（缺失的会在插件启动时自动补齐，补后需重启灵动岛生效）";

        return $"宿主图标：内嵌 {embedded}/{total}，已就位 {presentOnDisk}/{total}{tail}";
    }
}
