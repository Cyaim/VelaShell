using VelaShell.Core.Ssh;
using VelaShell.PluginSdk;
using VelaShell.PluginSdk.RemoteExec;

namespace VelaShell.Infrastructure.Plugins.Capabilities;

/// <summary>
/// <see cref="IRemoteExecApi" /> 的桥接实现:复用宿主既有连接,经独立 exec 通道执行
/// (<see cref="ISshClientWrapper.RunCommandDetailedAsync" />),不进用户终端、不碰输入串行化队列。
/// <para>
/// 实例是**按插件**创建的(见 <c>PluginContext</c>),因此
/// <see cref="IRemoteExecApi.MaxConcurrentStreams" /> 这个并发上限天然就是按插件计的。
/// </para>
/// </summary>
internal sealed class RemoteExecCapability(ISshConnectionService connections, string sourcePluginId = "", ISudoPrompt? sudoPrompt = null,
    CancellationToken shutdown = default) : IRemoteExecApi
{
    private static readonly TimeSpan MaxTimeout = TimeSpan.FromMinutes(10);

    /// <summary>当前在飞的流式执行条数(每插件)。</summary>
    private int _activeStreams;

    internal int ActiveStreams => Volatile.Read(ref _activeStreams);

    public async Task<ExecResult> RunAsync(string sessionId, string command, ExecOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        ISshClientWrapper client = Resolve(sessionId);
        TimeSpan timeout = options?.Timeout is { } t && t > TimeSpan.Zero && t <= MaxTimeout ? t : TimeSpan.FromSeconds(30);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, shutdown);
        cts.CancelAfter(timeout);
        try
        {
            RemoteCommandResult result = await client.RunCommandDetailedAsync(command, cts.Token).ConfigureAwait(false);
            return new(result.StandardOutput)
            {
                Error = result.StandardError,
                ExitCode = result.ExitCode
            };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && !shutdown.IsCancellationRequested)
        {
            throw new TimeoutException($"Remote command timed out after {timeout.TotalSeconds:0}s: {command}");
        }
    }

    public async Task<ExecStreamResult> StreamAsync(string sessionId, string command, ExecStreamOptions? options,
        IProgress<ExecOutput> output, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        ArgumentNullException.ThrowIfNull(output);
        ISshClientWrapper client = Resolve(sessionId);
        // 先占坑再干活:流是不限时的,每条占一个 SSH 通道。忘了取消的插件不该有能力
        // 把对端的 MaxSessions 耗光 —— 那时坏掉的是用户的连接,不只是这个插件。
        if (Interlocked.Increment(ref _activeStreams) > IRemoteExecApi.MaxConcurrentStreams)
        {
            Interlocked.Decrement(ref _activeStreams);
            throw new InvalidOperationException(
                $"This plugin already has {IRemoteExecApi.MaxConcurrentStreams} streaming commands in flight; cancel one before starting another.");
        }
        bool includeStandardError = options?.IncludeStandardError ?? true;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, shutdown);
        if (options?.Timeout is { } timeout && timeout > TimeSpan.Zero)
        {
            cts.CancelAfter(timeout);
        }
        try
        {
            RemoteCommandStreamResult result = await client.StreamCommandAsync(
                command,
                includeStandardError,
                (isError, line) => Deliver(output, new(isError ? ExecStream.StandardError : ExecStream.StandardOutput, line), cts.Token),
                cts.Token).ConfigureAwait(false);
            return new(result.ExitCode, result.Lines);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && !shutdown.IsCancellationRequested)
        {
            throw new TimeoutException($"Streaming remote command timed out: {command}");
        }
        finally
        {
            Interlocked.Decrement(ref _activeStreams);
        }
    }

    public async Task<SudoExecResult> StreamSudoAsync(string sessionId, string command, SudoExecOptions? options,
        IProgress<SudoExecEvent> progress, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        ArgumentNullException.ThrowIfNull(progress);
        ISshClientWrapper client = Resolve(sessionId);
        if (sudoPrompt is null || string.IsNullOrWhiteSpace(sourcePluginId))
        {
            return new(SudoExecOutcome.NotSupported);
        }
        if (Interlocked.Increment(ref _activeStreams) > IRemoteExecApi.MaxConcurrentStreams)
        {
            Interlocked.Decrement(ref _activeStreams);
            throw new InvalidOperationException("This plugin has reached its streaming command limit.");
        }
        using CancellationTokenSource lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, shutdown);
        if (options?.Timeout is { } timeout && timeout > TimeSpan.Zero)
        {
            lifetime.CancelAfter(timeout);
        }
        try
        {
            SudoExecutionResult result = await client.StreamSudoCommandAsync(command,
                new(sourcePluginId, string.Empty), sudoPrompt, entry =>
                {
                    if (entry.State is { } state)
                    {
                        return Deliver(progress, new(SudoExecEventKind.State, state switch
                        {
                            SudoExecutionState.AwaitingApproval => SudoExecState.AwaitingApproval,
                            SudoExecutionState.AwaitingAuthentication => SudoExecState.AwaitingAuthentication,
                            SudoExecutionState.Running => SudoExecState.Running,
                            _ => throw new InvalidOperationException("Unknown sudo state.")
                        }), lifetime.Token);
                    }
                    return entry.Text is { } text
                        ? Deliver(progress, new(SudoExecEventKind.Output, Text: text), lifetime.Token)
                        : ValueTask.CompletedTask;
                }, lifetime.Token).ConfigureAwait(false);
            SudoExecOutcome outcome = result.Outcome switch
            {
                SudoExecutionOutcome.Exited when result.ExitCode.HasValue => SudoExecOutcome.Exited,
                SudoExecutionOutcome.AuthenticationCancelled => SudoExecOutcome.AuthenticationCancelled,
                SudoExecutionOutcome.AuthenticationFailed => SudoExecOutcome.AuthenticationFailed,
                SudoExecutionOutcome.NotSupported => SudoExecOutcome.NotSupported,
                _ => SudoExecOutcome.Unknown
            };
            return new(outcome, result.ExitCode, result.OutputChunks);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && !shutdown.IsCancellationRequested)
        {
            throw new TimeoutException("The sudo command exceeded its execution deadline.");
        }
        finally
        {
            Interlocked.Decrement(ref _activeStreams);
        }
    }

    /// <summary>
    /// 能背压的接收方(隔离插件的 RPC 通知)逐条等它入队;进程内插件的接收方同步转发,行序照旧由调用线程保证。
    /// </summary>
    private static ValueTask Deliver<T>(IProgress<T> progress, T value, CancellationToken cancellationToken)
    {
        if (progress is IFlowControlledProgress<T> flowControlled)
        {
            return flowControlled.ReportAsync(value, cancellationToken);
        }
        progress.Report(value);
        return ValueTask.CompletedTask;
    }

    private ISshClientWrapper Resolve(string sessionId) =>
        Guid.TryParse(sessionId, out Guid id) && connections.GetClient(id) is { IsConnected: true } client
            ? client
            : throw new PluginSessionNotFoundException(sessionId);
}
