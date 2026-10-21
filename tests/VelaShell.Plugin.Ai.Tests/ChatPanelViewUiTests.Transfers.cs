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
}
