// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 被测规格: velashell-docs/zh/ssh/spec/05-connection.md §2.3;velashell-docs/zh/ssh/spec/08-failures.md §一

using VelaShell.Ssh.Channels;
using VelaShell.Ssh.Protocol;

namespace VelaShell.Ssh.Tests.Channels;

/// <summary>
/// <c>CHANNEL_OPEN_FAILURE</c> 翻成的建议：每个原因码都归到一个 <see cref="SshChannelFailureKind"/>，
/// 消息的中文原文与结构化之前一字不差（照抄自那时的 <c>FromOpenFailure</c>）。
/// </summary>
[TestClass]
[TestCategory("Channels")]
public sealed class ChannelFailureKindTests
{
    [TestMethod]
    [DataRow(SshProtocolNames.ChannelDirectTcpIp, 1u, SshChannelFailureKind.ForwardingProhibited, "服务端禁止了端口转发（检查 sshd_config 的 AllowTcpForwarding）。")]
    [DataRow(SshProtocolNames.ChannelDirectStreamLocal, 1u, SshChannelFailureKind.ForwardingProhibited, "服务端禁止了端口转发（检查 sshd_config 的 AllowTcpForwarding）。")]
    [DataRow(SshProtocolNames.ChannelSession, 1u, SshChannelFailureKind.OpenProhibited, "服务端管理性地拒绝了这条通道（检查 sshd_config 的相关开关）。")]
    [DataRow(SshProtocolNames.ChannelDirectTcpIp, 2u, SshChannelFailureKind.ForwardTargetUnreachable, "服务端连不上转发的目标地址。")]
    [DataRow("x-unknown@example", 3u, SshChannelFailureKind.UnknownChannelType, "服务端不认识通道类型 x-unknown@example。")]
    [DataRow(SshProtocolNames.ChannelSession, 4u, SshChannelFailureKind.ResourceShortage, "服务端资源不足 —— 常见原因是并发会话数已满（sshd_config 的 MaxSessions）。")]
    [DataRow(SshProtocolNames.ChannelSession, 99u, SshChannelFailureKind.OpenRejected, "服务端拒绝打开通道，原因码 99。")]
    public void 每个原因码都归了类且原文不变(string channelType, uint code, SshChannelFailureKind kind, string advice)
    {
        SshChannelException plain = SshChannelException.FromOpenFailure(channelType, code, "");
        Assert.AreEqual(kind, plain.FailureKind);
        Assert.AreEqual(channelType, plain.ChannelType);
        Assert.AreEqual(advice, plain.Message);

        SshChannelException said = SshChannelException.FromOpenFailure(channelType, code, "nope");
        Assert.AreEqual($"{advice} 服务端说：nope", said.Message);
        Assert.AreEqual("nope", said.PeerDescription);
    }
}
