// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 被测规格: velashell-docs/zh/ssh/spec/09-dialing.md
//
// 假代理跑在真的环回 TCP 上，握手之后在同一条流上直接跑测试 SSH 服务端 ——
// 于是「代理应答后面紧跟着 SSH 标识串」这件事是端到端验到的，不是模拟的。

using System.Buffers.Binary;
using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using System.Text;
using VelaShell.Ssh.Auth;
using VelaShell.Ssh.Channels;
using VelaShell.Ssh.Diagnostics;
using VelaShell.Ssh.HostKeys;
using VelaShell.Ssh.Session;
using VelaShell.Ssh.Tests.TestKit;
using VelaShell.Ssh.Transport;

namespace VelaShell.Ssh.Tests.Transport;

[TestClass]
public sealed class ProxyDialerTests
{
    private const string TargetHost = "target.internal";

    // ------------------------------------------------------------ SOCKS5

    [TestMethod]
    public async Task 经SOCKS5代理连上且主机名交给代理解析()
    {
        await using var proxy = FakeSocks5Proxy.Start();

        await using SshConnection connection = await ConnectAsync(DialerChain.Socks5("127.0.0.1", proxy.Port));
        SshCommandResult output = await connection.RunAsync("hello");

        Assert.AreEqual("来自目标", output.StandardOutput);

        // ⚠️ 主机名必须原样交给代理（地址类型 3），不在本机解析。
        (byte addressType, string host, int port) = proxy.Requests.Single();
        Assert.AreEqual(3, addressType);
        Assert.AreEqual(TargetHost, host);
        Assert.AreEqual(22, port);
    }

    [TestMethod]
    public async Task SOCKS5的用户名口令认证()
    {
        await using var proxy = FakeSocks5Proxy.Start(required: new SshProxyCredentials("alice", "s3cret"));

        await using SshConnection connection = await ConnectAsync(
            DialerChain.Socks5("127.0.0.1", proxy.Port, new SshProxyCredentials("alice", "s3cret")));

        Assert.AreEqual("来自目标", (await connection.RunAsync("hello")).StandardOutput);
    }

    [TestMethod]
    public async Task SOCKS5要认证而没配凭据时报ProxyAuthRequired()
    {
        await using var proxy = FakeSocks5Proxy.Start(required: new SshProxyCredentials("alice", "s3cret"));

        SshConnectException ex = await Assert.ThrowsExactlyAsync<SshConnectException>(
            async () => await ConnectAsync(DialerChain.Socks5("127.0.0.1", proxy.Port)));

        Assert.AreEqual(SshFailureReason.ProxyAuthRequired, ex.Reason);
    }

    /// <summary>配了凭据而被拒是「改对」，不是「去配」：两个原因码分开（曾经共用 ProxyAuthRequired）。</summary>
    [TestMethod]
    public async Task SOCKS5凭据被拒时报ProxyAuthFailed()
    {
        await using var proxy = FakeSocks5Proxy.Start(required: new SshProxyCredentials("alice", "s3cret"));

        SshConnectException ex = await Assert.ThrowsExactlyAsync<SshConnectException>(
            async () => await ConnectAsync(DialerChain.Socks5("127.0.0.1", proxy.Port, new SshProxyCredentials("alice", "wrong"))));

        Assert.AreEqual(SshFailureReason.ProxyAuthFailed, ex.Reason);
        Assert.IsFalse(ex.IsRetryable);
    }

    /// <summary>凭据超长在本地就发不出去：配置错误，不是代理拒绝（那个码可重试）。</summary>
    [TestMethod]
    public async Task SOCKS5凭据超长时报配置错误()
    {
        await using var proxy = FakeSocks5Proxy.Start(required: new SshProxyCredentials("alice", "s3cret"));

        SshConnectException ex = await Assert.ThrowsExactlyAsync<SshConnectException>(
            async () => await ConnectAsync(DialerChain.Socks5("127.0.0.1", proxy.Port, new SshProxyCredentials(new string('a', 256), "s3cret"))));

        Assert.AreEqual(SshFailureReason.InvalidConfiguration, ex.Reason);
    }

    /// <summary>主机名放不进 SOCKS5 请求（不是合法域名、超过 255 字节）：配置错误，代理根本没见到请求。</summary>
    [TestMethod]
    public void SOCKS5放不进请求的主机名报配置错误()
    {
        string tooLong = string.Join('.', Enumerable.Repeat(new string('a', 60), 5)) + ".example";

        foreach (string host in new[] { "a..example", tooLong })
        {
            SshConnectException ex = Assert.ThrowsExactly<SshConnectException>(
                () => Socks5Dialer.BuildConnectRequest(new SshEndPoint(host, 22)), host);

            Assert.AreEqual(SshFailureReason.InvalidConfiguration, ex.Reason, host);
        }
    }

    [TestMethod]
    public async Task SOCKS5拒绝时带着结果码与每一跳()
    {
        await using var proxy = FakeSocks5Proxy.Start(replyCode: 5);

        SshConnectException ex = await Assert.ThrowsExactlyAsync<SshConnectException>(
            async () => await ConnectAsync(DialerChain.Socks5("127.0.0.1", proxy.Port)));

        Assert.AreEqual(SshFailureReason.ProxyRefused, ex.Reason);
        Assert.Contains("目标拒绝连接", ex.Message);

        // 从近到远：到代理成功，代理到目标失败。
        Assert.HasCount(2, ex.Hops);
        Assert.AreEqual(SshDialKind.Tcp, ex.Hops[0].Kind);
        Assert.IsTrue(ex.Hops[0].Succeeded);
        Assert.AreEqual(SshDialKind.Socks5, ex.Hops[1].Kind);
        Assert.IsFalse(ex.Hops[1].Succeeded);
        Assert.AreEqual($"{TargetHost}:22", ex.Hops[1].Target);
    }

    /// <summary>
    /// 使用者自己实现的拨号器当代理的内层：到代理那一跳记 <see cref="SshDialKind.Custom"/>。
    /// 曾经要实现者自己声明种类，按每次拨号现选路的实现给不出真值，只好记「上一次」。
    /// </summary>
    [TestMethod]
    public async Task 使用者的拨号器当内层时那一跳记Custom()
    {
        await using var proxy = FakeSocks5Proxy.Start(replyCode: 5);

        SshConnectException ex = await Assert.ThrowsExactlyAsync<SshConnectException>(
            async () => await ConnectAsync(DialerChain.Socks5("127.0.0.1", proxy.Port, via: new UserDialer())));

        Assert.HasCount(2, ex.Hops);
        Assert.AreEqual(SshDialKind.Custom, ex.Hops[0].Kind);
        Assert.IsTrue(ex.Hops[0].Succeeded);
        Assert.AreEqual(SshDialKind.Socks5, ex.Hops[1].Kind);
    }

    /// <summary>使用者自己实现的拨号器：直连。</summary>
    private sealed class UserDialer : ISshTransportDialer
    {
        public ValueTask<Stream> DialAsync(SshDialTarget target, CancellationToken cancellationToken = default) =>
            DialerChain.Tcp.DialAsync(target, cancellationToken);
    }

    [TestMethod]
    public async Task 代理本身连不上时说清是哪一跳()
    {
        int closedPort = FindClosedPort();

        SshConnectException ex = await Assert.ThrowsExactlyAsync<SshConnectException>(
            async () => await ConnectAsync(DialerChain.Socks5("127.0.0.1", closedPort)));

        // 〔W6〕没开的是代理，不是目标：ProxyUnreachable，「被拒」这个具体原因留在内层异常里。
        Assert.AreEqual(SshFailureReason.ProxyUnreachable, ex.Reason);
        Assert.IsTrue(ex.IsRetryable);
        Assert.AreEqual(SshFailureReason.TcpRefused, ((SshException)ex.InnerException!).Reason);
        Assert.Contains("SOCKS5 代理", ex.Message);
        Assert.AreEqual(SshDialKind.Tcp, ex.Hops.Single().Kind);
        Assert.IsFalse(ex.Hops.Single().Succeeded);

        // 嵌套时外层沿用内层已经说清的原因：HTTP 代理连不上，经它到 SOCKS5 的外层照样是 ProxyUnreachable，跳信息指着 HTTP 代理那一跳。
        SshConnectException nested = await Assert.ThrowsExactlyAsync<SshConnectException>(
            async () => await ConnectAsync(DialerChain.Socks5("socks.internal", 1080, via: DialerChain.HttpConnect("127.0.0.1", closedPort))));
        Assert.AreEqual(SshFailureReason.ProxyUnreachable, nested.Reason);
        Assert.AreEqual($"127.0.0.1:{closedPort}", nested.Hops.Single().Target);
    }

    [TestMethod]
    public void SOCKS5请求里IP字面量按IP发()
    {
        byte[] v4 = Socks5Dialer.BuildConnectRequest(new SshEndPoint("10.0.0.9", 22));
        Assert.AreEqual(1, v4[3]);
        Assert.AreSequenceEqual(new byte[] { 10, 0, 0, 9 }, v4[4..8]);

        byte[] v6 = Socks5Dialer.BuildConnectRequest(new SshEndPoint("::1", 2222));
        Assert.AreEqual(4, v6[3]);
        Assert.AreEqual(2222, BinaryPrimitives.ReadUInt16BigEndian(v6.AsSpan(20)));

        byte[] idn = Socks5Dialer.BuildConnectRequest(new SshEndPoint("例子.测试", 22));
        Assert.AreEqual(3, idn[3]);
        Assert.StartsWith("xn--", Encoding.ASCII.GetString(idn, 5, idn[4]));
    }

    // ------------------------------------------------------------ HTTP CONNECT

    /// <summary>
    /// 200 应答与 SSH 标识串在<b>同一次写</b>里到达：多读到的字节必须原样交还给 SSH 层。
    /// </summary>
    [TestMethod]
    public async Task 经HTTP代理连上且应答后紧跟的标识串不丢()
    {
        await using var proxy = FakeHttpProxy.Start("HTTP/1.1 200 Connection established");

        await using SshConnection connection = await ConnectAsync(
            DialerChain.HttpConnect("127.0.0.1", proxy.Port, new SshProxyCredentials("bob", "pw")));

        Assert.AreEqual("来自目标", (await connection.RunAsync("hello")).StandardOutput);

        string request = proxy.Requests.Single();
        Assert.StartsWith($"CONNECT {TargetHost}:22 HTTP/1.1\r\n", request);
        Assert.Contains($"Host: {TargetHost}:22\r\n", request);
        Assert.Contains(
$"Proxy-Authorization: Basic {Convert.ToBase64String(Encoding.UTF8.GetBytes("bob:pw"))}\r\n", request);
    }

    /// <summary>〔FW-E11〕代理先回一个或几个 1xx 中间响应再回 200：跳过它们照常连上（曾经当成拒绝）。</summary>
    [TestMethod]
    public async Task HTTP代理先回1xx中间响应时照常连上()
    {
        await using var proxy = FakeHttpProxy.Start(
            "HTTP/1.1 100 Continue\r\n\r\nHTTP/1.1 102 Processing\r\nX-Note: wait\r\n\r\nHTTP/1.1 200 Connection established");

        await using SshConnection connection = await ConnectAsync(DialerChain.HttpConnect("127.0.0.1", proxy.Port));

        Assert.AreEqual("来自目标", (await connection.RunAsync("hello")).StandardOutput);
    }

    [TestMethod]
    [DataRow("evil.example\r\nX-Injected: 1", DisplayName = "CR LF 注入头部")]
    [DataRow("evil.example\nHost: other", DisplayName = "LF")]
    [DataRow("a b", DisplayName = "空格")]
    [DataRow("host/path", DisplayName = "斜杠")]
    [DataRow("x:y", DisplayName = "带冒号却不是 IPv6")]
    public void HTTP代理请求里不放行合法主机名以外的字符(string host)
    {
        // 主机名原样拼进请求行与 Host 头的话，带 \r\n 就能往发给代理的请求里注入头部。
        SshConnectException error = Assert.ThrowsExactly<SshConnectException>(
            () => HttpConnectDialer.BuildRequest(new SshEndPoint(host, 22), credentials: null));

        // 不是「代理拒绝」—— 那一类会被当成可重试的；这里重试多少次都一样，得改输入。
        Assert.AreEqual(SshFailureReason.InvalidConfiguration, error.Reason);
        Assert.IsFalse(error.IsRetryable);
        Assert.DoesNotContain("\n", error.Message, "消息里的主机名也要清掉控制字符");
    }

    [TestMethod]
    public void HTTP代理请求里国际化域名转Punycode_IPv6加方括号()
    {
        // 请求按 ASCII 编码：直接编码会把非 ASCII 字符变成「?」，代理拿到的就是另一个名字。
        string idn = Encoding.ASCII.GetString(HttpConnectDialer.BuildRequest(new SshEndPoint("例子.测试", 22), null));
        Assert.StartsWith("CONNECT xn--", idn);
        Assert.DoesNotContain("?", idn);

        string v6 = Encoding.ASCII.GetString(HttpConnectDialer.BuildRequest(new SshEndPoint("::1", 2222), null));
        Assert.StartsWith("CONNECT [::1]:2222 HTTP/1.1\r\n", v6);
        Assert.Contains("Host: [::1]:2222\r\n", v6);
    }

    [TestMethod]
    public async Task HTTP代理407报ProxyAuthRequired()
    {
        await using var proxy = FakeHttpProxy.Start(
            "HTTP/1.1 407 Proxy Authentication Required\r\nProxy-Authenticate: Basic realm=\"corp\"");

        SshConnectException ex = await Assert.ThrowsExactlyAsync<SshConnectException>(
            async () => await ConnectAsync(DialerChain.HttpConnect("127.0.0.1", proxy.Port)));

        Assert.AreEqual(SshFailureReason.ProxyAuthRequired, ex.Reason);
        Assert.Contains("Basic realm", ex.Message);
    }

    [TestMethod]
    public async Task HTTP代理407且配了凭据时报ProxyAuthFailed()
    {
        await using var proxy = FakeHttpProxy.Start(
            "HTTP/1.1 407 Proxy Authentication Required\r\nProxy-Authenticate: Basic realm=\"corp\"");

        SshConnectException ex = await Assert.ThrowsExactlyAsync<SshConnectException>(
            async () => await ConnectAsync(DialerChain.HttpConnect("127.0.0.1", proxy.Port, new SshProxyCredentials("alice", "wrong"))));

        Assert.AreEqual(SshFailureReason.ProxyAuthFailed, ex.Reason);
        Assert.Contains("alice", ex.Message);
    }

    [TestMethod]
    public async Task HTTP代理拒绝22端口时给出建议()
    {
        await using var proxy = FakeHttpProxy.Start("HTTP/1.1 403 Forbidden");

        SshConnectException ex = await Assert.ThrowsExactlyAsync<SshConnectException>(
            async () => await ConnectAsync(DialerChain.HttpConnect("127.0.0.1", proxy.Port)));

        Assert.AreEqual(SshFailureReason.ProxyRefused, ex.Reason);
        Assert.Contains("80/443", ex.Message);
    }

    /// <summary>嵌套：经 HTTP 代理到达 SOCKS5 代理，再由它连目标。跳信息从近到远。</summary>
    [TestMethod]
    public async Task 代理可以嵌套且失败时跳信息从近到远()
    {
        await using var http = FakeHttpProxy.Start("HTTP/1.1 502 Bad Gateway");

        SshConnectException ex = await Assert.ThrowsExactlyAsync<SshConnectException>(
            async () => await ConnectAsync(
                DialerChain.Socks5("socks.internal", 1080, via: DialerChain.HttpConnect("127.0.0.1", http.Port))));

        Assert.AreEqual(SshFailureReason.ProxyRefused, ex.Reason);
        Assert.AreEqual(SshDialKind.Tcp, ex.Hops[0].Kind);
        Assert.IsTrue(ex.Hops[0].Succeeded);
        Assert.AreEqual(SshDialKind.HttpConnect, ex.Hops[1].Kind);
        Assert.AreEqual("socks.internal:1080", ex.Hops[1].Target, "HTTP 代理那一跳要连的是 SOCKS5 代理");
        Assert.IsFalse(ex.Hops[1].Succeeded);
    }

    // ------------------------------------------------------------ 跳板

    [TestMethod]
    public async Task 经跳板连上目标且目标名交给跳板解析()
    {
        List<string> tunnelTargets = [];
        await using JumpHost jump = new(tunnelTargets);

        await using SshConnection connection = await ConnectAsync(new SshJumpDialer(jump.Options));

        Assert.AreEqual("来自目标", (await connection.RunAsync("hello")).StandardOutput);
        Assert.AreEqual($"{TargetHost}:22", tunnelTargets.Single());
    }

    /// <summary>
    /// 〔FW-D4〕跳板认证没过：仍然是认证失败（SshAuthenticationException），逐条尝试记录与服务端给的方法留在最外层，
    /// 消息里说清是哪一跳。曾经改写成 SshConnectException，那些结构化信息只剩在 InnerException 里。
    /// </summary>
    [TestMethod]
    public async Task 跳板认证失败时仍报认证失败且带着尝试记录()
    {
        await using JumpHost jump = new([]);

        SshAuthenticationException ex = await Assert.ThrowsExactlyAsync<SshAuthenticationException>(
            async () => await ConnectAsync(new SshJumpDialer(jump.Options with { Credentials = [new PasswordCredential("wrong")] })));

        Assert.IsNotEmpty(ex.Attempts, "尝试记录要留在最外层");
        Assert.Contains(Ssh.Protocol.SshProtocolNames.AuthPassword, ex.ServerOffered);
        Assert.Contains("跳板", ex.Message);
        Assert.Contains("jump.example", ex.Message);
    }

    [TestMethod]
    public async Task 跳板拒绝转发时报ProxyRefused且说清哪一跳()
    {
        await using JumpHost jump = new(tunnelTargets: null);   // 不配隧道处理器 = 拒绝

        SshConnectException ex = await Assert.ThrowsExactlyAsync<SshConnectException>(
            async () => await ConnectAsync(new SshJumpDialer(jump.Options)));

        Assert.AreEqual(SshFailureReason.ProxyRefused, ex.Reason);
        Assert.HasCount(2, ex.Hops);
        Assert.IsTrue(ex.Hops[0].Succeeded, "跳板本身是连上了的");
        Assert.AreEqual(SshDialKind.SshJump, ex.Hops[1].Kind);
        Assert.IsFalse(ex.Hops[1].Succeeded);
    }

    /// <summary>
    /// 开隧道时跳板自己断了：那是断线（可重试），不是「跳板不肯转发」。曾经一律改写成 ProxyRefused。
    /// </summary>
    [TestMethod]
    public async Task 跳板在开隧道时断开不报成不肯转发()
    {
        await using JumpHost jump = new(tunnelTargets: null, dropOnTunnelOpen: true);

        SshConnectException ex = await Assert.ThrowsExactlyAsync<SshConnectException>(
            async () => await ConnectAsync(new SshJumpDialer(jump.Options)));

        Assert.AreEqual(SshFailureReason.ClosedByPeer, ex.Reason);
        Assert.DoesNotContain("不肯转发", ex.Message);
        Assert.HasCount(2, ex.Hops);
        Assert.IsTrue(ex.Hops[0].Succeeded, "跳板本身是连上了的");
        Assert.IsFalse(ex.Hops[1].Succeeded);
    }

    [TestMethod]
    public async Task 在跳板上输口令的时间不算进外层的连接超时()
    {
        // 用户在跳板上输动态码花了比外层连接超时还长的时间 —— 那是在等人，不是网络慢。
        List<string> tunnelTargets = [];
        await using JumpHost jump = new(tunnelTargets);

        SshJumpDialer dialer = new(jump.Options with
        {
            Credentials =
            [
                new PasswordCredential(async cancellationToken =>
                {
                    await Task.Delay(TimeSpan.FromSeconds(1.5), cancellationToken);
                    return "hunter2";
                }),
            ],
        });

        SshConnectionOptions options = new($"joe@{TargetHost}:22")
        {
            Dialer = dialer,
            HostKeyPolicy = new DangerousAcceptAnyHostKeyPolicy(),
            Credentials = [new PasswordCredential("hunter2")],
            ConnectTimeout = TimeSpan.FromSeconds(1),
        };

        await using SshConnection connection = await SshConnection.ConnectAsync(options);
        Assert.AreEqual("来自目标", (await connection.RunAsync("hello")).StandardOutput);
    }

    /// <summary>
    /// 跳板连接由调用方的回调建（按跳准备凭据的那种用法）：在跳板上输口令的时间同样不算进外层的连接超时。
    /// </summary>
    /// <remarks>曾经回调只拿到一个取消令牌、自己去连，外层的计时器传不进去 —— 认证被当场掐断。</remarks>
    [TestMethod]
    public async Task 回调建的跳板上输口令的时间也不算进外层的连接超时()
    {
        List<string> tunnelTargets = [];
        await using JumpHost jump = new(tunnelTargets);

        SshConnectionOptions jumpOptions = jump.Options with
        {
            Credentials =
            [
                new PasswordCredential(async cancellationToken =>
                {
                    await Task.Delay(TimeSpan.FromSeconds(1.5), cancellationToken);
                    return "hunter2";
                }),
            ],
        };

        ISshTransportDialer dialer = DialerChain.Jump(
            new SshEndPoint(jumpOptions.Host, jumpOptions.Port),
            (context, cancellationToken) => context.ConnectAsync(jumpOptions, cancellationToken));

        SshConnectionOptions options = new($"joe@{TargetHost}:22")
        {
            Dialer = dialer,
            HostKeyPolicy = new DangerousAcceptAnyHostKeyPolicy(),
            Credentials = [new PasswordCredential("hunter2")],
            ConnectTimeout = TimeSpan.FromSeconds(1),
        };

        await using SshConnection connection = await SshConnection.ConnectAsync(options);
        Assert.AreEqual("来自目标", (await connection.RunAsync("hello")).StandardOutput);
    }

    /// <summary>
    /// 跳板的主机密钥要用户确认（首次连跳板，确认框摆在那里等人看指纹）：确认的时间同样不算进外层的连接超时 ——
    /// 直接拨号与回调建的跳板两种都是。曾经只测过跳板上输口令。
    /// </summary>
    [TestMethod]
    [DataRow(false, DisplayName = "跳板拨号器")]
    [DataRow(true, DisplayName = "回调建的跳板")]
    public async Task 确认跳板主机密钥的时间不算进外层的连接超时(bool viaCallback)
    {
        List<string> tunnelTargets = [];
        await using JumpHost jump = new(tunnelTargets);
        ThinkingPolicy thinking = new(TimeSpan.FromSeconds(1.5));
        SshConnectionOptions jumpOptions = jump.Options with { HostKeyPolicy = thinking };

        ISshTransportDialer dialer = viaCallback
            ? DialerChain.Jump(
                new SshEndPoint(jumpOptions.Host, jumpOptions.Port),
                (context, cancellationToken) => context.ConnectAsync(jumpOptions, cancellationToken))
            : new SshJumpDialer(jumpOptions);

        SshConnectionOptions options = new($"joe@{TargetHost}:22")
        {
            Dialer = dialer,
            HostKeyPolicy = new DangerousAcceptAnyHostKeyPolicy(),
            Credentials = [new PasswordCredential("hunter2")],
            ConnectTimeout = TimeSpan.FromSeconds(1),
        };

        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        await using SshConnection connection = await SshConnection.ConnectAsync(options);
        Assert.AreEqual("来自目标", (await connection.RunAsync("hello")).StandardOutput);
        Assert.AreEqual(1, thinking.Evaluations, "跳板的主机密钥要经过这个策略");
        Assert.IsGreaterThan(TimeSpan.FromSeconds(1.4), elapsed.Elapsed, "前提：确认确实拖过了外层的连接超时");
    }

    /// <summary>想一会儿再信任：扮演摆在那里等人看指纹的确认框。</summary>
    private sealed class ThinkingPolicy(TimeSpan thinking) : IHostKeyPolicy
    {
        public int Evaluations { get; private set; }

        public async ValueTask<SshHostKeyVerdict> EvaluateAsync(SshHostKeyContext context, CancellationToken cancellationToken = default)
        {
            Evaluations++;
            await Task.Delay(thinking, cancellationToken);
            return SshHostKeyVerdict.Accept;
        }
    }

    [TestMethod]
    public async Task 外层计时器在跳板握手时到点_报超时并说清是哪一跳()
    {
        // 跳板那头一声不吭（握手卡住）。外层的计时器先到：不能报成「建立 TCP 连接超时」——
        // 卡住的是跳板的握手。
        SshConnectionOptions silentJump = new("jumper@jump.example:22")
        {
            Dialer = InMemoryTransport.CreateDialer((_, _, _) => ValueTask.CompletedTask),
            HostKeyPolicy = new DangerousAcceptAnyHostKeyPolicy(),
            Credentials = [new PasswordCredential("hunter2")],
            ConnectTimeout = TimeSpan.FromSeconds(30),
        };

        SshConnectionOptions options = new($"joe@{TargetHost}:22")
        {
            Dialer = new SshJumpDialer(silentJump),
            HostKeyPolicy = new DangerousAcceptAnyHostKeyPolicy(),
            Credentials = [new PasswordCredential("hunter2")],
            ConnectTimeout = TimeSpan.FromMilliseconds(500),
        };

        SshConnectException ex = await Assert.ThrowsExactlyAsync<SshConnectException>(
            async () => await SshConnection.ConnectAsync(options));

        Assert.AreEqual(SshFailureReason.Timeout, ex.Reason);
        Assert.Contains("跳板", ex.Message);
        Assert.AreEqual(SshDialKind.SshJump, ex.Hops[^1].Kind);
        Assert.IsFalse(ex.Hops[^1].Succeeded);
    }

    /// <summary>
    /// 代理接下了 TCP、却一直不回握手。连接计时器先到：不能报成「建立 TCP 连接超时」—— TCP 早连上了，
    /// 卡住的是代理。与跳板同一个做法：报超时并说清是哪一跳。
    /// </summary>
    [TestMethod]
    public async Task 代理接下连接却不回握手时报超时并说清是哪一跳()
    {
        SshConnectionOptions options = new($"joe@{TargetHost}:22")
        {
            Dialer = DialerChain.Socks5("proxy.example", 1080, via: InMemoryTransport.CreateDialer((_, _, _) => ValueTask.CompletedTask)),
            HostKeyPolicy = new DangerousAcceptAnyHostKeyPolicy(),
            Credentials = [new PasswordCredential("hunter2")],
            ConnectTimeout = TimeSpan.FromMilliseconds(500),
        };

        SshConnectException ex = await Assert.ThrowsExactlyAsync<SshConnectException>(
            async () => await SshConnection.ConnectAsync(options));

        Assert.AreEqual(SshFailureReason.Timeout, ex.Reason);
        Assert.AreEqual(SshPhase.Dialing, ex.Phase);
        Assert.Contains("代理握手", ex.Message);
        Assert.HasCount(2, ex.Hops);
        Assert.IsTrue(ex.Hops[0].Succeeded, "到代理那一跳是通的");
        Assert.AreEqual(SshDialKind.Socks5, ex.Hops[1].Kind);
        Assert.IsFalse(ex.Hops[1].Succeeded);
    }

    // ------------------------------------------------------------ 代理命令

    [TestMethod]
    public void 代理命令的记号替换()
    {
        ProxyCommandDialer dialer = new("nc -X connect -x proxy:3128 %h %p # %r@%n 100%%")
        {
            UserName = "joe",
            OriginalHost = "alias",
        };

        Assert.AreEqual(
            "nc -X connect -x proxy:3128 10.0.0.9 2222 # joe@alias 100%",
            dialer.Expand(new SshEndPoint("10.0.0.9", 2222)));
    }

    [TestMethod]
    [DataRow("x;touch /tmp/pwn", null, DisplayName = "sh 的命令分隔")]
    [DataRow("x&calc", null, DisplayName = "cmd 的命令分隔")]
    [DataRow("%COMSPEC%", null, DisplayName = "cmd 的环境变量展开")]
    [DataRow("$(id)", null, DisplayName = "sh 的命令替换")]
    [DataRow("host.example.com", "$(id)", DisplayName = "用户名里的命令替换")]
    [DataRow("host\nrm -rf ~", null, DisplayName = "换行")]
    [DataRow("-oProxyUseFdpass", null, DisplayName = "主机名以 - 开头:被 ssh 当成选项")]
    [DataRow("-e", null, DisplayName = "主机名以 - 开头:被 nc 当成选项")]
    [DataRow("host.example.com", "-x", DisplayName = "用户名以 - 开头")]
    public void 主机名或用户名里有shell元字符时不代入ProxyCommand(string host, string? user)
    {
        // 主机名、用户名常常不是写配置的人给的（ssh:// 链接、导入的会话、快速连接框）。
        // 原样代入交给 shell 的命令行，就是一条被执行的命令（CVE-2023-51385 那一类）。
        ProxyCommandDialer dialer = new("nc %h %p # %r") { UserName = user ?? "joe" };

        SshConnectException error = Assert.ThrowsExactly<SshConnectException>(
            () => dialer.Expand(new SshEndPoint(host, 22)));
        // 不是「代理拒绝」—— 那一类会被当成可重试的；这里重试一百次也一样，得改输入。
        Assert.AreEqual(SshFailureReason.InvalidConfiguration, error.Reason);
        Assert.IsFalse(error.IsRetryable);
    }

    [TestMethod]
    public void IPv6地址与带域名的用户照常代入()
    {
        ProxyCommandDialer dialer = new("nc %h %p # %r") { UserName = "joe@corp.example" };
        Assert.AreEqual("nc fe80::1 22 # joe@corp.example", dialer.Expand(new SshEndPoint("fe80::1", 22)));
    }

    [TestMethod]
    public async Task 代理命令失败时带出它在stderr上说的话()
    {
        string command = OperatingSystem.IsWindows()
            ? "echo 代理拒绝了 1>&2 & exit /b 3"
            : "echo 代理拒绝了 >&2; exit 3";

        await using Stream stream = await new ProxyCommandDialer(command)
            .DialAsync(SshDialTarget.Direct(TargetHost, 22));

        // 〔spec/09 §6〕连上之前就退出是拨号失败：ProxyRefused，带着这一跳。
        SshConnectException ex = await Assert.ThrowsExactlyAsync<SshConnectException>(
            async () => await stream.ReadExactlyAsync(new byte[16]));

        Assert.AreEqual(SshFailureReason.ProxyRefused, ex.Reason);
        Assert.AreEqual(SshPhase.Dialing, ex.Phase);
        Assert.Contains("退出码 3", ex.Message);
        Assert.HasCount(1, ex.Hops);
        Assert.AreEqual(SshDialKind.ProxyCommand, ex.Hops[0].Kind);
        Assert.IsFalse(ex.Hops[0].Succeeded);
    }

    /// <summary>
    /// 建连全程：代理程序在连上之前退出，报的是代理那一跳失败，stderr 完整进消息。
    /// 曾经被建连路上那道 catch 归成「对端关闭了连接」（ClosedByPeer），消息截到 256 个字符，跳信息也没了。
    /// </summary>
    [TestMethod]
    public async Task 代理命令在连上之前退出时建连报ProxyRefused且stderr不截断()
    {
        string detail = "proxy said no " + new string('x', 300) + " END";
        string command = OperatingSystem.IsWindows()
            ? $"echo {detail} 1>&2 & exit /b 7"
            : $"echo {detail} >&2; exit 7";

        SshConnectException ex = await Assert.ThrowsExactlyAsync<SshConnectException>(
            async () => await ConnectAsync(new ProxyCommandDialer(command)));

        Assert.AreEqual(SshFailureReason.ProxyRefused, ex.Reason);
        Assert.Contains("退出码 7", ex.Message);
        Assert.Contains(" END", ex.Message, "stderr 的末尾不该被截掉 —— 代理程序的失败原因常在最后一行");
        Assert.AreEqual(SshDialKind.ProxyCommand, ex.Hops[^1].Kind);
    }

    // ------------------------------------------------------------ 脚手架

    private static async Task<SshConnection> ConnectAsync(ISshTransportDialer dialer)
    {
        SshConnectionOptions options = new($"joe@{TargetHost}:22")
        {
            Dialer = dialer,
            HostKeyPolicy = new DangerousAcceptAnyHostKeyPolicy(),
            Credentials = [new PasswordCredential("hunter2")],
            ConnectTimeout = TimeSpan.FromSeconds(10),
        };

        return await SshConnection.ConnectAsync(options);
    }

    /// <summary>在一条流上跑完整的测试 SSH 服务端（握手 → 认证 → 通道）。</summary>
    private static async Task ServeSshAsync(Stream stream, TestChannelScript script, CancellationToken cancellationToken)
    {
        try
        {
            await using TestSshServer server = new(stream);
            TestSshServerHandshake handshake = await server.HandshakeAsync(cancellationToken);

            TestAuthServer auth = new(
                server.Transport, handshake.ExchangeHash, new TestAuthPolicy { AcceptPassword = "hunter2" });
            await auth.RunAsync(cancellationToken);

            TestChannelServer channels = new(server.Transport, script);
            await channels.RunAsync(cancellationToken);
        }
        catch (Exception)
        {
            // 客户端走了、用例拆场 —— 服务端这一侧的收尾不是被测对象。
        }
    }

    private static TestChannelScript TargetScript() => new()
    {
        StandardOutput = Encoding.UTF8.GetBytes("来自目标"),
        ExitCode = 0,
    };

    private static int FindClosedPort()
    {
        using Socket probe = new(SocketType.Stream, ProtocolType.Tcp);
        probe.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)probe.LocalEndPoint!).Port;   // 绑了但不 Listen，随后释放 —— 连它会被拒
    }

    /// <summary>一个会说 SOCKS5 的假代理：握手完之后自己就是目标 SSH 服务端。</summary>
    private sealed class FakeSocks5Proxy : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cts = new(TimeSpan.FromSeconds(30));
        private readonly SshProxyCredentials? _required;
        private readonly byte _replyCode;
        private readonly Task _loop;

        private FakeSocks5Proxy(SshProxyCredentials? required, byte replyCode)
        {
            _required = required;
            _replyCode = replyCode;
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            _loop = AcceptLoopAsync();
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public List<(byte AddressType, string Host, int Port)> Requests { get; } = [];

        public static FakeSocks5Proxy Start(SshProxyCredentials? required = null, byte replyCode = 0) =>
            new(required, replyCode);

        private async Task AcceptLoopAsync()
        {
            try
            {
                while (true)
                {
                    Socket socket = await _listener.AcceptSocketAsync(_cts.Token);
                    _ = HandleAsync(new NetworkStream(socket, ownsSocket: true));
                }
            }
            catch (Exception)
            {
                // 收工。
            }
        }

        private async Task HandleAsync(NetworkStream stream)
        {
            await using NetworkStream owned = stream;
            try
            {
                byte[] head = await ReadAsync(stream, 2);
                byte[] methods = await ReadAsync(stream, head[1]);

                byte choice = _required is null ? (byte)0
                    : methods.Contains((byte)2) ? (byte)2
                    : (byte)0xFF;
                await stream.WriteAsync(new byte[] { 5, choice }, _cts.Token);
                if (choice == 0xFF)
                {
                    return;
                }

                if (choice == 2)
                {
                    _ = await ReadAsync(stream, 1);
                    string user = Encoding.UTF8.GetString(await ReadAsync(stream, (await ReadAsync(stream, 1))[0]));
                    string password = Encoding.UTF8.GetString(await ReadAsync(stream, (await ReadAsync(stream, 1))[0]));
                    bool ok = user == _required!.UserName && password == _required.Password;
                    await stream.WriteAsync(new byte[] { 1, ok ? (byte)0 : (byte)1 }, _cts.Token);
                    if (!ok)
                    {
                        return;
                    }
                }

                byte[] request = await ReadAsync(stream, 4);
                string host = request[3] switch
                {
                    1 => new IPAddress(await ReadAsync(stream, 4)).ToString(),
                    4 => new IPAddress(await ReadAsync(stream, 16)).ToString(),
                    _ => Encoding.ASCII.GetString(await ReadAsync(stream, (await ReadAsync(stream, 1))[0])),
                };
                int port = BinaryPrimitives.ReadUInt16BigEndian(await ReadAsync(stream, 2));
                lock (Requests)
                {
                    Requests.Add((request[3], host, port));
                }

                await stream.WriteAsync(new byte[] { 5, _replyCode, 0, 1, 0, 0, 0, 0, 0, 0 }, _cts.Token);
                if (_replyCode != 0)
                {
                    return;
                }

                await ServeSshAsync(stream, TargetScript(), _cts.Token);
            }
            catch (Exception)
            {
                // 客户端走了。
            }
        }

        private async Task<byte[]> ReadAsync(Stream stream, int count)
        {
            byte[] buffer = new byte[count];
            await stream.ReadExactlyAsync(buffer, _cts.Token);
            return buffer;
        }

        public async ValueTask DisposeAsync()
        {
            await _cts.CancelAsync();
            _listener.Stop();
            await _loop;
            _cts.Dispose();
        }
    }

    /// <summary>一个会说 HTTP CONNECT 的假代理：200 之后自己就是目标 SSH 服务端。</summary>
    private sealed class FakeHttpProxy : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cts = new(TimeSpan.FromSeconds(30));
        private readonly string _responseHead;
        private readonly Task _loop;

        private FakeHttpProxy(string responseHead)
        {
            _responseHead = responseHead;
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            _loop = AcceptLoopAsync();
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public List<string> Requests { get; } = [];

        public static FakeHttpProxy Start(string responseHead) => new(responseHead);

        private async Task AcceptLoopAsync()
        {
            try
            {
                while (true)
                {
                    Socket socket = await _listener.AcceptSocketAsync(_cts.Token);
                    _ = HandleAsync(new NetworkStream(socket, ownsSocket: true));
                }
            }
            catch (Exception)
            {
                // 收工。
            }
        }

        private async Task HandleAsync(NetworkStream stream)
        {
            await using NetworkStream owned = stream;
            try
            {
                // 一个字节一个字节读到空行 —— 假代理不能多读，多读的是客户端的 SSH 标识串。
                StringBuilder request = new();
                byte[] one = new byte[1];
                while (!request.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
                {
                    await stream.ReadExactlyAsync(one, _cts.Token);
                    request.Append((char)one[0]);
                }
                lock (Requests)
                {
                    Requests.Add(request.ToString());
                }

                byte[] response = Encoding.ASCII.GetBytes(_responseHead + "\r\n\r\n");
                if (!_responseHead.Contains(" 200 ", StringComparison.Ordinal))
                {
                    await stream.WriteAsync(response, _cts.Token);
                    return;
                }

                // ⚠️ 200 与服务端的第一次写（标识串）**合成一次写** —— 验「多读的字节要交还」。
                await ServeSshAsync(new FirstWritePrefixStream(response, stream), TargetScript(), _cts.Token);
            }
            catch (Exception)
            {
                // 客户端走了。
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _cts.CancelAsync();
            _listener.Stop();
            await _loop;
            _cts.Dispose();
        }
    }

    /// <summary>把一段字节拼到第一次写的前面。</summary>
    private sealed class FirstWritePrefixStream(byte[] prefix, Stream inner) : Stream
    {
        private byte[]? _prefix = prefix;

        public override bool CanRead => true;

        public override bool CanWrite => true;

        public override bool CanSeek => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            inner.ReadAsync(buffer, cancellationToken);

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Exchange(ref _prefix, null) is { } first)
            {
                await inner.WriteAsync((byte[])[.. first, .. buffer.Span], cancellationToken);
                return;
            }
            await inner.WriteAsync(buffer, cancellationToken);
        }

        public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);

        public override void Flush() => inner.Flush();

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();
    }

    /// <summary>一台内存里的跳板：它的 direct-tcpip 隧道里再跑一台目标 SSH 服务端。</summary>
    private sealed class JumpHost : IAsyncDisposable
    {
        private readonly CancellationTokenSource _cts = new(TimeSpan.FromSeconds(30));
        private readonly List<Task> _servers = [];

        public JumpHost(List<string>? tunnelTargets, bool dropOnTunnelOpen = false)
        {
            TestChannelScript jumpScript = tunnelTargets is null
                ? new TestChannelScript { DropConnectionOnTunnelOpen = dropOnTunnelOpen }
                : new TestChannelScript
                {
                    TunnelHandler = (target, input, output, ct) =>
                    {
                        lock (tunnelTargets)
                        {
                            tunnelTargets.Add(target);
                        }
                        return ServeSshAsync(new DuplexPipeStream(input, output), TargetScript(), ct);
                    },
                };

            Options = new SshConnectionOptions("jumper@jump.example:22")
            {
                Dialer = InMemoryTransport.CreateDialer((server, _, _) =>
                {
                    lock (_servers)
                    {
                        _servers.Add(ServeSshAsync(server, jumpScript, _cts.Token));
                    }
                    return ValueTask.CompletedTask;
                }),
                HostKeyPolicy = new DangerousAcceptAnyHostKeyPolicy(),
                Credentials = [new PasswordCredential("hunter2")],
            };
        }

        public SshConnectionOptions Options { get; }

        public async ValueTask DisposeAsync()
        {
            await _cts.CancelAsync();
            Task[] servers;
            lock (_servers)
            {
                servers = [.. _servers];
            }
            await Task.WhenAll(servers);
            _cts.Dispose();
        }
    }

    /// <summary>一对管道拼成的双向流（隧道的服务端那一头）。</summary>
    private sealed class DuplexPipeStream(PipeReader input, PipeWriter output) : Stream
    {
        private readonly Stream _read = input.AsStream();
        private readonly Stream _write = output.AsStream();

        public override bool CanRead => true;

        public override bool CanWrite => true;

        public override bool CanSeek => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            _read.ReadAsync(buffer, cancellationToken);

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await _write.WriteAsync(buffer, cancellationToken);
            await _write.FlushAsync(cancellationToken);
        }

        public override Task FlushAsync(CancellationToken cancellationToken) => _write.FlushAsync(cancellationToken);

        public override void Flush() => _write.Flush();

        public override int Read(byte[] buffer, int offset, int count) => _read.Read(buffer, offset, count);

        public override void Write(byte[] buffer, int offset, int count) => _write.Write(buffer, offset, count);

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _read.Dispose();
                _write.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
