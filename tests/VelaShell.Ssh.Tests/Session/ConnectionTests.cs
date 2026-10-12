// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 被测规格: velashell-docs/zh/ssh/design/architecture.md §6.2;velashell-docs/zh/ssh/spec/05-connection.md §6.3;velashell-docs/zh/ssh/spec/08-failures.md
//
// 这里测的是**面向使用者的那一层**：一个 SshConnectionOptions 进去，
// 一条能用的连接出来。中间的拨号 → 版本交换 → 密钥交换 → 裁决 → 认证
// 都不该再由调用方自己拼。

using System.Text;
using VelaShell.Ssh.Auth;
using VelaShell.Ssh.Channels;
using VelaShell.Ssh.Diagnostics;
using VelaShell.Ssh.HostKeys;
using VelaShell.Ssh.Protocol;
using VelaShell.Ssh.Session;
using VelaShell.Ssh.Tests.TestKit;
using VelaShell.Ssh.Transport;

namespace VelaShell.Ssh.Tests.Session;

[TestClass]
[TestCategory("Session")]
public sealed class ConnectionTests
{
    /// <summary>把整台测试服务端跑起来，并给出一个指向它的拨号器。</summary>
    private sealed class FakeServer : IAsyncDisposable
    {
        private readonly CancellationTokenSource _cts = new(TimeSpan.FromSeconds(30));
        private readonly List<Task> _running = [];
        private readonly List<TestChannelServer> _channelServers = [];
        private readonly List<TestSshServer> _servers = [];
        private readonly TestChannelScript _script;
        private readonly TestAuthPolicy _authPolicy;

        private readonly TestSshServerOptions? _serverOptions;

        public FakeServer(TestChannelScript? script = null, TestAuthPolicy? authPolicy = null, TestSshServerOptions? serverOptions = null)
        {
            _script = script ?? new TestChannelScript();
            _authPolicy = authPolicy ?? new TestAuthPolicy { AcceptPassword = "hunter2" };
            _serverOptions = serverOptions;
        }

        /// <summary>服务端出示的主机公钥（第一条连接建立之后才有）。</summary>
        public SshPublicKey? HostKey { get; private set; }

        /// <summary>最近一条连接的认证侧观察（走到认证之后才有）。</summary>
        public TestAuthObservation? AuthObservation { get; private set; }

        /// <summary>最近一条连接的服务端（握手侧的观察在它上面）。</summary>
        public TestSshServer? LastServer => _servers.Count == 0 ? null : _servers[^1];

        public ISshTransportDialer CreateDialer() => new Dialer(this);

        private sealed class Dialer(FakeServer owner) : ISshTransportDialer
        {
            public ValueTask<Stream> DialAsync(
                SshDialTarget target, CancellationToken cancellationToken)
            {
                (InMemoryDuplexStream client, InMemoryDuplexStream server) = InMemoryTransport.CreatePair();
                owner.StartServerSide(server);
                return ValueTask.FromResult<Stream>(client);
            }
        }

        private void StartServerSide(InMemoryDuplexStream serverStream)
        {
            TestSshServer server = new(serverStream, _serverOptions);
            _servers.Add(server);

            _running.Add(Task.Run(async () =>
            {
                TestSshServerHandshake handshake = await server.HandshakeAsync(_cts.Token);
                HostKey = SshPublicKey.Decode(handshake.HostKeyBlob);

                TestAuthServer auth = new(server.Transport, handshake.ExchangeHash, _authPolicy) { SshServer = server };
                AuthObservation = auth.Observation;
                await auth.RunAsync(_cts.Token);

                TestChannelServer channels = new(server.Transport, _script);
                lock (_channelServers)
                {
                    _channelServers.Add(channels);
                }
                await channels.RunAsync(_cts.Token);
            }));
        }

        private bool _disposed;

        /// <summary>
        /// 幂等 —— 有的用例要**在中途**把服务端拆掉（验掉线），
        /// 而 <c>await using</c> 在作用域结束时还会再拆一次。
        /// </summary>
        public async ValueTask DisposeAsync()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;

            await _cts.CancelAsync();

            foreach (Task task in _running)
            {
                try
                {
                    await task;
                }
                catch (Exception)
                {
                    // 收尾时被取消是预期的。
                }
            }

            foreach (TestChannelServer channels in _channelServers)
            {
                channels.Dispose();
            }

            foreach (TestSshServer server in _servers)
            {
                await server.DisposeAsync();
            }

            _cts.Dispose();
        }
    }

    // ------------------------------------------------------------ 目标串解析

    [TestMethod]
    public void 目标串的各种写法()
    {
        SshConnectionOptions full = new("root@10.0.0.1:2222");
        Assert.AreEqual("root", full.UserName);
        Assert.AreEqual("10.0.0.1", full.Host);
        Assert.AreEqual(2222, full.Port);

        SshConnectionOptions noPort = new("joe@example.com");
        Assert.AreEqual("joe", noPort.UserName);
        Assert.AreEqual("example.com", noPort.Host);
        Assert.AreEqual(22, noPort.Port);

        // IPv6 带端口必须写方括号 —— 不然冒号分不清是地址还是端口。
        SshConnectionOptions ipv6 = new("joe@[fe80::1]:2222");
        Assert.AreEqual("fe80::1", ipv6.Host);
        Assert.AreEqual(2222, ipv6.Port);

        SshConnectionOptions ipv6NoPort = new("joe@[::1]");
        Assert.AreEqual("::1", ipv6NoPort.Host);
        Assert.AreEqual(22, ipv6NoPort.Port);

        // 不带方括号的 IPv6 整段是地址：最后一段全是数字也不当端口（#611）。
        SshConnectionOptions bare = new("tzraeq@2001:db8:0:1::10:20");
        Assert.AreEqual("tzraeq", bare.UserName);
        Assert.AreEqual("2001:db8:0:1::10:20", bare.Host);
        Assert.AreEqual(22, bare.Port);

        SshConnectionOptions bareLetters = new("joe@fe80::abcd");
        Assert.AreEqual("fe80::abcd", bareLetters.Host);
        Assert.AreEqual(22, bareLetters.Port);
    }

    [TestMethod]
    public void 不合法的目标串被当场拒绝()
    {
        Assert.ThrowsExactly<ArgumentException>(() => new SshConnectionOptions("joe@host:不是数字"));
        Assert.ThrowsExactly<ArgumentException>(() => new SshConnectionOptions("joe@host:0"));
        Assert.ThrowsExactly<ArgumentException>(() => new SshConnectionOptions("joe@host:70000"));
        Assert.ThrowsExactly<ArgumentException>(() => new SshConnectionOptions("@host"));
        Assert.ThrowsExactly<ArgumentException>(() => new SshConnectionOptions("joe@[::1"));
    }

    [TestMethod]
    public void 默认主机密钥策略不是接受任何密钥()
    {
        SshConnectionOptions options = new("joe@example.com");

        // 默认放行等于关掉中间人防护 —— 而 SSH 的全部安全性
        // 都建立在「你确实连到了你以为的那台机器」之上。
        Assert.IsInstanceOfType<KnownHostsPolicy>(options.HostKeyPolicy);
    }

    // ------------------------------------------------------------ 主线

    [TestMethod]
    public async Task 一步连上并跑一条命令()
    {
        await using FakeServer server = new(new TestChannelScript
        {
            StandardOutput = Encoding.UTF8.GetBytes("Linux velashell 6.1\n"),
            ExitCode = 0,
        });

        SshConnectionOptions options = new("joe@test.invalid:22")
        {
            Dialer = server.CreateDialer(),
            HostKeyPolicy = new DangerousAcceptAnyHostKeyPolicy(),
            Credentials = [new PasswordCredential("hunter2")],
        };

        await using SshConnection connection = await SshConnection.ConnectAsync(options);

        Assert.IsTrue(connection.IsAlive);
        Assert.AreEqual("joe@test.invalid:22", connection.Description,
            "描述里带上端口 —— 日志里「连的是哪台」要一眼看全");
        Assert.IsNotNull(connection.HostKey, "连上之后要能拿到服务端的主机密钥");

        // 协商成功的结果也要交出去 —— 终端产品要在状态栏上显示它，
        // 排障时第一句话也是「这条连接到底谈成了什么」（架构原则 4）。
        Assert.IsFalse(
            string.IsNullOrEmpty(connection.Algorithms.KeyExchange),
            "密钥交换算法名不该是空的");
        Assert.AreEqual(
            SshAlgorithmNames.None, connection.Algorithms.CompressionClientToServer,
            "默认不开压缩");

        SshCommandResult output = await connection.RunAsync("uname -a");

        Assert.AreEqual("Linux velashell 6.1\n", output.StandardOutput);
        Assert.AreEqual(0, output.ExitCode);
        Assert.IsTrue(output.IsSuccess);
    }

    /// <summary>
    /// 等 exec 应答时取消：通道在本端收尾、CLOSE 已发，对端随后才回那条 exec 的应答。
    /// 那是合法的在途报文 —— 连接不能因此判 FIFO 失步，同一连接上的其它通道照常可用。
    /// </summary>
    [TestMethod]
    public async Task 等命令应答时取消之后迟到的应答不会打断整条连接()
    {
        await using FakeServer server = new(new TestChannelScript
        {
            StandardOutput = "ok\n"u8.ToArray(),
            ExitCode = 0,
            DelayFirstRequestReply = TimeSpan.FromMilliseconds(500),
        });

        SshConnectionOptions options = new("joe@test.invalid")
        {
            Dialer = server.CreateDialer(),
            HostKeyPolicy = new DangerousAcceptAnyHostKeyPolicy(),
            Credentials = [new PasswordCredential("hunter2")],
        };

        await using SshConnection connection = await SshConnection.ConnectAsync(options);

        using (CancellationTokenSource probeTimeout = new(TimeSpan.FromMilliseconds(100)))
        {
            await Assert.ThrowsAsync<OperationCanceledException>(
                async () => await connection.RunAsync("慢吞吞的探测", cancellationToken: probeTimeout.Token));
        }

        // 等那条迟到的应答（以及对端对我们 CLOSE 的回应）落地。
        await Task.Delay(TimeSpan.FromMilliseconds(800));

        Assert.IsTrue(connection.IsAlive, "迟到的应答被当成 FIFO 失步，整条连接断了");

        SshCommandResult output = await connection.RunAsync("echo ok");
        Assert.AreEqual("ok\n", output.StandardOutput);
    }

    [TestMethod]
    public async Task 命令失败时异常里带着stderr()
    {
        await using FakeServer server = new(new TestChannelScript
        {
            StandardError = Encoding.UTF8.GetBytes("bash: 没有那个文件或目录\n"),
            ExitCode = 127,
        });

        SshConnectionOptions options = new("joe@test.invalid")
        {
            Dialer = server.CreateDialer(),
            HostKeyPolicy = new DangerousAcceptAnyHostKeyPolicy(),
            Credentials = [new PasswordCredential("hunter2")],
        };

        await using SshConnection connection = await SshConnection.ConnectAsync(options);
        SshCommandResult output = await connection.RunAsync("不存在的命令");

        Assert.AreEqual(127, output.ExitCode);
        Assert.IsFalse(output.IsSuccess);

        SshCommandFailedException error = Assert.ThrowsExactly<SshCommandFailedException>(
            () => output.EnsureSuccess("不存在的命令"));

        // 「命令失败了」而不说它抱怨了什么，等于让调用方再跑一遍去看。
        Assert.Contains("没有那个文件或目录", error.Message);
        Assert.Contains("退出码 127", error.Message);
    }

    [TestMethod]
    [DataRow(new byte[] { 20, 1, 2, 3 }, DisplayName = "KEXINIT 连 cookie 都不完整")]
    [DataRow(new byte[] { 20, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0xFF, 0xFF }, DisplayName = "KEXINIT 的名单长度被截断")]
    [DataRow(new byte[0], DisplayName = "空载荷的帧")]
    public async Task 握手时对端发来解不开的东西_报SshProtocolException而不是内部异常(byte[] kexInit)
    {
        // 曾经让解析层 internal 的 SshWireFormatException / SshFrameFormatException 原样漏出 ConnectAsync ——
        // 调用方 catch (SshException) 接不住。会话期间有统一的归类，握手期没有。
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(10));
        SshConnectionOptions options = new("joe@test.invalid")
        {
            Dialer = new RawKexInitDialer(kexInit, cts.Token),
            HostKeyPolicy = new DangerousAcceptAnyHostKeyPolicy(),
            Credentials = [new PasswordCredential("x")],
        };

        SshProtocolException error = await Assert.ThrowsExactlyAsync<SshProtocolException>(
            async () => await SshConnection.ConnectAsync(options, cts.Token));

        Assert.AreEqual(SshFailureReason.ProtocolError, error.Reason);
        Assert.AreEqual(SshPhase.KeyExchange, error.Phase);
    }

    /// <summary>服务端在首次交换里发一段写好的「KEXINIT」。</summary>
    private sealed class RawKexInitDialer(byte[] kexInit, CancellationToken cancellationToken) : ISshTransportDialer
    {
        public ValueTask<Stream> DialAsync(SshDialTarget target, CancellationToken ct)
        {
            (InMemoryDuplexStream client, InMemoryDuplexStream server) = InMemoryTransport.CreatePair();
            TestSshServer fake = new(server, new TestSshServerOptions { RawInitialKexInit = kexInit });
            _ = Task.Run(async () =>
            {
                try
                {
                    await fake.HandshakeAsync(cancellationToken);
                }
                catch (Exception)
                {
                    // 客户端拒了这次握手 —— 服务端这边失败是预期的。
                }
            }, CancellationToken.None);   // 服务端跟着测试的令牌走，不跟这一次拨号的
            return ValueTask.FromResult<Stream>(client);
        }
    }

    [TestMethod]
    public async Task 主机密钥被策略拒绝时连不上()
    {
        await using FakeServer server = new();

        SshConnectionOptions options = new("joe@test.invalid")
        {
            Dialer = server.CreateDialer(),
            HostKeyPolicy = new PinnedFingerprintHostKeyPolicy(["SHA256:对不上的指纹"]),
            Credentials = [new PasswordCredential("hunter2")],
        };

        SshException error = await Assert.ThrowsExactlyAsync<SshConnectException>(
            async () => await SshConnection.ConnectAsync(options));

        Assert.Contains("不在允许列表里", error.Message);
    }

    [TestMethod]
    public async Task 认证失败时异常里带着逐条记录()
    {
        await using FakeServer server = new(
            null, new TestAuthPolicy { AcceptPassword = "正确的" });

        SshConnectionOptions options = new("joe@test.invalid")
        {
            Dialer = server.CreateDialer(),
            HostKeyPolicy = new DangerousAcceptAnyHostKeyPolicy(),
            Credentials = [new PasswordCredential("错的")],
        };

        SshAuthenticationException error = await Assert.ThrowsExactlyAsync<SshAuthenticationException>(
            async () => await SshConnection.ConnectAsync(options));

        Assert.IsNotEmpty(error.Attempts);
        Assert.Contains("password", error.DescribeAttempts());
    }

    [TestMethod]
    public async Task 横幅被送到回调()
    {
        List<string> banners = [];

        await using FakeServer server = new(
            null,
            new TestAuthPolicy
            {
                AcceptPassword = "hunter2",
                Banners = ["未经授权的访问将被记录。"],
            });

        SshConnectionOptions options = new("joe@test.invalid")
        {
            Dialer = server.CreateDialer(),
            HostKeyPolicy = new DangerousAcceptAnyHostKeyPolicy(),
            Credentials = [new PasswordCredential("hunter2")],
            BannerHandler = (text, _) =>
            {
                banners.Add(text);
                return ValueTask.CompletedTask;
            },
        };

        await using SshConnection connection = await SshConnection.ConnectAsync(options);

        Assert.AreSequenceEqual(["未经授权的访问将被记录。"], banners);
    }

    /// <summary>连上之后交出对端的标识串、成功的认证方法、server-sig-algs 与各阶段耗时（可观测性缺口 2）。</summary>
    [TestMethod]
    public async Task 连接交出对端版本认证方法与各阶段耗时()
    {
        await using FakeServer server = new(
            null,
            new TestAuthPolicy
            {
                AcceptPassword = "hunter2",
                ServerSignatureAlgorithms = ["ssh-ed25519", "rsa-sha2-512"],
            });

        SshConnectionOptions options = new("joe@test.invalid")
        {
            Dialer = server.CreateDialer(),
            HostKeyPolicy = new DangerousAcceptAnyHostKeyPolicy(),
            Credentials = [new PasswordCredential("hunter2")],
        };

        await using SshConnection connection = await SshConnection.ConnectAsync(options);

        Assert.StartsWith("SSH-2.0-", connection.PeerVersion);
        Assert.AreEqual("password", connection.AuthenticationMethod);
        Assert.AreSequenceEqual(["ssh-ed25519", "rsa-sha2-512"], [.. connection.ServerSignatureAlgorithms]);

        SshConnectTimings timings = connection.ConnectTimings;
        Assert.IsTrue(timings.Dialing >= TimeSpan.Zero && timings.VersionExchange >= TimeSpan.Zero
                      && timings.KeyExchange > TimeSpan.Zero && timings.Authentication > TimeSpan.Zero, timings.ToString());
        Assert.AreEqual(timings.Dialing + timings.VersionExchange + timings.KeyExchange + timings.Authentication, timings.Total);
    }

    /// <summary>
    /// 〔spec 05 §八〕标着 always_display 的 SSH_MSG_DEBUG 交给回调（认证期间与连上之后都交），清洗过；
    /// 不带 always_display 的不交。曾经一律丢掉。
    /// </summary>
    [TestMethod]
    public async Task 标着always_display的调试消息交给回调()
    {
        List<string> shown = [];
        TaskCompletionSource afterLogin = new(TaskCreationOptions.RunContinuationsAsynchronously);

        await using FakeServer server = new(
            new TestChannelScript { DebugMessagesOnStart = [(false, "仅排错"), (true, "连上之后：\u001b[31m注意")] },
            new TestAuthPolicy
            {
                AcceptPassword = "hunter2",
                DebugMessages = [(true, "这把钥不许转发端口。"), (false, "认证细节")],
            });

        SshConnectionOptions options = new("joe@test.invalid")
        {
            Dialer = server.CreateDialer(),
            HostKeyPolicy = new DangerousAcceptAnyHostKeyPolicy(),
            Credentials = [new PasswordCredential("hunter2")],
            DebugMessageHandler = (text, _) =>
            {
                lock (shown)
                {
                    shown.Add(text);
                    if (shown.Count == 2)
                    {
                        afterLogin.TrySetResult();
                    }
                }
                return ValueTask.CompletedTask;
            },
        };

        await using SshConnection connection = await SshConnection.ConnectAsync(options);
        await afterLogin.Task.WaitAsync(TimeSpan.FromSeconds(10));

        lock (shown)
        {
            Assert.AreEqual("这把钥不许转发端口。", shown[0], "认证期间的");
            Assert.StartsWith("连上之后：", shown[1]);
            Assert.DoesNotContain("\u001b", shown[1], "对端文本要清洗");
            Assert.HasCount(2, shown, "不带 always_display 的不交");
        }
    }

    [TestMethod]
    public async Task 连不上的目标给出带原因的异常()
    {
        SshConnectionOptions options = new("joe@127.0.0.1:1")
        {
            // 真的去连一个几乎肯定没人听的端口。
            HostKeyPolicy = new DangerousAcceptAnyHostKeyPolicy(),
            ConnectTimeout = TimeSpan.FromSeconds(5),
        };

        SshConnectException error = await Assert.ThrowsExactlyAsync<SshConnectException>(
            async () => await SshConnection.ConnectAsync(options));

        // 「连不上」三个字对用户没有任何帮助 —— 要说清是 DNS、拒绝、还是超时。
        //
        // 端口 1 上多半没有在听的服务（→ 拒绝），但在被防火墙静默丢包的
        // 网络里会是超时 —— 两者都该有**明确的** TCP 层原因码，
        // 而不是笼统的 Timeout。
        Assert.IsTrue(
            error.Reason is SshFailureReason.TcpRefused or SshFailureReason.TcpTimeout
                or SshFailureReason.TcpUnreachable,
            $"原因码应当是可判定的 TCP 层原因，实际是 {error.Reason}");
        Assert.AreEqual(SshPhase.Dialing, error.Phase);
    }

    [TestMethod]
    public async Task DNS查不到时说的是DNS()
    {
        SshConnectionOptions options = new("joe@这个名字一定查不到.invalid")
        {
            HostKeyPolicy = new DangerousAcceptAnyHostKeyPolicy(),
            ConnectTimeout = TimeSpan.FromSeconds(10),
        };

        SshConnectException error = await Assert.ThrowsExactlyAsync<SshConnectException>(
            async () => await SshConnection.ConnectAsync(options));

        Assert.AreEqual(SshFailureReason.DnsFailure, error.Reason);
        Assert.Contains("DNS", error.Message);
    }

    // ------------------------------------------------------------ 保活

    /// <summary>一个「要想一会儿」的策略：模拟用户盯着指纹看了一阵才点「永久信任」。</summary>
    private sealed class SlowTrustPolicy(TimeSpan thinking) : IHostKeyPolicy
    {
        public bool PersistTokenWasCancelled { get; private set; } = true;

        public int Persisted { get; private set; }

        public async ValueTask<SshHostKeyVerdict> EvaluateAsync(
            SshHostKeyContext context, CancellationToken cancellationToken = default)
        {
            await Task.Delay(thinking, cancellationToken);
            return SshHostKeyVerdict.AcceptAndPersist;
        }

        public ValueTask PersistAsync(SshHostKeyContext context, CancellationToken cancellationToken = default)
        {
            PersistTokenWasCancelled = cancellationToken.IsCancellationRequested;
            Persisted++;
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>
    /// 主机密钥裁决（等人）不算进连接超时：用户想得比连接超时久，连接照样成功，
    /// 「永久信任」也照样存下来。
    /// </summary>
    [TestMethod]
    public async Task 主机密钥裁决期间连接计时器停表()
    {
        await using FakeServer server = new();
        SlowTrustPolicy policy = new(TimeSpan.FromMilliseconds(1500));

        SshConnectionOptions options = new("joe@test.invalid")
        {
            Dialer = server.CreateDialer(),
            HostKeyPolicy = policy,
            Credentials = [new PasswordCredential("hunter2")],
            ConnectTimeout = TimeSpan.FromMilliseconds(700),
        };

        await using SshConnection connection = await SshConnection.ConnectAsync(options);

        Assert.IsTrue(connection.IsAlive);
        Assert.AreEqual(1, policy.Persisted);
        Assert.IsFalse(policy.PersistTokenWasCancelled, "持久化拿到的是已取消的令牌 —— 「永久信任」存不下来");
        Assert.IsNull(connection.HostKeyPersistFailure);
    }

    // ------------------------------------------------------------ 建连时调用方回调自己的异常

    /// <summary>按给定的方式失败的策略：裁决时抛，或者记的时候抛；都不给时「信任并记住」。</summary>
    private sealed class FailingPolicy(Exception? onEvaluate = null, Exception? onPersist = null) : IHostKeyPolicy
    {
        public ValueTask<SshHostKeyVerdict> EvaluateAsync(
            SshHostKeyContext context, CancellationToken cancellationToken = default) =>
            onEvaluate is null
                ? ValueTask.FromResult(SshHostKeyVerdict.AcceptAndPersist)
                : ValueTask.FromException<SshHostKeyVerdict>(onEvaluate);

        public ValueTask PersistAsync(SshHostKeyContext context, CancellationToken cancellationToken = default) =>
            onPersist is null ? ValueTask.CompletedTask : ValueTask.FromException(onPersist);
    }

    private static SshConnectionOptions Options(FakeServer server, IHostKeyPolicy policy) => new("joe@test.invalid")
    {
        Dialer = server.CreateDialer(),
        HostKeyPolicy = policy,
        Credentials = [new PasswordCredential("hunter2")],
    };

    /// <summary>
    /// 策略是调用方的代码：它自己抛的 IO 错（宿主的信任库连不上）原样交还。
    /// 曾经被建连路上那道 catch 改写成「对端关闭了连接」，还判为可重试 —— 重试只会再失败一次。
    /// </summary>
    [TestMethod]
    public async Task 策略裁决时自己抛的IO错原样交还而不是记成对端断开()
    {
        await using FakeServer server = new();
        IOException storeDown = new("信任库连不上");

        IOException error = await Assert.ThrowsExactlyAsync<IOException>(
            async () => await SshConnection.ConnectAsync(Options(server, new FailingPolicy(onEvaluate: storeDown))));

        Assert.AreSame(storeDown, error);
    }

    /// <summary>横幅回调同理（velashell-docs/zh/ssh/spec/04 §3.4：照实抛出）。</summary>
    [TestMethod]
    public async Task 横幅回调自己抛的IO错原样交还()
    {
        await using FakeServer server = new(authPolicy: new TestAuthPolicy { AcceptPassword = "hunter2", Banners = ["欢迎"] });
        IOException logFull = new("横幅日志写不进去");

        SshConnectionOptions options = Options(server, new DangerousAcceptAnyHostKeyPolicy()) with
        {
            BannerHandler = (_, _) => ValueTask.FromException(logFull),
        };

        IOException error = await Assert.ThrowsExactlyAsync<IOException>(
            async () => await SshConnection.ConnectAsync(options));

        Assert.AreSame(logFull, error);
    }

    /// <summary>
    /// 〔spec/03 §5.4〕信任已经给了、只是没记下来：这次连接照常进行，原因记在连接上（与 OpenSSH 一样只是提醒）。
    /// 曾经整条连接失败，用户点了「信任」换来一句「对端关闭了连接」。
    /// </summary>
    [TestMethod]
    public async Task 记主机密钥失败不影响这次连接()
    {
        await using FakeServer server = new();
        IOException diskFull = new("磁盘满了");

        await using SshConnection connection = await SshConnection.ConnectAsync(
            Options(server, new FailingPolicy(onPersist: diskFull)));

        Assert.IsTrue(connection.IsAlive);
        Assert.AreSame(diskFull, connection.HostKeyPersistFailure);
    }

    /// <summary>本库的 known_hosts 写不进去时同样放行，失败是带原因码的 <see cref="SshConnectException"/>。</summary>
    [TestMethod]
    public async Task Known_hosts写不进去时连接照常并记下原因()
    {
        await using FakeServer server = new();

        // 拿一个目录当 known_hosts：读的时候当它不存在（没见过这台主机），写的时候失败。
        DirectoryInfo directory = Directory.CreateTempSubdirectory("kh-");
        try
        {
            KnownHostsPolicy policy = new(directory.FullName, (_, _) => ValueTask.FromResult(true));

            await using SshConnection connection = await SshConnection.ConnectAsync(Options(server, policy));

            Assert.IsTrue(connection.IsAlive);
            SshConnectException failure = Assert.IsInstanceOfType<SshConnectException>(connection.HostKeyPersistFailure);
            Assert.AreEqual(SshFailureReason.HostKeyStoreFailed, failure.Reason);
            Assert.Contains(directory.FullName, failure.Message);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    /// <summary>策略有意不让记（主机名不能写进 known_hosts）不是「没记下来」：连接照样不放行。</summary>
    [TestMethod]
    public async Task 策略有意不让记时连接不放行()
    {
        await using FakeServer server = new();
        SshConnectException refusal = new(SshFailureReason.InvalidConfiguration, SshPhase.KeyExchange, "这个名字不能记");

        SshConnectException error = await Assert.ThrowsExactlyAsync<SshConnectException>(
            async () => await SshConnection.ConnectAsync(Options(server, new FailingPolicy(onPersist: refusal))));

        Assert.AreSame(refusal, error);
    }

    /// <summary>known_hosts 读不出来：没法判断认不认识这台主机，连接不放行，报存储失败、不可重试。</summary>
    [TestMethod]
    public async Task Known_hosts读不出来时连接不放行()
    {
        await using FakeServer server = new();
        string path = Path.Combine(Path.GetTempPath(), $"kh-{Guid.NewGuid():N}");
        File.WriteAllText(path, "");

        try
        {
            SshConnectException error;
            await using (FileStream exclusive = new(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                error = await Assert.ThrowsExactlyAsync<SshConnectException>(
                    async () => await SshConnection.ConnectAsync(Options(server, new KnownHostsPolicy(path))));
            }

            Assert.AreEqual(SshFailureReason.HostKeyStoreFailed, error.Reason);
            Assert.AreEqual(SshPhase.KeyExchange, error.Phase);
            Assert.IsFalse(error.IsRetryable);
        }
        finally
        {
            File.Delete(path);
        }
    }

    // ------------------------------------------------------------ 失败时告诉对端原因（DISCONNECT）

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (int i = 0; i < 300 && !condition(); i++)
        {
            await Task.Delay(10);
        }
    }

    /// <summary>认证方法用尽：服务端收到 DISCONNECT(NO_MORE_AUTH_METHODS_AVAILABLE)，日志里有原因。</summary>
    [TestMethod]
    public async Task 认证方法用尽时告诉服务端原因()
    {
        await using FakeServer server = new();
        SshConnectionOptions options = Options(server, new DangerousAcceptAnyHostKeyPolicy()) with
        {
            Credentials = [new PasswordCredential("wrong")],
        };

        await Assert.ThrowsExactlyAsync<SshAuthenticationException>(async () => await SshConnection.ConnectAsync(options));

        await WaitForAsync(() => server.AuthObservation?.ClientDisconnectReason is not null);
        Assert.AreEqual((uint)SshDisconnectReason.NoMoreAuthMethodsAvailable, server.AuthObservation?.ClientDisconnectReason);
    }

    /// <summary>
    /// 〔velashell-docs/zh/ssh/spec/03 §3.7.4〕密钥交换里服务端的公开值不合格：本端报 <c>ProtocolError</c>，
    /// 告诉服务端的原因码是 KEY_EXCHANGE_FAILED（3）—— 所有方法一律这样（曾经发 2）。
    /// </summary>
    [TestMethod]
    [DataRow("mlkem768nistp256-sha256")]
    [DataRow("mlkem1024nistp384-sha384")]
    [DataRow("mlkem768x25519-sha256")]
    [DataRow("ecdh-sha2-nistp256")]
    [DataRow("curve25519-sha256")]
    public async Task 服务端公开值不合格时告诉服务端KEY_EXCHANGE_FAILED(string kex)
    {
        await using FakeServer server = new(serverOptions: new TestSshServerOptions { MangleServerPublicValue = value => value[..^1] });
        SshConnectionOptions options = Options(server, new DangerousAcceptAnyHostKeyPolicy()) with
        {
            Algorithms = Ssh.Crypto.SshAlgorithmSet.Default with { KeyExchange = [kex] },
        };

        SshException error = await Assert.ThrowsAsync<SshException>(async () => await SshConnection.ConnectAsync(options));
        Assert.AreEqual(SshFailureReason.ProtocolError, error.Reason, error.Message);

        await WaitForAsync(() => server.LastServer?.ClientDisconnectReason is not null);
        Assert.AreEqual((uint)SshDisconnectReason.KeyExchangeFailed, server.LastServer?.ClientDisconnectReason);
    }

    /// <summary>主机密钥被拒：服务端收到 DISCONNECT(HOST_KEY_NOT_VERIFIABLE)。</summary>
    [TestMethod]
    public async Task 主机密钥被拒时告诉服务端原因()
    {
        await using FakeServer server = new();
        IHostKeyPolicy reject = new RejectAll();

        SshConnectException error = await Assert.ThrowsExactlyAsync<SshConnectException>(
            async () => await SshConnection.ConnectAsync(Options(server, reject)));
        Assert.AreEqual(SshFailureReason.HostKeyRejected, error.Reason);

        await WaitForAsync(() => server.LastServer?.ClientDisconnectReason is not null);
        Assert.AreEqual((uint)SshDisconnectReason.HostKeyNotVerifiable, server.LastServer?.ClientDisconnectReason);
    }

    private sealed class RejectAll : IHostKeyPolicy
    {
        public ValueTask<SshHostKeyVerdict> EvaluateAsync(SshHostKeyContext context, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(SshHostKeyVerdict.Reject("不认识。"));
    }

    // ------------------------------------------------------------ 认证期间的重协商

    /// <summary>
    /// 〔RFC 4253 §9〕认证期间服务端发起重协商（用户找动态码花了几分钟、服务端按时间 RekeyLimit 发起）：
    /// 就地做完，认证照常成功，之后的会话用新密钥。曾经 KEXINIT 被当成意外报文，连接以协议错误失败。
    /// </summary>
    [TestMethod]
    public async Task 认证期间服务端发起重协商时就地做完()
    {
        await using FakeServer server = new(
            new TestChannelScript { StandardOutput = Encoding.UTF8.GetBytes("ok\n"), ExitCode = 0 },
            new TestAuthPolicy { AcceptPassword = "hunter2", RekeyBeforeSuccess = true });

        await using SshConnection connection = await SshConnection.ConnectAsync(
            Options(server, new DangerousAcceptAnyHostKeyPolicy()));

        Assert.AreEqual(1, server.AuthObservation?.RekeysDuringAuth);
        SshCommandResult result = await connection.RunAsync("ok");
        Assert.AreEqual("ok\n", result.StandardOutput, "重协商之后的报文用新密钥照样收发");
    }

    // ------------------------------------------------------------ 前导行

    /// <summary>
    /// 标识串之前的前导行（法律声明）交给调用方。曾经收集了却从没有交出去的路（spec/02 §三写的回调不存在）。
    /// </summary>
    [TestMethod]
    public async Task 标识串之前的前导行交给回调()
    {
        await using FakeServer server = new(serverOptions: new TestSshServerOptions
        {
            PreAuthBanner = ["Authorized use only.", "All activity is monitored."],
        });
        List<string> received = [];

        SshConnectionOptions options = Options(server, new DangerousAcceptAnyHostKeyPolicy()) with
        {
            PreAuthBannerHandler = (lines, _) =>
            {
                received.AddRange(lines);
                return ValueTask.CompletedTask;
            },
        };

        await using SshConnection connection = await SshConnection.ConnectAsync(options);

        Assert.AreSequenceEqual(["Authorized use only.", "All activity is monitored."], [.. received]);
    }

    [TestMethod]
    public async Task 没有前导行时不调回调()
    {
        await using FakeServer server = new();
        int calls = 0;

        SshConnectionOptions options = Options(server, new DangerousAcceptAnyHostKeyPolicy()) with
        {
            PreAuthBannerHandler = (_, _) =>
            {
                calls++;
                return ValueTask.CompletedTask;
            },
        };

        await using SshConnection connection = await SshConnection.ConnectAsync(options);

        Assert.AreEqual(0, calls);
    }

    // ------------------------------------------------------------ 回调自己抛的取消

    /// <summary>
    /// 用户在口令框上点了「取消」，回调抛 <see cref="OperationCanceledException"/>：调用方没取消、认证计时器也没到点，
    /// 这是「不连了」—— 报 <see cref="SshFailureReason.Aborted"/>，并告诉服务端是用户取消的。
    /// 曾经报成「认证超时（限 120 秒）」，宿主只好在回调里另记一笔、失败之后再认回来。
    /// </summary>
    [TestMethod]
    public async Task 认证回调自己抛的取消报成使用者取消而不是超时()
    {
        await using FakeServer server = new();
        SshConnectionOptions options = Options(server, new DangerousAcceptAnyHostKeyPolicy()) with
        {
            Credentials = [new PasswordCredential(_ => throw new OperationCanceledException())],
        };

        SshConnectException error = await Assert.ThrowsExactlyAsync<SshConnectException>(
            async () => await SshConnection.ConnectAsync(options));

        Assert.AreEqual(SshFailureReason.Aborted, error.Reason);
        Assert.AreEqual(SshPhase.Authenticating, error.Phase);
        Assert.IsFalse(error.IsRetryable);

        // DISCONNECT 在出口之前就发出去了，服务端那边读到它要再等一会儿。
        for (int i = 0; i < 200 && server.AuthObservation?.ClientDisconnectReason is null; i++)
        {
            await Task.Delay(10);
        }
        Assert.AreEqual((uint)SshDisconnectReason.AuthCancelledByUser, server.AuthObservation?.ClientDisconnectReason);
    }

    /// <summary>
    /// 主机密钥策略同理：不限时的裁决里冒出取消，是用户关掉了询问框。
    /// 曾经报的是「主机密钥裁决超时（-00:00:00.001）」。
    /// </summary>
    [TestMethod]
    public async Task 主机密钥策略自己抛的取消报成使用者取消()
    {
        await using FakeServer server = new();

        SshConnectException error = await Assert.ThrowsExactlyAsync<SshConnectException>(
            async () => await SshConnection.ConnectAsync(
                Options(server, new FailingPolicy(onEvaluate: new OperationCanceledException()))));

        Assert.AreEqual(SshFailureReason.Aborted, error.Reason);
        Assert.AreEqual(SshPhase.KeyExchange, error.Phase);
        Assert.DoesNotContain("超时", error.Message);
    }

    /// <summary>裁决之外（记的时候）冒出的取消走建连出口那一道判断：连接计时器没到点，同样是使用者取消。</summary>
    [TestMethod]
    public async Task 记主机密钥时自己抛的取消报成使用者取消()
    {
        await using FakeServer server = new();

        SshConnectException error = await Assert.ThrowsExactlyAsync<SshConnectException>(
            async () => await SshConnection.ConnectAsync(
                Options(server, new FailingPolicy(onPersist: new OperationCanceledException()))));

        Assert.AreEqual(SshFailureReason.Aborted, error.Reason);
        Assert.AreEqual(SshPhase.KeyExchange, error.Phase);
    }

    /// <summary>调用方自己取消的，照旧原样当取消交还 —— 不是「使用者在框上点了取消」那一种。</summary>
    [TestMethod]
    public async Task 调用方取消时照旧抛取消()
    {
        await using FakeServer server = new();
        using CancellationTokenSource cts = new();
        SshConnectionOptions options = Options(server, new DangerousAcceptAnyHostKeyPolicy()) with
        {
            Credentials = [new PasswordCredential(async ct =>
            {
                await cts.CancelAsync();
                ct.ThrowIfCancellationRequested();
                return "hunter2";
            })],
        };

        await Assert.ThrowsAsync<OperationCanceledException>(
            async () => await SshConnection.ConnectAsync(options, cts.Token));
    }

    /// <summary>记下被问了几次；问到就放行。</summary>
    private sealed class CountingTrustPolicy : IHostKeyPolicy
    {
        public int Asked { get; private set; }

        public ValueTask<SshHostKeyVerdict> EvaluateAsync(
            SshHostKeyContext context, CancellationToken cancellationToken = default)
        {
            Asked++;
            return ValueTask.FromResult(SshHostKeyVerdict.Accept);
        }
    }

    /// <summary>拨通之后把手动时钟往前拨，但不执行到点的回调 —— 摆出「已经到点、回调还没轮到执行」的那一刻。</summary>
    private sealed class ClockAdvancingDialer(ISshTransportDialer inner, ManualTimeProvider clock, TimeSpan by)
        : ISshTransportDialer
    {
        public async ValueTask<Stream> DialAsync(SshDialTarget target, CancellationToken cancellationToken = default)
        {
            Stream stream = await inner.DialAsync(target, cancellationToken);
            clock.AdvanceWithoutRunningCallbacks(by);
            return stream;
        }
    }

    /// <summary>
    /// 连接超时在裁决之前就用完了，只是到点的回调还没轮到执行：不该再拿指纹去问用户 ——
    /// 用户点完「信任」，这一轮照样超时。报的是密钥交换这一步超时。
    /// </summary>
    [TestMethod]
    public async Task 裁决之前连接超时已经用完就不再去问()
    {
        await using FakeServer server = new();
        ManualTimeProvider clock = new();
        CountingTrustPolicy policy = new();

        SshConnectionOptions options = new("joe@test.invalid")
        {
            Dialer = new ClockAdvancingDialer(server.CreateDialer(), clock, TimeSpan.FromSeconds(11)),
            HostKeyPolicy = policy,
            Credentials = [new PasswordCredential("hunter2")],
            ConnectTimeout = TimeSpan.FromSeconds(10),
            TimeProvider = clock,
        };

        SshConnectException error = await Assert.ThrowsExactlyAsync<SshConnectException>(
            async () => await SshConnection.ConnectAsync(options));

        Assert.AreEqual(0, policy.Asked, "已经超时的连接还在拿指纹问用户");
        Assert.AreEqual(SshFailureReason.Timeout, error.Reason);
        Assert.AreEqual(SshPhase.KeyExchange, error.Phase);
    }

    /// <summary>上一条的对照：裁决之前还剩时间，照常去问、照常连上。</summary>
    [TestMethod]
    public async Task 裁决之前连接超时还有剩余就照常去问()
    {
        await using FakeServer server = new();
        ManualTimeProvider clock = new();
        CountingTrustPolicy policy = new();

        SshConnectionOptions options = new("joe@test.invalid")
        {
            Dialer = new ClockAdvancingDialer(server.CreateDialer(), clock, TimeSpan.FromSeconds(9)),
            HostKeyPolicy = policy,
            Credentials = [new PasswordCredential("hunter2")],
            ConnectTimeout = TimeSpan.FromSeconds(10),
            TimeProvider = clock,
        };

        await using SshConnection connection = await SshConnection.ConnectAsync(options);

        Assert.AreEqual(1, policy.Asked);
        Assert.IsTrue(connection.IsAlive);
    }

    [TestMethod]
    public async Task 保活探测在链路闲下来之后发出()
    {
        await using FakeServer server = new();

        SshConnectionOptions options = new("joe@test.invalid")
        {
            Dialer = server.CreateDialer(),
            HostKeyPolicy = new DangerousAcceptAnyHostKeyPolicy(),
            Credentials = [new PasswordCredential("hunter2")],
            KeepAlive = new SshKeepAlivePolicy(TimeSpan.FromMilliseconds(100)),
        };

        await using SshConnection connection = await SshConnection.ConnectAsync(options);

        // 闲着 —— 保活该自己发出去。
        await Task.Delay(500);

        Assert.IsTrue(connection.IsAlive, "服务端会回 REQUEST_FAILURE，那也算有应答");
    }

    [TestMethod]
    public async Task 链路忙的时候不发保活()
    {
        await using FakeServer server = new(new TestChannelScript
        {
            StandardOutput = "x"u8.ToArray(),
            ExitCode = 0,
        });

        SshConnectionOptions options = new("joe@test.invalid")
        {
            Dialer = server.CreateDialer(),
            HostKeyPolicy = new DangerousAcceptAnyHostKeyPolicy(),
            Credentials = [new PasswordCredential("hunter2")],
            KeepAlive = new SshKeepAlivePolicy(TimeSpan.FromMilliseconds(200)),
        };

        await using SshConnection connection = await SshConnection.ConnectAsync(options);

        // 持续有流量 —— 每一条命令都会让「上次收到报文」的时刻刷新。
        for (int i = 0; i < 8; i++)
        {
            await connection.RunAsync($"echo {i}");
            await Task.Delay(50);
        }

        // 计时基准是「上次收到任何报文」而不是固定周期，
        // 所以一直忙的链路上一次保活都不该发。
        Assert.IsTrue(connection.IsAlive);
    }

    // ------------------------------------------------------------ 掉线信号

    /// <summary>连接活着的时候，<c>Disconnected</c> 不能是已取消的。</summary>
    /// <remarks>
    /// 听起来是废话，但它挡住的是一类很实在的错：令牌如果在构造时就被取消，
    /// 或者与 <c>_lifetime</c> 搞混，上层的读循环会在连上的瞬间就退出，
    /// 表现成「一连就断」。
    /// </remarks>
    [TestMethod]
    public async Task 连着的时候掉线令牌没有被取消()
    {
        await using FakeServer server = new();

        SshConnectionOptions options = new("joe@test.invalid")
        {
            Dialer = server.CreateDialer(),
            HostKeyPolicy = new DangerousAcceptAnyHostKeyPolicy(),
            Credentials = [new PasswordCredential("hunter2")],
        };

        await using SshConnection connection = await SshConnection.ConnectAsync(options);

        Assert.IsTrue(connection.IsAlive);
        Assert.IsFalse(connection.Disconnected.IsCancellationRequested);
        Assert.IsTrue(connection.Disconnected.CanBeCanceled, "它必须是一个真的能被取消的令牌");
    }

    /// <summary>
    /// 释放连接要放出掉线信号 —— 上层的读循环靠它退出。
    /// </summary>
    /// <remarks>
    /// 而且**释放之后还要读得到**：令牌如果是每次从已释放的 CTS 上取，
    /// 这里会抛 <see cref="ObjectDisposedException"/>，
    /// 而「连接已经释放了」恰恰是最常去读它的时刻。
    /// </remarks>
    [TestMethod]
    public async Task 释放连接会放出掉线信号且之后仍读得到()
    {
        await using FakeServer server = new();

        SshConnectionOptions options = new("joe@test.invalid")
        {
            Dialer = server.CreateDialer(),
            HostKeyPolicy = new DangerousAcceptAnyHostKeyPolicy(),
            Credentials = [new PasswordCredential("hunter2")],
        };

        SshConnection connection = await SshConnection.ConnectAsync(options);
        CancellationToken token = connection.Disconnected;

        // 掉线要能**等**到，不是靠轮询问出来的。
        TaskCompletionSource signalled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using CancellationTokenRegistration registration = token.Register(signalled.SetResult);

        await connection.DisposeAsync();

        await signalled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsTrue(token.IsCancellationRequested);
        Assert.IsTrue(connection.Disconnected.IsCancellationRequested, "释放之后再读也要读得到");
        Assert.IsFalse(connection.IsAlive);
    }

    /// <summary>
    /// 拨通之后流上读出错（对端重置），建连报出的是库自己的「连接断了」，不是原始的 <see cref="IOException"/>。
    /// </summary>
    [TestMethod]
    public async Task 建连途中链路出错报连接断了而不是原始的IO异常()
    {
        SshConnectionOptions options = new("joe@test.invalid")
        {
            Dialer = new ResettingDialer(),
            HostKeyPolicy = new DangerousAcceptAnyHostKeyPolicy(),
            Credentials = [new PasswordCredential("hunter2")],
        };

        SshConnectionClosedException ex = await Assert.ThrowsExactlyAsync<SshConnectionClosedException>(
            async () => await SshConnection.ConnectAsync(options));

        Assert.AreEqual(SshFailureReason.ClosedByPeer, ex.Reason);
        Assert.IsInstanceOfType<IOException>(ex.InnerException);
    }

    /// <summary>发完版本串之后，再读就当成对端重置了连接。</summary>
    private sealed class ResettingDialer : ISshTransportDialer
    {
        public ValueTask<Stream> DialAsync(SshDialTarget target, CancellationToken cancellationToken) =>
            ValueTask.FromResult<Stream>(new ResettingStream());

        private sealed class ResettingStream : Stream
        {
            private readonly byte[] _banner = Encoding.ASCII.GetBytes("SSH-2.0-ResetsAfterBanner\r\n");
            private int _offset;

            public override bool CanRead => true;
            public override bool CanWrite => true;
            public override bool CanSeek => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

            public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            {
                if (_offset >= _banner.Length)
                {
                    throw new IOException("连接被对端重置。");
                }

                int count = Math.Min(buffer.Length, _banner.Length - _offset);
                _banner.AsSpan(_offset, count).CopyTo(buffer.Span);
                _offset += count;
                return ValueTask.FromResult(count);
            }

            public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
                ValueTask.CompletedTask;

            public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
            public override void Flush() { }
            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) { }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
        }
    }

    /// <summary>对端断链时也要放出掉线信号，而不是只有主动释放才放。</summary>
    [TestMethod]
    public async Task 对端断开时放出掉线信号()
    {
        await using FakeServer server = new();

        SshConnectionOptions options = new("joe@test.invalid")
        {
            Dialer = server.CreateDialer(),
            HostKeyPolicy = new DangerousAcceptAnyHostKeyPolicy(),
            Credentials = [new PasswordCredential("hunter2")],
        };

        await using SshConnection connection = await SshConnection.ConnectAsync(options);
        Assert.IsFalse(connection.Disconnected.IsCancellationRequested);

        TaskCompletionSource signalled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using CancellationTokenRegistration registration =
            connection.Disconnected.Register(signalled.SetResult);

        // 把服务端那一侧整个拆掉 —— 客户端的接收循环会读到 EOF。
        await server.DisposeAsync();

        await signalled.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.IsTrue(connection.Disconnected.IsCancellationRequested);
        Assert.IsFalse(connection.IsAlive, "掉线之后 IsAlive 与掉线令牌必须是同一个结论");
    }

    [TestMethod]
    public async Task 关掉保活时不发探测()
    {
        await using FakeServer server = new();

        SshConnectionOptions options = new("joe@test.invalid")
        {
            Dialer = server.CreateDialer(),
            HostKeyPolicy = new DangerousAcceptAnyHostKeyPolicy(),
            Credentials = [new PasswordCredential("hunter2")],
            KeepAlive = SshKeepAlivePolicy.Disabled,
        };

        await using SshConnection connection = await SshConnection.ConnectAsync(options);
        await Task.Delay(200);

        Assert.IsTrue(connection.IsAlive);
        Assert.IsFalse(options.KeepAlive.IsEnabled);
    }
}
