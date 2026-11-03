using System.IO;
using Microsoft.Win32;
using SkiaSharp;

namespace NpsPushBridge;

/// <summary>
/// 字体选择的**唯一真源** —— 插件里所有 SkiaSharp 文本绘制都必须经由这里拿 Typeface。
///
/// ── 为什么必须有它（实测踩过的坑）────────────────────────────────────────
/// SkiaSharp 的 <see cref="SKPaint"/> **不设 <c>Typeface</c> 时用的是一个不含任何 CJK
/// 字形的默认字体**：实测 <c>new SKPaint { TextSize = 11.5f }.GetGlyphs("有新版本")</c>
/// 返回 <c>[0,0,0,0]</c>（字形 id 0 = <c>.notdef</c>），画出来就是四个豆腐块。
/// 灵动岛上的提示条当初漏设字体，于是「有新版本 v1.1.1」里的中文全成了方框。
///
/// ── 为什么不直接写 <c>SKTypeface.FromFamilyName("Microsoft YaHei UI")</c> ──
/// 1. <c>FromFamilyName</c> 找不到字体族时**静默返回 null**（不是抛异常），null 又会退回
///    那个没有 CJK 的默认字体 —— 也就是同一个 bug 原地复活，而且不报错、极难发现。
/// 2. 不同系统的字体清单不一样（Windows 中文版有雅黑，某些精简/英文版镜像未必有）。
/// 3. 频繁 <c>FromFamilyName</c> 会**反复创建原生对象**，不适合每帧调用。
///
/// 于是这里做三件事：**按优先级依次回退**、**用字符覆盖度实测挑字体**、**进程内缓存结果**。
///
/// ── 三层字体与它们的关系（2026-09-23 统一）───────────────────────────────
/// <list type="table">
/// <item><term><see cref="Cjk"/></term><description>系统 CJK 链。**兜底的兜底**，永远可用。</description></item>
/// <item><term><see cref="Ui"/></term><description>插件界面字体 = 宿主设置里的**自定义字体**（用户在宿主里选了就用它），否则等于 <see cref="Cjk"/>。</description></item>
/// <item><term><see cref="TypefaceFor"/></term><description>★ 实际绘制应该用的那个 ★ 逐**字符串**判断：<see cref="Ui"/> 画得全这串字就用它，画不全就用 <see cref="Cjk"/>。</description></item>
/// </list>
///
/// ★ 为什么要「逐字符串」而不是「要么全用、要么全不用」★
/// 用户选的自定义字体可能是**专门的显示字体**，缺个别汉字或个别符号（<c>▲</c>/<c>✓</c> 这类
/// 最容易缺）。若整块界面一刀切，缺一个字就会连累整屏文字全部放弃自定义字体；
/// 若不做覆盖度检查，缺的那些字会直接变成方框 —— 那是本项目早期真实发生过的事故。
/// 逐串判定两头都避开了：**能画的照用户的字体画，画不出的那串退回系统字体**，
/// 界面上永远不会出现方框，用户选的字体也在绝大多数地方生效。
///
/// ⚠️ 一条不可违反的配套约束：**量宽与画宽必须用同一个 Typeface**。
/// 所以调用方必须在**截断 / 居中之前**就定好字体（见 <see cref="Fit"/> 的用法），
/// 而不是先按 A 字体量、再按 B 字体画 —— 那会让文字溢出或被裁。
///
/// ── 缓存是否安全 ────────────────────────────────────────────────────────
/// 项目规则是「绝不跨线程缓存原生 SKPaint」。<see cref="SKTypeface"/> 不同：
/// 它是**不可变的**（没有可变绘制状态，只描述字形来源），SkiaSharp 文档明确写它
/// 「can be shared between threads」；而且我们**从不 Dispose 缓存里的实例**，
/// 因此不存在「一个线程释放、另一个线程还在用」的 UAF 风险。
/// 缓存的是「字体族 → SKTypeface」的映射，每个字体族只查一次。
/// </summary>
internal static class PluginFonts
{
    /// <summary>
    /// 候选中文字体族，按优先级排列。
    ///
    /// 为什么把「Microsoft YaHei UI」放第一：它是 Win10/11 系统 UI 的中文默认字体，
    /// 与灵动岛自身的文字观感最一致；配置面板早已在用它，统一到同一个字体才不会
    /// 「岛上一个味、设置里另一个味」。后面的几项只是不同 Windows 版本下的兜底。
    /// </summary>
    private static readonly string[] PreferredFamilies =
    {
        "Microsoft YaHei UI",   // Win10/11 中文系统 UI 字体
        "Microsoft YaHei",      // 同族正式名，注册表里可能只登记这一项
        "Noto Sans SC",         // 部分镜像预装的思源黑体
        "SimSun",               // 老系统最后的 CJK 兜底
        "Segoe UI",             // 至少保证拉丁字母 / 数字正常（CJK 仍会缺，但聊胜于无）
    };

    /// <summary>用来验证字体是否真的能画中文的样本字（「有」在 CJK 统一表意区开头附近，覆盖最广）。</summary>
    private const string CjkProbe = "有新版本";

    // ====================================================================
    // 宿主自定义字体（只读宿主注册表；宿主源码零改动）
    // ====================================================================

    /// <summary>宿主读写自身配置的注册表位置 —— 与 <c>NotchPeninsula/Program.cs</c> 里的字符串逐字一致。</summary>
    private const string HostRegistryPath = @"SOFTWARE\NotchPeninsula";

    /// <summary>宿主记忆「用户选中的自定义字体文件」所用的值名（<c>ConsoleWindow.cs</c> 写入、<c>Program.cs</c> 读回）。</summary>
    private const string HostFontPathValue = "CustomFontPath";

    /// <summary>宿主字体文件最多探测的字体面数量（与宿主 <c>FontConfig.LoadFaces</c> 的上限 8 保持一致）。</summary>
    private const int MaxFontFaces = 8;

    /// <summary>
    /// 「采用这个自定义字体」的**最低门槛**：至少能画出岛内容所需的这批字。
    ///
    /// 注意它**不是**「必须画全插件所有文字」—— 那种要求会让一个缺个别符号的字体被整体弃用。
    /// 这里只用来排掉「根本不是中文字体」的情况（例如用户误选了纯拉丁/纯符号字体），
    /// 剩下的个别缺字由 <see cref="TypefaceFor"/> 逐串兜住。
    /// </summary>
    private const string CoreProbe = "有新版本更新橙绿灰白名单端口保存";

    /// <summary>逐字符覆盖度缓存的上限。到了上限就不再增长（改成每次现算），避免日志这类动态文本把缓存撑爆。</summary>
    private const int CoverageCacheLimit = 4096;

    private static readonly object Gate = new();
    private static readonly Dictionary<string, SKTypeface> Cache = new(StringComparer.Ordinal);
    private static SKTypeface? _resolved;

    /// <summary>
    /// 从宿主字体文件加载出来的字体面；<c>null</c> 表示「没有可用的自定义字体」。
    /// 用「缓存 + 指纹」而不是布尔值，是因为宿主的自定义字体**可以在运行时切换**
    /// （<c>FontConfig.ApplyCustomFont</c> 写注册表后热应用），缓存必须能在指纹变化时失效。
    /// </summary>
    private static SKTypeface? _hostFace;

    /// <summary>生成 <see cref="_hostFace"/> 时对应的字体指纹（路径 + 文件时间戳 + 大小）；无自定义字体时为空串。</summary>
    private static string _hostFingerprint = "";

    /// <summary>宿主注册表里记忆的字体路径（未设置 / 读取失败时为空串）。每次调用都读注册表，成本可忽略。</summary>
    private static string _hostPath = "";

    /// <summary>该字体面是否**画得全** <see cref="CoreProbe"/>（画不全也照样采用，只是界面上会注明有回退）。</summary>
    private static bool _hostCoversCore;

    /// <summary>字符 → 该自定义字体是否含此字形。字体一变就整体清空（见 <see cref="ResolveUi"/>）。</summary>
    private static readonly Dictionary<int, bool> CoverageCache = new();

    /// <summary>是否已经解析过一次（用于让「读注册表 + 查文件时间」这一步能被节流）。</summary>
    private static bool _uiResolved;

    /// <summary>
    /// 上次核对字体指纹的时刻（<see cref="Environment.TickCount64"/>，毫秒）。
    ///
    /// ★ 为什么必须节流 ★ <see cref="TypefaceFor"/> 会被**每个字符串、每一帧**调用，
    /// 而核对指纹要读注册表 + 取一次文件时间戳 —— 60fps 下每帧几十次系统调用，
    /// 纯属浪费。字体的切换主体是「用户在宿主设置里改」，晚 1 秒发现毫无影响。
    /// </summary>
    private static long _lastUiCheck;

    /// <summary>两次核对指纹之间的最小间隔（毫秒）。</summary>
    private const long FingerprintCheckIntervalMs = 1000;

    /// <summary>
    /// 取得绘制中文可用的字体。**返回值永不为 null**（最坏也返回系统默认字体），
    /// 因此调用方不必再写 null 判断 —— 少一个分支就少一处漏判。
    /// </summary>
    public static SKTypeface Cjk
    {
        get
        {
            var cached = _resolved;
            if (cached != null) return cached;

            lock (Gate)
            {
                if (_resolved != null) return _resolved;

                _resolved = Resolve();
                return _resolved;
            }
        }
    }

    /// <summary>
    /// 按候选顺序找第一个**实测能画中文**的字体族。
    ///
    /// 关键点：<c>FromFamilyName</c> 成功 ≠ 能画中文。比如 "Segoe UI" 在 Windows 上
    /// 一定能拿到（返回非 null），但它没有汉字字形 —— 只判断 null 就会挑到它，
    /// 于是又回到豆腐块。所以这里拿到实例后还要用 <see cref="SKPaint.ContainsGlyphs(string)"/>
    /// 实测一次覆盖度，不合格就继续往下试。
    /// </summary>
    private static SKTypeface Resolve()
    {
        foreach (var family in PreferredFamilies)
        {
            var typeface = TryGet(family);
            if (typeface == null) continue;
            if (CoversCjk(typeface)) return typeface;
        }

        // 候选全军覆没：扫一遍系统字体族，挑第一个能画中文的（宁可启动时多花一次扫描，
        // 也不要给用户一片方框）。整个过程只发生一次，之后一直命中缓存。
        try
        {
            foreach (var family in SKFontManager.Default.FontFamilies)
            {
                var typeface = TryGet(family);
                if (typeface != null && CoversCjk(typeface)) return typeface;
            }
        }
        catch
        {
            // 枚举失败（权限 / 平台差异）不影响继续走默认字体
        }

        // 真的是一个中文字体都没有：退回默认字体。此时中文仍会显示为方框，
        // 但插件功能不受影响，且这是环境问题而非代码问题。
        return SKTypeface.Default;
    }

    /// <summary>按字体族取字体，带缓存；取不到返回 null（不抛异常）。</summary>
    private static SKTypeface? TryGet(string family)
    {
        lock (Gate)
        {
            if (Cache.TryGetValue(family, out var cached)) return cached;
        }

        SKTypeface? typeface;
        try
        {
            typeface = SKTypeface.FromFamilyName(family);
        }
        catch
        {
            typeface = null;
        }

        // 注意：null 也要缓存（用 null! 占位会破坏语义），这里只缓存命中的项，
        // 未命中的字体族每次都会重查 —— 但候选只有 5 个且 Resolve 只跑一次，无影响。
        if (typeface == null) return null;

        lock (Gate) { Cache[family] = typeface; }
        return typeface;
    }

    /// <summary>实测该字体是否含有全部探测字的字形。用 <c>ContainsGlyphs</c> 而不是自己比对
    /// 字形 id，是因为前者在 SkiaSharp 里就是为这个用途提供的，语义更直白。</summary>
    private static bool CoversCjk(SKTypeface typeface)
    {
        try
        {
            using var paint = new SKPaint { Typeface = typeface, TextSize = 12f };
            return paint.ContainsGlyphs(CjkProbe);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 造一个已经设好中文字体与字号的 paint。
    ///
    /// 用这个工厂而不是各处 <c>new SKPaint { ... }</c>，是为了让「忘记设 Typeface」
    /// 这个 bug 在结构上无法再发生 —— 漏写一次就是一个组件的文字全变方框，很难靠测试发现。
    /// 生命周期仍由调用方掌控（<c>using</c>），本方法不缓存 SKPaint。
    ///
    /// 注意它用的是**系统 CJK** 而不是 <see cref="Ui"/>：这是「保底字体」，
    /// 真正画界面文字前应当再调一次 <see cref="Fit"/> 把字体按字符串切到宿主字体。
    /// 这样即使某处漏了 <see cref="Fit"/>，结果也只是「那串字没跟随宿主字体」，
    /// 而**不是豆腐块** —— 失败方向故意选成无害的那一边。
    /// </summary>
    public static SKPaint TextPaint(float textSize, SKColor? color = null, bool antialias = true)
        => new()
        {
            Typeface = Cjk,
            TextSize = textSize,
            IsAntialias = antialias,
            Color = color ?? SKColors.Black,
        };

    // ====================================================================
    // 插件统一字体：宿主自定义字体 + 逐字符串回退
    // ====================================================================

    /// <summary>
    /// 插件界面文字应当使用的字体（宿主自定义字体；没设或不可用时等于 <see cref="Cjk"/>）。
    ///
    /// ⚠️ **直接拿它去画 **不**保证不出方框** —— 用户的字体可能缺个别字形。
    /// 真正绘制请走 <see cref="TypefaceFor"/> / <see cref="Fit"/>，它们会按字符串兜底。
    /// 这个属性留给两类用途：需要「整体同一字体」的场合（如上岛组件按文案量宽），
    /// 以及界面上的「当前字体」说明文字。
    /// </summary>
    public static SKTypeface Ui
    {
        get
        {
            try
            {
                ResolveUi();
                return _hostFace ?? Cjk;
            }
            catch
            {
                // 任何意外都不该让绘制整体失败 —— 字体只是观感，功能优先
                return Cjk;
            }
        }
    }

    /// <summary>
    /// ★ 画某个字符串时应该用的字体 ★
    ///
    /// 宿主自定义字体画得全这串字就用它，否则回退 <see cref="Cjk"/>。
    /// 返回**永不为 null**，调用方不需要判空。
    ///
    /// 判定成本：按**单个字符**缓存命中与否，缓存键是字符码点，所以同一字符只真正探测一次。
    /// 首帧之后几乎全是字典命中，不会成为每帧热点。
    /// </summary>
    public static SKTypeface TypefaceFor(string? text)
    {
        SKTypeface? host;
        try
        {
            ResolveUi();
            host = _hostFace;
        }
        catch
        {
            host = null;
        }

        if (host == null) return Cjk;
        if (string.IsNullOrEmpty(text)) return host;

        return CoversAll(host, text) ? host : Cjk;
    }

    /// <summary>
    /// 把画笔的字体切到「画这个字符串应该用的字体」。
    ///
    /// ★ 必须在量宽 / 截断 / 算基线**之前**调用 ★ 否则会出现「按甲字体量、按乙字体画」，
    /// 文字要么溢出被裁、要么与省略号错位。这是本类唯一的使用约定。
    /// </summary>
    public static void Fit(SKPaint paint, string? text)
    {
        var typeface = TypefaceFor(text);
        if (!ReferenceEquals(paint.Typeface, typeface)) paint.Typeface = typeface;
    }

    /// <summary>岛内容应当使用的 paint（与 <see cref="TextPaint"/> 同构，但字体走 <see cref="Ui"/>）。</summary>
    public static SKPaint IslandPaint(float textSize, SKColor? color = null, bool antialias = true)
        => new()
        {
            Typeface = Ui,
            TextSize = textSize,
            IsAntialias = antialias,
            Color = color ?? SKColors.Black,
        };

    /// <summary>
    /// 当前实际在用的字体族名（自定义字体不可用时是系统字体名），用于界面显示与诊断。
    /// </summary>
    public static string UiFontName
    {
        get
        {
            try
            {
                ResolveUi();
                var face = _hostFace ?? Cjk;
                var name = face.FamilyName;
                return string.IsNullOrWhiteSpace(name) ? "（未知字体）" : name;
            }
            catch
            {
                return "（未知字体）";
            }
        }
    }

    /// <summary>当前是否真的在用宿主设置里的自定义字体（界面 / 日志据此说明）。</summary>
    public static bool UiUsesCustomFont
    {
        get { try { ResolveUi(); return _hostFace != null; } catch { return false; } }
    }

    /// <summary>
    /// 宿主注册表里记忆的自定义字体路径；未设置或读取失败时为空串。
    /// 供配置界面显示「当前用的是哪个字体文件」用。
    /// </summary>
    public static string UiCustomFontPath
    {
        get { try { ResolveUi(); return _hostPath; } catch { return ""; } }
    }

    /// <summary>
    /// 自定义字体是否**画得全**常用界面文字（见 <see cref="CoreProbe"/>）。
    /// 为 false 表示部分字形会逐串回退系统字体 —— 界面上要如实说明，不能让用户以为漏了。
    /// </summary>
    public static bool UiCoversCore
    {
        get { try { ResolveUi(); return _hostFace == null || _hostCoversCore; } catch { return true; } }
    }

    /// <summary>
    /// 一句给用户看的「当前字体」说明。界面直接显示，保证与真正生效的字体**同源**。
    /// </summary>
    public static string UiDescribe()
    {
        var name = UiFontName;
        if (!UiUsesCustomFont) return $"系统字体（{name}）";
        return UiCoversCore
            ? $"宿主自定义字体（{name}）"
            : $"宿主自定义字体（{name}）· 缺字形处回退系统字体";
    }

    /// <summary>
    /// 强制丢弃字体缓存，下次访问重新读注册表并重新加载。
    ///
    /// 调用时机：宿主（或用户在宿主设置界面）更换字体之后。若不主动调用，
    /// 指纹检测也会发现并重载 —— 这里只是给调用方一个「立刻生效、不必等下一帧」的显式入口。
    /// </summary>
    public static void InvalidateUiFont()
    {
        lock (Gate)
        {
            _hostFace = null;
            _hostFingerprint = "";
            _hostPath = "";
            _hostCoversCore = true;
            _uiResolved = false;
            _lastUiCheck = 0;
            CoverageCache.Clear();
        }
    }

    // ====================================================================
    // 内部实现
    // ====================================================================

    /// <summary>
    /// 该字体是否含这个字符的字形。按字符缓存（字体一变缓存即被清空）。
    ///
    /// ⚠️ 这里**故意为每次未命中新建一个 SKPaint** 而不是复用字段：项目规则是
    /// 「绝不跨线程缓存原生 SKPaint」。好在只有首帧会大量未命中，之后全是字典查询。
    /// </summary>
    private static bool CoversChar(SKTypeface typeface, int codePoint)
    {
        lock (Gate)
        {
            if (CoverageCache.TryGetValue(codePoint, out bool known)) return known;
        }

        bool covered;
        try
        {
            using var paint = new SKPaint { Typeface = typeface, TextSize = 12f };
            covered = paint.ContainsGlyphs(char.ConvertFromUtf32(codePoint));
        }
        catch
        {
            // 给不出结论时按「画不出」处理：宁可回退到系统字体（一定有字），也不要赌它画得出
            covered = false;
        }

        lock (Gate)
        {
            // 到上限就不再增长：日志是动态文本，字符集合可能慢慢变大，
            // 无上限缓存等于慢性泄漏。超限后只是退化为每次现算，功能不受影响。
            if (CoverageCache.Count < CoverageCacheLimit) CoverageCache[codePoint] = covered;
        }
        return covered;
    }

    /// <summary>该字体是否画得全整个字符串（控制字符与空白不参与判定）。</summary>
    private static bool CoversAll(SKTypeface typeface, string text)
    {
        foreach (var ch in text)
        {
            if (ch < ' ') continue;                 // 换行 / 制表等不参与
            if (!CoversChar(typeface, ch)) return false;
        }
        return true;
    }

    /// <summary>
    /// 解析宿主自定义字体并在指纹未变时直接复用。调用方无需持锁 —— 本方法内部自行加锁。
    ///
    /// ⚠️ 本方法**不得在持锁状态下调用 <see cref="Cjk"/>**（它的 getter 也 `lock (Gate)`）。
    /// 这里刻意不取兜底字体：需要兜底的 <see cref="Ui"/> / <see cref="TypefaceFor"/>
    /// 都是在本方法**返回之后**才去读 <see cref="Cjk"/> 的，那时锁已释放。
    /// </summary>
    private static void ResolveUi()
    {
        long now = Environment.TickCount64;

        // 节流：刚刚核对过就不要再读注册表 / 查文件时间
        if (_uiResolved && now - _lastUiCheck < FingerprintCheckIntervalMs) return;

        string path = ReadHostFontPath();
        string fingerprint = BuildFingerprint(path);

        if (_uiResolved && fingerprint == _hostFingerprint)
        {
            _lastUiCheck = now;   // 字体没变，只更新时间戳
            return;
        }

        lock (Gate)
        {
            // 双重检查：并发进入时只让第一个完成加载
            if (_uiResolved && fingerprint == _hostFingerprint) { _lastUiCheck = now; return; }

            var face = LoadHostFace(path, out bool coversCore);

            // 换了字体文件（或字体内容变了）→ 之前按字符缓存的覆盖度结论全部失效
            CoverageCache.Clear();

            _hostFace = face;
            _hostFingerprint = fingerprint;
            _hostPath = path;
            _hostCoversCore = coversCore;
            _uiResolved = true;
            _lastUiCheck = now;
        }
    }

    /// <summary>
    /// 按宿主记忆的路径加载字体，挑一个可用的字体面。
    ///
    /// 选择顺序：**先找画得全 <see cref="CoreProbe"/> 的那个字面**（正常的中文字体走这条），
    /// 找不到再退而取**第一个能加载成功的字面**。
    ///
    /// ★ 为什么「画不全也采用」★ 因为 <see cref="TypefaceFor"/> 会逐串兜底：
    /// 采用一个缺个别符号的字体，结果是「它擅长的那些字跟随宿主，缺的那几个用系统字体」，
    /// 严格优于「整屏都不用用户的字体」。真正需要排掉的是「根本不是字体文件 / 一个面都加载不出来」。
    ///
    /// ★ 为什么不记录 / 不缓存在 SKTypeface 缓存表里 ★
    /// 这个实例是从文件新建的原生对象，生命周期完全归本类；只在指纹变化时重建，
    /// 旧实例**不 Dispose**（可能仍有 SKPaint 抓着它，Dispose 会造成 use-after-dispose，
    /// 与 <see cref="Cjk"/> 的处理原则一致）。字体文件内存由 GC 在无引用后回收原生对象时释放。
    /// </summary>
    private static SKTypeface? LoadHostFace(string path, out bool coversCore)
    {
        coversCore = true;

        if (string.IsNullOrWhiteSpace(path)) return null;   // 用户从未选过自定义字体 —— 最常见的路径

        try
        {
            if (!File.Exists(path)) return null;
        }
        catch
        {
            return null;
        }

        SKTypeface? firstFace = null;
        SKTypeface? probeFace = null;

        for (int index = 0; index < MaxFontFaces; index++)
        {
            SKTypeface? face;
            try { face = SKTypeface.FromFile(path, index); }
            catch { face = null; }

            if (face == null) break;   // 索引越界即视为已取完（与宿主 LoadFaces 同策略）

            firstFace ??= face;

            if (CanRenderCore(face)) { probeFace = face; break; }

            // 这个字面画不全核心字形：不 Dispose（可能已被外部引用），交给 GC
        }

        var chosen = probeFace ?? firstFace;
        if (chosen == null) return null;   // 文件存在但一个面都加载不出来（损坏 / 不支持的格式）

        coversCore = probeFace != null;
        return chosen;
    }

    /// <summary>该字体能否画全常用界面文字（<see cref="CoreProbe"/>）。</summary>
    private static bool CanRenderCore(SKTypeface typeface)
    {
        try
        {
            using var paint = new SKPaint { Typeface = typeface, TextSize = 12f };
            return paint.ContainsGlyphs(CoreProbe);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 只读宿主注册表取字体路径。
    ///
    /// ★ 严格只读 ★ 用 <c>OpenSubKey</c>（需要读权限）而非 <c>CreateSubKey</c>——
    /// 后者会在键不存在时**创建**它，等于替宿主写了一次注册表。
    /// 插件对宿主的配置只应「看」，不应「动」。
    /// </summary>
    private static string ReadHostFontPath()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(HostRegistryPath, writable: false);
            if (key == null) return "";

            object? value = key.GetValue(HostFontPathValue);
            return value as string ?? "";
        }
        catch
        {
            // 权限受限 / 键被删除 / 平台差异 —— 一律视为「没有自定义字体」
            return "";
        }
    }

    /// <summary>
    /// 字体指纹：路径 + 文件最后写入时间 + 文件大小。
    ///
    /// 只比路径是不够的：用户完全可能**用同一个文件名换掉字体内容**
    /// （例如把下载的新字体覆盖到原路径），此时路径没变但字体已不同。
    /// 加上时间戳与大小后，这种替换也能被检测到。
    /// </summary>
    private static string BuildFingerprint(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return path + "|missing";
            return $"{path}|{info.LastWriteTimeUtc.Ticks}|{info.Length}";
        }
        catch
        {
            // 取不到元数据时退回只比路径（至少能感知「用户换了别的文件」）
            return path + "|unknown";
        }
    }
}
