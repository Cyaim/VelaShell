using System.Buffers;
using System.Reflection;
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
        ISftpService target = ConsumingTarget();

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
        ISftpService target = ConsumingTarget();

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
        ISftpService target = ConsumingTarget();

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

    [TestMethod]
    [DataRow(0)]
    [DataRow(4)]
    [DataRow(11)]
    public async Task CopyFile_WhenSourceLengthDoesNotMatch_RejectsSuccess(int streamLength)
    {
        (ISftpService source, ISftpService target) = RelayServices(10, new SequentialStream(new byte[streamLength]));
        bool writing = false;

        RemoteTransferLengthException error = await Assert.ThrowsExactlyAsync<RemoteTransferLengthException>(() =>
            RemoteRelay.CopyFileAsync(source, SourceSession, "/a/x", target, TargetSession, "/b/x", writing: () => writing = true));
        Assert.AreEqual(10L, error.ExpectedBytes);
        Assert.AreEqual(streamLength, error.ActualBytes);
        Assert.IsFalse(writing, "Small source length checks must precede target writes.");
        await target.DidNotReceiveWithAnyArgs().UploadStreamAsync(default, null!, null!, 0);
    }

    /// <summary>
    /// 长度未知(-1:服务器不支持 SIZE)的源不能被当成空文件。以前整份缓存分支把 limit 算成负数,
    /// 一块都不读就报 EOF,目标收到 0 字节 —— 这正是 FTP→SFTP 中转成片失败的那种形态。
    /// </summary>
    [TestMethod]
    [DataRow(7)]
    [DataRow(4 * 1024 * 1024 + 5)]
    public async Task CopyFile_UnknownSourceLength_TransfersEveryByte(int streamLength)
    {
        (ISftpService source, ISftpService target) = RelayServices(-1, new SequentialStream(new byte[streamLength]));
        long received = -1;
        target.UploadStreamAsync(TargetSession, Arg.Any<Stream>(), "/b/x", Arg.Any<long>(), Arg.Any<DateTime?>(),
                Arg.Any<IProgress<TransferProgress>?>(), Arg.Any<long>(), Arg.Any<uint?>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                using var sink = new MemoryStream();
                await call.ArgAt<Stream>(1).CopyToAsync(sink);
                received = sink.Length;
            });

        await RemoteRelay.CopyFileAsync(source, SourceSession, "/a/x", target, TargetSession, "/b/x")
            .WaitAsync(TimeSpan.FromSeconds(20));

        Assert.AreEqual(streamLength, received, "长度未知时以源的 EOF 为准,一个字节都不能少。");
    }

    [TestMethod]
    public async Task CopyFile_WhenTargetDoesNotConsumeSource_RejectsSuccess()
    {
        (ISftpService source, _) = RelayServices(10, new SequentialStream(new byte[10]));
        ISftpService target = Substitute.For<ISftpService>();

        await Assert.ThrowsAsync<IOException>(() =>
            RemoteRelay.CopyFileAsync(source, SourceSession, "/a/x", target, TargetSession, "/b/x"));
    }

    [TestMethod]
    [DataRow(0, 0)]
    [DataRow(0, 10)]
    [DataRow(10, 10)]
    public async Task CopyFile_EmptyUnknownAndMatchingLengths_Succeed(int expected, int actual)
    {
        (ISftpService source, ISftpService target) = RelayServices(expected, new SequentialStream(new byte[actual]));

        await RemoteRelay.CopyFileAsync(source, SourceSession, "/a/x", target, TargetSession, "/b/x");
    }

    [TestMethod]
    public async Task CopyFile_WhenSourceCloseFails_DoesNotReportCompletion()
    {
        (ISftpService source, ISftpService target) = RelayServices(10, new CloseFailureStream(new byte[10]));
        var progress = new RecordingProgress();

        await Assert.ThrowsAsync<IOException>(() =>
            RemoteRelay.CopyFileAsync(source, SourceSession, "/a/x", target, TargetSession, "/b/x", progress));

        Assert.DoesNotContain(p => p.Percentage == 100, progress.Items, "FTP 最终回复确认前不能发布成功进度。");
        Assert.Contains(p => p.BytesTransferred == 10 && p.Percentage < 100, progress.Items, "确认字节必须保留,供失败重试探测目标起点。");
    }

    [TestMethod]
    public async Task CopyFile_ReportsCompletionOnlyAfterSourceClose()
    {
        var stream = new TrackingStream(new byte[10]);
        (ISftpService source, ISftpService target) = RelayServices(10, stream);
        var progress = new RecordingProgress(p =>
        {
            if (p.Percentage == 100)
            {
                Assert.IsTrue(stream.Disposed);
            }
        });

        await RemoteRelay.CopyFileAsync(source, SourceSession, "/a/x", target, TargetSession, "/b/x", progress);

        Assert.ContainsSingle(p => p.Percentage == 100, progress.Items);
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(3)]
    public async Task CopyFile_SeekableResume_UsesActualReadPositionNotCumulativeReads(int actualStart)
    {
        (ISftpService source, ISftpService target) = RelayServices(10, new MemoryStream(new byte[10]));
        target.UploadStreamAsync(default, null!, null!, 0).ReturnsForAnyArgs(async call =>
        {
            Stream input = call.ArgAt<Stream>(1);
            input.Seek(2, SeekOrigin.Begin);
            await input.ReadExactlyAsync(new byte[5]); // 先核实尾部,然后从实际安全起点重新读。
            input.Seek(actualStart, SeekOrigin.Begin);
            await input.CopyToAsync(Stream.Null);
            input.Seek(0, SeekOrigin.Begin); // 契约不保证返回时的位置。
        });

        await RemoteRelay.CopyFileAsync(source, SourceSession, "/a/x", target, TargetSession, "/b/x", resumeOffset: 6);
    }

    [TestMethod]
    public async Task CopyFile_LateCompletionCallback_IsReportedOnceAfterSuccessfulClose()
    {
        var stream = new TrackingStream(new byte[10]);
        (ISftpService source, ISftpService target) = RelayServices(10, stream);
        IProgress<TransferProgress>? delayed = null;
        var progress = new RecordingProgress();
        target.UploadStreamAsync(default, null!, null!, 0).ReturnsForAnyArgs(async call =>
        {
            await call.ArgAt<Stream>(1).CopyToAsync(Stream.Null);
            delayed = call.ArgAt<IProgress<TransferProgress>?>(5);
        });

        await RemoteRelay.CopyFileAsync(source, SourceSession, "/a/x", target, TargetSession, "/b/x", progress);
        Assert.IsTrue(stream.Disposed);
        Assert.IsNotNull(delayed);
        var final = new TransferProgress
        {
            FileName = "x",
            BytesTransferred = 10,
            TotalBytes = 10,
            Percentage = 100,
            SpeedBytesPerSecond = 0,
            EstimatedTimeRemaining = TimeSpan.Zero,
        };
        delayed.Report(final);
        delayed.Report(final);

        Assert.HasCount(1, progress.Items);
    }

    [TestMethod]
    public async Task CopyFile_ThroughSftpService_VerifiesTailAndReadsFromSafeResumePoint()
    {
        (SftpService target, ISftpClientWrapper client) = SftpTarget();
        byte[] payload = [.. Enumerable.Range(0, 100).Select(static i => (byte)i)];
        (ISftpService source, _) = RelayServices(payload.Length, new MemoryStream(payload));
        client.GetFileSizeAsync("/b/x", Arg.Any<CancellationToken>()).Returns(60L);
        client.ResumeSafetyMargin.Returns(10L);
        client.OpenAsync("/b/x", FileMode.Open, FileAccess.Read, Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult<Stream>(new MemoryStream(payload[..60])));
        byte[]? suffix = null;
        client.UploadAsync(Arg.Any<Stream>(), "/b/x", Arg.Any<long>(), Arg.Any<Action<ulong>?>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                Stream input = call.ArgAt<Stream>(0);
                Assert.AreEqual(50L, call.ArgAt<long>(2));
                input.Seek(call.ArgAt<long>(2), SeekOrigin.Begin);
                using var received = new MemoryStream();
                await input.CopyToAsync(received);
                suffix = received.ToArray();
            });

        await RemoteRelay.CopyFileAsync(source, SourceSession, "/a/x", target, TargetSession, "/b/x", resumeOffset: 60);

        Assert.AreSequenceEqual(payload[50..], suffix);
    }

    [TestMethod]
    public async Task CopyFile_ZeroLengthReadDoesNotCountAsEof_AndShortChunksAreValid()
    {
        (ISftpService source, ISftpService target) = RelayServices(10, new SequentialStream(new byte[10]));
        target.UploadStreamAsync(default, null!, null!, 0).ReturnsForAnyArgs(async call =>
        {
            Stream input = call.ArgAt<Stream>(1);
            Assert.AreEqual(0, await input.ReadAsync(Memory<byte>.Empty));
            byte[] buffer = new byte[1];
            while (await input.ReadAsync(buffer, 0, buffer.Length) != 0) { }
        });

        await RemoteRelay.CopyFileAsync(source, SourceSession, "/a/x", target, TargetSession, "/b/x");
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task CopyFile_SynchronousReadEntryPoints_RejectShortSource(bool useSpan)
    {
        // Keep this on the original seekable path: the nonseekable small-file
        // path now rejects short sources before an uploader can invoke Read.
        (ISftpService source, ISftpService target) = RelayServices(10, new MemoryStream(new byte[4]));
        target.UploadStreamAsync(default, null!, null!, 0).ReturnsForAnyArgs(call =>
        {
            Stream input = call.ArgAt<Stream>(1);
            byte[] buffer = new byte[16];
            while ((useSpan ? input.Read(buffer.AsSpan()) : input.Read(buffer, 0, buffer.Length)) != 0) { }
            return Task.CompletedTask;
        });

        await Assert.ThrowsAsync<IOException>(() =>
            RemoteRelay.CopyFileAsync(source, SourceSession, "/a/x", target, TargetSession, "/b/x"));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task CopyFile_SeekWithoutReading_DoesNotPassLengthCheck(bool readEof)
    {
        (ISftpService source, ISftpService target) = RelayServices(10, new MemoryStream(new byte[10]));
        target.UploadStreamAsync(default, null!, null!, 0).ReturnsForAnyArgs(call =>
        {
            Stream input = call.ArgAt<Stream>(1);
            input.Seek(10, SeekOrigin.Begin);
            if (readEof)
            {
                Assert.AreEqual(0, input.Read(new byte[1], 0, 1));
            }
            return Task.CompletedTask;
        });

        await Assert.ThrowsAsync<IOException>(() =>
            RemoteRelay.CopyFileAsync(source, SourceSession, "/a/x", target, TargetSession, "/b/x"));
    }

    [TestMethod]
    public async Task CopyFile_CancelledAfterUpload_DoesNotReportCompletion()
    {
        using var cts = new CancellationTokenSource();
        var stream = new TrackingStream(new byte[10]);
        (ISftpService source, ISftpService target) = RelayServices(10, stream);
        var progress = new RecordingProgress();
        target.UploadStreamAsync(default, null!, null!, 0).ReturnsForAnyArgs(async call =>
        {
            await call.ArgAt<Stream>(1).CopyToAsync(Stream.Null);
            cts.Cancel();
        });

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() =>
            RemoteRelay.CopyFileAsync(source, SourceSession, "/a/x", target, TargetSession, "/b/x", progress, cancellationToken: cts.Token));

        Assert.IsTrue(stream.Disposed);
        Assert.IsEmpty(progress.Items);
    }

    [TestMethod]
    public async Task CopyFile_ReadFailureIsNotMaskedBySourceCloseFailure()
    {
        (ISftpService source, ISftpService target) = RelayServices(10, new CloseFailureStream(new byte[10]));
        var original = new OperationCanceledException();
        target.UploadStreamAsync(default, null!, null!, 0).ReturnsForAnyArgs(Task.FromException(original));

        Exception error = await Assert.ThrowsExactlyAsync<OperationCanceledException>(() =>
            RemoteRelay.CopyFileAsync(source, SourceSession, "/a/x", target, TargetSession, "/b/x"));

        Assert.AreSame(original, error);
    }

    [TestMethod]
    [DataRow(4 * 1024 * 1024 - 1)]
    [DataRow(4 * 1024 * 1024)]
    [DataRow(4 * 1024 * 1024 + 1)]
    public async Task CopyFile_BufferBoundary_ClosesSmallSourceBeforeWriting(int size)
    {
        var stream = new ControlledSequentialStream(size);
        (ISftpService source, ISftpService target) = RelayServices(size, stream);
        bool writingCalled = false;
        target.UploadStreamAsync(default, null!, null!, 0).ReturnsForAnyArgs(async call =>
        {
            Stream input = call.ArgAt<Stream>(1);
            Assert.IsFalse(input.CanSeek);
            Assert.ThrowsExactly<NotSupportedException>(() => input.Seek(size, SeekOrigin.Begin));
            long received = 0;
            byte[] buffer = new byte[9191];
            int count;
            while ((count = await input.ReadAsync(buffer.AsMemory())) != 0)
            {
                for (int i = 0; i < count; i++)
                {
                    Assert.AreEqual((byte)((received + i) * 31), buffer[i]);
                }
                received += count;
            }
            Assert.AreEqual(size, received);
        });

        await RemoteRelay.CopyFileAsync(source, SourceSession, "/a/x", target, TargetSession, "/b/x", writing: () =>
        {
            writingCalled = true;
            Assert.AreEqual(size <= 4 * 1024 * 1024 ? 1 : 0, stream.CloseCount);
        });

        Assert.IsTrue(writingCalled);
        Assert.AreEqual(1, stream.CloseCount);
    }

    [TestMethod]
    [DataRow(0L)]
    [DataRow(-1L)]
    public async Task CopyFile_UnknownEmpty_CloseFailureNeverTouchesTarget(long stat)
    {
        var original = new IOException("final reply");
        var stream = new ControlledSequentialStream(0) { CloseError = original };
        (ISftpService source, ISftpService target) = RelayServices(stat, stream);
        bool writing = false;

        Exception actual = await Assert.ThrowsExactlyAsync<IOException>(() => RemoteRelay.CopyFileAsync(
            source, SourceSession, "/a/x", target, TargetSession, "/b/x", writing: () => writing = true));

        Assert.AreSame(original, actual);
        Assert.IsFalse(writing);
        Assert.AreEqual(1, stream.CloseCount);
        await target.DidNotReceiveWithAnyArgs().UploadStreamAsync(default, null!, null!, 0);
    }

    [TestMethod]
    [DataRow(1L)]
    [DataRow(0L)]
    [DataRow(8L * 1024 * 1024)]
    public async Task CopyFile_FirstReadFailure_DoesNotTouchTargetAndPreservesOriginal(long stat)
    {
        var original = new IOException("source read");
        var stream = new ControlledSequentialStream(1) { ReadError = original, CloseError = new IOException("close") };
        (ISftpService source, ISftpService target) = RelayServices(stat, stream);
        bool writing = false;

        Exception actual = await Assert.ThrowsExactlyAsync<IOException>(() => RemoteRelay.CopyFileAsync(
            source, SourceSession, "/a/x", target, TargetSession, "/b/x", writing: () => writing = true));

        Assert.AreSame(original, actual);
        Assert.IsFalse(writing);
        Assert.AreEqual(1, stream.CloseCount);
        await target.DidNotReceiveWithAnyArgs().UploadStreamAsync(default, null!, null!, 0);
    }

    [TestMethod]
    [DataRow(10L)]
    [DataRow(0L)]
    [DataRow(8L * 1024 * 1024)]
    public async Task CopyFile_TargetEarlyReturn_DoesNotConfusePrefetchWithConsumption(long stat)
    {
        var stream = new ControlledSequentialStream(stat == 0 ? 10 : stat);
        (ISftpService source, ISftpService target) = RelayServices(stat, stream);
        target.UploadStreamAsync(default, null!, null!, 0).ReturnsForAnyArgs(async call =>
        {
            Assert.AreEqual(1, await call.ArgAt<Stream>(1).ReadAsync(new byte[1]));
        });

        await Assert.ThrowsExactlyAsync<RemoteTransferLengthException>(() => RemoteRelay.CopyFileAsync(
            source, SourceSession, "/a/x", target, TargetSession, "/b/x").WaitAsync(TimeSpan.FromSeconds(10)));

        Assert.AreEqual(1, stream.CloseCount);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task CopyFile_BufferedSynchronousReads_ConsumeAllBytes(bool useSpan)
    {
        var stream = new ControlledSequentialStream(4 * 1024 * 1024 + 1) { MaxRead = 173 };
        (ISftpService source, ISftpService target) = RelayServices(stream.Size, stream);
        target.UploadStreamAsync(default, null!, null!, 0).ReturnsForAnyArgs(call =>
        {
            Stream input = call.ArgAt<Stream>(1);
            byte[] buffer = new byte[733];
            Assert.AreEqual(0, input.Read(buffer, 0, 0));
            long consumed = 0;
            int count;
            while ((count = useSpan ? input.Read(buffer.AsSpan()) : input.Read(buffer, 0, buffer.Length)) != 0)
            {
                consumed += count;
            }
            Assert.AreEqual(stream.Size, consumed);
            return Task.CompletedTask;
        });

        await RemoteRelay.CopyFileAsync(source, SourceSession, "/a/x", target, TargetSession, "/b/x")
            .WaitAsync(TimeSpan.FromSeconds(15));
        Assert.AreEqual(1, stream.CloseCount);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task BufferedStream_IndependentPrefetch_HoldsBudgetAcrossConsumerAndProducer_AndDrainsOnStop(bool throwingCancellation)
    {
        const int blockSize = 256 * 1024;
        var pool = new CountingPool();
        var source = new ControlledSequentialStream(32L * blockSize)
        {
            OnRead = throwingCancellation ? token => token.Register(() => throw new IOException("cancel callback")) : null,
        };
        (Stream input, Func<Task> ready, Func<Task> stop) = CreateBuffered(source, source.Size, pool);
        try
        {
            await ready().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.AreEqual(1, await input.ReadAsync(new byte[1])); // consumer retains a partially read block
            await source.BudgetRead.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.AreEqual(16, pool.Outstanding);
            Assert.AreEqual(4 * 1024 * 1024, pool.HighWaterBytes);
            Assert.AreEqual(4 * 1024 * 1024L, source.BytesRead);
            // ReadAsync is not waiting for the producer to execute on this consumer:
            // no consumer reads occurred while the other fifteen blocks filled.
            if (throwingCancellation)
            {
                await Assert.ThrowsExactlyAsync<AggregateException>(() => stop().WaitAsync(TimeSpan.FromSeconds(10)));
            }
            else
            {
                await stop().WaitAsync(TimeSpan.FromSeconds(10));
            }
            Assert.AreEqual(0, pool.Outstanding);
            Assert.AreEqual(16, pool.Returned);
            Assert.IsTrue(pool.AllReturnedCleared);
            Assert.AreEqual(0, source.CloseCount, "Only Relay owns the source close.");
        }
        finally
        {
            await stop();
            await source.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task CopyFile_TargetFailureWithFullBuffer_PreservesErrorAndClosesOnce()
    {
        var stream = new ControlledSequentialStream(8 * 1024 * 1024) { CloseError = new IOException("close") };
        (ISftpService source, ISftpService target) = RelayServices(stream.Size, stream);
        var original = new IOException("target");
        target.UploadStreamAsync(default, null!, null!, 0).ReturnsForAnyArgs(async _ =>
        {
            await stream.BudgetRead.Task.WaitAsync(TimeSpan.FromSeconds(10));
            throw original;
        });

        Exception actual = await Assert.ThrowsExactlyAsync<IOException>(() => RemoteRelay.CopyFileAsync(
            source, SourceSession, "/a/x", target, TargetSession, "/b/x").WaitAsync(TimeSpan.FromSeconds(10)));

        Assert.AreSame(original, actual);
        Assert.AreEqual(1, stream.CloseCount);
    }

    [TestMethod]
    public async Task CopyFile_CancelWithFullBuffer_JoinsProducerAndClosesOnce()
    {
        using var cts = new CancellationTokenSource();
        var stream = new ControlledSequentialStream(8 * 1024 * 1024);
        (ISftpService source, ISftpService target) = RelayServices(stream.Size, stream);
        target.UploadStreamAsync(default, null!, null!, 0).ReturnsForAnyArgs(async _ =>
        {
            await stream.BudgetRead.Task.WaitAsync(TimeSpan.FromSeconds(10));
            cts.Cancel();
        });

        OperationCanceledException error = await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => RemoteRelay.CopyFileAsync(
            source, SourceSession, "/a/x", target, TargetSession, "/b/x", cancellationToken: cts.Token)
            .WaitAsync(TimeSpan.FromSeconds(10)));

        Assert.AreEqual(cts.Token, error.CancellationToken);
        Assert.AreEqual(1, stream.CloseCount);
    }

    [TestMethod]
    public async Task BufferedStream_ProducerFailureAfterFirstBlock_PreservesOriginalAndReturnsEveryRent()
    {
        var original = new IOException("second block source read");
        var pool = new CountingPool();
        var source = new ControlledSequentialStream(8 * 1024 * 1024)
        {
            ReadError = original,
            FailAt = 256 * 1024,
        };
        (Stream input, Func<Task> ready, Func<Task> stop) = CreateBuffered(source, source.Size, pool);
        try
        {
            await ready().WaitAsync(TimeSpan.FromSeconds(10));
            Exception actual = await Assert.ThrowsExactlyAsync<IOException>(() => input.CopyToAsync(Stream.Null)
                .WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.AreSame(original, actual);
        }
        finally
        {
            await stop().WaitAsync(TimeSpan.FromSeconds(10));
        }
        Assert.AreEqual(0, pool.Outstanding);
        Assert.IsTrue(pool.AllReturnedCleared);
        Assert.AreEqual(0, source.CloseCount);
    }

    private static (Stream Input, Func<Task> Ready, Func<Task> Stop) CreateBuffered(Stream source, long size, ArrayPool<byte> pool)
    {
        Type type = typeof(RemoteRelay).Assembly.GetType("VelaShell.Core.Sftp.BufferedRelayReadStream", throwOnError: true)!;
        var stream = (Stream)Activator.CreateInstance(type, BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null, args: [source, size, "/a/x", false, CancellationToken.None, pool], culture: null)!;
        return (stream,
            () => (Task)type.GetMethod("WaitUntilReadyAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(stream, null)!,
            () => (Task)type.GetMethod("StopAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(stream, null)!);
    }

    private sealed class CountingPool : ArrayPool<byte>
    {
        private readonly Lock _sync = new();
        private readonly HashSet<byte[]> _outstanding = [];
        public int Outstanding { get { lock (_sync) { return _outstanding.Count; } } }
        public int HighWaterBytes { get; private set; }
        public int Returned { get; private set; }
        public bool AllReturnedCleared { get; private set; } = true;

        public override byte[] Rent(int minimumLength)
        {
            Assert.AreEqual(256 * 1024, minimumLength);
            byte[] buffer = new byte[minimumLength];
            lock (_sync)
            {
                _outstanding.Add(buffer);
                HighWaterBytes = Math.Max(HighWaterBytes, _outstanding.Sum(b => b.Length));
            }
            return buffer;
        }

        public override void Return(byte[] array, bool clearArray = false)
        {
            lock (_sync)
            {
                Assert.IsTrue(_outstanding.Remove(array), "Every rent must be returned exactly once.");
                AllReturnedCleared &= clearArray;
                Returned++;
            }
            if (clearArray) { Array.Clear(array); }
        }
    }

    private sealed class ControlledSequentialStream(long size) : Stream
    {
        private long _position;
        public long Size => size;
        public long BytesRead => Interlocked.Read(ref _position);
        public int CloseCount { get; private set; }
        public int MaxRead { get; init; } = int.MaxValue;
        public Exception? ReadError { get; init; }
        public long FailAt { get; init; }
        public Exception? CloseError { get; init; }
        public Action<CancellationToken>? OnRead { get; init; }
        public TaskCompletionSource BudgetRead { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            OnRead?.Invoke(cancellationToken);
            if (_position >= FailAt && ReadError is { } error) { return ValueTask.FromException<int>(error); }
            int count = (int)Math.Min(Math.Min(buffer.Length, MaxRead), size - _position);
            for (int i = 0; i < count; i++) { buffer.Span[i] = (byte)((_position + i) * 31); }
            Interlocked.Add(ref _position, count);
            if (_position == 4 * 1024 * 1024) { BudgetRead.TrySetResult(); }
            return ValueTask.FromResult(count);
        }

        public override ValueTask DisposeAsync()
        {
            CloseCount++;
            return CloseError is { } error ? ValueTask.FromException(error) : ValueTask.CompletedTask;
        }
        public override bool CanSeek => false;
        public override bool CanRead => true;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new AssertFailedException("Producer must use asynchronous reads.");
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private static (ISftpService Source, ISftpService Target) RelayServices(long expected, Stream stream)
    {
        ISftpService source = Substitute.For<ISftpService>();
        source.GetFileInfoAsync(SourceSession, "/a/x", Arg.Any<CancellationToken>()).Returns(RemoteFile("/a/x", expected, default));
        source.OpenReadAsync(SourceSession, "/a/x", Arg.Any<CancellationToken>()).Returns(Task.FromResult(stream));
        ISftpService target = Substitute.For<ISftpService>();
        target.UploadStreamAsync(TargetSession, Arg.Any<Stream>(), "/b/x", Arg.Is(Math.Max(0, expected)), Arg.Any<DateTime?>(),
                Arg.Any<IProgress<TransferProgress>?>(), Arg.Any<long>(), Arg.Any<uint?>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                await call.ArgAt<Stream>(1).CopyToAsync(Stream.Null, call.ArgAt<CancellationToken>(8));
                call.ArgAt<IProgress<TransferProgress>?>(5)?.Report(new TransferProgress
                {
                    FileName = "x",
                    BytesTransferred = expected,
                    TotalBytes = expected,
                    Percentage = 100,
                    SpeedBytesPerSecond = 0,
                    EstimatedTimeRemaining = TimeSpan.Zero,
                });
            });
        return (source, target);
    }

    private sealed class RecordingProgress(Action<TransferProgress>? onReport = null) : IProgress<TransferProgress>
    {
        public List<TransferProgress> Items { get; } = [];

        public void Report(TransferProgress value)
        {
            onReport?.Invoke(value);
            Items.Add(value);
        }
    }

    private sealed class CloseFailureStream(byte[] data) : MemoryStream(data)
    {
        public override ValueTask DisposeAsync() => ValueTask.FromException(new IOException("Final FTP reply failed."));
    }

    private static ISftpService ConsumingTarget()
    {
        ISftpService target = Substitute.For<ISftpService>();
        target.UploadStreamAsync(default, null!, null!, 0).ReturnsForAnyArgs(async call =>
        {
            Stream stream = call.ArgAt<Stream>(1);
            if (call.ArgAt<long>(6) > 0)
            {
                stream.Seek(call.ArgAt<long>(6), SeekOrigin.Begin);
            }
            await stream.CopyToAsync(Stream.Null);
        });
        return target;
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
