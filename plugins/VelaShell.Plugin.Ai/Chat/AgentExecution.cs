using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using VelaShell.Plugin.Ai.Ui;

namespace VelaShell.Plugin.Ai.Chat;

/// <summary>运行级工具账本:恢复可以复用调用,不能重新解释同一调用身份。</summary>
internal sealed class AgentExecution(string runId, Func<bool>? hasUnknownOutcome = null,
    Func<bool>? hasDeniedExecution = null)
{
    private sealed record Invocation(string Signature, Task<object?> Result);
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Invocation> _calls = new(StringComparer.Ordinal);
    private readonly List<ChatMessage> _checkpoint = [];
    private readonly List<ChatMessage> _completed = [];
    internal IReadOnlyList<ChatMessage> CompletedMessages { get { lock (_gate) return [.. _completed]; } }
    internal IReadOnlyList<ChatMessage> Notifications { get { lock (_gate) return _completed.Where(m => m.Role == ChatRole.User).ToList(); } }
    private int _steps;
    private int _batchSteps;
    private int _batchSize = 25;
    private Exception? _paused;
    private long _tokens;
    private string? _lastCall;
    private int _repeated;
    internal string RunId { get; } = runId;
    internal bool HasToolCalls { get { lock (_gate) return _calls.Count > 0; } }
    internal IReadOnlyList<ChatMessage> Checkpoint { get { lock (_gate) return [.. _checkpoint]; } }

    internal void Configure(FunctionInvokingChatClient client, int batchSize)
    {
        // 批次边界由外层协调器续接,不能让MEAI静默结束。
        _batchSize = batchSize;
        // 最后一个模型调用由内层预算守卫拦下,禁止MEAI隐式去掉tools作总结。
        client.MaximumIterationsPerRequest = 1000;
        client.MaximumConsecutiveErrorsPerRequest = 0;
        client.AllowConcurrentInvocation = false;
        client.FunctionInvoker = InvokeAsync;
    }

    internal ValueTask<object?> InvokeAsync(FunctionInvocationContext context, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        string callId = context.CallContent.CallId;
        string signature = context.Function.Name + ":" + CanonicalArguments(context.CallContent.Arguments);
        TaskCompletionSource<object?>? completion = null;
        Task<object?> result;
        lock (_gate)
        {
            if (hasDeniedExecution?.Invoke() == true) throw new AgentPausedException("ExecutionDenied");
            if (hasUnknownOutcome?.Invoke() == true) throw new AgentPausedException("TaskUnknown");
            if (_paused is { } paused) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(paused).Throw();
            if (_calls.TryGetValue(callId, out Invocation? previous))
            {
                if (previous.Signature != signature)
                {
                    _paused = new InvalidOperationException("A tool call id was reused with different arguments; execution paused.");
                    throw _paused;
                }
                return new ValueTask<object?>(previous.Result.WaitAsync(token));
            }
            if (_lastCall == signature) _repeated++; else { _lastCall = signature; _repeated = 0; }
            if (_repeated >= 3)
            {
                _paused = new AgentPausedException("NoProgress");
                throw _paused;
            }
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            result = completion.Task;
            _calls.Add(callId, new(signature, result));
        }
        // 失败/取消也占据账本身份:副作用未知时不能重放。
        _ = CompleteAsync(context, completion, token);
        return new ValueTask<object?>(result);
    }

    private async Task CompleteAsync(FunctionInvocationContext context, TaskCompletionSource<object?> completion, CancellationToken token)
    {
        try
        {
            object? value = await context.Function.InvokeAsync(context.Arguments, token).ConfigureAwait(false);
            lock (_gate)
            {
                var result = new ChatMessage(ChatRole.Tool,
                    [new FunctionResultContent(context.CallContent.CallId, value)]);
                _checkpoint.Add(result);
                _completed.Add(result);
            }
            completion.TrySetResult(value);
        }
        catch (OperationCanceledException ex)
        {
            lock (_gate)
            {
                _paused = ex;
                var result = new ChatMessage(ChatRole.Tool, [new FunctionResultContent(context.CallContent.CallId,
                    "Execution was cancelled. The remote outcome is unknown; do not automatically repeat this operation.")]);
                _checkpoint.Add(result);
                _completed.Add(result);
            }
            completion.TrySetCanceled(ex.CancellationToken);
        }
        catch (Exception ex)
        {
            lock (_gate)
            {
                _paused = ex;
                var result = new ChatMessage(ChatRole.Tool,
                    [new FunctionResultContent(context.CallContent.CallId, "Tool failed; execution paused. Its side effects must not be retried automatically.") { Exception = ex }]);
                _checkpoint.Add(result);
                _completed.Add(result);
            }
            completion.TrySetException(ex);
        }
    }

    internal void BeginStep(IEnumerable<ChatMessage> messages)
    {
        lock (_gate)
        {
            if (hasDeniedExecution?.Invoke() == true) throw new AgentPausedException("ExecutionDenied");
            if (hasUnknownOutcome?.Invoke() == true) throw new AgentPausedException("TaskUnknown");
            if (_paused is { } paused) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(paused).Throw();
            if (_batchSteps >= _batchSize) throw new AgentBatchBoundaryException();
            if (++_steps > 100 || _tokens > 200_000) throw new AgentPausedException("ExecutionBudget");
            _batchSteps++;
            _checkpoint.Clear();
            _checkpoint.AddRange(messages);
        }
    }

    internal void RecordInterruptedText(string text)
    {
        lock (_gate) _completed.Add(new ChatMessage(ChatRole.Assistant, text));
    }

    internal void RecordFailedUsage(UsageDetails? usage)
    {
        lock (_gate) _tokens += usage?.TotalTokenCount ?? 0;
    }

    internal void RecordNotification(ChatMessage notification)
    {
        lock (_gate) { _completed.Add(notification); _checkpoint.Add(notification); }
    }

    internal IReadOnlyList<ChatMessage> InterruptedHistory(string visibleText)
    {
        lock (_gate)
        {
            // 正文采用用户看到的一份,已完成步骤只保留工具配对与后台终态,避免重复正文。
            List<ChatMessage> result = [];
            var seen = new HashSet<string>(StringComparer.Ordinal);
            Dictionary<string, FunctionResultContent> results = _completed.SelectMany(m => m.Contents)
                .OfType<FunctionResultContent>().GroupBy(c => c.CallId, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.Last(), StringComparer.Ordinal);
            foreach (ChatMessage message in _completed)
            {
                FunctionCallContent[] calls = message.Contents.OfType<FunctionCallContent>()
                    .Where(c => seen.Add(c.CallId)).ToArray();
                if (calls.Length == 0) continue;
                result.Add(new ChatMessage(ChatRole.Assistant, calls.Cast<AIContent>().ToList()));
                result.Add(new ChatMessage(ChatRole.Tool, calls.Select(c => (AIContent)new FunctionResultContent(c.CallId,
                    results.TryGetValue(c.CallId, out FunctionResultContent? completed)
                        ? completed.Result : "This call was not started: execution paused. Do not infer that it ran." )).ToList()));
            }
            result.AddRange(_completed.Where(m => m.Role == ChatRole.User));
            if (!string.IsNullOrWhiteSpace(visibleText)) result.Add(new ChatMessage(ChatRole.Assistant, visibleText));
            return result;
        }
    }

    internal void ContinueBatch() { lock (_gate) _batchSteps = 0; }

    internal void CompleteStep(ChatResponse response)
    {
        lock (_gate)
        {
            _checkpoint.AddRange(response.Messages);
            _completed.AddRange(response.Messages);
            _tokens += response.Usage?.TotalTokenCount ?? 0;
        }
    }

    internal static string CanonicalArguments(IDictionary<string, object?>? arguments)
    {
        JsonElement element = JsonSerializer.SerializeToElement(arguments ?? new Dictionary<string, object?>());
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream)) WriteCanonical(writer, element);
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            writer.WriteStartObject();
            foreach (JsonProperty property in value.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
            { writer.WritePropertyName(property.Name); WriteCanonical(writer, property.Value); }
            writer.WriteEndObject();
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            writer.WriteStartArray();
            foreach (JsonElement item in value.EnumerateArray()) WriteCanonical(writer, item);
            writer.WriteEndArray();
        }
        else value.WriteTo(writer);
    }
}

internal sealed class AgentBatchBoundaryException : Exception;

internal sealed class AgentPausedException(string key) : InvalidOperationException(key)
{
    internal string ReasonKey { get; } = key;
}

/// <summary>
/// 位于函数循环之内、Steering之外:每次恢复都重新复验凭据。
/// 完整模型步骤确认前扣住工具帧,截断调用绝不执行。
/// </summary>
internal sealed class RecoveringChatClient(IChatClient inner, AgentExecution execution,
    string continuationBoundary, Action<int>? retrying = null,
    Func<TimeSpan, CancellationToken, Task>? delay = null) : DelegatingChatClient(inner)
{
    public override async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        List<ChatResponseUpdate> updates = [];
        await foreach (ChatResponseUpdate update in GetStreamingResponseAsync(messages, options, cancellationToken).ConfigureAwait(false))
            updates.Add(update);
        return updates.ToChatResponse();
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        List<ChatMessage> outgoing = [.. messages];
        execution.BeginStep(outgoing);
        for (int attempt = 0; ; attempt++)
        {
            List<ChatResponseUpdate> buffered = [];
            var partial = new StringBuilder();
            Exception? failure = null;
            await using (IAsyncEnumerator<ChatResponseUpdate> iterator = InnerClient
                .GetStreamingResponseAsync(outgoing, options, cancellationToken).GetAsyncEnumerator(cancellationToken))
            {
                while (true)
                {
                    ChatResponseUpdate update;
                    try
                    {
                        if (!await iterator.MoveNextAsync().ConfigureAwait(false)) break;
                        update = iterator.Current;
                    }
                    catch (Exception ex) { failure = ex; break; }
                    buffered.Add(update);
                    // 正文/思考可供流式展示,但未完成工具参数不离开本步骤缓冲。
                    if (update.Contents.Any(c => c is TextContent or TextReasoningContent)
                        || ReasoningPeek.IsBlank(update))
                    {
                        List<AIContent> prose = update.Contents.Where(c => c is TextContent or TextReasoningContent).ToList();
                        foreach (TextContent text in prose.OfType<TextContent>()) partial.Append(text.Text);
                        ChatResponseUpdate visible = update.Clone();
                        visible.Contents = prose;
                        visible.FinishReason = null;
                        if (ReasoningPeek.IsBlank(update) && ReasoningPeek.TryRead(update.RawRepresentation, out string reasoning))
                            visible.Contents = [new TextReasoningContent(reasoning)];
                        visible.RawRepresentation = null;
                        yield return visible;
                    }
                }
            }
            if (failure is null)
            {
                ChatResponse response = buffered.ToChatResponse();
                if (response.Messages.SelectMany(m => m.Contents).OfType<FunctionCallContent>().Any()
                    && (response.FinishReason != ChatFinishReason.ToolCalls
                        || response.Messages.SelectMany(m => m.Contents).OfType<FunctionCallContent>().Any(c => c.Exception is not null)))
                    failure = new IOException("Incomplete model tool-call step; no tool was executed.");
                else
                {
                    execution.CompleteStep(response);
                    foreach (ChatResponseUpdate update in buffered)
                    {
                        // 保存的是独立消息列表;输出时不改供应商原始帧,避免污染 checkpoint。
                        if (update.Contents.All(c => c is not TextContent and not TextReasoningContent))
                        {
                            if (ReasoningPeek.IsBlank(update))
                            {
                                ChatResponseUpdate metadata = update.Clone();
                                metadata.RawRepresentation = null;
                                yield return metadata;
                            }
                            else yield return update;
                        }
                        else
                        {
                            ChatResponseUpdate remainder = update.Clone();
                            remainder.Contents = update.Contents.Where(c => c is not TextContent and not TextReasoningContent).ToList();
                            remainder.RawRepresentation = null;
                            yield return remainder;
                        }
                    }
                    yield break;
                }
            }
            // 失败尝试的usage仍已计费;不把未完成调用/推理当完整历史送出。
            execution.RecordFailedUsage(buffered.ToChatResponse().Usage);
            foreach (ChatResponseUpdate update in buffered)
            {
                if (!update.Contents.Any(c => c is UsageContent)) continue;
                ChatResponseUpdate usage = update.Clone();
                usage.Contents = update.Contents.Where(c => c is UsageContent).ToList();
                usage.FinishReason = null;
                usage.RawRepresentation = null;
                yield return usage;
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (attempt >= 2 || !TransientFailure.IsTransient(failure))
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure!).Throw();
            retrying?.Invoke(attempt + 1);
            if (partial.Length > 0)
            {
                // 不重放未完成调用或供应商推理;正文续接边界明确显示。
                string snippet = partial.ToString();
                if (snippet.Length > 8000) snippet = snippet[^8000..];
                execution.RecordInterruptedText(snippet + continuationBoundary);
                outgoing.Add(new ChatMessage(ChatRole.Assistant, snippet));
                outgoing.Add(new ChatMessage(ChatRole.User,
                    "The preceding response was interrupted. Continue from its last confirmed text. Do not repeat completed tool calls. Incomplete tool calls were not executed."));
                yield return new ChatResponseUpdate(ChatRole.Assistant, continuationBoundary);
            }
            TimeSpan backoff = RetryAfter(failure!) ?? TimeSpan.FromMilliseconds(400 * (attempt + 1));
            await (delay ?? Task.Delay)(backoff, cancellationToken).ConfigureAwait(false);
        }
    }

    private static TimeSpan? RetryAfter(Exception exception)
    {
        // SDK可在Data附加Retry-After;超出恢复等待预算则暂停,不能提早重试。
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            string? text = current.Data["Retry-After"] as string;
            if (current is System.ClientModel.ClientResultException result
                && result.GetRawResponse() is { } response
                && response.Headers.TryGetValue("Retry-After", out string? header)) text = header;
            if (text is null) continue;
            TimeSpan? wait = double.TryParse(text, System.Globalization.CultureInfo.InvariantCulture, out double seconds) && seconds >= 0
                ? TimeSpan.FromSeconds(seconds)
                : DateTimeOffset.TryParse(text, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeUniversal, out DateTimeOffset date)
                    ? date - DateTimeOffset.UtcNow : null;
            if (wait is null) continue;
            if (wait > TimeSpan.FromSeconds(120)) throw new AgentPausedException("ExecutionBudget");
            return wait < TimeSpan.Zero ? TimeSpan.Zero : wait;
        }
        return null;
    }
}
