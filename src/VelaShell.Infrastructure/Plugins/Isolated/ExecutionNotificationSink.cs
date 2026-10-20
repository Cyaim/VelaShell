using System.Threading.Channels;
using VelaShell.Infrastructure.Plugins.Capabilities;

namespace VelaShell.Infrastructure.Plugins.Isolated;

/// <summary>
/// 收集执行事件、单异步写泵按序发送;终态必须 await DrainAsync。
/// 能等的生产者(SSH 读泵)走 <see cref="ReportAsync" />,队列满时等写泵腾出位置 —— 背压一路传回远端。
/// 只能同步上报的调用方走 <see cref="Report" />,队列满时明确失败而不静默丢输出。
/// </summary>
internal sealed class ExecutionNotificationSink<T> : IFlowControlledProgress<T>
{
    private readonly Lock _gate = new();
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
            ThrowIfClosed();
            if (!_queue.Writer.TryWrite(value))
            {
                _failure = new InvalidOperationException("Execution output notification queue overflowed; execution stopped.");
                _queue.Writer.TryComplete();
                _onFailure?.Invoke();
                throw _failure;
            }
        }
    }

    public async ValueTask ReportAsync(T value, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            ThrowIfClosed();
            if (_queue.Writer.TryWrite(value))
            {
                return;
            }
        }
        try
        {
            await _queue.Writer.WriteAsync(value, cancellationToken).ConfigureAwait(false);
        }
        catch (ChannelClosedException)
        {
            // 等待期间写泵失败或已收尾:报出真实原因,而不是一句"通道已关"。
            lock (_gate) { ThrowIfClosed(); }
            throw;
        }
    }

    private void ThrowIfClosed()
    {
        if (_finished)
        {
            throw new InvalidOperationException("Execution output arrived after completion.");
        }
        if (_failure is { } failure)
        {
            throw new InvalidOperationException("Execution notification delivery failed.", failure);
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
            lock (_gate)
            {
                _failure ??= ex;
                // 写泵不再读:唤醒正等空位的生产者,让它立刻带着失败返回,而不是等到取消。
                _queue.Writer.TryComplete();
            }
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
