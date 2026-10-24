// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   RFC 8332  rsa-sha2-256 / rsa-sha2-512
//   RFC 5656  ecdsa-sha2-nistp*
//   行为规格: velashell-docs/zh/ssh/spec/04-authentication.md §4.8

using System.Runtime.Versioning;
using System.Security.Cryptography;
using VelaShell.Ssh.Diagnostics;
using VelaShell.Ssh.HostKeys;
using VelaShell.Ssh.Keys;

namespace VelaShell.Ssh.Auth;

/// <summary>
/// Windows CNG 密钥库里的钥（软件密钥库、TPM —— <c>Microsoft Platform Crypto Provider</c>、智能卡的 KSP）做签名器：
/// 私钥不导出、不落盘，签名交给密钥库做。
/// </summary>
/// <remarks>
/// <para>
/// 〔velashell-docs/zh/ssh/spec/04 §4.8〕企业发的智能卡、合规要求「私钥不落盘」的环境里，钥本来就导不出来 ——
/// 用它直接登录，不必先导出成文件。RSA（<c>rsa-sha2-512</c> / <c>-256</c>）与 ECDSA P-256 / P-384 / P-521；
/// CNG 没有 Ed25519。
/// </para>
/// <para>
/// <see cref="IsLocalAndCheap"/> 为假：TPM 签一次要上百毫秒，智能卡可能还要输 PIN、弹确认 —— 认证器因此先问服务端认不认这把钥再签。
/// 签名放到线程池上做，不堵调用方的线程。也因为私钥导不出来，这把钥加不进 agent。
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class CngSshSigner : ISshSigner, IDisposable
{
    private readonly CngKey _key;
    private readonly InMemorySshSigner _inner;

    private CngSshSigner(CngKey key, InMemorySshSigner inner, string keyName, string provider)
    {
        _key = key;
        _inner = inner;
        KeyName = keyName;
        Provider = provider;
    }

    /// <summary>打开密钥库里一把有名字的钥。</summary>
    /// <param name="keyName">钥的名字（创建它的人起的，如 <c>certutil -csp ... -key</c> 列出的那一列）。</param>
    /// <param name="provider">密钥库；<see langword="null"/> 是软件密钥库（<c>Microsoft Software Key Storage Provider</c>）。
    /// TPM 用 <see cref="CngProvider.MicrosoftPlatformCryptoProvider"/>，智能卡用 <see cref="CngProvider.MicrosoftSmartCardKeyStorageProvider"/>。</param>
    /// <param name="options">打开选项（机器级的钥用 <see cref="CngKeyOpenOptions.MachineKey"/>）。</param>
    /// <exception cref="SshPrivateKeyException">
    /// 找不到这把钥、没有权限（<see cref="SshFailureReason.KeyFileUnreadable"/>），或者它不是能签名的 RSA / NIST ECDSA 钥
    /// （<see cref="SshFailureReason.Unsupported"/>）。
    /// </exception>
    public static CngSshSigner Open(string keyName, CngProvider? provider = null, CngKeyOpenOptions options = CngKeyOpenOptions.None)
    {
        ArgumentException.ThrowIfNullOrEmpty(keyName);
        CngProvider actual = provider ?? CngProvider.MicrosoftSoftwareKeyStorageProvider;

        CngKey key;
        try
        {
            key = CngKey.Open(keyName, actual, options);
        }
        catch (CryptographicException ex)
        {
            throw new SshPrivateKeyException(SshFailureReason.KeyFileUnreadable,
                $"打不开密钥库 {actual.Provider} 里的钥「{keyName}」：{ex.Message}", ex)
            {
                ProblemKind = SshKeyProblemKind.KeyStoreUnavailable,
                Origin = keyName,
            };
        }

        try
        {
            InMemorySshSigner inner = key.AlgorithmGroup == CngAlgorithmGroup.Rsa
                ? InMemorySshSigner.FromRsa(new RSACng(key))
                : key.AlgorithmGroup == CngAlgorithmGroup.ECDsa
                    ? InMemorySshSigner.FromEcdsa(new ECDsaCng(key))
                    : throw new SshPrivateKeyException(SshFailureReason.Unsupported,
                        $"「{keyName}」是 {key.AlgorithmGroup?.AlgorithmGroup ?? "未知"} 钥，不能用来签名：只认 RSA 与 ECDSA。")
                    {
                        ProblemKind = SshKeyProblemKind.UnsupportedKeyType,
                        Origin = keyName,
                        AlgorithmName = key.AlgorithmGroup?.AlgorithmGroup,
                    };
            return new CngSshSigner(key, inner, keyName, actual.Provider);
        }
        catch (Exception ex) when (ex is not SshPrivateKeyException)
        {
            key.Dispose();
            throw new SshPrivateKeyException(SshFailureReason.Unsupported, $"「{keyName}」用不了：{ex.Message}", ex)
            {
                ProblemKind = SshKeyProblemKind.KeyStoreUnavailable,
                Origin = keyName,
            };
        }
        catch
        {
            key.Dispose();
            throw;
        }
    }

    /// <summary>钥的名字。</summary>
    public string KeyName { get; }

    /// <summary>密钥库的名字。</summary>
    public string Provider { get; }

    /// <inheritdoc />
    public SshPublicKey PublicKey => _inner.PublicKey;

    /// <inheritdoc />
    public IReadOnlyList<string> SignatureAlgorithms => _inner.SignatureAlgorithms;

    /// <inheritdoc />
    /// <remarks>TPM、智能卡签一次又慢、又可能要人确认：认证器先问服务端认不认这把钥再签。</remarks>
    public bool IsLocalAndCheap => false;

    /// <inheritdoc />
    public async ValueTask<byte[]> SignAsync(ReadOnlyMemory<byte> data, string algorithm, CancellationToken cancellationToken = default) =>
        // 密钥库签名是同步的、可能很慢（TPM、PIN 框）：放到线程池上，不堵调用方。
        await Task.Run(() => _inner.SignAsync(data, algorithm, cancellationToken).AsTask(), cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    public void Dispose()
    {
        _inner.Dispose();
        _key.Dispose();
    }
}
