// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 被测规格: velashell-docs/zh/ssh/spec/07-forwarding.md §7.1;velashell-docs/zh/ssh/spec/08-failures.md §3
//
// 本机 agent 没在跑是常态（Windows 上服务默认停着）。这一组钉住两件事：
// 连不上要**有时限**地报出来，而且原因码要说清是「没在跑」—— 宿主据此给出本地化的提示。

using System.Diagnostics;
using VelaShell.Ssh.Diagnostics;
using VelaShell.Ssh.Keys;

namespace VelaShell.Ssh.Tests.Keys;

[TestClass]
[TestCategory("Keys")]
public sealed class AgentConnectTests
{
    /// <summary>
    /// 默认端点：Windows 上 SSH_AUTH_SOCK 是命名管道时采纳（1Password、KeePassXC），是 Unix 套接字（Git Bash / WSL）时不认、
    /// 用 OpenSSH agent 服务的管道；其它平台照 SSH_AUTH_SOCK。曾经 Windows 上一律无视 SSH_AUTH_SOCK。
    /// </summary>
    [TestMethod]
    public void 默认端点在Windows上认命名管道形式的SSH_AUTH_SOCK()
    {
        const string openSshPipe = @"\\.\pipe\openssh-ssh-agent";

        Assert.AreEqual(@"\\.\pipe\agent-1password", SshAgentClient.DefaultEndpointFor(windows: true, @"\\.\pipe\agent-1password"));
        Assert.AreEqual(@"\\.\PIPE\keepassxc", SshAgentClient.DefaultEndpointFor(windows: true, @"\\.\PIPE\keepassxc"));
        Assert.AreEqual(openSshPipe, SshAgentClient.DefaultEndpointFor(windows: true, "/tmp/ssh-XXXX/agent.123"));
        Assert.AreEqual(openSshPipe, SshAgentClient.DefaultEndpointFor(windows: true, null));
        Assert.AreEqual("/run/user/1000/agent.sock", SshAgentClient.DefaultEndpointFor(windows: false, "/run/user/1000/agent.sock"));
        Assert.IsNull(SshAgentClient.DefaultEndpointFor(windows: false, null));
    }

    [TestMethod]
    public async Task 端点为空时报没在跑()
    {
        // 等同于 SSH_AUTH_SOCK 没设。
        SshAgentException error = await Assert.ThrowsExactlyAsync<SshAgentException>(
            async () => await SshAgentClient.ConnectAsync(""));

        Assert.AreEqual(SshFailureReason.AgentNotRunning, error.Reason);
        Assert.AreEqual(SshAgentFailureKind.NotRunning, error.FailureKind);
    }

    [TestMethod]
    public async Task 套接字不存在时报没在跑()
    {
        // 文件名要短：macOS 的临时目录本身就有约 50 个字符，而 Unix 套接字路径上限是 104（Linux 108）——
        // 超了的话构造端点时就抛，测到的是「路径不合法」而不是「没在跑」。
        string missing = Path.Combine(Path.GetTempPath(), $"vna-{Guid.NewGuid().ToString("N")[..8]}.sock");

        SshAgentException error = await Assert.ThrowsExactlyAsync<SshAgentException>(
            async () => await SshAgentClient.ConnectAsync(missing));

        Assert.AreEqual(SshFailureReason.AgentNotRunning, error.Reason, error.Message);
        Assert.AreEqual(SshAgentFailureKind.NotRunning, error.FailureKind);
        Assert.AreEqual(missing, error.Endpoint);
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Windows, IgnoreMessage = "只在 Windows 上走命名管道。")]
    public async Task 命名管道不存在时在时限内报没在跑而不是一直等()
    {
        // 不带时限的管道连接会一直重试到管道出现 —— 曾经 agent 转发那一路就是这样，
        // 服务没起时远端的 ssh / git 一直挂到 shell 关掉。
        string pipe = $@"\\.\pipe\velashell-no-agent-{Guid.NewGuid():N}";
        var elapsed = Stopwatch.StartNew();

        SshAgentException error = await Assert.ThrowsExactlyAsync<SshAgentException>(
            () => SshAgentClient.ConnectAsync(pipe).AsTask().WaitAsync(TimeSpan.FromSeconds(30)));

        Assert.AreEqual(SshFailureReason.AgentNotRunning, error.Reason, error.Message);
        Assert.IsLessThan(SshAgentClient.PipeConnectTimeout + TimeSpan.FromSeconds(5), elapsed.Elapsed);
    }
}
