using NpsPushBridge;

namespace BridgeHarness;

/// <summary>
/// <see cref="ResidueCleaner"/>（卸载/关闭时清理残留）的验证。
///
/// ★ 这一组的重点是「不误删」而不是「能删掉」★
/// 清理器一旦越界，后果是不可逆的：多删一个注册表值就丢了用户配置，多删一个文件就可能毁掉
/// 别的插件的回滚备份。所以每条断言都在守一个边界：
///   · 临时目录必须落在系统临时目录内才动手；
///   · 备份只认**本插件 dll 同名**的 <c>.bak-*</c> / <c>.new</c>，同目录下别人的文件必须原样留着；
///   · 注册表只按本插件前缀删**值**，绝不删键。
/// 测试一律使用临时目录里的假数据，不触碰真实的 <c>%TEMP%\NpsPushBridge</c> 与用户注册表。
/// </summary>
internal static class ResidueCleanerTests
{
    private static int _passed, _failed;

    private static void Check(string name, bool ok, string detail = "")
    {
        if (ok) { _passed++; Console.WriteLine($"  [PASS] {name}"); }
        else { _failed++; Console.WriteLine($"  [FAIL] {name}{(detail.Length > 0 ? "  —— " + detail : "")}"); }
    }

    public static (int Passed, int Failed) Run()
    {
        Console.WriteLine("【残留清理：范围与边界】");

        ConstantSection();
        TempSection();
        BackupSection();
        RegistryScopeSection();

        return (_passed, _failed);
    }

    private static void ConstantSection()
    {
        Check("设置前缀与宿主拼法一致（Plugin.<id>.）",
            ResidueCleaner.SettingPrefix == "Plugin.com.nps.pushbridge.",
            ResidueCleaner.SettingPrefix);

        Check("临时目录在系统临时目录之内",
            ResidueCleaner.TempRoot.StartsWith(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase),
            ResidueCleaner.TempRoot);
    }

    // ── 临时目录：删得掉、且不越界 ──────────────────────────────────────

    private static void TempSection()
    {
        string root = Path.Combine(Path.GetTempPath(), "NpsPushBridgeTest", Guid.NewGuid().ToString("N"));

        try
        {
            Directory.CreateDirectory(Path.Combine(root, "upgrade", "pkg"));
            File.WriteAllText(Path.Combine(root, "upgrade", "pkg", "a.txt"), "x");
            File.WriteAllText(Path.Combine(root, "top.bin"), "y");

            int removed = ResidueCleaner.CleanTemp(rootOverride: root);

            Check("★ 递归删干净：文件与子目录都不留", !Directory.Exists(root),
                $"目录仍存在；删除 {removed} 项");
            Check("返回值计入了实际删除的条目", removed >= 3, $"实际 {removed}");
        }
        finally
        {
            try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch { }
        }

        Check("目录不存在时返回 0（幂等，不抛）",
            ResidueCleaner.CleanTemp(rootOverride: Path.Combine(Path.GetTempPath(), "NpsPushBridgeTest", "nope-" + Guid.NewGuid().ToString("N"))) == 0);

        // ★ 边界：指向系统临时目录的**父目录**必须被拒绝 ★
        string parent = Path.GetDirectoryName(Path.GetTempPath().TrimEnd('\\', '/')) ?? @"C:\";
        Check("★ 越界路径被拒（父目录不在临时目录内 → 一个都不删）",
            ResidueCleaner.CleanTemp(rootOverride: parent) == 0, parent);
    }

    // ── 升级备份：只删本插件的，别人的必须留着 ──────────────────────────

    private static void BackupSection()
    {
        string dir = Path.Combine(Path.GetTempPath(), "NpsPushBridgeTest", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);

        try
        {
            File.WriteAllText(Path.Combine(dir, "NpsPushBridge.dll"), "running");
            File.WriteAllText(Path.Combine(dir, "NpsPushBridge.dll.bak-20261017120000"), "old1");
            File.WriteAllText(Path.Combine(dir, "NpsPushBridge.dll.bak-20261017130000"), "old2");
            File.WriteAllText(Path.Combine(dir, "NpsPushBridge.dll.new"), "staged");
            // 这些是「别人的东西」—— 一个都不许动
            File.WriteAllText(Path.Combine(dir, "NpsMediaEnhancement.dll.bak-1"), "other");
            File.WriteAllText(Path.Combine(dir, "NpsPushBridge.dll.keep"), "unrelated-ext");
            File.WriteAllText(Path.Combine(dir, "OtherPlugin.dll"), "other2");

            int removed = ResidueCleaner.CleanUpgradeBackups(dir);

            Check("★ 删掉 2 个 .bak-* 与 1 个 .new", removed == 3, $"实际 {removed}");
            Check("★ 正在用的 dll 本体不能删",
                File.Exists(Path.Combine(dir, "NpsPushBridge.dll")));
            Check("★★ 其他插件的备份必须原样留着",
                File.Exists(Path.Combine(dir, "NpsMediaEnhancement.dll.bak-1")));
            Check("★ 非备份后缀（.keep）不碰",
                File.Exists(Path.Combine(dir, "NpsPushBridge.dll.keep")));
            Check("★ 同目录其他插件文件不碰",
                File.Exists(Path.Combine(dir, "OtherPlugin.dll")));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }

        Check("目录不存在时返回 0",
            ResidueCleaner.CleanUpgradeBackups(Path.Combine(Path.GetTempPath(), "NpsPushBridgeTest", "nope-" + Guid.NewGuid().ToString("N"))) == 0);
    }

    // ── 注册表：只按前缀删值，键本身绝不动 ──────────────────────────────

    private static void RegistryScopeSection()
    {
        // 只做「命名空间」的静态校验：真删用户注册表不是测试该做的事（会污染真实配置）。
        var prefix = ResidueCleaner.SettingPrefix;

        Check("★ 前缀判定：本插件自己的键命中",
            "Plugin.com.nps.pushbridge.whitelist".StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

        Check("★ 前缀判定：其他插件的键不命中",
            !"Plugin.com.nps.mediamixer.enabled".StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            && !"Plugin.nansai.nps.sysmonitor.net".StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

        Check("★ 前缀判定：宿主的自有键（无 Plugin. 前缀）不命中",
            !"Plugins_Order".StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            && !"Custom_BaseH".StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

        Check("★ 前缀判定：相似但不相同的前缀不命中（pushbridge2 属于别的插件）",
            !"Plugin.com.nps.pushbridge2.port".StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }
}
