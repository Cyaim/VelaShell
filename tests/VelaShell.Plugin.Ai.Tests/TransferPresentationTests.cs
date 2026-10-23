using VelaShell.Plugin.Ai.Bridge;
using VelaShell.Plugin.Ai.Ui;
using VelaShell.PluginSdk.RemoteFs;

namespace VelaShell.Plugin.Ai.Tests;

[TestClass]
public sealed class TransferPresentationTests
{
    [TestMethod]
    public void LargeCountsAndUnknownOrEmptyTotals_DoNotInventSuccess()
    {
        var snapshot = new RemoteTransferSnapshot { TransferId = "t", OperationId = "op", SessionId = "s", LocalPath = "source", RemotePath = "/target", State = RemoteTransferState.Running,
            TransferredBytes = 5_000_000_000, TotalBytes = 10_000_000_000 };
        Assert.AreEqual(50d, TransferPresentation.Percent(snapshot));
        Assert.Contains(5_000_000_000L.ToString("N0"), TransferPresentation.Text(new Loc("en"), snapshot));
        Assert.IsNull(TransferPresentation.Percent(snapshot with { TotalBytes = -1 }));
        Assert.AreEqual(0d, TransferPresentation.Percent(snapshot with { TotalBytes = 0, TransferredBytes = 0 }));
        Assert.AreEqual(100d, TransferPresentation.Percent(snapshot with { State = RemoteTransferState.Succeeded, TotalBytes = 0, TransferredBytes = 0 }));
        Assert.AreEqual("TransferFinalizing", TransferPresentation.StateKey(snapshot with { TransferredBytes = 10_000_000_000 }));
    }

    [TestMethod]
    public void FullBytesRequireExplicitSucceeded_AndFinalizingDoesNotClaimZeroEta()
    {
        var snapshot = new RemoteTransferSnapshot { TransferId = "t", OperationId = "op", SessionId = "s", LocalPath = "source", RemotePath = "/target", State = RemoteTransferState.Running, TotalBytes = 10,
            TransferredBytes = 10, EstimatedTimeRemaining = TimeSpan.Zero };
        var loc = new Loc("en");
        Assert.Contains(loc["TransferFinalizing"], TransferPresentation.Text(loc, snapshot));
        Assert.Contains(loc["TransferEtaUnknown"], TransferPresentation.Text(loc, snapshot));
        foreach (var state in new[] { RemoteTransferState.Unknown, RemoteTransferState.Failed,
                     RemoteTransferState.Cancelled, RemoteTransferState.TimedOut })
            Assert.AreNotEqual("TransferSucceeded", TransferPresentation.StateKey(snapshot with { State = state }));
    }

    /// <summary>
    /// 没成功的终态带上宿主给的原因 —— 卡片上只写"失败",用户只能去翻日志。
    /// 原因只取一行、去掉控制与格式字符并限长;成功或仍在跑的不带。
    /// </summary>
    [TestMethod]
    public void UnsuccessfulTerminalTransfer_ShowsAShortSanitizedCause()
    {
        var loc = new Loc("en");
        var failed = new RemoteTransferSnapshot { TransferId = "t", OperationId = "op", SessionId = "s", LocalPath = "target", RemotePath = "/source",
            Direction = RemoteTransferDirection.Download, State = RemoteTransferState.Failed, TotalBytes = 10,
            ErrorMessage = "Cannot lock the destination\r\ndirectory chain.\u001b‮" };
        string text = TransferPresentation.Text(loc, failed);
        Assert.Contains(loc["TransferFailed"], text);
        Assert.Contains("Cannot lock the destination directory chain.", text);
        Assert.DoesNotContain("\n", text);
        Assert.DoesNotContain("\u001b", text);
        Assert.DoesNotContain("‮", text);
        Assert.Contains("not confirmed", TransferPresentation.Text(loc, failed with
        { State = RemoteTransferState.Unknown, ErrorMessage = "Stop was not confirmed." }));
        Assert.IsLessThan(400, TransferPresentation.Text(loc, failed with { ErrorMessage = new string('x', 5000) }).Length);
        foreach (RemoteTransferState state in new[] { RemoteTransferState.Succeeded, RemoteTransferState.Running })
            Assert.DoesNotContain("stale", TransferPresentation.Text(loc, failed with { State = state, ErrorMessage = "stale" }));
    }

    [TestMethod]
    public void TransferStatusesHaveFiveLocalizedVariants()
    {
        foreach (string locale in new[] { "en", "zh-Hans", "zh-Hant", "ja", "ko" })
        {
            var loc = new Loc(locale);
            foreach (RemoteTransferState state in Enum.GetValues<RemoteTransferState>())
            {
                var snapshot = new RemoteTransferSnapshot { TransferId = "t", OperationId = "op", SessionId = "s", LocalPath = "source", RemotePath = "/target", State = state, TotalBytes = -1 };
                string key = TransferPresentation.StateKey(snapshot);
                Assert.AreNotEqual(key, loc[key]);
                Assert.Contains(loc[key], TransferPresentation.Text(loc, snapshot));
            }
        }
    }

    [TestMethod]
    public void ModelBudgetPausesOnlyWhenSignalled_AndResumesRemainingRatherThanResetting()
    {
        long now = 0;
        using var stop = new CancellationTokenSource();
        using var budget = new BridgeTurnBudget(TimeSpan.FromSeconds(300), stop.Token, () => now);
        now = 10_000;
        budget.LocalWaiting(true);
        now = 900_000;
        Assert.AreEqual(290_000L, budget.RemainingMilliseconds);
        budget.LocalWaiting(false);
        now += 20_000;
        Assert.AreEqual(270_000L, budget.RemainingMilliseconds);
        budget.LocalWaiting(true);
        stop.Cancel();
        Assert.IsTrue(budget.Token.IsCancellationRequested, "Stop/lifetime remain effective during upload waiting");
    }
}
