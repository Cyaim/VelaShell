// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   draft-miller-ssh-agent
//   行为规格: velashell-docs/zh/ssh/spec/04-authentication.md §4;velashell-docs/zh/ssh/spec/08-failures.md

using VelaShell.Ssh.Diagnostics;

namespace VelaShell.Ssh.Keys;

/// <summary>连不上 agent、agent 拒绝了请求，或者 agent 的应答不合协议。</summary>
/// <remarks>
/// 具体是哪一种看 <see cref="SshException.Reason"/>：<see cref="SshFailureReason.AgentNotRunning"/>、
/// <see cref="SshFailureReason.AgentUnavailable"/>、<see cref="SshFailureReason.AgentRefused"/>、<see cref="SshFailureReason.ProtocolError"/>、
/// <see cref="SshFailureReason.LimitExceeded"/>；更细的情形与参数看 <see cref="FailureKind"/>。
/// </remarks>
/// <remarks>创建一个 agent 异常。</remarks>
/// <param name="reason">失败的分类。</param>
/// <param name="message">诊断消息。</param>
/// <param name="innerException">内部异常。</param>
public sealed class SshAgentException(SshFailureReason reason, string message, Exception? innerException = null) : SshException(reason, SshPhase.Authenticating, message, innerException)
{
    /// <summary>具体是哪种情形 —— 界面按它说，不解析 <see cref="Exception.Message"/> 里的中文。</summary>
    /// <remarks>库自己抛的 agent 异常都填；使用者自己构造、没填时是 <see cref="SshAgentFailureKind.Unknown"/>。</remarks>
    public SshAgentFailureKind FailureKind { get; init; }

    /// <summary>agent 的端点（Unix 套接字路径或 Windows 命名管道名）；不知道时为 <see langword="null"/>。</summary>
    public string? Endpoint { get; init; }

    /// <summary>不可信的命名管道属主的 SID（<see cref="SshAgentFailureKind.PipeOwnerUntrusted"/>；读不到属主时为 <see langword="null"/>）。</summary>
    public string? PipeOwner { get; init; }

    /// <summary>请求的签名算法（<see cref="SshAgentFailureKind.SignatureAlgorithmMismatch"/>）。</summary>
    public string? RequestedAlgorithm { get; init; }

    /// <summary>agent 实际用的签名算法（<see cref="SshAgentFailureKind.SignatureAlgorithmMismatch"/>）。来自 agent 的应答：<b>未清洗</b>。</summary>
    public string? SignedAlgorithm { get; init; }

    /// <summary>被拒的加钥请求带着目的地约束（<see cref="SshAgentFailureKind.AddRefused"/>）—— agent 可能不支持它。</summary>
    public bool HasDestinationConstraints { get; init; }
}
