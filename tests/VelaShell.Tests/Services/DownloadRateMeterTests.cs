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
    public void Sample_SlidesSmoothlyInsteadOfRestartingTheWindow()
    {
        long now = 1_000;
        DownloadRateMeter meter = new(() => now);
        meter.Sample(0);

        // 一阵一阵地到:每 250 毫秒一拍,隔一拍来 20 KB,平均 40 KB/s。窗口若是"走完就从零重开",
        // 重开后的头几拍只看得到一两拍,速率会在 0 到 80 KB/s 之间乱跳;真滑动窗口始终是整窗的平均。
        long bytes = 0;
        for (int tick = 1; tick <= 40; tick++)
        {
            now += 250;
            if (tick % 2 == 1)
            {
                bytes += 20_000;
            }
            double rate = meter.Sample(bytes);
            if (now - 1_000 >= DownloadRateMeter.WindowMilliseconds)
            {
                Assert.AreEqual(40_000d, rate, 7_000, $"第 {tick} 拍(t={now})速率跳出了整窗平均,实际 {rate}");
            }
        }
    }

    [TestMethod]
    [TestCategory("Update")]
    public void Sample_ResumeAfterLongStall_DoesNotReportPreStallRate()
    {
        long now = 1_000;
        DownloadRateMeter meter = new(() => now);
        meter.Sample(0);
        for (int i = 1; i <= 12; i++)
        {
            now += 250;
            meter.Sample(10_000L * i);
        }

        // 停了 10 秒(期间没有任何汇报)后恢复:第一拍不能把停滞前那个 40 KB/s 当成现在的网速报出去。
        now += 10_000;
        Assert.AreEqual(0d, meter.Sample(130_000));

        // 再来一拍就是新鲜的实测值:1 秒 20 KB。
        now += 1_000;
        Assert.AreEqual(20_000d, meter.Sample(150_000), 1);
    }

    [TestMethod]
    [TestCategory("Update")]
    public void Sample_InterleavedSegmentReports_NeverGoBackwards()
    {
        long now = 1_000;
        DownloadRateMeter meter = new(() => now);
        meter.Sample(0);
        now += 1_000;
        double rate = meter.Sample(80_000);

        // 分段并发时,先算出累计值的段可能后汇报:倒退的累计值不能把速率拉成负数。
        now += 1;
        Assert.IsGreaterThanOrEqualTo(rate * 0.9, meter.Sample(60_000));
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
