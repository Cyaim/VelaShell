// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 被测规格: velashell-docs/zh/ssh/spec/03-key-exchange.md §3（七种 KEX）、§4（交换哈希）、§7（密钥派生）
//
// 这一套**自己扮演服务端**：用 BC/BCL 直接做对侧的运算，验证两边算出同一个共享密钥。
// 只测「客户端不抛异常」是没有意义的 —— 共享密钥算错了照样不抛，
// 只会在后面的签名验证里表现为一句看不出原因的失败。

using System.Security.Cryptography;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Agreement;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Kems;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Pqc.Crypto.NtruPrime;
using Org.BouncyCastle.Security;
using VelaShell.Ssh.Crypto;
using VelaShell.Ssh.Crypto.Kex;
using VelaShell.Ssh.Protocol;

namespace VelaShell.Ssh.Tests.Crypto;

[TestClass]
[TestCategory("Crypto")]
public sealed class KeyExchangeTests
{
    // ------------------------------------------------------- ML-KEM 的两种实现

    /// <summary>
    /// 〔AGENTS 3.3〕ML-KEM（768 / 1024）平台支持时走 BCL、否则走 BouncyCastle —— 两种实现都会被用到，必须互通：
    /// 一边生成、另一边封装、生成的那边解封装，共享密钥一致。
    /// </summary>
    [TestMethod]
    [DataRow(768)]
    [DataRow(1024)]
    public void MLKem的BCL实现与BouncyCastle实现互通(int level)
    {
        if (!MLKem.IsSupported)
        {
            Assert.Inconclusive("这个平台的 BCL 不支持 ML-KEM，用的是 BouncyCastle。");
        }
        MLKemAlgorithm algorithm = level == 1024 ? MLKemAlgorithm.MLKem1024 : MLKemAlgorithm.MLKem768;
        MLKemParameters parameters = level == 1024 ? MLKemParameters.ml_kem_1024 : MLKemParameters.ml_kem_768;

        // BCL 生成、BouncyCastle 封装、BCL 解封装。
        using HybridKeyExchange.BclMlKem bcl = new(algorithm);
        byte[] bclPublic = bcl.GenerateKeyPairAndGetPublicKey();
        MLKemEncapsulator encapsulator = new(parameters);
        encapsulator.Init(MLKemPublicKeyParameters.FromEncoding(parameters, bclPublic));
        byte[] ciphertext = new byte[encapsulator.EncapsulationLength];
        byte[] secret = new byte[encapsulator.SecretLength];
        encapsulator.Encapsulate(ciphertext, 0, ciphertext.Length, secret, 0, secret.Length);
        Assert.AreSequenceEqual(secret, bcl.Decapsulate(ciphertext));

        // BouncyCastle 生成、BCL 封装、BouncyCastle 解封装。
        using HybridKeyExchange.BouncyCastleMlKem bouncy = new(parameters);
        byte[] bouncyPublic = bouncy.GenerateKeyPairAndGetPublicKey();
        Assert.AreEqual(algorithm.CiphertextSizeInBytes, bouncy.CiphertextBytes);
        using var encapsulationKey = MLKem.ImportEncapsulationKey(algorithm, bouncyPublic);
        encapsulationKey.Encapsulate(out byte[] ciphertext2, out byte[] secret2);
        Assert.AreSequenceEqual(secret2, bouncy.Decapsulate(ciphertext2));
    }
    // ------------------------------------------------------- 椭圆曲线 / 有限域

    [TestMethod]
    public void Curve25519两端算出同一个共享密钥()
    {
        using Curve25519KeyExchange client = new();
        byte[] clientPublic = client.CreateClientPublicValue();
        Assert.HasCount(32, clientPublic);

        // 服务端那一侧
        X25519PrivateKeyParameters serverPrivate = new(new SecureRandom());
        byte[] serverPublic = serverPrivate.GeneratePublicKey().GetEncoded();

        byte[] serverSecret = new byte[32];
        X25519Agreement agreement = new();
        agreement.Init(serverPrivate);
        agreement.CalculateAgreement(new X25519PublicKeyParameters(clientPublic), serverSecret, 0);

        byte[] clientSecret = client.ComputeSharedSecret(serverPublic);
        Assert.AreSequenceEqual(serverSecret, clientSecret);
    }

    [TestMethod]
    public void Curve25519拒绝全零结果()
    {
        // RFC 7748 §6.1 的 contributory behaviour：低阶点会让共享密钥与我方私钥无关，
        // 对端可以单方面决定它。X25519 的低阶点之一是全零公钥。
        using Curve25519KeyExchange client = new();
        _ = client.CreateClientPublicValue();

        Assert.ThrowsExactly<SshKeyExchangeException>(
            () => client.ComputeSharedSecret(new byte[32]));
    }

    /// <summary>
    /// 密钥交换的失败是建连阶段的失败（<see cref="Diagnostics.SshConnectException"/>），与协商失败同一层：
    /// 按 getting-started 的说法分流的调用方曾经把它落进兜底分支。
    /// </summary>
    [TestMethod]
    public void 密钥交换失败是建连阶段的失败()
    {
        using Curve25519KeyExchange client = new();
        _ = client.CreateClientPublicValue();

        SshKeyExchangeException error = Assert.ThrowsExactly<SshKeyExchangeException>(() => client.ComputeSharedSecret(new byte[31]));

        Assert.IsInstanceOfType<Diagnostics.SshConnectException>(error);
        Assert.AreEqual(Diagnostics.SshFailureReason.ProtocolError, error.Reason);
        Assert.AreEqual(Diagnostics.SshPhase.KeyExchange, error.Phase);
    }

    /// <summary>算法清单与工厂表对不上是库自己的编程错误：<see cref="InvalidOperationException"/>，不借对端的名义报协议错误。</summary>
    [TestMethod]
    [DataRow("no-such-cipher@example.com", "hmac-sha2-256", DisplayName = "加密算法")]
    [DataRow("aes128-ctr", "no-such-mac@example.com", DisplayName = "MAC 算法")]
    [DataRow("aes128-ctr", null, DisplayName = "非 AEAD 却没有 MAC")]
    public void 工厂表里没有的算法是编程错误(string encryption, string? mac)
    {
        SshNegotiatedAlgorithms algorithms = new(
            "curve25519-sha256", "ssh-ed25519", encryption, encryption, mac, mac, "none", "none",
            StrictKeyExchange: true, PeerSupportsExtensionInfo: false);

        Assert.ThrowsExactly<InvalidOperationException>(() => SshSessionKeys.Derive(
            algorithms, HashAlgorithmName.SHA256, new byte[32], SshKexValueEncoding.Mpint, new byte[32], new byte[32]));
    }

    /// <summary>
    /// ECDH 的「点在不在曲线上」先由库自己查（方程与坐标范围），不全靠平台后端的导入校验 ——
    /// 那一层各平台各是各的（CNG / OpenSSL / Apple），曾经只在 Windows 上验证过。
    /// </summary>
    [TestMethod]
    [DataRow(SshAlgorithmNames.EcdhSha2Nistp256, 32)]
    [DataRow(SshAlgorithmNames.EcdhSha2Nistp384, 48)]
    [DataRow(SshAlgorithmNames.EcdhSha2Nistp521, 66)]
    public void ECDH自己核对点在曲线上(string name, int coord)
    {
        using EcdhKeyExchange exchange = new(name);
        ECCurve curve = name switch
        {
            SshAlgorithmNames.EcdhSha2Nistp256 => ECCurve.NamedCurves.nistP256,
            SshAlgorithmNames.EcdhSha2Nistp384 => ECCurve.NamedCurves.nistP384,
            _ => ECCurve.NamedCurves.nistP521,
        };

        // 平台造出来的公钥一定在曲线上。
        using var peer = ECDiffieHellman.Create(curve);
        ECParameters q = peer.ExportParameters(includePrivateParameters: false);
        byte[] x = new byte[coord];
        byte[] y = new byte[coord];
        q.Q.X!.CopyTo(x, coord - q.Q.X!.Length);
        q.Q.Y!.CopyTo(y, coord - q.Q.Y!.Length);
        Assert.IsTrue(exchange.IsOnCurve(x, y), "合法的点被拒了");

        // y 改一个比特就不在曲线上了；全零（无穷远点写不出来）也不在。
        byte[] tampered = (byte[])y.Clone();
        tampered[^1] ^= 1;
        Assert.IsFalse(exchange.IsOnCurve(x, tampered));
        Assert.IsFalse(exchange.IsOnCurve(new byte[coord], new byte[coord]));

        // 曲线常数与平台给的一致（支持导出显式参数的平台上核对；macOS 不支持就只靠上面的用例）。
        try
        {
            ECParameters explicitParameters = peer.ExportExplicitParameters(includePrivateParameters: false);
            System.Numerics.BigInteger p = new(explicitParameters.Curve.Prime!, isUnsigned: true, isBigEndian: true);
            System.Numerics.BigInteger a = new(explicitParameters.Curve.A!, isUnsigned: true, isBigEndian: true);
            Assert.AreEqual(p - 3, a, "三条 NIST 曲线的 a 都是 −3");

            // P-521 的坐标有 66 字节、p 只有 521 位：x + p 编得进去，与 x 同余、方程照样成立，必须按「坐标不小于 p」拦下。
            if (name == SshAlgorithmNames.EcdhSha2Nistp521)
            {
                System.Numerics.BigInteger shifted = new System.Numerics.BigInteger(x, isUnsigned: true, isBigEndian: true) + p;
                byte[] big = shifted.ToByteArray(isUnsigned: true, isBigEndian: true);
                byte[] encoded = new byte[coord];
                big.CopyTo(encoded, coord - big.Length);
                Assert.IsFalse(exchange.IsOnCurve(encoded, y), "坐标不小于 p 的编码不合法");
            }
        }
        catch (PlatformNotSupportedException)
        {
        }
    }

    [TestMethod]
    public void Curve25519拒绝长度不对的公钥()
    {
        using Curve25519KeyExchange client = new();
        _ = client.CreateClientPublicValue();

        Assert.ThrowsExactly<SshKeyExchangeException>(() => client.ComputeSharedSecret(new byte[31]));
        Assert.ThrowsExactly<SshKeyExchangeException>(() => client.ComputeSharedSecret(new byte[33]));
    }

    [TestMethod]
    public void Ecdh三条曲线都能算出同一个共享密钥()
    {
        (string Name, ECCurve Curve, int Coord)[] cases =
        [
            (SshAlgorithmNames.EcdhSha2Nistp256, ECCurve.NamedCurves.nistP256, 32),
            (SshAlgorithmNames.EcdhSha2Nistp384, ECCurve.NamedCurves.nistP384, 48),
            // 521 位 → 66 字节。按 64/65 写死的实现在这条上崩，而它最少被测到。
            (SshAlgorithmNames.EcdhSha2Nistp521, ECCurve.NamedCurves.nistP521, 66),
        ];

        foreach ((string name, ECCurve curve, int coord) in cases)
        {
            using EcdhKeyExchange client = new(name);
            byte[] clientPublic = client.CreateClientPublicValue();

            Assert.HasCount(1 + (coord * 2), clientPublic, $"{name}：未压缩点长度");
            Assert.AreEqual(0x04, clientPublic[0], $"{name}：必须是未压缩点编码");

            using var server = ECDiffieHellman.Create(curve);
            ECParameters serverParams = server.ExportParameters(false);

            byte[] serverPublic = new byte[1 + (coord * 2)];
            serverPublic[0] = 0x04;
            CopyRightAligned(serverParams.Q.X!, serverPublic.AsSpan(1, coord));
            CopyRightAligned(serverParams.Q.Y!, serverPublic.AsSpan(1 + coord, coord));

            using var clientPeer = ECDiffieHellman.Create(new ECParameters
            {
                Curve = curve,
                Q = new ECPoint
                {
                    X = clientPublic[1..(1 + coord)],
                    Y = clientPublic[(1 + coord)..],
                },
            });

            byte[] serverSecret = server.DeriveRawSecretAgreement(clientPeer.PublicKey);
            byte[] clientSecret = client.ComputeSharedSecret(serverPublic);

            Assert.AreSequenceEqual(serverSecret, clientSecret, $"{name}：共享密钥不一致");
        }
    }

    [TestMethod]
    public void Ecdh拒绝不在曲线上的点()
    {
        // 不校验就等于接受任意点，那是一条可以泄漏私钥的路（无效曲线攻击）。
        using EcdhKeyExchange client = new(SshAlgorithmNames.EcdhSha2Nistp256);
        _ = client.CreateClientPublicValue();

        byte[] bogus = new byte[65];
        bogus[0] = 0x04;
        bogus[1] = 0x01;   // 几乎肯定不在曲线上

        Assert.ThrowsExactly<SshKeyExchangeException>(() => client.ComputeSharedSecret(bogus));
    }

    [TestMethod]
    public void Ecdh拒绝压缩点编码()
    {
        using EcdhKeyExchange client = new(SshAlgorithmNames.EcdhSha2Nistp256);
        _ = client.CreateClientPublicValue();

        byte[] compressed = new byte[65];
        compressed[0] = 0x02;   // 压缩点标记
        Assert.ThrowsExactly<SshKeyExchangeException>(() => client.ComputeSharedSecret(compressed));
    }

    [TestMethod]
    public void 有限域DH两端算出同一个共享密钥()
    {
        foreach (string name in new[]
                 {
                     SshAlgorithmNames.DiffieHellmanGroup14Sha256,
                     SshAlgorithmNames.DiffieHellmanGroup16Sha512,
                     SshAlgorithmNames.DiffieHellmanGroup18Sha512,
                 })
        {
            using DiffieHellmanGroupKeyExchange client = new(name);
            byte[] clientPublic = client.CreateClientPublicValue();

            // 服务端用同一个群（RFC 3526 §3 / §5 / §7）做一次 DH。
            DHParameters group = name switch
            {
                SshAlgorithmNames.DiffieHellmanGroup14Sha256 => DHStandardGroups.rfc3526_2048,
                SshAlgorithmNames.DiffieHellmanGroup16Sha512 => DHStandardGroups.rfc3526_4096,
                _ => DHStandardGroups.rfc3526_8192,
            };
            DHParameters parameters = new(group.P, group.G, null, 512);

            DHKeyPairGenerator generator = new();
            generator.Init(new DHKeyGenerationParameters(new SecureRandom(), parameters));
            AsymmetricCipherKeyPair serverPair = generator.GenerateKeyPair();

            byte[] serverPublic = ((DHPublicKeyParameters)serverPair.Public).Y.ToByteArrayUnsigned();

            DHBasicAgreement serverAgreement = new();
            serverAgreement.Init(serverPair.Private);
            byte[] serverSecret = serverAgreement
                .CalculateAgreement(new DHPublicKeyParameters(
                    new Org.BouncyCastle.Math.BigInteger(1, clientPublic), parameters))
                .ToByteArrayUnsigned();

            byte[] clientSecret = client.ComputeSharedSecret(serverPublic);
            Assert.AreSequenceEqual(serverSecret, clientSecret, $"{name}：共享密钥不一致");
        }
    }

    [TestMethod]
    public void 有限域DH拒绝越界的公钥()
    {
        // 0、1、p-1 会让共享密钥落进一个极小的集合（小子群攻击）。
        using DiffieHellmanGroupKeyExchange client = new(SshAlgorithmNames.DiffieHellmanGroup14Sha256);
        _ = client.CreateClientPublicValue();

        Assert.ThrowsExactly<SshKeyExchangeException>(() => client.ComputeSharedSecret([0]));
        Assert.ThrowsExactly<SshKeyExchangeException>(() => client.ComputeSharedSecret([1]));

        byte[] pMinusOne = DHStandardGroups.rfc3526_2048.P
            .Subtract(Org.BouncyCastle.Math.BigInteger.One).ToByteArrayUnsigned();
        Assert.ThrowsExactly<SshKeyExchangeException>(() => client.ComputeSharedSecret(pMinusOne));
    }

    // ------------------------------------------------------------ 后量子混合

    [TestMethod]
    public void MlKem混合两端算出同一个共享密钥()
    {
        using HybridKeyExchange client = new(SshAlgorithmNames.MlKem768X25519Sha256);
        byte[] clientPublic = client.CreateClientPublicValue();

        // ML-KEM-768 公钥 1184 字节 + X25519 公钥 32 字节
        Assert.HasCount(1184 + 32, clientPublic);

        // 服务端：对 KEM 公钥做封装，再做一次 X25519。
        MLKemEncapsulator encapsulator = new(MLKemParameters.ml_kem_768);
        encapsulator.Init(MLKemPublicKeyParameters.FromEncoding(MLKemParameters.ml_kem_768, clientPublic[..1184]));
        byte[] ciphertext = new byte[encapsulator.EncapsulationLength];
        byte[] kemSecret = new byte[encapsulator.SecretLength];
        encapsulator.Encapsulate(ciphertext, 0, ciphertext.Length, kemSecret, 0, kemSecret.Length);
        Assert.HasCount(1088, ciphertext, "ML-KEM-768 密文长度");

        X25519PrivateKeyParameters serverX25519 = new(new SecureRandom());
        byte[] serverX25519Public = serverX25519.GeneratePublicKey().GetEncoded();
        byte[] classicalSecret = new byte[32];
        X25519Agreement agreement = new();
        agreement.Init(serverX25519);
        agreement.CalculateAgreement(new X25519PublicKeyParameters(clientPublic[1184..]), classicalSecret, 0);

        byte[] serverReply = [.. ciphertext, .. serverX25519Public];
        byte[] clientSecret = client.ComputeSharedSecret(serverReply);

        // K = SHA-256(K_kem ‖ K_x25519)
        byte[] expected = SHA256.HashData([.. kemSecret, .. classicalSecret]);
        Assert.AreSequenceEqual(expected, clientSecret);
        Assert.HasCount(32, clientSecret, "SHA-256 输出");
    }

    [TestMethod]
    public void SNtruPrime混合两端算出同一个共享密钥()
    {
        using HybridKeyExchange client = new(SshAlgorithmNames.Sntrup761X25519Sha512);
        byte[] clientPublic = client.CreateClientPublicValue();
        Assert.HasCount(1158 + 32, clientPublic, "sntrup761 公钥 1158 + X25519 32");

        SNtruPrimeKemGenerator generator = new(new SecureRandom());
        ISecretWithEncapsulation encapsulated = generator.GenerateEncapsulated(
            new SNtruPrimePublicKeyParameters(SNtruPrimeParameters.sntrup761, clientPublic[..1158]));
        byte[] ciphertext = encapsulated.GetEncapsulation();
        byte[] kemSecret = encapsulated.GetSecret();
        Assert.HasCount(1039, ciphertext, "sntrup761 密文长度");

        X25519PrivateKeyParameters serverX25519 = new(new SecureRandom());
        byte[] serverX25519Public = serverX25519.GeneratePublicKey().GetEncoded();
        byte[] classicalSecret = new byte[32];
        X25519Agreement agreement = new();
        agreement.Init(serverX25519);
        agreement.CalculateAgreement(new X25519PublicKeyParameters(clientPublic[1158..]), classicalSecret, 0);

        byte[] clientSecret = client.ComputeSharedSecret([.. ciphertext, .. serverX25519Public]);

        byte[] expected = SHA512.HashData([.. kemSecret, .. classicalSecret]);
        Assert.AreSequenceEqual(expected, clientSecret);
        Assert.HasCount(64, clientSecret, "SHA-512 输出");
    }

    [TestMethod]
    public void 混合方法的共享密钥按定长串编码()
    {
        // 这是与 curve25519 / ECDH 的**根本差别**（velashell-docs/zh/ssh/spec/03 §3.6）。
        // 写成 mpint 会在最高位为 1 时多补一个 0x00，表现为概率性签名失败。
        using HybridKeyExchange mlkem = new(SshAlgorithmNames.MlKem768X25519Sha256);
        Assert.AreEqual(SshKexValueEncoding.ByteString, mlkem.SharedSecretEncoding);

        using Curve25519KeyExchange c25519 = new();
        Assert.AreEqual(SshKexValueEncoding.Mpint, c25519.SharedSecretEncoding);

        using DiffieHellmanGroupKeyExchange dh = new(SshAlgorithmNames.DiffieHellmanGroup14Sha256);
        Assert.AreEqual(SshKexValueEncoding.Mpint, dh.SharedSecretEncoding);
        Assert.AreEqual(SshKexValueEncoding.Mpint, dh.PublicValueEncoding, "DH 的公钥也是 mpint");
    }

    // ------------------------------------------------------------ 工厂

    [TestMethod]
    public void 默认清单里的每个KEX算法都造得出实例()
    {
        // 算法清单与工厂注册表不一致是一个编程错误 —— 它会在协商成功之后才炸，
        // 那时用户已经等了一个往返。这条用例把它挪到构建期。
        foreach (string name in SshAlgorithmSet.Default.KeyExchange)
        {
            Assert.IsTrue(SshKeyExchangeFactory.IsSupported(name), $"{name} 未注册");
            using ISshKeyExchange kex = SshKeyExchangeFactory.Create(name);
            Assert.AreEqual(name, kex.Name);
        }
    }

    [TestMethod]
    public void 放开老算法后的清单也都造得出实例()
    {
        foreach (string name in SshAlgorithmSet.Default.WithLegacyInterop().KeyExchange)
        {
            using ISshKeyExchange kex = SshKeyExchangeFactory.Create(name);
            Assert.AreEqual(name, kex.Name);
        }
    }

    [TestMethod]
    public void 未注册的算法名抛出()
    {
        Assert.ThrowsExactly<InvalidOperationException>(
            () => SshKeyExchangeFactory.Create("kex-that-does-not-exist"));
    }

    [TestMethod]
    [DataRow("curve25519")]
    [DataRow("ecdh")]
    [DataRow("hybrid")]
    [DataRow("dh")]
    public void 每种交换都拒绝不属于自己的算法名(string family)
    {
        // 挂着别的名字的实例会让协商结果与实际做的交换对不上。
        Action create = family switch
        {
            "curve25519" => () => new Curve25519KeyExchange(SshAlgorithmNames.EcdhSha2Nistp256).Dispose(),
            "ecdh" => () => new EcdhKeyExchange(SshAlgorithmNames.Curve25519Sha256).Dispose(),
            "hybrid" => () => new HybridKeyExchange(SshAlgorithmNames.Curve25519Sha256).Dispose(),
            _ => () => new DiffieHellmanGroupKeyExchange(SshAlgorithmNames.Curve25519Sha256).Dispose(),
        };

        Assert.ThrowsExactly<ArgumentException>(create);
    }

    private static void CopyRightAligned(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        destination.Clear();
        source.CopyTo(destination[^source.Length..]);
    }
}
