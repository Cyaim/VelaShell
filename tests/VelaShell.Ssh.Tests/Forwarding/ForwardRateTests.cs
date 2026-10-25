// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 被测规格: velashell-docs/zh/ssh/spec/07-forwarding.md §5.1

using VelaShell.Ssh.Forwarding;
using VelaShell.Ssh.Tests.TestKit;

namespace VelaShell.Ssh.Tests.Forwarding;

/// <summary>吞吐的滑动窗口与令牌桶限速，用手动拨的时钟算得一清二楚。</summary>
[TestClass]
[TestCategory("Forwarding")]
public sealed class ForwardRateTests
{
    /// <summary>报的是最近三个整秒的平均，不含正在走的这一秒；窗口外的不算。</summary>
    [TestMethod]
    public void 吞吐取最近三个整秒的平均()
    {
        ManualTimeProvider time = new();
        ThroughputMeter meter = new(time);

        meter.Record(3000);                         // 第 0 秒
        time.Advance(TimeSpan.FromSeconds(1));
        meter.Record(6000);                         // 第 1 秒
        Assert.AreEqual(1000, meter.PerSecond, "正在走的第 1 秒不算：(3000) / 3");

        time.Advance(TimeSpan.FromSeconds(1));
        Assert.AreEqual(3000, meter.PerSecond, "(3000 + 6000) / 3");

        time.Advance(TimeSpan.FromSeconds(2));      // 第 4 秒：窗口是 1–3 秒
        Assert.AreEqual(2000, meter.PerSecond, "第 0 秒滑出窗口：6000 / 3");

        time.Advance(TimeSpan.FromSeconds(10));
        Assert.AreEqual(0, meter.PerSecond, "早就没动静了");
    }

    /// <summary>一秒的额度可以一次用掉；之后欠着、按欠额等；歇够了额度补回来、但不超过一秒的量。</summary>
    [TestMethod]
    public void 令牌桶允许一秒的突发_之后按速率等()
    {
        ManualTimeProvider time = new();
        ByteRateLimiter limiter = new(1000, time);

        Assert.AreEqual(TimeSpan.Zero, limiter.Reserve(1000), "一秒的额度可以一次用掉");
        Assert.AreEqual(TimeSpan.FromSeconds(0.5), limiter.Reserve(500), "欠 500，按 1000/秒要等半秒");

        time.Advance(TimeSpan.FromSeconds(0.5));
        Assert.AreEqual(TimeSpan.FromSeconds(0.5), limiter.Reserve(500), "补回来的刚好还清旧账，新的 500 又要等半秒");

        time.Advance(TimeSpan.FromSeconds(60));
        Assert.AreEqual(TimeSpan.Zero, limiter.Reserve(1000), "歇了很久：额度补满，但不超过一秒的量");
        Assert.AreNotEqual(TimeSpan.Zero, limiter.Reserve(1), "补满也只有一秒的量");
    }

    /// <summary>
    /// 要得再多，一次也只放行一秒的额度，剩下的下一次再要 —— 不是整块记账、整块等完再整块放行
    /// （那样单次突发就是好几秒的额度）。要得比额度少时照要的给。
    /// </summary>
    [TestMethod]
    public async Task 一次至多放行一秒的额度()
    {
        ManualTimeProvider time = new();
        ByteRateLimiter limiter = new(1000, time);

        ValueTask<int> first = limiter.AcquireAsync(5000, CancellationToken.None);
        Assert.IsTrue(first.IsCompletedSuccessfully, "额度在手：当场放行");
        Assert.AreEqual(1000, await first, "要五秒的量，只放一秒的");

        Task<int> second = limiter.AcquireAsync(5000, CancellationToken.None).AsTask();
        Assert.IsFalse(second.IsCompleted, "额度用完了：欠着、等一秒");
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.AreEqual(1000, await second.WaitAsync(TimeSpan.FromSeconds(10)));

        time.Advance(TimeSpan.FromSeconds(2));
        Assert.AreEqual(300, await limiter.AcquireAsync(300, CancellationToken.None), "比额度少的照要的给");
    }

    /// <summary>限速必须为正；不限给 null。</summary>
    [TestMethod]
    public void 限速参数不为正当场报()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new LocalPortForwardOptions { MaxBytesPerSecond = 0 });
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new RemotePortForwardOptions { MaxBytesPerSecond = -1 });
        Assert.IsNull(new LocalPortForwardOptions { MaxBytesPerSecond = null }.MaxBytesPerSecond);
    }
}
