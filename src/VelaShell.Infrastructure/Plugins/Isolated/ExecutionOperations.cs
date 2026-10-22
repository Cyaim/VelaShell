namespace VelaShell.Infrastructure.Plugins.Isolated;

/// <summary>
/// 每个插件连接独占的执行登记。取消可早于请求线程池登记,且停机后不再接收操作。
/// 未登记取消有界;超过上限即 fail closed,不丢弃取消后继续执行。
/// </summary>
internal sealed class ExecutionOperations : IAsyncDisposable
{
    private const int MaxEarlyCancellations = 128;
    private const int RecentCompletionLimit = 512;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Operation> _operations = [with(StringComparer.Ordinal)];
    private readonly HashSet<string> _earlyCancelled = [with(StringComparer.Ordinal)];
    private readonly HashSet<string> _recentCompleted = [with(StringComparer.Ordinal)];
    private readonly Queue<string> _completionOrder = new();
    private readonly CancellationToken _shutdown;
    private bool _stopped;

    internal ExecutionOperations(CancellationToken shutdown) => _shutdown = shutdown;

    internal int ActiveCount
    {
        get { lock (_gate) { return _operations.Count; } }
    }

    internal Operation Register(string id, CancellationToken connectionToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        if (id.Length > 128)
        {
            throw new ArgumentException("Execution operation id is too long.", nameof(id));
        }
        lock (_gate)
        {
            if (_stopped || _shutdown.IsCancellationRequested)
            {
                throw new OperationCanceledException("Plugin execution lifetime has ended.");
            }
            if (_operations.ContainsKey(id) || _recentCompleted.Contains(id))
            {
                throw new InvalidOperationException("Execution operation id has already been used.");
            }
            var operation = new Operation(this, id,
                CancellationTokenSource.CreateLinkedTokenSource(connectionToken, _shutdown));
            _operations.Add(id, operation);
            if (_earlyCancelled.Remove(id))
            {
                operation.Cancel();
            }
            return operation;
        }
    }

    internal void Cancel(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 128)
        {
            return;
        }
        lock (_gate)
        {
            if (_operations.TryGetValue(id, out Operation? operation))
            {
                operation.Cancel();
            }
            else if (!_stopped && !_recentCompleted.Contains(id))
            {
                if (_earlyCancelled.Count >= MaxEarlyCancellations && !_earlyCancelled.Contains(id))
                {
                    // 不驱逐仍可能在请求线程池排队的取消:那样会让已停止的命令重新启动。
                    StopUnderLock();
                }
                else
                {
                    _earlyCancelled.Add(id);
                }
            }
        }
    }

    internal void Stop()
    {
        lock (_gate) { StopUnderLock(); }
    }

    private void StopUnderLock()
    {
        _stopped = true;
        _earlyCancelled.Clear();
        foreach (Operation operation in _operations.Values)
        {
            operation.Cancel();
        }
    }

    private void Complete(Operation operation)
    {
        lock (_gate)
        {
            _operations.Remove(operation.Id);
            _recentCompleted.Add(operation.Id);
            _completionOrder.Enqueue(operation.Id);
            if (_completionOrder.Count > RecentCompletionLimit)
            {
                _recentCompleted.Remove(_completionOrder.Dequeue());
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        Task[] pending;
        lock (_gate)
        {
            StopUnderLock();
            pending = _operations.Values.Select(operation => operation.Completed).ToArray();
        }
        await Task.WhenAll(pending).ConfigureAwait(false);
    }

    internal sealed class Operation(ExecutionOperations owner, string id, CancellationTokenSource source) : IAsyncDisposable
    {
        private readonly TaskCompletionSource _completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private Task? _cancellation;
        private int _disposed;
        internal string Id => id;
        internal CancellationToken Token => source.Token;
        internal Task Completed => _completed.Task;

        // 仅在 owner._gate 内调用。CancelAsync 立即置取消状态,回调不在登记锁内运行。
        internal void Cancel()
        {
            if (Volatile.Read(ref _disposed) == 0)
            {
                _cancellation ??= source.CancelAsync();
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                await Completed.ConfigureAwait(false);
                return;
            }
            Task? cancellation;
            lock (owner._gate)
            {
                // 仍保留在 active 集合中,DisposeAsync 必须等取消回调和资源释放完成。
                cancellation = _cancellation;
            }
            try
            {
                if (cancellation is not null)
                {
                    try { await cancellation.ConfigureAwait(false); }
                    catch (AggregateException) { /* 停机回调不能阻止释放。 */ }
                }
            }
            finally
            {
                source.Dispose();
                lock (owner._gate)
                {
                    _completed.TrySetResult();
                    owner.Complete(this);
                }
            }
        }
    }
}
