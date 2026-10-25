using System.Diagnostics.CodeAnalysis;

namespace VelaShell.Core.Models;

/// <summary>
/// 系统密钥库(Windows CNG)里一把钥的引用:密钥库名 + 钥名 + 是不是机器级的。私钥不出密钥库,配置里只有这几样。
/// </summary>
/// <remarks>
/// <para>
/// 证书存储、TPM(<c>Microsoft Platform Crypto Provider</c>)、智能卡(<c>Microsoft Smart Card Key Storage Provider</c>)里那些
/// 导不出来的钥,签名交给密钥库做(SSH 库的 <c>CngSshSigner</c>)。引用不是机密,明文落盘、随云同步漫游 ——
/// 同步到没有这把钥的机器上(别的电脑、没插卡),连接时如实报「打不开」。
/// </para>
/// <para>
/// <see cref="PublicKey" /> 是选钥时记下的公钥,只给界面显示与「复制公钥」(往服务器的 authorized_keys 里加)用;
/// 连接时不信它,以密钥库里那把钥实际的公钥为准。
/// </para>
/// </remarks>
public sealed record SystemKeyReference
{
    /// <summary>软件密钥库(钥在用户配置文件里,受 DPAPI 保护)。</summary>
    public const string SoftwareProvider = "Microsoft Software Key Storage Provider";

    /// <summary>TPM 密钥库。</summary>
    public const string PlatformProvider = "Microsoft Platform Crypto Provider";

    /// <summary>智能卡密钥库。</summary>
    public const string SmartCardProvider = "Microsoft Smart Card Key Storage Provider";

    private const string UserPrefix = "cng:";
    private const string MachinePrefix = "cng-machine:";

    /// <summary>密钥库(CNG 的 KSP 名字)。</summary>
    public required string Provider { get; init; }

    /// <summary>钥的名字(CNG 的容器名;证书的私钥就是证书属性里记着的那个)。</summary>
    public required string KeyName { get; init; }

    /// <summary>机器级的钥(<c>NCRYPT_MACHINE_KEY_FLAG</c>);用户级的为 <see langword="false" />。</summary>
    public bool MachineKey { get; init; }

    /// <summary>选钥时记下的公钥(OpenSSH 一行);只给界面用,见类说明。</summary>
    public string? PublicKey { get; init; }

    /// <summary>
    /// 一行文本的写法(CSV 导入导出用):<c>cng:密钥库/钥名</c>,机器级的是 <c>cng-machine:密钥库/钥名</c>。
    /// 密钥库名里没有 <c>/</c>,按第一个 <c>/</c> 分开;钥名里有 <c>/</c> 也无妨。
    /// </summary>
    public string ToText() => (MachineKey ? MachinePrefix : UserPrefix) + Provider + "/" + KeyName;

    /// <summary>读 <see cref="ToText" /> 的写法;不是这个写法时返回 <see langword="false" />。</summary>
    /// <param name="text">文本。</param>
    /// <param name="reference">引用。</param>
    public static bool TryParse(string? text, [NotNullWhen(true)] out SystemKeyReference? reference)
    {
        reference = null;
        string value = (text ?? string.Empty).Trim();
        bool machine = value.StartsWith(MachinePrefix, StringComparison.OrdinalIgnoreCase);
        if (!machine && !value.StartsWith(UserPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        string rest = value[(machine ? MachinePrefix.Length : UserPrefix.Length)..];
        int slash = rest.IndexOf('/');
        if (slash <= 0 || slash == rest.Length - 1)
        {
            return false;
        }
        reference = new SystemKeyReference { Provider = rest[..slash], KeyName = rest[(slash + 1)..], MachineKey = machine };
        return true;
    }
}
