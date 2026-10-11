// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 被测规格: velashell-docs/zh/ssh/spec/05-connection.md §6.5

using VelaShell.Ssh.Protocol;
using VelaShell.Ssh.Tests.TestKit;

namespace VelaShell.Ssh.Tests.Session;

/// <summary>传输层的 PING / PONG：对端的 PING 原样回 PONG；对端宣告了 ping@openssh.com，量往返时间就用 PING。</summary>
[TestClass]
[TestCategory("Session")]
public sealed class PingTests
{
    /// <summary>服务端认证之后宣告 ping 并发一个 PING：客户端原样回 PONG，也就认得 PING 了；量往返时间发的是 PING 而不是保活全局请求。</summary>
    [TestMethod]
    public async Task 对端的PING原样回PONG_宣告了就用PING量往返()
    {
        byte[] data = [1, 2, 3, 4, 5];
        await using TestSshServerHost host = await TestSshServerHost.StartAsync(new TestChannelScript
        {
            PingOnStart = data,
            AnswerPings = true,
        });

        for (int i = 0; i < 200 && (host.Channels.Observation.Pongs.Count == 0 || !host.Connection.PeerSupportsPing); i++)
        {
            await Task.Delay(10, host.Token);
        }
        Assert.IsTrue(host.Connection.PeerSupportsPing, "认证之后的 EXT_INFO 里宣告了 ping@openssh.com");
        Assert.AreSequenceEqual(data, host.Channels.Observation.Pongs.Single(), "PONG 原样带回数据");

        TimeSpan rtt = await host.Connection.MeasureRoundTripAsync(host.Token);
        Assert.IsGreaterThan(TimeSpan.Zero, rtt);
        Assert.AreEqual(1, host.Channels.Observation.PingsReceived);
        Assert.DoesNotContain(SshProtocolNames.KeepAliveOpenSsh, host.Channels.Observation.GlobalRequests);
    }

    /// <summary>没宣告 ping：量往返时间照旧用保活全局请求，一个 PING 都不发（对端不认的报文不该发）。</summary>
    [TestMethod]
    public async Task 没宣告ping就不发PING()
    {
        await using TestSshServerHost host = await TestSshServerHost.StartAsync(new TestChannelScript());

        Assert.IsFalse(host.Connection.PeerSupportsPing);
        await host.Connection.MeasureRoundTripAsync(host.Token);
        Assert.AreEqual(0, host.Channels.Observation.PingsReceived);
        Assert.Contains(SshProtocolNames.KeepAliveOpenSsh, host.Channels.Observation.GlobalRequests);
    }
}
