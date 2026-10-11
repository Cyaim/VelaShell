// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 被测规格: velashell-docs/zh/ssh/spec/03-key-exchange.md §3.7(mlkem768nistp256-sha256 / mlkem1024nistp384-sha384)
//
// 靶机是 docker-compose.test.yml 的 ssh-pq(AlmaLinux 10.2 的 OpenSSH 9.9p1,RHEL 的下游补丁)。
// RFC 10042 没有测试向量:线上格式与 K 的算法只有对着别人的实现才验得出来。

using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using VelaShell.Ssh.Auth;
using VelaShell.Ssh.Crypto;
using VelaShell.Ssh.Diagnostics;
using VelaShell.Ssh.HostKeys;
using VelaShell.Ssh.Protocol;
using VelaShell.Ssh.Session;

namespace VelaShell.Ssh.Tests.Interop;

public sealed partial class OpenSshInteropTests
{
    /// <summary>ssh-pq 靶机的「FIPS 模式」清单所在端口；另两个 sshd 在它后面的两个端口（只给 1024、RHEL 的 DEFAULT 策略）。</summary>
    private static int PqPort =>
        int.TryParse(Environment.GetEnvironmentVariable("VELASHELL_SSH_INTEROP_PQ_PORT"), out int port) ? port : 2226;

    private static async Task RequirePqServerAsync()
    {
        RequireServer();
        try
        {
            using TcpClient probe = new();
            await probe.ConnectAsync(Host, PqPort).WaitAsync(TimeSpan.FromSeconds(3));
        }
        catch (Exception ex) when (ex is SocketException or TimeoutException)
        {
            Assert.Inconclusive($"ssh-pq 靶机（本机 {PqPort}）连不上：docker compose -f docker-compose.test.yml up -d --build ssh-pq。{ex.Message}");
        }
    }

    private static SshConnectionOptions PqOptions(int port, SshAlgorithmSet algorithms, string? host = null) =>
        new("vela-pq", host ?? Host, port)
        {
            HostKeyPolicy = new DangerousAcceptAnyHostKeyPolicy(),
            Credentials = [new PasswordCredential("velapass")],
            Algorithms = algorithms,
            ConnectTimeout = TimeSpan.FromSeconds(30),
        };

    private static SshAlgorithmSet Only(string kex) => SshAlgorithmSet.Default with { KeyExchange = [kex] };

    /// <summary>重协商一次，等它做完，交回这一次谈成的密钥交换算法。</summary>
    private static async Task<string> RekeyOnceAsync(SshConnection connection, CancellationToken cancellationToken = default)
    {
        TaskCompletionSource<string> done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnRekeyed(object? sender, SshRekeyEventArgs e) => done.TrySetResult(e.Algorithms.KeyExchange);
        connection.Rekeyed += OnRekeyed;
        try
        {
            await connection.StartRekeyAsync(cancellationToken);
            return await done.Task.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken);
        }
        finally
        {
            connection.Rekeyed -= OnRekeyed;
        }
    }

    /// <summary>两种面向 FIPS 的混合各自对真 OpenSSH 握手、跑命令、重协商几次（spec/03 §3.7.7 第 1、2 条）。</summary>
    [TestMethod]
    [DataRow(SshAlgorithmNames.MlKem768Nistp256Sha256)]
    [DataRow(SshAlgorithmNames.MlKem1024Nistp384Sha384)]
    public async Task 面向FIPS的后量子混合对真OpenSSH握手并重协商(string kex)
    {
        await RequirePqServerAsync();
        await using SshConnection connection = await SshConnection.ConnectAsync(PqOptions(PqPort, Only(kex)));

        Assert.AreEqual(kex, connection.Algorithms.KeyExchange);
        Assert.AreEqual("vela-pq", (await connection.RunAsync("id -un")).StandardOutput.Trim());
        for (int i = 0; i < 3; i++)
        {
            Assert.AreEqual(kex, await RekeyOnceAsync(connection), "重协商谈成的还是这一种");
            Assert.AreEqual(0, (await connection.RunAsync("true")).ExitCode, $"第 {i + 1} 次重协商之后照常可用");
        }
    }

    /// <summary>
    /// 每种至少 1200 次交换（重协商比整条连接便宜），全部成功：X 坐标首字节为 0 的概率是 1/256，1200 次一次都碰不到的概率约 0.9% ——
    /// 定长的 <c>K_CL</c> 写错（照 §3.3 的习惯去掉前导零）就会在这里冒出签名失败（spec/03 §3.7.7 第 3 条）。
    /// </summary>
    [TestMethod]
    [Timeout(300_000, CooperativeCancellation = true)]
    [DataRow(SshAlgorithmNames.MlKem768Nistp256Sha256)]
    [DataRow(SshAlgorithmNames.MlKem1024Nistp384Sha384)]
    public async Task 面向FIPS的后量子混合连续交换一千二百次(string kex)
    {
        // 到点时测试框架取消这个令牌,只等方法自己收尾:连接与每一轮重协商都得看着它,否则超时就停不下来。
        CancellationToken token = TestContext.CancellationToken;
        await RequirePqServerAsync();
        await using SshConnection connection = await SshConnection.ConnectAsync(PqOptions(PqPort, Only(kex)), token);

        for (int i = 0; i < 1200; i++)
        {
            Assert.AreEqual(kex, await RekeyOnceAsync(connection, token), $"第 {i + 1} 次重协商");
        }
        Assert.AreEqual(0, (await connection.RunAsync("true")).ExitCode);
    }

    /// <summary>
    /// 默认清单与 FIPS 清单在三种服务端上各谈成哪一种（spec/03 §3.7.7 第 4–6 条）：
    /// 不给 X25519 的（FIPS 模式）、只给 1024 那种的、照 RHEL 的 DEFAULT 策略三种都给的。
    /// </summary>
    [TestMethod]
    public async Task 默认清单与FIPS清单对不同服务端谈成哪一种()
    {
        await RequirePqServerAsync();
        (int Port, string Default, string Fips)[] cases =
        [
            (PqPort, SshAlgorithmNames.MlKem768Nistp256Sha256, SshAlgorithmNames.MlKem768Nistp256Sha256),
            (PqPort + 1, SshAlgorithmNames.MlKem1024Nistp384Sha384, SshAlgorithmNames.MlKem1024Nistp384Sha384),
            (PqPort + 2, SshAlgorithmNames.MlKem768X25519Sha256, SshAlgorithmNames.MlKem768Nistp256Sha256),
        ];

        foreach ((int port, string expectedDefault, string expectedFips) in cases)
        {
            await using (SshConnection viaDefault = await SshConnection.ConnectAsync(PqOptions(port, SshAlgorithmSet.Default)))
            {
                Assert.AreEqual(expectedDefault, viaDefault.Algorithms.KeyExchange, $"端口 {port}：默认清单");
            }
            await using SshConnection viaFips = await SshConnection.ConnectAsync(PqOptions(port, SshAlgorithmSet.FipsApprovedOnly));
            Assert.AreEqual(expectedFips, viaFips.Algorithms.KeyExchange, $"端口 {port}：FIPS 清单");
            Assert.AreEqual(0, (await viaFips.RunAsync("true")).ExitCode);
        }
    }

    /// <summary>
    /// 在客户端与真服务端之间放一个改字节的中继（首次交换是明文，spec/03 §3.7.7 第 8、9 条）：
    /// 不改时记下线上的 <c>C_INIT</c> / <c>S_REPLY</c> 长度（1249 / 1153、1665 / 1665）；
    /// 改 EC 点的一个比特 → 本端 <c>ProtocolError</c>；改 KEM 密文的一个比特 → 交换本身不报错、签名验不过 → <c>HostKeyRejected</c>。
    /// </summary>
    [TestMethod]
    [DataRow(SshAlgorithmNames.MlKem768Nistp256Sha256, 1249, 1153)]
    [DataRow(SshAlgorithmNames.MlKem1024Nistp384Sha384, 1665, 1665)]
    public async Task 面向FIPS的后量子混合经改字节的中继(string kex, int clientInitBytes, int serverReplyBytes)
    {
        await RequirePqServerAsync();

        await using (var relay = KexRelay.Start(Host, PqPort, flipAt: null))
        {
            await using SshConnection connection = await SshConnection.ConnectAsync(PqOptions(relay.Port, Only(kex), "127.0.0.1"));
            Assert.AreEqual(0, (await connection.RunAsync("true")).ExitCode);
            Assert.AreEqual(clientInitBytes, relay.ClientInitBytes, "线上的 C_INIT");
            Assert.AreEqual(serverReplyBytes, relay.ServerReplyBytes, "线上的 S_REPLY");
        }

        await using (var relay = KexRelay.Start(Host, PqPort, flipAt: length => length - 1))
        {
            SshException error = await Assert.ThrowsAsync<SshException>(async () =>
                await SshConnection.ConnectAsync(PqOptions(relay.Port, Only(kex), "127.0.0.1")));
            Assert.AreEqual(SshFailureReason.ProtocolError, error.Reason, $"EC 点被改：{error.Message}");
        }

        await using (var relay = KexRelay.Start(Host, PqPort, flipAt: _ => 10))
        {
            SshException error = await Assert.ThrowsAsync<SshException>(async () =>
                await SshConnection.ConnectAsync(PqOptions(relay.Port, Only(kex), "127.0.0.1")));
            Assert.AreEqual(SshFailureReason.HostKeyRejected, error.Reason, $"KEM 密文被改：{error.Message}");
        }
    }

    /// <summary>
    /// 只转一条连接的 TCP 中继：首次交换（明文）里解出报文，记下客户端 30 号报文的 <c>C_INIT</c> 与服务端 31 号报文的 <c>S_REPLY</c> 长度，
    /// 需要时把 <c>S_REPLY</c> 里某一字节翻一个比特；各自的 <c>NEWKEYS</c> 之后原样搬运。
    /// </summary>
    private sealed class KexRelay : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _relaying;

        private KexRelay(string host, int port, Func<int, int>? flipAt)
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _relaying = Task.Run(() => RelayAsync(host, port, flipAt));
        }

        public int Port { get; }

        public int? ClientInitBytes { get; private set; }

        public int? ServerReplyBytes { get; private set; }

        /// <param name="host">真服务端。</param>
        /// <param name="port">真服务端的端口。</param>
        /// <param name="flipAt">给出 <c>S_REPLY</c> 的长度，交回要翻比特的那个字节在 <c>S_REPLY</c> 里的位置；<see langword="null"/> 不改。</param>
        public static KexRelay Start(string host, int port, Func<int, int>? flipAt) => new(host, port, flipAt);

        private async Task RelayAsync(string host, int port, Func<int, int>? flipAt)
        {
            try
            {
                using TcpClient client = await _listener.AcceptTcpClientAsync(_stop.Token);
                using TcpClient server = new();
                await server.ConnectAsync(host, port, _stop.Token);
                NetworkStream fromClient = client.GetStream(), toServer = server.GetStream();

                Task up = PumpAsync(fromClient, toServer, isFromServer: false, flipAt: null);
                Task down = PumpAsync(toServer, fromClient, isFromServer: true, flipAt);
                await Task.WhenAny(up, down);
            }
            catch (Exception)
            {
                // 连接被一端关掉、或者用例收工：中继到此为止。
            }
        }

        private async Task PumpAsync(NetworkStream from, NetworkStream to, bool isFromServer, Func<int, int>? flipAt)
        {
            try
            {
                // 标识行：读到以 SSH- 开头的那一行为止（服务端可能先发别的行），原样转过去。
                while (true)
                {
                    List<byte> line = [];
                    byte[] one = new byte[1];
                    while (await from.ReadAsync(one, _stop.Token) == 1)
                    {
                        line.Add(one[0]);
                        if (one[0] == (byte)'\n')
                        {
                            break;
                        }
                    }
                    await to.WriteAsync(line.ToArray(), _stop.Token);
                    if (line.Count == 0 || Encoding.ASCII.GetString([.. line]).StartsWith("SSH-", StringComparison.Ordinal))
                    {
                        break;
                    }
                }

                // 首次交换：明文的二进制报文，NEWKEYS 之后就是密文 —— 从那里起原样搬。
                while (true)
                {
                    byte[] header = new byte[4];
                    await from.ReadExactlyAsync(header, _stop.Token);
                    byte[] body = new byte[BinaryPrimitives.ReadUInt32BigEndian(header)];
                    await from.ReadExactlyAsync(body, _stop.Token);

                    int payload = 1;   // body[0] 是填充长度
                    byte message = body[payload];
                    if (!isFromServer && message == 30)
                    {
                        ClientInitBytes = (int)BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(payload + 1));
                    }
                    else if (isFromServer && message == 31)
                    {
                        int hostKeyLength = (int)BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(payload + 1));
                        int replyAt = payload + 1 + 4 + hostKeyLength;
                        int replyLength = (int)BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(replyAt));
                        ServerReplyBytes = replyLength;
                        if (flipAt is not null)
                        {
                            body[replyAt + 4 + flipAt(replyLength)] ^= 0x01;
                        }
                    }

                    await to.WriteAsync(header, _stop.Token);
                    await to.WriteAsync(body, _stop.Token);
                    if (message == 21)
                    {
                        break;
                    }
                }

                await from.CopyToAsync(to, _stop.Token);
            }
            catch (Exception)
            {
                // 一端断了：另一端随中继一起关。
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            _listener.Stop();
            await _relaying;
            _stop.Dispose();
        }
    }
}
