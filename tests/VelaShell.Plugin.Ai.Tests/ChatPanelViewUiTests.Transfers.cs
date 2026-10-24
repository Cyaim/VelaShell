using System.Reflection;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.LogicalTree;
using Avalonia.Controls;
using VelaShell.Tests.TestSupport;
using Avalonia.VisualTree;
using VelaShell.Plugin.Ai.Agent;
using VelaShell.Plugin.Ai.Ui;
using VelaShell.PluginSdk.RemoteFs;
using VelaShell.PluginSdk.Testing;

namespace VelaShell.Plugin.Ai.Tests;

public sealed partial class ChatPanelViewUiTests
{
    [TestMethod]
    public Task TransferCard_UsesMeasuredProgress_AndOnlySucceededIsGreen() => HeadlessUi.RunOnUiAsync(_session, async () =>
    {
        using var context = new TestPluginContext();
        (Window window, ChatPanelView panel) = await ShowAsync(context);
        try
        {
            var scope = FileTransferJobRegistry.For(context).BeginRun("owner", "run");
            var card = new ChatPanelView.ToolCard(panel, "upload_local_file", "{}");
            card.ShowTransfer(scope, "t");
            window.Content = card.Root;
            var snapshot = new RemoteTransferSnapshot { TransferId = "t", OperationId = "op", SessionId = "s", LocalPath = "source", RemotePath = "/target", State = RemoteTransferState.Running,
                Generation = 1, Sequence = 1, TransferredBytes = 5_000_000_000, TotalBytes = 10_000_000_000 };
            card.UpdateTransfer(new("owner", "run", "t", "", "", snapshot));
            var bar = card.Root.GetVisualDescendants().OfType<ProgressBar>().Single();
            Assert.AreEqual(50d, bar.Value);
            Assert.IsFalse(bar.IsIndeterminate);
            Assert.IsTrue(card.Root.GetVisualDescendants().OfType<Button>().Single().IsVisible);
            card.UpdateTransfer(new("owner", "run", "t", "", "", snapshot with { TotalBytes = -1, Sequence = 2 }));
            Assert.IsTrue(bar.IsIndeterminate);
            card.UpdateTransfer(new("owner", "run", "t", "", "", snapshot with { TransferredBytes = 10_000_000_000, Sequence = 3 }));
            Assert.IsTrue(bar.IsIndeterminate, "满字节仅表示校验/提交，不能伪装成功");
            card.UpdateTransfer(new("owner", "run", "t", "", "", snapshot with { State = RemoteTransferState.Unknown, Sequence = 4 }));
            Assert.IsTrue(card.Root.GetVisualDescendants().OfType<Button>().Single().IsVisible, "未确认停止仍允许显式取消");
            card.UpdateTransfer(new("owner", "run", "t", "", "", snapshot with { State = RemoteTransferState.Succeeded, Sequence = 5 }));
            Assert.IsFalse(bar.IsIndeterminate);
            Assert.IsFalse(card.Root.GetVisualDescendants().OfType<Button>().Single().IsVisible);
        }
        finally { panel.Detach(); window.Close(); }
    });

    [TestMethod]
    public Task HistoricalRunningReceipt_IsNotPresentedAsSuccessful() => HeadlessUi.RunOnUiAsync(_session, async () =>
    {
        using var context = new TestPluginContext();
        (Window window, ChatPanelView panel) = await ShowAsync(context);
        try
        {
            var card = new ChatPanelView.ToolCard(panel, "upload_local_file", "{}");
            window.Content = card.Root;
            card.Complete(AgentToolbox.FormatTransfer(new RemoteTransferSnapshot
            { TransferId = "t", OperationId = "op", SessionId = "s", LocalPath = "source", RemotePath = "/target", State = RemoteTransferState.Running, TotalBytes = 10, TransferredBytes = 10 }));
            Assert.IsTrue(card.Root.GetVisualDescendants().OfType<TextBlock>()
                .Any(t => t.Text == new Loc("en")["TransferHistorical"]));
            Assert.IsFalse(card.Root.GetVisualDescendants().OfType<Button>().Single().IsVisible);
        }
        finally { panel.Detach(); window.Close(); }
    });

    /// <summary>
    /// 下载工具没给出快照 —— 被拒、启动前就挡下、回执丢失的 <c>{"state":"Unknown"}</c> —— 时不打"完成"的勾。
    /// 与上传卡同一条规则,否则用户会以为文件已经下好了。
    /// </summary>
    [TestMethod]
    public Task DownloadCardWithoutASnapshot_IsNotMarkedDone() => HeadlessUi.RunOnUiAsync(_session, async () =>
    {
        using var context = new TestPluginContext();
        (Window window, ChatPanelView panel) = await ShowAsync(context);
        try
        {
            var loc = new Loc("en");
            foreach (string result in new[]
                     {
                         "The user DENIED this download. Do not retry; ask the user how to proceed.",
                         "{\"operationId\":\"op\",\"state\":\"Unknown\",\"error\":\"Download receipt unavailable.\"}"
                     })
            {
                var card = new ChatPanelView.ToolCard(panel, "download_remote_file", "{}");
                window.Content = card.Root;
                card.Complete(result);
                object?[] tips = [.. card.Root.GetLogicalDescendants().OfType<Decorator>().Select(ToolTip.GetTip)];
                Assert.IsTrue(tips.Contains(loc["TransferUnknown"]), result);
                Assert.IsFalse(tips.Contains(loc["ToolDone"]), result);
            }
        }
        finally { panel.Detach(); window.Close(); }
    });

    [TestMethod]
    public Task Binding_KeepsOriginalBubble_MultipleCardsAndRunIsolation() => HeadlessUi.RunOnUiAsync(_session, async () =>
    {
        using var context = new TestPluginContext();
        var session = context.FakeSessions.AddConnected();
        var snapshot = new RemoteTransferSnapshot { TransferId = "t", OperationId = "op", SessionId = session.SessionId, LocalPath = "source", RemotePath = "/target",
            State = RemoteTransferState.Running, Generation = 1, Sequence = 1, TotalBytes = 100, TransferredBytes = 10 };
        context.FakeRemoteFs.SupportsTransferOperations = true;
        context.FakeRemoteFs.StartUploadHandler = (_, _, _, _, _) => Task.FromResult(snapshot);
        context.FakeRemoteFs.GetTransferHandler = (_, _) => Task.FromResult(snapshot);
        var wait = new TaskCompletionSource<RemoteTransferSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        context.FakeRemoteFs.WaitTransferHandler = (_, _, _, ct) => wait.Task.WaitAsync(ct);
        (Window window, ChatPanelView panel) = await ShowAsync(context);
        try
        {
            var scope = FileTransferJobRegistry.For(context).BeginRun("owner", "run");
            var bubble = new ChatPanelView.AssistantBubble(panel);
            using var binding = new ChatPanelView.TransferBinding(panel, scope, bubble);
            await scope.StartAsync(session.SessionId, "source", "/target", "op", false, CancellationToken.None);
            var upload = new ChatPanelView.ToolCard(panel, "upload_local_file", "{}");
            var status = new ChatPanelView.ToolCard(panel, "transfer_status", "{}");
            Assert.IsTrue(binding.Bind(upload, AgentToolbox.FormatTransfer(snapshot)));
            Assert.IsTrue(binding.Bind(status, AgentToolbox.FormatTransfer(snapshot)));
            Assert.IsFalse(binding.Bind(new(panel, "other_tool", "{}"), AgentToolbox.FormatTransfer(snapshot)));
            var other = new StackPanel();
            window.Content = other;
            var end = snapshot with { State = RemoteTransferState.Succeeded, Sequence = 2, TransferredBytes = 100 };
            scope.Publish(new("owner", "run", "t", "", "", snapshot, MonitoringUnavailable: true));
            await PumpAsync();
            Assert.IsTrue(upload.Root.GetLogicalDescendants().OfType<TextBlock>()
                .Any(t => t.Text == new Loc("en")["TransferMonitoringUnavailable"]));
            scope.Publish(new("owner", "run", "t", "", "", snapshot));
            await PumpAsync();
            Assert.IsFalse(upload.Root.GetLogicalDescendants().OfType<TextBlock>()
                .Any(t => t.Text == new Loc("en")["TransferMonitoringUnavailable"]));
            scope.Publish(new("wrong-owner", "run", "t", "", "", end));
            await PumpAsync(3);
            Assert.IsTrue(upload.Root.GetLogicalDescendants().OfType<Button>().Single().IsVisible);
            scope.Publish(new("owner", "run", "t", "", "", end));
            await PumpAsync(3);
            Assert.IsFalse(upload.Root.GetLogicalDescendants().OfType<Button>().Single().IsVisible);
            Assert.IsFalse(status.Root.GetLogicalDescendants().OfType<Button>().Single().IsVisible);
            Assert.IsEmpty(other.Children, "进度不得写入切换后的当前消息容器");
            wait.TrySetResult(end);
            scope.Publish(new("owner", "run", "t", "", "", null, true));
            await PumpAsync(3);
            Assert.IsTrue(upload.Root.GetLogicalDescendants().OfType<TextBlock>()
                .Any(t => t.Text == new Loc("en")["TransferWithheld"]));
        }
        finally { panel.Detach(); window.Close(); }
    });

    /// <summary>
    /// 宿主没有后台下载能力时,点远端链接当场说清 —— 不先弹保存框让用户白挑一次位置。
    /// </summary>
    [TestMethod]
    public Task LinkDownload_WithoutDownloadSupport_FailsBeforeTheSavePicker() => HeadlessUi.RunOnUiAsync(_session, async () =>
    {
        using var context = new TestPluginContext();
        var session = context.FakeSessions.AddConnected();
        context.FakeRemoteFs.AddFile(session.SessionId, "/srv/report.pdf", new byte[8]);
        context.FakeRemoteFs.SupportsTransferOperations = true;
        context.FakeRemoteFs.SupportsDownloadOperations = false;
        (Window window, ChatPanelView panel) = await ShowAsync(context);
        try
        {
            var download = (Task)typeof(ChatPanelView).GetMethod("DownloadRemoteAsync",
                BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(panel, ["/srv/report.pdf"])!;
            await download.WaitAsync(TimeSpan.FromSeconds(10));
            var loc = new Loc("en");
            Assert.AreEqual($"{loc["Error"]}: {loc["TransferDownloadUnsupported"]}", Find<TextBlock>(panel, "StatusText").Text);
        }
        finally { panel.Detach(); window.Close(); }
    });

    /// <summary>
    /// 链接下载卡对停止未确认的 Unknown/TimedOut 显示取消按钮,点下去就得真的发取消 ——
    /// 按钮在、点了却什么都不做,用户只会以为已经停了。
    /// </summary>
    [TestMethod]
    public Task LinkCard_CancelReachesAnUnconfirmedTerminalDownload() => HeadlessUi.RunOnUiAsync(_session, async () =>
    {
        using var context = new TestPluginContext();
        var unknown = new RemoteTransferSnapshot { TransferId = "t", OperationId = "op", SessionId = "s", LocalPath = "/tmp/report.pdf",
            RemotePath = "/srv/report.pdf", Direction = RemoteTransferDirection.Download, State = RemoteTransferState.Unknown,
            RemoteStopConfirmed = false, Generation = 3, Sequence = 4, TotalBytes = 10 };
        context.FakeRemoteFs.SupportsTransferOperations = true;
        context.FakeRemoteFs.SupportsDownloadOperations = true;
        context.FakeRemoteFs.StartDownloadHandler = (_, _, _, _, _) => Task.FromResult(unknown);
        context.FakeRemoteFs.GetTransferHandler = (_, _) => Task.FromResult(unknown);
        context.FakeRemoteFs.CancelTransferHandler = (_, _, _) => Task.FromResult(unknown with
        { State = RemoteTransferState.Cancelled, RemoteStopConfirmed = true, Sequence = 5 });
        (Window window, ChatPanelView panel) = await ShowAsync(context);
        try
        {
            FileTransferRunScope scope = FileTransferJobRegistry.For(context).BeginRun("link:", "run");
            await scope.StartDownloadAsync("s", "/srv/report.pdf", "/tmp/report.pdf", "op", false, CancellationToken.None);
            Type binding = typeof(ChatPanelView).GetNestedType("LinkTransferBinding", BindingFlags.NonPublic)!;
            using var card = (IDisposable)Activator.CreateInstance(binding, BindingFlags.Instance | BindingFlags.NonPublic,
                null, [panel, scope, "t", "/tmp/report.pdf"], null)!;
            await PumpAsync();
            var root = (Border)Find<StackPanel>(panel, "MessagesPanel").Children[^1];
            Button cancel = ((StackPanel)root.Child!).Children.OfType<Button>().Single();
            Assert.IsTrue(cancel.IsVisible, "停止未确认的终态仍显示取消");
            cancel.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            await PumpAsync();
            Assert.HasCount(1, context.FakeRemoteFs.TransfersCancelled);
            Assert.AreEqual(3L, context.FakeRemoteFs.TransfersCancelled[0].ExpectedGeneration);
        }
        finally { panel.Detach(); window.Close(); }
    });

    /// <summary>
    /// 链接另存时选到已有文件,系统保存框已经问过"要替换吗":按覆盖去请宿主确认。
    /// 照旧按不覆盖请求的话,宿主落盘只会报"目标已存在",用户白确认一次;选的是新文件名则照旧不覆盖。
    /// </summary>
    [TestMethod]
    public Task LinkDownload_ToAnExistingFileTheUserChoseToReplace_AsksTheHostToOverwrite() => HeadlessUi.RunOnUiAsync(_session, async () =>
    {
        using var context = new TestPluginContext();
        var session = context.FakeSessions.AddConnected();
        var never = new TaskCompletionSource<RemoteTransferSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        context.FakeRemoteFs.SupportsTransferOperations = true;
        context.FakeRemoteFs.SupportsDownloadOperations = true;
        context.FakeRemoteFs.StartDownloadHandler = (id, remote, local, options, _) => Task.FromResult(new RemoteTransferSnapshot
        {
            TransferId = options.OperationId, OperationId = options.OperationId, SessionId = id, LocalPath = local, RemotePath = remote,
            Direction = RemoteTransferDirection.Download, State = RemoteTransferState.Running, Generation = 1, Sequence = 1, TotalBytes = 8
        });
        context.FakeRemoteFs.WaitTransferHandler = (_, _, _, ct) => never.Task.WaitAsync(ct);
        string folder = Path.Combine(context.DataDirectory, "saved");
        Directory.CreateDirectory(folder);
        string existing = Path.Combine(folder, "report.pdf");
        File.WriteAllText(existing, "old");
        (Window window, ChatPanelView panel) = await ShowAsync(context);
        try
        {
            MethodInfo start = typeof(ChatPanelView).GetMethod("StartLinkDownloadAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
            foreach (string target in new[] { existing, Path.Combine(folder, "new.pdf") })
                await ((Task)start.Invoke(panel, [session.SessionId, "/srv/report.pdf", target])!).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.HasCount(2, context.FakeRemoteFs.DownloadsStarted);
            Assert.IsTrue(context.FakeRemoteFs.DownloadsStarted[0].Options.Overwrite, "保存框里已经确认过替换");
            Assert.IsFalse(context.FakeRemoteFs.DownloadsStarted[1].Options.Overwrite, "新文件名不需要覆盖");
        }
        finally { panel.Detach(); window.Close(); }
    });

    /// <summary>
    /// 链接下载的授权闭包活得和插件一样长(任务登记从不删除),所以只能捕获会话 id 与宿主上下文 ——
    /// 捕获了面板,每关一个聊天面板就漏一个。
    /// </summary>
    [TestMethod]
    public Task LinkScope_AuthorizationDoesNotCaptureThePanel() => HeadlessUi.RunOnUiAsync(_session, async () =>
    {
        using var context = new TestPluginContext();
        var session = context.FakeSessions.AddConnected();
        (Window window, ChatPanelView panel) = await ShowAsync(context);
        try
        {
            var scope = (FileTransferRunScope)typeof(ChatPanelView).GetMethod("BeginLinkTransferScope",
                BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(panel, [session.SessionId])!;
            Func<string, CancellationToken, Task<bool>> authorize = scope._authorize!;
            Assert.IsTrue(await authorize(session.SessionId, CancellationToken.None));
            Assert.IsFalse(await authorize("another-session", CancellationToken.None));
            Assert.IsFalse(CapturesVisual(authorize.Target), "授权闭包不能引用面板或任何控件");
        }
        finally { panel.Detach(); window.Close(); }
    });

    /// <summary>委托目标(含编译器生成的嵌套闭包)里有没有界面对象。</summary>
    private static bool CapturesVisual(object? target, int depth = 0)
        => target is Visual || (target is not null && depth < 4
            && target.GetType().IsDefined(typeof(CompilerGeneratedAttribute), inherit: false)
            && target.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Any(field => CapturesVisual(field.GetValue(target), depth + 1)));
}
