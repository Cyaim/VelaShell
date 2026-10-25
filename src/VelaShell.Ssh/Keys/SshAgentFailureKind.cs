// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   draft-miller-ssh-agent §3(报文格式)、§4.5(签名标志位)、§5(约束)
//   OpenSSH PROTOCOL.agent 的 restrict-destination-v00@openssh.com
//   行为规格: velashell-docs/zh/ssh/spec/04-authentication.md §4;velashell-docs/zh/ssh/spec/08-failures.md §一

namespace VelaShell.Ssh.Keys;

/// <summary>ssh-agent 失败的具体情形（<see cref="SshAgentException.FailureKind"/>）—— 比原因码更细，界面据此说清是没在跑、连不上、不可信还是拒绝了。</summary>
/// <remarks>
/// 异常消息是写给开发者的中文；界面按这个种类与异常上的参数（端点、管道属主、两个签名算法名）自己说。
/// 零值 <see cref="Unknown"/> 是「没归类」：库自己抛的 agent 异常都会归类。
/// </remarks>
public enum SshAgentFailureKind
{
    /// <summary>没归类。</summary>
    Unknown = 0,

    /// <summary>
    /// 本机没有在跑 ssh-agent（<see cref="Diagnostics.SshFailureReason.AgentNotRunning"/>）：<c>SSH_AUTH_SOCK</c> 没设、套接字不在或没人监听、
    /// Windows 的命名管道在时限内没有出现。端点（如果有）在 <see cref="SshAgentException.Endpoint"/>。
    /// </summary>
    NotRunning,

    /// <summary>连不上 <see cref="SshAgentException.Endpoint"/> 上的 agent（别的 IO / 套接字错误，在内层）。</summary>
    ConnectFailed,

    /// <summary>读不到命名管道 <see cref="SshAgentException.Endpoint"/> 的属主，不能确认另一头是可信的 agent。</summary>
    PipeOwnerUnreadable,

    /// <summary>
    /// 命名管道 <see cref="SshAgentException.Endpoint"/> 的属主（<see cref="SshAgentException.PipeOwner"/>）既不是当前用户也不是系统 ——
    /// 可能是别的用户抢先占了这个管道名，已拒绝连接。
    /// </summary>
    PipeOwnerUntrusted,

    /// <summary>上一次问答中途被打断（取消或出错），这条连接上的问答已经错位，不能再用。</summary>
    ConversationOutOfStep,

    /// <summary>agent 在应答之前就断开了。</summary>
    ClosedBeforeReply,

    /// <summary>与 agent 通信失败（IO 错误，在内层）。</summary>
    CommunicationFailed,

    /// <summary>agent 的应答格式不对、长度不合理，或者回的不是这一问该回的报文（<see cref="Diagnostics.SshFailureReason.ProtocolError"/>）。</summary>
    MalformedReply,

    /// <summary>
    /// agent 用 <see cref="SshAgentException.SignedAlgorithm"/> 签了名，而请求的是 <see cref="SshAgentException.RequestedAlgorithm"/>
    /// （<see cref="Diagnostics.SshFailureReason.Unsupported"/>）：多半是太老、不认 SHA-2 签名标志位的 agent。
    /// </summary>
    SignatureAlgorithmMismatch,

    /// <summary>agent 拒绝签名（钥不在了、<c>ssh-add -c</c> 的确认没批准、目的地约束不许）。</summary>
    SignRefused,

    /// <summary>agent 拒绝加入这把钥（不支持约束、已锁定、不支持这种钥）；带着目的地约束时 <see cref="SshAgentException.HasDestinationConstraints"/> 为真。</summary>
    AddRefused,

    /// <summary>agent 拒绝清空（多半是锁着）。</summary>
    RemoveAllRefused,

    /// <summary>要加入 agent 的钥或目的地约束太大，超出了 agent 报文的长度上限（<see cref="Diagnostics.SshFailureReason.LimitExceeded"/>）。</summary>
    RequestTooLarge,
}
