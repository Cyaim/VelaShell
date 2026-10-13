using Avalonia.Headless;
using Avalonia.Threading;
using VelaShell.Core.Models;
using VelaShell.Tests.TestSupport;
using VelaShell.ViewModels;

namespace VelaShell.Tests.ViewModels;

[TestClass]
[TestCategory("FileTransfer")]
public sealed class TransferProgressUiTests
{
    private static HeadlessUnitTestSession _session = null!;

    [ClassInitialize]
    public static void Init(TestContext _) =>
        _session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(TransferProgressUiTests).Assembly);

    [TestMethod]
    public async Task ProgressConsumer_LowSpeedFinalFrame_UpdatesOnTheUiThread() => await _session.RunOnUiAsync(async () =>
    {
        Assert.IsNotNull(SynchronizationContext.Current);
        var item = new TransferItemViewModel(new TransferTask
        {
            Id = Guid.NewGuid(),
            Type = TransferType.Upload,
            RemotePath = "/small.bin",
            LocalPath = "small.bin",
            Status = TransferStatus.InProgress
        });
        var consumed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        IProgress<TransferProgress> progress = new Progress<TransferProgress>(value =>
        {
            try
            {
                Assert.IsTrue(Dispatcher.UIThread.CheckAccess());
                item.UpdateProgress(value);
                consumed.SetResult();
            }
            catch (Exception ex)
            {
                consumed.SetException(ex);
            }
        });

        progress.Report(new TransferProgress
        {
            FileName = "small.bin",
            BytesTransferred = 101,
            TotalBytes = 101,
            Percentage = 100,
            SpeedBytesPerSecond = 0.5,
            EstimatedTimeRemaining = TimeSpan.Zero
        });
        await consumed.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.AreEqual(100, item.Progress);
        Assert.AreEqual(101L, item.TransferredBytes);
        Assert.AreEqual("0 B/s", item.Speed);
        Assert.AreEqual(TransferStatus.InProgress, item.Status);
    });
}
