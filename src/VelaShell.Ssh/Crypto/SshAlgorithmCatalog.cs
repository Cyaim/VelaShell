// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 行为规格: velashell-docs/zh/ssh/spec/00-overview.md §6

using System.Collections.Frozen;
using VelaShell.Ssh.Protocol;

namespace VelaShell.Ssh.Crypto;

/// <summary>本库的算法目录：每一类实现了哪些名字，以及常见却没实现的那些。</summary>
/// <remarks>
/// <para>
/// 给使用者校验、提示用 —— 比如让用户照着 <c>ssh_config</c> 写一条自定义清单时，当场说清「这个名字本库没实现」
/// 还是「这个名字不认识」。曾经没有这份目录，宿主只好用 <c>Default.WithLegacyInterop()</c> 去推算实现了哪些，
/// 再手工维护一份「认得但没实现」的名单。
/// </para>
/// <para>
/// 实现了的那一份按偏好顺序排：默认清单在前，<see cref="SshAlgorithmSet.WithLegacyInterop"/> 放开的老算法在后。
/// 它与连接前的校验（<c>SshAlgorithmSet.Validate()</c>）、各个工厂表是同一个口径，用例逐个核对。
/// </para>
/// </remarks>
public static class SshAlgorithmCatalog
{
    private static readonly SshAlgorithmSet Everything = SshAlgorithmSet.Default.WithLegacyInterop();

    private static readonly FrozenDictionary<SshAlgorithmCategory, IReadOnlyList<string>> ImplementedNames =
        new Dictionary<SshAlgorithmCategory, IReadOnlyList<string>>
        {
            [SshAlgorithmCategory.KeyExchange] = Everything.KeyExchange,
            [SshAlgorithmCategory.HostKey] = Everything.HostKey,
            [SshAlgorithmCategory.Encryption] = Everything.EncryptionClientToServer,
            [SshAlgorithmCategory.Mac] = Everything.MacClientToServer,
            [SshAlgorithmCategory.Compression] = Array.AsReadOnly([SshAlgorithmNames.None, SshAlgorithmNames.ZlibOpenSsh]),
        }.ToFrozenDictionary();

    /// <summary>OpenSSH 认得、常见于抄来的配置里，本库却没实现的名字（老、弱，或者还没做）。</summary>
    private static readonly FrozenDictionary<SshAlgorithmCategory, IReadOnlyList<string>> KnownUnimplementedNames =
        new Dictionary<SshAlgorithmCategory, IReadOnlyList<string>>
        {
            [SshAlgorithmCategory.KeyExchange] = Array.AsReadOnly(
            [
                "diffie-hellman-group1-sha1", "diffie-hellman-group-exchange-sha1",
            ]),
            [SshAlgorithmCategory.HostKey] = Array.AsReadOnly(
            [
                "ssh-dss", "ssh-dss-cert-v01@openssh.com", "ssh-rsa-cert-v01@openssh.com",
                "sk-ssh-ed25519@openssh.com", "sk-ecdsa-sha2-nistp256@openssh.com",
                "sk-ssh-ed25519-cert-v01@openssh.com", "sk-ecdsa-sha2-nistp256-cert-v01@openssh.com",
            ]),
            [SshAlgorithmCategory.Encryption] = Array.AsReadOnly(
            [
                "aes128-cbc", "aes192-cbc", "aes256-cbc", "3des-cbc", "blowfish-cbc", "cast128-cbc",
                "arcfour", "arcfour128", "arcfour256", "rijndael-cbc@lysator.liu.se",
            ]),
            [SshAlgorithmCategory.Mac] = Array.AsReadOnly(
            [
                "hmac-md5", "hmac-md5-96", "hmac-md5-etm@openssh.com", "hmac-md5-96-etm@openssh.com",
                "hmac-sha1-96", "hmac-sha1-96-etm@openssh.com", "hmac-ripemd160", "hmac-ripemd160@openssh.com",
                "umac-64@openssh.com", "umac-128@openssh.com", "umac-64-etm@openssh.com", "umac-128-etm@openssh.com",
            ]),
            [SshAlgorithmCategory.Compression] = Array.AsReadOnly(["zlib"]),
        }.ToFrozenDictionary();

    /// <summary>这一类本库实现了的名字，按偏好顺序（默认清单在前，老算法在后）。</summary>
    /// <param name="category">类别。</param>
    /// <exception cref="ArgumentOutOfRangeException">不认识的类别。</exception>
    public static IReadOnlyList<string> Implemented(SshAlgorithmCategory category) =>
        ImplementedNames.TryGetValue(category, out IReadOnlyList<string>? names)
            ? names
            : throw new ArgumentOutOfRangeException(nameof(category), category, null);

    /// <summary>这一类里常见、本库却没实现的名字（写进清单也谈不成）。</summary>
    /// <param name="category">类别。</param>
    /// <exception cref="ArgumentOutOfRangeException">不认识的类别。</exception>
    public static IReadOnlyList<string> KnownUnimplemented(SshAlgorithmCategory category) =>
        KnownUnimplementedNames.TryGetValue(category, out IReadOnlyList<string>? names)
            ? names
            : throw new ArgumentOutOfRangeException(nameof(category), category, null);

    /// <summary>这个名字在这一类里本库实现了没有（大小写敏感，与协议一致）。</summary>
    /// <param name="category">类别。</param>
    /// <param name="name">算法名。</param>
    public static bool IsImplemented(SshAlgorithmCategory category, string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return Implemented(category).Contains(name, StringComparer.Ordinal);
    }
}
