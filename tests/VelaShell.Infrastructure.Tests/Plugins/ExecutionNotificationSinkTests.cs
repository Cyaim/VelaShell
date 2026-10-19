using VelaShell.Infrastructure.Plugins.Isolated;

namespace VelaShell.Infrastructure.Tests.Plugins;

/// <summary>
/// 隔离插件执行通知的发送队列:能等的生产者被背压住,而不是一冲就溢出把执行判失败;
/// 真正的投递失败仍然明确地报出来,不静默丢输出。
/// </summary>
[TestClass]
public sealed class ExecutionNotificationSinkTests
{
    [TestMethod]
    public async Task AwaitingProducer_IsHeldBackInsteadOfOverflowing_AndKeepsOrderAndSequence()
    {
        using SemaphoreSlim slots = new(0);
        List<(int Value, long Sequence)> sent = [];
        var sink = new ExecutionNotificationSink<int>(async (value, sequence) =>
        {
            await slots.WaitAsync();
            lock (sent) { sent.Add((value, sequence)); }
        });

        // 一段 4096 字符的 `ls -1` 输出就能切出上千行:远超队列的 256。
        const int count = 2000;
        Task producing = Task.Run(async () =>
        {
            for (int i = 1; i <= count; i++)
            {
                await sink.ReportAsync(i, CancellationToken.None);
            }
        });
        await Task.Delay(200);
        Assert.IsFalse(producing.IsCompleted, "发送端没腾出位置时,生产者应当被挡住");
        Assert.IsFalse(producing.IsFaulted);

        slots.Release(count);
        await producing.WaitAsync(TimeSpan.FromSeconds(10));
        await sink.DrainAsync().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.HasCount(count, sent);
        for (int i = 0; i < count; i++)
        {
            Assert.AreEqual((i + 1, (long)(i + 1)), sent[i]);
        }
    }

    [TestMethod]
    public async Task DeliveryFailure_WakesTheWaitingProducerWithAnExplicitError()
    {
        int failures = 0;
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sink = new ExecutionNotificationSink<int>(async (_, _) =>
        {
            await gate.Task;
            throw new IOException("pipe broken");
        }, () => Interlocked.Increment(ref failures));

        Task producing = Task.Run(async () =>
        {
            for (int i = 0; i < 1000; i++)
            {
                await sink.ReportAsync(i, CancellationToken.None);
            }
        });
        await Task.Delay(100);
        Assert.IsFalse(producing.IsCompleted);
        gate.SetResult();

        // 不靠取消令牌:写泵一失败,正等空位的生产者立刻带着失败返回。
        InvalidOperationException error = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => producing.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.IsInstanceOfType<IOException>(error.InnerException);
        Assert.AreEqual(1, failures);
        await Assert.ThrowsExactlyAsync<IOException>(() => sink.DrainAsync());
    }

    [TestMethod]
    public async Task WaitingProducer_HonoursCancellation()
    {
        var never = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sink = new ExecutionNotificationSink<int>((_, _) => never.Task);
        using CancellationTokenSource cancel = new();
        Task producing = Task.Run(async () =>
        {
            for (int i = 0; ; i++)
            {
                await sink.ReportAsync(i, cancel.Token);
            }
        });
        await Task.Delay(100);
        await cancel.CancelAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(() => producing.WaitAsync(TimeSpan.FromSeconds(5)));
        never.SetResult();
        await sink.DrainAsync().WaitAsync(TimeSpan.FromSeconds(5));
    }

    [TestMethod]
    public async Task ImmediateReport_StillEnqueuesWhenTheTokenIsAlreadyCancelled()
    {
        // Running 这类已经发生的事实不能因为同时到来的死线而丢:有空位就入队,不看取消。
        List<int> sent = [];
        var sink = new ExecutionNotificationSink<int>((value, _) =>
        {
            lock (sent) { sent.Add(value); }
            return Task.CompletedTask;
        });
        using CancellationTokenSource cancelled = new();
        await cancelled.CancelAsync();
        await sink.ReportAsync(7, cancelled.Token);
        await sink.DrainAsync();
        Assert.AreSequenceEqual((int[])[7], [.. sent]);
    }
}
