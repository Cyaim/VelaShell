// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   OpenSSH PROTOCOL.certkeys
//   行为规格: velashell-docs/zh/ssh/spec/04-authentication.md §4;velashell-docs/zh/ssh/spec/08-failures.md

using VelaShell.Ssh.Diagnostics;

namespace VelaShell.Ssh.Keys;

/// <summary>证书读不出来，或者用不了。</summary>
/// <remarks>
/// 具体是哪一种看 <see cref="SshException.Reason"/>：<see cref="SshFailureReason.KeyFileUnreadable"/>、
/// <see cref="SshFailureReason.KeyFormatInvalid"/>、<see cref="SshFailureReason.KeyMismatch"/>（与私钥不是一对、
/// 拿主机证书去登录）、<see cref="SshFailureReason.Unsupported"/>。
/// </remarks>
/// <remarks>创建一个证书异常。</remarks>
/// <param name="reason">失败的分类。</param>
/// <param name="message">诊断消息。</param>
/// <param name="innerException">内部异常。</param>
public sealed class SshCertificateException(SshFailureReason reason, string message, Exception? innerException = null) : SshException(reason, SshPhase.None, message, innerException)
{
    /// <summary>具体是哪种情形 —— 界面按它说话，不解析 <see cref="Exception.Message"/>。</summary>
    /// <remarks>库自己抛的都填；使用者自己构造、没填时是 <see cref="SshKeyProblemKind.Unknown"/>。</remarks>
    public SshKeyProblemKind ProblemKind { get; init; }

    /// <summary>出事的证书从哪来：<c>LoadAsync</c> 的文件路径、<c>Parse</c> 的 <c>origin</c>；不知道时为 <see langword="null"/>。</summary>
    /// <remarks>由读证书的入口在异常离开库之前补上（解 blob 的那一层不知道来源），所以是 <c>internal set</c>。</remarks>
    public string? Origin { get; internal set; }

    /// <summary>
    /// 说到的那个名字（<see cref="SshKeyProblemKind.NotACertificate"/> 的类型串、<see cref="SshKeyProblemKind.UnsupportedKeyType"/> 的密钥类型）。
    /// 来自文件内容，<b>未清洗</b>。
    /// </summary>
    public string? AlgorithmName { get; init; }

    /// <summary>证书的 Key ID（<see cref="SshKeyProblemKind.HostCertificateForLogin"/>、<see cref="SshKeyProblemKind.CertificateKeyMismatch"/>）。<b>未清洗</b>。</summary>
    public string? KeyId { get; init; }

    /// <summary>证书里那把公钥（<see cref="SshKeyProblemKind.CertificateKeyMismatch"/>）—— 界面据此显示两边的指纹。</summary>
    public HostKeys.SshPublicKey? CertificateKey { get; init; }

    /// <summary>私钥（签名器）这边的公钥（<see cref="SshKeyProblemKind.CertificateKeyMismatch"/>）。</summary>
    public HostKeys.SshPublicKey? SignerKey { get; init; }
}
