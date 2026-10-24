using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Sockets;
using NSubstitute;
using VelaShell.Core.Models;
using VelaShell.Core.Resources;
using VelaShell.Core.Ssh;
using VelaShell.Infrastructure.Ssh;
using VelaShell.Infrastructure.Tunnels;

namespace VelaShell.Core.Tests.Ssh;

/// <summary>
/// 隧道在<b>真实 OpenSSH 服务端</b>上的端到端行为:从 <see cref="TunnelConfig" /> 起,经 <see cref="TunnelService" />、
/// 宿主的 SSH 客户端包装与 SSH 库的转发器,一直到线上。
/// </summary>
/// <remarks>
/// <para>
/// 靶子是 <c>docker-compose.test.yml</c> 的 <c>ssh-shells</c>(端口 2223,账号 vela-bash,Alpine 的 OpenSSH,
/// <c>AllowTcpForwarding</c> / <c>AllowStreamLocalForwarding</c> 都是默认的开)。远端那头用 bash 的 <c>/dev/tcp</c>
/// 当客户端,不依赖靶机上装了什么别的工具。
/// </para>
/// <para>
/// 连接走宿主真正的装配路径(<see cref="SshConnectionAssembler" /> + <see cref="VelaSshClientWrapper" />),
/// 隧道走 <see cref="TunnelService" /> —— 要验的正是「面板上填的那几项有没有一路接到线上」。
/// </para>
/// </remarks>
[SuppressMessage("Usage", "MSTEST0045:Use cooperative cancellation with [Timeout]",
    Justification = "被等待的 docker/SSH 操作不接受测试取消令牌,协作取消无法中断它们。")]
[TestClass]
[TestCategory("DockerIntegration")]
[DoNotParallelize]
public class TunnelForwardingIntegrationTests
{
    private const string TestHost = "127.0.0.1";
    private const int TestPort = 2223;
    private const string TestUser = "vela-bash";
    private const string TestPassword = "velapass";

    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// 限速真的生效:远端往一条限速 64 KB/s 的远程转发里灌 256 KB,本机这头收完要三秒上下(一秒的突发额度之后按速率补);
    /// 搬运途中,速率与连接快照(来源、目标、到目前为止的字节数)也从库一路同步到了 <see cref="TunnelInfo" />。
    /// </summary>
    [TestMethod]
    [Timeout(60_000)]
    public async Task RemoteForward_RateLimitIsEnforcedAndTheConnectionShowsUpWhileItRuns()
    {
        RequireContainer();
        await using VelaSshClientWrapper ssh = await ConnectAsync();
        await using TunnelService service = CreateService(ssh);
        using TcpListener sink = new(IPAddress.Loopback, 0);
        sink.Start();
        int sinkPort = ((IPEndPoint)sink.LocalEndpoint).Port;
        int serverPort = RandomServerPort();
        const int payload = 256 * 1024;

        TunnelInfo tunnel = await service.CreateRemoteForwardAsync(SessionId, new TunnelConfig
        {
            Type = TunnelType.RemoteForward,
            Name = "limited",
            LocalHost = "127.0.0.1",
            LocalPort = (uint)sinkPort,
            RemoteHost = "127.0.0.1",
            RemotePort = (uint)serverPort,
            MaxBytesPerSecond = 64 * 1024
        });

        Task<string> push = ssh.RunCommandAsync(
            $"head -c {payload} /dev/zero > /dev/tcp/127.0.0.1/{serverPort} && echo PUSHED");
        using TcpClient accepted = await sink.AcceptTcpClientAsync(TestContext.CancellationToken);
        Stopwatch elapsed = Stopwatch.StartNew();
        Task<long> drain = DrainAsync(accepted.GetStream());

        await Task.Delay(TimeSpan.FromSeconds(1.5), TestContext.CancellationToken);
        service.RefreshStatistics();
        TunnelConnectionInfo connection = tunnel.Connections.Single();
        Assert.AreEqual($"127.0.0.1:{sinkPort}", connection.Target);
        Assert.IsGreaterThan(0L, connection.BytesReceived, "搬运途中的连接快照里应当已有收回的字节。");
        Assert.IsLessThan((long)payload, connection.BytesReceived, "限着速,一秒半之内不该已经收完。");
        Assert.IsGreaterThan(0d, tunnel.ReceivedBytesPerSecond);
        Assert.AreEqual(1, tunnel.ActiveConnections);

        long received = await drain;
        elapsed.Stop();
        StringAssert.Contains(await push, "PUSHED");
        Assert.AreEqual((long)payload, received);
        Assert.IsGreaterThanOrEqualTo(2.0, elapsed.Elapsed.TotalSeconds,
            $"64 KB/s 收 256 KB 至少要两秒多(一秒突发之后按速率补),实际 {elapsed.Elapsed.TotalSeconds:0.00} 秒 —— 限速没生效。");

        // 本机这头也关掉,这条连接才算搬完、从快照里摘掉(半关闭:另一个方向还开着时它仍在搬)。
        accepted.Dispose();
        await WaitForAsync(() =>
        {
            service.RefreshStatistics();
            return tunnel.Connections.Count == 0;
        }, "搬完的连接应当从快照里摘掉。");
    }

    /// <summary>
    /// 远程动态转发:远端把服务器上的那个端口当 SOCKS5 代理,名单里的目标由本机替它连上、双向搬运;
    /// 名单外的目标回「规则不允许」(0x02),隧道行上记一句本地化的提示。
    /// </summary>
    [TestMethod]
    [Timeout(60_000)]
    public async Task RemoteDynamicForward_ConnectsAllowedTargetsAndRefusesTheRest()
    {
        RequireContainer();
        await using VelaSshClientWrapper ssh = await ConnectAsync();
        await using TunnelService service = CreateService(ssh);
        using TcpListener echo = new(IPAddress.Loopback, 0);
        echo.Start();
        int echoPort = ((IPEndPoint)echo.LocalEndpoint).Port;
        int serverPort = RandomServerPort();

        TunnelInfo tunnel = await service.CreateRemoteDynamicForwardAsync(SessionId, new TunnelConfig
        {
            Type = TunnelType.RemoteDynamicForward,
            Name = "socks-back",
            LocalHost = "127.0.0.1",
            LocalPort = 0,
            RemoteHost = "127.0.0.1",
            RemotePort = (uint)serverPort,
            PermitRemoteOpen = [$"127.0.0.1:{echoPort}"]
        });

        Task serve = ServeUppercaseEchoOnceAsync(echo);
        string allowed = await ssh.RunCommandAsync(SocksClientScript(serverPort, echoPort, "ping"));
        await serve.WaitAsync(TimeSpan.FromSeconds(10), TestContext.CancellationToken);

        StringAssert.Contains(allowed, "M=05 00", $"SOCKS 方法协商没过。远端输出:\n{allowed}");
        StringAssert.Contains(allowed, "R=05 00", $"名单里的目标应当回成功。远端输出:\n{allowed}");
        StringAssert.Contains(allowed, "D=PING", $"数据没有经本机的目标原样回来。远端输出:\n{allowed}");
        Assert.IsNull(tunnel.LastError);

        // 被拒之后通道随即关掉:不再往里写(写一个关掉的套接字,bash 会吃 SIGPIPE)。
        string refused = await ssh.RunCommandAsync(SocksClientScript(serverPort, echoPort == 65535 ? 1 : echoPort + 1, payload: null));
        StringAssert.Contains(refused, "R=05 02", $"名单外的目标应当回「规则不允许」。远端输出:\n{refused}");
        await WaitForAsync(() => tunnel.LastError is not null, "被拒的那一条应当记到隧道行上。");
        Assert.AreEqual(Strings.Get("TunnelSvc_TargetNotPermitted"), tunnel.LastError);
    }

    /// <summary>
    /// 远端那头的 SOCKS5 客户端(bash 的 <c>/dev/tcp</c> + 逐字节的 <c>dd</c>,不靠靶机上另装工具):
    /// 协商「不认证」,请求连 <c>127.0.0.1:目标端口</c>,发一段数据,把方法应答、请求应答与回来的数据分别打出来。
    /// <paramref name="payload" /> 为 <see langword="null" /> 时只走到请求应答为止。
    /// </summary>
    private static string SocksClientScript(int proxyPort, int targetPort, string? payload) =>
        $"exec 3<>/dev/tcp/127.0.0.1/{proxyPort}; " +
        "printf '\\x05\\x01\\x00' >&3; " +
        "echo \"M=$(dd bs=1 count=2 <&3 2>/dev/null | od -An -tx1 | tr -s ' ' | sed 's/^ //')\"; " +
        $"printf '\\x05\\x01\\x00\\x01\\x7f\\x00\\x00\\x01\\x{targetPort >> 8:x2}\\x{targetPort & 0xFF:x2}' >&3; " +
        "echo \"R=$(dd bs=1 count=10 <&3 2>/dev/null | od -An -tx1 | tr -s ' ' | sed 's/^ //')\"" +
        (payload is null
            ? string.Empty
            : $"; printf '{payload}' >&3; echo \"D=$(timeout 5 dd bs=1 count={payload.Length} <&3 2>/dev/null)\"");

    /// <summary>本机那头的目标:接一条连接,把读到的转成大写回送,读到对端关闭为止。</summary>
    private async Task ServeUppercaseEchoOnceAsync(TcpListener listener)
    {
        using TcpClient client = await listener.AcceptTcpClientAsync(TestContext.CancellationToken);
        NetworkStream stream = client.GetStream();
        byte[] buffer = new byte[1024];
        int read;
        while ((read = await stream.ReadAsync(buffer, TestContext.CancellationToken)) > 0)
        {
            for (int i = 0; i < read; i++)
            {
                buffer[i] = (byte)char.ToUpperInvariant((char)buffer[i]);
            }
            await stream.WriteAsync(buffer.AsMemory(0, read), TestContext.CancellationToken);
        }
    }

    // ------------------------------------------------------------ 装配

    private static readonly Guid SessionId = Guid.NewGuid();

    /// <summary>一个只认这一条会话、把它交给真实客户端包装的隧道服务。</summary>
    private static TunnelService CreateService(VelaSshClientWrapper ssh)
    {
        ISshConnectionService connections = Substitute.For<ISshConnectionService>();
        connections.GetSession(SessionId).Returns(new SshSession
        {
            SessionId = SessionId,
            Status = SessionStatus.Connected,
            ConnectionInfo = new()
            {
                Host = TestHost,
                Port = TestPort,
                Username = TestUser,
                AuthMethod = AuthMethod.Password
            }
        });
        return new TunnelService(connections, _ => ssh, null, (_, _) => false);
    }

    private async Task<VelaSshClientWrapper> ConnectAsync()
    {
        ConnectionInfo info = new()
        {
            Host = TestHost,
            Port = TestPort,
            Username = TestUser,
            AuthMethod = AuthMethod.Password,
            Password = TestPassword,
        };
        SshConnectionAssembler.Assembled assembled = SshConnectionAssembler.Create(
            info, hostKey: null, settings: null, prompt: null, alerts: null, proxyResolver: null);
        VelaSshClientWrapper wrapper = new(assembled.Connect, assembled.ConnectTimeout, info.Ssh);
        await wrapper.ConnectAsync(TestContext.CancellationToken);
        return wrapper;
    }

    /// <summary>服务端上一个大概率空着的高端口(多条用例、多次运行之间不撞)。</summary>
    private static int RandomServerPort() => 20000 + Random.Shared.Next(0, 20000);

    /// <summary>等某个条件成立(转发器在后台收尾),至多 10 秒。</summary>
    private async Task WaitForAsync(Func<bool> condition, string message)
    {
        DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition())
        {
            Assert.IsLessThanOrEqualTo(deadline, DateTime.UtcNow, message);
            await Task.Delay(50, TestContext.CancellationToken);
        }
    }

    /// <summary>读到对端关闭为止,返回读到的字节数。</summary>
    private static async Task<long> DrainAsync(Stream stream)
    {
        byte[] buffer = new byte[16 * 1024];
        long total = 0;
        int read;
        while ((read = await stream.ReadAsync(buffer)) > 0)
        {
            total += read;
        }
        return total;
    }

    private static void RequireContainer()
    {
        try
        {
            using TcpClient tcp = new();
            if (!tcp.ConnectAsync(TestHost, TestPort).Wait(TimeSpan.FromSeconds(3)))
            {
                Assert.Inconclusive($"[SKIP] ssh-shells 靶机 {TestHost}:{TestPort} 不可达。" +
                                    "运行 'docker compose -f docker-compose.test.yml up -d --build ssh-shells'。");
            }
        }
        catch (Exception ex) when (ex is SocketException or AggregateException)
        {
            Assert.Inconclusive($"[SKIP] ssh-shells 靶机 {TestHost}:{TestPort} 不可达:{ex.Message}");
        }
    }
}
