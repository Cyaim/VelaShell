using VelaShell.Core.Sftp;
using VelaShell.Core.Ssh;
using VelaShell.Infrastructure.Ssh;
using VelaShell.Ssh.Auth;
using VelaShell.Ssh.Crypto;
using VelaShell.Ssh.Diagnostics;
using VelaShell.Ssh.HostKeys;
using VelaShell.Ssh.Session;
using VelaShell.Ssh.Sftp;
using VelaShell.Ssh.Tests.TestKit;
using VelaShell.Ssh.Transport;

namespace VelaShell.Core.Tests.Ssh;

/// <summary>
/// <c>VelaSftpClientWrapper.OpenAsync</c> 交出的远端文件流:读写失败时抛的是 Core 的中立异常,
/// 失败重试(<see cref="TransferRetryPolicy" />)认得出断线 —— 续传下载、双栏中转都是自己在循环里读这条流。
/// </summary>
/// <remarks>
/// 以前这条流是库的 <c>SftpFileStream</c> 原样交出去,断线时冒出库自己的异常,按永久失败处理:
/// 一个下载续传过一次之后再断,就不再重试,也不挂「已暂停」而是标红。
/// </remarks>
[TestClass]
[TestCategory("Sftp")]
public sealed class SftpStreamTranslationTests
{
    private const string RemotePath = "/home/joe/big.bin";

    /// <summary>读到一半连接没了:流上抛的是中立异常,按临时 / 会话已不在处理,不是永久失败。</summary>
    [TestMethod]
    public async Task ConnectionDropsMidRead_StreamThrowsANeutralException_ThatRetryTreatsAsTransient()
    {
        await using Harness harness = await Harness.StartAsync();
        harness.Server.AddFile(RemotePath, new byte[4 * 1024 * 1024]);
        await using Stream stream = await harness.Sftp.OpenAsync(RemotePath, FileMode.Open, FileAccess.Read);
        Assert.IsTrue(stream.CanSeek, "续传核实要来回定位,包了一层之后仍得可 Seek");
        byte[] buffer = new byte[64 * 1024];
        Assert.IsGreaterThan(0, await stream.ReadAsync(buffer));

        await harness.DropConnectionAsync();

        Exception failure = await ReadUntilFailureAsync(stream, buffer);
        Assert.DoesNotStartWith("VelaShell.Ssh", failure.GetType().Namespace ?? string.Empty,
            $"库的异常类型漏出来了:{failure.GetType().FullName}");
        Assert.AreNotEqual(TransferFailureKind.Permanent, TransferRetryPolicy.Classify(failure),
            $"断线被当成了永久失败:{failure.GetType().FullName}: {failure.Message}");
    }

    [TestMethod]
    public async Task LibraryFailures_OnEveryAsyncPath_AreTranslated()
    {
        var closed = new SshConnectionClosedException(SshFailureReason.ClosedByPeer, SshPhase.Open, "连接已经关闭");
        await using var stream = new TranslatingSftpStream(new FailingStream(closed));

        await Assert.ThrowsExactlyAsync<VelaSshConnectionException>(async () => _ = await stream.ReadAsync(new byte[4].AsMemory()));
        await Assert.ThrowsExactlyAsync<VelaSshConnectionException>(() => stream.ReadAsync(new byte[4], 0, 4, CancellationToken.None));
        await Assert.ThrowsExactlyAsync<VelaSshConnectionException>(async () => await stream.WriteAsync(new byte[4].AsMemory()));
        await Assert.ThrowsExactlyAsync<VelaSshConnectionException>(() => stream.WriteAsync(new byte[4], 0, 4, CancellationToken.None));
        await Assert.ThrowsExactlyAsync<VelaSshConnectionException>(() => stream.FlushAsync());
        Assert.ThrowsExactly<VelaSshConnectionException>(() => stream.Seek(0, SeekOrigin.Begin));
        // CopyToAsync / 哈希走的是 ReadAsync(Memory) —— 同一条路。
        await Assert.ThrowsExactlyAsync<VelaSshConnectionException>(() => stream.CopyToAsync(Stream.Null));
    }

    /// <summary>关流(等在途写入落地)时报的中断带着续传点,翻过去之后上层照样拿得到。</summary>
    [TestMethod]
    public async Task InterruptedClose_KeepsTheDurableLength()
    {
        var interrupted = new SftpTransferInterruptedException(4096, "中断");
        var stream = new TranslatingSftpStream(new FailingStream(interrupted));

        VelaSftpTransferInterruptedException failure =
            await Assert.ThrowsExactlyAsync<VelaSftpTransferInterruptedException>(async () => await stream.DisposeAsync());

        Assert.AreEqual(4096, failure.DurableLength);
        Assert.AreEqual(TransferFailureKind.Transient, TransferRetryPolicy.Classify(failure));
    }

    /// <summary>不是库的异常原样抛:调用方自己的取消、只读流上的写、流已关。</summary>
    [TestMethod]
    public async Task NonLibraryFailures_AndCallerCancellation_PassThroughUnchanged()
    {
        await using var notSupported = new TranslatingSftpStream(new FailingStream(new NotSupportedException("只读")));
        await Assert.ThrowsExactlyAsync<NotSupportedException>(async () => await notSupported.WriteAsync(new byte[1].AsMemory()));

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await using var cancelled = new TranslatingSftpStream(new FailingStream(new OperationCanceledException(cts.Token)));
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () => _ = await cancelled.ReadAsync(new byte[1].AsMemory(), cts.Token));

        // 调用方没取消、库却收到取消 = 库内部超时,与包装器其它方法一样翻成超时(可重试)。
        await using var timedOut = new TranslatingSftpStream(new FailingStream(new OperationCanceledException()));
        await Assert.ThrowsExactlyAsync<VelaSshOperationTimeoutException>(async () => _ = await timedOut.ReadAsync(new byte[1].AsMemory()));
    }

    private static async Task<Exception> ReadUntilFailureAsync(Stream stream, byte[] buffer)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try
        {
            while (await stream.ReadAsync(buffer, deadline.Token) > 0)
            {
            }
        }
        catch (Exception ex) when (!deadline.IsCancellationRequested)
        {
            return ex;
        }
        Assert.Fail("连接断了,4 MiB 的文件却整份读完了 —— 预读窗口比文件还大,用例没测到断线");
        return null!;
    }

    /// <summary>每个操作都抛同一个异常的流;长度、可读写、可定位照常回答。</summary>
    private sealed class FailingStream(Exception failure) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => true;
        public override long Length => 10;
        public override long Position { get => 0; set => throw failure; }
        public override void Flush() => throw failure;
        public override Task FlushAsync(CancellationToken cancellationToken) => Task.FromException(failure);
        public override int Read(byte[] buffer, int offset, int count) => throw failure;
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromException<int>(failure);
        public override long Seek(long offset, SeekOrigin origin) => throw failure;
        public override void SetLength(long value) => throw failure;
        public override void Write(byte[] buffer, int offset, int count) => throw failure;
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromException(failure);
        public override ValueTask DisposeAsync() =>
            failure is SftpTransferInterruptedException ? ValueTask.FromException(failure) : ValueTask.CompletedTask;
        protected override void Dispose(bool disposing) => base.Dispose(disposing);
    }

    /// <summary>握手 → 认证 → 通道 → SFTP 全在内存里跑;能从服务端一侧掐断连接。</summary>
    private sealed class Harness : IAsyncDisposable
    {
        private readonly TestSshServer _server;
        private readonly TestChannelServer _channelServer;
        private readonly Task _serverChannels;
        private readonly SshConnection _connection;
        private readonly CancellationTokenSource _cts;
        private bool _dropped;

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

        public static async Task<Harness> StartAsync()
        {
            (InMemoryDuplexStream clientStream, InMemoryDuplexStream serverStream) = InMemoryTransport.CreatePair();
            TestSshServer server = new(serverStream);
            SshPacketTransport clientTransport = new(clientStream);
            CancellationTokenSource cts = new(TimeSpan.FromSeconds(60));

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

            TestSftpServer sftpServer = new(new TestSftpOptions());
            TestChannelServer channelServer = new(server.Transport, new TestChannelScript { SubsystemHandler = sftpServer.RunAsync });
            Task serverChannels = channelServer.RunAsync(cts.Token);
            SshConnection connection = new(clientTransport, kex);
            connection.Start();
            // 小块、少量在途:预读窗口只有 64 KiB,断线时远远没读完。
            var sftp = new VelaSftpClientWrapper(ct => SftpFileSystem.ConnectAsync(connection, new SftpOptions
            {
                BlockSize = 32 * 1024,
                MaxInFlight = 2,
                IsPipelineDepthAdaptive = false,
            }, ct));
            await sftp.ConnectAsync(cts.Token);
            return new Harness(server, channelServer, serverChannels, connection, sftpServer, sftp, cts);
        }

        /// <summary>从服务端一侧掐断:像对端主机掉线那样,客户端只看到连接没了。</summary>
        public async Task DropConnectionAsync()
        {
            _dropped = true;
            await _server.DisposeAsync();
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
                // 收尾时被取消、连接被掐断都是预期的。
            }
            _channelServer.Dispose();
            if (!_dropped)
            {
                await _server.DisposeAsync();
            }
            _cts.Dispose();
        }
    }
}
