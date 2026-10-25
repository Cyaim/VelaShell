using VelaShell.Core.Models;

namespace VelaShell.Core.Ssh;

/// <summary>系统密钥库里能拿来登录 SSH 的一把钥(私钥导不出来,签名由密钥库做)。</summary>
/// <param name="Reference">引用(存进连接配置的就是它,带着公钥)。</param>
/// <param name="KeyType">SSH 的钥类型,如 <c>ssh-rsa</c>、<c>ecdsa-sha2-nistp256</c>。</param>
/// <param name="KeyBits">位数。</param>
/// <param name="Fingerprint">SHA256 指纹。</param>
/// <param name="CertificateSubject">证书存储里有证书用这把钥时,证书的主题(给人认钥用);没有为 <see langword="null" />。</param>
public sealed record SystemKeyInfo(
    SystemKeyReference Reference,
    string KeyType,
    int KeyBits,
    string Fingerprint,
    string? CertificateSubject);

/// <summary>
/// 系统密钥库:列出能用来登录 SSH 的钥(Windows CNG 的软件密钥库、TPM、智能卡,以及证书存储里证书用的那些)。
/// </summary>
/// <remarks>只有 Windows 有;别的平台 <see cref="IsSupported" /> 为假、列表为空(PKCS#11 与 macOS Secure Enclave 另见 feature-plan)。</remarks>
public interface ISystemKeyStore
{
    /// <summary>这个平台有没有系统密钥库(Windows 才有)。</summary>
    bool IsSupported { get; }

    /// <summary>
    /// 列出能用的钥:RSA 与 NIST P-256 / P-384 / P-521 的 ECDSA(CNG 没有 Ed25519);只用来交换密钥的、打不开的(没权限、要弹框才能打开)不列。
    /// </summary>
    /// <remarks>不弹任何框(静默打开):列表是在编辑连接配置时现列的,智能卡的 PIN 留到真签名时再问。</remarks>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<IReadOnlyList<SystemKeyInfo>> ListKeysAsync(CancellationToken cancellationToken = default);
}
