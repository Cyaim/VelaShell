using System.Runtime.CompilerServices;
using VelaShell.PluginSdk.RemoteExec;

namespace VelaShell.Plugin.Ai.Agent;

/// <summary>本地管理状态;取消通道不等于已证实远端进程停止。</summary>
public enum CommandJobState
{
    /// <summary>已排队,尚未开通道。</summary>
    Starting,
    /// <summary>等待可信审批或认证。</summary>
    AwaitingUser,
    /// <summary>前台执行通道已启动。</summary>
    Running,
    /// <summary>已确认退出码为零。</summary>
    Succeeded,
    /// <summary>已确认执行失败或不支持。</summary>
    Failed,
    /// <summary>排队任务在启动前已取消。</summary>
    Cancelled,
    /// <summary>执行死线已到;不保证远端停止。</summary>
    TimedOut,
    /// <summary>通道失败或取消后无法确认远端结果。</summary>
    Unknown
}

/// <summary>带单调游标与原始流标识的一段有界输出。</summary>
/// <param name="Cursor">此条输出的单调序号。</param>
/// <param name="Stream">stdout、stderr 或提权 PTY 的 combined。</param>
/// <param name="Text">不超过单条上限的原文。</param>
public sealed record CommandJobOutput(long Cursor, string Stream, string Text);

/// <summary>不可变、有界的任务快照。游标指向下一条输出,过期部分以 OutputTruncated 标记。</summary>
public sealed record CommandJobSnapshot(
    string TaskId, string SessionId, CommandJobState State, int? ExitCode,
    DateTimeOffset CreatedAt, DateTimeOffset? StartedAt, DateTimeOffset? CompletedAt,
    bool CancellationRequested, bool RemoteStopConfirmed, bool Monitoring,
    IReadOnlyList<CommandJobOutput> Output, long NextCursor, long EarliestCursor,
    bool OutputTruncated, bool HasMoreOutput, string? Error)
{
    /// <summary>本地托管执行已经收尾,不代表未知任务的远端副作用已完成。</summary>
    public bool IsTerminal => State is CommandJobState.Succeeded or CommandJobState.Failed
        or CommandJobState.Cancelled or CommandJobState.TimedOut or CommandJobState.Unknown;
}

/// <summary>
/// 应用存活期间的 exec 任务注册表。不解析 shell、不接管脱离 exec 通道的进程。
/// 同一插件上下文的工具箱共用四个槽,避免多对话分别占满宿主额度。
/// </summary>
public sealed class CommandJobRegistry(IRemoteExecApi remoteExec, object? schedulerKey = null,
    CancellationToken lifetimeToken = default) : IAsyncDisposable
{
    private const int MaxJobsPerRun = 128;
    private const int MaxOutputChars = 32 * 1024;
    private const int MaxEntryChars = 4096;
    private const int MaxOutputEntries = 256;
    private static readonly ConditionalWeakTable<object, SemaphoreSlim> Schedulers = new();
    private readonly SemaphoreSlim _streamSlots = Schedulers.GetValue(schedulerKey ?? remoteExec,
        static _ => new SemaphoreSlim(IRemoteExecApi.MaxConcurrentStreams));
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Job> _jobs = new(StringComparer.Ordinal);
    private readonly HashSet<CommandJobRunScope> _runs = [];
    private bool _disposed;

    /// <summary>创建所有者隔离的轮次,并绑定查询和实际启动前的会话授权检查。</summary>
    public CommandJobRunScope BeginRun(string ownerId, string runId, CancellationToken cancellationToken = default,
        Func<string, CancellationToken, Task<bool>>? authorize = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_runs.Any(r => r.OwnerId == ownerId && r.RunId == runId))
            {
                throw new InvalidOperationException("This owner/run is already registered.");
            }
            var scope = new CommandJobRunScope(this, ownerId, runId, cancellationToken, lifetimeToken, authorize);
            _runs.Add(scope);
            return scope;
        }
    }

    /// <summary>登记普通前台命令并进入共享流调度队列;返回不含目标信息的随机任务 id。</summary>
    public string Start(CommandJobRunScope scope, string sessionId, string command,
        TimeSpan? executionTimeout = null, bool monitoring = false)
        => StartCore(scope, sessionId, command, executionTimeout, monitoring,
            (output, _, token) => remoteExec.StreamAsync(sessionId, command,
                new ExecStreamOptions { Timeout = executionTimeout, IncludeStandardError = true }, output, token));

    /// <summary>仅通过专用宿主契约执行整条 root 命令,不回退普通执行。</summary>
    public string StartSudo(CommandJobRunScope scope, string sessionId, string command,
        TimeSpan? executionTimeout = null, bool monitoring = false)
        => StartCore(scope, sessionId, command, executionTimeout, monitoring, async (output, state, token) =>
        {
            SudoExecResult result = await remoteExec.StreamSudoAsync(sessionId, command,
                new SudoExecOptions { Timeout = executionTimeout }, new SudoOutput(output, state), token).ConfigureAwait(false);
            if (result.Outcome == SudoExecOutcome.AuthenticationCancelled)
            {
                scope.MarkExecutionDenied();
            }
            return result.Outcome switch
            {
                SudoExecOutcome.Exited when result.ExitCode is { } code => new ExecStreamResult(code, result.OutputChunks),
                SudoExecOutcome.AuthenticationCancelled => throw new JobOutcomeException(CommandJobState.Failed,
                    "The user denied root execution or cancelled authentication. The command did not start. Do not retry.", true),
                SudoExecOutcome.AuthenticationFailed => throw new JobOutcomeException(CommandJobState.Failed,
                    "Sudo authentication failed. The command did not start. Do not automatically retry.", true),
                SudoExecOutcome.NotSupported => throw new JobOutcomeException(CommandJobState.Failed,
                    "Trusted sudo execution is not supported. The command did not start; no ordinary-exec fallback was attempted.", true),
                _ => throw new JobOutcomeException(CommandJobState.Unknown,
                    "Sudo command outcome is unknown or its exit code is missing. Do not replay this operation.", false)
            };
        }, mergedOutput: true);

    private sealed class SudoOutput(IProgress<ExecOutput> output, Action<CommandJobState> state) : IProgress<SudoExecEvent>
    {
        public void Report(SudoExecEvent value)
        {
            if (value.Kind == SudoExecEventKind.Output && value.Text is { } text)
            {
                output.Report(new ExecOutput(ExecStream.StandardOutput, text));
            }
            else if (value.Kind == SudoExecEventKind.State)
            {
                state(value.State == SudoExecState.Running ? CommandJobState.Running : CommandJobState.AwaitingUser);
            }
        }
    }

    private sealed class JobOutcomeException(CommandJobState state, string message, bool confirmed) : Exception(message)
    {
        public CommandJobState State { get; } = state;
        public bool Confirmed { get; } = confirmed;
    }

    // 提权使用同一调度、所有权、输出和清理路径;认证秘密不进入注册表。
    internal string StartCore(CommandJobRunScope scope, string sessionId, string command,
        TimeSpan? executionTimeout, bool monitoring,
        Func<IProgress<ExecOutput>, Action<CommandJobState>, CancellationToken, Task<ExecStreamResult>> execute,
        bool mergedOutput = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        if (executionTimeout is { } timeout && timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(executionTimeout));
        }
        Job job;
        lock (_gate)
        {
            ValidateScope(scope);
            scope.Token.ThrowIfCancellationRequested();
            if (_jobs.Values.Count(j => ReferenceEquals(j.Scope, scope)) >= MaxJobsPerRun)
            {
                throw new InvalidOperationException("The command task budget for this run is exhausted.");
            }
            job = new Job(scope, sessionId, command, monitoring, mergedOutput);
            _jobs.Add(job.Id, job);
            // 先登记再执行;同步完成的替身和真实异步实现走同一路径,无 Task.Run 包 I/O。
            job.Execution = ExecuteAsync(job, execute);
        }
        return job.Id;
    }

    /// <summary>验证所有者与当前授权,读取游标之后保留的输出。</summary>
    public async Task<CommandJobSnapshot> ReadAsync(CommandJobRunScope scope, string taskId,
        long cursor = 0, CancellationToken cancellationToken = default)
    {
        Job job = Find(scope, taskId);
        await AuthorizeAsync(job, cancellationToken).ConfigureAwait(false);
        return job.Snapshot(cursor);
    }

    /// <summary>只限本地等待窗口,不会改变远端执行死线。</summary>
    public async Task<CommandJobSnapshot> WaitAsync(CommandJobRunScope scope, string taskId, TimeSpan wait,
        long cursor = 0, CancellationToken cancellationToken = default)
    {
        Job job = Find(scope, taskId);
        await AuthorizeAsync(job, cancellationToken).ConfigureAwait(false);
        try
        {
            await job.Completion.Task.WaitAsync(wait, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // 短等结束仍持有任务和独立 CTS;不是执行超时。
        }
        await AuthorizeAsync(job, cancellationToken).ConfigureAwait(false);
        return job.Snapshot(cursor);
    }

    /// <summary>工具已返回运行中回执;允许终态投递为新的本地完成通知。</summary>
    public void MarkBackground(CommandJobRunScope scope, string taskId)
    {
        Job job = Find(scope, taskId);
        lock (job.Gate)
        {
            job.Background = true;
        }
    }

    /// <summary>取消不重新验证会话范围,授权撤销也不能阻挡资源清理。</summary>
    public async Task CancelAsync(CommandJobRunScope scope, string taskId)
    {
        Job job = Find(scope, taskId);
        await job.Cancel.CancelAsync().ConfigureAwait(false);
        // 不等可能失联的远端,也不返回撤权后的输出、退出码或目标元数据。
    }

    private Job Find(CommandJobRunScope scope, string taskId)
    {
        lock (_gate)
        {
            ValidateScope(scope);
            if (!_jobs.TryGetValue(taskId, out Job? job) || !ReferenceEquals(job.Scope, scope))
            {
                // 不泄漏其它所有者是否拥有这个 id。
                throw new KeyNotFoundException("No command task with this id belongs to this run.");
            }
            return job;
        }
    }

    private void ValidateScope(CommandJobRunScope scope)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_runs.Contains(scope) || scope.IsDisposed)
        {
            throw new InvalidOperationException("The command run scope is no longer active.");
        }
    }

    private static async Task AuthorizeAsync(Job job, CancellationToken cancellationToken)
    {
        if (job.Scope.Authorize is { } authorize
            && !await authorize(job.SessionId, cancellationToken).ConfigureAwait(false))
        {
            await job.Cancel.CancelAsync().ConfigureAwait(false);
            throw new UnauthorizedAccessException("Authorization for this task's session was revoked; output is withheld.");
        }
    }

    private async Task ExecuteAsync(Job job,
        Func<IProgress<ExecOutput>, Action<CommandJobState>, CancellationToken, Task<ExecStreamResult>> execute)
    {
        bool slot = false;
        bool started = false;
        try
        {
            await _streamSlots.WaitAsync(job.Cancel.Token).ConfigureAwait(false);
            slot = true;
            job.Cancel.Token.ThrowIfCancellationRequested();
            await AuthorizeAsync(job, job.Cancel.Token).ConfigureAwait(false);
            job.Cancel.Token.ThrowIfCancellationRequested();
            if (job.Scope.HasDeniedExecution)
                throw new UnauthorizedAccessException("Execution was denied in this run; queued commands cannot start.");
            lock (job.Gate)
            {
                job.State = CommandJobState.Running;
                job.StartedAt = DateTimeOffset.UtcNow;
                job.CommandStarted = !job.MergedOutput;
            }
            started = true;
            ExecStreamResult result = await execute(new DirectOutput(job), state =>
            {
                lock (job.Gate)
                {
                    job.State = state;
                    if (state == CommandJobState.Running)
                    {
                        job.CommandStarted = true;
                    }
                }
            }, job.Cancel.Token).ConfigureAwait(false);
            lock (job.Gate)
            {
                job.ExitCode = result.ExitCode;
                job.State = result.ExitCode == 0 ? CommandJobState.Succeeded : CommandJobState.Failed;
                // 退出码证实受监控的前台进程已退出;不对其自行脱离的子进程作保证。
                job.RemoteStopConfirmed = true;
            }
        }
        catch (OperationCanceledException) when (job.Cancel.IsCancellationRequested)
        {
            lock (job.Gate)
            {
                job.State = started ? CommandJobState.Unknown : CommandJobState.Cancelled;
                job.RemoteStopConfirmed = !started;
                job.Error = started ? "Exec channel cancelled; remote process termination is not confirmed." : null;
            }
        }
        catch (JobOutcomeException ex)
        {
            lock (job.Gate)
            {
                job.State = ex.State;
                job.Error = ex.Message;
                job.RemoteStopConfirmed = ex.Confirmed;
            }
        }
        catch (TimeoutException)
        {
            lock (job.Gate)
            {
                job.State = CommandJobState.TimedOut;
                job.Error = "Execution deadline expired; remote process termination is not confirmed.";
            }
        }
        catch (Exception ex)
        {
            lock (job.Gate)
            {
                job.State = started && ex is not (NotSupportedException or UnauthorizedAccessException)
                    ? CommandJobState.Unknown : CommandJobState.Failed;
                job.Error = ex.Message;
            }
        }
        finally
        {
            if (slot)
            {
                _streamSlots.Release();
            }
            lock (job.Gate)
            {
                job.CompletedAt = DateTimeOffset.UtcNow;
            }
            job.Completion.TrySetResult();
        }
    }

    internal bool HasActiveJobs(CommandJobRunScope scope)
    {
        lock (_gate)
        {
            return _jobs.Values.Any(j => ReferenceEquals(j.Scope, scope) && j.NeedsNotification);
        }
    }

    internal bool HasUnknownOutcome(CommandJobRunScope scope)
    {
        lock (_gate)
        {
            return _jobs.Values.Any(j => ReferenceEquals(j.Scope, scope) && j.HasUnknownOutcome);
        }
    }

    internal bool HasMonitoringJobs(CommandJobRunScope scope)
    {
        lock (_gate)
        {
            return _jobs.Values.Any(j => ReferenceEquals(j.Scope, scope) && j.Monitoring && !j.Completion.Task.IsCompleted);
        }
    }

    internal async Task<IReadOnlyList<CommandJobSnapshot>> WaitForCompletionAsync(CommandJobRunScope scope,
        CancellationToken cancellationToken)
    {
        Job[] jobs;
        lock (_gate)
        {
            ValidateScope(scope);
            jobs = [.. _jobs.Values.Where(j => ReferenceEquals(j.Scope, scope) && j.NeedsNotification)];
        }
        if (jobs.Length == 0)
        {
            return [];
        }
        await Task.WhenAny(jobs.Select(j => j.Completion.Task)).WaitAsync(cancellationToken).ConfigureAwait(false);
        var snapshots = new List<CommandJobSnapshot>();
        foreach (Job job in jobs.Where(j => j.Completion.Task.IsCompleted))
        {
            try
            {
                await AuthorizeAsync(job, cancellationToken).ConfigureAwait(false);
            }
            catch (UnauthorizedAccessException)
            {
                // 自动续接和手动查询遵守同一范围;撤销后不能借完成通知读旧输出。
                lock (job.Gate)
                {
                    job.Notified = true;
                }
                continue;
            }
            lock (job.Gate)
            {
                if (!job.Notified)
                {
                    job.Notified = true;
                    snapshots.Add(job.Snapshot(0));
                }
            }
        }
        return snapshots;
    }

    internal Task WaitForMonitoringEndAsync(CommandJobRunScope scope, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            ValidateScope(scope);
            return Task.WhenAll(_jobs.Values.Where(j => ReferenceEquals(j.Scope, scope) && j.Monitoring)
                .Select(j => j.Completion.Task)).WaitAsync(cancellationToken);
        }
    }

    internal async Task CancelRunAsync(CommandJobRunScope scope)
    {
        await scope.CancelSource.CancelAsync().ConfigureAwait(false);
        Job[] jobs;
        lock (_gate)
        {
            jobs = [.. _jobs.Values.Where(j => ReferenceEquals(j.Scope, scope))];
        }
        await Task.WhenAll(jobs.Select(j => j.Execution)).ConfigureAwait(false);
    }

    internal async ValueTask DisposeRunAsync(CommandJobRunScope scope)
    {
        await CancelRunAsync(scope).ConfigureAwait(false);
        lock (_gate)
        {
            foreach (Job job in _jobs.Values.Where(j => ReferenceEquals(j.Scope, scope)).ToArray())
            {
                _jobs.Remove(job.Id);
                job.Cancel.Dispose();
            }
            _runs.Remove(scope);
        }
    }

    /// <summary>取消并等待托管 I/O 收尾后释放本地资源。</summary>
    public async ValueTask DisposeAsync()
    {
        CommandJobRunScope[] runs;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            runs = [.. _runs];
        }
        foreach (CommandJobRunScope run in runs)
        {
            await run.DisposeAsync().ConfigureAwait(false);
        }
    }

    private sealed class DirectOutput(Job job) : IProgress<ExecOutput>
    {
        public void Report(ExecOutput value) => job.Append(value);
    }

    private sealed class Job(CommandJobRunScope scope, string sessionId, string command, bool monitoring, bool mergedOutput)
    {
        public readonly object Gate = new();
        public readonly string Id = Guid.NewGuid().ToString("N");
        public readonly CommandJobRunScope Scope = scope;
        public readonly string SessionId = sessionId;
        public readonly string Command = command;
        public readonly bool Monitoring = monitoring;
        public readonly bool MergedOutput = mergedOutput;
        public bool CommandStarted;
        public readonly DateTimeOffset CreatedAt = DateTimeOffset.UtcNow;
        public readonly CancellationTokenSource Cancel = CancellationTokenSource.CreateLinkedTokenSource(scope.Token);
        public readonly TaskCompletionSource Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task Execution = Task.CompletedTask;
        public CommandJobState State = CommandJobState.Starting;
        public DateTimeOffset? StartedAt;
        public DateTimeOffset? CompletedAt;
        public int? ExitCode;
        public bool RemoteStopConfirmed;
        public bool Background;
        public bool Notified;
        public string? Error;
        private readonly Queue<CommandJobOutput> _output = new();
        private int _characters;
        private long _nextCursor;
        private bool _truncated;

        public bool HasUnknownOutcome
        {
            get
            {
                lock (Gate)
                {
                    return State == CommandJobState.Unknown
                        || (State == CommandJobState.TimedOut && CommandStarted && !RemoteStopConfirmed);
                }
            }
        }

        public bool NeedsNotification
        {
            get
            {
                lock (Gate)
                {
                    return Background && !Monitoring && !Notified;
                }
            }
        }

        public void Append(ExecOutput value)
        {
            lock (Gate)
            {
                // SDK 2.0.9 已按行回调;接收器仍限制单条长度和总字符,\r 进度也按原文保留。
                string text = value.Line ?? "";
                if (text.Length > MaxEntryChars)
                {
                    text = text[..MaxEntryChars];
                    _truncated = true;
                }
                int cost = Math.Max(1, text.Length);
                while ((_characters + cost > MaxOutputChars || _output.Count >= MaxOutputEntries)
                    && _output.TryDequeue(out CommandJobOutput? old))
                {
                    _characters -= Math.Max(1, old.Text.Length);
                    _truncated = true;
                }
                _output.Enqueue(new CommandJobOutput(_nextCursor++,
                    MergedOutput ? "combined" : value.Stream == ExecStream.StandardError ? "stderr" : "stdout", text));
                _characters += cost;
            }
        }

        public CommandJobSnapshot Snapshot(long cursor)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(cursor);
            lock (Gate)
            {
                long earliest = _output.TryPeek(out CommandJobOutput? first) ? first.Cursor : _nextCursor;
                CommandJobOutput[] output = [.. _output.Where(o => o.Cursor >= cursor)];
                return new CommandJobSnapshot(Id, SessionId, State, ExitCode, CreatedAt, StartedAt, CompletedAt,
                    Cancel.IsCancellationRequested, RemoteStopConfirmed, Monitoring, output,
                    _nextCursor, earliest, _truncated || cursor < earliest, false, Error);
            }
        }
    }
}

/// <summary>一轮执行的所有者能力令牌。调用者必须异步释放,Stop 也取消其所有排队和运行任务。</summary>
public sealed class CommandJobRunScope : IAsyncDisposable
{
    private readonly CommandJobRegistry _registry;
    private int _disposed;
    private int _executionDenied;
    internal readonly CancellationTokenSource CancelSource;
    internal readonly Func<string, CancellationToken, Task<bool>>? Authorize;
    internal bool IsDisposed => Volatile.Read(ref _disposed) != 0;
    internal CancellationToken Token => CancelSource.Token;
    /// <summary>稳定的入口或对话所有者,不交由模型指定。</summary>
    public string OwnerId { get; }
    /// <summary>入口生成的唯一轮次代际。</summary>
    public string RunId { get; }
    /// <summary>仍有尚未消费完成通知的有限后台任务。</summary>
    public bool HasActiveJobs => !IsDisposed && _registry.HasActiveJobs(this);
    /// <summary>本轮存在 Unknown 或已启动且未确认停止的 TimedOut,不得自动推进或重放。</summary>
    public bool HasUnknownOutcome => !IsDisposed && _registry.HasUnknownOutcome(this);
    /// <summary>本轮审批或认证被拒绝,不得自动推进任何后续工具。</summary>
    public bool HasDeniedExecution => Volatile.Read(ref _executionDenied) != 0;
    internal void MarkExecutionDenied() => Interlocked.Exchange(ref _executionDenied, 1);
    /// <summary>仍有不应自动请求模型的显式无限监控任务。</summary>
    public bool HasMonitoringJobs => !IsDisposed && _registry.HasMonitoringJobs(this);

    internal CommandJobRunScope(CommandJobRegistry registry, string ownerId, string runId,
        CancellationToken cancellationToken, CancellationToken lifetimeToken,
        Func<string, CancellationToken, Task<bool>>? authorize)
    {
        _registry = registry;
        OwnerId = ownerId;
        RunId = runId;
        Authorize = authorize;
        CancelSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetimeToken);
    }

    /// <summary>首个有限后台任务结束时唤醒,原子消费此刻所有已完成且未投递的快照。</summary>
    public Task<IReadOnlyList<CommandJobSnapshot>> WaitForCompletionAsync(CancellationToken cancellationToken = default)
        => _registry.WaitForCompletionAsync(this, cancellationToken);

    /// <summary>仅本地等待显式监控任务结束,不投递模型完成通知。</summary>
    public Task WaitForMonitoringEndAsync(CancellationToken cancellationToken = default)
        => _registry.WaitForMonitoringEndAsync(this, cancellationToken);

    /// <summary>停止本轮,等待排队及运行任务的本地收尾;不受会话范围撤销影响。</summary>
    public Task CancelAsync() => _registry.CancelRunAsync(this);

    /// <summary>取消并等待托管 I/O 收尾后释放本地资源。</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }
        await _registry.DisposeRunAsync(this).ConfigureAwait(false);
        CancelSource.Dispose();
    }
}
