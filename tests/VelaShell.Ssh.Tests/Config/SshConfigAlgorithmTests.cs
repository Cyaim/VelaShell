// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 被测规格: velashell-docs/zh/ssh/spec/09-dialing.md §7.2

using VelaShell.Ssh.Config;
using VelaShell.Ssh.Crypto;
using VelaShell.Ssh.Diagnostics;
using VelaShell.Ssh.Protocol;
using VelaShell.Ssh.Session;

namespace VelaShell.Ssh.Tests.Config;

/// <summary>算法清单的 <c>+ - ^</c> 写法，以及 <c>ssh_config</c> 里那几项落到连接参数上。</summary>
[TestClass]
[TestCategory("Config")]
public sealed class SshConfigAlgorithmTests
{
    private static readonly string[] Defaults = ["a", "b", "c"];

    /// <summary>四种写法：追加、提前、删除（含通配）、整个替换；重复的名字只算一次。</summary>
    [TestMethod]
    public void 四种写法()
    {
        const SshAlgorithmCategory cipher = SshAlgorithmCategory.Encryption;
        string[] defaults = [SshAlgorithmNames.Aes256Gcm, SshAlgorithmNames.ChaCha20Poly1305, SshAlgorithmNames.Aes128Ctr];

        Assert.AreSequenceEqual(
            [SshAlgorithmNames.Aes256Gcm, SshAlgorithmNames.ChaCha20Poly1305, SshAlgorithmNames.Aes128Ctr, SshAlgorithmNames.Aes256Ctr],
            [.. SshAlgorithmSpec.Apply(cipher, $"+{SshAlgorithmNames.Aes256Ctr},{SshAlgorithmNames.Aes128Ctr}", defaults)]);
        Assert.AreSequenceEqual(
            [SshAlgorithmNames.Aes128Ctr, SshAlgorithmNames.Aes256Gcm, SshAlgorithmNames.ChaCha20Poly1305],
            [.. SshAlgorithmSpec.Apply(cipher, $"^{SshAlgorithmNames.Aes128Ctr}", defaults)]);
        Assert.AreSequenceEqual(
            [SshAlgorithmNames.Aes256Gcm, SshAlgorithmNames.Aes128Ctr],
            [.. SshAlgorithmSpec.Apply(cipher, "-chacha20*", defaults)]);
        Assert.AreSequenceEqual(
            [SshAlgorithmNames.Aes128Ctr],
            [.. SshAlgorithmSpec.Apply(cipher, $"{SshAlgorithmNames.Aes128Ctr}, {SshAlgorithmNames.Aes128Ctr}", defaults)]);
        Assert.AreSame(Defaults, SshAlgorithmSpec.Apply(cipher, "  ", Defaults), "空白 = 原样用默认");
    }

    /// <summary>写错的四种：没名字、不认识、认得却没实现、删完不剩；拼错的删除项同样报（不然什么都没删掉）。</summary>
    [TestMethod]
    public void 写错的当场报出来()
    {
        SshAlgorithmSpecProblem ProblemOf(string spec) =>
            Assert.ThrowsExactly<SshAlgorithmSpecException>(
                () => SshAlgorithmSpec.Apply(SshAlgorithmCategory.Encryption, spec, [SshAlgorithmNames.Aes128Ctr])).Problem;

        Assert.AreEqual(SshAlgorithmSpecProblem.Empty, ProblemOf("+ , "));
        Assert.AreEqual(SshAlgorithmSpecProblem.Unknown, ProblemOf("aes128-gcm"));
        Assert.AreEqual(SshAlgorithmSpecProblem.Unimplemented, ProblemOf("+aes256-cbc"));
        Assert.AreEqual(SshAlgorithmSpecProblem.Unknown, ProblemOf("-chacha20-poly1305"), "拼错的删除项（少了 @openssh.com）");
        Assert.AreEqual(SshAlgorithmSpecProblem.NothingLeft, ProblemOf("-aes*"));
    }

    /// <summary>
    /// ssh_config 的四项作用到默认清单上（加密与 MAC 两个方向一起）；<c>PubkeyAcceptedAlgorithms +ssh-rsa</c>（老服务器最常见的那一行）
    /// 放开 SHA-1 的 RSA 签名；不写就不放开。
    /// </summary>
    [TestMethod]
    public async Task 配置里的算法清单落到连接参数上()
    {
        IReadOnlyList<SshConfigBlock> blocks = SshConfigFile.Parse($"""
            Host old
                KexAlgorithms ^{SshAlgorithmNames.DiffieHellmanGroupExchangeSha256}
                Ciphers -chacha20*
                MACs {SshAlgorithmNames.HmacSha256}
                HostKeyAlgorithms +{SshAlgorithmNames.SshRsa}
                PubkeyAcceptedAlgorithms +{SshAlgorithmNames.SshRsa}
            Host new
                HostName 10.0.0.1
            """);

        SshConnectionOptions old = await SshConfigFile.CreateConnectionOptionsAsync(blocks, "old");
        Assert.AreEqual(SshAlgorithmNames.DiffieHellmanGroupExchangeSha256, old.Algorithms.KeyExchange[0]);
        Assert.DoesNotContain(SshAlgorithmNames.ChaCha20Poly1305, old.Algorithms.EncryptionClientToServer);
        Assert.AreSequenceEqual([.. old.Algorithms.EncryptionClientToServer], [.. old.Algorithms.EncryptionServerToClient]);
        Assert.AreSequenceEqual([SshAlgorithmNames.HmacSha256], [.. old.Algorithms.MacServerToClient]);
        Assert.AreEqual(SshAlgorithmNames.SshRsa, old.Algorithms.HostKey[^1]);
        Assert.IsTrue(old.AllowSha1RsaSignatures);

        SshConnectionOptions fresh = await SshConfigFile.CreateConnectionOptionsAsync(blocks, "new");
        Assert.AreSequenceEqual([.. SshAlgorithmSet.Default.KeyExchange], [.. fresh.Algorithms.KeyExchange]);
        Assert.IsFalse(fresh.AllowSha1RsaSignatures);
    }

    /// <summary>配置里写错是配置错误：InvalidConfiguration，消息说清是哪台主机。</summary>
    [TestMethod]
    public async Task 配置里写错的清单报配置错误()
    {
        IReadOnlyList<SshConfigBlock> blocks = SshConfigFile.Parse("""
            Host legacy
                Ciphers +aes256-cbc
            """);

        SshConnectException failure = await Assert.ThrowsAsync<SshConnectException>(
            async () => await SshConfigFile.CreateConnectionOptionsAsync(blocks, "legacy"));
        Assert.AreEqual(SshFailureReason.InvalidConfiguration, failure.Reason);
        Assert.Contains("legacy", failure.Message);
        Assert.IsInstanceOfType<SshAlgorithmSpecException>(failure.InnerException);
    }
}
