using System.Threading.Channels;

namespace VelaShell.Infrastructure.Plugins.Isolated;

/// <summary>同步收集、单异步写泵发送;终态必须 await DrainAsync。队列溢出明确失败而不静默丢输出。</summary>
internal sealed class ExecutionNotificationSink<T> : IProgress<T>
{
    private readonly object _gate = new();
    private readonly Channel<T> _queue = Channel.CreateBounded<T>(new BoundedChannelOptions(256)
    {
        SingleReader = true,
        SingleWriter = false,
        AllowSynchronousContinuations = false,
        FullMode = BoundedChannelFullMode.Wait
    });
    private readonly Func<T, long, Task> _send;
    private readonly Action? _onFailure;
    private readonly Task _pump;
    private bool _finished;
    private Exception? _failure;

    internal ExecutionNotificationSink(Func<T, long, Task> send, Action? onFailure = null)
    {
        _send = send;
        _onFailure = onFailure;
        _pump = PumpAsync();
    }

    public void Report(T value)
    {
        lock (_gate)
        {
            if (_finished)
            {
                throw new InvalidOperationException("Execution output arrived after completion.");
            }
            if (_failure is { } failure)
            {
                throw new InvalidOperationException("Execution notification delivery failed.", failure);
            }
            if (!_queue.Writer.TryWrite(value))
            {
                _failure = new InvalidOperationException("Execution output notification queue overflowed; execution stopped.");
                _onFailure?.Invoke();
                throw _failure;
            }
        }
    }

    private async Task PumpAsync()
    {
        long sequence = 0;
        try
        {
            await foreach (T value in _queue.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                await _send(value, ++sequence).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            lock (_gate) { _failure ??= ex; }
            // 不等下一条输出:远端可能输出一次后长期沉默。
            try { _onFailure?.Invoke(); }
            catch { /* Drain 仍传播原始发送失败。 */ }
        }
    }

    internal async Task DrainAsync()
    {
        lock (_gate)
        {
            _finished = true;
            _queue.Writer.TryComplete();
        }
        await _pump.ConfigureAwait(false);
        Exception? failure;
        lock (_gate) { failure = _failure; }
        if (failure is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }
}
