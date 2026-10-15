using VelaShell.Infrastructure.Plugins.Isolated;

namespace VelaShell.Infrastructure.Tests.Plugins;

[TestClass]
public class ExecutionOperationsTests
{
    [TestMethod]
    public async Task EarlyCancellation_IsObservedBeforeExecutionStarts()
    {
        await using var operations = new ExecutionOperations(CancellationToken.None);
        operations.Cancel("early");
        await using ExecutionOperations.Operation operation = operations.Register("early", CancellationToken.None);
        Assert.IsTrue(operation.Token.IsCancellationRequested);
        Assert.AreEqual(1, operations.ActiveCount);
    }

    [TestMethod]
    public async Task Completion_ReleasesBusyCount_AndLateCancelDoesNotLeak()
    {
        await using var operations = new ExecutionOperations(CancellationToken.None);
        ExecutionOperations.Operation operation = operations.Register("op", CancellationToken.None);
        Assert.AreEqual(1, operations.ActiveCount);
        await operation.DisposeAsync();
        Assert.AreEqual(0, operations.ActiveCount);
        for (int i = 0; i < 200; i++) { operations.Cancel("op"); }
        await using ExecutionOperations.Operation next = operations.Register("next", CancellationToken.None);
        Assert.IsFalse(next.Token.IsCancellationRequested);
        Assert.ThrowsExactly<InvalidOperationException>(() => operations.Register("op", CancellationToken.None));
    }

    [TestMethod]
    public async Task Shutdown_CancelsEveryOperation_AndDisposeWaitsForCleanup()
    {
        using var shutdown = new CancellationTokenSource();
        var operations = new ExecutionOperations(shutdown.Token);
        ExecutionOperations.Operation first = operations.Register("one", CancellationToken.None);
        ExecutionOperations.Operation second = operations.Register("two", CancellationToken.None);
        shutdown.Cancel();
        Assert.IsTrue(first.Token.IsCancellationRequested);
        Assert.IsTrue(second.Token.IsCancellationRequested);
        Task cleanup = operations.DisposeAsync().AsTask();
        Assert.IsFalse(cleanup.IsCompleted);
        await first.DisposeAsync();
        Assert.IsFalse(cleanup.IsCompleted);
        await second.DisposeAsync();
        await cleanup;
        Assert.ThrowsExactly<OperationCanceledException>(() => operations.Register("late", CancellationToken.None));
    }

    [TestMethod]
    public async Task CancelIsConnectionLocal_AndIdempotent()
    {
        await using var a = new ExecutionOperations(CancellationToken.None);
        await using var b = new ExecutionOperations(CancellationToken.None);
        await using ExecutionOperations.Operation one = a.Register("same", CancellationToken.None);
        await using ExecutionOperations.Operation two = b.Register("same", CancellationToken.None);
        a.Cancel("same");
        a.Cancel("same");
        Assert.IsTrue(one.Token.IsCancellationRequested);
        Assert.IsFalse(two.Token.IsCancellationRequested);
    }

    [TestMethod]
    public async Task ExcessEarlyCancellations_FailClosedInsteadOfForgettingCancellation()
    {
        await using var operations = new ExecutionOperations(CancellationToken.None);
        for (int i = 0; i < 129; i++) { operations.Cancel("early-" + i); }
        Assert.ThrowsExactly<OperationCanceledException>(() => operations.Register("early-0", CancellationToken.None));
        Assert.ThrowsExactly<OperationCanceledException>(() => operations.Register("new", CancellationToken.None));
    }

    [TestMethod]
    public async Task OrderedSink_DrainsBeforeCompletion_AndSupportsInterleavedOperations()
    {
        List<(string Op, long Sequence, int Value)> received = [];
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var a = new ExecutionNotificationSink<int>(async (value, seq) =>
        {
            entered.TrySetResult();
            await release.Task;
            lock (received) { received.Add(("a", seq, value)); }
        });
        var b = new ExecutionNotificationSink<int>((value, seq) =>
        {
            lock (received) { received.Add(("b", seq, value)); }
            return Task.CompletedTask;
        });
        a.Report(1);
        a.Report(2);
        b.Report(3);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Task drain = a.DrainAsync();
        Assert.IsFalse(drain.IsCompleted);
        await b.DrainAsync();
        release.TrySetResult();
        await drain;
        CollectionAssert.AreEqual(new[] { 1, 2 }, received.Where(v => v.Op == "a").Select(v => v.Value).ToArray());
        CollectionAssert.AreEqual(new long[] { 1, 2 }, received.Where(v => v.Op == "a").Select(v => v.Sequence).ToArray());
        Assert.ThrowsExactly<InvalidOperationException>(() => a.Report(4));
    }

    [TestMethod]
    public async Task SendFailure_CancelsSilentExecutionImmediately()
    {
        await using var operations = new ExecutionOperations(CancellationToken.None);
        await using ExecutionOperations.Operation operation = operations.Register("silent", CancellationToken.None);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using CancellationTokenRegistration registration = operation.Token.Register(() => cancelled.TrySetResult());
        var sink = new ExecutionNotificationSink<int>((_, _) => Task.FromException(new IOException("broken")),
            () => operations.Cancel("silent"));
        sink.Report(1);
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.ThrowsExactlyAsync<IOException>(sink.DrainAsync);
    }

    [TestMethod]
    public async Task OrderedSink_SendFailureAndOverflowAreNotSuccessfulCompletion()
    {
        var broken = new ExecutionNotificationSink<int>((_, _) => Task.FromException(new IOException("send failed")));
        broken.Report(1);
        await Assert.ThrowsExactlyAsync<IOException>(broken.DrainAsync);

        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stalled = new ExecutionNotificationSink<int>(async (_, _) => { entered.TrySetResult(); await release.Task; });
        stalled.Report(0);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        for (int i = 0; i < 256; i++) { stalled.Report(i); }
        Assert.ThrowsExactly<InvalidOperationException>(() => stalled.Report(257));
        release.TrySetResult();
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(stalled.DrainAsync);
    }
}
