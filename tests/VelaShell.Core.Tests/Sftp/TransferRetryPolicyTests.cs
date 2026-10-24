using System.Net.Sockets;
using VelaShell.Core.Ftp;
using VelaShell.Core.Protocols;
using VelaShell.Core.Sftp;
using VelaShell.Core.Ssh;

namespace VelaShell.Core.Tests.Sftp;

/// <summary>
/// 传输失败重试的口径:哪些失败重试、哪些直接失败、哪些留给换一条连接再接着传;退避与次数上限。
/// </summary>
[TestClass]
[TestCategory("Sftp")]
public sealed class TransferRetryPolicyTests
{
    private const int DiskFullHResult = unchecked((int)0x80070070);

    public static IEnumerable<object[]> TransientFailures =>
    [
        [new VelaSshConnectionException("连接断了")],
        [new VelaSshOperationTimeoutException("超时")],
        [new VelaSftpTransferInterruptedException("中断", 4096)],
        [new VelaSshClientException("通道被对端关闭")],
        [new VelaFtpConnectionException("数据连接掉了")],
        [new PluginProtocolConnectionException("端点不可达")],
        [new TimeoutException()],
        [new SocketException((int)SocketError.ConnectionReset)],
        [new IOException("无法从传输连接中读取数据", new SocketException((int)SocketError.ConnectionAborted))],
        [new IOException("读到一半断掉了")],
        [new EndOfStreamException()],
    ];

    public static IEnumerable<object[]> PermanentFailures =>
    [
        [new VelaSftpPermissionDeniedException("权限拒绝")],
        [new VelaSftpPathNotFoundException("远端不存在")],
        [new VelaSftpOperationException("Failure: No space left on device")],
        [new VelaSftpResumeMismatchException("半截对不上")],
        [new VelaSftpTransferInterruptedException("服务端拒写", 4096) { RefusedByServer = true }],
        [new VelaSshAuthenticationException("认证失败")],
        [new VelaSshHostKeyRejectedException("指纹被拒")],
        [new VelaSshAuthenticationCancelledException("用户取消了动态码")],
        [new VelaFtpPermissionDeniedException("550")],
        [new VelaFtpPathNotFoundException("550 not found")],
        [new VelaFtpAuthenticationException("530")],
        [new PluginProtocolAuthenticationException("凭据无效")],
        [new PluginProtocolUnavailableException("s3", "插件被停用")],
        [new UnauthorizedAccessException("本地无权限")],
        [new FileNotFoundException("本地文件不见了")],
        [new DirectoryNotFoundException("本地目录不见了")],
        [new IOException("磁盘已满", DiskFullHResult)],
        [new OperationCanceledException()],
        [new InvalidOperationException("不认识的错误")],
        [new NotSupportedException("协议不支持")],
        [new ArgumentException("参数不对")],
    ];

    [TestMethod]
    [DynamicData(nameof(TransientFailures))]
    public void LinkLevelFailures_AreRetried(Exception failure) =>
        Assert.AreEqual(TransferFailureKind.Transient, TransferRetryPolicy.Classify(failure), failure.GetType().Name);

    [TestMethod]
    [DynamicData(nameof(PermanentFailures))]
    public void FailuresOfTheFileItself_AreNotRetried(Exception failure) =>
        Assert.AreEqual(TransferFailureKind.Permanent, TransferRetryPolicy.Classify(failure), failure.GetType().Name);

    [TestMethod]
    public void AGoneSession_IsNeitherRetriedInPlaceNorAFailureOfTheFile()
    {
        Assert.AreEqual(TransferFailureKind.SessionLost,
            TransferRetryPolicy.Classify(new RemoteSessionUnavailableException("Session x not found")));
        Assert.AreEqual(TransferFailureKind.SessionLost,
            TransferRetryPolicy.Classify(new ObjectDisposedException("SftpFileSystem")));
    }

    [TestMethod]
    public void UnknownWrappers_AreJudgedByTheirInnerException()
    {
        var wrapped = new InvalidOperationException("包了一层", new VelaSshConnectionException("连接断了"));

        Assert.AreEqual(TransferFailureKind.Transient, TransferRetryPolicy.Classify(wrapped));
    }

    [TestMethod]
    public void AnAggregateWithOnePermanentFailure_IsNotRetried()
    {
        var mixed = new AggregateException(new TimeoutException(), new VelaSftpPermissionDeniedException("权限拒绝"));
        var allTransient = new AggregateException(new TimeoutException(), new VelaSshConnectionException("断了"));

        Assert.AreEqual(TransferFailureKind.Permanent, TransferRetryPolicy.Classify(mixed));
        Assert.AreEqual(TransferFailureKind.Transient, TransferRetryPolicy.Classify(allTransient));
    }

    [TestMethod]
    public void UnixDiskFull_IsNotRetried() =>
        // Unix 上 .NET 把 errno 原样放进 HResult(ENOSPC = 28)。
        Assert.AreEqual(TransferFailureKind.Permanent, TransferRetryPolicy.Classify(new IOException("No space left on device", 28)));

    [TestMethod]
    public void BackoffDoubles_AndIsCapped()
    {
        double[] seconds = [.. Enumerable.Range(1, 8).Select(n => TransferRetryPolicy.DelayBeforeRetry(n).TotalSeconds)];

        CollectionAssert.AreEqual(new double[] { 1, 2, 4, 8, 16, 30, 30, 30 }, seconds);
        Assert.AreEqual(TimeSpan.Zero, TransferRetryPolicy.DelayBeforeRetry(0));
        Assert.AreEqual(TransferRetryPolicy.MaxDelay, TransferRetryPolicy.DelayBeforeRetry(int.MaxValue));
    }

    [TestMethod]
    public void RetryCount_IsClampedToTheSettingsRange()
    {
        Assert.AreEqual(0, TransferRetryPolicy.Clamp(-3));
        Assert.AreEqual(3, TransferRetryPolicy.Clamp(3));
        Assert.AreEqual(TransferRetryPolicy.MaxRetries, TransferRetryPolicy.Clamp(1_000));
    }
}
