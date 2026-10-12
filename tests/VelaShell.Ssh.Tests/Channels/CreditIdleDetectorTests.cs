// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 被测规格: velashell-docs/zh/ssh/spec/05-connection.md §3.3（扩窗认「空等」）
//
// 判据只吃给定的时刻，这里按到达序列逐拍核对，不经链路模拟。
// 曾经用链路模拟测「带宽才是瓶颈时窗口不白长」：模拟器在计时器醒晚时把积压的报文一口气补发，
// macOS 的 CI 上「匀速到达」成了「成批到、批间空着」，窗口照规矩长了上去，用例失败 —— 测到的不是它想测的东西。

using VelaShell.Ssh.Channels;

namespace VelaShell.Ssh.Tests.Channels;

[TestClass]
[TestCategory("Channels")]
public sealed class CreditIdleDetectorTests
{
    /// <summary>一轮 4 个报文（256 KiB 窗口、32 KiB 报文，回补在半窗处）。</summary>
    private const int PacketsPerRound = 4;

    /// <summary>
    /// 往一个判据里按给定的间隔一包一包送数据，每 <see cref="PacketsPerRound"/> 包结束一轮（回补）。
    /// 时刻跨多次 <see cref="Send"/> 连续累加，从 1 起（0 表示「还没有」）。
    /// </summary>
    private sealed class Feed
    {
        private long _now = 1;
        private int _packets;

        public CreditIdleDetector Detector { get; } = new();

        /// <summary>送这些包，返回这期间每轮结束时的判定。</summary>
        public List<bool> Send(IEnumerable<long> gapsBeforeEachPacket, bool drained = true, bool eligible = true)
        {
            List<bool> rounds = [];
            foreach (long gap in gapsBeforeEachPacket)
            {
                _now += gap;
                // 通道的第一包之前读的一方本来就空着，那不算等
                Detector.NoteArrival(_now, readerDrained: drained && _packets > 0);
                if (++_packets % PacketsPerRound == 0)
                {
                    rounds.Add(Detector.EndRound(_now, eligible));
                }
            }
            return rounds;
        }
    }

    /// <summary>匀速到达：每包隔 <paramref name="spacing"/>。</summary>
    private static IEnumerable<long> Even(int packets, long spacing) => Enumerable.Repeat(spacing, packets);

    /// <summary>成批到达：每批 <paramref name="burst"/> 包挨着到（隔 1），批与批之间空 <paramref name="roundTrip"/>。</summary>
    private static IEnumerable<long> Bursts(int batches, int burst, long roundTrip)
    {
        for (int b = 0; b < batches; b++)
        {
            for (int p = 0; p < burst; p++)
            {
                yield return p == 0 ? roundTrip : 1;
            }
        }
    }

    [TestMethod]
    public void 匀速到达不算空等()
    {
        // 带宽是瓶颈：对端手里一直有额度，最长的空档只是一个报文的传输时间，占两轮（8 个报文）的 1/8。
        List<bool> rounds = new Feed().Send(Even(packets: 80, spacing: 1000));

        Assert.IsFalse(rounds.Any(idle => idle), "匀速到达时最长空档只占两轮的 1/8,不该算空等");
    }

    [TestMethod]
    public void 成批到达批间空着一个往返算空等()
    {
        // 窗口是瓶颈：对端一口气发完一窗额度（8 包），停下来等回补，空一个往返。
        List<bool> rounds = new Feed().Send(Bursts(batches: 10, burst: 8, roundTrip: 10_000));

        // 前两轮还没有「上上次回补」可比；之后每一轮都算空等（空档落在两轮中的一轮里，看的是两轮）。
        Assert.HasCount(20, rounds);
        Assert.IsTrue(rounds.Skip(2).All(idle => idle), $"成批到达时每轮都该算空等,实际 [{string.Join(", ", rounds)}]");
    }

    [TestMethod]
    public void 读的一方没读空时再长的间隔也不算()
    {
        // 读得慢：管道里总有没读完的。那时的空档不是在等额度。
        List<bool> rounds = new Feed().Send(Bursts(batches: 10, burst: 8, roundTrip: 10_000), drained: false);

        Assert.IsFalse(rounds.Any(idle => idle), "读的一方没读空时不该算空等");
    }

    [TestMethod]
    public void 窗口不到四个报文时恒为否()
    {
        // 窗口只有一两个报文时两轮本来就没几包，匀速到达的空档也占得到 1/4；那时只认见底。
        List<bool> rounds = new Feed().Send(Bursts(batches: 10, burst: 8, roundTrip: 10_000), eligible: false);

        Assert.IsFalse(rounds.Any(idle => idle));
    }

    [TestMethod]
    public void 扩过一次之后上一轮的空档作废()
    {
        // 一段空档会落在相邻的两次判定里（看的是两轮）。扩过一次就把上一轮的作废，同一段空档不连扩两回。
        Feed withDiscard = new(), withoutDiscard = new();
        long[] gapRound = [5000, 1000, 1000, 1000];   // 一轮里有一段长空档
        long[] evenRound = [1000, 1000, 1000, 1000];  // 接着一轮匀速
        foreach (Feed feed in (Feed[])[withDiscard, withoutDiscard])
        {
            feed.Send(Even(packets: 8, spacing: 1000));   // 先攒够两轮,有「上上次回补」可比
            Assert.IsTrue(feed.Send(gapRound).Single(), "有长空档的那一轮算空等");
        }

        withDiscard.Detector.Discard();
        Assert.IsFalse(withDiscard.Send(evenRound).Single(), "扩过一次之后,上一轮的空档不该再让下一轮也算空等");
        Assert.IsTrue(withoutDiscard.Send(evenRound).Single(), "前提:不作废的话,上一轮的空档会让下一轮也算空等");
    }

    [TestMethod]
    public void 没有上上次回补可比时不判()
    {
        CreditIdleDetector detector = new();
        detector.NoteArrival(1, readerDrained: false);
        detector.NoteArrival(100_000, readerDrained: true);   // 很长的一段空档

        Assert.IsFalse(detector.EndRound(100_001, eligible: true), "第一次回补:还不知道两轮有多长");
        Assert.IsFalse(detector.EndRound(100_002, eligible: true), "第二次回补:同上");
    }
}
