using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.AI;
using VelaShell.Plugin.Ai.Agent;

namespace VelaShell.Plugin.Ai.Chat;

/// <summary>
/// 每个对话只推进一个活动run。等待本地完成信号,不让模型轮询。
/// 调用方持有对话串行闸门,取消令牌废弃迟到的完成唤醒。
/// </summary>
internal sealed class AgentRunCoordinator(CommandJobRunScope jobs, AgentExecution execution)
{
    internal async IAsyncEnumerable<ChatResponseUpdate> RunAsync(IChatClient client,
        IEnumerable<ChatMessage> messages, ChatOptions options, Action? waiting,
        [EnumeratorCancellation] CancellationToken cancellationToken, Action<int>? continuingBatch = null)
    {
        List<ChatMessage> context = [.. messages];
        for (int batch = 0; ; batch++)
        {
            if (batch >= 8) throw new AgentPausedException("ExecutionBudget");
            cancellationToken.ThrowIfCancellationRequested();
            List<ChatResponseUpdate> updates = [];
            bool atBatchBoundary = false;
            await using (IAsyncEnumerator<ChatResponseUpdate> iterator = client.GetStreamingResponseAsync(context, options, cancellationToken).GetAsyncEnumerator(cancellationToken))
            {
                while (true)
                {
                    ChatResponseUpdate update;
                    try
                    {
                        if (!await iterator.MoveNextAsync().ConfigureAwait(false)) break;
                        update = iterator.Current;
                    }
                    catch (AgentBatchBoundaryException) { atBatchBoundary = true; break; }
                    updates.Add(update);
                    yield return update;
                }
            }
            ChatResponse response = updates.ToChatResponse();
            context.AddRange(response.Messages);
            if (atBatchBoundary)
            {
                continuingBatch?.Invoke(batch + 2);
                execution.ContinueBatch();
                continue;
            }
            if (!jobs.HasActiveJobs)
            {
                if (jobs.HasMonitoringJobs)
                {
                    waiting?.Invoke();
                    await jobs.WaitForMonitoringEndAsync(cancellationToken).ConfigureAwait(false);
                }
                yield break;
            }
            waiting?.Invoke();
            // 本地等待不消耗模型预算,更不能因等待窗口结束杀死长任务。
            IReadOnlyList<CommandJobSnapshot> completed = await jobs.WaitForCompletionAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (completed.Count == 0) yield break;
            // running工具已取得唯一结果。终态通知是新输入,
            // 绝不能作为原CallId的第二个FunctionResultContent。
            var notification = new ChatMessage(ChatRole.User,
                "Background task completion notification (not a new user command). Continue the existing request using these final results. "
                + "Unknown/cancelled outcomes must not be automatically rerun. " + JsonSerializer.Serialize(completed));
            context.Add(notification);
            execution.RecordNotification(notification);
            if (jobs.HasDeniedExecution)
                throw new AgentPausedException("ExecutionDenied");
            if (completed.Any(job => job.State == CommandJobState.Unknown))
                throw new AgentPausedException("TaskUnknown");
        }
    }
}
