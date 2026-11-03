using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace BridgeHarness;

/// <summary>
/// 测试用：手工造一个**最小但合法**的 .NET 程序集字节流，带指定的
/// <c>AssemblyInformationalVersion</c> 特性与一段 Id 字符串字面量。
///
/// ★ 为什么需要它 ★
/// 静默升级的校验要读真实程序集的两样东西：<c>AssemblyInformationalVersion</c>（版本号），
/// 以及用户字符串堆里的 Id 字面量（确认"这确实是本插件"）。测试必须能造出
/// 「版本不同 / Id 不同 / 根本不是程序集」这些形态的输入，而真实编译产物只有固定的一份。
///
/// ★ 为什么不用 csc 编译 ★
/// 本机沙箱禁止直接调用编译器（等价于任意代码执行，被安全策略拦截），
/// 所以测试里不能"现场编译一段 C#"。改为用 <see cref="MetadataBuilder"/> 直接写元数据 ——
/// 这也正好只依赖 <c>System.Reflection.Metadata</c>（共享框架自带，不需要额外包）。
///
/// ⚠️ 这里**故意不写方法体、不写真实类型**（没有 IL）：
/// 校验链路只需要读元数据，不需要可执行的代码。产出的引用在"能通过元数据校验"这个意义上是
/// **合法**的（<see cref="AssemblyName.GetAssemblyName(string)"/> 与 <see cref="PEReader"/>
/// 都能正常读取），但它不可加载运行 —— 测试也不加载它。
/// </summary>
internal static class FakeAssembly
{
    /// <summary>程序集显示名（与真实插件一致，便于校验逻辑走同一条路）。</summary>
    private const string AssemblyName = "NpsPushBridge";

    /// <summary>
    /// 造一个程序集：版本号写进 AssemblyInformationalVersion，<paramref name="pluginId"/>
    /// 作为用户字符串（#US 堆）里的字面量，模拟 <c>Id</c> 属性的取值。
    /// </summary>
    /// <param name="tag">
    /// 可选的区分标记，只用来让同版本不同用途的假 dll 内容互不相同
    /// （便于"替换后逐字节比对"这类用例有实际区分度）。
    /// </param>
    public static byte[] Build(string version, string pluginId, string tag = "")
    {
        var metadata = new MetadataBuilder();

        // ── 字符串堆：把用到的字符串一次性加上，拿到句柄 ──
        var nameHandle = metadata.GetOrAddString(AssemblyName);
        var moduleNameHandle = metadata.GetOrAddString(AssemblyName + ".dll");
        var infoVersionHandle = metadata.GetOrAddString(version);

        // Id 字面量：校验逻辑是「在文件字节里搜 UTF-16 的 Id 字符串」，
        // 而 GetOrAddUserString 写的正是 UTF-16 编码的用户字符串堆 —— 与真实编译产物一致。
        metadata.GetOrAddUserString(pluginId);

        if (tag.Length > 0)
            metadata.GetOrAddUserString("fake-tag:" + tag);

        // ── 模块行（每个程序集必须有） ──
        metadata.AddModule(
            generation: 0,
            moduleName: moduleNameHandle,
            mvid: metadata.GetOrAddGuid(Guid.NewGuid()),
            encId: default,
            encBaseId: default);

        // ── 程序集行 ──
        var assemblyDef = metadata.AddAssembly(
            name: nameHandle,
            version: ParseVersion(version),
            culture: default,
            publicKey: default,
            flags: default,
            hashAlgorithm: AssemblyHashAlgorithm.Sha1);

        // ── AssemblyInformationalVersionAttribute 特性（校验读的就是它） ──
        // 特性类型来自 System.Runtime，所以先加一条程序集引用，再建 TypeRef / MemberRef。
        var coreLibRef = metadata.AddAssemblyReference(
            name: metadata.GetOrAddString("System.Runtime"),
            version: new Version(10, 0, 0, 0),
            culture: default,
            publicKeyOrToken: default,
            flags: default,
            hashValue: default);

        var attrType = metadata.AddTypeReference(
            resolutionScope: coreLibRef,
            @namespace: metadata.GetOrAddString("System.Reflection"),
            name: metadata.GetOrAddString("AssemblyInformationalVersionAttribute"));

        var attrCtor = metadata.AddMemberReference(
            parent: attrType,
            name: metadata.GetOrAddString(".ctor"),
            signature: metadata.GetOrAddBlob(InstanceStringCtorSignature()));

        // 特性的值 blob：prolog(0x0001) + SerString。
        // SerString 就是"压缩整数长度 + UTF8 字节"，长度 -1 表示 null。
        var valueBlob = new BlobBuilder();
        valueBlob.WriteUInt16(0x0001);
        WriteSerString(valueBlob, version);

        metadata.AddCustomAttribute(assemblyDef, attrCtor, metadata.GetOrAddBlob(valueBlob));

        // ── 生成 PE ──
        var peBuilder = new ManagedPEBuilder(
            header: PEHeaderBuilder.CreateLibraryHeader(),
            metadataRootBuilder: new MetadataRootBuilder(metadata),
            ilStream: new BlobBuilder(),
            mappedFieldData: new BlobBuilder(),
            managedResources: new BlobBuilder(),
            nativeResources: null,
            debugDirectoryBuilder: null,
            strongNameSignatureSize: 0,
            entryPoint: default,
            flags: CorFlags.ILOnly,
            deterministicIdProvider: null);

        var peBlob = new BlobBuilder();
        peBuilder.Serialize(peBlob);
        return peBlob.ToArray();
    }

    /// <summary><c>.ctor(string)</c> 的实例方法签名 blob。</summary>
    private static BlobBuilder InstanceStringCtorSignature()
    {
        var blob = new BlobBuilder();
        new BlobEncoder(blob).MethodSignature(isInstanceMethod: true)
            .Parameters(1, returnType => returnType.Void(), parameters => parameters.AddParameter().Type().String());
        return blob;
    }

    /// <summary>按 ECMA-335 的 SerString 格式写一个字符串（压缩整数长度 + UTF8 字节）。</summary>
    private static void WriteSerString(BlobBuilder blob, string value)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(value);
        WriteCompressedInteger(blob, bytes.Length);
        blob.WriteBytes(bytes);
    }

    /// <summary>写 ECMA-335 的压缩无符号整数。</summary>
    private static void WriteCompressedInteger(BlobBuilder blob, int value)
    {
        if (value <= 0x7F)
        {
            blob.WriteByte((byte)value);
        }
        else if (value <= 0x3FFF)
        {
            blob.WriteByte((byte)(0x80 | (value >> 8)));
            blob.WriteByte((byte)(value & 0xFF));
        }
        else
        {
            blob.WriteByte((byte)(0xC0 | (value >> 24)));
            blob.WriteByte((byte)((value >> 16) & 0xFF));
            blob.WriteByte((byte)((value >> 8) & 0xFF));
            blob.WriteByte((byte)(value & 0xFF));
        }
    }

    /// <summary>把 "1.3.0" 变成四段式 <see cref="Version"/>。</summary>
    private static Version ParseVersion(string version)
    {
        var parts = (NpsPushBridge.PluginUpdate.NormalizeVersion(version) ?? "1.0.0")
            .Split('-')[0].Split('.');
        int a = parts.Length > 0 && int.TryParse(parts[0], out var x) ? x : 1;
        int b = parts.Length > 1 && int.TryParse(parts[1], out var y) ? y : 0;
        int c = parts.Length > 2 && int.TryParse(parts[2], out var z) ? z : 0;
        return new Version(a, b, c, 0);
    }
}
