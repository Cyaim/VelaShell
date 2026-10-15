using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using VelaShell.Plugin.Ai.Agent;
using VelaShell.Plugin.Ai.Chat;
using VelaShell.PluginSdk.RemoteExec;

namespace VelaShell.Plugin.Ai.Tests;

[TestClass]
public sealed class AgentExecutionTests
{
    [TestMethod]
    public async Task ToolCompleted_ThenStreamFails_RecoversOnlyTheModelStep()
    {
        int executed = 0, verified = 0;
        var wire = new Wire((n, _) => n switch
        {
            1 => [Call("one", "change"), End(ChatFinishReason.ToolCalls)],
            2 => [new(ChatRole.Assistant, "partial")],
            _ => [new(ChatRole.Assistant, "continued"), End(ChatFinishReason.Stop)]
        }, failOn: [2]);
        var execution = new AgentExecution("run");
        using IChatClient client = new RecoveringChatClient(new SteeringChatClient(wire, new(),
                beforeRequest: _ => { verified++; return Task.CompletedTask; }), execution, "\n[resumed]\n",
                delay: (_, _) => Task.CompletedTask)
            .AsBuilder().UseFunctionInvocation(configure: c => execution.Configure(c, 25)).Build();
        List<ChatResponseUpdate> updates = [];
        await foreach (var update in client.GetStreamingResponseAsync([new(ChatRole.User, "go")],
                           new() { Tools = [AIFunctionFactory.Create(() => { executed++; return "done"; }, "change")] }))
            updates.Add(update);
        Assert.AreEqual(1, executed);
        Assert.AreEqual(3, verified);
        Assert.Contains("[resumed]", updates.ToChatResponse().Text);
        Assert.IsTrue(wire.Requests[2].SelectMany(m => m.Contents).OfType<FunctionResultContent>().Any());
    }

    [TestMethod]
    public async Task TruncatedToolCall_IsNeverExecuted()
    {
        int executed = 0;
        var execution = new AgentExecution("run");
        using IChatClient client = new RecoveringChatClient(new Wire((_, _) => [Call("one", "change")]),
                execution, "boundary", delay: (_, _) => Task.CompletedTask)
            .AsBuilder().UseFunctionInvocation(configure: c => execution.Configure(c, 25)).Build();
        await Assert.ThrowsAsync<IOException>(async () =>
        {
            await foreach (var _ in client.GetStreamingResponseAsync([new(ChatRole.User, "go")],
                               new() { Tools = [AIFunctionFactory.Create(() => { executed++; return "done"; }, "change")] })) { }
        });
        Assert.AreEqual(0, executed);
    }

    [TestMethod]
    public async Task SameCallId_ReusesResult_ButDifferentArgumentsAreRejected()
    {
        int executed = 0;
        var function = AIFunctionFactory.Create((int value) => { executed++; return value; }, "change");
        var execution = new AgentExecution("run");
        FunctionInvocationContext Context(int value) => new()
        {
            Function = function,
            Arguments = new() { ["value"] = value },
            CallContent = new("one", "change", new Dictionary<string, object?> { ["value"] = value })
        };
        Assert.AreEqual("1", (await execution.InvokeAsync(Context(1), default))?.ToString());
        Assert.AreEqual("1", (await execution.InvokeAsync(Context(1), default))?.ToString());
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await execution.InvokeAsync(Context(2), default));
        Assert.AreEqual(1, executed);
    }

    [TestMethod]
    public async Task CancellationAndPermissionFailure_AreNotRetried()
    {
        foreach (Exception failure in new Exception[] { new OperationCanceledException(), new HttpRequestException("denied", null, System.Net.HttpStatusCode.Forbidden) })
        {
            var wire = new Wire((_, _) => [], failOn: [1], failure: failure);
            using var client = new RecoveringChatClient(wire, new("run"), "boundary", delay: (_, _) => Task.CompletedTask);
            await Assert.ThrowsAsync<Exception>(async () =>
            { await foreach (var _ in client.GetStreamingResponseAsync([new(ChatRole.User, "go")])) { } });
            Assert.HasCount(1, wire.Requests);
        }
    }

    [TestMethod]
    public async Task LengthFinish_WithValidArguments_DoesNotExecute()
    {
        int executed = 0;
        var execution = new AgentExecution("run");
        using IChatClient client = new RecoveringChatClient(new Wire((_, _) => [Call("one", "change"), End(ChatFinishReason.Length)]),
                execution, "boundary", delay: (_, _) => Task.CompletedTask)
            .AsBuilder().UseFunctionInvocation(configure: c => execution.Configure(c, 25)).Build();
        await Assert.ThrowsAsync<IOException>(async () =>
        {
            await foreach (var _ in client.GetStreamingResponseAsync([new(ChatRole.User, "go")],
                               new() { Tools = [AIFunctionFactory.Create(() => { executed++; return "done"; }, "change")] })) { }
        });
        Assert.AreEqual(0, executed);
    }

    [TestMethod]
    public async Task MultipleCalls_OneFails_CheckpointHasPairedResults_AndNoNextModelStep()
    {
        int executed = 0;
        var wire = new Wire((_, _) => [Call("one", "good"), Call("two", "bad"), End(ChatFinishReason.ToolCalls)]);
        var execution = new AgentExecution("run");
        using IChatClient client = new RecoveringChatClient(wire, execution, "boundary", delay: (_, _) => Task.CompletedTask)
            .AsBuilder().UseFunctionInvocation(configure: c => execution.Configure(c, 25)).Build();
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in client.GetStreamingResponseAsync([new(ChatRole.User, "go")], new()
            {
                Tools = [AIFunctionFactory.Create(() => { executed++; return "ok"; }, "good"),
                    AIFunctionFactory.Create((Func<string>)(() => throw new InvalidOperationException("denied")), "bad")]
            })) { }
        });
        Assert.AreEqual(1, executed);
        Assert.HasCount(1, wire.Requests);
        Assert.AreEqual(2, execution.CompletedMessages.SelectMany(m => m.Contents).OfType<FunctionCallContent>().Count());
        Assert.AreEqual(2, execution.CompletedMessages.SelectMany(m => m.Contents).OfType<FunctionResultContent>().Count());
    }

    [TestMethod]
    public async Task SameResponse_SecondCallPauses_ThirdCallCannotExecute()
    {
        int executed = 0;
        var wire = new Wire((_, _) => [Call("one", "change", new() { ["value"] = 1 }),
            Call("one", "change", new() { ["value"] = 2 }), Call("three", "change", new() { ["value"] = 3 }), End(ChatFinishReason.ToolCalls)]);
        var execution = new AgentExecution("run");
        using IChatClient client = new RecoveringChatClient(wire, execution, "boundary")
            .AsBuilder().UseFunctionInvocation(configure: c => execution.Configure(c, 25)).Build();
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in client.GetStreamingResponseAsync([new(ChatRole.User, "go")], new()
            { Tools = [AIFunctionFactory.Create((int value) => { executed++; return value; }, "change")] })) { }
        });
        Assert.AreEqual(1, executed);
        Assert.HasCount(1, wire.Requests);
    }

    [TestMethod]
    public async Task MonitoringCommandEnding_EndsRunWithoutAnotherModelRequest()
    {
        var finish = new TaskCompletionSource<ExecStreamResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var registry = new CommandJobRegistry(new ControlledExec { Pending = finish.Task });
        await using var scope = registry.BeginRun("owner", "run");
        registry.Start(scope, "s", "tail", monitoring: true);
        var execution = new AgentExecution("run");
        var wire = new Wire((_, _) => [new(ChatRole.Assistant, "monitoring"), End(ChatFinishReason.Stop)]);
        using var client = new RecoveringChatClient(wire, execution, "boundary");
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task Consume()
        {
            await foreach (var _ in new AgentRunCoordinator(scope, execution).RunAsync(client,
              [new(ChatRole.User, "go")], new(), () => waiting.TrySetResult(), default)) { }
        }
        Task run = Consume();
        await waiting.Task.WaitAsync(TimeSpan.FromSeconds(5));
        finish.SetResult(new(1, 0));
        await run.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.HasCount(1, wire.Requests);
    }

    [TestMethod]
    public async Task UnknownOutcome_EarlyOrBackground_NeverRequestsAnotherModelStep()
    {
        foreach (bool background in new[] { false, true })
        {
            var finish = new TaskCompletionSource<ExecStreamResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var registry = new CommandJobRegistry(new ControlledExec { Pending = finish.Task });
            await using var scope = registry.BeginRun("owner", "run");
            string? id = null;
            int executed = 0;
            var execution = new AgentExecution("run", () => scope.HasUnknownOutcome);
            var wire = new Wire((n, _) => n == 1 && !background
                ? [Call("one", "run"), End(ChatFinishReason.ToolCalls)]
                : [new(ChatRole.Assistant, "waiting"), End(ChatFinishReason.Stop)]);
            using IChatClient client = new RecoveringChatClient(wire, execution, "boundary")
                .AsBuilder().UseFunctionInvocation(configure: c => execution.Configure(c, 25)).Build();
            if (background) { id = registry.Start(scope, "s", "command"); registry.MarkBackground(scope, id); }
            async Task<string> Run()
            {
                executed++;
                id = registry.Start(scope, "s", "command");
                finish.TrySetException(new IOException("connection lost"));
                CommandJobSnapshot snapshot = await registry.WaitAsync(scope, id, TimeSpan.FromSeconds(5));
                return AgentToolbox.FormatJob(snapshot);
            }
            var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            async Task Consume()
            {
                await foreach (var _ in new AgentRunCoordinator(scope, execution).RunAsync(client, [new(ChatRole.User, "go")],
                    new() { Tools = [AIFunctionFactory.Create(Run, "run")] }, () => waiting.TrySetResult(), default)) { }
            }
            Task run = Consume();
            if (background)
            {
                await waiting.Task.WaitAsync(TimeSpan.FromSeconds(5));
                finish.TrySetException(new IOException("connection lost"));
            }
            await Assert.ThrowsAsync<AgentPausedException>(async () => await run);
            Assert.HasCount(1, wire.Requests);
            Assert.AreEqual(background ? 0 : 1, executed);
            Assert.IsTrue(scope.HasUnknownOutcome);
            string history = string.Join("\n", execution.InterruptedHistory("").Select(m => m.Text + string.Join(" ", m.Contents.OfType<FunctionResultContent>().Select(r => r.Result))));
            Assert.Contains(background ? "must not be automatically rerun" : "Unknown", history);
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task DeniedExecution_EarlyOrBackground_PausesBeforeFurtherToolsOrRequests(bool background)
    {
        await using var registry = new CommandJobRegistry(new ControlledExec());
        await using var scope = registry.BeginRun("owner", "run");
        var release = new TaskCompletionSource<ExecStreamResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        int laterCalls = 0;
        var execution = new AgentExecution("run", () => scope.HasUnknownOutcome, () => scope.HasDeniedExecution);
        var wire = new Wire((_, _) => background
            ? [new(ChatRole.Assistant, "waiting"), End(ChatFinishReason.Stop)]
            : [Call("deny", "deny"), Call("later", "later"), End(ChatFinishReason.ToolCalls)]);
        using IChatClient client = new RecoveringChatClient(wire, execution, "boundary")
            .AsBuilder().UseFunctionInvocation(configure: c => execution.Configure(c, 25)).Build();
        if (background)
        {
            string id = registry.StartCore(scope, "s", "command", null, false, async (_, _, ct) =>
            {
                ExecStreamResult result = await release.Task.WaitAsync(ct);
                scope.MarkExecutionDenied();
                return result;
            });
            registry.MarkBackground(scope, id);
        }
        string Deny() { scope.MarkExecutionDenied(); return "Execution denied"; }
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task Consume()
        {
            await foreach (var _ in new AgentRunCoordinator(scope, execution).RunAsync(client, [new(ChatRole.User, "go")],
                new() { Tools = [AIFunctionFactory.Create(Deny, "deny"), AIFunctionFactory.Create(() => ++laterCalls, "later")] },
                () => waiting.TrySetResult(), default)) { }
        }
        Task run = Consume();
        if (background)
        {
            await waiting.Task.WaitAsync(TimeSpan.FromSeconds(5));
            release.SetResult(new(1, 0));
        }
        AgentPausedException paused = await Assert.ThrowsAsync<AgentPausedException>(() => run.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.AreEqual("ExecutionDenied", paused.ReasonKey);
        Assert.AreEqual(0, laterCalls);
        Assert.HasCount(1, wire.Requests);
        Assert.IsTrue(scope.HasDeniedExecution);
    }

    [TestMethod]
    public void CanonicalArguments_SortsNestedObjects_NotArrays()
    {
        Assert.AreEqual(AgentExecution.CanonicalArguments(new Dictionary<string, object?> { ["b"] = 2, ["a"] = 1 }),
            AgentExecution.CanonicalArguments(new Dictionary<string, object?> { ["a"] = 1, ["b"] = 2 }));
    }

    [TestMethod]
    public async Task Coordinator_ContinuesAfterBatch25_WithoutRemovingTools()
    {
        int executed = 0;
        var wire = new Wire((n, _) => n <= 26
            ? [Call($"call{n}", "step", new() { ["value"] = n }), End(ChatFinishReason.ToolCalls)]
            : [new(ChatRole.Assistant, "done"), End(ChatFinishReason.Stop)]);
        var execution = new AgentExecution("run");
        await using var registry = new CommandJobRegistry(new ControlledExec());
        await using var scope = registry.BeginRun("owner", "run");
        using IChatClient client = new RecoveringChatClient(wire, execution, "boundary")
            .AsBuilder().UseFunctionInvocation(configure: c => execution.Configure(c, 25)).Build();
        var coordinator = new AgentRunCoordinator(scope, execution);
        List<ChatResponseUpdate> updates = [];
        await foreach (var update in coordinator.RunAsync(client, [new(ChatRole.User, "go")], new()
        { Tools = [AIFunctionFactory.Create((int value) => { executed++; return value; }, "step")] }, null, default))
            updates.Add(update);
        Assert.AreEqual(26, executed);
        Assert.AreEqual(27, wire.Requests.Count);
        Assert.Contains("done", updates.ToChatResponse().Text);
        Assert.AreEqual(26, wire.Requests[^1].SelectMany(m => m.Contents).OfType<FunctionResultContent>().Count());
    }

    [TestMethod]
    public async Task BackgroundCompletion_ResumesOnce_AndStopRejectsLateCompletion()
    {
        foreach (bool stop in new[] { false, true })
        {
            var finish = new TaskCompletionSource<ExecStreamResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var registry = new CommandJobRegistry(new ControlledExec { Pending = finish.Task });
            using var cancellation = new CancellationTokenSource();
            await using var scope = registry.BeginRun("owner", "run", cancellation.Token);
            string id = registry.Start(scope, "s", "long foreground");
            registry.MarkBackground(scope, id);
            var execution = new AgentExecution("run");
            var wire = new Wire((_, _) => [new(ChatRole.Assistant, "reply"), End(ChatFinishReason.Stop)]);
            using var client = new RecoveringChatClient(wire, execution, "boundary");
            var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            async Task Consume()
            {
                await foreach (var _ in new AgentRunCoordinator(scope, execution).RunAsync(client,
                                   [new(ChatRole.User, "go")], new(), () => waiting.TrySetResult(), cancellation.Token)) { }
            }
            Task run = Consume();
            await waiting.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.AreEqual(1, wire.Requests.Count, "等待本地任务时不能轮询模型");
            if (stop) await cancellation.CancelAsync();
            finish.TrySetResult(new(0, 0));
            if (stop) await Assert.ThrowsAsync<OperationCanceledException>(async () => await run);
            else await run.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.AreEqual(stop ? 1 : 2, wire.Requests.Count);
            if (!stop) Assert.Contains("Background task completion notification", wire.Requests[1][^1].Text);
        }
    }

    private sealed class ControlledExec : IRemoteExecApi
    {
        internal Task<ExecStreamResult> Pending = Task.FromResult(new ExecStreamResult(0, 0));
        public Task<ExecResult> RunAsync(string sessionId, string command, ExecOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public Task<ExecStreamResult> StreamAsync(string sessionId, string command, ExecStreamOptions? options, IProgress<ExecOutput> output,
            CancellationToken cancellationToken = default) => Pending.WaitAsync(cancellationToken);
    }

    private static ChatResponseUpdate Call(string id, string name, Dictionary<string, object?>? arguments = null) => new(ChatRole.Assistant, [new FunctionCallContent(id, name, arguments)]);
    private static ChatResponseUpdate End(ChatFinishReason reason) => new() { FinishReason = reason };

    private sealed class Wire(Func<int, List<ChatMessage>, ChatResponseUpdate[]> script,
        int[]? failOn = null, Exception? failure = null) : IChatClient
    {
        internal List<List<ChatMessage>> Requests { get; } = [];
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var request = messages.ToList(); Requests.Add(request);
            int step = Requests.Count;
            foreach (var update in script(step, request)) { await Task.CompletedTask; yield return update; }
            if (failOn?.Contains(step) == true) throw failure ?? new IOException("disconnected");
        }
    }
}
