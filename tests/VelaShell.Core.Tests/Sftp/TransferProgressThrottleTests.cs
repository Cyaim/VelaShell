using VelaShell.Core.Models;
using VelaShell.Core.Sftp;

namespace VelaShell.Core.Tests.Sftp;

[TestClass]
[TestCategory("Sftp")]
public class TransferProgressThrottleTests
{
    [TestMethod]
    public async Task Resume_ExistingPrefixIsProgressButNotSpeed()
    {
        const long start = 90_000_000;
        const long moved = 1_000;
        var reports = new List<TransferProgress>();
        var reporter = new TransferProgressThrottle(new CaptureProgress(reports.Add), "resume.bin", 100_000_000, start);

        reporter.Report(start);
        Assert.AreEqual(start, reports[^1].BytesTransferred);
        Assert.AreEqual(90, reports[^1].Percentage);
        Assert.AreEqual(0d, reports[^1].SpeedBytesPerSecond);
        await Task.Delay(150);
        reporter.ReportFinal(start + moved);

        TransferProgress final = reports[^1];
        Assert.AreEqual(start + moved, final.BytesTransferred);
        Assert.IsGreaterThan(0d, final.SpeedBytesPerSecond);
        Assert.IsLessThan(20_000d, final.SpeedBytesPerSecond, "速度只能计本次新增的 1000 字节,不能算已有的 90 MB。");
        Assert.AreEqual((100_000_000 - final.BytesTransferred) / final.SpeedBytesPerSecond,
            final.EstimatedTimeRemaining.TotalSeconds, 0.001d);
    }

    [TestMethod]
    public void Final_BypassesThrottleAndKeepsTheMaximum()
    {
        var reports = new List<TransferProgress>();
        var reporter = new TransferProgressThrottle(new CaptureProgress(reports.Add), "small.bin", 100);

        reporter.Report(99);
        reporter.ReportFinal(100);
        reporter.ReportFinal(80);

        Assert.HasCount(3, reports);
        Assert.AreEqual(99L, reports[0].BytesTransferred);
        Assert.AreEqual(100L, reports[1].BytesTransferred);
        Assert.AreEqual(100L, reports[2].BytesTransferred);
        Assert.AreEqual(100, reports[^1].Percentage);
    }

    [TestMethod]
    public void NoSink_DisablesProgress()
    {
        var reporter = new TransferProgressThrottle(null, "silent.bin", 100, 90);
        Assert.IsFalse(reporter.IsEnabled);
        reporter.Report(95);
        reporter.Advance(5);
        reporter.ReportFinal(100);
    }

    private sealed class CaptureProgress(Action<TransferProgress> capture) : IProgress<TransferProgress>
    {
        public void Report(TransferProgress value) => capture(value);
    }
}
