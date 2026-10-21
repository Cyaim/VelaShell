using VelaShell.Plugin.Ai.Agent;
using VelaShell.Plugin.Ai.Ui;

namespace VelaShell.Plugin.Ai.Bridge;

/// <summary>脱离短工具/模型调用的本地进度观察，不追加模型或历史消息帧。</summary>
internal sealed class BridgeTransferObserver : IDisposable
{
    private readonly FileTransferRunScope _scope;
    private readonly Loc _loc;
    private readonly Action<string> _progress;
    private readonly Action<string> _report;
    private readonly Action<BridgeTransferObserver> _remove;
    private readonly IDisposable _subscription;
    private readonly Lock _gate = new();
    private bool _finished, _disposed;
    private long _lastProgress;
    private string _text = "";
    private readonly Dictionary<string, bool> _terminal = [];
    internal FileTransferRunScope Scope => _scope;

    internal BridgeTransferObserver(FileTransferRunScope scope, Loc loc, Action<string> progress,
        Action<string> report, Action<BridgeTransferObserver> remove)
    {
        _scope = scope; _loc = loc; _progress = progress; _report = report; _remove = remove;
        _subscription = scope.Subscribe(OnProgress);
    }

    internal void Text(string text) { lock (_gate) _text = text; }

    private void OnProgress(FileTransferJobUpdate update)
    {
        if (update.OwnerId != _scope.OwnerId || update.RunId != _scope.RunId) return;
        string? progress = null, report = null;
        bool dispose;
        lock (_gate)
        {
            if (_disposed) return;
            string status = update.Withheld || update.Snapshot is null ? _loc["TransferWithheld"]
                : $"[{update.TransferId}] {(update.MonitoringUnavailable ? _loc["TransferMonitoringUnavailable"] : TransferPresentation.Text(_loc, update.Snapshot))}";
            bool terminal = update.Withheld || (update.Snapshot?.IsTerminal == true && !update.MonitoringUnavailable);
            _terminal[update.TransferId] = terminal;
            long now = Environment.TickCount64;
            if (_finished)
            {
                if (terminal) report = status;
            }
            else if (terminal || now - _lastProgress >= 1200)
            {
                _lastProgress = now;
                progress = $"{_text}\n\n{status}";
            }
            dispose = _finished && (!_scope.HasRunningTransfers || (_terminal.Count > 0 && _terminal.Values.All(t => t)));
        }
        if (progress is not null) _progress(progress);
        if (report is not null) _report(report);
        if (dispose) Dispose();
    }

    internal void Finish()
    {
        lock (_gate)
        {
            _finished = true;
            if (!_scope.HasRunningTransfers) Dispose();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _subscription.Dispose();
            _remove(this);
        }
    }
}
