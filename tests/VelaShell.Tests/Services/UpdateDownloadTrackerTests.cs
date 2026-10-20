using VelaShell.Services.Update;

namespace VelaShell.Tests.Services;

/// <summary>
/// <see cref="UpdateDownloadTracker" /> 的行为:字节只增不减、速率会随心跳归零、
/// 停滞到阈值后置位。时钟由构造参数注入,用例不依赖真实流逝的时间,繁忙的 CI 上也不会抖。
/// </summary>
[TestClass]
public sealed class UpdateDownloadTrackerTests
{
    private static readonly TimeSpan ShortStall = TimeSpan.FromMilliseconds(60);
    private static readonly TimeSpan ShortFreshness = TimeSpan.FromMilliseconds(30);

    private long _now = 1_000;

    private UpdateDownloadTracker CreateTracker() => new(ShortStall, ShortFreshness, () => _now);

    [TestMethod]
    [TestCategory("Update")]
    public void Report_TracksBytesTotalAndPercent()
    {
        UpdateDownloadTracker tracker = CreateTracker();

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
        UpdateDownloadTracker tracker = CreateTracker();

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
        UpdateDownloadTracker tracker = CreateTracker();

        tracker.Report(new UpdateDownloadProgress(4_096, 0, 900));

        Assert.AreEqual(0, tracker.Percent);
        Assert.AreEqual(0L, tracker.TotalBytes);
        Assert.AreEqual(4_096L, tracker.BytesDownloaded);
    }

    [TestMethod]
    [TestCategory("Update")]
    public void Tick_ZeroesStaleSpeed()
    {
        UpdateDownloadTracker tracker = CreateTracker();
        tracker.Report(new UpdateDownloadProgress(1_000, 10_000, 2_048));

        _now += (long)ShortFreshness.TotalMilliseconds - 1;
        tracker.Tick();
        Assert.AreEqual(2_048d, tracker.BytesPerSecond, "保鲜期内刚报过的速率不能立刻抹掉。");

        _now += 1;
        tracker.Tick();
        Assert.AreEqual(0d, tracker.BytesPerSecond);
    }

    [TestMethod]
    [TestCategory("Update")]
    public void Report_ResetsStallClock()
    {
        UpdateDownloadTracker tracker = CreateTracker();
        tracker.Report(new UpdateDownloadProgress(1_000, 10_000, 2_048));

        _now += (long)ShortStall.TotalMilliseconds;
        Assert.IsTrue(tracker.IsStalled, "长时间没有新字节就算停滞。");
        Assert.AreEqual(ShortStall, tracker.StalledFor);

        // 新字节一到,停滞判定就该撤销——界面回到正常的「百分比 · 速率」文案。
        tracker.Report(new UpdateDownloadProgress(1_200, 10_000, 2_048));

        Assert.IsFalse(tracker.IsStalled);
    }

    [TestMethod]
    [TestCategory("Update")]
    public void BeforeFirstReport_IsPreparingAndNeverStalled()
    {
        UpdateDownloadTracker tracker = CreateTracker();

        // 连接很慢、或正在校验上次留下的断点前缀:一个字节都还没收到,但这不是"连接可能已经中断"。
        _now += 60_000;
        tracker.Tick();

        Assert.IsTrue(tracker.IsPreparing);
        Assert.IsFalse(tracker.IsStalled);
        Assert.AreEqual(TimeSpan.Zero, tracker.StalledFor);

        // 第一条汇报到达:准备阶段结束,停滞从这一刻起算,而不是从开始连接起算。
        tracker.Report(new UpdateDownloadProgress(4_096, 10_000, 0));
        Assert.IsFalse(tracker.IsPreparing);
        Assert.IsFalse(tracker.IsStalled);
        _now += (long)ShortStall.TotalMilliseconds;
        Assert.IsTrue(tracker.IsStalled);
    }

    [TestMethod]
    [TestCategory("Update")]
    public void DefaultThresholds_MatchTheDocumentedValues()
    {
        Assert.AreEqual(TimeSpan.FromSeconds(10), UpdateDownloadTracker.StallThreshold);
        Assert.AreEqual(TimeSpan.FromSeconds(1), UpdateDownloadTracker.SpeedFreshness);
    }
}
