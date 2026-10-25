using NSubstitute;
using NSubstitute.ExceptionExtensions;
using VelaShell.Core.Models;
using VelaShell.Core.Sftp;
using VelaShell.Core.Ssh;

namespace VelaShell.Core.Tests.Sftp;

/// <summary>
/// 双栏远程文档的跨会话中转:源端顺序读,目标端边收边写,中间不落盘。
/// </summary>
[TestClass]
[TestCategory("Sftp")]
public class RemoteRelayTests
{
    private static readonly Guid SourceSession = Guid.NewGuid();
    private static readonly Guid TargetSession = Guid.NewGuid();

    [TestMethod]
    public async Task CopyFile_StreamsSourceIntoTarget_WithSizeAndMtime()
    {
        byte[] payload = [.. Enumerable.Range(0, 300_000).Select(static i => (byte)(i * 31))];
        var mtime = new DateTime(2026, 9, 1, 8, 30, 0, DateTimeKind.Utc);
        ISftpService source = Substitute.For<ISftpService>();
        source.GetFileInfoAsync(SourceSession, "/a/data.bin", Arg.Any<CancellationToken>())
              .Returns(RemoteFile("/a/data.bin", payload.Length, mtime));
        source.OpenReadAsync(SourceSession, "/a/data.bin", Arg.Any<CancellationToken>())
              .Returns(_ => Task.FromResult<Stream>(new MemoryStream(payload)));

        byte[]? received = null;
        ISftpService target = Substitute.For<ISftpService>();
        target.UploadStreamAsync(TargetSession, Arg.Any<Stream>(), "/b/data.bin", payload.Length, mtime,
                  Arg.Any<IProgress<TransferProgress>?>(), Arg.Any<long>(), Arg.Any<uint?>(), Arg.Any<CancellationToken>())
              .Returns(call =>
              {
                  using var copy = new MemoryStream();
                  call.ArgAt<Stream>(1).CopyTo(copy);
                  received = copy.ToArray();
                  return Task.CompletedTask;
              });

        await RemoteRelay.CopyFileAsync(source, SourceSession, "/a/data.bin", target, TargetSession, "/b/data.bin");

        Assert.IsNotNull(received, "目标端必须收到一次从流上传(大小与修改时间要原样带过去)。");
        Assert.AreSequenceEqual(payload, received);
    }

    [TestMethod]
    public async Task CopyFile_WhenSourceIsGone_NeverTouchesTheTarget()
    {
        // 源在规划之后被删掉:失败发生在目标被碰之前。调用方据「writing 有没有回调」决定要不要清半截文件 ——
        // 这里回调了,就会把用户原有的同名目标当半截文件删掉。
        ISftpService source = Substitute.For<ISftpService>();
        source.GetFileInfoAsync(SourceSession, "/a/gone.txt", Arg.Any<CancellationToken>())
              .ThrowsAsync(new FileNotFoundException("gone"));
        ISftpService target = Substitute.For<ISftpService>();
        bool writing = false;

        await Assert.ThrowsExactlyAsync<FileNotFoundException>(() => RemoteRelay.CopyFileAsync(
            source, SourceSession, "/a/gone.txt", target, TargetSession, "/b/gone.txt", writing: () => writing = true));

        Assert.IsFalse(writing);
        await target.DidNotReceiveWithAnyArgs().UploadStreamAsync(default, null!, null!, 0);
    }

    [TestMethod]
    public async Task CopyFile_DisposesTheSourceStream()
    {
        var stream = new TrackingStream([1, 2, 3]);
        ISftpService source = Substitute.For<ISftpService>();
        source.GetFileInfoAsync(SourceSession, "/a/x", Arg.Any<CancellationToken>())
              .Returns(RemoteFile("/a/x", 3, default));
        source.OpenReadAsync(SourceSession, "/a/x", Arg.Any<CancellationToken>()).Returns(Task.FromResult<Stream>(stream));
        ISftpService target = Substitute.For<ISftpService>();

        await RemoteRelay.CopyFileAsync(source, SourceSession, "/a/x", target, TargetSession, "/b/x");

        Assert.IsTrue(stream.Disposed, "源端的流(FTP 上还连着一条数据连接)必须在中转结束后释放。");
    }

    [TestMethod]
    public async Task SftpUploadStream_WritesThroughTheClient_AndLeavesTheCallersStreamOpen()
    {
        ISshConnectionService connections = Substitute.For<ISshConnectionService>();
        ISftpClientWrapper client = Substitute.For<ISftpClientWrapper>();
        client.IsConnected.Returns(true);
        connections.GetSession(TargetSession).Returns(new SshSession
        {
            SessionId = TargetSession,
            ConnectionInfo = new() { Host = "h", Port = 22, Username = "u", AuthMethod = AuthMethod.Password, Password = "p" },
            Status = SessionStatus.Connected,
        });
        byte[]? written = null;
        // 没有设置服务时按默认「保留时间戳」:走关闭之前按句柄设修改时间的那个重载。
        client.UploadAsync(Arg.Any<Stream>(), "/b/y.txt", Arg.Any<RemoteUploadOptions>(), Arg.Any<Action<ulong>?>(), Arg.Any<CancellationToken>())
              .Returns(call =>
              {
                  using var copy = new MemoryStream();
                  call.ArgAt<Stream>(0).CopyTo(copy);
                  written = copy.ToArray();
                  return Task.CompletedTask;
              });
        var mtime = new DateTime(2026, 9, 2, 1, 2, 3, DateTimeKind.Utc);
        var service = new SftpService(connections, _ => client);
        var source = new TrackingStream([9, 8, 7, 6]);

        await service.UploadStreamAsync(TargetSession, source, "/b/y.txt", 4, mtime);

        Assert.AreSequenceEqual(new byte[] { 9, 8, 7, 6 }, written);
        Assert.IsFalse(source.Disposed, "流归调用方(契约),服务不能替它关掉。");
        // 目标的修改时间对齐源文件。
        await client.Received(1).UploadAsync(
            Arg.Any<Stream>(), "/b/y.txt",
            Arg.Is<RemoteUploadOptions>(o => o.ResumeOffset == 0 && o.LastWriteTime!.Value.UtcDateTime == mtime && !o.Fsync),
            Arg.Any<Action<ulong>?>(), Arg.Any<CancellationToken>());
    }

    [TestMethod]
    public async Task CopyFile_PassesTheResumePoint_WhenTheSourceCanSeek()
    {
        ISftpService source = Substitute.For<ISftpService>();
        source.GetFileInfoAsync(SourceSession, "/a/x", Arg.Any<CancellationToken>()).Returns(RemoteFile("/a/x", 10, default));
        source.OpenReadAsync(SourceSession, "/a/x", Arg.Any<CancellationToken>())
              .Returns(_ => Task.FromResult<Stream>(new MemoryStream(new byte[10])));
        ISftpService target = Substitute.For<ISftpService>();

        await RemoteRelay.CopyFileAsync(source, SourceSession, "/a/x", target, TargetSession, "/b/x", resumeOffset: 4);

        await target.Received(1).UploadStreamAsync(TargetSession, Arg.Any<Stream>(), "/b/x", 10, Arg.Any<DateTime?>(),
            Arg.Any<IProgress<TransferProgress>?>(), 4, Arg.Any<uint?>(), Arg.Any<CancellationToken>());
    }

    [TestMethod]
    public async Task CopyFile_FromAnUnseekableSource_AskedToResume_ReportsItCannotVerify()
    {
        // FTP 的读流是一条顺序的数据连接:回不了头,核实不了目标上那个较短的同名文件是不是上一次的半截。
        // 悄悄整份重传等于不经询问覆盖它 —— 要按「对不上」报,交回同名冲突策略,且目标一个字节都不能碰。
        ISftpService source = Substitute.For<ISftpService>();
        source.GetFileInfoAsync(SourceSession, "/a/x", Arg.Any<CancellationToken>()).Returns(RemoteFile("/a/x", 10, default));
        source.OpenReadAsync(SourceSession, "/a/x", Arg.Any<CancellationToken>())
              .Returns(_ => Task.FromResult<Stream>(new SequentialStream(new byte[10])));
        ISftpService target = Substitute.For<ISftpService>();
        bool writing = false;

        await Assert.ThrowsExactlyAsync<VelaSftpResumeMismatchException>(() => RemoteRelay.CopyFileAsync(
            source, SourceSession, "/a/x", target, TargetSession, "/b/x", writing: () => writing = true, resumeOffset: 4));

        Assert.IsFalse(writing, "目标没被碰过,不能被当成半截文件清掉。");
        await target.DidNotReceiveWithAnyArgs().UploadStreamAsync(default, null!, null!, 0);
    }

    [TestMethod]
    public async Task CopyFile_FromAnUnseekableSource_WithoutResume_StreamsNormally()
    {
        ISftpService source = Substitute.For<ISftpService>();
        source.GetFileInfoAsync(SourceSession, "/a/x", Arg.Any<CancellationToken>()).Returns(RemoteFile("/a/x", 10, default));
        source.OpenReadAsync(SourceSession, "/a/x", Arg.Any<CancellationToken>())
              .Returns(_ => Task.FromResult<Stream>(new SequentialStream(new byte[10])));
        ISftpService target = Substitute.For<ISftpService>();

        await RemoteRelay.CopyFileAsync(source, SourceSession, "/a/x", target, TargetSession, "/b/x");

        await target.Received(1).UploadStreamAsync(TargetSession, Arg.Any<Stream>(), "/b/x", 10, Arg.Any<DateTime?>(),
            Arg.Any<IProgress<TransferProgress>?>(), 0, Arg.Any<uint?>(), Arg.Any<CancellationToken>());
    }

    [TestMethod]
    public async Task SftpUploadStream_Resume_VerifiesTheTailAndContinuesFromTheSafePoint()
    {
        (SftpService service, ISftpClientWrapper client) = SftpTarget();
        byte[] whole = [.. Enumerable.Range(0, 100).Select(static i => (byte)i)];
        // 目标上已有前 60 字节;安全回退 10 字节 → 从 50 接着写。
        client.GetFileSizeAsync("/b/big.bin", Arg.Any<CancellationToken>()).Returns(60L);
        client.ResumeSafetyMargin.Returns(10L);
        client.OpenAsync("/b/big.bin", FileMode.Open, FileAccess.Read, Arg.Any<CancellationToken>())
              .Returns(_ => Task.FromResult<Stream>(new MemoryStream(whole[..60])));
        long? resumedAt = null;
        long? sourcePosition = null;
        client.UploadAsync(Arg.Any<Stream>(), "/b/big.bin", Arg.Any<long>(), Arg.Any<Action<ulong>?>(), Arg.Any<CancellationToken>())
              .Returns(call =>
              {
                  resumedAt = call.ArgAt<long>(2);
                  sourcePosition = call.ArgAt<Stream>(0).Position;
                  return Task.CompletedTask;
              });

        await service.UploadStreamAsync(TargetSession, new MemoryStream(whole), "/b/big.bin", whole.Length, resumeOffset: 60);

        Assert.AreEqual(50L, resumedAt, "起点取此刻的远端长度再回退一个在途写入窗口。");
        Assert.IsNotNull(sourcePosition);
        await client.DidNotReceive().UploadAsync(Arg.Any<Stream>(), "/b/big.bin", Arg.Any<Action<ulong>?>(), Arg.Any<CancellationToken>());
    }

    [TestMethod]
    public async Task SftpUploadStream_Resume_WhenTheTargetIsNotAPrefixOfTheSource_Throws()
    {
        (SftpService service, ISftpClientWrapper client) = SftpTarget();
        byte[] whole = [.. Enumerable.Range(0, 100).Select(static i => (byte)i)];
        client.GetFileSizeAsync("/b/big.bin", Arg.Any<CancellationToken>()).Returns(60L);
        client.ResumeSafetyMargin.Returns(10L);
        client.OpenAsync("/b/big.bin", FileMode.Open, FileAccess.Read, Arg.Any<CancellationToken>())
              .Returns(_ => Task.FromResult<Stream>(new MemoryStream(new byte[60])));

        await Assert.ThrowsExactlyAsync<VelaSftpResumeMismatchException>(() =>
            service.UploadStreamAsync(TargetSession, new MemoryStream(whole), "/b/big.bin", whole.Length, resumeOffset: 60));
        await client.DidNotReceiveWithAnyArgs().UploadAsync(null!, null!, 0L);
    }

    [TestMethod]
    public async Task SftpUploadStream_Resume_WhenNothingUsableIsThere_StartsOverFromTheBeginning()
    {
        (SftpService service, ISftpClientWrapper client) = SftpTarget();
        client.GetFileSizeAsync("/b/big.bin", Arg.Any<CancellationToken>()).Returns(-1L);
        long? startPosition = null;
        client.UploadAsync(Arg.Any<Stream>(), "/b/big.bin", Arg.Any<Action<ulong>?>(), Arg.Any<CancellationToken>())
              .Returns(call =>
              {
                  startPosition = call.ArgAt<Stream>(0).Position;
                  return Task.CompletedTask;
              });
        var source = new MemoryStream(new byte[100]) { Position = 37 };

        await service.UploadStreamAsync(TargetSession, source, "/b/big.bin", 100, resumeOffset: 60);

        Assert.AreEqual(0L, startPosition, "目标已经不在了就整份重传,从源的开头读。");
    }

    [TestMethod]
    public async Task SftpUploadStream_Resume_RequiresASeekableSource()
    {
        (SftpService service, _) = SftpTarget();

        await Assert.ThrowsExactlyAsync<ArgumentException>(() =>
            service.UploadStreamAsync(TargetSession, new SequentialStream(new byte[10]), "/b/x", 10, resumeOffset: 4));
    }

    private static (SftpService Service, ISftpClientWrapper Client) SftpTarget()
    {
        ISshConnectionService connections = Substitute.For<ISshConnectionService>();
        ISftpClientWrapper client = Substitute.For<ISftpClientWrapper>();
        client.IsConnected.Returns(true);
        connections.GetSession(TargetSession).Returns(new SshSession
        {
            SessionId = TargetSession,
            ConnectionInfo = new() { Host = "h", Port = 22, Username = "u", AuthMethod = AuthMethod.Password, Password = "p" },
            Status = SessionStatus.Connected,
        });
        return (new SftpService(connections, _ => client), client);
    }

    /// <summary>只能顺序读的流(像 FTP 的数据连接)。</summary>
    private sealed class SequentialStream(byte[] data) : MemoryStream(data)
    {
        public override bool CanSeek => false;
    }

    private static RemoteFileInfo RemoteFile(string path, long size, DateTime modified) => new()
    {
        Name = path[(path.LastIndexOf('/') + 1)..],
        FullPath = path,
        Size = size,
        LastModified = modified,
        Permissions = "-rw-r--r--",
        IsDirectory = false,
        Owner = "u",
        Group = "g",
    };

    /// <summary>记下自己有没有被释放的内存流。</summary>
    private sealed class TrackingStream(byte[] data) : MemoryStream(data)
    {
        public bool Disposed { get; private set; }

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }
}
