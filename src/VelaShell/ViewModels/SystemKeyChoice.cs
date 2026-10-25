using VelaShell.Core.Models;
using VelaShell.Core.Resources;
using VelaShell.Core.Ssh;

namespace VelaShell.ViewModels;

/// <summary>连接配置「系统密钥库」认证下拉里的一项:密钥库里的一把钥,或者存过、这台电脑上却找不到的那一把。</summary>
public sealed class SystemKeyChoice
{
    private SystemKeyChoice(SystemKeyReference reference, string title, string detail, bool isMissing)
    {
        Reference = reference;
        Title = title;
        Detail = detail;
        IsMissing = isMissing;
    }

    /// <summary>存进配置的引用。</summary>
    public SystemKeyReference Reference { get; }

    /// <summary>第一行:证书主题,没有证书就是钥名。</summary>
    public string Title { get; }

    /// <summary>第二行:在哪个密钥库(软件 / TPM / 智能卡)、类型与位数、指纹。</summary>
    public string Detail { get; }

    /// <summary>存过、这台电脑上却找不到(别的电脑上建的、智能卡没插、钥被删了)。</summary>
    public bool IsMissing { get; }

    /// <summary>公钥行(复制进服务器的 authorized_keys);找不到又没记下公钥时为 <see langword="null" />。</summary>
    public string? PublicKeyLine => Reference.PublicKey;

    /// <summary>密钥库里找到的一把。</summary>
    /// <param name="key">密钥库列出的钥。</param>
    public static SystemKeyChoice Found(SystemKeyInfo key)
    {
        string title = key.CertificateSubject ?? key.Reference.KeyName;
        string detail = $"{LocationOf(key.Reference)} · {key.KeyType} {key.KeyBits} · {key.Fingerprint}";
        if (key.CertificateSubject is not null)
        {
            // 证书主题当了标题,钥名放在末尾:同一个主题可能有几张证书。
            detail += $" · {key.Reference.KeyName}";
        }
        return new SystemKeyChoice(key.Reference, title, detail, isMissing: false);
    }

    /// <summary>存过、这次没列出来的那一把:照样列着并选中,写明找不到。</summary>
    /// <param name="reference">配置里存的引用。</param>
    public static SystemKeyChoice Missing(SystemKeyReference reference) =>
        new(reference, reference.KeyName,
            $"{LocationOf(reference)} · {Strings.Get("Profile_KeyStoreKeyMissing")}", isMissing: true);

    /// <summary>是不是同一把钥(密钥库、钥名、用户级还是机器级都对得上;CNG 的名字不分大小写)。</summary>
    /// <param name="reference">配置里存的引用。</param>
    public bool Matches(SystemKeyReference reference) =>
        Reference.MachineKey == reference.MachineKey
        && string.Equals(Reference.Provider, reference.Provider, StringComparison.OrdinalIgnoreCase)
        && string.Equals(Reference.KeyName, reference.KeyName, StringComparison.OrdinalIgnoreCase);

    /// <summary>给人看的位置:软件密钥库 / TPM / 智能卡;别的 KSP 直接写它的名字。机器级的加一句。</summary>
    /// <param name="reference">引用。</param>
    public static string LocationOf(SystemKeyReference reference)
    {
        string location = reference.Provider switch
        {
            SystemKeyReference.SoftwareProvider => Strings.Get("Profile_KeyStoreSoftware"),
            SystemKeyReference.PlatformProvider => Strings.Get("Profile_KeyStoreTpm"),
            SystemKeyReference.SmartCardProvider => Strings.Get("Profile_KeyStoreSmartCard"),
            _ => reference.Provider,
        };
        return reference.MachineKey ? Strings.Format("Profile_KeyStoreMachine", location) : location;
    }

    /// <inheritdoc />
    public override string ToString() => Title;
}
