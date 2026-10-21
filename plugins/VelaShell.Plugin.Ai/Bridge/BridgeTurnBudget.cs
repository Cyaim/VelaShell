namespace VelaShell.Plugin.Ai.Bridge;

/// <summary>有上限的模型请求预算；仅上传本地等待不计入请求耗时。</summary>
internal sealed class BridgeTurnBudget : IDisposable
{
    private readonly Lock _gate = new();
    private readonly CancellationTokenSource _source;
    private readonly Func<long> _clock;
    private long _remaining, _resumed;
    private bool _paused, _disposed;
    internal CancellationToken Token => _source.Token;

    internal BridgeTurnBudget(TimeSpan budget, CancellationToken stop, Func<long>? clock = null)
    {
        _clock = clock ?? (() => Environment.TickCount64);
        _remaining = Math.Max(1, (long)budget.TotalMilliseconds);
        _resumed = _clock();
        _source = CancellationTokenSource.CreateLinkedTokenSource(stop);
        _source.CancelAfter(TimeSpan.FromMilliseconds(_remaining));
    }

    internal void LocalWaiting(bool waiting)
    {
        lock (_gate)
        {
            if (_disposed || _paused == waiting || _source.IsCancellationRequested) return;
            if (waiting)
            {
                _remaining = Math.Max(0, _remaining - (_clock() - _resumed));
                if (_remaining == 0) { _source.Cancel(); return; }
                _source.CancelAfter(Timeout.InfiniteTimeSpan);
            }
            else
            {
                _resumed = _clock();
                _source.CancelAfter(TimeSpan.FromMilliseconds(Math.Max(1, _remaining)));
            }
            _paused = waiting;
        }
    }

    internal long RemainingMilliseconds
    {
        get { lock (_gate) return Math.Max(0, _remaining - (_paused ? 0 : _clock() - _resumed)); }
    }

    public void Dispose() { lock (_gate) { _disposed = true; _source.Dispose(); } }
}
