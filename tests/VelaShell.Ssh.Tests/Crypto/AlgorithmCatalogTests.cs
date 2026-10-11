// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 被测规格: velashell-docs/zh/ssh/spec/00-overview.md §6

using VelaShell.Ssh.Crypto;
using VelaShell.Ssh.Crypto.Kex;
using VelaShell.Ssh.Protocol;

namespace VelaShell.Ssh.Tests.Crypto;

/// <summary>算法目录与实际实现是同一个口径：目录里说实现了的，工厂表造得出、连接前的校验放得过；没实现的那份一个都不在实现里。</summary>
[TestClass]
[TestCategory("Crypto")]
public sealed class AlgorithmCatalogTests
{
    [TestMethod]
    public void 目录里实现了的都造得出()
    {
        Assert.IsTrue(SshAlgorithmCatalog.Implemented(SshAlgorithmCategory.KeyExchange).All(SshKeyExchangeFactory.IsSupported));
        Assert.IsTrue(SshAlgorithmCatalog.Implemented(SshAlgorithmCategory.Encryption).All(SshSessionKeys.IsSupportedEncryption));
        Assert.IsTrue(SshAlgorithmCatalog.Implemented(SshAlgorithmCategory.Mac).All(SshSessionKeys.IsSupportedMac));

        // 整份目录写成一个清单，连接前的校验要放得过。
        SshAlgorithmSet everything = new()
        {
            KeyExchange = SshAlgorithmCatalog.Implemented(SshAlgorithmCategory.KeyExchange),
            HostKey = SshAlgorithmCatalog.Implemented(SshAlgorithmCategory.HostKey),
            EncryptionClientToServer = SshAlgorithmCatalog.Implemented(SshAlgorithmCategory.Encryption),
            EncryptionServerToClient = SshAlgorithmCatalog.Implemented(SshAlgorithmCategory.Encryption),
            MacClientToServer = SshAlgorithmCatalog.Implemented(SshAlgorithmCategory.Mac),
            MacServerToClient = SshAlgorithmCatalog.Implemented(SshAlgorithmCategory.Mac),
            CompressionClientToServer = SshAlgorithmCatalog.Implemented(SshAlgorithmCategory.Compression),
            CompressionServerToClient = SshAlgorithmCatalog.Implemented(SshAlgorithmCategory.Compression),
        };
        everything.Validate();
    }

    [TestMethod]
    public void 默认清单与老算法都在目录里_默认的排在前面()
    {
        SshAlgorithmSet legacy = SshAlgorithmSet.Default.WithLegacyInterop();
        IReadOnlyList<string> kex = SshAlgorithmCatalog.Implemented(SshAlgorithmCategory.KeyExchange);

        Assert.AreSequenceEqual([.. SshAlgorithmSet.Default.KeyExchange], [.. kex.Take(SshAlgorithmSet.Default.KeyExchange.Count)]);
        Assert.IsTrue(legacy.HostKey.All(a => SshAlgorithmCatalog.IsImplemented(SshAlgorithmCategory.HostKey, a)));
        Assert.IsTrue(legacy.MacClientToServer.All(a => SshAlgorithmCatalog.IsImplemented(SshAlgorithmCategory.Mac, a)));
        Assert.IsTrue(SshAlgorithmCatalog.IsImplemented(SshAlgorithmCategory.Compression, SshAlgorithmNames.ZlibOpenSsh));
    }

    /// <summary>FIPS 认可的清单：全都实现了、校验放得过，而且一个不认可的都不在里面。</summary>
    [TestMethod]
    public void FIPS清单只含认可的算法()
    {
        SshAlgorithmSet fips = SshAlgorithmSet.FipsApprovedOnly;
        fips.Validate();

        string[] all = [.. fips.KeyExchange, .. fips.HostKey, .. fips.EncryptionClientToServer, .. fips.MacClientToServer];
        string[] forbidden = ["25519", "chacha20", "sntrup", "ssh-rsa", "sha1", "-cbc", "zlib"];
        foreach (string algorithm in all)
        {
            Assert.IsFalse(forbidden.Any(f => algorithm.Contains(f, StringComparison.Ordinal)), $"不认可的算法混进来了：{algorithm}");
        }
        Assert.IsTrue(fips.HostKey.All(a => SshAlgorithmCatalog.IsImplemented(SshAlgorithmCategory.HostKey, a)));
    }

    [TestMethod]
    public void 认得却没实现的一个都不在实现里()
    {
        foreach (SshAlgorithmCategory category in Enum.GetValues<SshAlgorithmCategory>())
        {
            IReadOnlyList<string> unimplemented = SshAlgorithmCatalog.KnownUnimplemented(category);
            Assert.IsNotEmpty(unimplemented, category.ToString());
            Assert.IsFalse(unimplemented.Any(n => SshAlgorithmCatalog.IsImplemented(category, n)), category.ToString());
        }

        Assert.IsFalse(SshKeyExchangeFactory.IsSupported("diffie-hellman-group1-sha1"));
        Assert.IsFalse(SshSessionKeys.IsSupportedEncryption("aes256-cbc"));
        Assert.IsFalse(SshSessionKeys.IsSupportedMac("umac-128-etm@openssh.com"));
    }
}
