// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 被测规格: velashell-docs/zh/ssh/spec/04-authentication.md §4.7;OpenSSH PROTOCOL.u2f

using System.Buffers;
using System.Security.Cryptography;
using VelaShell.Ssh.Auth;
using VelaShell.Ssh.HostKeys;
using VelaShell.Ssh.Keys;
using VelaShell.Ssh.Protocol;
using VelaShell.Ssh.Tests.TestKit;
using VelaShell.Ssh.Transport;

namespace VelaShell.Ssh.Tests.Keys;

/// <summary>
/// FIDO / U2F 安全密钥（sk-*@openssh.com）经 agent 用：认得公钥、列得出来、当凭据出示。
/// 公钥格式另由真 ssh-keygen 读同样的字节核对（互通用例）。
/// </summary>
[TestClass]
[TestCategory("Keys")]
public sealed class SecurityKeyTests
{
    internal static byte[] Ed25519SkBlob(byte[] publicKey, string application = "ssh:")
    {
        ArrayBufferWriter<byte> buffer = new();
        SshDataWriter writer = new(buffer);
        writer.WriteUtf8String(SshAlgorithmNames.SkSshEd25519);
        writer.WriteString(publicKey);
        writer.WriteUtf8String(application);
        return buffer.WrittenSpan.ToArray();
    }

    internal static byte[] EcdsaSkBlob(string application = "ssh:")
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        ECParameters p = key.ExportParameters(false);
        ArrayBufferWriter<byte> buffer = new();
        SshDataWriter writer = new(buffer);
        writer.WriteUtf8String(SshAlgorithmNames.SkEcdsaSha2Nistp256);
        writer.WriteUtf8String("nistp256");
        writer.WriteString([0x04, .. p.Q.X!, .. p.Q.Y!]);
        writer.WriteUtf8String(application);
        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>两种 sk 公钥：类型、位数、application、签名算法名；文本形式往返不变。签名不验（客户端用不上）。</summary>
    [TestMethod]
    public void 安全密钥的公钥认得出来()
    {
        using var ed = InMemorySshSigner.GenerateEd25519();
        byte[] edBlob = Ed25519SkBlob(ed.PublicKey.Blob[^32..].ToArray(), "ssh:work");
        var sk = SshPublicKey.Decode(edBlob);

        Assert.AreEqual(SshAlgorithmNames.SkSshEd25519, sk.KeyType);
        Assert.AreEqual(256, sk.KeyBits);
        Assert.IsTrue(sk.IsSecurityKey);
        Assert.AreEqual("ssh:work", sk.SecurityKeyApplication);
        Assert.AreSequenceEqual([SshAlgorithmNames.SkSshEd25519], [.. sk.SignatureAlgorithms]);
        Assert.AreEqual(sk, SshPublicKey.Parse(sk.ToOpenSshFormat()));
        Assert.IsFalse(sk.VerifySignature([0, 0, 0, 0], [1], SshAlgorithmNames.SkSshEd25519));

        var ecdsa = SshPublicKey.Decode(EcdsaSkBlob());
        Assert.AreEqual(SshAlgorithmNames.SkEcdsaSha2Nistp256, ecdsa.KeyType);
        Assert.AreEqual("ssh:", ecdsa.SecurityKeyApplication);
        Assert.IsFalse(ed.PublicKey.IsSecurityKey);
    }

    /// <summary>写坏的：公钥不是 32 字节、后面多东西、曲线不对 —— 格式错误。</summary>
    [TestMethod]
    public void 写坏的安全密钥公钥报格式错误()
    {
        Assert.ThrowsExactly<SshPublicKeyException>(() => SshPublicKey.Decode(Ed25519SkBlob(new byte[31])));
        Assert.ThrowsExactly<SshPublicKeyException>(() => SshPublicKey.Decode(new byte[][] { Ed25519SkBlob(new byte[32]), [0] }.SelectMany(b => b).ToArray()));

        ArrayBufferWriter<byte> buffer = new();
        SshDataWriter writer = new(buffer);
        writer.WriteUtf8String(SshAlgorithmNames.SkEcdsaSha2Nistp256);
        writer.WriteUtf8String("nistp384");
        writer.WriteString(new byte[97]);
        writer.WriteUtf8String("ssh:");
        Assert.ThrowsExactly<SshPublicKeyException>(() => SshPublicKey.Decode(buffer.WrittenSpan.ToArray()));
    }

    /// <summary>agent 里的安全密钥：列得出来（原来直接跳过），当凭据出示时用 sk 的签名算法名。</summary>
    [TestMethod]
    public async Task Agent里的安全密钥列得出来也能当凭据()
    {
        TestAgent agent = new();
        using var ed = InMemorySshSigner.GenerateEd25519();
        agent.AddOpaque(Ed25519SkBlob(ed.PublicKey.Blob[^32..].ToArray()), "yubikey");

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(30));
        (InMemoryDuplexStream ours, InMemoryDuplexStream theirs) = InMemoryTransport.CreatePair();
        var serving = Task.Run(() => agent.ServeAsync(theirs, cts.Token));
        await using (var client = SshAgentClient.FromStream(ours, "(测试 agent)"))
        {
            IReadOnlyList<SshAgentIdentity> identities = await client.ListIdentitiesAsync(cts.Token);
            SshAgentIdentity identity = identities.Single();
            Assert.IsTrue(identity.PublicKey.IsSecurityKey);
            Assert.AreEqual("yubikey", identity.Comment);

            IReadOnlyList<SshCredential> credentials = await client.GetCredentialsAsync(cts.Token);
            var credential = (PublicKeyCredential)credentials.Single();
            Assert.AreSequenceEqual([SshAlgorithmNames.SkSshEd25519], [.. credential.Signer.SignatureAlgorithms]);
        }
        await cts.CancelAsync();
        await serving;
    }
}
