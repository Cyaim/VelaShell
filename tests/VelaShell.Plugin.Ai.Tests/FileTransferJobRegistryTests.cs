using System.Text.Json;
using Microsoft.Extensions.AI;
using VelaShell.Plugin.Ai.Agent;
using VelaShell.Plugin.Ai.Configuration;
using VelaShell.Plugin.Ai.Interop;
using VelaShell.PluginSdk.RemoteFs;
using VelaShell.PluginSdk.Testing;

namespace VelaShell.Plugin.Ai.Tests;

[TestClass]
public sealed class FileTransferJobRegistryTests
{
    private static RemoteTransferSnapshot Running(string session, string source, string destination, string operation)
        => new()
        {
            TransferId = "transfer",
            OperationId = operation,
            SessionId = session,
            LocalPath = source,
            RemotePath = destination,
            State = RemoteTransferState.Running,
            Sequence = 1,
            Generation = 1,
            TotalBytes = 64L * 1024 * 1024,
            TransferredBytes = 1024,
            SpeedBytesPerSecond = 256,
            EstimatedTimeRemaining = TimeSpan.FromSeconds(20)
        };

    private static AIFunction Tool(AgentToolbox toolbox, CommandJobRunScope run, string name)
        => toolbox.CreateTools(ChatMode.Agent, runScope: run).OfType<AIFunction>().Single(t => t.Name == name);

    private static async Task<string> Call(AgentToolbox toolbox, CommandJobRunScope run, string name,
        Dictionary<string, object?> args, CancellationToken token = default)
        => (await Tool(toolbox, run, name).InvokeAsync([with(args)], token))?.ToString() ?? "";

    private static string Source(long size)
    {
        string path = Path.Combine(Path.GetTempPath(), "vela-transfer-" + Guid.NewGuid().ToString("N"));
        using FileStream stream = File.Create(path);
        stream.SetLength(size);
        return path;
    }

    [TestMethod]
    public async Task LargeUpload_ReturnsReceipt_AndRequestCancellationOrDisposeDoesNotCancelHost()
    {
        using var context = new TestPluginContext();
        var session = context.FakeSessions.AddConnected();
        var finish = new TaskCompletionSource<RemoteTransferSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        RemoteTransferSnapshot? snapshot = null;
        context.FakeRemoteFs.SupportsTransferOperations = true;
        context.FakeRemoteFs.StartUploadHandler = (id, source, target, options, _) =>
            Task.FromResult(snapshot = Running(id, source, target, options.OperationId));
        context.FakeRemoteFs.GetTransferHandler = (_, _) => Task.FromResult(snapshot!);
        context.FakeRemoteFs.WaitTransferHandler = (_, _, _, token) => finish.Task.WaitAsync(token);
        var toolbox = new AgentToolbox(context) { Approval = ApprovalMode.Bypass };
        CommandJobRunScope run = toolbox.BeginRun("owner", "run");
        string local = Source(64L * 1024 * 1024);
        try
        {
            string receipt = await Call(toolbox, run, "upload_local_file", new()
            { ["localPath"] = local, ["remotePath"] = "/tmp/large", ["sessionId"] = session.SessionId });
            using JsonDocument json = JsonDocument.Parse(receipt);
            Assert.AreEqual("transfer", json.RootElement.GetProperty("transfer_id").GetString());
            Assert.IsTrue(json.RootElement.GetProperty("stillRunning").GetBoolean());
            Assert.HasCount(1, context.FakeRemoteFs.UploadsStarted);
            string status = await Call(toolbox, run, "transfer_status", new() { ["transferId"] = "transfer" });
            Assert.Contains("speedBytesPerSecond", status);
            string timedWait = await Call(toolbox, run, "transfer_wait", new() { ["transferId"] = "transfer", ["waitSeconds"] = 0 });
            Assert.Contains("\"stillRunning\":true", timedWait);
            Assert.IsEmpty(context.FakeRemoteFs.TransfersCancelled);
            using var cancel = new CancellationTokenSource();
            Task waiting = Call(toolbox, run, "transfer_wait", new() { ["transferId"] = "transfer", ["waitSeconds"] = 30 }, cancel.Token);
            await cancel.CancelAsync();
            await Assert.ThrowsAsync<OperationCanceledException>(async () => await waiting);
            await run.DisposeAsync();
            await toolbox.DisposeAsync();
            Assert.IsEmpty(context.FakeRemoteFs.TransfersCancelled);
            finish.SetResult(snapshot! with { State = RemoteTransferState.Succeeded, Sequence = 2 });
        }
        finally { File.Delete(local); }
    }

    [TestMethod]
    public async Task AcceptedStart_CallCancellationAndExplicitOwnerCleanup_CancelsLateReceipt()
    {
        using var context = new TestPluginContext();
        var accepted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var receipt = new TaskCompletionSource<RemoteTransferSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var observed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken hostRequest = default;
        context.FakeRemoteFs.SupportsTransferOperations = true;
        context.FakeRemoteFs.StartUploadHandler = (_, _, _, _, ct) =>
        {
            hostRequest = ct;
            accepted.TrySetResult();
            return receipt.Task.WaitAsync(ct);
        };
        context.FakeRemoteFs.CancelTransferHandler = (id, generation, _) =>
        {
            Assert.AreEqual("transfer", id);
            Assert.AreEqual(7L, generation);
            cancelled.TrySetResult();
            return Task.FromResult(Running("session", "source", "destination", "operation") with
            { State = RemoteTransferState.Cancelled, Generation = generation, Sequence = 2, RemoteStopConfirmed = true });
        };
        FileTransferRunScope scope = FileTransferJobRegistry.For(context).BeginRun("owner", "run");
        using IDisposable subscription = scope.Subscribe(update =>
        { if (update.Snapshot?.State == RemoteTransferState.Cancelled) observed.TrySetResult(); });
        using var request = new CancellationTokenSource();
        Task<RemoteTransferSnapshot> start = scope.StartAsync("session", "source", "destination", "operation", false, request.Token);
        await accepted.Task;
        Assert.IsTrue(scope.HasRunningTransfers, "未取得回执的已批准启动也必须参与闲置保护");
        await request.CancelAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(() => start);
        Assert.IsFalse(hostRequest.IsCancellationRequested, "调用令牌只能停止等待回执，不能取消宿主请求观察");
        await scope.CancelAllAsync();
        await scope.DisposeAsync();
        receipt.SetResult(Running("session", "source", "destination", "operation") with { Generation = 7 });
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await observed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await scope.StartAsync("session", "source", "destination", "operation", false, default);
        Assert.IsFalse(scope.HasRunningTransfers);
        Assert.HasCount(1, context.FakeRemoteFs.UploadsStarted);
    }

    [TestMethod]
    public async Task Completion_IsDeliveredOnce_AndStatusConsumesTerminal()
    {
        using var context = new TestPluginContext();
        var session = context.FakeSessions.AddConnected();
        var finish = new TaskCompletionSource<RemoteTransferSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        RemoteTransferSnapshot? snapshot = null;
        context.FakeRemoteFs.SupportsTransferOperations = true;
        context.FakeRemoteFs.StartUploadHandler = (id, source, target, options, _) =>
            Task.FromResult(snapshot = Running(id, source, target, options.OperationId));
        context.FakeRemoteFs.WaitTransferHandler = (_, _, _, ct) => finish.Task.WaitAsync(ct);
        context.FakeRemoteFs.GetTransferHandler = (_, _) => Task.FromResult(snapshot!);
        await using var toolbox = new AgentToolbox(context) { Approval = ApprovalMode.Bypass };
        await using CommandJobRunScope run = toolbox.BeginRun("owner", "run");
        string local = Source(1);
        try
        {
            await Call(toolbox, run, "upload_local_file", new()
            {
                ["localPath"] = local,
                ["remotePath"] = "/tmp/file",
                ["sessionId"] = session.SessionId
            });
            Task<IReadOnlyList<RemoteTransferSnapshot>> completed = run.Transfers!.WaitForCompletionAsync();
            Assert.IsFalse(completed.IsCompleted);
            snapshot = snapshot! with { State = RemoteTransferState.Succeeded, Sequence = 2 };
            finish.SetResult(snapshot);
            Assert.HasCount(1, await completed.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.IsEmpty(await run.Transfers.WaitForCompletionAsync());
            Assert.IsFalse(run.Transfers.HasActiveJobs);
        }
        finally { File.Delete(local); }
    }

    [TestMethod]
    public async Task TwoRuns_CannotReadEachOthersTransfer_AndLargeDownloadStartsInBackground()
    {
        using var context = new TestPluginContext();
        var session = context.FakeSessions.AddConnected();
        context.FakeRemoteFs.SupportsTransferOperations = true;
        context.FakeRemoteFs.SupportsDownloadOperations = true;
        context.FakeRemoteFs.StartUploadHandler = (id, source, target, options, _) => Task.FromResult(
            Running(id, source, target, options.OperationId) with { State = RemoteTransferState.Succeeded });
        var downloadCompletion = new TaskCompletionSource<RemoteTransferSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        context.FakeRemoteFs.StartDownloadHandler = (id, remote, local, options, _) => Task.FromResult(
            Running(id, local, remote, options.OperationId) with { TransferId = "download-transfer", Direction = RemoteTransferDirection.Download });
        context.FakeRemoteFs.WaitTransferHandler = (_, _, _, ct) => downloadCompletion.Task.WaitAsync(ct);
        await using var toolbox = new AgentToolbox(context) { Approval = ApprovalMode.Bypass };
        await using CommandJobRunScope first = toolbox.BeginRun("owner", "first");
        await using CommandJobRunScope second = toolbox.BeginRun("owner", "second");
        string local = Source(1);
        try
        {
            await Call(toolbox, first, "upload_local_file", new()
            {
                ["localPath"] = local,
                ["remotePath"] = "/tmp/file",
                ["sessionId"] = session.SessionId
            });
            await Assert.ThrowsAsync<FileNotFoundException>(() => second.Transfers!.ReadAsync("transfer"));
            Assert.IsEmpty(context.FakeRemoteFs.TransfersRead);
            context.FakeRemoteFs.AddFile(session.SessionId, "/tmp/large", new byte[33 * 1024 * 1024]);
            string download = await Call(toolbox, first, "download_remote_file", new()
            {
                ["remotePath"] = "/tmp/large",
                ["sessionId"] = session.SessionId
            });
            using JsonDocument downloadJson = JsonDocument.Parse(download);
            Assert.AreEqual("Download", downloadJson.RootElement.GetProperty("direction").GetString());
            Assert.AreEqual("download-transfer", downloadJson.RootElement.GetProperty("transfer_id").GetString());
            Assert.IsTrue(downloadJson.RootElement.GetProperty("stillRunning").GetBoolean());
            Assert.HasCount(1, context.FakeRemoteFs.DownloadsStarted);
            Assert.IsFalse(context.FakeRemoteFs.DownloadsStarted[0].Options.Overwrite);
            await Assert.ThrowsAsync<FileNotFoundException>(() => second.Transfers!.ReadAsync("download-transfer"));
            Assert.IsEmpty(context.FakeRemoteFs.TransfersRead);
            downloadCompletion.SetResult(Running(session.SessionId, "", "/tmp/large", "download") with
            {
                TransferId = "download-transfer",
                Direction = RemoteTransferDirection.Download,
                State = RemoteTransferState.Succeeded,
                Sequence = 2
            });
            RemoteTransferSnapshot completed = (await first.Transfers!.WaitForCompletionAsync().WaitAsync(TimeSpan.FromSeconds(5)))[0];
            Assert.AreEqual(RemoteTransferDirection.Download, completed.Direction);
            Assert.IsTrue(completed.IsSuccess);
        }
        finally { File.Delete(local); }
    }

    [TestMethod]
    public async Task DeniedUpload_DoesNotStart_AndUnknownNeverResumes()
    {
        using var context = new TestPluginContext();
        var session = context.FakeSessions.AddConnected();
        context.FakeRemoteFs.SupportsTransferOperations = true;
        context.FakeRemoteFs.StartUploadHandler = (id, source, target, options, _) => Task.FromResult(
            Running(id, source, target, options.OperationId) with { State = RemoteTransferState.Unknown });
        context.FakeRemoteFs.GetTransferHandler = (_, _) => Task.FromResult(Running(session.SessionId, "", "", "")
            with
        { State = RemoteTransferState.Unknown });
        await using var toolbox = new AgentToolbox(context) { ApprovalHandler = _ => Task.FromResult(false) };
        string local = Source(1);
        try
        {
            await using CommandJobRunScope denied = toolbox.BeginRun("owner", "denied");
            string result = await Call(toolbox, denied, "upload_local_file", new()
            {
                ["localPath"] = local,
                ["remotePath"] = "/tmp/file",
                ["sessionId"] = session.SessionId
            });
            Assert.Contains("DENIED", result);
            Assert.IsEmpty(context.FakeRemoteFs.UploadsStarted);
            toolbox.Approval = ApprovalMode.Bypass;
            await using CommandJobRunScope unknown = toolbox.BeginRun("owner", "unknown");
            await Call(toolbox, unknown, "upload_local_file", new()
            {
                ["localPath"] = local,
                ["remotePath"] = "/tmp/file",
                ["sessionId"] = session.SessionId
            });
            Assert.IsTrue(unknown.HasUnknownOutcome);
            string resumed = await Call(toolbox, unknown, "transfer_resume", new()
            {
                ["transferId"] = "transfer",
                ["expectedGeneration"] = 1L,
                ["sessionId"] = session.SessionId
            });
            Assert.Contains("not confirmed", resumed);
            Assert.IsEmpty(context.FakeRemoteFs.TransfersResumed);
        }
        finally { File.Delete(local); }
    }

    [TestMethod]
    public async Task WaitFailure_FallsBackToGetRunning_ThenKeepsWatchingUntilCompletion()
    {
        using var context = new TestPluginContext();
        var session = context.FakeSessions.AddConnected();
        var finish = new TaskCompletionSource<RemoteTransferSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        RemoteTransferSnapshot? snapshot = null;
        int waits = 0;
        context.FakeRemoteFs.SupportsTransferOperations = true;
        context.FakeRemoteFs.StartUploadHandler = (id, source, target, options, _) =>
            Task.FromResult(snapshot = Running(id, source, target, options.OperationId));
        context.FakeRemoteFs.GetTransferHandler = (_, _) => Task.FromResult(snapshot!);
        context.FakeRemoteFs.WaitTransferHandler = (_, _, _, ct) => Interlocked.Increment(ref waits) == 1
            ? Task.FromException<RemoteTransferSnapshot>(new IOException("temporary")) : finish.Task.WaitAsync(ct);
        await using var toolbox = new AgentToolbox(context) { Approval = ApprovalMode.Bypass };
        await using CommandJobRunScope run = toolbox.BeginRun("owner", "run");
        string local = Source(1);
        try
        {
            await Call(toolbox, run, "upload_local_file", new()
            {
                ["localPath"] = local,
                ["remotePath"] = "/tmp/file",
                ["sessionId"] = session.SessionId
            });
            Task<IReadOnlyList<RemoteTransferSnapshot>> completed = run.Transfers!.WaitForCompletionAsync();
            finish.SetResult(snapshot! with { State = RemoteTransferState.Succeeded, Sequence = 2 });
            Assert.AreEqual(RemoteTransferState.Succeeded,
                (await completed.WaitAsync(TimeSpan.FromSeconds(5)))[0].State);
            Assert.IsTrue(waits >= 2);
        }
        finally { File.Delete(local); }
    }

    [TestMethod]
    public async Task UiPeek_DoesNotConsumeTerminal_AndFullBytesAreNotSuccess()
    {
        using var context = new TestPluginContext();
        var session = context.FakeSessions.AddConnected();
        var finish = new TaskCompletionSource<RemoteTransferSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        RemoteTransferSnapshot? snapshot = null;
        context.FakeRemoteFs.SupportsTransferOperations = true;
        context.FakeRemoteFs.StartUploadHandler = (id, source, target, options, _) => Task.FromResult(
            snapshot = Running(id, source, target, options.OperationId) with { TransferredBytes = 64L * 1024 * 1024 });
        context.FakeRemoteFs.WaitTransferHandler = (_, _, _, ct) => finish.Task.WaitAsync(ct);
        context.FakeRemoteFs.GetTransferHandler = (_, _) => Task.FromResult(snapshot!);
        await using var toolbox = new AgentToolbox(context) { Approval = ApprovalMode.Bypass };
        await using CommandJobRunScope run = toolbox.BeginRun("owner", "run");
        string local = Source(1);
        try
        {
            string receipt = await Call(toolbox, run, "upload_local_file", new()
            {
                ["localPath"] = local,
                ["remotePath"] = "/tmp/file",
                ["sessionId"] = session.SessionId
            });
            Assert.Contains("\"stillRunning\":true", receipt);
            snapshot = snapshot! with { State = RemoteTransferState.Succeeded, Sequence = 2 };
            RemoteTransferSnapshot peek = await run.Transfers!.PeekAsync("transfer");
            Assert.IsTrue(peek.IsSuccess);
            Assert.IsTrue(run.Transfers.HasActiveJobs, "界面读取不能消费模型完成通知");
            Assert.HasCount(1, await run.Transfers.WaitForCompletionAsync());
            Assert.IsEmpty(await run.Transfers.WaitForCompletionAsync());
            finish.TrySetResult(snapshot);
        }
        finally { File.Delete(local); }
    }

    [TestMethod]
    public async Task MonitorFailure_DoesNotForgeHostSequence_AndStillAllowsExplicitCleanup()
    {
        using var context = new TestPluginContext();
        var session = context.FakeSessions.AddConnected();
        RemoteTransferSnapshot? snapshot = null;
        bool recover = false;
        context.FakeRemoteFs.SupportsTransferOperations = true;
        context.FakeRemoteFs.StartUploadHandler = (id, source, target, options, _) =>
            Task.FromResult(snapshot = Running(id, source, target, options.OperationId));
        context.FakeRemoteFs.WaitTransferHandler = (_, _, _, _) => Task.FromException<RemoteTransferSnapshot>(new IOException("offline"));
        context.FakeRemoteFs.GetTransferHandler = (_, _) => recover ? Task.FromResult(snapshot!)
            : Task.FromException<RemoteTransferSnapshot>(new IOException("offline"));
        context.FakeRemoteFs.CancelTransferHandler = (_, _, _) => Task.FromResult(snapshot! with
        { State = RemoteTransferState.Cancelled, Sequence = 2, RemoteStopConfirmed = true });
        await using var toolbox = new AgentToolbox(context) { Approval = ApprovalMode.Bypass };
        await using CommandJobRunScope run = toolbox.BeginRun("owner", "run");
        List<FileTransferJobUpdate> updates = [];
        using IDisposable subscription = run.Transfers!.Subscribe(updates.Add);
        string local = Source(1);
        try
        {
            await Call(toolbox, run, "upload_local_file", new()
            {
                ["localPath"] = local,
                ["remotePath"] = "/tmp/file",
                ["sessionId"] = session.SessionId
            });
            RemoteTransferSnapshot unknown = (await run.Transfers!.WaitForCompletionAsync().WaitAsync(TimeSpan.FromSeconds(5)))[0];
            Assert.AreEqual(RemoteTransferState.Unknown, unknown.State);
            Assert.AreEqual(1L, unknown.Sequence, "本地观察失败不能递增宿主权威Sequence");
            Assert.IsTrue(run.Transfers.HasRunningTransfers);
            Assert.IsTrue(run.HasUnknownOutcome);
            Assert.IsEmpty(context.FakeRemoteFs.TransfersCancelled, "网络异常不能伪装成撤权取消");
            Assert.IsTrue(updates.Last().MonitoringUnavailable);
            Assert.AreEqual(RemoteTransferState.Running, updates.Last().Snapshot!.State);
            recover = true;
            Assert.AreEqual(1L, (await run.Transfers.PeekAsync("transfer")).Sequence);
            Assert.IsFalse(updates.Last().MonitoringUnavailable, "同一宿主Sequence恢复观察必须通知界面");
            await run.Transfers.CancelAllAsync();
            Assert.HasCount(1, context.FakeRemoteFs.TransfersCancelled);
        }
        finally { File.Delete(local); }
    }

    [TestMethod]
    public async Task UnconfirmedHostUnknown_RemainsExplicitlyCancellable()
    {
        using var context = new TestPluginContext();
        RemoteTransferSnapshot snapshot = Running("session", "source", "destination", "start") with
        { State = RemoteTransferState.Unknown, RemoteStopConfirmed = false };
        context.FakeRemoteFs.SupportsTransferOperations = true;
        context.FakeRemoteFs.StartUploadHandler = (_, _, _, _, _) => Task.FromResult(snapshot);
        context.FakeRemoteFs.CancelTransferHandler = (_, _, _) => Task.FromResult(snapshot with
        { State = RemoteTransferState.Cancelled, Sequence = 2, RemoteStopConfirmed = true });
        FileTransferRunScope scope = FileTransferJobRegistry.For(context).BeginRun("owner", "run");
        await scope.StartAsync("session", "source", "destination", "start", false, default);
        Assert.IsTrue(scope.HasRunningTransfers);
        await scope.CancelAllAsync();
        Assert.HasCount(1, context.FakeRemoteFs.TransfersCancelled);
        Assert.IsFalse(scope.HasRunningTransfers);
    }

    [TestMethod]
    public async Task RevokedScope_ProducesOnlyWithheldNotification_AndNoPathLeak()
    {
        using var context = new TestPluginContext();
        bool allowed = true;
        var finish = new TaskCompletionSource<RemoteTransferSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        RemoteTransferSnapshot snapshot = Running("session", "private-source", "private-destination", "operation");
        context.FakeRemoteFs.SupportsTransferOperations = true;
        context.FakeRemoteFs.StartUploadHandler = (_, _, _, _, _) => Task.FromResult(snapshot);
        context.FakeRemoteFs.WaitTransferHandler = (_, _, _, ct) => finish.Task.WaitAsync(ct);
        context.FakeRemoteFs.CancelTransferHandler = (_, _, _) => Task.FromResult(snapshot);
        FileTransferRunScope scope = FileTransferJobRegistry.For(context).BeginRun("owner", "run", (_, _) => Task.FromResult(allowed));
        List<FileTransferJobUpdate> updates = [];
        using IDisposable subscription = scope.Subscribe(updates.Add);
        await scope.StartAsync("session", "private-source", "private-destination", "operation", false, default);
        allowed = false;
        finish.SetResult(snapshot with { State = RemoteTransferState.Succeeded, Sequence = 2 });
        RemoteTransferSnapshot withheld = (await scope.WaitForCompletionAsync().WaitAsync(TimeSpan.FromSeconds(5)))[0];
        Assert.AreEqual(RemoteTransferState.Unknown, withheld.State);
        Assert.AreEqual("", withheld.LocalPath);
        Assert.AreEqual("", withheld.RemotePath);
        Assert.AreEqual("", withheld.SessionId);
        Assert.IsTrue(updates.Last().Withheld);
        Assert.IsNull(updates.Last().Snapshot);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => scope.PeekAsync("transfer"));
    }

    [TestMethod]
    public async Task HostAuthorizationRevoked_WaitExceptionWithholdsMetadataAndStopsModelSafely()
    {
        using var context = new TestPluginContext();
        var revoked = new TaskCompletionSource<RemoteTransferSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        RemoteTransferSnapshot snapshot = Running("private-session", "private-source", "private-destination", "operation");
        context.FakeRemoteFs.SupportsTransferOperations = true;
        context.FakeRemoteFs.StartUploadHandler = (_, _, _, _, _) => Task.FromResult(snapshot);
        context.FakeRemoteFs.WaitTransferHandler = (_, _, _, ct) => revoked.Task.WaitAsync(ct);
        context.FakeRemoteFs.CancelTransferHandler = (_, _, _) => Task.FromResult(snapshot with
        {
            SessionId = "",
            LocalPath = "",
            RemotePath = "",
            State = RemoteTransferState.Unknown,
            Sequence = 2,
            ErrorCode = "permission-revoked"
        });
        FileTransferRunScope scope = FileTransferJobRegistry.For(context).BeginRun("owner", "run");
        var hidden = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using IDisposable subscription = scope.Subscribe(update =>
        {
            if (!update.Withheld) return;
            Assert.IsNull(update.Snapshot);
            Assert.AreEqual("", update.LocalPath);
            Assert.AreEqual("", update.RemotePath);
            hidden.TrySetResult();
        });
        await scope.StartAsync("private-session", "private-source", "private-destination", "operation", false, default);
        revoked.SetException(new PluginSdk.PluginPermissionDeniedException("Upload authorization was revoked."));
        RemoteTransferSnapshot withheld = (await scope.WaitForCompletionAsync().WaitAsync(TimeSpan.FromSeconds(5)))[0];
        await hidden.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsTrue(scope.HasUnknownOutcome);
        Assert.AreEqual(RemoteTransferState.Unknown, withheld.State);
        Assert.AreEqual("", withheld.SessionId);
        Assert.AreEqual("", withheld.LocalPath);
        Assert.AreEqual("", withheld.RemotePath);
        Assert.IsEmpty(context.FakeRemoteFs.TransfersRead, "明确撤权不能当网络错误回读泄漏旧快照");
    }

    [TestMethod]
    public async Task Resume_PreservesHostGenerationAndCurrentTarget_AndCancelCarriesGeneration()
    {
        using var context = new TestPluginContext();
        RemoteTransferSnapshot snapshot = Running("old-session", "source", "destination", "start") with
        {
            State = RemoteTransferState.Failed,
            RemoteStopConfirmed = true,
            Resumable = true,
            DestinationCommitConfirmedAbsent = true
        };
        context.FakeRemoteFs.SupportsTransferOperations = true;
        context.FakeRemoteFs.StartUploadHandler = (_, _, _, _, _) => Task.FromResult(snapshot);
        context.FakeRemoteFs.ResumeTransferHandler = (_, options, _) => Task.FromResult(snapshot = snapshot with
        { SessionId = options.TargetSessionId, Generation = 2, Sequence = 2, State = RemoteTransferState.Succeeded });
        context.FakeRemoteFs.CancelTransferHandler = (_, generation, _) => generation == 2 ? Task.FromResult(snapshot)
            : Task.FromException<RemoteTransferSnapshot>(new InvalidOperationException("stale generation"));
        FileTransferRunScope scope = FileTransferJobRegistry.For(context).BeginRun("owner", "run");
        await scope.StartAsync("old-session", "source", "destination", "start", false, default);
        RemoteTransferSnapshot resumed = await scope.ResumeAsync("transfer", new RemoteTransferResumeOptions
        { OperationId = "resume", ExpectedGeneration = 1, TargetSessionId = "current-session" }, default);
        Assert.AreEqual(2L, resumed.Generation);
        Assert.AreEqual("current-session", context.FakeRemoteFs.TransfersResumed[0].Options.TargetSessionId);
        await Assert.ThrowsAsync<InvalidOperationException>(() => scope.CancelAsync("transfer", 1));
        await scope.CancelAsync("transfer", 2);
        Assert.AreEqual(2L, context.FakeRemoteFs.TransfersCancelled.Last().ExpectedGeneration);
    }

    [TestMethod]
    public async Task LostStartReceipt_BlocksAutomaticSideEffects_AndUsesStableCallIdentity()
    {
        using var context = new TestPluginContext();
        var session = context.FakeSessions.AddConnected();
        context.FakeRemoteFs.SupportsTransferOperations = true;
        context.FakeRemoteFs.StartUploadHandler = (_, _, _, _, _) =>
            Task.FromException<RemoteTransferSnapshot>(new IOException("receipt lost"));
        await using var toolbox = new AgentToolbox(context) { Approval = ApprovalMode.Bypass };
        await using CommandJobRunScope run = toolbox.BeginRun("owner", "run");
        string local = Source(1);
        try
        {
            AgentToolbox.InvocationIdentity.Value = "stable-call";
            string first = await Call(toolbox, run, "upload_local_file", new()
            {
                ["localPath"] = local,
                ["remotePath"] = "/tmp/file",
                ["sessionId"] = session.SessionId
            });
            string second = await Call(toolbox, run, "upload_local_file", new()
            {
                ["localPath"] = local,
                ["remotePath"] = "/tmp/file",
                ["sessionId"] = session.SessionId
            });
            Assert.Contains("Unknown", first);
            Assert.IsTrue(run.HasUnknownOutcome);
            Assert.HasCount(2, context.FakeRemoteFs.UploadsStarted, "回执网络丢失只用相同OperationId重取一次；重复调用复用在途/失败账本");
            Assert.AreEqual(context.FakeRemoteFs.UploadsStarted[0].Options.OperationId,
                context.FakeRemoteFs.UploadsStarted[1].Options.OperationId);
            Assert.AreEqual(first, second);
        }
        finally { AgentToolbox.InvocationIdentity.Value = null; File.Delete(local); }
    }

    [TestMethod]
    public async Task McpUpload_ReturnsQuickly_AndActiveTransferSurvivesCallTimeout()
    {
        using var context = new TestPluginContext();
        var session = context.FakeSessions.AddConnected();
        var finish = new TaskCompletionSource<RemoteTransferSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        RemoteTransferSnapshot? snapshot = null;
        context.FakeRemoteFs.SupportsTransferOperations = true;
        context.FakeRemoteFs.StartUploadHandler = (id, source, target, options, _) =>
            Task.FromResult(snapshot = Running(id, source, target, options.OperationId));
        context.FakeRemoteFs.WaitTransferHandler = (_, _, _, ct) => finish.Task.WaitAsync(ct);
        context.FakeRemoteFs.CancelTransferHandler = (_, _, _) => Task.FromResult(snapshot! with
        { State = RemoteTransferState.Cancelled, Sequence = 2, RemoteStopConfirmed = true });
        await using var host = new McpToolHost(context, new McpServerSettings { Mode = ChatMode.Agent, Approval = ApprovalMode.Bypass });
        string local = Source(64L * 1024 * 1024);
        try
        {
            using var request = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            string result = await host.CallAsync("upload_local_file", new Dictionary<string, object?>
            { ["localPath"] = local, ["remotePath"] = "/tmp/file", ["sessionId"] = session.SessionId }, request.Token);
            Assert.Contains("transfer_id", result);
            await request.CancelAsync();
            Assert.IsTrue(host.HasActiveTransfers);
            Assert.IsEmpty(context.FakeRemoteFs.TransfersCancelled);
            finish.TrySetResult(snapshot! with { State = RemoteTransferState.Succeeded, Sequence = 2 });
        }
        finally { File.Delete(local); }
    }
}
