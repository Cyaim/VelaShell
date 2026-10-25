// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 行为规格: velashell-docs/zh/ssh/spec/07-forwarding.md §八

using VelaShell.Ssh.Diagnostics;

namespace VelaShell.Ssh.Forwarding;

/// <summary>转发器起不来。</summary>
/// <remarks>
/// <para>
/// <b>只有「转发器本身」的失败才抛这个。</b>
/// 单条连接的失败走转发器的 <c>Error</c> 事件 ——
/// 一条隧道要能跑几天，期间必然有连不上的目标、被重置的连接；
/// 把那些当成致命错误，隧道就没法用了。
/// </para>
/// <para>
/// 具体是哪一种看 <see cref="SshException.Reason"/>：<see cref="SshFailureReason.ForwardRejected"/>、
/// <see cref="SshFailureReason.ForwardBindFailed"/>、<see cref="SshFailureReason.ForwardSetupFailed"/>、
/// <see cref="SshFailureReason.LimitExceeded"/>、<see cref="SshFailureReason.ProtocolError"/>；
/// 更细的情形与参数看 <see cref="FailureKind"/>。
/// </para>
/// </remarks>
/// <remarks>创建一个转发异常。</remarks>
/// <param name="reason">失败的分类。</param>
/// <param name="message">诊断消息。</param>
/// <param name="innerException">内部异常。</param>
public sealed class SshForwardException(SshFailureReason reason, string message, Exception? innerException = null) : SshException(reason, SshPhase.Open, message, innerException)
{
    /// <summary>具体是哪种情形 —— 界面按它说，不解析 <see cref="Exception.Message"/> 里的中文。</summary>
    /// <remarks>库自己抛的转发异常都填；使用者自己构造、没填时是 <see cref="SshForwardFailureKind.Unknown"/>。</remarks>
    public SshForwardFailureKind FailureKind { get; init; }

    /// <summary>
    /// 涉及的监听端点：本机或服务端的 <c>地址:端口</c>，或 Unix 套接字路径（<see cref="SshForwardFailureKind.UnmatchedForwardedChannel"/> 时是服务端报来的，<b>未清洗</b>）。
    /// </summary>
    public string? Endpoint { get; init; }

    /// <summary>请求服务端监听的端口（<see cref="SshForwardFailureKind.RemoteListenRejected"/>；1–1023 是特权端口）。</summary>
    public int? Port { get; init; }

    /// <summary>撞上的并发上限（<see cref="SshForwardFailureKind.ChannelLimitReached"/>）。</summary>
    public int? Limit { get; init; }

    /// <summary>xauth 的时限（<see cref="SshForwardFailureKind.XAuthTimedOut"/>）。</summary>
    public TimeSpan? Timeout { get; init; }

    /// <summary>X 显示在 xauth 里的名字（<see cref="SshForwardFailureKind.XAuthNoCookie"/>）。</summary>
    public string? DisplayName { get; init; }

    /// <summary>xauth 的退出码（<see cref="SshForwardFailureKind.XAuthFailed"/>）。</summary>
    public int? ExitCode { get; init; }

    /// <summary>xauth 在 stderr 上说的末尾一段（<see cref="SshForwardFailureKind.XAuthFailed"/>），已按 <see cref="PeerText.SanitizeTail"/> 清洗、换行收成一行。</summary>
    public string? ErrorOutput { get; init; }

    /// <summary>没起来的那条 ssh_config 转发的原文（<see cref="SshForwardFailureKind.ConfigForwardFailed"/>），<b>未清洗</b>。</summary>
    public string? ConfigLine { get; init; }
}
