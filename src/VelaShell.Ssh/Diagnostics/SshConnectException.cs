// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 行为规格: velashell-docs/zh/ssh/spec/08-failures.md §1(原则)、§2(层级)、§5(带上下文的三个异常)

namespace VelaShell.Ssh.Diagnostics;

/// <summary>建连阶段的失败（拨号、版本交换、协商、主机密钥）。</summary>
/// <remarks>用给定原因、阶段与消息创建异常。</remarks>
public class SshConnectException(SshFailureReason reason, SshPhase phase, string message, Exception? innerException = null) : SshException(reason, phase, message, innerException)
{

    /// <summary>
    /// 链路上每一跳的结果（代理、跳板、最终目标）。
    /// </summary>
    /// <remarks>
    /// 失败在哪一跳**必须能看出来**：「连不上跳板机 jump.example.com」与
    /// 「连不上 10.0.0.9」对用户是两个完全不同的问题，而没有这张表它们长得一模一样。
    /// </remarks>
    public IReadOnlyList<SshHopInfo> Hops { get; init; } = [];

    /// <summary>具体是哪种情形 —— 界面按它说，不解析 <see cref="Exception.Message"/> 里的中文。</summary>
    /// <remarks>
    /// 库自己抛的主机密钥被拒、代理握手失败、跳板失败都填；原因码本身说得清的（DNS、TCP、超时……）与使用者自己构造的是
    /// <see cref="SshConnectFailureKind.Unknown"/>。下面几个参数各在哪些种类上有值，见各种类的说明。
    /// </remarks>
    public SshConnectFailureKind FailureKind { get; init; }

    /// <summary>
    /// 经过的那一跳：代理的地址（<c>host:port</c>）、跳板（<c>host:port</c>，按连接参数构造的带 <c>user@</c>），或 <c>ProxyCommand</c> 的命令行。
    /// </summary>
    /// <remarks>来自本端的配置，不来自对端；地址里的主机名可能是用户粘进来的一串，展示前照样过一遍 <see cref="PeerText.Sanitize"/>。</remarks>
    public string? Via { get; init; }

    /// <summary>请代理 / 跳板去连的目标（<c>host:port</c>）；<see cref="SshConnectFailureKind.ProxyTargetNameInvalid"/> 时是那个放不进请求的主机名。<b>未清洗</b>。</summary>
    public string? Target { get; init; }

    /// <summary>服务端出示的主机密钥的类型串（证书带 <c>-cert-v01@openssh.com</c> 后缀）。来自对端：<b>未清洗</b>，展示前先过 <see cref="PeerText.Sanitize"/>。</summary>
    public string? HostKeyType { get; init; }

    /// <summary>协商出的主机密钥算法（本端名单里的名字）。</summary>
    public string? HostKeyAlgorithm { get; init; }

    /// <summary>主机密钥的位数（<see cref="SshConnectFailureKind.HostKeyTooShort"/>）。</summary>
    public int? HostKeyBits { get; init; }

    /// <summary>要求的最小位数（<see cref="SshConnectFailureKind.HostKeyTooShort"/>）。</summary>
    public int? MinimumHostKeyBits { get; init; }

    /// <summary>
    /// 代理回的那个数：SOCKS5 的版本字节 / 方法字节 / 地址类型 / 应答码，HTTP 的状态码，<c>ProxyCommand</c> 的退出码 —— 是哪一个看 <see cref="FailureKind"/>。
    /// </summary>
    public int? ProxyReplyCode { get; init; }

    /// <summary>
    /// 代理回的原话：HTTP 的状态行，<c>ProxyCommand</c> 的 stderr 末尾（已清洗）。HTTP 的状态行来自对端：<b>未清洗</b>，展示前先过 <see cref="PeerText.Sanitize"/>。
    /// </summary>
    public string? ProxyReply { get; init; }
}
