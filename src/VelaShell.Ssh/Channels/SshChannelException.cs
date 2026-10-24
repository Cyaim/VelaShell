// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 行为规格: velashell-docs/zh/ssh/spec/05-connection.md §2.3、§8

using VelaShell.Ssh.Diagnostics;
using VelaShell.Ssh.Protocol;

namespace VelaShell.Ssh.Channels;

/// <summary>通道级的失败。</summary>
/// <remarks>
/// <b>通道是独立的失败域</b>（velashell-docs/zh/ssh/spec/05 §1 规则 5）：
/// 一条通道打不开或出错，不得影响会话或其它通道。
/// 所以这条异常从通道的方法里抛出来，而**会话仍然可用**。
/// </remarks>
/// <remarks>创建一个通道异常。</remarks>
public sealed class SshChannelException(
    SshFailureReason reason,
    string message,
    SshChannelOpenFailureReason? openFailureReason = null,
    string? peerDescription = null,
    Exception? innerException = null) : SshException(reason, SshPhase.Open, message, innerException)
{

    /// <summary>对端给的 <c>CHANNEL_OPEN_FAILURE</c> 原因码。</summary>
    public SshChannelOpenFailureReason? OpenFailureReason { get; } = openFailureReason;

    /// <summary>对端给的说明文字。<b>不可信文本</b>，展示时按不可信内容处理。</summary>
    public string? PeerDescription { get; } = peerDescription;

    /// <summary>具体是哪种情形 —— 界面按它给建议，不解析 <see cref="Exception.Message"/> 里的中文建议。</summary>
    /// <remarks>库自己抛的通道异常都填；使用者自己构造、没填时是 <see cref="SshChannelFailureKind.Unknown"/>。</remarks>
    public SshChannelFailureKind FailureKind { get; init; }

    /// <summary>
    /// 开不成的通道类型（<c>CHANNEL_OPEN_FAILURE</c> 的几种情形）。入站的开通道请求里它来自对端：<b>未清洗</b>，展示前先过
    /// <see cref="PeerText.Sanitize"/>。
    /// </summary>
    public string? ChannelType { get; init; }

    /// <summary>被拒的子系统名（<see cref="SshChannelFailureKind.SubsystemRejected"/>）。</summary>
    public string? SubsystemName { get; init; }

    /// <summary>撞到的本端上限（<see cref="SshChannelFailureKind.ChannelLimitReached"/> 是通道数，<see cref="SshChannelFailureKind.WindowBudgetExhausted"/> 是字节数）。</summary>
    public long? Limit { get; init; }

    /// <summary>
    /// 把一个 <c>CHANNEL_OPEN_FAILURE</c> 翻成一句能照着做事的话。
    /// </summary>
    /// <remarks>
    /// 〔决策 velashell-docs/zh/ssh/spec/05 §2.3〕原因码 1 与 4 **必须**给出可操作的提示。
    /// 「服务端禁止了端口转发（AllowTcpForwarding no）」比「通道打开失败」有用得多 ——
    /// 前者告诉用户去改哪个配置，后者只告诉他事情没成。
    /// </remarks>
    internal static SshChannelException FromOpenFailure(
        string channelType, uint reasonCode, string description)
    {
        var reason = (SshChannelOpenFailureReason)reasonCode;

        string advice = reason switch
        {
            SshChannelOpenFailureReason.AdministrativelyProhibited =>
                channelType is SshProtocolNames.ChannelDirectTcpIp or SshProtocolNames.ChannelDirectStreamLocal
                    ? "服务端禁止了端口转发（检查 sshd_config 的 AllowTcpForwarding）。"
                    : "服务端管理性地拒绝了这条通道（检查 sshd_config 的相关开关）。",

            SshChannelOpenFailureReason.ConnectFailed =>
                "服务端连不上转发的目标地址。",

            SshChannelOpenFailureReason.UnknownChannelType =>
                $"服务端不认识通道类型 {PeerText.Sanitize(channelType, 64)}。",

            SshChannelOpenFailureReason.ResourceShortage =>
                "服务端资源不足 —— 常见原因是并发会话数已满（sshd_config 的 MaxSessions）。",

            _ => $"服务端拒绝打开通道，原因码 {reasonCode}。",
        };

        // 服务端的原话进消息之前先清一遍（见 PeerText）；原话照样留在 PeerDescription 里。
        // 通道类型也清：入站的开通道请求里它来自对端。
        string message = string.IsNullOrWhiteSpace(description)
            ? advice
            : $"{advice} 服务端说：{PeerText.Sanitize(description)}";

        return new SshChannelException(
            SshFailureReason.ChannelOpenFailed, message, reason, description)
        {
            FailureKind = reason switch
            {
                SshChannelOpenFailureReason.AdministrativelyProhibited =>
                    channelType is SshProtocolNames.ChannelDirectTcpIp or SshProtocolNames.ChannelDirectStreamLocal
                        ? SshChannelFailureKind.ForwardingProhibited
                        : SshChannelFailureKind.OpenProhibited,
                SshChannelOpenFailureReason.ConnectFailed => SshChannelFailureKind.ForwardTargetUnreachable,
                SshChannelOpenFailureReason.UnknownChannelType => SshChannelFailureKind.UnknownChannelType,
                SshChannelOpenFailureReason.ResourceShortage => SshChannelFailureKind.ResourceShortage,
                _ => SshChannelFailureKind.OpenRejected,
            },
            ChannelType = channelType,
        };
    }
}
