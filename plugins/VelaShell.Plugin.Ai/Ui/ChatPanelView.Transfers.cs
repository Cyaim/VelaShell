using System.Globalization;
using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Threading;
using VelaShell.Plugin.Ai.Agent;
using VelaShell.PluginSdk;
using VelaShell.PluginSdk.RemoteFs;

namespace VelaShell.Plugin.Ai.Ui;

public sealed partial class ChatPanelView
{
    private readonly HashSet<TransferBinding> _transferBindings = [];
    private readonly HashSet<FileTransferRunScope> _transferCleanupScopes = [];
    private readonly HashSet<FileTransferRunScope> _linkTransferScopes = [];
    private readonly HashSet<LinkTransferBinding> _linkTransferBindings = [];

    private TransferBinding BindTransfers(CommandJobRunScope run, AssistantBubble bubble)
    {
        var binding = new TransferBinding(this, run.Transfers!, bubble);
        _transferBindings.Add(binding);
        _transferCleanupScopes.Add(run.Transfers!);
        bubble.Transfers = binding;
        return binding;
    }

    private FileTransferRunScope BeginLinkTransferScope(string sessionId)
    {
        // 授权闭包随任务登记活到插件停用;只捕获宿主上下文与会话 id,捕获 _context 字段就把整个面板一起留住了。
        IPluginContext context = _context;
        FileTransferRunScope scope = FileTransferJobRegistry.For(context).BeginRun(
            "link:" + _active.SessionKey, Guid.NewGuid().ToString("N"),
            async (id, token) => id == sessionId && await context.Sessions.GetAsync(id, token).ConfigureAwait(false) is not null);
        _linkTransferScopes.Add(scope);
        return scope;
    }

    private async Task CloseTransfersAsync()
    {
        TransferBinding[] bindings = [.. _transferBindings];
        foreach (TransferBinding binding in bindings) binding.Dispose();
        FileTransferRunScope[] scopes = [.. _transferCleanupScopes];
        _transferCleanupScopes.Clear();
        foreach (FileTransferRunScope scope in scopes)
        {
            try { await scope.CancelAllAsync(); }
            catch (Exception ex) { _context.Log.Warn($"AI transfer cleanup failed: {ex.Message}"); }
        }

        LinkTransferBinding[] linkBindings = [.. _linkTransferBindings];
        foreach (LinkTransferBinding binding in linkBindings) binding.Dispose();
        FileTransferRunScope[] links = [.. _linkTransferScopes];
        _linkTransferScopes.Clear();
        foreach (FileTransferRunScope scope in links)
        {
            try { await scope.CancelAllAsync(); }
            catch (Exception ex) { _context.Log.Warn($"AI link transfer cleanup failed: {ex.Message}"); }
        }
    }

    private sealed class LinkTransferBinding : IDisposable
    {
        private readonly ChatPanelView _owner;
        private readonly FileTransferRunScope _scope;
        private readonly string _transferId;
        private readonly string _localPath;
        private readonly IDisposable _subscription;
        private readonly DispatcherTimer _timer;
        private FileTransferJobUpdate? _latest;
        private readonly ProgressBar _bar;
        private readonly TextBlock _text;
        private readonly Button _cancel;
        private bool _disposed;

        internal LinkTransferBinding(ChatPanelView owner, FileTransferRunScope scope, string transferId, string localPath)
        {
            _owner = owner; _scope = scope; _transferId = transferId; _localPath = localPath;
            _bar = new ProgressBar { Minimum = 0, Maximum = 100, Height = 5, IsIndeterminate = true };
            _bar[!ForegroundProperty] = new DynamicResourceExtension("VelaAccent");
            _bar[!BackgroundProperty] = new DynamicResourceExtension("VelaBorderPrimary");
            _text = new TextBlock { Text = _owner._loc["TransferStarting"], TextWrapping = Avalonia.Media.TextWrapping.Wrap };
            _text[!TextBlock.FontSizeProperty] = new DynamicResourceExtension("VelaFontSize11");
            _text[!TextBlock.ForegroundProperty] = new DynamicResourceExtension("VelaTextSecondary");
            _cancel = new Button { Content = _owner._loc["TransferCancel"] };
            _cancel[!ThemeProperty] = new DynamicResourceExtension("VelaOutlineButtonTheme");
            _cancel.Click += async (_, _) =>
            {
                // 与按钮可见性同一条规则:停止未确认的 Unknown/TimedOut 终态也得真的发取消
                if (_latest?.Snapshot is not { } snapshot || !ToolCard.CanCancel(snapshot)) return;
                _cancel.IsEnabled = false;
                try { await _scope.CancelAsync(_transferId, snapshot.Generation); }
                catch { _cancel.IsEnabled = true; }
            };
            var panel = new StackPanel { Spacing = 5, Margin = new Thickness(0, 6, 0, 0) };
            panel.Children.Add(_bar); panel.Children.Add(_text); panel.Children.Add(_cancel);
            owner.MessagesPanel.Children.Add(new Border { Classes = { "toolCard" }, Padding = new Thickness(8, 6), Child = panel });
            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            _timer.Tick += (_, _) => { _timer.Stop(); Drain(); };
            _subscription = scope.Subscribe(update =>
            {
                if (update.TransferId != _transferId || update.Withheld || update.Snapshot is null) return;
                _latest = update;
                Dispatcher.UIThread.Post(() => { if (!_disposed && !_timer.IsEnabled) _timer.Start(); });
            });
            _owner._linkTransferBindings.Add(this);
            _ = RefreshAsync();
        }

        private async Task RefreshAsync()
        {
            try
            {
                _latest = new(_scope.OwnerId, _scope.RunId, _transferId, "", "", await _scope.PeekAsync(_transferId));
                Dispatcher.UIThread.Post(Drain);
            }
            catch { }
        }

        private void Drain()
        {
            if (_disposed || _latest?.Snapshot is not { } snapshot) return;
            _text.Text = TransferPresentation.Text(_owner._loc, snapshot);
            _bar.IsIndeterminate = !snapshot.IsTerminal && (snapshot.TotalBytes < 0 || TransferPresentation.StateKey(snapshot) is "TransferStarting" or "TransferFinalizing");
            _bar.Value = TransferPresentation.Percent(snapshot) ?? 0;
            _cancel.IsVisible = ToolCard.CanCancel(snapshot);
            _cancel.IsEnabled = snapshot.State != RemoteTransferState.CancelRequested;
            if (snapshot.IsSuccess) _owner.StatusText.Text = _owner._loc.F("LinkDownloaded", _localPath);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true; _subscription.Dispose(); _timer.Stop();
            _owner._linkTransferBindings.Remove(this);
        }
    }

    internal sealed partial class ToolCard
    {
        internal string ToolName { get; }
        private ProgressBar? _transferBar;
        private TextBlock? _transferText, _resumeHint;
        private Button? _cancelTransfer;
        private RemoteTransferSnapshot? _transferSnapshot;
        private FileTransferRunScope? _transferScope;
        private string _transferId = "";

        internal static bool CanCancel(RemoteTransferSnapshot s) => !s.IsTerminal
            || (!s.RemoteStopConfirmed && s.State is RemoteTransferState.Unknown or RemoteTransferState.TimedOut);

        public void ShowTransfer(FileTransferRunScope? scope, string transferId)
        {
            _transferScope = scope; _transferId = transferId;
            if (_transferBar is not null) return;
            _transferBar = new ProgressBar { Minimum = 0, Maximum = 100, Height = 5, IsIndeterminate = true };
            _transferBar[!ForegroundProperty] = new DynamicResourceExtension("VelaAccent");
            _transferBar[!BackgroundProperty] = new DynamicResourceExtension("VelaBorderPrimary");
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
            _cancelTransfer[!ThemeProperty] = new DynamicResourceExtension("VelaOutlineButtonTheme");
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
            if (ToolName is not ("upload_local_file" or "download_remote_file" or "transfer_status" or "transfer_wait" or "transfer_resume" or "transfer_cancel")) return false;
            try
            {
                using var json = JsonDocument.Parse(result);
                if (!json.RootElement.TryGetProperty("snapshot", out JsonElement element)
                    || !json.RootElement.TryGetProperty("transfer_id", out _))
                {
                    return false;
                }

                RemoteTransferSnapshot? snapshot = element.Deserialize<RemoteTransferSnapshot>();
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
            RemoteTransferSnapshot snapshot = update.Snapshot;
            _transferSnapshot = snapshot;
            _result = AgentToolbox.FormatTransfer(snapshot);
            _transferText.Text = update.MonitoringUnavailable ? _owner._loc["TransferMonitoringUnavailable"]
                : TransferPresentation.Text(_owner._loc, snapshot);
            _transferBar.IsIndeterminate = !snapshot.IsTerminal
                && (snapshot.TotalBytes < 0 || TransferPresentation.StateKey(snapshot) is "TransferStarting" or "TransferFinalizing");
            _transferBar.Value = TransferPresentation.Percent(snapshot) ?? 0;
            string tone = update.MonitoringUnavailable ? "VelaWarning" : snapshot.IsSuccess ? "VelaStatusConnected" : snapshot.IsTerminal ? "VelaWarning" : "VelaAccent";
            _transferBar[!ForegroundProperty] = new DynamicResourceExtension(tone);
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
                // 撤权先在合并队列里生效,避免 UI 刷新前被在途快照覆盖。
                if (_latest.TryGetValue(update.TransferId, out FileTransferJobUpdate? old)
                    && (old.Withheld || (old.Snapshot is { } previous && update.Snapshot is { } next
                        && (next.Generation < previous.Generation || next.Sequence < previous.Sequence
                            || (next.Sequence == previous.Sequence && update.MonitoringUnavailable == old.MonitoringUnavailable)))))
                {
                    return;
                }

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
            if (card.ToolName is not ("upload_local_file" or "download_remote_file" or "transfer_status" or "transfer_wait" or "transfer_resume" or "transfer_cancel")) return false;
            try
            {
                using var json = JsonDocument.Parse(result);
                if (!json.RootElement.TryGetProperty("transfer_id", out JsonElement id)
                    || id.ValueKind != JsonValueKind.String || id.GetString() is not { Length: > 0 } transferId)
                {
                    return false;
                }

                if (!_cards.TryGetValue(transferId, out List<ToolCard>? cards)) _cards[transferId] = cards = [];
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
            foreach (FileTransferJobUpdate update in updates)
            {
                if (_cards.TryGetValue(update.TransferId, out List<ToolCard>? cards))
                    foreach (ToolCard card in cards) card.UpdateTransfer(update);
            }

            if (_finished && (!Scope.HasRunningTransfers
                || (updates.Length > 0 && updates.All(u => u.Withheld || (u.Snapshot?.IsTerminal == true && !u.MonitoringUnavailable)))))
            {
                Dispose();
            }
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
        RemoteTransferState.Running => s.Direction == RemoteTransferDirection.Download ? "TransferDownloading" : "TransferRunning",
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
        string text = $"{loc[StateKey(s)]} · {bytes} · {percent} · {speed} · {eta}";
        return Cause(s) is { } cause ? $"{text} · {cause}" : text;
    }

    private const int MaxCauseLength = 160;

    /// <summary>
    /// 没成功的终态带上宿主给的原因 —— 卡片上只写"失败",用户只能去翻日志。
    /// 原因只是展示文本:折成一行,去掉控制与格式字符(换行、终端转义、双向覆盖),并限长。
    /// </summary>
    private static string? Cause(RemoteTransferSnapshot s)
    {
        if (!s.IsTerminal || s.IsSuccess || string.IsNullOrWhiteSpace(s.ErrorMessage)) return null;
        var line = new StringBuilder(Math.Min(s.ErrorMessage.Length, MaxCauseLength + 1));
        foreach (char c in s.ErrorMessage)
        {
            if (line.Length > MaxCauseLength) break;
            if (!char.IsWhiteSpace(c) && !char.IsControl(c) && char.GetUnicodeCategory(c) != UnicodeCategory.Format)
                line.Append(c);
            else if (line.Length > 0 && line[^1] != ' ')
                line.Append(' ');
        }
        string cause = line.ToString().TrimEnd();
        if (cause.Length <= MaxCauseLength) return cause.Length == 0 ? null : cause;
        int cut = char.IsHighSurrogate(cause[MaxCauseLength - 2]) ? MaxCauseLength - 2 : MaxCauseLength - 1;
        return cause[..cut] + "…";
    }
}
