using System.Runtime.ExceptionServices;

namespace VelaShell.PluginHost;

/// <summary>
/// 只在 RPC 有序通知读泵上同步转发。接收器必须快速且不阻塞,不得同步等待本连接 RPC。
/// 插件回调失败仅中止本操作并留存原异常,不会破坏其它 RPC 或把丢失输出伪装为成功。
/// </summary>
internal sealed class ExecutionEventReceiver<T>(IProgress<T> sink, Action cancel, bool requireSequence = true,
    Func<T, bool>? isOutput = null)
{
    private readonly object _gate = new();
    private ExceptionDispatchInfo? _failure;
    private long _lastSequence;
    private long _outputCount;
    private bool _completed;

    internal void Report(long sequence, T value)
    {
        lock (_gate) { ReportUnderLock(sequence, value); }
    }

    private void ReportUnderLock(long sequence, T value)
    {
        if (_completed || _failure is not null)
        {
            return;
        }
        try
        {
            if ((requireSequence && sequence <= 0) || (sequence > 0 && sequence != _lastSequence + 1))
            {
                throw new IOException("Execution event sequence is incomplete or out of order.");
            }
            if (sequence > 0) { _lastSequence = sequence; }
            sink.Report(value);
            if (isOutput?.Invoke(value) ?? true) { _outputCount++; }
        }
        catch (Exception ex)
        {
            _failure = ExceptionDispatchInfo.Capture(ex);
            try { cancel(); }
            catch { /* 回调错误原样留存,取消投递失败不影响其它 RPC。 */ }
        }
    }

    internal void ValidateOutputCount(long expected)
    {
        lock (_gate)
        {
            if (expected < 0 || expected != _outputCount)
            {
                _failure ??= ExceptionDispatchInfo.Capture(new IOException("Execution output delivery count does not match its terminal result."));
            }
        }
        ThrowIfFailed();
    }

    internal void ThrowIfFailed()
    {
        ExceptionDispatchInfo? failure;
        lock (_gate)
        {
            _completed = true;
            failure = _failure;
        }
        failure?.Throw();
    }
}
