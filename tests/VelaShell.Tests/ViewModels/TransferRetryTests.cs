using Avalonia.Headless;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using ReactiveUI.Primitives;
using VelaShell.Core.Models;
using VelaShell.Core.Resources;
using VelaShell.Core.Sftp;
using VelaShell.Core.Ssh;
using VelaShell.Tests.TestSupport;
using VelaShell.ViewModels;

namespace VelaShell.Tests.ViewModels;

/// <summary>
/// 传输失败自动重试(设置 → 文件传输 → 失败自动重试,<c>TransferMaxRetries</c>):
/// 临时失败按次数重试、带退避;永久失败直接失败;重试时续传与否跟着「恢复中断的传输」走。
/// </summary>
/// <remarks>
/// 跑在 headless UI 线程上,理由见 <see cref="GlobalTransferConcurrencyTests" />:传输面板的记账要求续体回到 UI 线程。
/// </remarks>
[TestClass]
[TestCategory("FileTransfer")]
public sealed class TransferRetryTests
{
    private const string RemoteFile = "/data/a.bin";
    private static HeadlessUnitTestSession _session = null!;

    [ClassInitialize]
    public static void Init(TestContext _) =>
        _session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(TransferRetryTests).Assembly);

    [TestMethod]
    public Task ATransientFailure_IsRetriedWithBackoff_UntilItGoesThrough() => OnUi(async () =>
    {
        using var rig = new Rig(retries: 3);
        rig.Sftp.DownloadFileAsync(rig.SessionId, RemoteFile, Arg.Any<string>(), Arg.Any<IProgress<TransferProgress>?>(),
                Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(
                _ => Task.FromException(new VelaSshConnectionException("连接被重置")),
                _ => Task.FromException(new VelaSshOperationTimeoutException("超时")),
                _ => Task.CompletedTask);

        await rig.DownloadAsync();

        Assert.HasCount(3, rig.DownloadCalls(), "失败两次、第三次成功");
        Assert.AreSequenceEqual([TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2)], rig.Delays, "翻倍退避");
        Assert.AreEqual(TransferStatus.Completed, rig.Sink.Transfers.Single().Status);
        Assert.IsNull(rig.Browser.ErrorMessage);
    });

    [TestMethod]
    public Task RetriesStopAtTheConfiguredLimit_AndTheErrorSaysHowManyWereTried() => OnUi(async () =>
    {
        using var rig = new Rig(retries: 2);
        var failure = new VelaSshConnectionException("连接被重置");
        rig.Sftp.DownloadFileAsync(rig.SessionId, RemoteFile, Arg.Any<string>(), Arg.Any<IProgress<TransferProgress>?>(),
                Arg.Any<long>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(failure);

        await rig.DownloadAsync();

        Assert.HasCount(3, rig.DownloadCalls(), "首次 + 重试 2 次");
        Assert.AreEqual(TransferStatus.Failed, rig.Sink.Transfers.Single().Status);
        Assert.AreEqual(Strings.Format("Transfer_FailedAfterRetries", 2, failure.Message), rig.Browser.ErrorMessage);
    });

    [TestMethod]
    public Task ZeroRetries_FailsOnTheFirstError() => OnUi(async () =>
    {
        using var rig = new Rig(retries: 0);
        rig.Sftp.DownloadFileAsync(rig.SessionId, RemoteFile, Arg.Any<string>(), Arg.Any<IProgress<TransferProgress>?>(),
                Arg.Any<long>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new VelaSshConnectionException("连接被重置"));

        await rig.DownloadAsync();

        Assert.HasCount(1, rig.DownloadCalls());
        Assert.IsEmpty(rig.Delays);
        Assert.AreEqual("连接被重置", rig.Browser.ErrorMessage, "没重试过就不提重试");
    });

    [TestMethod]
    [DataRow("permission", DisplayName = "权限拒绝")]
    [DataRow("missing", DisplayName = "远端不存在")]
    [DataRow("refused", DisplayName = "服务端拒写(磁盘满)")]
    [DataRow("local", DisplayName = "本地无权限")]
    public Task APermanentFailure_IsNotRetried(string kind) => OnUi(async () =>
    {
        using var rig = new Rig(retries: 3);
        Exception failure = kind switch
        {
            "permission" => new VelaSftpPermissionDeniedException("Permission denied"),
            "missing" => new VelaSftpPathNotFoundException("No such file"),
            "refused" => new VelaSftpTransferInterruptedException("No space left on device", 4096) { RefusedByServer = true },
            _ => new UnauthorizedAccessException("Access denied"),
        };
        rig.Sftp.DownloadFileAsync(rig.SessionId, RemoteFile, Arg.Any<string>(), Arg.Any<IProgress<TransferProgress>?>(),
                Arg.Any<long>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(failure);

        await rig.DownloadAsync();

        Assert.HasCount(1, rig.DownloadCalls(), "永久性失败再试也一样");
        Assert.IsEmpty(rig.Delays);
        Assert.AreEqual(TransferStatus.Failed, rig.Sink.Transfers.Single().Status);
    });

    /// <summary>续传开着:重试从这个文件自己已经写下的地方接着传,而不是整份重来。</summary>
    [TestMethod]
    public Task WithResumeOn_ARetryContinuesFromWhatThisFileAlreadyWrote() => OnUi(async () =>
    {
        using var rig = new Rig(retries: 3, resume: true);
        rig.Sftp.GetFileInfoAsync(rig.SessionId, RemoteFile, Arg.Any<CancellationToken>())
            .Returns(Remote(100));
        rig.Sftp.DownloadFileAsync(rig.SessionId, RemoteFile, Arg.Any<string>(), Arg.Any<IProgress<TransferProgress>?>(),
                Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(
                call => Rig.WritePartialThenFail(call, 40),
                _ => Task.CompletedTask);

        await rig.DownloadAsync();

        Assert.AreSequenceEqual([0, 40], rig.DownloadCalls().Select(c => c.Offset).ToArray());
        Assert.AreEqual(TransferStatus.Completed, rig.Sink.Transfers.Single().Status);
    });

    /// <summary>续传关着:重试整份重来。</summary>
    [TestMethod]
    public Task WithResumeOff_ARetryStartsOver() => OnUi(async () =>
    {
        using var rig = new Rig(retries: 3, resume: false);
        rig.Sftp.GetFileInfoAsync(rig.SessionId, RemoteFile, Arg.Any<CancellationToken>())
            .Returns(Remote(100));
        rig.Sftp.DownloadFileAsync(rig.SessionId, RemoteFile, Arg.Any<string>(), Arg.Any<IProgress<TransferProgress>?>(),
                Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(
                call => Rig.WritePartialThenFail(call, 40),
                _ => Task.CompletedTask);

        await rig.DownloadAsync();

        Assert.AreSequenceEqual([0, 0], rig.DownloadCalls().Select(c => c.Offset).ToArray());
    });

    /// <summary>
    /// 上一次连一个字节都没传出去:目标上的东西不是它写的,不能凭长度去续(目录同步的目标比源短是常态)。
    /// </summary>
    [TestMethod]
    public Task AnAttemptThatMovedNoBytes_IsRetriedFromTheSameStart() => OnUi(async () =>
    {
        using var rig = new Rig(retries: 3, resume: true);
        rig.Sftp.GetFileInfoAsync(rig.SessionId, RemoteFile, Arg.Any<CancellationToken>())
            .Returns(Remote(100));
        rig.Sftp.DownloadFileAsync(rig.SessionId, RemoteFile, Arg.Any<string>(), Arg.Any<IProgress<TransferProgress>?>(),
                Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(
                call =>
                {
                    // 别人(不是这次传输)在本地放了一份 40 字节的同名文件,这一次没传出任何字节就断了。
                    File.WriteAllBytes(call.ArgAt<string>(2), new byte[40]);
                    return Task.FromException(new VelaSshConnectionException("连接被重置"));
                },
                _ => Task.CompletedTask);

        await rig.DownloadAsync();

        Assert.AreSequenceEqual([0, 0], rig.DownloadCalls().Select(c => c.Offset).ToArray());
    });

    /// <summary>等待重试期间,行上写明是第几次重试(而不是像又回去排队了)。</summary>
    [TestMethod]
    public Task WhileWaitingToRetry_TheRowSaysWhichRetryItIs() => OnUi(async () =>
    {
        using var rig = new Rig(retries: 3);
        string? shown = null;
        rig.Browser.RetryDelay = (_, _) =>
        {
            shown ??= rig.Sink.Transfers.Single().ProgressText;
            return Task.CompletedTask;
        };
        rig.Sftp.DownloadFileAsync(rig.SessionId, RemoteFile, Arg.Any<string>(), Arg.Any<IProgress<TransferProgress>?>(),
                Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromException(new TimeoutException()), _ => Task.CompletedTask);

        await rig.DownloadAsync();

        Assert.AreEqual(Strings.Format("Transfer_RetryWaiting", 1, 3), shown);
    });

    /// <summary>「全部取消」照样能把一个正等着重试的传输停下,而且不再重试。</summary>
    [TestMethod]
    public Task CancellingWhileWaitingToRetry_StopsTheTransfer() => OnUi(async () =>
    {
        using var rig = new Rig(retries: 5);
        var waiting = new TaskCompletionSource();
        rig.Browser.RetryDelay = async (_, ct) =>
        {
            waiting.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
        };
        rig.Sftp.DownloadFileAsync(rig.SessionId, RemoteFile, Arg.Any<string>(), Arg.Any<IProgress<TransferProgress>?>(),
                Arg.Any<long>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new VelaSshConnectionException("连接被重置"));

        Task download = rig.DownloadAsync();
        await waiting.Task.WaitAsync(TimeSpan.FromSeconds(10));
        rig.Sink.CancelAllCommand.Execute().Subscribe();
        await download.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.HasCount(1, rig.DownloadCalls());
        Assert.AreEqual(TransferStatus.Cancelled, rig.Sink.Transfers.Single().Status);
    });

    /// <summary>上传与中转走同一条重试路径(这里验上传)。</summary>
    [TestMethod]
    public Task UploadsAreRetriedToo() => OnUi(async () =>
    {
        using var rig = new Rig(retries: 2);
        string local = Path.Combine(rig.Directory, "up.bin");
        await File.WriteAllBytesAsync(local, new byte[64]);
        rig.Sftp.ListDirectoryAsync(rig.SessionId, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(new List<RemoteFileInfo>()));
        rig.Sftp.UploadFileAsync(rig.SessionId, local, Arg.Any<string>(), Arg.Any<IProgress<TransferProgress>?>(),
                Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromException(new VelaSshClientException("通道被对端关闭")), _ => Task.CompletedTask);

        await rig.Browser.UploadLocalPathsAsync([local]);

        await rig.Sftp.Received(2).UploadFileAsync(rig.SessionId, local, Arg.Any<string>(),
            Arg.Any<IProgress<TransferProgress>?>(), Arg.Any<long>(), Arg.Any<CancellationToken>());
        Assert.AreEqual(TransferStatus.Completed, rig.Sink.Transfers.Single().Status);
    });

    private static Task OnUi(Func<Task> body) => _session.RunOnUiAsync(body);

    private static RemoteFileInfo Remote(long size) => new()
    {
        Name = "a.bin",
        FullPath = RemoteFile,
        Size = size,
        Permissions = "-rw-r--r--",
        IsDirectory = false,
        LastModified = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc),
        Owner = "u",
        Group = "g",
    };

    /// <summary>一个面板 + 一个传输浮窗 + 一个本地下载目录;重试等待换成立即返回并记下时长。</summary>
    private sealed class Rig : IDisposable
    {
        public Rig(int retries, bool resume = true)
        {
            Directory = Path.Combine(Path.GetTempPath(), "vela-retry-" + Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(Directory);
            Browser = new FileBrowserViewModel(Sftp, SessionId)
            {
                TransferSink = Sink,
                TransferOptions = new()
                {
                    ConflictPolicy = "overwrite",
                    ResumeEnabled = resume,
                    AutoCleanTempFiles = false,
                    TransferLogging = false,
                    NotifyOnComplete = false,
                    TransferMaxRetries = retries,
                },
                RetryDelay = (delay, _) =>
                {
                    Delays.Add(delay);
                    return Task.CompletedTask;
                },
            };
        }

        public Guid SessionId { get; } = Guid.NewGuid();

        public ISftpService Sftp { get; } = Substitute.For<ISftpService>();

        public FileTransferViewModel Sink { get; } = new(null);

        public FileBrowserViewModel Browser { get; }

        public string Directory { get; }

        public List<TimeSpan> Delays { get; } = [];

        public Task DownloadAsync() => Browser.DownloadRemoteEntriesAsync(
            [new RemoteFileInfoViewModel(Remote(100))],
            Directory);

        /// <summary>按次序列出每一次下载调用的起点。</summary>
        public List<(string Local, long Offset)> DownloadCalls() =>
        [
            .. Sftp.ReceivedCalls()
                .Where(c => c.GetMethodInfo().Name == nameof(ISftpService.DownloadFileAsync))
                .Select(c => ((string)c.GetArguments()[2]!, (long)c.GetArguments()[4]!)),
        ];

        /// <summary>模拟一次写了 <paramref name="bytes" /> 字节就断线的下载。</summary>
        public static Task WritePartialThenFail(NSubstitute.Core.CallInfo call, long bytes)
        {
            File.WriteAllBytes(call.ArgAt<string>(2), new byte[bytes]);
            call.ArgAt<IProgress<TransferProgress>?>(3)?.Report(new()
            {
                FileName = "a.bin",
                BytesTransferred = bytes,
                TotalBytes = 100,
                Percentage = (int)bytes,
                SpeedBytesPerSecond = 0,
                EstimatedTimeRemaining = TimeSpan.Zero,
            });
            return Task.FromException(new VelaSshConnectionException("连接被重置"));
        }

        public void Dispose()
        {
            try
            {
                System.IO.Directory.Delete(Directory, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
