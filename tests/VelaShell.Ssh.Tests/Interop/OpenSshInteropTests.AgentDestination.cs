// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 被测规格: velashell-docs/zh/ssh/spec/07-forwarding.md §7.3.2(agent 加钥的目的地约束)
//
// 目的地约束由 agent 执行 —— 测试桩与实现出自同一份理解，两边一起错时照样全绿。
// 这里全部对着真的 OpenSSH ssh-agent：在服务端起一个只为用例服务的 agent，经隧道接过来。

using System.Net.Sockets;
using VelaShell.Ssh.Auth;
using VelaShell.Ssh.Channels;
using VelaShell.Ssh.Diagnostics;
using VelaShell.Ssh.Forwarding;
using VelaShell.Ssh.HostKeys;
using VelaShell.Ssh.Keys;
using VelaShell.Ssh.Session;

namespace VelaShell.Ssh.Tests.Interop;

public sealed partial class OpenSshInteropTests
{
    /// <summary>容器里 sshd 自己监听的端口（从服务端自己看，跳板与 ssh-keyscan 用）。</summary>
    private const int InnerPort = 2222;

    /// <summary>服务端上一个只为一条用例服务的 ssh-agent，以及为它授权的那把钥；释放时一并收拾。</summary>
    private sealed class RemoteAgent(SshConnection connection, string socket, string pid, string tag) : IAsyncDisposable
    {
        /// <summary>agent 在服务端的套接字。</summary>
        public string Socket => socket;

        public static async Task<RemoteAgent> StartAsync(SshConnection connection)
        {
            string socket = $"/tmp/vela-rd-{Guid.NewGuid():N}.sock";
            SshCommandResult started = await connection.RunAsync($"ssh-agent -s -a {socket}");
            Assert.AreEqual(0, started.ExitCode, started.StandardError);
            string pid = started.StandardOutput.Split("SSH_AGENT_PID=")[1].Split(';')[0];
            return new RemoteAgent(connection, socket, pid, $"vela-rd-{Guid.NewGuid():N}");
        }

        /// <summary>经这条连接接到 agent 上的一个新客户端（交来的流，重开不了）。</summary>
        public async Task<SshAgentClient> OpenClientAsync() =>
            SshAgentClient.FromStream((await connection.OpenUnixSocketTunnelAsync(socket)).AsStream(), socket);

        /// <summary>把这把钥加进登录用户的 authorized_keys（释放时删掉）。</summary>
        public async Task AuthorizeAsync(SshPublicKey key)
        {
            SshCommandResult result = await connection.RunAsync(
                $"mkdir -p ~/.ssh && chmod 700 ~/.ssh && echo '{key.ToOpenSshFormat()} {tag}' >> ~/.ssh/authorized_keys && chmod 600 ~/.ssh/authorized_keys");
            Assert.AreEqual(0, result.ExitCode, result.StandardError);
        }

        /// <summary>从服务端自己看，<c>ssh-keyscan</c> 一台主机，按 <c>known_hosts</c> 拼出它（用的就是 <see cref="SshAgentHopHost.FromKnownHosts"/>）。</summary>
        public async Task<SshAgentHopHost> ScanAsync(string host, int port) =>
            await TryScanAsync(host, port) ?? throw new AssertFailedException($"ssh-keyscan {host}:{port} 什么都没扫到。");

        /// <summary>同 <see cref="ScanAsync"/>；从服务端连不到那台主机时交回 <see langword="null"/>。</summary>
        public async Task<SshAgentHopHost?> TryScanAsync(string host, int port)
        {
            SshCommandResult scanned = await connection.RunAsync($"ssh-keyscan -T 5 -p {port} {host} 2>/dev/null");
            return SshAgentHopHost.FromKnownHosts(KnownHostsFile.Parse(scanned.StandardOutput), host, port);
        }

        /// <summary>服务端 <c>ssh-add -l</c> 的输出（没声明过的连接）。</summary>
        public async Task<string> ListAsync() =>
            (await connection.RunAsync($"SSH_AUTH_SOCK={socket} ssh-add -l")).StandardOutput;

        public async ValueTask DisposeAsync() =>
            await connection.RunAsync($"kill {pid}; rm -f {socket}; sed -i '/{tag}/d' ~/.ssh/authorized_keys 2>/dev/null; true");
    }

    /// <summary>先用 agent 里的钥、不行再用口令登录一次，交回最终用的认证方法：<c>publickey</c> 是 agent 签了，<c>password</c> 是它拒签了。</summary>
    /// <remarks>
    /// 反向用例不让认证整个失败：OpenSSH 9.8 起的 PerSourcePenalties 会把认证失败过几次的来源地址拒掉一阵，
    /// 连累后面从同一地址连的用例。agent 拒签只是这条凭据跳过、接着用口令，服务端那边不记失败。
    /// </remarks>
    private static async Task<string> LoginWithAgentAsync(SshAgentClient agent, SshConnectionOptions? options = null)
    {
        IReadOnlyList<SshCredential> credentials = [.. await agent.GetCredentialsAsync(), new PasswordCredential(Password)];
        await using SshConnection login = await SshConnection.ConnectAsync((options ?? Options()) with { Credentials = credentials });
        Assert.AreEqual(0, (await login.RunAsync("true")).ExitCode);
        return login.AuthenticationMethod;
    }

    /// <summary>
    /// 目的地约束对真 OpenSSH agent：没声明过的连接上列得出这把钥；放行的主机连得上（用户名可以带通配）；
    /// 不放行的主机、用户名不对，agent 拒签，认证记一笔、不抛别的。同一把钥再加一次，新约束整个替换旧的。
    /// </summary>
    [TestMethod]
    public async Task 目的地约束对真agent_放行的主机才签()
    {
        RequireServer();
        await using SshConnection admin = await SshConnection.ConnectAsync(Options());
        await using RemoteAgent remote = await RemoteAgent.StartAsync(admin);
        using var key = InMemorySshSigner.GenerateEd25519();
        await remote.AuthorizeAsync(key.PublicKey);
        SshAgentHopHost server = await remote.ScanAsync("127.0.0.1", InnerPort);
        using var stranger = InMemorySshSigner.GenerateEd25519();
        await using SshAgentClient adder = await remote.OpenClientAsync();

        async Task<string> AddAndLoginAsync(SshAgentKeyConstraints constraints)
        {
            await adder.AddIdentityAsync(key, "vela-rd", constraints);
            await using SshAgentClient agent = await remote.OpenClientAsync();
            return await LoginWithAgentAsync(agent);
        }

        Assert.AreEqual("publickey", await AddAndLoginAsync(new() { AllowedHops = [new SshAgentHop(server, userName: User)] }), "放行的主机与用户");
        Assert.Contains(key.PublicKey.Sha256Fingerprint, await remote.ListAsync(), "没声明过的连接上照样列得出");
        Assert.AreEqual("publickey", await AddAndLoginAsync(new() { AllowedHops = [new SshAgentHop(server, userName: User[..2] + "*")] }),
            "用户名可以带通配");

        Assert.AreEqual("password", await AddAndLoginAsync(new()
        {
            AllowedHops = [new SshAgentHop(new SshAgentHopHost("127.0.0.1", [stranger.PublicKey]))],
        }), "主机钥对不上：agent 拒签，退到口令");
        Assert.AreEqual("password", await AddAndLoginAsync(new() { AllowedHops = [new SshAgentHop(server, userName: "nobody-" + User)] }),
            "用户名对不上：agent 拒签，退到口令");

        // 不带约束再加一次：约束整个换掉，照常能登。
        Assert.AreEqual("publickey", await AddAndLoginAsync(new SshAgentKeyConstraints()), "再加一次不带约束的，旧约束随之没了");
    }

    /// <summary>
    /// 经跳板：跳板与目标用的是同一份 agent 凭据（ssh_config 的 ProxyJump 就是这样）。agent 不接受在已经为认证声明过的连接上
    /// 再声明另一个会话 —— 自己连上的客户端在后一跳之前重开连接，两跳都签；交来的流重开不了，受约束的钥在后一跳被拒，
    /// 而不带约束的钥照常能用。
    /// </summary>
    [TestMethod]
    public async Task 目的地约束对真agent_跳板与目标共用一个客户端()
    {
        RequireServer();
        if (!Socket.OSSupportsUnixDomainSockets)
        {
            Assert.Inconclusive("这台机器不支持 Unix 套接字。");
        }

        await using SshConnection admin = await SshConnection.ConnectAsync(Options());
        await using RemoteAgent remote = await RemoteAgent.StartAsync(admin);
        using var key = InMemorySshSigner.GenerateEd25519();
        await remote.AuthorizeAsync(key.PublicKey);
        SshAgentHopHost server = await remote.ScanAsync("127.0.0.1", InnerPort);

        // 本机一个 Unix 套接字转到服务端的 agent：自己连上的客户端（能重开）连它。
        string localSocket = Path.Combine(Path.GetTempPath(), $"vrd-{Guid.NewGuid().ToString("N")[..8]}.sock");
        await using var forwarder = LocalPortForwarder.StartToUnixSocket(
            admin, remote.Socket, new LocalPortForwardOptions { ListenSocketPath = localSocket });

        // 交回目标那一跳最终用的认证方法（口令兜底，理由见 LoginWithAgentAsync）。
        static async Task<string> ViaJumpAsync(SshAgentClient agent)
        {
            IReadOnlyList<SshCredential> credentials = [.. await agent.GetCredentialsAsync(), new PasswordCredential(Password)];
            SshConnectionOptions target = new(User, "127.0.0.1", InnerPort)
            {
                HostKeyPolicy = new DangerousAcceptAnyHostKeyPolicy(),
                Credentials = credentials,
                Dialer = Ssh.Transport.DialerChain.Jump(Options(credentials: credentials)),
                ConnectTimeout = TimeSpan.FromSeconds(30),
            };
            await using SshConnection connection = await SshConnection.ConnectAsync(target);
            Assert.AreEqual("经跳板", (await connection.RunAsync("echo 经跳板")).StandardOutput.Trim());
            return connection.AuthenticationMethod;
        }

        await using SshAgentClient adder = await remote.OpenClientAsync();
        await adder.AddIdentityAsync(key, "vela-rd", new SshAgentKeyConstraints { AllowedHops = [new SshAgentHop(server)] });

        await using (SshAgentClient reopening = await SshAgentClient.ConnectAsync(localSocket))
        {
            Assert.AreEqual("publickey", await ViaJumpAsync(reopening), "自己连上的客户端重开连接之后，目标那一跳也签");
        }

        await using (SshAgentClient fixedStream = await remote.OpenClientAsync())
        {
            Assert.AreEqual("password", await ViaJumpAsync(fixedStream),
                "agent 拒绝在同一条连接上第二次做认证声明 —— 不重开的话，受约束的钥在目标那一跳被拒签");
        }

        // 不带约束的钥不受「声明给了别的会话」影响。
        await adder.AddIdentityAsync(key, "vela-rd");
        await using (SshAgentClient fixedStream = await remote.OpenClientAsync())
        {
            Assert.AreEqual("publickey", await ViaJumpAsync(fixedStream), "不带约束的钥照常两跳都签");
        }
    }

    /// <summary>
    /// 经转发：钥带（本机 → A）与（A → B）时，转发到 A 上列得出、从 A 登 B 成功；去掉（A → B），A 上列不出、登 B 被拒。
    /// A 是这台服务端，B 是同一个 Docker 网络里的多 shell 靶子（docker-compose.test.yml 的 ssh-shells，本机 2223）。
    /// </summary>
    [TestMethod]
    public async Task 目的地约束对真agent_经转发逐跳放行()
    {
        RequireServer();
        if (!Socket.OSSupportsUnixDomainSockets)
        {
            Assert.Inconclusive("这台机器不支持 Unix 套接字。");
        }

        const string shellsHost = "velashell-test-shells", shellsUser = "vela-bash";
        using var key = InMemorySshSigner.GenerateEd25519();
        SshConnection? shells = null;
        string? unreachable = null;
        try
        {
            shells = await SshConnection.ConnectAsync(new SshConnectionOptions(shellsUser, Host, 2223)
            {
                HostKeyPolicy = new DangerousAcceptAnyHostKeyPolicy(),
                Credentials = [new PasswordCredential("velapass")],
                ConnectTimeout = TimeSpan.FromSeconds(10),
            });
        }
        catch (SshException ex)
        {
            unreachable = ex.Message;
        }
        if (shells is null)
        {
            Assert.Inconclusive($"多 shell 靶子（本机 2223）连不上：{unreachable}");
            return;
        }

        string tag = $"vela-rd-{Guid.NewGuid():N}";
        await using (shells)
        {
            SshCommandResult authorized = await shells.RunAsync(
                $"mkdir -p ~/.ssh && chmod 700 ~/.ssh && echo '{key.PublicKey.ToOpenSshFormat()} {tag}' >> ~/.ssh/authorized_keys && chmod 600 ~/.ssh/authorized_keys");
            Assert.AreEqual(0, authorized.ExitCode, authorized.StandardError);
            try
            {
                await using SshConnection admin = await SshConnection.ConnectAsync(Options());
                await using RemoteAgent remote = await RemoteAgent.StartAsync(admin);
                SshAgentHopHost a = await remote.ScanAsync("127.0.0.1", InnerPort);
                if (await remote.TryScanAsync(shellsHost, InnerPort) is not { } b)
                {
                    Assert.Inconclusive($"从这台服务端连不到 {shellsHost}（不在 docker-compose.test.yml 的同一个网络里）。");
                    return;
                }

                string localSocket = Path.Combine(Path.GetTempPath(), $"vrd-{Guid.NewGuid().ToString("N")[..8]}.sock");
                await using var forwarder = LocalPortForwarder.StartToUnixSocket(
                    admin, remote.Socket, new LocalPortForwardOptions { ListenSocketPath = localSocket });
                await using SshAgentClient adder = await remote.OpenClientAsync();

                async Task<SshCommandResult> HopAsync()
                {
                    await using SshConnection toA = await SshConnection.ConnectAsync(Options());
                    return await toA.RunAsync(
                        "ssh-add -l; ssh -o StrictHostKeyChecking=no -o UserKnownHostsFile=/dev/null -o BatchMode=yes " +
                        $"-o LogLevel=ERROR -p {InnerPort} {shellsUser}@{shellsHost} 'echo 到了B'",
                        new SshCommandOptions { AgentForwarding = new AgentForwardOptions { AgentEndpoint = localSocket } });
                }

                await adder.AddIdentityAsync(key, "vela-rd", new SshAgentKeyConstraints
                {
                    AllowedHops = [new SshAgentHop(a), new SshAgentHop(b, userName: shellsUser, via: a)],
                });
                SshCommandResult both = await HopAsync();
                Assert.Contains(key.PublicKey.Sha256Fingerprint, both.StandardOutput, "有一跳以 A 为起点：A 上列得出");
                Assert.Contains("到了B", both.StandardOutput, both.StandardError);

                await adder.AddIdentityAsync(key, "vela-rd", new SshAgentKeyConstraints { AllowedHops = [new SshAgentHop(a)] });
                SshCommandResult onlyA = await HopAsync();
                Assert.DoesNotContain(key.PublicKey.Sha256Fingerprint, onlyA.StandardOutput, "没有以 A 为起点的跳：A 上列不出");
                Assert.DoesNotContain("到了B", onlyA.StandardOutput, "少了（A → B）：登 B 被拒");
            }
            finally
            {
                await shells.RunAsync($"sed -i '/{tag}/d' ~/.ssh/authorized_keys; true");
            }
        }
    }

    /// <summary>
    /// 凭 CA 认主机：服务端出示主机证书、known_hosts 里只有 <c>@cert-authority</c> 行时，用 <see cref="SshAgentHopHost.FromKnownHosts"/>
    /// 拼出的跳连得上（名字对证书的 principals）；名字对不上 principals 时被拒。要 Start-TestServer.ps1 配好的主机证书。
    /// </summary>
    [TestMethod]
    public async Task 目的地约束对真agent_凭CA认主机()
    {
        RequireServer();
        string? hostCa = Environment.GetEnvironmentVariable("VELASHELL_SSH_INTEROP_HOST_CA");
        if (string.IsNullOrEmpty(hostCa) || !File.Exists(hostCa))
        {
            Assert.Inconclusive("没有配 VELASHELL_SSH_INTEROP_HOST_CA（Start-TestServer.ps1 会生成）。");
        }

        string pattern = Port == 22 ? Host : $"[{Host}]:{Port}";
        string knownHosts = Path.Combine(Path.GetTempPath(), $"vela-interop-kh-{Guid.NewGuid():N}");
        try
        {
            await File.WriteAllTextAsync(knownHosts, $"@cert-authority {pattern} {(await File.ReadAllTextAsync(hostCa)).Trim()}\n");
            IReadOnlyList<KnownHostEntry> entries = await KnownHostsFile.LoadAsync(knownHosts);
            SshAgentHopHost byCa = SshAgentHopHost.FromKnownHosts(entries, Host, Port)!;
            Assert.IsEmpty(byCa.HostKeys);
            Assert.HasCount(1, byCa.CertificateAuthorities);

            await using SshConnection admin = await SshConnection.ConnectAsync(Options());
            await using RemoteAgent remote = await RemoteAgent.StartAsync(admin);
            using var key = InMemorySshSigner.GenerateEd25519();
            await remote.AuthorizeAsync(key.PublicKey);
            await using SshAgentClient adder = await remote.OpenClientAsync();

            // 按 CA 验主机：协商时证书算法排在前面，服务端出示的是主机证书，会话声明交给 agent 的也是它。
            SshConnectionOptions viaCertificate = Options() with
            {
                HostKeyPolicy = new KnownHostsPolicy(knownHosts) { UnknownHost = UnknownHostBehavior.Reject },
            };

            await adder.AddIdentityAsync(key, "vela-rd", new SshAgentKeyConstraints { AllowedHops = [new SshAgentHop(byCa)] });
            await using (SshAgentClient agent = await remote.OpenClientAsync())
            {
                Assert.AreEqual("publickey", await LoginWithAgentAsync(agent, viaCertificate), "CA 签的主机证书、principals 里有这个名字");
            }

            await adder.AddIdentityAsync(key, "vela-rd", new SshAgentKeyConstraints
            {
                AllowedHops = [new SshAgentHop(new SshAgentHopHost("not-a-principal", [], byCa.CertificateAuthorities))],
            });
            await using (SshAgentClient agent = await remote.OpenClientAsync())
            {
                Assert.AreEqual("password", await LoginWithAgentAsync(agent, viaCertificate), "名字不在证书的 principals 里：agent 拒签，退到口令");
            }
        }
        finally
        {
            File.Delete(knownHosts);
        }
    }
}
