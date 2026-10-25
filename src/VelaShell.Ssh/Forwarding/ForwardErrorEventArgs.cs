// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 行为规格: velashell-docs/zh/ssh/spec/07-forwarding.md §五

namespace VelaShell.Ssh.Forwarding;

/// <summary>一条转发连接失败了 —— <b>转发器本身仍在跑</b>。</summary>
/// <remarks>创建一条错误事件。</remarks>
/// <param name="reason">失败的分类。</param>
/// <param name="message">诊断消息。</param>
/// <param name="exception">底层异常。</param>
public sealed class ForwardErrorEventArgs(ForwardErrorReason reason, string message, Exception? exception = null) : EventArgs
{

    /// <summary>失败的分类。</summary>
    public ForwardErrorReason Reason { get; } = reason;

    /// <summary>诊断消息（写给开发者看的，界面文案请按 <see cref="Reason"/> 本地化）。</summary>
    public string Message { get; } = message;

    /// <summary>底层异常（可能为空）。</summary>
    /// <remarks>
    /// 为空的那几种（撞并发上限、SOCKS 握手没完成或不合法、隧道在时限内没打开、目标不在放行名单）界面没有异常可译，
    /// 按 <see cref="Reason"/> 与下面几个参数说 —— 不解析 <see cref="Message"/>。
    /// </remarks>
    public Exception? Exception { get; } = exception;

    /// <summary>这一条要连的目标（<c>host:port</c>，或 Unix 套接字路径）；还没读出目标时（SOCKS 握手没完成、撞上限）为 <see langword="null"/>。</summary>
    /// <remarks>动态转发里它是 SOCKS 客户端给的名字（远程动态转发时来自对端，已清洗过）；展示前照样按不可信文本处理。</remarks>
    public string? Target { get; init; }

    /// <summary>
    /// 超时没完成的那一种的时限：<see cref="ForwardErrorReason.SocksHandshake"/> 是 SOCKS 握手的时限，
    /// <see cref="ForwardErrorReason.ChannelOpen"/> 是隧道通道打开的时限；不是超时为 <see langword="null"/>。
    /// </summary>
    public TimeSpan? Timeout { get; init; }

    /// <summary>撞上的并发连接上限（<see cref="ForwardErrorReason.ConnectionLimit"/>）。</summary>
    public int? Limit { get; init; }

    /// <summary>
    /// 这一次报告代表拒了几条（<see cref="ForwardErrorReason.ConnectionLimit"/>：事件每秒至多报一次，期间拒的都算在这一条里）。
    /// </summary>
    public int? RejectedCount { get; init; }
}
