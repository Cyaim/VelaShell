// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   RFC 4254 §5.1  CHANNEL_OPEN_FAILURE 的原因码
//   RFC 4254 §6.2、§6.5  pty-req / shell / exec / subsystem 请求
//   行为规格: velashell-docs/zh/ssh/spec/05-connection.md §2.3、§8;velashell-docs/zh/ssh/spec/08-failures.md §一

namespace VelaShell.Ssh.Channels;

/// <summary>通道失败的具体情形（<see cref="SshChannelException.FailureKind"/>）—— 界面据此给出「下一步该查什么」的建议。</summary>
/// <remarks>
/// 异常消息里的中文建议是写给开发者的；界面按这个种类与异常上的参数自己说，不去解析那句话。
/// 零值 <see cref="Unknown"/> 是「没归类」：库自己抛的通道异常都会归类。
/// </remarks>
public enum SshChannelFailureKind
{
    /// <summary>没归类。</summary>
    Unknown = 0,

    /// <summary>服务端禁止端口转发（<c>CHANNEL_OPEN_FAILURE</c> 原因码 1，转发类通道）：查 sshd_config 的 <c>AllowTcpForwarding</c>。</summary>
    ForwardingProhibited,

    /// <summary>服务端管理性地拒绝了这条通道（原因码 1，别的通道类型）：查 sshd_config 的相关开关。</summary>
    OpenProhibited,

    /// <summary>服务端连不上转发的目标地址（原因码 2）。</summary>
    ForwardTargetUnreachable,

    /// <summary>服务端不认识通道类型 <see cref="SshChannelException.ChannelType"/>（原因码 3）。</summary>
    UnknownChannelType,

    /// <summary>服务端资源不足（原因码 4），常见原因是并发会话数已满（sshd_config 的 <c>MaxSessions</c>）。</summary>
    ResourceShortage,

    /// <summary>服务端以别的原因码拒绝打开通道（原因码在 <see cref="SshChannelException.OpenFailureReason"/>）。</summary>
    OpenRejected,

    /// <summary>对端还没确认通道就把它关掉了。</summary>
    ClosedBeforeConfirmation,

    /// <summary>本端的并发通道数已达上限（<see cref="SshChannelException.Limit"/> 条）。</summary>
    ChannelLimitReached,

    /// <summary>会话的接收窗口总预算已用尽（<see cref="SshChannelException.Limit"/> 字节）：把窗口策略调小，或者少开几条并发通道。</summary>
    WindowBudgetExhausted,

    /// <summary>服务端拒绝执行命令（<c>exec</c>）：账号被限制成只能跑固定命令，或者 sshd_config 里配了 <c>ForceCommand</c>。</summary>
    ExecRejected,

    /// <summary>服务端拒绝分配伪终端（<c>pty-req</c>）：sshd_config 里 <c>PermitTTY no</c>，或者账号被配成了无终端登录。</summary>
    PtyRejected,

    /// <summary>服务端拒绝启动 shell（<c>shell</c>）。</summary>
    ShellRejected,

    /// <summary>服务端没有提供子系统 <see cref="SshChannelException.SubsystemName"/>（<c>subsystem</c>；sftp 时查 sshd_config 的 <c>Subsystem sftp</c>）。</summary>
    SubsystemRejected,

    /// <summary>对端给通道宣告的 max packet 是 0：它不收任何数据，写进 stdin 的内容发不出去。</summary>
    ZeroMaxPacket,
}
