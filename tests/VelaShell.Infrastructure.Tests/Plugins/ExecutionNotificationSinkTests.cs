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
        var producing = Task.Run(async () =>
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
            Assert.AreEqual((i + 1, i + 1L), sent[i]);
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

        async Task ProduceAsync()
        {
            for (int i = 0; i < 1000; i++)
            {
                await sink.ReportAsync(i, CancellationToken.None);
            }
        }
        // 直接启动到队列满为止,不靠延时猜测生产者是否已经开始等待。
        Task producing = ProduceAsync();
        Assert.IsFalse(producing.IsCompleted);
        gate.SetResult();

        // 不靠取消令牌:写泵一失败,正等空位的生产者立刻带着失败返回。
        InvalidOperationException error = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => producing.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.IsInstanceOfType<IOException>(error.InnerException);
        // 生产者被唤醒不代表失败回调已执行;Drain 等写泵和回调都完成后再检查计数。
        await Assert.ThrowsExactlyAsync<IOException>(
            () => sink.DrainAsync().WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.AreEqual(1, Volatile.Read(ref failures));
    }

    [TestMethod]
    public async Task DeliveryFailure_ProducerCanFailBeforeCallbackCompletes_AndDrainWaitsForCallback()
    {
        int failures = 0;
        var sending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var failSend = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callbackEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using ManualResetEventSlim releaseCallback = new(false);
        var sink = new ExecutionNotificationSink<int>(async (_, _) =>
        {
            sending.SetResult();
            await failSend.Task;
            throw new IOException("pipe broken");
        }, () =>
        {
            callbackEntered.SetResult();
            // 受控地停在计数之前,让三个平台都能覆盖 CI 曾撞到的先后顺序。
            releaseCallback.Wait(TimeSpan.FromSeconds(30));
            Interlocked.Increment(ref failures);
        });

        try
        {
            await sink.ReportAsync(0, CancellationToken.None);
            await sending.Task.WaitAsync(TimeSpan.FromSeconds(5));
            for (int i = 1; i <= 256; i++)
            {
                await sink.ReportAsync(i, CancellationToken.None);
            }
            Task producing = sink.ReportAsync(257, CancellationToken.None).AsTask();
            Assert.IsFalse(producing.IsCompleted);
            failSend.SetResult();

            await callbackEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            InvalidOperationException error = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
                () => producing.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.IsInstanceOfType<IOException>(error.InnerException);
            Assert.AreEqual(0, Volatile.Read(ref failures));

            Task draining = sink.DrainAsync();
            Assert.IsFalse(draining.IsCompleted, "Drain 必须等失败回调结束,不能只等生产者收到错误");
            releaseCallback.Set();
            await Assert.ThrowsExactlyAsync<IOException>(
                () => draining.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.AreEqual(1, Volatile.Read(ref failures));
        }
        finally
        {
            failSend.TrySetResult();
            releaseCallback.Set();
            // 即使前面的断言失败,也先等写泵退出,再释放回调仍可能使用的同步对象。
            await Assert.ThrowsExactlyAsync<IOException>(
                () => sink.DrainAsync().WaitAsync(TimeSpan.FromSeconds(5)));
        }
    }

    [TestMethod]
    public async Task WaitingProducer_HonoursCancellation()
    {
        var never = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sink = new ExecutionNotificationSink<int>((_, _) => never.Task);
        using CancellationTokenSource cancel = new();
        var producing = Task.Run(async () =>
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
        Assert.AreSequenceEqual([7], [.. sent]);
    }
}
