using System.IO;
using Microsoft.Win32;

namespace NpsPushBridge;

/// <summary>
/// 清理本插件留在这台机器上的「冗杂残留」。
///
/// ★ 分两个时机、两套范围，绝不混在一起 ★
///   · <see cref="CleanTemp"/> —— **每次关闭**（重载 / 禁用 / 退出宿主 / 卸载都会走到 <c>Dispose</c>）：
///     只清**临时文件**。这些是升级过程产生的下载与解压残留，删了不影响任何已保存的配置。
///   · <see cref="CleanUpgradeBackups"/> + <see cref="CleanSettings"/> —— **只在真正卸载时**：
///     清升级备份与注册表设置。⚠️ 这两项**必须**留在卸载时机：重载与退出也会调 <c>Dispose</c>，
///     若在那里删注册表，用户点一次「重载」就会把全部配置清空。
///
/// ★ 三条安全边界（写死在这里，不接受参数放宽）★
///   1. 临时目录：只认 <c>%TEMP%\NpsPushBridge</c> 这一个子树，且落盘前再校验一次「确实在系统临时目录内」；
///   2. 备份文件：只删**本插件 dll 同名**的 <c>.bak-*</c> / <c>.new</c>，别的插件的文件一个字都不碰；
///   3. 注册表：只在宿主的键下删**带本插件前缀**的值，绝不删键本身
///      （那个键里还有宿主的主题、排序、其他插件的全部设置）。
///
/// 所有方法都**不抛异常**：返回实际清理了多少项，失败只写日志。
/// 清理失败绝不能让「卸载」或「关闭」卡住 —— 那会让用户既不成功也不报错。
/// </summary>
internal static class ResidueCleaner
{
    /// <summary>本插件的 ID —— 注册表值名前缀与临时目录名都以它为准（单一真源）。</summary>
    public const string PluginId = "com.nps.pushbridge";

    /// <summary>宿主存放插件设置与全部配置的注册表键。</summary>
    private const string HostRegistryPath = @"SOFTWARE\NotchPeninsula";

    /// <summary>本插件在宿主键下的值名前缀（宿主 <c>PluginHost.PrefixedKey</c> 的同款拼法）。</summary>
    public const string SettingPrefix = "Plugin." + PluginId + ".";

    /// <summary>本插件的 dll 文件名（升级备份与 staging 都以它为基础名）。</summary>
    private const string DllName = "NpsPushBridge.dll";

    /// <summary>升级过程使用的临时目录根（与 <c>SilentUpgrader</c> 完全同一处）。</summary>
    public static string TempRoot
        => Path.Combine(Path.GetTempPath(), "NpsPushBridge");

    /// <summary>
    /// 删除升级残留的临时目录（<c>%TEMP%\NpsPushBridge</c>）。
    ///
    /// 每次关闭都调 —— 这些文件只在「下载 → 解压 → 替换」这三个瞬间有用，
    /// 流程走完就成了纯垃圾（解压出来的 dll 与宿主目录里那份重复，白白占几 MB）。
    ///
    /// 返回删除的条目数（含子目录里的文件）。
    /// </summary>
    /// <param name="log">日志回调（可为 null）。</param>
    /// <param name="rootOverride">
    /// 仅测试用：覆盖要清理的目录。生产调用一律传 null（= <see cref="TempRoot"/>）。
    /// 有了它，测试才能在不碰真实 <c>%TEMP%\NpsPushBridge</c> 的前提下验证「递归删 + 边界校验」。
    /// ⚠️ 覆盖值仍然要过下面的「必须在系统临时目录内」那道校验。
    /// </param>
    public static int CleanTemp(Action<string>? log = null, string? rootOverride = null)
    {
        string root;
        try
        {
            root = Path.GetFullPath(rootOverride ?? TempRoot);
        }
        catch (Exception ex)
        {
            log?.Invoke($"清理临时目录失败（路径无法规范化）：{ex.Message}");
            return 0;
        }

        // 边界校验：必须在系统临时目录之内。写死这一层是为了万一有人把 TempRoot 改坏，
        // 也不会出现「递归删掉 C:\」这种不可挽回的事故。
        if (!IsInsideDirectory(root, Path.GetTempPath()))
        {
            log?.Invoke($"清理临时目录已跳过：{root} 不在系统临时目录内");
            return 0;
        }

        return DeleteDirectorySafely(root, log);
    }

    /// <summary>
    /// 删除本插件 dll 旁边的升级备份与 staging 文件：
    /// <c>NpsPushBridge.dll.bak-&lt;时间戳&gt;</c>（每次静默升级都会留一个）与 <c>NpsPushBridge.dll.new</c>。
    ///
    /// ⚠️ **只在卸载时调用**：正常运行时那些备份是回滚用的，删掉就等于放弃「升级失败能退回旧版」。
    /// 返回删除的文件数。
    /// </summary>
    /// <param name="directory">dll 所在目录（一般是 <c>&lt;宿主&gt;\plugins</c>）；为 null 时用宿主目录。</param>
    public static int CleanUpgradeBackups(string? directory, Action<string>? log = null)
    {
        string dir;
        try
        {
            dir = Path.GetFullPath(directory ?? AppContext.BaseDirectory);
        }
        catch (Exception ex)
        {
            log?.Invoke($"清理升级备份失败（目录无法规范化）：{ex.Message}");
            return 0;
        }

        if (!Directory.Exists(dir)) return 0;

        int removed = 0;
        try
        {
            // 只认「本插件 dll 名」开头的备份，绝不按 *.bak 通配 ——
            // 同目录下还有别的插件，通配会顺手删掉它们的回滚备份。
            foreach (var path in Directory.EnumerateFiles(dir, DllName + ".*"))
            {
                string name = Path.GetFileName(path);
                bool isBackup = name.StartsWith(DllName + ".bak-", StringComparison.OrdinalIgnoreCase);
                bool isStaging = string.Equals(name, DllName + ".new", StringComparison.OrdinalIgnoreCase);
                if (!isBackup && !isStaging) continue;

                if (TryDeleteFile(path)) removed++;
            }
        }
        catch (Exception ex)
        {
            log?.Invoke($"清理升级备份时出错：{ex.Message}");
        }

        if (removed > 0) log?.Invoke($"已清理 {removed} 个升级备份文件");
        return removed;
    }

    /// <summary>
    /// 删除本插件在宿主注册表键下的**全部设置值**（<c>Plugin.com.nps.pushbridge.*</c>）。
    ///
    /// ⚠️ **只在卸载时调用**。宿主的卸载流程不碰设置（它只有 GetValue / SetValue），
    /// 所以不清理的话，卸载再重装会「带着上次的全部配置回来」，而用户以为已经卸载干净了。
    ///
    /// ⚠️ 只删**值**、不删键：同一个键里还有宿主的主题、插件排序、以及其他插件的设置。
    /// 返回删除的值个数。
    /// </summary>
    public static int CleanSettings(Action<string>? log = null)
    {
        int removed = 0;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(HostRegistryPath, writable: true);
            if (key == null) return 0;

            // 先收集再删：在枚举过程中删值会让枚举器行为不可预期。
            var names = new List<string>();
            foreach (var name in key.GetValueNames())
            {
                if (name.StartsWith(SettingPrefix, StringComparison.OrdinalIgnoreCase))
                    names.Add(name);
            }

            foreach (var name in names)
            {
                try
                {
                    key.DeleteValue(name, throwOnMissingValue: false);
                    removed++;
                }
                catch (Exception ex)
                {
                    log?.Invoke($"删除设置项 {name} 失败：{ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            log?.Invoke($"清理注册表设置失败：{ex.Message}");
        }

        if (removed > 0) log?.Invoke($"已清理 {removed} 项宿主注册表设置");
        return removed;
    }

    // ====================================================================
    // 工具
    // ====================================================================

    /// <summary>
    /// <paramref name="path"/> 是否位于 <paramref name="parent"/> 之内（含相等）。
    /// 比较前两边都规范化并补上目录分隔符，避免 <c>C:\Tempx</c> 被误判成在 <c>C:\Temp</c> 内。
    /// </summary>
    private static bool IsInsideDirectory(string path, string parent)
    {
        try
        {
            var root = Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                       + Path.DirectorySeparatorChar;
            var target = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return target.StartsWith(root, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 递归删除目录，返回删除的条目数（文件 + 子目录）。
    /// 只读文件、被占用的文件都单独 try —— 一个删不掉不该让整轮清理停下。
    /// </summary>
    private static int DeleteDirectorySafely(string dir, Action<string>? log)
    {
        if (!Directory.Exists(dir)) return 0;

        int removed = 0;
        try
        {
            foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                if (TryDeleteFile(file)) removed++;

            // 自底向上删空目录：EnumerateDirectories 的深度顺序不保证，按路径长度倒序最稳
            var dirs = new List<string>(Directory.EnumerateDirectories(dir, "*", SearchOption.AllDirectories));
            dirs.Sort(static (a, b) => b.Length.CompareTo(a.Length));
            foreach (var sub in dirs)
            {
                try { Directory.Delete(sub, recursive: false); removed++; }
                catch { /* 非空 / 被占用：留着，下次再说 */ }
            }

            Directory.Delete(dir, recursive: false);
        }
        catch (Exception ex)
        {
            log?.Invoke($"清理临时目录时出错：{ex.Message}");
        }

        return removed;
    }

    /// <summary>删一个文件；只读属性先摘掉。失败返回 false（不抛）。</summary>
    private static bool TryDeleteFile(string path)
    {
        try
        {
            var attrs = File.GetAttributes(path);
            if (attrs.HasFlag(FileAttributes.ReadOnly))
                File.SetAttributes(path, attrs & ~FileAttributes.ReadOnly);

            File.Delete(path);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
