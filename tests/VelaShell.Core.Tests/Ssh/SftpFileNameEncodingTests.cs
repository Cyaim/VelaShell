using System.Text;
using VelaShell.Core.Models;
using VelaShell.Core.Ssh;
using VelaShell.Infrastructure.Ssh;
using VelaShell.Ssh.Auth;
using VelaShell.Ssh.Crypto;
using VelaShell.Ssh.HostKeys;
using VelaShell.Ssh.Session;
using VelaShell.Ssh.Sftp;
using VelaShell.Ssh.Tests.TestKit;
using VelaShell.Ssh.Transport;

namespace VelaShell.Core.Tests.Ssh;

/// <summary>
/// SFTP 文件名按会话编码解读:会话是 GBK 时,GBK 字节的名字列得出中文、按中文名打得开、删得掉;
/// 会话是 UTF-8 时宿主不把编码交给库,库照旧无损往返(名字显示成转义字符,但拿它回去照样删得掉)。
/// </summary>
/// <remarks>
/// 内存里的 SFTP 服务端按字节记名字(解不开 UTF-8 的字节无损转义成 <c>U+DC80</c>–<c>U+DCFF</c>),
/// 所以「服务端上那串字节」就是 GBK 的原样字节 —— 与一台 LANG=zh_CN.GBK 的老服务器在线上看到的一样。
/// </remarks>
[TestClass]
[TestCategory("Sftp")]
public sealed class SftpFileNameEncodingTests
{
    private const string Home = "/home/joe";
    private const string ChineseName = "中文报告.txt";
    private static readonly byte[] Content = "hello gbk"u8.ToArray();

    [ClassInitialize]
    public static void RegisterCodePages(TestContext _) =>
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("UTF-8")]
    [DataRow("utf8")]
    [DataRow("no-such-charset")]
    public void Utf8BlankOrUnknown_IsNotPassedToTheLibrary(string? name)
    {
        Assert.IsNull(SftpConnectOptions.ResolveFileNameEncoding(name));
        Assert.IsNull(SftpConnectOptions.For(Info(name)), "UTF-8 时用库的默认参数(无损往返)");
        Assert.IsNull(SftpConnectOptions.For(null));
    }

    [TestMethod]
    public void LegacyCharset_IsPassedThrough()
    {
        SftpOptions? options = SftpConnectOptions.For(Info("GBK"));

        Assert.IsNotNull(options);
        Assert.AreEqual(936, options.FileNameEncoding?.CodePage);
        // 其余参数仍是库的默认值,没被顺手改掉。
        Assert.AreEqual(SftpOptions.Default.MaxInFlight, options.MaxInFlight);
        Assert.AreEqual(SftpOptions.Default.HandshakeTimeout, options.HandshakeTimeout);
    }

    [TestMethod]
    public async Task GbkSession_ListsOpensAndDeletesGbkNames()
    {
        Encoding gbk = Encoding.GetEncoding("GBK");
        await using Harness harness = await Harness.StartAsync(SftpConnectOptions.For(Info("GBK")));
        string serverKey = harness.AddRawNameFile(gbk.GetBytes(ChineseName), Content);

        IEnumerable<SftpEntry> listed = await harness.Sftp.ListDirectoryAsync(Home, CancellationToken.None);
        SftpEntry entry = listed.Single(e => e.Name is not "." and not "..");
        Assert.AreEqual(ChineseName, entry.Name, "GBK 会话里列出来的就是中文名");
        Assert.AreEqual($"{Home}/{ChineseName}", entry.FullName);

        // 按中文名打开:编回 GBK 字节,服务端认得出是那个文件。
        await using (Stream stream = await harness.Sftp.OpenAsync(entry.FullName, FileMode.Open, FileAccess.Read))
        {
            using var copy = new MemoryStream();
            await stream.CopyToAsync(copy);
            CollectionAssert.AreEqual(Content, copy.ToArray());
        }

        await harness.Sftp.DeleteFileAsync(entry.FullName);
        Assert.IsFalse(harness.Server.Nodes.ContainsKey(serverKey), "按中文名删掉的必须是服务端那串 GBK 字节的文件");
    }

    /// <summary>UTF-8 会话不传编码:名字显示不出中文,但仍是原样字节的无损往返 —— 打得开、删得掉。</summary>
    [TestMethod]
    public async Task Utf8Session_KeepsTheLosslessRoundTrip()
    {
        Encoding gbk = Encoding.GetEncoding("GBK");
        await using Harness harness = await Harness.StartAsync(SftpConnectOptions.For(Info("UTF-8")));
        string serverKey = harness.AddRawNameFile(gbk.GetBytes(ChineseName), Content);

        IEnumerable<SftpEntry> listed = await harness.Sftp.ListDirectoryAsync(Home, CancellationToken.None);
        SftpEntry entry = listed.Single(e => e.Name is not "." and not "..");
        Assert.AreNotEqual(ChineseName, entry.Name);

        await using (Stream stream = await harness.Sftp.OpenAsync(entry.FullName, FileMode.Open, FileAccess.Read))
        {
            using var copy = new MemoryStream();
            await stream.CopyToAsync(copy);
            CollectionAssert.AreEqual(Content, copy.ToArray());
        }
        await harness.Sftp.DeleteFileAsync(entry.FullName);
        Assert.IsFalse(harness.Server.Nodes.ContainsKey(serverKey));
    }

    private static ConnectionInfo Info(string? encoding) => new()
    {
        Host = "legacy.invalid",
        Username = "joe",
        AuthMethod = AuthMethod.Password,
        FileNameEncoding = encoding,
    };

    /// <summary>握手 → 认证 → 通道 → SFTP 全在内存里跑;SFTP 参数取宿主装配的那一份。</summary>
    private sealed class Harness : IAsyncDisposable
    {
        private readonly TestSshServer _server;
        private readonly TestChannelServer _channelServer;
        private readonly Task _serverChannels;
        private readonly SshConnection _connection;
        private readonly CancellationTokenSource _cts;

        private Harness(TestSshServer server, TestChannelServer channelServer, Task serverChannels,
            SshConnection connection, TestSftpServer sftpServer, VelaSftpClientWrapper sftp, CancellationTokenSource cts)
        {
            _server = server;
            _channelServer = channelServer;
            _serverChannels = serverChannels;
            _connection = connection;
            Server = sftpServer;
            Sftp = sftp;
            _cts = cts;
        }

        public TestSftpServer Server { get; }

        public VelaSftpClientWrapper Sftp { get; }

        /// <summary>
        /// 放一个名字是给定原始字节的文件:服务端按字节记名(解不开 UTF-8 的字节无损转义),这里交回它在服务端的键。
        /// </summary>
        public string AddRawNameFile(byte[] nameBytes, byte[] content)
        {
            string key = $"{Home}/{SftpNameCodec.Utf8.Decode(nameBytes)}";
            Server.AddFile(key, content);
            return key;
        }

        public static async Task<Harness> StartAsync(SftpOptions? options)
        {
            (InMemoryDuplexStream clientStream, InMemoryDuplexStream serverStream) = InMemoryTransport.CreatePair();
            TestSshServer server = new(serverStream);
            SshPacketTransport clientTransport = new(clientStream);
            CancellationTokenSource cts = new(TimeSpan.FromSeconds(30));

            Task<TestSshServerHandshake> serverHandshake = server.HandshakeAsync(cts.Token);
            SshVersionExchangeResult versions = await SshVersionExchange.ExchangeAsync(clientTransport, cancellationToken: cts.Token);
            SshKeyExchangeRunner runner = new(clientTransport, SshAlgorithmSet.Default, new DangerousAcceptAnyHostKeyPolicy());
            SshKeyExchangeResult kex = await runner.RunAsync(versions, "test.invalid", 22, cancellationToken: cts.Token);
            TestSshServerHandshake handshake = await serverHandshake;

            TestAuthServer authServer = new(server.Transport, handshake.ExchangeHash, new TestAuthPolicy { AcceptPassword = "hunter2" });
            Task<bool> serverAuth = authServer.RunAsync(cts.Token);
            SshAuthenticator authenticator = new(clientTransport, "joe", kex.SessionId);
            await authenticator.AuthenticateAsync([new PasswordCredential("hunter2")], cts.Token);
            await serverAuth;

            TestSftpServer sftpServer = new(new TestSftpOptions { WorkingDirectory = Home });
            TestChannelServer channelServer = new(server.Transport, new TestChannelScript { SubsystemHandler = sftpServer.RunAsync });
            Task serverChannels = channelServer.RunAsync(cts.Token);
            SshConnection connection = new(clientTransport, kex);
            connection.Start();
            var sftp = new VelaSftpClientWrapper(ct => SftpFileSystem.ConnectAsync(connection, options, ct));
            await sftp.ConnectAsync(cts.Token);
            return new Harness(server, channelServer, serverChannels, connection, sftpServer, sftp, cts);
        }

        public async ValueTask DisposeAsync()
        {
            await Sftp.DisposeAsync();
            await _cts.CancelAsync();
            await _connection.DisposeAsync();
            try
            {
                await _serverChannels;
            }
            catch (Exception)
            {
                // 收尾时被取消是预期的。
            }
            _channelServer.Dispose();
            await _server.DisposeAsync();
            _cts.Dispose();
        }
    }
}
