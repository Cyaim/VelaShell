// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 被测规格: velashell-docs/zh/ssh/spec/03-key-exchange.md §3.7(mlkem768nistp256-sha256 / mlkem1024nistp384-sha384)、
//           velashell-docs/zh/ssh/spec/00-overview.md §6.1、§6.6(清单里的位置)
//
// RFC 10042 没有测试向量：两端的 K 一致靠 TestKexResponder 独立地算一遍，线上格式靠对真服务端的互操作用例。

using VelaShell.Ssh.Crypto;
using VelaShell.Ssh.Crypto.Kex;
using VelaShell.Ssh.Protocol;
using VelaShell.Ssh.Tests.TestKit;

namespace VelaShell.Ssh.Tests.Crypto;

[TestClass]
[TestCategory("Crypto")]
public sealed class NistHybridKeyExchangeTests
{
    private const string P256 = SshAlgorithmNames.MlKem768Nistp256Sha256;
    private const string P384 = SshAlgorithmNames.MlKem1024Nistp384Sha384;

    /// <summary>（C_INIT 字节数、S_REPLY 字节数、坐标字节数、K 字节数）—— spec/03 §3.7 的表。</summary>
    private static (int ClientInit, int ServerReply, int Coordinate, int Secret) Sizes(string name) =>
        name == P256 ? (1249, 1153, 32, 32) : (1665, 1665, 48, 48);

    [TestMethod]
    [DataRow(P256)]
    [DataRow(P384)]
    public void 两端算出同一个K且长度照表(string name)
    {
        (int clientInit, int serverReply, _, int secretBytes) = Sizes(name);
        using HybridKeyExchange client = new(name);

        byte[] init = client.CreateClientPublicValue();
        Assert.HasCount(clientInit, init);
        Assert.AreEqual((byte)0x04, init[clientInit - (name == P256 ? 65 : 97)], "ek_pq 之后是未压缩点");
        Assert.AreEqual(SshKexValueEncoding.ByteString, client.SharedSecretEncoding, "K 按 string，不是 mpint");

        TestKexResponse reply = TestKexResponder.Respond(name, init);
        Assert.HasCount(serverReply, reply.ServerPublicValue);

        byte[] secret = client.ComputeSharedSecret(reply.ServerPublicValue);
        Assert.HasCount(secretBytes, secret);
        Assert.AreSequenceEqual(reply.SharedSecret, secret);
    }

    /// <summary>
    /// 共享点 X 坐标的首字节为 0 时（约 1/256）：<c>K_CL</c> 仍是 32 / 48 字节、前导零保留，两端算出同一个 <c>K</c>。
    /// 照 §3.3 的习惯去掉前导零的实现只在这时出错 —— 平常的用例碰不到，这里反复交换直到碰到为止。
    /// </summary>
    [TestMethod]
    [DataRow(P256)]
    [DataRow(P384)]
    public void X坐标首字节为零时K_CL保留前导零(string name)
    {
        int coordinate = Sizes(name).Coordinate;
        for (int attempt = 0; attempt < 20_000; attempt++)
        {
            using HybridKeyExchange client = new(name);
            byte[] init = client.CreateClientPublicValue();
            TestKexResponse reply = TestKexResponder.RespondNistHybrid(name, init, out byte[] classical);
            if (classical[0] != 0)
            {
                continue;
            }

            Assert.HasCount(coordinate, classical);
            Assert.AreSequenceEqual(reply.SharedSecret, client.ComputeSharedSecret(reply.ServerPublicValue),
                $"第 {attempt + 1} 次交换碰到了前导零，两端的 K 却不一致");
            return;
        }
        Assert.Fail("两万次交换都没碰到 X 坐标首字节为 0 —— 概率上不该发生。");
    }

    /// <summary>每次交换都新生成一对 ML-KEM 与一对 ECDH 密钥（RFC 10042 §5）：两个实例的 C_INIT 没有一段相同。</summary>
    [TestMethod]
    [DataRow(P256)]
    [DataRow(P384)]
    public void 每次交换都用新的临时密钥(string name)
    {
        using HybridKeyExchange first = new(name), second = new(name);
        byte[] a = first.CreateClientPublicValue(), b = second.CreateClientPublicValue();
        int pointBytes = 1 + (2 * Sizes(name).Coordinate);
        Assert.IsFalse(a.AsSpan(0, a.Length - pointBytes).SequenceEqual(b.AsSpan(0, b.Length - pointBytes)), "ML-KEM 封装密钥复用了");
        Assert.IsFalse(a.AsSpan(a.Length - pointBytes).SequenceEqual(b.AsSpan(b.Length - pointBytes)), "ECDH 公钥复用了");
    }

    // ------------------------------------------------------------ 负面（spec/03 §3.7.3、§3.7.7 第 7 条）

    private static (HybridKeyExchange Client, byte[] Reply) Exchange(string name)
    {
        HybridKeyExchange client = new(name);
        return (client, TestKexResponder.Respond(name, client.CreateClientPublicValue()).ServerPublicValue);
    }

    [TestMethod]
    [DataRow(P256, -1)]
    [DataRow(P256, 1)]
    [DataRow(P384, -1)]
    [DataRow(P384, 1)]
    public void 服务端回的S_REPLY长度不对时在解封装之前就拒绝(string name, int delta)
    {
        (HybridKeyExchange client, byte[] reply) = Exchange(name);
        using (client)
        {
            byte[] mangled = delta < 0 ? reply[..^1] : [.. reply, 0];
            SshKeyExchangeException error = Assert.ThrowsExactly<SshKeyExchangeException>(() => client.ComputeSharedSecret(mangled));
            Assert.Contains($"{Sizes(name).ServerReply} 字节", error.Message);
        }
    }

    [TestMethod]
    [DataRow(P256, (byte)0x02)]
    [DataRow(P256, (byte)0x03)]
    [DataRow(P384, (byte)0x02)]
    [DataRow(P384, (byte)0x03)]
    public void 不收压缩点(string name, byte tag)
    {
        (HybridKeyExchange client, byte[] reply) = Exchange(name);
        using (client)
        {
            reply[^(1 + (2 * Sizes(name).Coordinate))] = tag;
            Assert.ThrowsExactly<SshKeyExchangeException>(() => client.ComputeSharedSecret(reply));
        }
    }

    [TestMethod]
    [DataRow(P256)]
    [DataRow(P384)]
    public void 不收不在曲线上的点(string name)
    {
        int coordinate = Sizes(name).Coordinate;
        int point = Sizes(name).ServerReply - (1 + (2 * coordinate));

        (HybridKeyExchange flipped, byte[] reply) = Exchange(name);
        using (flipped)
        {
            reply[^1] ^= 0x01;   // Y 改一个比特
            Assert.ThrowsExactly<SshKeyExchangeException>(() => flipped.ComputeSharedSecret(reply));
        }

        (HybridKeyExchange zero, byte[] zeroReply) = Exchange(name);
        using (zero)
        {
            zeroReply.AsSpan(point + 1).Clear();   // 全零的点（无穷远点写不出来）
            Assert.ThrowsExactly<SshKeyExchangeException>(() => zero.ComputeSharedSecret(zeroReply));
        }

        (HybridKeyExchange atPrime, byte[] primeReply) = Exchange(name);
        using (atPrime)
        {
            byte[] prime = Convert.FromHexString(name == P256
                ? "FFFFFFFF00000001000000000000000000000000FFFFFFFFFFFFFFFFFFFFFFFF"
                : "FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFEFFFFFFFF0000000000000000FFFFFFFF");
            prime.CopyTo(primeReply.AsSpan(point + 1));   // X = p：编码不合法（坐标必须小于 p）
            Assert.ThrowsExactly<SshKeyExchangeException>(() => atPrime.ComputeSharedSecret(primeReply));
        }
    }

    /// <summary>密文改一个比特：ML-KEM 隐式拒绝，交换本身不报错，算出的 K 与服务端的不同 —— 之后的签名验证会失败。</summary>
    [TestMethod]
    [DataRow(P256)]
    [DataRow(P384)]
    public void 密文被改过时交换不报错而K不同(string name)
    {
        using HybridKeyExchange client = new(name);
        TestKexResponse reply = TestKexResponder.Respond(name, client.CreateClientPublicValue());
        byte[] tampered = [.. reply.ServerPublicValue];
        tampered[10] ^= 0x01;

        byte[] secret = client.ComputeSharedSecret(tampered);
        Assert.IsFalse(secret.AsSpan().SequenceEqual(reply.SharedSecret));
    }

    // ------------------------------------------------------------ 清单里的位置（spec/00 §6.1、§6.6）

    [TestMethod]
    public void 默认清单与FIPS清单里的位置()
    {
        IReadOnlyList<string> defaults = SshAlgorithmSet.Default.KeyExchange;
        Assert.AreSequenceEqual(
            [SshAlgorithmNames.MlKem768X25519Sha256, P256, P384, SshAlgorithmNames.Sntrup761X25519Sha512],
            [.. defaults.Take(4)],
            "X25519 那种之后、sntrup761 之前，768 在前");

        Assert.AreSequenceEqual(
            [P256, P384, SshAlgorithmNames.EcdhSha2Nistp256],
            [.. SshAlgorithmSet.FipsApprovedOnly.KeyExchange.Take(3)],
            "FIPS 清单里排最前，然后才是不带后量子的 ECDH");

        Assert.IsTrue(SshKeyExchangeFactory.IsSupported(P256) && SshKeyExchangeFactory.IsSupported(P384));
    }
}
