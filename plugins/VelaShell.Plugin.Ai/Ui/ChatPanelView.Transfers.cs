using System.Globalization;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Threading;
using VelaShell.Plugin.Ai.Agent;
using VelaShell.PluginSdk.RemoteFs;

namespace VelaShell.Plugin.Ai.Ui;

public sealed partial class ChatPanelView
{
    private readonly HashSet<TransferBinding> _transferBindings = [];
    private readonly HashSet<FileTransferRunScope> _transferCleanupScopes = [];

    private TransferBinding BindTransfers(CommandJobRunScope run, AssistantBubble bubble)
    {
        var binding = new TransferBinding(this, run.Transfers!, bubble);
        _transferBindings.Add(binding);
        _transferCleanupScopes.Add(run.Transfers!);
        bubble.Transfers = binding;
        return binding;
    }

    private async Task CloseTransfersAsync()
    {
        TransferBinding[] bindings = [.. _transferBindings];
        foreach (TransferBinding binding in bindings) binding.Dispose();
        FileTransferRunScope[] scopes = [.. _transferCleanupScopes];
        _transferCleanupScopes.Clear();
        foreach (FileTransferRunScope scope in scopes)
            try { await scope.CancelAllAsync(); }
            catch (Exception ex) { _context.Log.Warn($"AI upload cleanup failed: {ex.Message}"); }
    }

    internal sealed partial class ToolCard
    {
        internal string ToolName => _name;
        private ProgressBar? _transferBar;
        private TextBlock? _transferText, _resumeHint;
        private Button? _cancelTransfer;
        private RemoteTransferSnapshot? _transferSnapshot;
        private FileTransferRunScope? _transferScope;
        private string _transferId = "";

        private static bool CanCancel(RemoteTransferSnapshot s) => !s.IsTerminal
            || (!s.RemoteStopConfirmed && s.State is RemoteTransferState.Unknown or RemoteTransferState.TimedOut);

        public void ShowTransfer(FileTransferRunScope? scope, string transferId)
        {
            _transferScope = scope; _transferId = transferId;
            if (_transferBar is not null) return;
            _transferBar = new ProgressBar { Minimum = 0, Maximum = 100, Height = 5, IsIndeterminate = true };
            _transferBar[!ProgressBar.ForegroundProperty] = new DynamicResourceExtension("VelaAccent");
            _transferBar[!ProgressBar.BackgroundProperty] = new DynamicResourceExtension("VelaBorderPrimary");
            _transferText = new TextBlock { Text = _owner._loc["TransferStarting"], TextWrapping = Avalonia.Media.TextWrapping.Wrap };
            _transferText[!TextBlock.FontFamilyProperty] = new DynamicResourceExtension("VelaUiMonoFont");
            _transferText[!TextBlock.FontSizeProperty] = new DynamicResourceExtension("VelaFontSize11");
            _transferText[!TextBlock.ForegroundProperty] = new DynamicResourceExtension("VelaTextSecondary");
            ToolTip.SetTip(_transferText, _owner._loc["TransferPreparationHint"]);
            _resumeHint = new TextBlock { Text = _owner._loc["TransferResumable"], IsVisible = false };
            _resumeHint[!TextBlock.FontFamilyProperty] = new DynamicResourceExtension("VelaUiFont");
            _resumeHint[!TextBlock.FontSizeProperty] = new DynamicResourceExtension("VelaFontSize11");
            _resumeHint[!TextBlock.ForegroundProperty] = new DynamicResourceExtension("VelaTextSecondary");
            _cancelTransfer = new Button { Content = _owner._loc["TransferCancel"], HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left };
            _cancelTransfer[!Button.ThemeProperty] = new DynamicResourceExtension("VelaOutlineButtonTheme");
            _cancelTransfer.Click += async (_, _) =>
            {
                if (_transferSnapshot is not { } current || !CanCancel(current) || _transferScope is null) return;
                _cancelTransfer.IsEnabled = false;
                try { await _transferScope.CancelAsync(_transferId, current.Generation); }
                catch { _transferText.Text = _owner._loc["TransferCancelFailed"]; _cancelTransfer.IsEnabled = true; }
            };
            var progress = new StackPanel { Spacing = 5, Margin = new Thickness(17, 6, 0, 0) };
            progress.Children.Add(_transferBar); progress.Children.Add(_transferText);
            progress.Children.Add(_resumeHint); progress.Children.Add(_cancelTransfer);
            ((StackPanel)Root.Child!).Children.Add(progress);
        }

        internal bool RestoreTransfer(string result)
        {
            if (ToolName is not ("upload_local_file" or "transfer_status" or "transfer_wait" or "transfer_resume" or "transfer_cancel")) return false;
            try
            {
                using var json = JsonDocument.Parse(result);
                if (!json.RootElement.TryGetProperty("snapshot", out var element)
                    || !json.RootElement.TryGetProperty("transfer_id", out _)) return false;
                var snapshot = element.Deserialize<RemoteTransferSnapshot>();
                if (snapshot is null) return false;
                ShowTransfer(null, snapshot.TransferId);
                UpdateTransfer(new("", "", snapshot.TransferId, "", "", snapshot));
                _cancelTransfer!.IsVisible = false;
                if (!snapshot.IsTerminal)
                {
                    _transferText!.Text = _owner._loc["TransferHistorical"];
                    _transferBar!.IsIndeterminate = false;
                    _statusIconHost.Child = MakeIcon("Icon.circle-alert", "VelaWarning", 11);
                    ToolTip.SetTip(_statusIconHost, _owner._loc["TransferHistorical"]);
                }
                return true;
            }
            catch (JsonException) { return false; }
        }

        public void UpdateTransfer(FileTransferJobUpdate update)
        {
            if (_transferBar is null || _transferText is null || _cancelTransfer is null) return;
            if (update.Withheld || update.Snapshot is null)
            {
                _transferWithheld = true; _argsText.Text = "";
                _result = ""; _detailsText.Text = ""; _details.IsVisible = false;
                _transferText.Text = _owner._loc["TransferWithheld"];
                _transferBar.IsIndeterminate = false; _transferBar.Value = 0;
                _cancelTransfer.IsVisible = false; _resumeHint!.IsVisible = false;
                _statusIconHost.Child = MakeIcon("Icon.circle-alert", "VelaWarning", 11);
                return;
            }
            if (_transferWithheld) return;
            var snapshot = update.Snapshot;
            _transferSnapshot = snapshot;
            _result = AgentToolbox.FormatTransfer(snapshot);
            _transferText.Text = update.MonitoringUnavailable ? _owner._loc["TransferMonitoringUnavailable"]
                : TransferPresentation.Text(_owner._loc, snapshot);
            _transferBar.IsIndeterminate = !snapshot.IsTerminal
                && (snapshot.TotalBytes < 0 || TransferPresentation.StateKey(snapshot) is "TransferStarting" or "TransferFinalizing");
            _transferBar.Value = TransferPresentation.Percent(snapshot) ?? 0;
            string tone = update.MonitoringUnavailable ? "VelaWarning" : snapshot.IsSuccess ? "VelaStatusConnected" : snapshot.IsTerminal ? "VelaWarning" : "VelaAccent";
            _transferBar[!ProgressBar.ForegroundProperty] = new DynamicResourceExtension(tone);
            _statusIconHost.Child = MakeIcon(snapshot.IsSuccess ? "Icon.circle-check" : snapshot.IsTerminal ? "Icon.circle-alert" : "Icon.ellipsis", tone, 11);
            ToolTip.SetTip(_statusIconHost, _owner._loc[update.MonitoringUnavailable ? "TransferMonitoringUnavailable" : TransferPresentation.StateKey(snapshot)]);
            _cancelTransfer.IsVisible = CanCancel(snapshot);
            _cancelTransfer.IsEnabled = snapshot.State != RemoteTransferState.CancelRequested;
            _resumeHint!.IsVisible = snapshot.CanResume;
        }
    }

    // 绑定捕获原始气泡、所有者和轮次，绝不依赖当前选中的对话。
    // 快照只替换有界的本地状态，不累加为模型或历史消息帧。
    internal sealed class TransferBinding : IDisposable
    {
        private readonly ChatPanelView _owner;
        private readonly AssistantBubble _bubble;
        private readonly IDisposable _subscription;
        private readonly Dictionary<string, FileTransferJobUpdate> _latest = [];
        private readonly Dictionary<string, List<ToolCard>> _cards = [];
        private readonly Lock _gate = new();
        private readonly DispatcherTimer _timer;
        private bool _disposed, _finished;
        public FileTransferRunScope Scope { get; }

        public TransferBinding(ChatPanelView owner, FileTransferRunScope scope, AssistantBubble bubble)
        {
            _owner = owner; Scope = scope; _bubble = bubble;
            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            _timer.Tick += (_, _) => { _timer.Stop(); Drain(); };
            _subscription = scope.Subscribe(OnProgress);
        }

        private void OnProgress(FileTransferJobUpdate update)
        {
            if (update.OwnerId != Scope.OwnerId || update.RunId != Scope.RunId) return;
            lock (_gate)
            {
                if (_disposed) return;
                if (_latest.TryGetValue(update.TransferId, out var old)
                    && old.Snapshot is { } previous && update.Snapshot is { } next
                    && (next.Generation < previous.Generation || next.Sequence < previous.Sequence
                        || (next.Sequence == previous.Sequence && update.MonitoringUnavailable == old.MonitoringUnavailable))) return;
                _latest[update.TransferId] = update;
            }
            Dispatcher.UIThread.Post(() =>
            {
                if (_disposed) return;
                if (update.Withheld || update.Snapshot?.IsTerminal == true) Drain();
                else if (!_timer.IsEnabled) _timer.Start();
            });
        }

        internal bool Bind(ToolCard card, string result)
        {
            if (card.ToolName is not ("upload_local_file" or "transfer_status" or "transfer_wait" or "transfer_resume" or "transfer_cancel")) return false;
            try
            {
                using var json = JsonDocument.Parse(result);
                if (!json.RootElement.TryGetProperty("transfer_id", out var id)
                    || id.ValueKind != JsonValueKind.String || id.GetString() is not { Length: > 0 } transferId)
                    return false;
                if (!_cards.TryGetValue(transferId, out var cards)) _cards[transferId] = cards = [];
                if (!cards.Contains(card)) cards.Add(card);
                card.ShowTransfer(Scope, transferId);
                // Peek复验归属和授权，但不消费模型的完成续接通知。
                _ = RefreshAsync(transferId);
                Drain();
                return true;
            }
            catch (JsonException) { return false; }
        }

        private async Task RefreshAsync(string transferId)
        {
            try
            {
                RemoteTransferSnapshot snapshot = await Scope.PeekAsync(transferId);
                OnProgress(new(Scope.OwnerId, Scope.RunId, transferId, snapshot.LocalPath, snapshot.RemotePath, snapshot));
            }
            catch (UnauthorizedAccessException) { OnProgress(new(Scope.OwnerId, Scope.RunId, transferId, "", "", null, true)); }
            catch { /* 暂时读失败由独立观察器重试；保留旧卡及取消入口，不伪装成撤权。 */ }
        }

        private void Drain()
        {
            if (_disposed) return;
            FileTransferJobUpdate[] updates;
            lock (_gate) updates = [.. _latest.Values];
            foreach (var update in updates)
                if (_cards.TryGetValue(update.TransferId, out var cards))
                    foreach (var card in cards) card.UpdateTransfer(update);
            if (_finished && (!Scope.HasRunningTransfers
                || (updates.Length > 0 && updates.All(u => u.Withheld || (u.Snapshot?.IsTerminal == true && !u.MonitoringUnavailable))))) Dispose();
        }

        public void Finish() { _finished = true; Drain(); }
        public void Dispose()
        {
            lock (_gate) { if (_disposed) return; _disposed = true; _latest.Clear(); }
            _subscription.Dispose(); _timer.Stop(); _cards.Clear();
            _bubble.Transfers = null;
            _owner._transferBindings.Remove(this);
            if (!Scope.HasRunningTransfers) _owner._transferCleanupScopes.Remove(Scope);
        }
    }
}

/// <summary>工具卡和IM占位消息共享的实测传输状态展示。</summary>
internal static class TransferPresentation
{
    internal static string StateKey(RemoteTransferSnapshot s) => s.State switch
    {
        RemoteTransferState.Queued => "TransferQueued",
        RemoteTransferState.Starting => "TransferStarting",
        RemoteTransferState.Running when s.TotalBytes >= 0 && s.TransferredBytes >= s.TotalBytes => "TransferFinalizing",
        RemoteTransferState.Running => "TransferRunning",
        RemoteTransferState.CancelRequested => "TransferCancelling",
        RemoteTransferState.Succeeded => "TransferSucceeded",
        RemoteTransferState.Failed => "TransferFailed",
        RemoteTransferState.Cancelled => "TransferCancelled",
        RemoteTransferState.TimedOut => "TransferTimedOut",
        _ => "TransferUnknown"
    };

    internal static double? Percent(RemoteTransferSnapshot s) => s.TotalBytes < 0 ? null
        : s.TotalBytes == 0 ? (s.IsSuccess ? 100 : 0)
        : Math.Clamp((double)s.TransferredBytes / s.TotalBytes * 100, 0, 100);

    internal static string Text(Loc loc, RemoteTransferSnapshot s)
    {
        string bytes = s.TotalBytes < 0 ? loc.F("TransferBytesUnknown", s.TransferredBytes.ToString("N0"))
            : loc.F("TransferBytes", s.TransferredBytes.ToString("N0"), s.TotalBytes.ToString("N0"));
        string percent = Percent(s) is { } p ? p.ToString("0.0", CultureInfo.CurrentCulture) + "%" : loc["TransferUnknownTotal"];
        string speed = s.SpeedBytesPerSecond is { } rate ? loc.F("TransferRate", rate.ToString("N0")) : loc["TransferRateUnknown"];
        bool finalizing = StateKey(s) == "TransferFinalizing";
        string eta = !s.IsTerminal && !finalizing && s.EstimatedTimeRemaining is { } remaining
            ? loc.F("TransferEta", Math.Max(0, remaining.TotalSeconds).ToString("0")) : loc["TransferEtaUnknown"];
        return $"{loc[StateKey(s)]} · {bytes} · {percent} · {speed} · {eta}";
    }
}
