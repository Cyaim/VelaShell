using VelaShell.Services.Update;

namespace VelaShell.Tests.Services;

/// <summary>
/// <see cref="DownloadRateMeter" /> 的行为:滑动窗口跟随当前网速、窗口走完能滚动、
/// 长时间无数据时归零。时钟由构造参数注入,时间窗在测试里瞬间跑完。
/// </summary>
[TestClass]
public sealed class DownloadRateMeterTests
{
    [TestMethod]
    [TestCategory("Update")]
    public void Sample_FirstCall_OnlyEstablishesBaseline()
    {
        long now = 1_000;
        DownloadRateMeter meter = new(() => now);

        Assert.AreEqual(0d, meter.Sample(0));
        Assert.AreEqual(0d, meter.Sample(50_000));
    }

    [TestMethod]
    [TestCategory("Update")]
    public void Sample_ReportsBytesPerSecondWithinWindow()
    {
        long now = 1_000;
        DownloadRateMeter meter = new(() => now);
        meter.Sample(0);

        now += 1_000;
        double rate = meter.Sample(50_000);

        Assert.AreEqual(50_000d, rate, 1);
    }

    [TestMethod]
    [TestCategory("Update")]
    public void Sample_WindowRollsSoSpeedFollowsCurrentNetwork()
    {
        long now = 1_000;
        DownloadRateMeter meter = new(() => now);
        meter.Sample(0);

        // 前 4 秒很快,随后彻底停住。
        for (int i = 0; i < 4; i++)
        {
            now += 1_000;
            meter.Sample(100_000L * (i + 1));
        }
        double fast = meter.Sample(400_000);

        // 再喂四次"没有新字节":窗口一个字节都没收到,速率必须掉到 0,
        // 而不是继续挂着前面那个好看的数字。
        double stalled = fast;
        for (int i = 0; i < 4; i++)
        {
            now += 1_000;
            stalled = meter.Sample(400_000);
        }

        Assert.IsGreaterThan(0, fast);
        Assert.AreEqual(0d, stalled, 0.001);
    }

    [TestMethod]
    [TestCategory("Update")]
    public void Sample_DoesNotFlashZeroRightAfterWindowRolls()
    {
        long now = 1_000;
        DownloadRateMeter meter = new(() => now);
        meter.Sample(0);
        now += 500;
        meter.Sample(25_000);
        double before = meter.Sample(25_000);

        // 正好跨过窗口边界、还没有新数据:窗口会滚到"最后一次收到字节"的时刻,
        // 但整窗零增益不等于停住(才过了一秒多),应沿用上一个速率,别闪一个 0。
        now = 1_000 + DownloadRateMeter.WindowMilliseconds;
        Assert.AreEqual(before, meter.Sample(25_000));
    }

    [TestMethod]
    [TestCategory("Update")]
    public void Sample_SlowDownload_ReportsSmallNonZeroRate()
    {
        long now = 1_000;
        DownloadRateMeter meter = new(() => now);
        meter.Sample(0);

        // 一秒 8 KB:网慢到几乎不动,但确实还在下,不能报 0。
        now += 2_000;
        double rate = meter.Sample(16_384);

        Assert.IsGreaterThan(0, rate, $"慢速下载也应报出非零速率,实际 {rate}");
        Assert.IsLessThan(20_000, rate, $"速率不该被放大,实际 {rate}");
    }
}
