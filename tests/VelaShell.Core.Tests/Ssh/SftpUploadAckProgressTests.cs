using System.Collections.Concurrent;
using System.Security.Cryptography;
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
/// 上传进度只跟着对端的 WRITE 确认走(#617)。<see cref="SftpUploadProgressIntegrationTests" /> 要真 OpenSSH 与账号,
/// CI 上永远跳过;这里换成内存里的 SFTP 服务端,由测试决定每条确认什么时候到 —— 包装层按 <c>DurableLength</c>
/// 采样、源读完后 Flush 期间每 200ms 补报这两件事,每次提交都验。
/// </summary>
[TestClass]
[TestCategory("Sftp")]
public sealed class SftpUploadAckProgressTests
{
    private const int BlockSize = 32 * 1024;
    private const int Blocks = 8;
    private const string RemotePath = "/home/joe/ack.bin";
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);

    [TestMethod]
    public async Task Upload_ProgressAdvancesOnlyWhenWriteAcksArrive()
    {
        await using Harness harness = await Harness.StartAsync();
        byte[] data = RandomNumberGenerator.GetBytes(Blocks * BlockSize);
        using var source = new ObservedSource(data);
        var samples = new ConcurrentQueue<(ulong Bytes, bool AfterEof)>();
        Task upload = harness.Sftp.UploadAsync(source, RemotePath, bytes => samples.Enqueue((bytes, source.Eof.IsCompleted)));

        // 源读完、整份数据都已作为 WRITE 发出,一条确认都没放。
        await source.Eof.WaitAsync(Deadline);
        await EventuallyAsync(() => harness.SftpServer.HeldWriteReplies == Blocks);
        // Flush 等确认期间要按 200ms 补报,不能一直不出声。不按固定时长数拍数:慢 runner 上 500ms 里只排得上一拍
        // (macOS CI 实测),没有补报时这里会一直等到 Deadline 超时失败。
        await EventuallyAsync(() => samples.Count(static s => s.AfterEof) >= 2);
        Assert.IsFalse(upload.IsCompleted, "确认没到,上传不能完成。");
        Assert.IsTrue(samples.All(static s => s.Bytes == 0), "已发出、未确认的字节不能算进度。");

        // 放行三条:进度停在三块,不多不少;这一拍来自 Flush 的采样(源早就读完了)。
        Assert.AreEqual(3, harness.SftpServer.ReleaseHeldWrites(3));
        await EventuallyAsync(() => samples.Any(static s => s.Bytes == 3 * BlockSize));
        await Task.Delay(500);
        Assert.IsFalse(upload.IsCompleted);
        Assert.AreEqual((ulong)(3 * BlockSize), samples.Max(static s => s.Bytes), "进度只能到已确认的那三块。");

        // 全部放行:上传完成,最后一帧是全量,全程单调不回退。
        Assert.AreEqual(Blocks - 3, harness.SftpServer.ReleaseHeldWrites());
        await upload.WaitAsync(Deadline);
        ulong[] reported = [.. samples.Select(static s => s.Bytes)];
        Assert.AreEqual((ulong)data.Length, reported[^1]);
        for (int i = 1; i < reported.Length; i++)
        {
            Assert.IsGreaterThanOrEqualTo(reported[i - 1], reported[i], "进度不能回退。");
        }
        Assert.AreSequenceEqual(data, [.. harness.SftpServer.Nodes[RemotePath].Content]);
    }

    private static async Task EventuallyAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(Deadline);
        while (!condition())
        {
            await Task.Delay(10, timeout.Token);
        }
    }

    /// <summary>记下源什么时候读到结尾:之后的回调都来自 Flush 的定时采样。</summary>
    private sealed class ObservedSource(byte[] data) : MemoryStream(data, writable: false)
    {
        private readonly TaskCompletionSource _eof = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Eof => _eof.Task;

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int read = Read(buffer.Span);
            if (read == 0)
            {
                _eof.TrySetResult();
            }
            return ValueTask.FromResult(read);
        }
    }

    /// <summary>握手 → 认证 → 通道 → SFTP 全在内存里跑;服务端扣住每个 WRITE 的确认。</summary>
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
            SftpServer = sftpServer;
            Sftp = sftp;
            _cts = cts;
        }

        public TestSftpServer SftpServer { get; }

        public VelaSftpClientWrapper Sftp { get; }

        public static async Task<Harness> StartAsync()
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

            TestSftpServer sftpServer = new(new TestSftpOptions { HoldWriteReplies = true });
            TestChannelServer channelServer = new(server.Transport, new TestChannelScript { SubsystemHandler = sftpServer.RunAsync });
            Task serverChannels = channelServer.RunAsync(cts.Token);

            SshConnection connection = new(clientTransport, kex);
            connection.Start();
            // 与真 OpenSSH 那组用例同一套参数:固定 32 KiB 一块、不自适应,整份数据能一次全部在途。
            var sftp = new VelaSftpClientWrapper(ct => SftpFileSystem.ConnectAsync(connection, new SftpOptions
            {
                BlockSize = BlockSize,
                MaxInFlight = 64,
                IsPipelineDepthAdaptive = false,
                CloseTimeout = TimeSpan.FromSeconds(3),
            }, ct));
            await sftp.ConnectAsync(cts.Token);
            return new Harness(server, channelServer, serverChannels, connection, sftpServer, sftp, cts);
        }

        public async ValueTask DisposeAsync()
        {
            // 用例中途失败时服务端还扣着确认:先全放掉,关流才不会等满 CloseTimeout。
            SftpServer.ReleaseHeldWrites();
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
