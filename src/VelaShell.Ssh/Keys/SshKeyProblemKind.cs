// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 行为规格: velashell-docs/zh/ssh/spec/04-authentication.md §4.6;velashell-docs/zh/ssh/spec/08-failures.md §一、§二

namespace VelaShell.Ssh.Keys;

/// <summary>
/// 私钥或证书用不了的具体情形（<see cref="SshPrivateKeyException.ProblemKind"/>、<see cref="SshCertificateException.ProblemKind"/>）。
/// </summary>
/// <remarks>
/// <para>
/// 比 <see cref="Diagnostics.SshFailureReason"/> 细一层：同是 <c>KeyFormatInvalid</c>，「文件坏了」与「KDF 参数不合理」的下一步不一样；
/// 同是 <c>Unsupported</c>，界面要说清不支持的是密钥类型、曲线还是加密算法。异常消息里的中文是写给开发者的，
/// 界面按这个种类与异常上的参数（<c>Origin</c>、<c>AlgorithmName</c>……）自己说，不去解析那句话。
/// </para>
/// <para>零值 <see cref="Unknown"/> 是「没归类」：库自己抛的私钥 / 证书异常都会归类。</para>
/// </remarks>
public enum SshKeyProblemKind
{
    /// <summary>没归类。</summary>
    Unknown = 0,

    /// <summary>文件读不出来（IO 错误，原因在内层异常）。</summary>
    FileUnreadable,

    /// <summary>没有权限读这个文件（Unix 上私钥应当是 0600）。</summary>
    FileAccessDenied,

    /// <summary>认不出私钥的格式（支持 OpenSSH、PKCS#8、PKCS#1、SEC1 与 PuTTY 的 .ppk）。</summary>
    FormatUnrecognized,

    /// <summary>内容不完整、格式不对或者已经损坏（截断、字段畸形、校验对不上、公私钥不是一对……）。</summary>
    Malformed,

    /// <summary>私钥是加密的，需要口令。</summary>
    PassphraseRequired,

    /// <summary>私钥解不开，口令多半不对。</summary>
    PassphraseIncorrect,

    /// <summary>不支持的密钥类型（<c>AlgorithmName</c>，如 DSA、Ed448、某个 PKCS#8 算法）。</summary>
    UnsupportedKeyType,

    /// <summary>不支持的椭圆曲线（<c>AlgorithmName</c>）；SSH 只用 NIST P-256 / P-384 / P-521。</summary>
    UnsupportedCurve,

    /// <summary>不支持的私钥加密算法（<c>AlgorithmName</c>）。</summary>
    UnsupportedCipher,

    /// <summary>不支持的口令派生方式（<c>AlgorithmName</c>）。</summary>
    UnsupportedKdf,

    /// <summary>不支持的文件格式版本（PuTTY .ppk 只认 v2 与 v3）。</summary>
    UnsupportedFormatVersion,

    /// <summary>ECDSA 的公开点不是未压缩形式（<c>AlgorithmName</c> 是密钥类型）。</summary>
    UnsupportedPointFormat,

    /// <summary>一个文件里有多把密钥，本库只处理一把。</summary>
    MultipleKeys,

    /// <summary>过时的传统加密 PEM（<c>Proc-Type: 4,ENCRYPTED</c>）：用 <c>ssh-keygen -p</c> 改一次口令就会转成 OpenSSH 格式。</summary>
    LegacyPemEncryption,

    /// <summary>口令派生的参数（轮数、迭代数、Argon2 的内存与遍数）超出上限：文件可能被改过，确实设过的话用更小的参数重新加密。</summary>
    KdfCostTooHigh,

    /// <summary>打不开系统密钥库里的钥，或者打开了用不了（<c>Origin</c> 是钥名）。</summary>
    KeyStoreUnavailable,

    /// <summary>证书文本里的类型（<c>AlgorithmName</c>）不是证书类型。</summary>
    NotACertificate,

    /// <summary>拿主机证书去登录（证书的 <c>KeyId</c>）—— 用户证书是 <c>ssh-keygen</c> 签发时不带 <c>-h</c> 的那一种。</summary>
    HostCertificateForLogin,

    /// <summary>证书里的公钥与私钥不是一对（证书的 <c>KeyId</c>，两边的公钥见 <c>CertificateKey</c> / <c>SignerKey</c>）。</summary>
    CertificateKeyMismatch,

    /// <summary>证书证的钥不在读出来的私钥里（ssh_config 的 <c>CertificateFile</c> 配错了 <c>IdentityFile</c>）。</summary>
    CertificateKeyNotLoaded,
}
