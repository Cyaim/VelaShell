using NSubstitute;
using VelaShell.Core.Models;
using VelaShell.Core.Sftp;

namespace VelaShell.Core.Tests.Sftp;

/// <summary>
/// FTP → SFTP 中转的流管道模式(<see cref="RemoteRelay.TryCopyFileWithRemoteCommitAsync" /> 的 <c>piped</c> 分支):
/// 源端一个循环往 <see cref="SourcePolledStream" /> 里灌,目标端一个循环从管道里取,字节只过一次有界内存。
/// </summary>
/// <remarks>
/// 现场的问题:源端是 FTP 时,旧路径先把整份文件缓存到本地、再上传 —— 一个字节都没动过目标,用户看到的就是
/// 「0 B 之后进入重试」。这里逐条把新路径的边界钉住:不预读、失败不留正式目标、源收尾失败不提交、取消能停住两端。
/// </remarks>
[TestClass]
[TestCategory("Sftp")]
public class RemoteRelayPipelineTests
{
    private static readonly Guid SourceSession = Guid.NewGuid();
    private static readonly Guid TargetSession = Guid.NewGuid();
    private const string TemporaryPrefix = "/b/.vela-relay-";

    [TestInitialize]
    public void Init() => RemoteRelay.PipesSequentialSources = true;

    [TestCleanup]
    public void Cleanup() => RemoteRelay.PipesSequentialSources = true;

    [TestMethod]
    public async Task StreamedSource_UploadsEveryByteWithoutPreDownloadingTheWholeFile()
    {
        byte[] payload = Generate(1024);
        ISftpService source = Source(payload.Length);
        var stream = new TrackingStream(payload);
        source.OpenReadAsync(SourceSession, "/a/data.bin", Arg.Any<CancellationToken>()).Returns(Task.FromResult<Stream>(stream));

        Stream? uploaded = null;
        ISftpService target = Target(call =>
        {
            uploaded = call.ArgAt<Stream>(1);
            return call.ArgAt<Stream>(1).CopyToAsync(Stream.Null, call.ArgAt<CancellationToken>(8));
        });

        bool copied = await RemoteRelay.TryCopyFileWithRemoteCommitAsync(
            source, SourceSession, "/a/data.bin", target, TargetSession, "/b/data.bin");

        Assert.IsTrue(copied);
        Assert.IsNotNull(uploaded);
        Assert.IsInstanceOfType<SourcePolledStream>(uploaded, "顺序源必须经管道中转,而不是先整份缓存。");
        Assert.IsTrue(stream.Disposed, "源流的收尾(FTP 的最终回复)必须在成功路径上跑完。");
        // 一个字节都没落本地:旧路径在这里会先调用 DownloadStreamAsync 写一份完整缓存。
        await source.DidNotReceiveWithAnyArgs().DownloadStreamAsync(default, null!, null!);
        await target.Received(1).CommitUploadAsync(TargetSession, Arg.Is<string>(p => p.StartsWith(TemporaryPrefix, StringComparison.Ordinal)),
            "/b/data.bin", true, Arg.Any<CancellationToken>());
    }

    [TestMethod]
    public async Task StreamedSource_DoesNotStatTheSourceWhenTheCallerAlreadyKnowsIt()
    {
        byte[] payload = Generate(1024);
        ISftpService source = Source(payload.Length);
        source.OpenReadAsync(SourceSession, "/a/data.bin", Arg.Any<CancellationToken>())
              .Returns(_ => Task.FromResult<Stream>(new TrackingStream(payload)));
        ISftpService target = TargetConsuming();

        await RemoteRelay.CopyFileAsync(source, SourceSession, "/a/data.bin", target, TargetSession, "/b/data.bin",
            knownSource: new RelaySourceSnapshot(payload.Length, new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc)));

        // FTP 上每一次 stat 都要与控制连接上的数据传输抢那条连接:调用方给过了就不再问。
        await source.DidNotReceiveWithAnyArgs().GetFileInfoAsync(default, null!);
    }

    [TestMethod]
    public async Task StreamedSource_FailureBeforeAnyByte_LeavesNoFileBehindTheFinalName()
    {
        ISftpService source = Source(4096);
        source.OpenReadAsync(SourceSession, "/a/data.bin", Arg.Any<CancellationToken>())
              .Returns(_ => Task.FromResult<Stream>(new FailingStream(new IOException("data connection reset"))));
        ISftpService target = TargetConsuming();

        await Assert.ThrowsExactlyAsync<IOException>(() => RemoteRelay.TryCopyFileWithRemoteCommitAsync(
            source, SourceSession, "/a/data.bin", target, TargetSession, "/b/data.bin"));

        await target.DidNotReceive().CommitUploadAsync(default, default!, default!, default, default);
    }

    [TestMethod]
    public async Task StreamedSource_ShortReadFails_AndNeverCommits()
    {
        // SIZE 说 3000,数据连接只给 1000 就正常 EOF:那是被服务器掐掉的一截,不能当成功提交。
        ISftpService source = Source(3000);
        source.OpenReadAsync(SourceSession, "/a/data.bin", Arg.Any<CancellationToken>())
              .Returns(_ => Task.FromResult<Stream>(new TrackingStream(Generate(1000))));
        ISftpService target = TargetConsuming();

        await Assert.ThrowsExactlyAsync<RemoteTransferLengthException>(() => RemoteRelay.TryCopyFileWithRemoteCommitAsync(
            source, SourceSession, "/a/data.bin", target, TargetSession, "/b/data.bin"));
        await target.DidNotReceive().CommitUploadAsync(default, default!, default!, default, default);
    }

    [TestMethod]
    public async Task StreamedSource_CompletionRejectedAfterAllBytes_StillFailsTheRelay()
    {
        // 字节全给了,但源的收尾(FTP 的最终回复)失败 —— 这条数据连接本身不可信,不能提交。
        ISftpService source = Source(2048);
        source.OpenReadAsync(SourceSession, "/a/data.bin", Arg.Any<CancellationToken>())
              .Returns(_ => Task.FromResult<Stream>(new TrackingStream(Generate(2048), dispose: new IOException("426 aborted"))));
        ISftpService target = TargetConsuming();

        await Assert.ThrowsExactlyAsync<IOException>(() => RemoteRelay.TryCopyFileWithRemoteCommitAsync(
            source, SourceSession, "/a/data.bin", target, TargetSession, "/b/data.bin"));
        await target.DidNotReceive().CommitUploadAsync(default, default!, default!, default, default);
        Assert.AreEqual(1, Uploads(target), "源收尾失败发生在目标写完之后:那次上传已经发生(写的是临时文件)。");
    }

    [TestMethod]
    public async Task StreamedSource_TargetFailure_StopsTheSourceAndReportsTheTargetError()
    {
        // 一次只给 64 KiB:像 FTP 数据流,读循环会一块一块灌。目标取走一块就失败,
        // 源不可能把整份文件读完(哪怕管道无界)。
        byte[] payload = Generate(8 * 1024 * 1024);
        ISftpService source = Source(payload.Length);
        var stream = new TrackingStream(payload, chunk: 256 * 1024);
        source.OpenReadAsync(SourceSession, "/a/data.bin", Arg.Any<CancellationToken>()).Returns(Task.FromResult<Stream>(stream));
        var targetFailure = new RemoteRelayCommitException(new IOException("disk full"));
        var anyByteTaken = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ISftpService target = Target(async call =>
        {
            byte[] first4 = new byte[4096];
            int read = await call.ArgAt<Stream>(1).ReadAsync(first4, call.ArgAt<CancellationToken>(8));
            Console.WriteLine($"[target] read {read} bytes, throwing {targetFailure.GetType().Name}");
            Assert.IsGreaterThan(0, read, "目标必须先真正从管道里取到字节。");
            anyByteTaken.TrySetResult();
            throw targetFailure;
        });

        RemoteRelayCommitException failure = await Assert.ThrowsExactlyAsync<RemoteRelayCommitException>(() => RemoteRelay.TryCopyFileWithRemoteCommitAsync(
            source, SourceSession, "/a/data.bin", target, TargetSession, "/b/data.bin").WaitAsync(TimeSpan.FromSeconds(30)));

        Assert.IsTrue(anyByteTaken.Task.IsCompleted);
        Assert.AreSame(targetFailure, failure, "目标先失败:报出去的是目标的错误,不是源被叫停时的后果。");
        Assert.IsTrue(stream.Disposed, "目标失败之后源端仍须释放(还连接、还名额)。");
        // 源比管道大得多,目标取了一块就失败:源不可能把整份文件读完 —— 背压与"目标收工"真的停住了它。
        Assert.IsLessThan(payload.Length, stream.BytesRead, $"源应被停住,实际读了 {stream.BytesRead}/{payload.Length}。");
        await target.DidNotReceive().CommitUploadAsync(default, default!, default!, default, default);
    }

    [TestMethod]
    public async Task StreamedSource_Cancellation_UnblocksBothLoops()
    {
        ISftpService source = Source(1024);
        source.OpenReadAsync(SourceSession, "/a/data.bin", Arg.Any<CancellationToken>())
              .Returns(_ => Task.FromResult<Stream>(new BlockingStream()));
        using var cts = new CancellationTokenSource();
        ISftpService target = Target(async call =>
        {
            // 目标卡在等源的第一块时取消:两端都要靠取消唤醒,而不是各自等一个不会来的 IO。
            await Task.Delay(50).ConfigureAwait(false);
            cts.Cancel();
            await call.ArgAt<Stream>(1).CopyToAsync(Stream.Null, call.ArgAt<CancellationToken>(8));
        });

        Exception failure = await Assert.ThrowsAsync<OperationCanceledException>(() => RemoteRelay.TryCopyFileWithRemoteCommitAsync(
            source, SourceSession, "/a/data.bin", target, TargetSession, "/b/data.bin",
            cancellationToken: cts.Token).WaitAsync(TimeSpan.FromSeconds(30)));

        Assert.IsFalse(failure is TimeoutException);
        await target.DidNotReceive().CommitUploadAsync(default, default!, default!, default, default);
    }

    [TestMethod]
    public async Task LegacyPath_IsStillReachableWhenPipingIsDisabled()
    {
        // 改动之前的路径不删:出事时它是一条已知可行的退路,验证用例也要能指到它。
        RemoteRelay.PipesSequentialSources = false;
        byte[] payload = Generate(4096);
        ISftpService source = Source(payload.Length);
        source.OpenReadAsync(SourceSession, "/a/data.bin", Arg.Any<CancellationToken>())
              .Returns(_ => Task.FromResult<Stream>(new TrackingStream(payload)));
        Stream? uploaded = null;
        ISftpService target = Target(call =>
        {
            uploaded = call.ArgAt<Stream>(1);
            return call.ArgAt<Stream>(1).CopyToAsync(Stream.Null, call.ArgAt<CancellationToken>(8));
        });

        await RemoteRelay.TryCopyFileWithRemoteCommitAsync(source, SourceSession, "/a/data.bin", target, TargetSession, "/b/data.bin");

        Assert.IsNotNull(uploaded);
        // 关掉管道后仍走原有的有界预读(BufferedRelayReadStream),不落盘、不整份缓存;
        // 它与管道是两条并行保留的路径,而不是把旧路径删掉。
        Assert.IsFalse(uploaded is SourcePolledStream);
    }

    private static ISftpService Source(long size)
    {
        ISftpService source = Substitute.For<ISftpService>();
        source.GetFileInfoAsync(SourceSession, "/a/data.bin", Arg.Any<CancellationToken>())
              .Returns(RemoteFile("/a/data.bin", size));
        return source;
    }

    /// <summary>支持原子提交的目标;上传行为由 <paramref name="upload" /> 决定。</summary>
    private static ISftpService Target(Func<NSubstitute.Core.CallInfo, Task> upload)
    {
        ISftpService target = Substitute.For<ISftpService>();
        target.CheckUploadCommitAsync(TargetSession, Arg.Any<string>(), true, Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        target.ExistsAsync(TargetSession, "/b/data.bin", Arg.Any<CancellationToken>()).Returns(false);
        target.GetUploadCreateModeAsync(TargetSession, "/b/data.bin", Arg.Any<CancellationToken>()).Returns((uint?)null);
        target.CommitUploadAsync(default, default!, default!, default, default).Returns(Task.CompletedTask);
        target.DeleteAsync(default, default!, default, default).Returns(Task.CompletedTask);
        target.UploadStreamAsync(default, null!, null!, 0).ReturnsForAnyArgs(upload);
        return target;
    }

    /// <summary>支持原子提交、把上传字节全部取走并记次数的目标。</summary>
    private static ISftpService TargetConsuming() => Target(Consume);

    private static Task Consume(NSubstitute.Core.CallInfo call) =>
        call.ArgAt<Stream>(1).CopyToAsync(Stream.Null, call.ArgAt<CancellationToken>(8));

    /// <summary>把上传端(目标)的每一步都打到测试输出:诊断「谁先收工、谁卡住」用。</summary>
    private static async Task TraceUpload(NSubstitute.Core.CallInfo call, string tag)
    {
        Console.WriteLine($"[target] {tag} start");
        try
        {
            await call.ArgAt<Stream>(1).CopyToAsync(Stream.Null, call.ArgAt<CancellationToken>(8));
            Console.WriteLine($"[target] {tag} done");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[target] {tag} failed: {ex.GetType().Name}");
            throw;
        }
    }

    private static async Task FailAfterFirstChunk(NSubstitute.Core.CallInfo call, Exception failure, TaskCompletionSource? first)
    {
        Console.WriteLine("[target] FailAfterFirstChunk entry");
        byte[] first4 = new byte[4096];
        Console.WriteLine("[target] about to read");
        int read = await call.ArgAt<Stream>(1).ReadAsync(first4, call.ArgAt<CancellationToken>(8));
        Console.WriteLine($"[target] read {read} bytes, throwing {failure.GetType().Name}");
        Assert.IsGreaterThan(0, read, "目标必须先真正从管道里取到字节。");
        first?.TrySetResult();
        throw failure;
    }

    /// <summary>数一数目标端被要求上传了几次(源收尾失败时那次上传已经发生过了)。</summary>
    private static int Uploads(ISftpService target) =>
        target.ReceivedCalls().Count(call => call.GetMethodInfo().Name == nameof(ISftpService.UploadStreamAsync));

    private static RemoteFileInfo RemoteFile(string path, long size) => new()
    {
        Name = path[(path.LastIndexOf('/') + 1)..],
        FullPath = path,
        Size = size,
        Permissions = "-rw-r--r--",
        IsDirectory = false,
        LastModified = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc),
        Owner = "u",
        Group = "g",
    };

    private static byte[] Generate(int length)
    {
        byte[] data = new byte[length];
        for (int i = 0; i < length; i++)
        {
            data[i] = (byte)(i * 31);
        }
        return data;
    }

    /// <summary>
    /// 一条顺序只读流:按块给数据,遇到 EOF 就 <see cref="EOF" />(像 FTP 数据流那样,
    /// 长度是"信封",真正的读结束以流 EOF 为准)。
    /// </summary>
    private sealed class TrackingStream(byte[] data, Exception? dispose = null, int chunk = int.MaxValue) : Stream
    {
        private readonly MemoryStream _inner = new(data);

        public long BytesRead { get; private set; }

        public bool Disposed { get; private set; }

        public bool EOF { get; private set; }

        public override bool CanRead => !Disposed;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => data.Length;

        public override long Position
        {
            get => _inner.Position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            int read = _inner.Read(buffer, offset, count);
            BytesRead += read;
            return read;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(cancellationToken);
            }
            if (EOF)
            {
                return ValueTask.FromResult(0);
            }
            // 模拟 FTP 数据流:一次读可以给不满 buffer,但不会超过 chunk。
            Memory<byte> slice = buffer[..Math.Min(buffer.Length, chunk)];
            int read = _inner.Read(slice.Span);
            BytesRead += read;
            if (read == 0)
            {
                EOF = true;
            }
            return ValueTask.FromResult(read);
        }

        public override void Flush() { }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }

        public override ValueTask DisposeAsync()
        {
            Disposed = true;
            return dispose is null ? ValueTask.CompletedTask : ValueTask.FromException(dispose);
        }
    }

    /// <summary>一读就失败的源(数据连接被服务器复位)。</summary>
    private sealed class FailingStream(Exception failure) : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => 0;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => throw failure;

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromException<int>(failure);

        public override void Flush() { }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>一直等下去的源:用来验证取消真的能把它唤醒。</summary>
    private sealed class BlockingStream : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => 0;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }

        public override void Flush() { }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
