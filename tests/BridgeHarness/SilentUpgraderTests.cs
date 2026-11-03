using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using NpsPushBridge;

namespace BridgeHarness;

/// <summary>
/// 静默升级**执行器**的端到端验证：用真实临时文件跑通
/// 「校验 → 解压 → 备份 → 原子替换 → 回退」，全部离线（不依赖外网）。
///
/// ★ 为什么这组用例特别重要 ★
/// 这是唯一会**真的动用户磁盘上的插件 dll** 的功能。<see cref="SilentUpgradeTests"/>
/// 只验证了"什么时候该做"，这里必须验证"做的时候不会把东西弄坏"：
///   · 垃圾包（404 页面 / 空文件 / 非 zip）必须被挡在替换之前；
///   · 不是本插件的 dll、版本不比当前高的包，必须拒绝；
///   · 替换成功后文件内容必须逐字节等于包内 dll；
///   · 回退必须能把备份盖回来；
///   · 目标不可写时旧文件必须完好、不能抛异常。
///
/// 测试用的"假插件 dll"由 <see cref="FakeAssembly"/> 用 <c>MetadataBuilder</c> 现拼一个
/// **最小但合法**的 .NET 程序集：校验逻辑读的是真实元数据
/// （<c>AssemblyInformationalVersion</c> 特性 + <c>#US</c> 堆里的 Id 字面量），
/// 只有真正写出这两个结构，才验证得了 <see cref="SilentUpgrader"/> 的读取路径。
/// （★ 不用 csc 现场编译：沙箱禁止调用编译器，而且没必要 —— 我们只需要这两个元数据结构。）
/// </summary>
internal static class SilentUpgraderTests
{
    public static (int Passed, int Failed) Run()
    {
        Console.WriteLine();
        Console.WriteLine("【静默升级执行器（真实文件端到端）】");

        int passed = 0, failed = 0;

        void Check(string name, bool condition, string detail = "")
        {
            if (condition)
            {
                passed++;
                Console.WriteLine($"  [PASS] {name}");
            }
            else
            {
                failed++;
                Console.WriteLine($"  [FAIL] {name}{(detail.Length == 0 ? "" : $"  →  {detail}")}");
            }
        }

        var sandbox = Path.Combine(Path.GetTempPath(), "NpsPushBridge.Test", Guid.NewGuid().ToString("N")[..8]);
        var pluginsRoot = Path.Combine(sandbox, "plugins");
        Directory.CreateDirectory(pluginsRoot);

        try
        {
            // ── 0. 先造三个不同版本 / 不同 Id 的假插件 dll ────────────
            var dllV12 = MakeFakeDll(sandbox, "1.2.0", "com.nps.pushbridge", "v12");
            var dllV13 = MakeFakeDll(sandbox, "1.3.0", "com.nps.pushbridge", "v13");
            var dllOther = MakeFakeDll(sandbox, "9.9.9", "com.example.other", "other");

            Check("假插件 dll 生成成功（v1.2 / v1.3 / 别的插件各一份）",
                dllV12.Length > 0 && dllV13.Length > 0 && dllOther.Length > 0,
                $"v1.2={dllV12.Length} v1.3={dllV13.Length} other={dllOther.Length}");

            if (dllV12.Length == 0 || dllV13.Length == 0 || dllOther.Length == 0)
                return (passed, failed);   // 前提不成立，后面的用例没有意义

            var installedBytes = File.ReadAllBytes(dllV12);
            var newBytes = File.ReadAllBytes(dllV13);

            // ── 1. 路径定位 ───────────────────────────────────────────
            Check("插件根目录就是 <宿主目录>\\plugins",
                SilentUpgrader.ResolvePluginsRoot().EndsWith("plugins", StringComparison.OrdinalIgnoreCase),
                SilentUpgrader.ResolvePluginsRoot());
            Check("宿主目录解析出非空路径", SilentUpgrader.ResolveHostDirectory().Length > 0);

            var realProbe = SilentUpgrader.ResolveInstalledDllPath();
            Check("定位结果要么为空串、要么指向真实存在的文件",
                realProbe.Length == 0 || File.Exists(realProbe), realProbe);

            // ── 2. 版本与 Id 读取（真实元数据）──────────────────────────
            Check("从真实 dll 读出 v1.2.0",
                SilentUpgrader.ReadPluginVersion(dllV12) == "1.2.0",
                SilentUpgrader.ReadPluginVersion(dllV12));
            Check("从真实 dll 读出 v1.3.0",
                SilentUpgrader.ReadPluginVersion(dllV13) == "1.3.0",
                SilentUpgrader.ReadPluginVersion(dllV13));
            Check("识别 dll 里含本插件 Id",
                SilentUpgrader.ContainsPluginId(dllV12, "com.nps.pushbridge"));
            Check("不误判：别的插件的 Id 不被认出",
                !SilentUpgrader.ContainsPluginId(dllV12, "com.example.other"));
            Check("空 Id 一律返回 false（不做无意义的全真判定）",
                !SilentUpgrader.ContainsPluginId(dllV12, ""));

            var notAssembly = Path.Combine(sandbox, "not-an-assembly.dll");
            File.WriteAllText(notAssembly, "这不是一个程序集");
            Check("非程序集文件读版本 → 空串", SilentUpgrader.ReadPluginVersion(notAssembly).Length == 0);

            // ── 3. 包校验：各种垃圾包都必须被拒 ────────────────────────
            var goodZip = Path.Combine(sandbox, "good.zip");
            MakePackage(goodZip, dllV13);

            var notZip = Path.Combine(sandbox, "page.html");
            File.WriteAllText(notZip, "<!DOCTYPE html><html><body>404 Not Found</body></html>");
            var r = SilentUpgrader.VerifyAndExtract(notZip, "1.2.0", "com.nps.pushbridge");
            Check("HTML 错误页被拒（不是有效 zip）", !r.Ok, r.Error);
            Check("被拒后不留下解包目录", r.ExtractDir.Length == 0, r.ExtractDir);

            var emptyZip = Path.Combine(sandbox, "empty.zip");
            using (var z = ZipFile.Open(emptyZip, ZipArchiveMode.Create)) { /* 空包 */ }
            var r2 = SilentUpgrader.VerifyAndExtract(emptyZip, "1.2.0", "com.nps.pushbridge");
            Check("空 zip 被拒（没有入口 dll）", !r2.Ok, r2.Error);

            var missingDll = Path.Combine(sandbox, "missing.zip");
            using (var z = ZipFile.Open(missingDll, ZipArchiveMode.Create))
            {
                var e = z.CreateEntry("README.md");
                using var w = new StreamWriter(e.Open());
                w.Write("只有文档，没有 dll");
            }
            var r3 = SilentUpgrader.VerifyAndExtract(missingDll, "1.2.0", "com.nps.pushbridge");
            Check("没有入口 dll 的包被拒", !r3.Ok, r3.Error);

            var otherPlugin = Path.Combine(sandbox, "other.zip");
            MakePackage(otherPlugin, dllOther);
            var r4 = SilentUpgrader.VerifyAndExtract(otherPlugin, "1.2.0", "com.nps.pushbridge");
            Check("别的插件的包被拒（Id 不匹配）", !r4.Ok, r4.Error);

            var sameVersion = Path.Combine(sandbox, "same.zip");
            MakePackage(sameVersion, dllV12);
            var r5 = SilentUpgrader.VerifyAndExtract(sameVersion, "1.2.0", "com.nps.pushbridge");
            Check("版本不高于当前的包被拒（不做无意义覆盖）", !r5.Ok, r5.Error);

            var notExists = SilentUpgrader.VerifyAndExtract(
                Path.Combine(sandbox, "no-such-file.zip"), "1.2.0", "com.nps.pushbridge");
            Check("不存在的包被拒", !notExists.Ok, notExists.Error);

            var trashDll = Path.Combine(sandbox, "trash.zip");
            using (var z = ZipFile.Open(trashDll, ZipArchiveMode.Create))
            {
                var e = z.CreateEntry(SilentUpgrader.DefaultDllName);
                using var w = new StreamWriter(e.Open());
                w.Write("我不是 dll");
            }
            var r6 = SilentUpgrader.VerifyAndExtract(trashDll, "1.2.0", "com.nps.pushbridge");
            Check("包内 dll 不是有效程序集 → 被拒", !r6.Ok, r6.Error);

            // ── 3.5 裸 dll 包（2026-09-27 起「只发 dll」的形态）────────
            //    形态按**内容魔数**判定，所以这里刻意让文件名/扩展名五花八门 ——
            //    单一形态一旦被绕过，用户的插件目录会被一个垃圾文件覆盖掉。
            var barePkg = Path.Combine(sandbox, "NpsPushBridge-1.3.0.dll");
            File.Copy(dllV13, barePkg, true);
            var rb = SilentUpgrader.VerifyAndExtract(barePkg, "1.2.0", "com.nps.pushbridge");
            Check("裸 dll 包通过校验（单文件分发形态）", rb.Ok, rb.Error);
            Check("裸 dll 包：版本与入口名都正确",
                rb.Ok && rb.Version == "1.3.0" && rb.DllName == SilentUpgrader.DefaultDllName,
                $"v={rb.Version} dll={rb.DllName}");
            Check("裸 dll 包：已按约定入口名落盘到解包目录",
                rb.Ok && File.Exists(Path.Combine(rb.ExtractDir, SilentUpgrader.DefaultDllName)),
                rb.ExtractDir);

            // 换成裸 dll 之后，原有的每一条「拒绝理由」都仍须成立
            var bareSame = Path.Combine(sandbox, "NpsPushBridge-1.2.0.dll");
            File.Copy(dllV12, bareSame, true);
            Check("裸 dll 版本不高于当前 → 被拒（单文件形态不能绕过版本判定）",
                !SilentUpgrader.VerifyAndExtract(bareSame, "1.2.0", "com.nps.pushbridge").Ok);

            var bareOther = Path.Combine(sandbox, "NpsPushBridge-9.9.9.dll");
            File.Copy(dllOther, bareOther, true);
            Check("裸 dll 是别的插件 → 被拒（单文件形态不能绕过插件 Id 校验）",
                !SilentUpgrader.VerifyAndExtract(bareOther, "1.2.0", "com.nps.pushbridge").Ok);

            // ⚠️「MZ 开头」≠「是插件」：魔数只负责**分流**，能不能装仍由元数据校验说了算。
            //    少了这条，一个精心伪造的 10 字节文件就能走进安装流程。
            var fakePe = Path.Combine(sandbox, "fake-pe.dll");
            File.WriteAllBytes(fakePe, new byte[] { 0x4D, 0x5A, 0x90, 0x00, 0x03, 0x00, 0x00, 0x00, 0x04, 0x00 });
            Check("MZ 开头但不是有效程序集 → 被拒（魔数不当作可信证据）",
                !SilentUpgrader.VerifyAndExtract(fakePe, "1.2.0", "com.nps.pushbridge").Ok);

            // 站点常用 /download.php?id=… 这类**没有扩展名**的地址 —— 形态判定不看扩展名
            var noExt = Path.Combine(sandbox, "download");
            File.Copy(dllV13, noExt, true);
            Check("包没有扩展名也照样被识别为裸 dll",
                SilentUpgrader.VerifyAndExtract(noExt, "1.2.0", "com.nps.pushbridge").Ok);

            // 纯文本（错误页 / 半截下载）既不是 zip 也不是 dll
            var notPackage = Path.Combine(sandbox, "notepad.txt");
            File.WriteAllText(notPackage, "hello world");
            Check("既不是 zip 也不是 dll 的文件 → 被拒",
                !SilentUpgrader.VerifyAndExtract(notPackage, "1.2.0", "com.nps.pushbridge").Ok);

            // ── 4. 合法包通过并解压 ───────────────────────────────────
            var ok = SilentUpgrader.VerifyAndExtract(goodZip, "1.2.0", "com.nps.pushbridge");
            Check("合法包通过校验", ok.Ok, ok.Error);
            Check("解出的包内版本 = 1.3.0", ok.Version == "1.3.0", ok.Version);
            Check("解出的入口 dll 名 = NpsPushBridge.dll",
                ok.DllName == SilentUpgrader.DefaultDllName, ok.DllName);
            Check("解包目录里确实有入口 dll",
                ok.Ok && File.Exists(Path.Combine(ok.ExtractDir, ok.DllName)));

            // ── 5. 安装：备份 + 原子替换 ──────────────────────────────
            var newDll = Path.Combine(ok.ExtractDir, ok.DllName);

            var installTarget = Path.Combine(pluginsRoot, "install-test.dll");
            File.WriteAllBytes(installTarget, installedBytes);

            var install = SilentUpgrader.Install(newDll, installTarget);
            Check("安装成功", install.Ok, install.Error);
            Check("安装后目标文件内容 = 包内 dll（逐字节）",
                install.Ok && File.ReadAllBytes(installTarget).SequenceEqual(newBytes));
            Check("安装时产生了备份文件", install.BackupPath.Length > 0 && File.Exists(install.BackupPath),
                install.BackupPath);
            Check("备份内容 = 替换前的旧 dll（逐字节）",
                install.BackupPath.Length > 0
                && File.ReadAllBytes(install.BackupPath).SequenceEqual(installedBytes));
            Check("安装后不留下 .new 临时文件", !File.Exists(installTarget + ".new"));

            // ── 6. 回退：备份盖回去 ──────────────────────────────────
            var rolled = SilentUpgrader.Rollback(install.BackupPath, installTarget);
            Check("回退成功", rolled);
            Check("回退后文件内容 = 最初的旧版本（逐字节）",
                File.ReadAllBytes(installTarget).SequenceEqual(installedBytes));
            Check("回退后不留下 .rollback 临时文件", !File.Exists(installTarget + ".rollback"));

            Check("没有备份可回退时返回 false（不是异常）", !SilentUpgrader.Rollback("", installTarget));
            Check("备份路径不存在时返回 false",
                !SilentUpgrader.Rollback(Path.Combine(sandbox, "nope.bak"), installTarget));

            // ── 7. 首次安装（目标不存在 → 无备份也成功）───────────────
            var freshTarget = Path.Combine(pluginsRoot, "fresh.dll");
            var fresh = SilentUpgrader.Install(newDll, freshTarget);
            Check("目标不存在时也能安装（首次安装）", fresh.Ok, fresh.Error);
            Check("首次安装成功且没有备份（本来就没有旧文件）", fresh.BackupPath.Length == 0);
            Check("首次安装后内容正确", File.ReadAllBytes(freshTarget).SequenceEqual(newBytes));

            // ── 8. 失败路径不抛异常、错误可读 ──────────────────────────
            var badTarget = Path.Combine(sandbox, "no-such-dir", "x.dll");
            var badInstall = SilentUpgrader.Install(newDll, badTarget);
            Check("目标目录不存在 → 返回失败而不是抛异常", !badInstall.Ok);
            Check("目标目录不存在的错误信息可读",
                badInstall.Error.Contains("目录", StringComparison.Ordinal), badInstall.Error);

            var noSource = SilentUpgrader.Install(Path.Combine(sandbox, "ghost.dll"), installTarget);
            Check("源文件不存在 → 返回失败而不是抛异常", !noSource.Ok, noSource.Error);

            // ── 9. 目录型布局的包（认 plugin.json 里的 dll 名）──────────
            var layoutPackage = Path.Combine(sandbox, "layout.zip");
            using (var z = ZipFile.Open(layoutPackage, ZipArchiveMode.Create))
            {
                var manifest = z.CreateEntry(SilentUpgrader.LayoutFileName);
                using (var w = new StreamWriter(manifest.Open()))
                    w.Write("{\"dll\":\"NpsPushBridge.dll\"}");

                var dllEntry = z.CreateEntry("NpsPushBridge.dll");
                using var ws = dllEntry.Open();
                var bytes = File.ReadAllBytes(dllV13);
                ws.Write(bytes, 0, bytes.Length);
            }

            var layoutVerify = SilentUpgrader.VerifyAndExtract(layoutPackage, "1.2.0", "com.nps.pushbridge");
            Check("目录型布局的包也能校验通过", layoutVerify.Ok, layoutVerify.Error);
            Check("目录型包解析出的 dll 名正确",
                layoutVerify.DllName == "NpsPushBridge.dll", layoutVerify.DllName);

            // ── 10. 清理 ─────────────────────────────────────────────
            SilentUpgrader.CleanupStaging(ok.ExtractDir, goodZip);
            Check("清理后解包目录已删除", !Directory.Exists(ok.ExtractDir));
            Check("清理后安装包已删除", !File.Exists(goodZip));
            SilentUpgrader.CleanupStaging("", "");     // 空参数不能抛
            SilentUpgrader.CleanupStaging(
                Path.Combine(sandbox, "never-existed"), Path.Combine(sandbox, "never.zip"));
            Check("清理不存在的路径是安全空操作（不抛异常）", true);
        }
        catch (Exception ex)
        {
            failed++;
            Console.WriteLine($"  [FAIL] 用例执行时抛异常：{ex.GetType().Name} {ex.Message}");
        }
        finally
        {
            try { Directory.Delete(sandbox, true); } catch { /* 沙箱清理失败不影响结论 */ }
        }

        return (passed, failed);
    }

    /// <summary>把一份 dll 打成安装包（含 plugin.json 与入口 dll）。</summary>
    private static void MakePackage(string zipPath, string dllPath)
    {
        using var z = ZipFile.Open(zipPath, ZipArchiveMode.Create);

        var manifest = z.CreateEntry(SilentUpgrader.LayoutFileName);
        using (var w = new StreamWriter(manifest.Open()))
            w.Write("{\"dll\":\"" + SilentUpgrader.DefaultDllName + "\"}");

        var dll = z.CreateEntry(SilentUpgrader.DefaultDllName);
        using var ws = dll.Open();
        var bytes = File.ReadAllBytes(dllPath);
        ws.Write(bytes, 0, bytes.Length);
    }

    /// <summary>
    /// 现造一个"假插件 dll"写进沙箱，返回它的路径；失败返回空串（用例会据此判失败）。
    /// <paramref name="tag"/> 只用来保证每个文件内容互不相同，便于比对字节。
    /// </summary>
    private static string MakeFakeDll(string dir, string version, string pluginId, string tag)
    {
        try
        {
            var path = Path.Combine(dir, $"Fake-{tag}.dll");
            File.WriteAllBytes(path, FakeAssembly.Build(version, pluginId, tag));
            return path;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"      （造假 dll 失败：{ex.GetType().Name}: {ex.Message}）");
            return "";
        }
    }
}
