using Microsoft.VisualStudio.TestTools.UnitTesting;
using VelaShell.Services.Update;

namespace VelaShell.Tests.Services;

/// <summary>
/// <see cref="UpdateDownloadTracker" /> 的行为:字节只增不减、速率会随心跳归零、
/// 停滞到阈值后置位。阈值与保鲜期都由构造参数给短值,测试不必真去等。
/// </summary>
[TestClass]
public sealed class UpdateDownloadTrackerTests
{
    private static readonly TimeSpan ShortStall = TimeSpan.FromMilliseconds(60);
    private static readonly TimeSpan ShortFreshness = TimeSpan.FromMilliseconds(30);

    [TestMethod]
    [TestCategory("Update")]
    public void Report_TracksBytesTotalAndPercent()
    {
        UpdateDownloadTracker tracker = new(ShortStall, ShortFreshness);

        tracker.Report(new UpdateDownloadProgress(2_500, 10_000, 1_024));

        Assert.AreEqual(2_500L, tracker.BytesDownloaded);
        Assert.AreEqual(10_000L, tracker.TotalBytes);
        Assert.AreEqual(25, tracker.Percent);
        Assert.AreEqual(1_024d, tracker.BytesPerSecond);
    }

    [TestMethod]
    [TestCategory("Update")]
    public void Report_BytesNeverGoBackwards()
    {
        UpdateDownloadTracker tracker = new(ShortStall, ShortFreshness);

        tracker.Report(new UpdateDownloadProgress(5_000, 10_000, 500));
        tracker.Report(new UpdateDownloadProgress(1_000, 10_000, 0));

        Assert.AreEqual(5_000L, tracker.BytesDownloaded);
        // 速率只认正数:0 表示"这一拍没数据",不能把上一个值抹掉。
        Assert.AreEqual(500d, tracker.BytesPerSecond);
    }

    [TestMethod]
    [TestCategory("Update")]
    public void Report_UnknownTotal_LeavesPercentAtZero()
    {
        UpdateDownloadTracker tracker = new(ShortStall, ShortFreshness);

        tracker.Report(new UpdateDownloadProgress(4_096, 0, 900));

        Assert.AreEqual(0, tracker.Percent);
        Assert.AreEqual(0L, tracker.TotalBytes);
    }

    [TestMethod]
    [TestCategory("Update")]
    public async Task Tick_ZeroesStaleSpeed()
    {
        UpdateDownloadTracker tracker = new(ShortStall, ShortFreshness);
        tracker.Report(new UpdateDownloadProgress(1_000, 10_000, 2_048));

        tracker.Tick();
        Assert.AreEqual(2_048d, tracker.BytesPerSecond, "保鲜期内刚报过的速率不能立刻抹掉。");

        await Task.Delay(ShortFreshness + TimeSpan.FromMilliseconds(30));
        tracker.Tick();
        Assert.AreEqual(0d, tracker.BytesPerSecond);
    }

    [TestMethod]
    [TestCategory("Update")]
    public async Task Report_ResetsStallClock()
    {
        UpdateDownloadTracker tracker = new(ShortStall, ShortFreshness);
        tracker.Report(new UpdateDownloadProgress(1_000, 10_000, 2_048));

        await Task.Delay(ShortStall + TimeSpan.FromMilliseconds(30));
        Assert.IsTrue(tracker.IsStalled, "长时间没有新字节就算停滞。");

        // 新字节一到,停滞判定就该撤销——界面回到正常的「百分比 · 速率」文案。
        tracker.Report(new UpdateDownloadProgress(1_200, 10_000, 2_048));

        Assert.IsFalse(tracker.IsStalled);
    }

    [TestMethod]
    [TestCategory("Update")]
    public void DefaultThresholds_MatchTheDocumentedValues()
    {
        Assert.AreEqual(TimeSpan.FromSeconds(10), UpdateDownloadTracker.StallThreshold);
        Assert.AreEqual(TimeSpan.FromSeconds(1), UpdateDownloadTracker.SpeedFreshness);
    }
}