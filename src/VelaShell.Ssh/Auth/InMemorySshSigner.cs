// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   RFC 4252 §7   publickey 的两段式与签名输入
//   RFC 8332 §3   rsa-sha2-256 / rsa-sha2-512 的选择
//   draft-miller-ssh-agent  加钥报文的私钥格式（WriteAgentPrivateKey）
//   行为规格:     velashell-docs/zh/ssh/spec/04-authentication.md §4;velashell-docs/zh/ssh/design/architecture.md §8 第 5 项;velashell-docs/zh/ssh/spec/07-forwarding.md §7.3

using System.Buffers;
using System.Security.Cryptography;
using VelaShell.Ssh.HostKeys;
using VelaShell.Ssh.Protocol;
using BcEd25519 = Org.BouncyCastle.Math.EC.Rfc8032.Ed25519;

namespace VelaShell.Ssh.Auth;

/// <summary>在进程内内存里持有私钥的签名器。</summary>
/// <remarks>
/// ⚠️ 私钥就在托管堆上，可能被交换到磁盘、被内存转储带走。
/// 对高价值密钥应当用 <see cref="ISshSigner"/> 的其它实现
/// （ssh-agent / PKCS#11 / HSM），让私钥从不进入本进程。
/// </remarks>
public sealed class InMemorySshSigner : ISshSigner, IDisposable
{
    /// <summary>Ed25519 的 32 字节种子（私钥）。释放时清零。</summary>
    /// <remarks>
    /// 自己持有种子、签名时交给 BouncyCastle 的静态 Ed25519 实现，而不是持有它的参数对象 ——
    /// 曾经持有 <c>Ed25519PrivateKeyParameters</c>，<see cref="Dispose"/> 只释放 RSA / ECDsa，
    /// 种子在托管堆上一直留到 GC，「释放即清零」对最常见的 Ed25519 恰恰不成立。
    /// </remarks>
    private readonly byte[]? _ed25519Seed;
    private readonly byte[]? _ed25519Public;
    private readonly ECDsa? _ecdsa;
    private readonly RSA? _rsa;

    /// <summary>释放时是否一并释放 <see cref="_rsa"/> / <see cref="_ecdsa"/>（<see cref="FromRsa"/> / <see cref="FromEcdsa"/> 的 <c>ownsKey</c>）。</summary>
    private readonly bool _ownsKey;
    private readonly int _coordinateBytes;
    private bool _disposed;

    private InMemorySshSigner(
        SshPublicKey publicKey,
        IReadOnlyList<string> algorithms,
        byte[]? ed25519Seed,
        byte[]? ed25519Public,
        ECDsa? ecdsa,
        RSA? rsa,
        int coordinateBytes,
        bool ownsKey = true)
    {
        PublicKey = publicKey;
        SignatureAlgorithms = algorithms;
        _ed25519Seed = ed25519Seed;
        _ed25519Public = ed25519Public;
        _ecdsa = ecdsa;
        _rsa = rsa;
        _coordinateBytes = coordinateBytes;
        _ownsKey = ownsKey;
    }

    /// <inheritdoc />
    public SshPublicKey PublicKey { get; }

    /// <inheritdoc />
    public IReadOnlyList<string> SignatureAlgorithms { get; }

    /// <inheritdoc />
    public bool IsLocalAndCheap => true;

    /// <summary>用一把 Ed25519 私钥构造。</summary>
    public static InMemorySshSigner FromEd25519(ReadOnlySpan<byte> privateKeySeed)
    {
        if (privateKeySeed.Length != 32)
        {
            throw new ArgumentException("Ed25519 私钥种子必须是 32 字节。", nameof(privateKeySeed));
        }

        // 复制一份归自己所有：调用方的缓冲由调用方清，这一份由 Dispose 清。
        byte[] seed = privateKeySeed.ToArray();
        byte[] publicKey = new byte[BcEd25519.PublicKeySize];
        BcEd25519.GeneratePublicKey(seed, publicKey);

        byte[] blob = BuildBlob(w =>
        {
            w.WriteUtf8String(SshAlgorithmNames.SshEd25519);
            w.WriteString(publicKey);
        });

        return new InMemorySshSigner(
            SshPublicKey.Decode(blob), [SshAlgorithmNames.SshEd25519], seed, publicKey, null, null, 0);
    }

    /// <summary>生成一把新的 RSA 密钥并构造签名器。</summary>
    /// <param name="bits">模数位数，2048–16384、8 的倍数；默认 3072（与 <c>ssh-keygen</c> 一致）。</param>
    /// <exception cref="ArgumentOutOfRangeException">位数不在范围内。</exception>
    /// <remarks>要写成文件，交给 <see cref="Keys.SshPrivateKeyFile.Format"/>。</remarks>
    public static InMemorySshSigner GenerateRsa(int bits = 3072)
    {
        if (bits is < 2048 or > 16384 || bits % 8 != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(bits), bits, "RSA 密钥要 2048–16384 位，且是 8 的倍数。");
        }

        var rsa = RSA.Create(bits);
        try
        {
            return FromRsa(rsa);
        }
        catch
        {
            rsa.Dispose();
            throw;
        }
    }

    /// <summary>生成一把新的 ECDSA 密钥并构造签名器。</summary>
    /// <param name="bits">曲线：256 / 384 / 521（NIST P-256 / P-384 / P-521，SSH 只定义了这三条）。</param>
    /// <exception cref="ArgumentOutOfRangeException">别的位数。</exception>
    public static InMemorySshSigner GenerateEcdsa(int bits = 256)
    {
        ECCurve curve = bits switch
        {
            256 => ECCurve.NamedCurves.nistP256,
            384 => ECCurve.NamedCurves.nistP384,
            521 => ECCurve.NamedCurves.nistP521,
            _ => throw new ArgumentOutOfRangeException(nameof(bits), bits, "ECDSA 只支持 256 / 384 / 521 位（NIST P-256 / P-384 / P-521）。"),
        };

        var ecdsa = ECDsa.Create(curve);
        try
        {
            return FromEcdsa(ecdsa);
        }
        catch
        {
            ecdsa.Dispose();
            throw;
        }
    }

    /// <summary>
    /// 按 <c>openssh-key-v1</c> 私钥区的格式写出这把钥：从类型名起，到注释之前（私钥文件的写出用）。
    /// </summary>
    /// <remarks>
    /// 〔velashell-docs/zh/ssh/spec/04 §4.6〕字段顺序与读取一侧（<see cref="Keys.SshPrivateKeyFile"/>）是同一份规格：
    /// Ed25519 是公钥 ‖ (种子 ‖ 公钥)；RSA 是 n、e、d、iqmp、p、q；ECDSA 是曲线名、公钥点、d。
    /// 导出的私钥参数用完清零（只清得了数组，<see cref="RSAParameters"/> 的托管副本由这里负责）。
    /// </remarks>
    internal void WriteOpenSshPrivateFields(ref SshDataWriter writer)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_ed25519Seed is not null)
        {
            writer.WriteUtf8String(SshAlgorithmNames.SshEd25519);
            writer.WriteString(_ed25519Public);
            byte[] secret = [.. _ed25519Seed, .. _ed25519Public!];
            try
            {
                writer.WriteString(secret);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(secret);
            }
            return;
        }

        if (_rsa is not null)
        {
            RSAParameters p = _rsa.ExportParameters(includePrivateParameters: true);
            try
            {
                writer.WriteUtf8String(SshAlgorithmNames.SshRsa);
                writer.WriteMpint(p.Modulus);
                writer.WriteMpint(p.Exponent);
                writer.WriteMpint(p.D);
                writer.WriteMpint(p.InverseQ);
                writer.WriteMpint(p.P);
                writer.WriteMpint(p.Q);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(p.D);
                CryptographicOperations.ZeroMemory(p.P);
                CryptographicOperations.ZeroMemory(p.Q);
                CryptographicOperations.ZeroMemory(p.DP);
                CryptographicOperations.ZeroMemory(p.DQ);
                CryptographicOperations.ZeroMemory(p.InverseQ);
            }
            return;
        }

        ECParameters ec = _ecdsa!.ExportParameters(includePrivateParameters: true);
        try
        {
            (string algorithm, string curveName, int coordinate) = NistCurveOf(ec.Curve)
                ?? throw new InvalidOperationException("签名器里的 ECDSA 曲线不是 SSH 认得的那三条 —— 构造时就该被拒。");
            byte[] point = new byte[1 + (coordinate * 2)];
            point[0] = 0x04;
            ec.Q.X!.CopyTo(point.AsSpan(1 + coordinate - ec.Q.X!.Length));
            ec.Q.Y!.CopyTo(point.AsSpan(1 + (coordinate * 2) - ec.Q.Y!.Length));

            writer.WriteUtf8String(algorithm);
            writer.WriteUtf8String(curveName);
            writer.WriteString(point);
            writer.WriteMpint(ec.D);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(ec.D);
        }
    }

    /// <summary>生成一把新的 Ed25519 密钥并构造签名器。</summary>
    /// <remarks>要写成文件，交给 <see cref="Keys.SshPrivateKeyFile.Format"/>。</remarks>
    public static InMemorySshSigner GenerateEd25519()
    {
        // Ed25519 的私钥就是 32 个随机字节（RFC 8032 §5.1.5）。
        byte[] seed = RandomNumberGenerator.GetBytes(BcEd25519.SecretKeySize);
        try
        {
            return FromEd25519(seed);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(seed);
        }
    }

    /// <summary>用一把 RSA 私钥构造。</summary>
    /// <param name="rsa">私钥。</param>
    /// <param name="ownsKey">释放签名器时是否一并释放 <paramref name="rsa"/>。默认 <see langword="true"/>：交进来就归它；还要接着用的传 <see langword="false"/>。</param>
    public static InMemorySshSigner FromRsa(RSA rsa, bool ownsKey = true)
    {
        ArgumentNullException.ThrowIfNull(rsa);
        RSAParameters p = rsa.ExportParameters(false);

        byte[] blob = BuildBlob(w =>
        {
            // blob 里的类型串**永远是 ssh-rsa**，与签名算法无关（RFC 8332 的不对称）。
            w.WriteUtf8String(SshAlgorithmNames.SshRsa);
            w.WriteMpint(p.Exponent);
            w.WriteMpint(p.Modulus);
        });

        // 三个都列出来,按偏好排序。SHA-1 的 ssh-rsa 排在最后,并且
        // **默认会被认证器过滤掉** —— 只有显式打开 AllowSha1RsaSignatures 才会用到它
        // (velashell-docs/zh/ssh/spec/04 §4.4)。在这里就删掉它的话,那个开关就永远没有效果了。
        return new InMemorySshSigner(
            SshPublicKey.Decode(blob),
            [SshAlgorithmNames.RsaSha512, SshAlgorithmNames.RsaSha256, SshAlgorithmNames.SshRsa],
            null, null, null, rsa, 0, ownsKey);
    }

    /// <summary>用一把 ECDSA 私钥构造。</summary>
    /// <param name="ecdsa">私钥。</param>
    /// <param name="ownsKey">释放签名器时是否一并释放 <paramref name="ecdsa"/>。默认 <see langword="true"/>：交进来就归它；还要接着用的传 <see langword="false"/>。</param>
    /// <exception cref="ArgumentException">曲线不是 NIST P-256 / P-384 / P-521（SSH 只定义了这三条，RFC 5656 §10.1）。</exception>
    public static InMemorySshSigner FromEcdsa(ECDsa ecdsa, bool ownsKey = true)
    {
        ArgumentNullException.ThrowIfNull(ecdsa);
        ECParameters p = ecdsa.ExportParameters(false);

        if (NistCurveOf(p.Curve) is not var (name, curveName, coordinate))
        {
            throw new ArgumentException(
                $"SSH 不支持这条 ECDSA 曲线（{DescribeCurve(p.Curve)}）：只认 NIST P-256 / P-384 / P-521。", nameof(ecdsa));
        }

        byte[] point = new byte[1 + (coordinate * 2)];
        point[0] = 0x04;
        p.Q.X!.CopyTo(point.AsSpan(1 + coordinate - p.Q.X!.Length));
        p.Q.Y!.CopyTo(point.AsSpan(1 + (coordinate * 2) - p.Q.Y!.Length));

        byte[] blob = BuildBlob(w =>
        {
            w.WriteUtf8String(name);
            w.WriteUtf8String(curveName);
            w.WriteString(point);
        });

        return new InMemorySshSigner(SshPublicKey.Decode(blob), [name], null, null, ecdsa, null, coordinate, ownsKey);
    }

    /// <summary>
    /// 按曲线本身（OID 或名字）认出 SSH 的三条 NIST 曲线；别的曲线为 <see langword="null"/>。
    /// </summary>
    /// <remarks>
    /// 曾经按位数认：256 位就当 nistp256 —— secp256k1、brainpoolP256r1 也是 256 位，被标成 nistp256 交给服务端，
    /// 签名验不过，症状是一句看不出原因的「服务端不接受这把公钥」。
    /// </remarks>
    internal static (string Algorithm, string CurveName, int Coordinate)? NistCurveOf(ECCurve curve)
    {
        if (!curve.IsNamed || curve.Oid is not { } oid)
        {
            return null;
        }

        return (oid.Value, oid.FriendlyName) switch
        {
            ("1.2.840.10045.3.1.7", _) or (_, "nistP256" or "ECDSA_P256" or "secp256r1" or "prime256v1") =>
                (SshAlgorithmNames.EcdsaSha2Nistp256, "nistp256", 32),
            ("1.3.132.0.34", _) or (_, "nistP384" or "ECDSA_P384" or "secp384r1") =>
                (SshAlgorithmNames.EcdsaSha2Nistp384, "nistp384", 48),
            ("1.3.132.0.35", _) or (_, "nistP521" or "ECDSA_P521" or "secp521r1") =>
                (SshAlgorithmNames.EcdsaSha2Nistp521, "nistp521", 66),
            _ => null,
        };
    }

    /// <summary>给错误消息用的曲线名。</summary>
    internal static string DescribeCurve(ECCurve curve) =>
        curve.Oid is { } oid ? oid.FriendlyName ?? oid.Value ?? "未命名曲线" : "显式参数的曲线";

    /// <inheritdoc />
    public ValueTask<byte[]> SignAsync(
        ReadOnlyMemory<byte> data, string algorithm, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!SignatureAlgorithms.Contains(algorithm, StringComparer.Ordinal))
        {
            throw new ArgumentException(
                $"这把 {PublicKey.KeyType} 密钥不支持签名算法 {algorithm}。", nameof(algorithm));
        }

        byte[] signature = algorithm switch
        {
            SshAlgorithmNames.SshEd25519 => SignEd25519(data.Span),
            SshAlgorithmNames.EcdsaSha2Nistp256 => SignEcdsa(data.Span, HashAlgorithmName.SHA256, algorithm),
            SshAlgorithmNames.EcdsaSha2Nistp384 => SignEcdsa(data.Span, HashAlgorithmName.SHA384, algorithm),
            SshAlgorithmNames.EcdsaSha2Nistp521 => SignEcdsa(data.Span, HashAlgorithmName.SHA512, algorithm),
            SshAlgorithmNames.RsaSha512 => SignRsa(data.Span, HashAlgorithmName.SHA512, algorithm),
            SshAlgorithmNames.RsaSha256 => SignRsa(data.Span, HashAlgorithmName.SHA256, algorithm),
            // SHA-1。只有使用者显式打开 SshAuthenticator.AllowSha1RsaSignatures 时
            // 才会走到这里 —— 为的是还能连上那些停在 OpenSSH 7.x 的老机器。
#pragma warning disable CA5350 // ssh-rsa 的签名摘要由 RFC 4253 规定就是 SHA-1,换不得
            SshAlgorithmNames.SshRsa => SignRsa(data.Span, HashAlgorithmName.SHA1, algorithm),
#pragma warning restore CA5350
            _ => throw new ArgumentException($"尚未实现的签名算法：{algorithm}", nameof(algorithm)),
        };

        return ValueTask.FromResult(signature);
    }

    /// <summary>
    /// 按 agent 加钥报文的格式写出「密钥类型 + 私钥内容」（velashell-docs/zh/ssh/spec/07 §7.3）。
    /// </summary>
    /// <remarks>
    /// 写进 <paramref name="output"/> 的是明文私钥 —— 用完由调用方清零。
    /// </remarks>
    internal void WriteAgentPrivateKey(IBufferWriter<byte> output, SshPublicKey? certificate = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        SshDataWriter writer = new(output);

        // 带证书时（draft-miller-ssh-agent「私钥格式」的证书那几行）：类型串换成证书的，紧跟整张证书；
        // 之后只放证书里没有的私钥部分 —— 公钥那几项已经在证书里了（ed25519 除外，它的两项照旧）。
        if (certificate is not null)
        {
            writer.WriteUtf8String(certificate.KeyType);
            writer.WriteString(certificate.Blob.Span);
        }

        if (_ed25519Seed is not null)
        {
            byte[] secret = new byte[_ed25519Seed.Length + _ed25519Public!.Length];
            try
            {
                // 种子在前、公钥在后，共 64 字节。
                _ed25519Seed.CopyTo(secret, 0);
                _ed25519Public.CopyTo(secret, _ed25519Seed.Length);

                if (certificate is null)
                {
                    writer.WriteUtf8String(SshAlgorithmNames.SshEd25519);
                }
                writer.WriteString(_ed25519Public);
                writer.WriteString(secret);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(secret);
            }
            return;
        }

        if (_rsa is not null)
        {
            RSAParameters p = _rsa.ExportParameters(true);
            try
            {
                // ⚠️ n 在前、e 在后 —— 与公钥 blob 的顺序相反。带证书时 n、e 在证书里，不再写。
                if (certificate is null)
                {
                    writer.WriteUtf8String(SshAlgorithmNames.SshRsa);
                    writer.WriteMpint(p.Modulus!);
                    writer.WriteMpint(p.Exponent!);
                }
                writer.WriteMpint(p.D!);
                writer.WriteMpint(p.InverseQ!);   // q⁻¹ mod p
                writer.WriteMpint(p.P!);
                writer.WriteMpint(p.Q!);
            }
            finally
            {
                ClearRsa(p);
            }
            return;
        }

        ECParameters e = _ecdsa!.ExportParameters(true);
        try
        {
            // 类型串、曲线名、公钥点三样与公钥 blob 完全相同，直接照搬。
            // 带证书时曲线名与公钥点都在证书里，只剩私钥标量。
            if (certificate is null)
            {
                SshDataReader blob = new(new ReadOnlySequence<byte>(PublicKey.Blob));
                string keyType = blob.ReadUtf8String(64);
                string curveName = blob.ReadUtf8String(64);
                byte[] point = blob.ReadStringAsArray(1 + (_coordinateBytes * 2));

                writer.WriteUtf8String(keyType);
                writer.WriteUtf8String(curveName);
                writer.WriteString(point);
            }
            writer.WriteMpint(e.D!);
        }
        finally
        {
            if (e.D is not null)
            {
                CryptographicOperations.ZeroMemory(e.D);
            }
        }
    }

    private static void ClearRsa(RSAParameters p)
    {
        foreach (byte[]? secret in new[] { p.D, p.P, p.Q, p.DP, p.DQ, p.InverseQ })
        {
            if (secret is not null)
            {
                CryptographicOperations.ZeroMemory(secret);
            }
        }
    }

    private byte[] SignEd25519(ReadOnlySpan<byte> data)
    {
        // 纯 Ed25519（不带 context，RFC 8032 §5.1.6；RFC 8709 §6）。
        // ⚠️ 公钥必须以 ReadOnlySpan 传：直接传 byte[] 时编译器挑中的是 Sign(sk, byte[] ctx, m, sig) ——
        //    那是 Ed25519ctx，把公钥当成了 context，签出来的东西谁也验不过。
        byte[] raw = new byte[BcEd25519.SignatureSize];
        BcEd25519.Sign((ReadOnlySpan<byte>)_ed25519Seed, (ReadOnlySpan<byte>)_ed25519Public, data, raw);

        return BuildBlob(w =>
        {
            w.WriteUtf8String(SshAlgorithmNames.SshEd25519);
            w.WriteString(raw);
        });
    }

    private byte[] SignEcdsa(ReadOnlySpan<byte> data, HashAlgorithmName hash, string algorithm)
    {
        byte[] ieee = _ecdsa!.SignData(data, hash);   // r ‖ s，各 _coordinateBytes 字节

        // SSH 的 ECDSA 签名是**双层嵌套**：外层 string 装「mpint r ‖ mpint s」
        // （velashell-docs/zh/ssh/spec/03 §5.2）。直接用 IEEE P1363 的 r‖s 或 DER 都是错的。
        byte[] inner = BuildBlob(w =>
        {
            w.WriteMpint(ieee.AsSpan(0, _coordinateBytes));
            w.WriteMpint(ieee.AsSpan(_coordinateBytes, _coordinateBytes));
        });

        return BuildBlob(w =>
        {
            w.WriteUtf8String(algorithm);
            w.WriteString(inner);
        });
    }

    private byte[] SignRsa(ReadOnlySpan<byte> data, HashAlgorithmName hash, string algorithm)
    {
        // PKCS#1 v1.5，不是 PSS（RFC 8332 §3）。
        byte[] raw = _rsa!.SignData(data, hash, RSASignaturePadding.Pkcs1);

        return BuildBlob(w =>
        {
            // 签名 blob 里是**签名算法**名，不是密钥类型名。
            w.WriteUtf8String(algorithm);
            w.WriteString(raw);
        });
    }

    private static byte[] BuildBlob(Action<SshDataWriterBox> write)
    {
        ArrayBufferWriter<byte> buffer = new();
        write(new SshDataWriterBox(buffer));
        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>把 ref struct 的写入器包一层，好在 lambda 里用。</summary>
    private sealed class SshDataWriterBox(ArrayBufferWriter<byte> output)
    {
        public void WriteUtf8String(string value)
        {
            SshDataWriter w = new(output);
            w.WriteUtf8String(value);
        }

        public void WriteString(ReadOnlySpan<byte> value)
        {
            SshDataWriter w = new(output);
            w.WriteString(value);
        }

        public void WriteMpint(ReadOnlySpan<byte> magnitude)
        {
            SshDataWriter w = new(output);
            w.WriteMpint(magnitude);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        if (_ed25519Seed is not null)
        {
            CryptographicOperations.ZeroMemory(_ed25519Seed);
        }
        if (_ownsKey)
        {
            _ecdsa?.Dispose();
            _rsa?.Dispose();
        }
    }

    /// <summary>私钥材料是否已经清零（测试用）。</summary>
    internal bool IsKeyMaterialCleared =>
        _disposed && (_ed25519Seed is null || _ed25519Seed.AsSpan().IndexOfAnyExcept((byte)0) < 0);
}
