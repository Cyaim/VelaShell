// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 被测规格: velashell-docs/zh/ssh/spec/09-dialing.md §7.3

using VelaShell.Ssh.Config;
using VelaShell.Ssh.Diagnostics;
using VelaShell.Ssh.Forwarding;

namespace VelaShell.Ssh.Tests.Config;

/// <summary>ssh_config 的三种转发键解析成结构化的两头，以及 ClearAllForwardings、PermitRemoteOpen。</summary>
[TestClass]
[TestCategory("Config")]
public sealed class SshConfigForwardTests
{
    private static SshConfigForward Local(string value) => SshConfigForward.Parse(SshConfigForwardKind.Local, value);

    /// <summary>本地转发的各种写法：只给端口、带地址、IPv6、全部网卡（* 与空）、两头都是套接字、到远端套接字。</summary>
    [TestMethod]
    public void 本地转发的写法()
    {
        Assert.AreEqual(new SshConfigForward(SshConfigForwardKind.Local, null, 8080, null, "db.internal", 5432, null, "8080 db.internal:5432"),
            Local("8080 db.internal:5432"));

        SshConfigForward withAddress = Local("127.0.0.1:8080 db:5432");
        Assert.AreEqual("127.0.0.1", withAddress.ListenAddress);

        SshConfigForward v6 = Local("[::1]:8080 [fe80::1]:22");
        Assert.AreEqual("::1", v6.ListenAddress);
        Assert.AreEqual("fe80::1", v6.TargetHost);
        Assert.AreEqual(22, v6.TargetPort);

        Assert.AreEqual("*", Local("*:8080 h:1").ListenAddress);
        Assert.AreEqual("*", Local(":8080 h:1").ListenAddress, "空地址同 *");

        SshConfigForward sockets = Local("/tmp/d.sock /var/run/docker.sock");
        Assert.AreEqual("/tmp/d.sock", sockets.ListenSocketPath);
        Assert.AreEqual("/var/run/docker.sock", sockets.TargetSocketPath);
        Assert.IsNull(sockets.TargetHost);

        Assert.AreEqual("/var/run/docker.sock", Local("2375 /var/run/docker.sock").TargetSocketPath);
    }

    /// <summary>远程转发不给目标是远程动态转发；端口 0 交给服务端分配；动态转发只有监听那一头。</summary>
    [TestMethod]
    public void 远程与动态转发的写法()
    {
        var remoteDynamic = SshConfigForward.Parse(SshConfigForwardKind.Remote, "9090");
        Assert.IsTrue(remoteDynamic.IsRemoteDynamic);
        Assert.AreEqual(9090, remoteDynamic.ListenPort);

        var remote = SshConfigForward.Parse(SshConfigForwardKind.Remote, "0 localhost:22");
        Assert.AreEqual(0, remote.ListenPort);
        Assert.IsFalse(remote.IsRemoteDynamic);

        var dynamic = SshConfigForward.Parse(SshConfigForwardKind.Dynamic, "127.0.0.1:1080");
        Assert.AreEqual("127.0.0.1", dynamic.ListenAddress);
        Assert.AreEqual(1080, dynamic.ListenPort);
    }

    /// <summary>写错的当场报 InvalidConfiguration，消息里有原文。</summary>
    [TestMethod]
    public void 写错的转发报配置错误()
    {
        foreach ((SshConfigForwardKind kind, string value) in new[]
                 {
                     (SshConfigForwardKind.Local, "8080"),
                     (SshConfigForwardKind.Local, "8080 db"),
                     (SshConfigForwardKind.Local, "70000 h:1"),
                     (SshConfigForwardKind.Remote, "8080 h:0"),
                     (SshConfigForwardKind.Dynamic, "/tmp/s.sock"),
                     (SshConfigForwardKind.Dynamic, "1080 extra"),
                 })
        {
            SshConnectException failure = Assert.ThrowsExactly<SshConnectException>(() => SshConfigForward.Parse(kind, value), value);
            Assert.AreEqual(SshFailureReason.InvalidConfiguration, failure.Reason);
            Assert.Contains(value, failure.Message);
        }
    }

    /// <summary>三个键累加；ClearAllForwardings 清空；PermitRemoteOpen 没写是 any、any / none 照字面、名单照规则、写错报配置错误。</summary>
    [TestMethod]
    public void 主机配置里的转发与放行名单()
    {
        IReadOnlyList<SshConfigBlock> blocks = SshConfigFile.Parse("""
            Host a
                LocalForward 8080 db:5432
                LocalForward 8081 cache:6379
                DynamicForward 1080
                RemoteForward 9090
                PermitRemoteOpen intranet.example:443 10.0.0.*:*
                ExitOnForwardFailure yes
                GatewayPorts yes
            Host b
                LocalForward 8080 db:5432
                ClearAllForwardings yes
                PermitRemoteOpen none
            Host c
                PermitRemoteOpen intranet.example
            Host d
                HostName d.example
            """);

        SshHostConfig a = SshConfigFile.Resolve(blocks, "a");
        Assert.AreSequenceEqual(
            [SshConfigForwardKind.Local, SshConfigForwardKind.Local, SshConfigForwardKind.Remote, SshConfigForwardKind.Dynamic],
            [.. a.GetForwards().Select(f => f.Kind)]);
        Assert.IsTrue(a.ExitOnForwardFailure);
        Assert.IsTrue(a.GatewayPorts);
        Assert.IsTrue(a.GetPermitRemoteOpen().Permits("intranet.example", 443));
        Assert.IsTrue(a.GetPermitRemoteOpen().Permits("10.0.0.7", 22));
        Assert.IsFalse(a.GetPermitRemoteOpen().Permits("intranet.example", 80));

        SshHostConfig b = SshConfigFile.Resolve(blocks, "b");
        Assert.IsEmpty(b.GetForwards());
        Assert.IsFalse(b.GetPermitRemoteOpen().Permits("anything", 1));

        Assert.AreEqual(SshFailureReason.InvalidConfiguration,
            Assert.ThrowsExactly<SshConnectException>(() => SshConfigFile.Resolve(blocks, "c").GetPermitRemoteOpen()).Reason);
        Assert.AreSame(RemoteOpenPolicy.Any, SshConfigFile.Resolve(blocks, "d").GetPermitRemoteOpen(), "没写是 OpenSSH 的默认 any");
    }
}
