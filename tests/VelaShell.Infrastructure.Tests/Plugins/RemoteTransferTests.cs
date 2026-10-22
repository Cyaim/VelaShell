using System.Diagnostics;
using System.IO.Pipes;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NSubstitute;
using VelaShell.Core.Ftp;
using VelaShell.Core.Models;
using VelaShell.Core.Protocols;
using VelaShell.Core.Sftp;
using VelaShell.Core.Ssh;
using VelaShell.Infrastructure.Plugins;
using VelaShell.Infrastructure.Plugins.Capabilities;
using VelaShell.Infrastructure.Plugins.Isolated;
using VelaShell.Infrastructure.Sftp;
using VelaShell.PluginHost;
using VelaShell.PluginSdk;
using VelaShell.PluginSdk.RemoteFs;
using VelaShell.PluginSdk.Rpc;
using VelaShell.PluginSdk.Testing;
using RemoteUploadOptions = VelaShell.PluginSdk.RemoteFs.RemoteUploadOptions;

namespace VelaShell.Infrastructure.Tests.Plugins;

[TestClass]
public sealed class RemoteTransferTests
{
    private readonly Guid _session = Guid.NewGuid();
    private readonly ISftpService _sftp = Substitute.For<ISftpService>();
    private readonly ISshConnectionService _connections = Substitute.For<ISshConnectionService>();

    public RemoteTransferTests() => _connections.GetSession(_session).Returns(new SshSession
    {
        SessionId = _session,
        Status = SessionStatus.Connected,
        ConnectionInfo = new() { Host = "test", Username = "user", AuthMethod = AuthMethod.Password },
    });

    [TestMethod]
    public async Task ActualManagerContextRequiresUploadGateAndPassesBoundPluginIdentity()
    {
        string file = CreateFile(8);
        string root = Path.Combine(Path.GetTempPath(), "vela-manager-upload-" + Guid.NewGuid().ToString("N"));
        string digest = Convert.ToHexString(SHA256.HashData(new byte[8]));
        _sftp.ComputeSha256Async(_session, Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult<IReadOnlyDictionary<string, string?>>(new Dictionary<string, string?> { [call.ArgAt<IReadOnlyList<string>>(1)[0]] = digest }));
        try
        {
            foreach (bool approved in new[] { false, true })
            {
                IPluginPermissionPrompt prompt = Substitute.For<IPluginPermissionPrompt>();
                prompt.RequestFileUploadAsync("test.manager", Arg.Any<string>(), RemoteFsCapability.ResolveUploadSource(file), "/target", false, Arg.Any<CancellationToken>())
                    .Returns(PluginPermissionDecision.AllowOnce);
                await using var manager = new PluginManager(new()
                {
                    PluginRoots = [],
                    DataRootDirectory = root,
                    Connections = _connections,
                    // 与宿主 DI 一致:插件拿到的是路由服务,不是 SFTP 后端本身(#656 F1:路由漏转提交,上传全部卡在提交)。
                    Sftp = Routed(_sftp),
                    UploadPermissions = approved ? new(null, prompt) : null,
                });
                Type runtimeType = typeof(PluginManager).GetNestedType("PluginRuntime", BindingFlags.NonPublic)!;
                object runtime = Activator.CreateInstance(runtimeType, nonPublic: true)!;
                runtimeType.GetProperty("Descriptor")!.SetValue(runtime, new PluginDescriptor { Directory = root });
                PluginManifest manifest = JsonSerializer.Deserialize<PluginManifest>("""{"id":"test.manager","version":"1","displayName":"Test","entry":"Test.dll"}""")!;
                using var context = (PluginContext)typeof(PluginManager).GetMethod("CreateContext", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(manager, [manifest, runtime])!;
                var api = (RemoteFsCapability)context.RemoteFs;
                var start = await api.StartUploadAsync(_session.ToString(), file, "/target", new() { OperationId = "manager" });
                var done = await Terminal(api, start.TransferId);
                Assert.AreEqual(approved ? RemoteTransferState.Succeeded : RemoteTransferState.Failed, done.State, done.ErrorMessage);
                Assert.IsTrue(done.RemoteStopConfirmed);
                if (!approved) Assert.AreEqual("permission-denied", done.ErrorCode);
                else await prompt.Received(1).RequestFileUploadAsync("test.manager", Arg.Any<string>(), RemoteFsCapability.ResolveUploadSource(file), "/target", false, Arg.Any<CancellationToken>());
                await WaitIdle(api);
            }
            await _sftp.Received(1).CommitUploadAsync(_session, Arg.Any<string>(), "/target", false, Arg.Any<CancellationToken>());
        }
        finally { File.Delete(file); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task MissingTrustedUploadGateFailsClosedWithoutReadingOrUploading()
    {
        string file = CreateFile(8);
        using var api = new RemoteFsCapability(_sftp, _connections, pluginId: "test.transfer");
        try
        {
            var start = await api.StartUploadAsync(_session.ToString(), file, "/target", new() { OperationId = "no-gate" });
            var done = await Terminal(api, start.TransferId);
            Assert.AreEqual(RemoteTransferState.Failed, done.State);
            Assert.AreEqual("permission-denied", done.ErrorCode);
            Assert.IsTrue(done.RemoteStopConfirmed);
            Assert.IsTrue(done.DestinationCommitConfirmedAbsent);
            await _sftp.DidNotReceive().DeleteAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<IProgress<SftpDeleteProgress>>(), Arg.Any<CancellationToken>());
            await _sftp.DidNotReceive().UploadStreamAsync(Arg.Any<Guid>(), Arg.Any<Stream>(), Arg.Any<string>(), Arg.Any<long>(), Arg.Any<DateTime?>(), Arg.Any<IProgress<TransferProgress>>(), Arg.Any<long>(), Arg.Any<CancellationToken>());
            await _sftp.DidNotReceive().CommitUploadAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
            await WaitIdle(api);
        }
        finally { File.Delete(file); }
    }

    [TestMethod]
    public async Task TrustedPromptIsBackgroundAndBindsStableSourceAndExactParameters()
    {
        string file = CreateFile(8);
        var asking = NewSignal();
        var decision = new TaskCompletionSource<PluginPermissionDecision>(TaskCreationOptions.RunContinuationsAsynchronously);
        IPluginPermissionPrompt prompt = Substitute.For<IPluginPermissionPrompt>();
        prompt.RequestFileUploadAsync("test.transfer", Arg.Any<string>(), RemoteFsCapability.ResolveUploadSource(file), "/target", true, Arg.Any<CancellationToken>())
            .Returns(async _ => { asking.TrySetResult(); return await decision.Task; });
        using var api = new RemoteFsCapability(_sftp, _connections, pluginId: "test.transfer", uploadPermissions: new(null, prompt));
        try
        {
            var start = await api.StartUploadAsync(_session.ToString(), file, "/target", new() { OperationId = "approval", Overwrite = true });
            await asking.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.IsFalse(start.IsTerminal, "Start短回执不等待可信审批。");
            Assert.AreEqual(RemoteFsCapability.ResolveUploadSource(file), start.LocalPath);
            if (OperatingSystem.IsWindows())
                Assert.ThrowsExactly<IOException>(() => { using var write = new FileStream(file, FileMode.Open, FileAccess.Write); });
            await _sftp.DidNotReceive().UploadStreamAsync(Arg.Any<Guid>(), Arg.Any<Stream>(), Arg.Any<string>(), Arg.Any<long>(), Arg.Any<DateTime?>(), Arg.Any<IProgress<TransferProgress>>(), Arg.Any<long>(), Arg.Any<CancellationToken>());
            decision.TrySetResult(PluginPermissionDecision.Deny);
            Assert.AreEqual("permission-denied", (await Terminal(api, start.TransferId)).ErrorCode);
            await WaitIdle(api);
            await _sftp.DidNotReceive().DeleteAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<IProgress<SftpDeleteProgress>>(), Arg.Any<CancellationToken>());
        }
        finally { decision.TrySetResult(PluginPermissionDecision.Deny); File.Delete(file); }
    }

    [TestMethod]
    [OSCondition(ConditionMode.Exclude, OperatingSystems.Windows)]
    public async Task UnixSourceModifiedWhileApprovalPendingFailsBeforeRemoteWrite()
    {
        string file = CreateFile(8);
        var asking = NewSignal();
        var decision = new TaskCompletionSource<PluginPermissionDecision>(TaskCreationOptions.RunContinuationsAsynchronously);
        IPluginPermissionPrompt prompt = Substitute.For<IPluginPermissionPrompt>();
        prompt.RequestFileUploadAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(async _ => { asking.TrySetResult(); return await decision.Task; });
        using var api = new RemoteFsCapability(_sftp, _connections, pluginId: "test.transfer", uploadPermissions: new(null, prompt));
        try
        {
            var start = await api.StartUploadAsync(_session.ToString(), file, "/target", new() { OperationId = "changed-before-approval" });
            await asking.Task.WaitAsync(TimeSpan.FromSeconds(5));
            using (var write = new FileStream(file, FileMode.Open, FileAccess.Write, FileShare.ReadWrite)) write.SetLength(16);
            decision.SetResult(PluginPermissionDecision.AllowOnce);
            RemoteTransferSnapshot done = await Terminal(api, start.TransferId);
            Assert.AreEqual(RemoteTransferState.Failed, done.State);
            Assert.AreEqual("source-changed", done.ErrorCode);
            await WaitIdle(api);
            await _sftp.DidNotReceive().UploadStreamAsync(Arg.Any<Guid>(), Arg.Any<Stream>(), Arg.Any<string>(), Arg.Any<long>(), Arg.Any<DateTime?>(), Arg.Any<IProgress<TransferProgress>>(), Arg.Any<long>(), Arg.Any<CancellationToken>());
            await _sftp.DidNotReceive().CommitUploadAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
            await _sftp.DidNotReceive().DeleteAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<IProgress<SftpDeleteProgress>>(), Arg.Any<CancellationToken>());
        }
        finally { decision.TrySetResult(PluginPermissionDecision.Deny); await WaitIdle(api); File.Delete(file); }
    }

    [TestMethod]
    [OSCondition(ConditionMode.Exclude, OperatingSystems.Windows)]
    public async Task ParentAliasesBindCanonicalSourceAndReplayDoesNotReopenRetargetedAlias()
    {
        string root = Path.Combine(Path.GetTempPath(), "vela-transfer-links-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "first"));
        Directory.CreateDirectory(Path.Combine(root, "second"));
        string first = Path.Combine(root, "first", "source");
        await File.WriteAllBytesAsync(first, [1, 2, 3]);
        await File.WriteAllBytesAsync(Path.Combine(root, "second", "source"), [9, 9, 9]);
        string alias = Path.Combine(root, "alias");
        string outer = Path.Combine(root, "outer");
        Directory.CreateSymbolicLink(alias, "first");
        Directory.CreateSymbolicLink(outer, alias);
        string requested = Path.Combine(outer, "source");
        string canonical = RemoteFsCapability.ResolveUploadSource(first);
        var asking = NewSignal();
        var decision = new TaskCompletionSource<PluginPermissionDecision>(TaskCreationOptions.RunContinuationsAsynchronously);
        IPluginPermissionPrompt prompt = Substitute.For<IPluginPermissionPrompt>();
        prompt.RequestFileUploadAsync("test.transfer", Arg.Any<string>(), canonical, "/target", false, Arg.Any<CancellationToken>())
            .Returns(async _ => { asking.TrySetResult(); return await decision.Task; });
        string digest = Convert.ToHexString(SHA256.HashData(new byte[] { 1, 2, 3 }));
        _sftp.UploadStreamAsync(_session, Arg.Any<Stream>(), Arg.Any<string>(), 3, Arg.Any<DateTime?>(), Arg.Any<IProgress<TransferProgress>>(), 0, Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                using var bytes = new MemoryStream();
                await call.ArgAt<Stream>(1).CopyToAsync(bytes);
                CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, bytes.ToArray());
            });
        _sftp.ComputeSha256Async(_session, Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult<IReadOnlyDictionary<string, string?>>(new Dictionary<string, string?> { [call.ArgAt<IReadOnlyList<string>>(1)[0]] = digest }));
        using var api = new RemoteFsCapability(_sftp, _connections, pluginId: "test.transfer", uploadPermissions: new(null, prompt));
        var options = new RemoteUploadOptions { OperationId = "alias" };
        try
        {
            var start = await api.StartUploadAsync(_session.ToString(), requested, "/target", options);
            await asking.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.AreEqual(canonical, start.LocalPath);
            Directory.Delete(alias);
            Directory.CreateSymbolicLink(alias, "second");
            Assert.AreEqual(start.TransferId, (await api.StartUploadAsync(_session.ToString(), requested, "/target", options)).TransferId);
            decision.SetResult(PluginPermissionDecision.AllowOnce);
            Assert.IsTrue((await Terminal(api, start.TransferId)).IsSuccess);
            await WaitIdle(api);
            File.Delete(Path.Combine(root, "second", "source"));
            Assert.AreEqual(start.TransferId, (await api.StartUploadAsync(_session.ToString(), requested, "/target", options)).TransferId);
            await prompt.Received(1).RequestFileUploadAsync("test.transfer", Arg.Any<string>(), canonical, "/target", false, Arg.Any<CancellationToken>());
        }
        finally
        {
            decision.TrySetResult(PluginPermissionDecision.Deny);
            await WaitIdle(api);
            Directory.Delete(outer);
            Directory.Delete(alias);
            Directory.Delete(root, true);
        }
    }

    [TestMethod]
    [OSCondition(ConditionMode.Exclude, OperatingSystems.Windows)]
    public void ParentLinkDotDotIsAppliedAfterIntermediateLinkExpansion()
    {
        string root = Path.Combine(Path.GetTempPath(), "vela-transfer-dotdot-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "real", "sub"));
        Directory.CreateDirectory(Path.Combine(root, "real", "destination"));
        Directory.CreateDirectory(Path.Combine(root, "destination"));
        string jump = Path.Combine(root, "jump");
        string alias = Path.Combine(root, "alias");
        Directory.CreateSymbolicLink(jump, "real/sub");
        Directory.CreateSymbolicLink(alias, "jump/../destination");
        try
        {
            Assert.AreEqual(RemoteFsCapability.ResolveUploadSource(Path.Combine(root, "real", "destination", "source")),
                RemoteFsCapability.ResolveUploadSource(Path.Combine(alias, "source")));
        }
        finally { Directory.Delete(alias); Directory.Delete(jump); Directory.Delete(root, true); }
    }

    [TestMethod]
    [OSCondition(ConditionMode.Exclude, OperatingSystems.Windows)]
    public async Task LeafLinksAndUnresolvableParentsRemainRejected()
    {
        string root = Path.Combine(Path.GetTempPath(), "vela-transfer-reject-links-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string source = Path.Combine(root, "source");
        await File.WriteAllBytesAsync(source, [1]);
        string leaf = Path.Combine(root, "leaf");
        string missing = Path.Combine(root, "missing");
        string loop = Path.Combine(root, "loop");
        File.CreateSymbolicLink(leaf, source);
        Directory.CreateSymbolicLink(missing, "absent");
        Directory.CreateSymbolicLink(loop, "loop");
        using var api = new RemoteFsCapability(_sftp, _connections, pluginId: "test.transfer", uploadPermissions: AllowUploads());
        try
        {
            await Assert.ThrowsExactlyAsync<NotSupportedException>(() => api.StartUploadAsync(_session.ToString(), leaf, "/target", new() { OperationId = "leaf" }));
            await Assert.ThrowsAsync<IOException>(() => api.StartUploadAsync(_session.ToString(), Path.Combine(missing, "source"), "/target", new() { OperationId = "missing" }));
            await Assert.ThrowsAsync<IOException>(() => api.StartUploadAsync(_session.ToString(), Path.Combine(loop, "source"), "/target", new() { OperationId = "loop" }));
            Assert.AreEqual(0, api.ActiveTransfers);
        }
        finally { File.Delete(leaf); File.Delete(missing); File.Delete(loop); Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task RevokingPendingPromptNeverStartsAnyRemoteOperation()
    {
        string file = CreateFile(8);
        var asking = NewSignal();
        IPluginPermissionPrompt prompt = Substitute.For<IPluginPermissionPrompt>();
        prompt.RequestFileUploadAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(async call => { asking.TrySetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, call.ArgAt<CancellationToken>(5)); return PluginPermissionDecision.AllowOnce; });
        var gate = new PluginPermissionGate(null, prompt);
        using var api = new RemoteFsCapability(_sftp, _connections, pluginId: "test.transfer", uploadPermissions: gate);
        try
        {
            var start = await api.StartUploadAsync(_session.ToString(), file, "/target", new() { OperationId = "pending-revoke" });
            await asking.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await gate.RevokeAsync("test.transfer");
            await WaitIdle(api);
            await Assert.ThrowsExactlyAsync<PluginPermissionDeniedException>(() => api.GetTransferAsync(start.TransferId));
            Assert.AreEqual("", (await api.CancelTransferAsync(start.TransferId, 1)).LocalPath);
            await _sftp.DidNotReceive().UploadStreamAsync(Arg.Any<Guid>(), Arg.Any<Stream>(), Arg.Any<string>(), Arg.Any<long>(), Arg.Any<DateTime?>(), Arg.Any<IProgress<TransferProgress>>(), Arg.Any<long>(), Arg.Any<CancellationToken>());
            await _sftp.DidNotReceive().DeleteAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<IProgress<SftpDeleteProgress>>(), Arg.Any<CancellationToken>());
            await _sftp.DidNotReceive().CommitUploadAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
        }
        finally { File.Delete(file); }
    }

    [TestMethod]
    public async Task RevokeStopsActiveUploadHidesQueriesButAllowsRedactedCancelCleanup()
    {
        string file = CreateFile(8);
        var started = NewSignal();
        var release = NewSignal();
        PluginPermissionGate gate = AllowUploads();
        _sftp.UploadStreamAsync(_session, Arg.Any<Stream>(), Arg.Any<string>(), Arg.Any<long>(), Arg.Any<DateTime?>(),
            Arg.Any<IProgress<TransferProgress>>(), 0, Arg.Any<CancellationToken>()).Returns(async _ => { started.TrySetResult(); await release.Task; });
        using var api = new RemoteFsCapability(_sftp, _connections, pluginId: "test.transfer", uploadPermissions: gate);
        try
        {
            var start = await api.StartUploadAsync(_session.ToString(), file, "/target", new() { OperationId = "revoke" });
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.IsTrue(await gate.HasGrantAsync("test.transfer"));
            Task revocation = gate.RevokeAsync("test.transfer");
            await Assert.ThrowsExactlyAsync<PluginPermissionDeniedException>(() => api.GetTransferAsync(start.TransferId));
            await Assert.ThrowsExactlyAsync<PluginPermissionDeniedException>(() => api.WaitTransferAsync(start.TransferId, 0, TimeSpan.Zero));
            await Assert.ThrowsExactlyAsync<PluginPermissionDeniedException>(() => api.StartUploadAsync(_session.ToString(), file, "/target", new() { OperationId = "revoke" }));
            var cancel = await api.CancelTransferAsync(start.TransferId, 1);
            Assert.AreEqual("", cancel.LocalPath);
            Assert.AreEqual("", cancel.RemotePath);
            Assert.IsFalse(cancel.RemoteStopConfirmed);
            await revocation;
            Assert.IsFalse(await gate.HasGrantAsync("test.transfer"));
            await _sftp.DidNotReceive().CommitUploadAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
        }
        finally { release.TrySetResult(); await WaitIdle(api); File.Delete(file); }
    }

    [TestMethod]
    public async Task LargeUpload_RequestCancellationAndWaitTimeoutDoNotStopTask_CommitIsAuthoritative()
    {
        string file = CreateFile(33L * 1024 * 1024 + 17);
        var writing = NewSignal();
        var close = NewSignal();
        var committing = NewSignal();
        var commit = NewSignal();
        string? hash = null;
        _sftp.UploadStreamAsync(_session, Arg.Any<Stream>(), Arg.Any<string>(), Arg.Any<long>(),
            Arg.Any<DateTime?>(), Arg.Any<IProgress<TransferProgress>>(), 0, Arg.Any<CancellationToken>()).Returns(async call =>
        {
            Stream source = call.ArgAt<Stream>(1);
            Assert.IsNotInstanceOfType<MemoryStream>(source);
            hash = Convert.ToHexString(await SHA256.HashDataAsync(source, call.ArgAt<CancellationToken>(7)));
            call.ArgAt<IProgress<TransferProgress>>(5).Report(new() { BytesTransferred = source.Length, TotalBytes = source.Length, FileName = "file", SpeedBytesPerSecond = 0, Percentage = 100, EstimatedTimeRemaining = TimeSpan.Zero });
            writing.TrySetResult();
            await close.Task;
        });
        _sftp.ComputeSha256Async(_session, Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult<IReadOnlyDictionary<string, string?>>(new Dictionary<string, string?> { [call.ArgAt<IReadOnlyList<string>>(1)[0]] = hash }));
        _sftp.CommitUploadAsync(_session, Arg.Any<string>(), "/target", false, Arg.Any<CancellationToken>())
            .Returns(async _ => { committing.TrySetResult(); await commit.Task; });
        using var api = new RemoteFsCapability(_sftp, _connections, pluginId: "test.transfer", uploadPermissions: AllowUploads());
        try
        {
            using var request = new CancellationTokenSource();
            RemoteTransferSnapshot start = await api.StartUploadAsync(_session.ToString(), file, "/target", new() { OperationId = "large" }, request.Token);
            request.Cancel();
            await writing.Task.WaitAsync(TimeSpan.FromSeconds(10));
            RemoteTransferSnapshot running = await api.GetTransferAsync(start.TransferId);
            Assert.IsFalse(running.IsTerminal);
            RemoteTransferSnapshot waited = await api.WaitTransferAsync(start.TransferId, running.Sequence, TimeSpan.FromMilliseconds(10));
            Assert.IsFalse(waited.IsTerminal);
            Assert.IsFalse(waited.CancellationRequested);
            close.TrySetResult();
            await committing.Task.WaitAsync(TimeSpan.FromSeconds(10));
            RemoteTransferSnapshot tail = await api.GetTransferAsync(start.TransferId);
            Assert.AreEqual(tail.TotalBytes, tail.TransferredBytes);
            Assert.IsFalse(tail.IsSuccess);
            Assert.IsNull(tail.EstimatedTimeRemaining);
            commit.TrySetResult();
            RemoteTransferSnapshot done = await Terminal(api, start.TransferId);
            Assert.IsTrue(done.IsSuccess);
            Assert.AreEqual(33L * 1024 * 1024 + 17, done.TotalBytes);
            Assert.IsTrue(done.RemoteStopConfirmed);
            Assert.IsFalse(done.CanResume);
            await WaitIdle(api);
        }
        finally
        {
            close.TrySetResult();
            commit.TrySetResult();
            api.Dispose();
            try { await WaitIdle(api); }
            finally { File.Delete(file); }
        }
    }

    [TestMethod]
    public void ProgressUses64BitBytesAndDoesNotTurnFullByteCountIntoSuccess()
    {
        long total = (long)int.MaxValue + 1024 * 1024;
        RemoteTransferSnapshot snapshot = new() { TransferId = "t", OperationId = "o", SessionId = "s", LocalPath = "l", RemotePath = "/r", TotalBytes = total, State = RemoteTransferState.Running };
        var updated = RemoteFsCapability.ApplyTransferProgress(snapshot, new()
        {
            FileName = "large",
            BytesTransferred = total,
            TotalBytes = total,
            Percentage = 100,
            SpeedBytesPerSecond = 1024,
            EstimatedTimeRemaining = TimeSpan.Zero,
        });
        Assert.AreEqual(total, updated.TransferredBytes);
        Assert.IsFalse(updated.IsSuccess);
    }

    [TestMethod]
    public async Task EmptyUploadSucceedsWithZeroBytesOnlyAfterCommit()
    {
        string file = CreateFile(0);
        string emptyHash = Convert.ToHexString(SHA256.HashData([]));
        _sftp.ComputeSha256Async(_session, Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult<IReadOnlyDictionary<string, string?>>(new Dictionary<string, string?> { [call.ArgAt<IReadOnlyList<string>>(1)[0]] = emptyHash }));
        using var api = new RemoteFsCapability(_sftp, _connections, pluginId: "test.transfer", uploadPermissions: AllowUploads());
        try
        {
            var start = await api.StartUploadAsync(_session.ToString(), file, "/empty", new() { OperationId = "empty" });
            var done = await Terminal(api, start.TransferId);
            Assert.IsTrue(done.IsSuccess);
            Assert.AreEqual(0L, done.TotalBytes);
            Assert.AreEqual(0L, done.TransferredBytes);
            await _sftp.Received(1).CommitUploadAsync(_session, Arg.Any<string>(), "/empty", false, Arg.Any<CancellationToken>());
            await WaitIdle(api);
        }
        finally { File.Delete(file); }
    }

    [TestMethod]
    public async Task HashMismatchFailsWithoutCommittingDestination()
    {
        string file = CreateFile(64);
        _sftp.ComputeSha256Async(_session, Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult<IReadOnlyDictionary<string, string?>>(new Dictionary<string, string?> { [call.ArgAt<IReadOnlyList<string>>(1)[0]] = new string('0', 64) }));
        using var api = new RemoteFsCapability(_sftp, _connections, pluginId: "test.transfer", uploadPermissions: AllowUploads());
        try
        {
            var start = await api.StartUploadAsync(_session.ToString(), file, "/target", new() { OperationId = "hash" });
            var done = await Terminal(api, start.TransferId);
            Assert.AreEqual(RemoteTransferState.Failed, done.State);
            Assert.IsTrue(done.RemoteStopConfirmed);
            Assert.IsTrue(done.DestinationCommitConfirmedAbsent);
            Assert.AreEqual("upload-failed", done.ErrorCode);
            Assert.AreEqual("Uploaded content verification failed.", done.ErrorMessage);
            await _sftp.DidNotReceive().CommitUploadAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
            await WaitIdle(api);
            await _sftp.Received(1).DeleteAsync(_session, Arg.Is<string>(path => path.StartsWith("/target.vela-upload-", StringComparison.Ordinal)),
                Arg.Any<IProgress<SftpDeleteProgress>>(), Arg.Any<CancellationToken>());
        }
        finally { File.Delete(file); }
    }

    [TestMethod]
    public async Task DiskSnapshotBranchUsesSeparateStreamAndCommitsVerifiedBytes()
    {
        string file = CreateFile(1024 * 1024 + 7);
        string? hash = null;
        _sftp.UploadStreamAsync(_session, Arg.Any<Stream>(), Arg.Any<string>(), Arg.Any<long>(), Arg.Any<DateTime?>(),
            Arg.Any<IProgress<TransferProgress>>(), 0, Arg.Any<CancellationToken>()).Returns(async call =>
        {
            var source = (FileStream)call.ArgAt<Stream>(1);
            Assert.AreNotEqual(file, source.Name);
            Assert.IsTrue(source.Name.Contains("vela-upload-", StringComparison.Ordinal));
            hash = Convert.ToHexString(await SHA256.HashDataAsync(source));
        });
        _sftp.ComputeSha256Async(_session, Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult<IReadOnlyDictionary<string, string?>>(new Dictionary<string, string?> { [call.ArgAt<IReadOnlyList<string>>(1)[0]] = hash }));
        using var api = new RemoteFsCapability(_sftp, _connections, pluginId: "test.transfer", uploadPermissions: AllowUploads()) { UseDiskSnapshot = true };
        try
        {
            var start = await api.StartUploadAsync(_session.ToString(), file, "/target", new() { OperationId = "snapshot" });
            RemoteTransferSnapshot done = await Terminal(api, start.TransferId);
            Assert.IsTrue(done.IsSuccess, $"{done.State}: {done.ErrorMessage}");
            await WaitIdle(api);
        }
        finally { File.Delete(file); }
    }

    [TestMethod]
    public async Task IdempotencyConflictsAndPluginOwnershipAreEnforced()
    {
        string file = CreateFile(8);
        var started = NewSignal();
        var release = NewSignal();
        _sftp.UploadStreamAsync(_session, Arg.Any<Stream>(), Arg.Any<string>(), Arg.Any<long>(), Arg.Any<DateTime?>(),
            Arg.Any<IProgress<TransferProgress>>(), 0, Arg.Any<CancellationToken>()).Returns(async _ => { started.TrySetResult(); await release.Task; throw new IOException(); });
        try
        {
            using var api = new RemoteFsCapability(_sftp, _connections, pluginId: "test.transfer", uploadPermissions: AllowUploads());
            using var other = new RemoteFsCapability(_sftp, _connections, pluginId: "test.transfer", uploadPermissions: AllowUploads());
            var options = new RemoteUploadOptions { OperationId = "same" };
            RemoteTransferSnapshot first = await api.StartUploadAsync(_session.ToString(), file, "/one", options);
            RemoteTransferSnapshot second = await api.StartUploadAsync(_session.ToString(), file, "/one", options);
            Assert.AreEqual(first.TransferId, second.TransferId);
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => api.StartUploadAsync(_session.ToString(), file, "/two", options));
            await Assert.ThrowsExactlyAsync<FileNotFoundException>(() => other.GetTransferAsync(first.TransferId));
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => api.CancelTransferAsync(first.TransferId, 2));
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            _connections.GetSession(_session).Returns((SshSession?)null);
            Assert.AreEqual(first.TransferId, (await api.StartUploadAsync(_session.ToString(), file, "/one", options)).TransferId,
                "断线后仍能用相同幂等标识重取原回执。");
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => api.StartUploadAsync(_session.ToString(), file, "/changed", options));
            release.TrySetResult();
            RemoteTransferSnapshot failed = await Terminal(api, first.TransferId);
            Assert.AreEqual(RemoteTransferState.Failed, failed.State, failed.ErrorMessage);
            Assert.IsTrue(failed.RemoteStopConfirmed);
            Assert.IsTrue(failed.DestinationCommitConfirmedAbsent);
            await Assert.ThrowsExactlyAsync<NotSupportedException>(() => api.ResumeTransferAsync(first.TransferId,
                new() { OperationId = "resume", ExpectedGeneration = 1, TargetSessionId = _session.ToString() }));
            await WaitIdle(api);
        }
        finally { release.TrySetResult(); File.Delete(file); }
    }

    [TestMethod]
    public async Task QueueQuotaRejectsNewWorkButKeepsExistingReceipt()
    {
        string file = CreateFile(8);
        var release = NewSignal();
        _sftp.UploadStreamAsync(_session, Arg.Any<Stream>(), Arg.Any<string>(), Arg.Any<long>(), Arg.Any<DateTime?>(),
            Arg.Any<IProgress<TransferProgress>>(), 0, Arg.Any<CancellationToken>()).Returns(async _ => await release.Task);
        using var api = new RemoteFsCapability(_sftp, _connections, pluginId: "test.transfer", uploadPermissions: AllowUploads());
        var starts = new List<RemoteTransferSnapshot>();
        try
        {
            for (int i = 0; i < 32; i++) starts.Add(await api.StartUploadAsync(_session.ToString(), file, "/target-" + i, new() { OperationId = "quota-" + i }));
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => api.StartUploadAsync(_session.ToString(), file, "/extra", new() { OperationId = "extra" }));
            Assert.AreEqual(starts[0].TransferId, (await api.StartUploadAsync(_session.ToString(), file, "/target-0", new() { OperationId = "quota-0" })).TransferId);
        }
        finally
        {
            api.Dispose(); release.TrySetResult(); await WaitIdle(api); File.Delete(file);
        }
    }

    [TestMethod]
    public async Task SessionDisconnectKeepsTaskQueryableAndDoesNotPretendRemoteStopped()
    {
        string file = CreateFile(8);
        var release = NewSignal();
        var started = NewSignal();
        _sftp.UploadStreamAsync(_session, Arg.Any<Stream>(), Arg.Any<string>(), Arg.Any<long>(), Arg.Any<DateTime?>(),
            Arg.Any<IProgress<TransferProgress>>(), 0, Arg.Any<CancellationToken>()).Returns(async _ => { started.TrySetResult(); await release.Task; });
        using var api = new RemoteFsCapability(_sftp, _connections, pluginId: "test.transfer", uploadPermissions: AllowUploads()) { StopGracePeriod = UncooperativeGrace };
        try
        {
            var start = await api.StartUploadAsync(_session.ToString(), file, "/target", new() { OperationId = "disconnect" });
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            SshSession disconnected = _connections.GetSession(_session)!;
            _connections.GetSession(_session).Returns((SshSession?)null);
            _connections.SessionDisconnected += Raise.Event<Action<SshSession>>(disconnected);
            var done = await Terminal(api, start.TransferId);
            Assert.AreEqual(RemoteTransferState.Unknown, done.State);
            Assert.IsFalse(done.RemoteStopConfirmed);
        }
        finally { release.TrySetResult(); await WaitIdle(api); File.Delete(file); }
    }

    [TestMethod]
    public async Task CancellationIsNotRemoteStopConfirmation_AndOldGenerationCannotCancel()
    {
        string file = CreateFile(64);
        var started = NewSignal();
        var release = NewSignal();
        _sftp.UploadStreamAsync(_session, Arg.Any<Stream>(), Arg.Any<string>(), Arg.Any<long>(), Arg.Any<DateTime?>(),
            Arg.Any<IProgress<TransferProgress>>(), 0, Arg.Any<CancellationToken>()).Returns(async _ => { started.TrySetResult(); await release.Task; });
        using var api = new RemoteFsCapability(_sftp, _connections, pluginId: "test.transfer", uploadPermissions: AllowUploads()) { StopGracePeriod = UncooperativeGrace };
        try
        {
            RemoteTransferSnapshot start = await api.StartUploadAsync(_session.ToString(), file, "/target", new() { OperationId = "cancel" });
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            RemoteTransferSnapshot cancelling = await api.CancelTransferAsync(start.TransferId, 1);
            Assert.IsTrue(cancelling.CancellationRequested);
            Assert.IsFalse(cancelling.RemoteStopConfirmed);
            RemoteTransferSnapshot done = await Terminal(api, start.TransferId);
            Assert.AreEqual(RemoteTransferState.Unknown, done.State);
            Assert.IsFalse(done.RemoteStopConfirmed);
            Assert.IsFalse(done.CanResume);
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => api.CancelTransferAsync(start.TransferId, 2));
            Assert.IsTrue((await api.CancelTransferAsync(start.TransferId, 1)).CancellationRequested);
        }
        finally { release.TrySetResult(); await WaitIdle(api); File.Delete(file); }
    }

    [TestMethod]
    public async Task CloseFailureCannotSucceedOrCommit()
    {
        string file = CreateFile(64);
        string? temporary = null;
        _sftp.UploadStreamAsync(_session, Arg.Any<Stream>(), Arg.Any<string>(), Arg.Any<long>(), Arg.Any<DateTime?>(),
            Arg.Any<IProgress<TransferProgress>>(), 0, Arg.Any<CancellationToken>()).Returns(call =>
            {
                temporary = call.ArgAt<string>(2);
                return Task.FromException(new IOException("close"));
            });
        using var api = new RemoteFsCapability(_sftp, _connections, pluginId: "test.transfer", uploadPermissions: AllowUploads());
        try
        {
            RemoteTransferSnapshot start = await api.StartUploadAsync(_session.ToString(), file, "/target", new() { OperationId = "close" });
            RemoteTransferSnapshot done = await Terminal(api, start.TransferId);
            // 写入任务已经以失败结束 —— 后端已经关了句柄,这就是确认停写:Failed,不是 Unknown。
            Assert.AreEqual(RemoteTransferState.Failed, done.State, done.ErrorMessage);
            Assert.IsTrue(done.RemoteStopConfirmed);
            Assert.IsTrue(done.DestinationCommitConfirmedAbsent);
            Assert.AreEqual("upload-failed", done.ErrorCode);
            await WaitIdle(api);
            await _sftp.Received(1).DeleteAsync(_session, temporary!, Arg.Any<IProgress<SftpDeleteProgress>>(), Arg.Any<CancellationToken>());
            await _sftp.DidNotReceive().CommitUploadAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
        }
        finally { await WaitIdle(api); File.Delete(file); }
    }

    [TestMethod]
    public async Task RemoteWriteFailureKeepsSanitizedCauseLogsItAndCleansTemporary()
    {
        string file = CreateFile(64);
        string? temporary = null;
        _sftp.UploadStreamAsync(_session, Arg.Any<Stream>(), Arg.Any<string>(), Arg.Any<long>(), Arg.Any<DateTime?>(),
            Arg.Any<IProgress<TransferProgress>>(), 0, Arg.Any<CancellationToken>()).Returns(call =>
            {
                temporary = call.ArgAt<string>(2);
                // 服务端原话不可信:里面的终端转义序列不能原样到达插件与模型。
                return Task.FromException(new VelaSftpPermissionDeniedException("Permission denied: quota-7f3a \u001b]52;c;cGF5bG9hZA==\u0007"));
            });
        using var trace = new TraceCapture();
        using var api = new RemoteFsCapability(_sftp, _connections, pluginId: "test.transfer", uploadPermissions: AllowUploads());
        try
        {
            RemoteTransferSnapshot start = await api.StartUploadAsync(_session.ToString(), file, "/target", new() { OperationId = "denied-write" });
            RemoteTransferSnapshot done = await Terminal(api, start.TransferId);
            Assert.AreEqual(RemoteTransferState.Failed, done.State, done.ErrorMessage);
            Assert.IsTrue(done.RemoteStopConfirmed);
            Assert.IsTrue(done.DestinationCommitConfirmedAbsent);
            Assert.AreEqual("upload-failed", done.ErrorCode);
            Assert.Contains("quota-7f3a", done.ErrorMessage!, StringComparison.Ordinal);
            Assert.DoesNotContain("\u001b", done.ErrorMessage!, StringComparison.Ordinal);
            await WaitIdle(api);
            await _sftp.Received(1).DeleteAsync(_session, temporary!, Arg.Any<IProgress<SftpDeleteProgress>>(), Arg.Any<CancellationToken>());
            await _sftp.DidNotReceive().CommitUploadAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
            Assert.Contains(start.TransferId, trace.Text, StringComparison.Ordinal);
            Assert.Contains(nameof(VelaSftpPermissionDeniedException), trace.Text, StringComparison.Ordinal);
            Assert.Contains("quota-7f3a", trace.Text, StringComparison.Ordinal);
        }
        finally { await WaitIdle(api); File.Delete(file); }
    }

    [TestMethod]
    [DataRow("status", false)]
    [DataRow("unsupported", true)]
    [DataRow("interrupted", false)]
    public async Task CommitIsUnknownOnlyWithoutAReply(string failure, bool overwrite)
    {
        string file = CreateFile(8);
        string? hash = null, temporary = null;
        _sftp.UploadStreamAsync(_session, Arg.Any<Stream>(), Arg.Any<string>(), Arg.Any<long>(), Arg.Any<DateTime?>(),
            Arg.Any<IProgress<TransferProgress>>(), 0, Arg.Any<CancellationToken>()).Returns(async call =>
            {
                temporary = call.ArgAt<string>(2);
                hash = Convert.ToHexString(await SHA256.HashDataAsync(call.ArgAt<Stream>(1)));
            });
        _sftp.ComputeSha256Async(_session, Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult<IReadOnlyDictionary<string, string?>>(new Dictionary<string, string?> { [call.ArgAt<IReadOnlyList<string>>(1)[0]] = hash }));
        if (failure == "unsupported")
        {
            // 要覆盖而服务端没有 posix-rename:请求根本没发出去就同步拒绝(与接口默认实现同样的抛法)。
            _sftp.CommitUploadAsync(_session, Arg.Any<string>(), "/target", overwrite, Arg.Any<CancellationToken>())
                .Returns(_ => throw new NotSupportedException("The server does not support atomic POSIX replacement."));
        }
        else
        {
            // 服务端的失败状态应答(普通 RENAME 因目标已存在被拒)是明确拒绝;通道中途断开则等不到应答。
            Exception error = failure == "status"
                ? new VelaSftpOperationException("Failure: /target (server: destination exists)")
                : new VelaSftpTransferInterruptedException("SFTP channel closed with requests in flight.", 0);
            _sftp.CommitUploadAsync(_session, Arg.Any<string>(), "/target", overwrite, Arg.Any<CancellationToken>())
                .Returns(Task.FromException(error));
        }
        using var api = new RemoteFsCapability(_sftp, _connections, pluginId: "test.transfer", uploadPermissions: AllowUploads());
        try
        {
            var start = await api.StartUploadAsync(_session.ToString(), file, "/target", new() { OperationId = "commit-" + failure, Overwrite = overwrite });
            RemoteTransferSnapshot done = await Terminal(api, start.TransferId);
            await WaitIdle(api);
            bool definite = failure != "interrupted";
            Assert.AreEqual(definite ? RemoteTransferState.Failed : RemoteTransferState.Unknown, done.State, done.ErrorMessage);
            Assert.AreEqual(definite, done.DestinationCommitConfirmedAbsent);
            Assert.AreEqual(definite, done.RemoteStopConfirmed);
            Assert.AreEqual(failure switch { "status" => "commit-rejected", "unsupported" => "not-supported", _ => "commit-unknown" }, done.ErrorCode);
            Assert.IsFalse(done.CanResume);
            if (definite)
            {
                Assert.Contains(failure == "status" ? "destination exists" : "posix", done.ErrorMessage!, StringComparison.OrdinalIgnoreCase);
                await _sftp.Received(1).DeleteAsync(_session, temporary!, Arg.Any<IProgress<SftpDeleteProgress>>(), Arg.Any<CancellationToken>());
            }
            else await _sftp.DidNotReceive().DeleteAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<IProgress<SftpDeleteProgress>>(), Arg.Any<CancellationToken>());
        }
        finally { await WaitIdle(api); File.Delete(file); }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task CooperativeStopDuringWriteIsConfirmedAndCleansTemporary(bool deadline)
    {
        string file = CreateFile(64);
        var started = NewSignal();
        string? temporary = null;
        _sftp.UploadStreamAsync(_session, Arg.Any<Stream>(), Arg.Any<string>(), Arg.Any<long>(), Arg.Any<DateTime?>(),
            Arg.Any<IProgress<TransferProgress>>(), 0, Arg.Any<CancellationToken>()).Returns(call =>
            {
                temporary = call.ArgAt<string>(2);
                return StopCooperatively(started, call.ArgAt<CancellationToken>(7));
            });
        using var api = new RemoteFsCapability(_sftp, _connections, pluginId: "test.transfer", uploadPermissions: AllowUploads());
        try
        {
            var start = await api.StartUploadAsync(_session.ToString(), file, "/target",
                new() { OperationId = "cooperative", Timeout = deadline ? TimeSpan.FromSeconds(1) : null });
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (!deadline) await api.CancelTransferAsync(start.TransferId, 1);
            RemoteTransferSnapshot done = await Terminal(api, start.TransferId);
            // 后端收到取消后照常收尾停下了:这是确认的停写,不是 Unknown;没提交过的临时文件随之清理。
            Assert.AreEqual(deadline ? RemoteTransferState.TimedOut : RemoteTransferState.Cancelled, done.State, done.ErrorMessage);
            Assert.AreEqual(deadline ? "timeout" : "cancelled", done.ErrorCode);
            Assert.IsTrue(done.RemoteStopConfirmed);
            Assert.IsTrue(done.DestinationCommitConfirmedAbsent);
            Assert.IsFalse(done.CanResume);
            await WaitIdle(api);
            await _sftp.Received(1).DeleteAsync(_session, temporary!, Arg.Any<IProgress<SftpDeleteProgress>>(), Arg.Any<CancellationToken>());
            await _sftp.DidNotReceive().CommitUploadAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
        }
        finally { await WaitIdle(api); File.Delete(file); }
    }

    [TestMethod]
    [DataRow("disconnect", RemoteTransferState.Failed, "session-disconnected")]
    [DataRow("dispose", RemoteTransferState.Cancelled, "plugin-stopped")]
    [DataRow("lifetime", RemoteTransferState.Cancelled, "plugin-stopped")]
    public async Task DisconnectAndPluginStopAreNotReportedAsTimeoutOrUserCancellation(string trigger, RemoteTransferState expected, string code)
    {
        string file = CreateFile(8);
        var started = NewSignal();
        _sftp.UploadStreamAsync(_session, Arg.Any<Stream>(), Arg.Any<string>(), Arg.Any<long>(), Arg.Any<DateTime?>(),
            Arg.Any<IProgress<TransferProgress>>(), 0, Arg.Any<CancellationToken>()).Returns(call => StopCooperatively(started, call.ArgAt<CancellationToken>(7)));
        using var lifetime = new CancellationTokenSource();
        using var api = new RemoteFsCapability(_sftp, _connections, lifetime.Token, "test.transfer", AllowUploads());
        try
        {
            // 设了死线:断线、停用都会取消同一个停止令牌,不能因此被推断成「到期」。
            var start = await api.StartUploadAsync(_session.ToString(), file, "/target",
                new() { OperationId = trigger, Timeout = TimeSpan.FromMinutes(10) });
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (trigger == "disconnect") _connections.SessionDisconnected += Raise.Event<Action<SshSession>>(_connections.GetSession(_session)!);
            else if (trigger == "dispose") api.Dispose();
            else lifetime.Cancel();
            await WaitIdle(api);
            // 停用后租约随任务收尾释放,只剩撤权清理入口能读到(脱敏的)快照;状态与停止确认照样可见。
            RemoteTransferSnapshot done = await api.CancelTransferAsync(start.TransferId, 1);
            Assert.AreEqual(expected, done.State, done.ErrorMessage);
            Assert.AreEqual(code, done.ErrorCode);
            Assert.IsTrue(done.RemoteStopConfirmed);
            Assert.IsTrue(done.DestinationCommitConfirmedAbsent);
        }
        finally { await WaitIdle(api); File.Delete(file); }
    }

    [TestMethod]
    public async Task FinalSpeedIsMeasuredFromRunningNotFromApproval()
    {
        string file = CreateFile(64 * 1024);
        IPluginPermissionPrompt prompt = Substitute.For<IPluginPermissionPrompt>();
        prompt.RequestFileUploadAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(async _ => { await Task.Delay(1500); return PluginPermissionDecision.AllowOnce; });
        string? hash = null;
        _sftp.UploadStreamAsync(_session, Arg.Any<Stream>(), Arg.Any<string>(), Arg.Any<long>(), Arg.Any<DateTime?>(),
            Arg.Any<IProgress<TransferProgress>>(), 0, Arg.Any<CancellationToken>()).Returns(async call =>
            { hash = Convert.ToHexString(await SHA256.HashDataAsync(call.ArgAt<Stream>(1))); });
        _sftp.ComputeSha256Async(_session, Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult<IReadOnlyDictionary<string, string?>>(new Dictionary<string, string?> { [call.ArgAt<IReadOnlyList<string>>(1)[0]] = hash }));
        using var api = new RemoteFsCapability(_sftp, _connections, pluginId: "test.transfer", uploadPermissions: new(null, prompt));
        try
        {
            var start = await api.StartUploadAsync(_session.ToString(), file, "/target", new() { OperationId = "speed" });
            RemoteTransferSnapshot done = await Terminal(api, start.TransferId);
            Assert.AreEqual(RemoteTransferState.Succeeded, done.State, done.ErrorMessage);
            // 用户在审批框前停了 1.5 秒;传输本身是本地替身,远用不了 0.75 秒。把审批算进去,速率会低于 总量/1.5 秒。
            Assert.IsGreaterThan(done.TotalBytes / 0.75, done.SpeedBytesPerSecond!.Value);
            await WaitIdle(api);
        }
        finally { await WaitIdle(api); File.Delete(file); }
    }

    [TestMethod]
    [OSCondition(ConditionMode.Exclude, OperatingSystems.Windows)]
    public async Task UnixFifoSourceIsRejectedBeforeOpeningAndDoesNotBlockOtherCalls()
    {
        string directory = Path.Combine(Path.GetTempPath(), "vela-transfer-fifo-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string fifo = Path.Combine(directory, "pipe");
        using (Process mkfifo = Process.Start(new ProcessStartInfo("mkfifo") { ArgumentList = { fifo } })!) await mkfifo.WaitForExitAsync();
        string file = CreateFile(8);
        var started = NewSignal();
        var release = NewSignal();
        _sftp.UploadStreamAsync(_session, Arg.Any<Stream>(), Arg.Any<string>(), Arg.Any<long>(), Arg.Any<DateTime?>(),
            Arg.Any<IProgress<TransferProgress>>(), 0, Arg.Any<CancellationToken>()).Returns(async _ => { started.TrySetResult(); await release.Task; });
        using var api = new RemoteFsCapability(_sftp, _connections, pluginId: "test.transfer", uploadPermissions: AllowUploads());
        Task<RemoteTransferSnapshot>? fifoStart = null;
        try
        {
            RemoteTransferSnapshot running = await api.StartUploadAsync(_session.ToString(), file, "/target", new() { OperationId = "regular" });
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            // 没有写端的 FIFO:打开它会一直阻塞。必须在打开前就拒绝,也不能占着账本锁让同插件的查询陪着卡死。
            fifoStart = Task.Run(() => api.StartUploadAsync(_session.ToString(), fifo, "/fifo", new() { OperationId = "fifo" }));
            await Assert.ThrowsExactlyAsync<NotSupportedException>(() => fifoStart.WaitAsync(TimeSpan.FromSeconds(5)));
            RemoteTransferSnapshot queried = await Task.Run(() => api.GetTransferAsync(running.TransferId)).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.AreEqual(RemoteTransferState.Running, queried.State);
            Assert.AreEqual(1, await Task.Run(() => api.ActiveTransfers).WaitAsync(TimeSpan.FromSeconds(5)));
        }
        finally
        {
            release.TrySetResult();
            // 修复前那次启动卡在 open 上(还占着锁):以写端打开一次把它放出来,免得拖住整个测试进程。
            if (fifoStart is { IsCompleted: false })
                await Task.Run(() => { using var writer = new FileStream(fifo, FileMode.Open, FileAccess.Write); }).WaitAsync(TimeSpan.FromSeconds(5));
            await WaitIdle(api);
            File.Delete(file);
            Directory.Delete(directory, true);
        }
    }

    [TestMethod]
    public async Task CancelDuringCommitRemainsUnknownUntilBackendFinishes_AndBlocksIdleRecycle()
    {
        string file = CreateFile(8);
        var committing = NewSignal();
        var release = NewSignal();
        string? hash = null;
        _sftp.UploadStreamAsync(_session, Arg.Any<Stream>(), Arg.Any<string>(), Arg.Any<long>(), Arg.Any<DateTime?>(),
            Arg.Any<IProgress<TransferProgress>>(), 0, Arg.Any<CancellationToken>()).Returns(async call =>
            { hash = Convert.ToHexString(await SHA256.HashDataAsync(call.ArgAt<Stream>(1))); });
        _sftp.ComputeSha256Async(_session, Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult<IReadOnlyDictionary<string, string?>>(new Dictionary<string, string?> { [call.ArgAt<IReadOnlyList<string>>(1)[0]] = hash }));
        _sftp.CommitUploadAsync(_session, Arg.Any<string>(), "/target", false, Arg.Any<CancellationToken>())
            .Returns(async _ => { committing.TrySetResult(); await release.Task; });
        using var api = new RemoteFsCapability(_sftp, _connections, pluginId: "test.transfer", uploadPermissions: AllowUploads()) { StopGracePeriod = UncooperativeGrace };
        try
        {
            var start = await api.StartUploadAsync(_session.ToString(), file, "/target", new() { OperationId = "commit" });
            await committing.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await api.CancelTransferAsync(start.TransferId, 1);
            var done = await Terminal(api, start.TransferId);
            Assert.AreEqual(RemoteTransferState.Unknown, done.State);
            Assert.IsFalse(done.RemoteStopConfirmed);
            Assert.IsFalse(done.DestinationCommitConfirmedAbsent);
            Assert.IsFalse(done.CanResume);
            Assert.AreEqual(1, api.ActiveTransfers);
        }
        finally { release.TrySetResult(); await WaitIdle(api); File.Delete(file); }
    }

    [TestMethod]
    public async Task ExplicitTimeoutIsLocalTerminal_NotProofOfRemoteStop()
    {
        string file = CreateFile(8);
        var release = NewSignal();
        _sftp.UploadStreamAsync(_session, Arg.Any<Stream>(), Arg.Any<string>(), Arg.Any<long>(), Arg.Any<DateTime?>(),
            Arg.Any<IProgress<TransferProgress>>(), 0, Arg.Any<CancellationToken>()).Returns(async _ => await release.Task);
        using var api = new RemoteFsCapability(_sftp, _connections, pluginId: "test.transfer", uploadPermissions: AllowUploads()) { StopGracePeriod = UncooperativeGrace };
        try
        {
            var start = await api.StartUploadAsync(_session.ToString(), file, "/target", new() { OperationId = "timeout", Timeout = TimeSpan.FromMilliseconds(200) });
            var done = await Terminal(api, start.TransferId);
            Assert.AreEqual(RemoteTransferState.TimedOut, done.State);
            Assert.IsFalse(done.IsSuccess);
            Assert.IsFalse(done.CanResume);
            Assert.IsFalse(done.RemoteStopConfirmed, "后端不理取消、宽限期内没停:到期不等于确认停写。");
        }
        finally { release.TrySetResult(); await WaitIdle(api); File.Delete(file); }
    }

    [TestMethod]
    public async Task ShutdownStopsWaitButDoesNotClaimUnconfirmedBackendStopped()
    {
        string file = CreateFile(8);
        var release = NewSignal();
        var started = NewSignal();
        _sftp.UploadStreamAsync(_session, Arg.Any<Stream>(), Arg.Any<string>(), Arg.Any<long>(), Arg.Any<DateTime?>(),
            Arg.Any<IProgress<TransferProgress>>(), 0, Arg.Any<CancellationToken>()).Returns(async _ => { started.TrySetResult(); await release.Task; });
        using var lifetime = new CancellationTokenSource();
        using var api = new RemoteFsCapability(_sftp, _connections, lifetime.Token, "test.transfer", AllowUploads()) { StopGracePeriod = UncooperativeGrace };
        try
        {
            var start = await api.StartUploadAsync(_session.ToString(), file, "/target", new() { OperationId = "shutdown" });
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            lifetime.Cancel();
            var done = await Terminal(api, start.TransferId);
            Assert.AreEqual(RemoteTransferState.Unknown, done.State);
            Assert.IsFalse(done.RemoteStopConfirmed);
        }
        finally { release.TrySetResult(); await WaitIdle(api); File.Delete(file); }
    }

    [TestMethod]
    public async Task LegacyHostDoesNotReceiveAnyTransferRpc()
    {
        await using var pair = await Pair.Open();
        int calls = 0;
        pair._host.SetRequestHandler((_, _, _) => { calls++; return Task.FromResult<object?>(null); });
        var proxy = new RpcRemoteFs(pair._plugin);
        await Assert.ThrowsExactlyAsync<NotSupportedException>(() => proxy.StartUploadAsync("s", "local", "/remote", new() { OperationId = "old" }));
        Assert.AreEqual(0, calls);
    }

    [TestMethod]
    public async Task RpcRequestCancellationDoesNotCancelFrameOrLoseOperationReceipt()
    {
        await using var pair = await Pair.Open();
        var registered = NewSignal();
        var release = NewSignal();
        RemoteTransferSnapshot snapshot = new() { TransferId = "transfer", OperationId = "same", SessionId = "s", LocalPath = "local", RemotePath = "/remote", Sequence = 1, Generation = 1, State = RemoteTransferState.Queued };
        int starts = 0;
        pair._host.SetRequestHandler(async (method, payload, _) =>
        {
            if (method != PluginRpc.FsTransferStartUpload) throw new InvalidOperationException();
            Assert.AreEqual("same", payload!.Value.Deserialize<FsTransferStartUploadRequest>()!.Options.OperationId);
            Interlocked.Increment(ref starts);
            registered.TrySetResult();
            await release.Task;
            return snapshot;
        });
        var proxy = new RpcRemoteFs(pair._plugin, true);
        using var request = new CancellationTokenSource();
        Task<RemoteTransferSnapshot> first = proxy.StartUploadAsync("s", "local", "/remote", new() { OperationId = "same" }, request.Token);
        await registered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        request.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => first);
        release.TrySetResult();
        Assert.AreEqual(snapshot, await proxy.StartUploadAsync("s", "local", "/remote", new() { OperationId = "same" }));
        Assert.AreEqual(2, starts, "回执可重取,宿主用同一 OperationId 去重而不是代理私造新 id。");
    }

    [TestMethod]
    public async Task RealPipeRoutesAllTransferMethodsAndAdvertisesActualCapability()
    {
        await using var pair = await Pair.Open();
        RemoteTransferSnapshot snapshot = new() { TransferId = "transfer", OperationId = "operation", SessionId = "s", LocalPath = "local", RemotePath = "/remote", Sequence = 3, Generation = 1, State = RemoteTransferState.Running };
        IRemoteFsApi fake = Substitute.For<IRemoteFsApi>();
        fake.SupportsTransferOperations.Returns(true);
        fake.StartUploadAsync("s", "local", "/remote", Arg.Any<RemoteUploadOptions>(), Arg.Any<CancellationToken>()).Returns(snapshot);
        fake.GetTransferAsync("transfer", Arg.Any<CancellationToken>()).Returns(snapshot);
        fake.WaitTransferAsync("transfer", 3, TimeSpan.Zero, Arg.Any<CancellationToken>()).Returns(snapshot);
        fake.CancelTransferAsync("transfer", 1, Arg.Any<CancellationToken>()).Returns(snapshot);
        fake.ResumeTransferAsync("transfer", Arg.Any<RemoteTransferResumeOptions>(), Arg.Any<CancellationToken>()).Returns(Task.FromException<RemoteTransferSnapshot>(new NotSupportedException()));
        using PluginContext context = Context(fake);
        using var router = new PluginCapabilityRouter(context, pair._host, "token", "1");
        pair._host.SetRequestHandler(router.HandleRequestAsync);
        HandshakeResponse? hello = await pair._plugin.RequestAsync<HandshakeResponse>(PluginRpc.Handshake,
            new HandshakeRequest("token", "test.transfer", "1", [VelaPluginApi.Level]), TimeSpan.FromSeconds(5));
        Assert.IsTrue(hello!.SupportsTransferOperations);
        var proxy = new RpcRemoteFs(pair._plugin, hello.SupportsTransferOperations);
        Assert.AreEqual(snapshot, await proxy.StartUploadAsync("s", "local", "/remote", new() { OperationId = "operation" }));
        Assert.AreEqual(snapshot, await proxy.GetTransferAsync("transfer"));
        Assert.AreEqual(snapshot, await proxy.WaitTransferAsync("transfer", 3, TimeSpan.Zero));
        Assert.AreEqual(snapshot, await proxy.CancelTransferAsync("transfer", 1));
        await Assert.ThrowsExactlyAsync<NotSupportedException>(() => proxy.ResumeTransferAsync("transfer", new() { OperationId = "resume", ExpectedGeneration = 1, TargetSessionId = "s" }));
    }

    [TestMethod]
    public async Task DownloadOnlyRpcCapabilitySupportsStartAndSharedQueriesButNotUpload()
    {
        await using var pair = await Pair.Open();
        RemoteTransferSnapshot snapshot = new() { TransferId = "download", OperationId = "d", SessionId = "s", LocalPath = "local", RemotePath = "/remote", Direction = RemoteTransferDirection.Download, Sequence = 1, Generation = 1, State = RemoteTransferState.Running };
        IRemoteFsApi fake = Substitute.For<IRemoteFsApi>();
        fake.SupportsDownloadOperations.Returns(true);
        fake.StartDownloadAsync("s", "/remote", "local", Arg.Any<RemoteDownloadOptions>(), Arg.Any<CancellationToken>()).Returns(snapshot);
        fake.GetTransferAsync("download", Arg.Any<CancellationToken>()).Returns(snapshot);
        fake.WaitTransferAsync("download", 1, TimeSpan.Zero, Arg.Any<CancellationToken>()).Returns(snapshot);
        fake.CancelTransferAsync("download", 1, Arg.Any<CancellationToken>()).Returns(snapshot);
        using PluginContext context = Context(fake);
        using var router = new PluginCapabilityRouter(context, pair._host, "token", "1");
        pair._host.SetRequestHandler(router.HandleRequestAsync);
        HandshakeResponse? hello = await pair._plugin.RequestAsync<HandshakeResponse>(PluginRpc.Handshake,
            new HandshakeRequest("token", "test.transfer", "1", [VelaPluginApi.Level]), TimeSpan.FromSeconds(5));
        Assert.IsFalse(hello!.SupportsTransferOperations);
        Assert.IsTrue(hello.SupportsDownloadOperations);
        var proxy = new RpcRemoteFs(pair._plugin, hello.SupportsTransferOperations, hello.SupportsDownloadOperations);
        Assert.AreEqual(snapshot, await proxy.StartDownloadAsync("s", "/remote", "local", new() { OperationId = "d" }));
        Assert.AreEqual(snapshot, await proxy.GetTransferAsync("download"));
        Assert.AreEqual(snapshot, await proxy.WaitTransferAsync("download", 1, TimeSpan.Zero));
        Assert.AreEqual(snapshot, await proxy.CancelTransferAsync("download", 1));
        await Assert.ThrowsExactlyAsync<NotSupportedException>(() => proxy.StartUploadAsync("s", "local", "/remote", new() { OperationId = "u" }));
    }

    [TestMethod]
    public void DownloadProgressKeepsLargeAndOverLengthActualCounts()
    {
        RemoteTransferSnapshot snapshot = new() { TransferId = "d", OperationId = "d", SessionId = "s", LocalPath = "l", RemotePath = "/r", Direction = RemoteTransferDirection.Download, TotalBytes = 10 };
        var value = RemoteFsCapability.ApplyTransferProgress(snapshot, new TransferProgress { FileName = "source", BytesTransferred = 5L * 1024 * 1024 * 1024, TotalBytes = 10, Percentage = 100, SpeedBytesPerSecond = 0, EstimatedTimeRemaining = TimeSpan.Zero });
        Assert.AreEqual(5L * 1024 * 1024 * 1024, value.TransferredBytes);
        Assert.AreEqual(17L, RemoteFsCapability.ApplyTransferProgress(snapshot with { TotalBytes = -1 }, new TransferProgress { FileName = "source", BytesTransferred = 17, TotalBytes = -1, Percentage = 0, SpeedBytesPerSecond = 0, EstimatedTimeRemaining = TimeSpan.Zero }).TransferredBytes);
    }

    [TestMethod]
    [OSCondition(ConditionMode.Include, OperatingSystems.Windows)]
    public async Task DownloadNoApprovalFailsClosedAndSharesDirectionSignature()
    {
        string path = Path.Combine(Path.GetTempPath(), "vela-download-test-" + Guid.NewGuid().ToString("N"));
        using var api = new RemoteFsCapability(_sftp, _connections, pluginId: "test.transfer");
        var options = new RemoteDownloadOptions { OperationId = "shared" };
        var started = await api.StartDownloadAsync(_session.ToString(), "/source", path, options);
        var done = await Terminal(api, started.TransferId);
        Assert.AreEqual("permission-denied", done.ErrorCode);
        Assert.IsFalse(File.Exists(path));
        Assert.AreEqual(started.TransferId, (await api.StartDownloadAsync(_session.ToString(), "/source", path, options)).TransferId);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => api.StartUploadAsync(_session.ToString(), path, "/source", new() { OperationId = "shared" }));
        await _sftp.DidNotReceive().DownloadFileAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IProgress<TransferProgress>>(), Arg.Any<long>(), Arg.Any<CancellationToken>());
        await WaitIdle(api);
    }

    [TestMethod]
    [OSCondition(ConditionMode.Include, OperatingSystems.Windows)]
    public async Task DownloadCommitsOnlyAfterVerificationAndProtectsExistingDestination()
    {
        string directory = Path.Combine(Path.GetTempPath(), "vela-download-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "target");
        IPluginPermissionPrompt prompt = Substitute.For<IPluginPermissionPrompt>();
        prompt.RequestFileDownloadAsync(Arg.Any<string>(), Arg.Any<string>(), "/source", path, Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(PluginPermissionDecision.AllowOnce);
        _sftp.GetFileInfoAsync(_session, "/source", Arg.Any<CancellationToken>()).Returns(new RemoteFileInfo { Name = "source", FullPath = "/source", Size = 3, LastModified = new DateTime(2026, 10, 1), Permissions = "-rw-------", IsDirectory = false, Owner = "user", Group = "user" });
        _sftp.DownloadStreamAsync(_session, "/source", Arg.Any<Stream>(), Arg.Any<IProgress<TransferProgress>>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                Assert.IsFalse(File.Exists(path), "Final target cannot appear before commit.");
                await call.ArgAt<Stream>(2).WriteAsync(new byte[] { 1, 2, 3 }, call.ArgAt<CancellationToken>(4));
            });
        ConfigureRemoteDigest("/source", [1, 2, 3]);
        using var api = new RemoteFsCapability(_sftp, _connections, pluginId: "test.transfer", uploadPermissions: new(null, prompt));
        try
        {
            var start = await api.StartDownloadAsync(_session.ToString(), "/source", path, new() { OperationId = "download" });
            var done = await Terminal(api, start.TransferId);
            await WaitIdle(api);
            Assert.AreEqual(RemoteTransferState.Succeeded, done.State, done.ErrorMessage);
            await _sftp.Received(1).ComputeSha256Async(_session, Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>());
            CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(path));
            var exists = await api.StartDownloadAsync(_session.ToString(), "/source", path, new() { OperationId = "existing" });
            RemoteTransferSnapshot refused = await Terminal(api, exists.TransferId);
            Assert.AreEqual(RemoteTransferState.Failed, refused.State, refused.ErrorMessage);
            Assert.IsTrue(refused.RemoteStopConfirmed);
            Assert.IsTrue(refused.DestinationCommitConfirmedAbsent);
            Assert.AreEqual("download-failed", refused.ErrorCode);
            await WaitIdle(api);
            CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(path));
            Assert.AreEqual(1, Directory.GetFiles(directory).Length);
        }
        finally { await WaitIdle(api); Directory.Delete(directory, true); }
    }

    [TestMethod]
    [OSCondition(ConditionMode.Exclude, OperatingSystems.Windows)]
    public async Task UnsupportedLocalPlatformsRejectBackgroundDownloadWithoutLegacyFallback()
    {
        using var api = new RemoteFsCapability(_sftp, _connections);
        Assert.IsFalse(api.SupportsDownloadOperations);
        await Assert.ThrowsExactlyAsync<PlatformNotSupportedException>(() => api.StartDownloadAsync(_session.ToString(), "/source", Path.Combine(Path.GetTempPath(), "target"), new() { OperationId = "unsupported" }));
        await _sftp.DidNotReceive().DownloadFileAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IProgress<TransferProgress>>(), Arg.Any<long>(), Arg.Any<CancellationToken>());
    }

    [TestMethod]
    [OSCondition(ConditionMode.Include, OperatingSystems.Windows)]
    public async Task DownloadShortReadKeepsOldTargetAndCleansOwnedTemporary()
    {
        // 取消的两种后端(照常停下 / 不理取消)另有 DownloadCooperativeStop… 与 DownloadCancellationDoesNotClaim… 两条用例。
        string directory = Path.Combine(Path.GetTempPath(), "vela-download-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "target");
        await File.WriteAllBytesAsync(path, [9]);
        IPluginPermissionPrompt prompt = Substitute.For<IPluginPermissionPrompt>();
        prompt.RequestFileDownloadAsync(Arg.Any<string>(), Arg.Any<string>(), "/source", path, true, Arg.Any<CancellationToken>()).Returns(PluginPermissionDecision.AllowOnce);
        _sftp.GetFileInfoAsync(_session, "/source", Arg.Any<CancellationToken>()).Returns(new RemoteFileInfo { Name = "source", FullPath = "/source", Size = 3, LastModified = DateTime.UnixEpoch, Permissions = "-rw-------", IsDirectory = false, Owner = "user", Group = "user" });
        _sftp.DownloadStreamAsync(_session, "/source", Arg.Any<Stream>(), Arg.Any<IProgress<TransferProgress>>(), Arg.Any<CancellationToken>()).Returns(async call => await call.ArgAt<Stream>(2).WriteAsync(new byte[] { 1 }));
        ConfigureRemoteDigest("/source", [1, 2, 3]);
        using var api = new RemoteFsCapability(_sftp, _connections, pluginId: "test.transfer", uploadPermissions: new(null, prompt));
        try
        {
            var start = await api.StartDownloadAsync(_session.ToString(), "/source", path, new() { OperationId = "short", Overwrite = true });
            RemoteTransferSnapshot done = await Terminal(api, start.TransferId);
            Assert.AreEqual(RemoteTransferState.Failed, done.State, done.ErrorMessage);
            Assert.IsTrue(done.RemoteStopConfirmed);
            Assert.IsTrue(done.DestinationCommitConfirmedAbsent);
            Assert.AreEqual("download-failed", done.ErrorCode);
            await WaitIdle(api);
            CollectionAssert.AreEqual(new byte[] { 9 }, await File.ReadAllBytesAsync(path));
            Assert.AreEqual(1, Directory.GetFiles(directory).Length);
        }
        finally { await WaitIdle(api); Directory.Delete(directory, true); }
    }

    [TestMethod]
    [OSCondition(ConditionMode.Include, OperatingSystems.Windows)]
    public async Task DownloadCancellationDoesNotClaimUncooperativeBackendStopped()
    {
        string directory = Path.Combine(Path.GetTempPath(), "vela-download-cancel-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "target");
        IPluginPermissionPrompt prompt = Substitute.For<IPluginPermissionPrompt>();
        prompt.RequestFileDownloadAsync(Arg.Any<string>(), Arg.Any<string>(), "/source", path, false, Arg.Any<CancellationToken>()).Returns(PluginPermissionDecision.AllowOnce);
        _sftp.GetFileInfoAsync(_session, "/source", Arg.Any<CancellationToken>()).Returns(new RemoteFileInfo { Name = "source", FullPath = "/source", Size = 3, LastModified = DateTime.UnixEpoch, Permissions = "-rw-------", IsDirectory = false, Owner = "user", Group = "user" });
        var writing = NewSignal();
        var release = NewSignal();
        _sftp.DownloadStreamAsync(_session, "/source", Arg.Any<Stream>(), Arg.Any<IProgress<TransferProgress>>(), Arg.Any<CancellationToken>())
            .Returns(async _ => { writing.TrySetResult(); await release.Task; });
        using var api = new RemoteFsCapability(_sftp, _connections, pluginId: "test.transfer", uploadPermissions: new(null, prompt)) { StopGracePeriod = UncooperativeGrace };
        try
        {
            var start = await api.StartDownloadAsync(_session.ToString(), "/source", path, new() { OperationId = "cancel" });
            await writing.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await api.CancelTransferAsync(start.TransferId, 1);
            var done = await Terminal(api, start.TransferId);
            Assert.AreEqual(RemoteTransferState.Unknown, done.State);
            Assert.IsFalse(done.RemoteStopConfirmed);
            Assert.IsGreaterThan(0, api.ActiveTransfers);
            Assert.IsFalse(File.Exists(path));
        }
        finally
        {
            release.TrySetResult();
            await WaitIdle(api);
            Assert.AreEqual(0, Directory.GetFiles(directory).Length);
            Directory.Delete(directory);
        }
    }

    [TestMethod]
    [DataRow(false, false, "target")]
    [DataRow(true, false, "target")]
    [DataRow(false, true, "target")]
    [DataRow(true, true, "target")]
    [DataRow(false, false, "下载-文件-\U0001F600.bin")]
    [DataRow(true, false, "下载-文件-\U0001F600.bin")]
    [OSCondition(ConditionMode.Include, OperatingSystems.Windows)]
    public async Task DownloadCommitHonorsLateDestinationAndSourceChanges(bool overwrite, bool sourceChanged, string fileName)
    {
        string directory = Path.Combine(Path.GetTempPath(), "vela-download-race-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, fileName);
        IPluginPermissionPrompt prompt = Substitute.For<IPluginPermissionPrompt>();
        prompt.RequestFileDownloadAsync(Arg.Any<string>(), Arg.Any<string>(), "/source", path, overwrite, Arg.Any<CancellationToken>()).Returns(PluginPermissionDecision.AllowOnce);
        RemoteFileInfo Info(int size) => new() { Name = "source", FullPath = "/source", Size = size, LastModified = DateTime.UnixEpoch, Permissions = "-rw-------", IsDirectory = false, Owner = "user", Group = "user" };
        int reads = 0;
        _sftp.GetFileInfoAsync(_session, "/source", Arg.Any<CancellationToken>()).Returns(_ => Task.FromResult(Info(++reads >= 3 && sourceChanged ? 4 : 3)));
        _sftp.DownloadStreamAsync(_session, "/source", Arg.Any<Stream>(), Arg.Any<IProgress<TransferProgress>>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                await call.ArgAt<Stream>(2).WriteAsync(new byte[] { 1, 2, 3 });
                await File.WriteAllBytesAsync(path, [9]);
            });
        ConfigureRemoteDigest("/source", [1, 2, 3]);
        using var api = new RemoteFsCapability(_sftp, _connections, pluginId: "test.transfer", uploadPermissions: new(null, prompt));
        try
        {
            var start = await api.StartDownloadAsync(_session.ToString(), "/source", path, new() { OperationId = "race", Overwrite = overwrite });
            var done = await Terminal(api, start.TransferId);
            await WaitIdle(api);
            Assert.AreEqual(overwrite && !sourceChanged, done.IsSuccess, done.ErrorMessage);
            Assert.AreEqual(!done.IsSuccess, done.DestinationCommitConfirmedAbsent);
            Assert.IsTrue(done.RemoteStopConfirmed);
            if (sourceChanged) Assert.AreEqual("source-changed", done.ErrorCode);
            CollectionAssert.AreEqual(overwrite && !sourceChanged ? new byte[] { 1, 2, 3 } : new byte[] { 9 }, await File.ReadAllBytesAsync(path));
            Assert.AreEqual(1, Directory.GetFiles(directory).Length);
        }
        finally { await WaitIdle(api); Directory.Delete(directory, true); }
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task DownloadVerifiesRemoteDigestBeforeCommit(bool match)
    {
        string directory = NewDirectory("vela-download-digest-");
        string path = Path.Combine(directory, "target");
        _sftp.GetFileInfoAsync(_session, "/source", Arg.Any<CancellationToken>()).Returns(SourceInfo(3));
        _sftp.DownloadStreamAsync(_session, "/source", Arg.Any<Stream>(), Arg.Any<IProgress<TransferProgress>>(), Arg.Any<CancellationToken>())
            .Returns(async call => await call.ArgAt<Stream>(2).WriteAsync(new byte[] { 1, 2, 3 }, call.ArgAt<CancellationToken>(4)));
        // 远端摘要取得到时必须真的比对:长度对、内容不对,也不能提交。
        ConfigureRemoteDigest("/source", match ? [1, 2, 3] : [9, 9, 9]);
        using RemoteFsCapability api = DownloadApi();
        try
        {
            var start = await api.StartDownloadAsync(_session.ToString(), "/source", path, new() { OperationId = "digest" });
            RemoteTransferSnapshot done = await Terminal(api, start.TransferId);
            await WaitIdle(api);
            await _sftp.Received(1).ComputeSha256Async(_session, Arg.Is<IReadOnlyList<string>>(paths => paths.Count == 1 && paths[0] == "/source"), Arg.Any<CancellationToken>());
            if (match)
            {
                Assert.AreEqual(RemoteTransferState.Succeeded, done.State, done.ErrorMessage);
                CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(path));
            }
            else
            {
                Assert.AreEqual(RemoteTransferState.Failed, done.State, done.ErrorMessage);
                Assert.IsTrue(done.RemoteStopConfirmed);
                Assert.IsTrue(done.DestinationCommitConfirmedAbsent);
                Assert.AreEqual("download-failed", done.ErrorCode);
                Assert.Contains("verification failed", done.ErrorMessage!, StringComparison.Ordinal);
                Assert.IsFalse(File.Exists(path));
            }
            Assert.HasCount(match ? 1 : 0, Directory.GetFiles(directory));
        }
        finally { await WaitIdle(api); Directory.Delete(directory, true); }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task DownloadCooperativeStopIsConfirmedAndCleansTemporary(bool deadline)
    {
        string directory = NewDirectory("vela-download-stop-");
        string path = Path.Combine(directory, "target");
        _sftp.GetFileInfoAsync(_session, "/source", Arg.Any<CancellationToken>()).Returns(SourceInfo(3));
        var started = NewSignal();
        _sftp.DownloadStreamAsync(_session, "/source", Arg.Any<Stream>(), Arg.Any<IProgress<TransferProgress>>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                await call.ArgAt<Stream>(2).WriteAsync(new byte[] { 1 });
                await StopCooperatively(started, call.ArgAt<CancellationToken>(4));
            });
        using RemoteFsCapability api = DownloadApi();
        try
        {
            var start = await api.StartDownloadAsync(_session.ToString(), "/source", path,
                new() { OperationId = "stop", Timeout = deadline ? TimeSpan.FromSeconds(1) : null });
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (!deadline) await api.CancelTransferAsync(start.TransferId, 1);
            RemoteTransferSnapshot done = await Terminal(api, start.TransferId);
            // 后端照常收尾停下:确认停写,自己的临时文件当场删掉;最终目标从未出现。
            Assert.AreEqual(deadline ? RemoteTransferState.TimedOut : RemoteTransferState.Cancelled, done.State, done.ErrorMessage);
            Assert.AreEqual(deadline ? "timeout" : "cancelled", done.ErrorCode);
            Assert.IsTrue(done.RemoteStopConfirmed);
            Assert.IsTrue(done.DestinationCommitConfirmedAbsent);
            await WaitIdle(api);
            Assert.IsFalse(File.Exists(path));
            Assert.IsEmpty(Directory.GetFiles(directory));
        }
        finally { await WaitIdle(api); Directory.Delete(directory, true); }
    }

    [TestMethod]
    public async Task DownloadUncooperativeBackendStaysUnknownUntilItExits()
    {
        string directory = NewDirectory("vela-download-uncooperative-");
        string path = Path.Combine(directory, "target");
        _sftp.GetFileInfoAsync(_session, "/source", Arg.Any<CancellationToken>()).Returns(SourceInfo(3));
        var writing = NewSignal();
        var release = NewSignal();
        _sftp.DownloadStreamAsync(_session, "/source", Arg.Any<Stream>(), Arg.Any<IProgress<TransferProgress>>(), Arg.Any<CancellationToken>())
            .Returns(async _ => { writing.TrySetResult(); await release.Task; });
        using RemoteFsCapability api = DownloadApi(stopGrace: UncooperativeGrace);
        try
        {
            var start = await api.StartDownloadAsync(_session.ToString(), "/source", path, new() { OperationId = "uncooperative" });
            await writing.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await api.CancelTransferAsync(start.TransferId, 1);
            RemoteTransferSnapshot done = await Terminal(api, start.TransferId);
            Assert.AreEqual(RemoteTransferState.Unknown, done.State, done.ErrorMessage);
            Assert.IsFalse(done.RemoteStopConfirmed);
            Assert.IsGreaterThan(0, api.ActiveTransfers);
            Assert.IsFalse(File.Exists(path));
        }
        finally
        {
            release.TrySetResult();
            await WaitIdle(api);
            Assert.IsEmpty(Directory.GetFiles(directory));
            Directory.Delete(directory, true);
        }
    }

    [TestMethod]
    [DataRow("disconnect", RemoteTransferState.Failed, "session-disconnected")]
    [DataRow("dispose", RemoteTransferState.Cancelled, "plugin-stopped")]
    public async Task DownloadDisconnectAndPluginStopAreNotReportedAsTimeout(string trigger, RemoteTransferState expected, string code)
    {
        string directory = NewDirectory("vela-download-reason-");
        string path = Path.Combine(directory, "target");
        _sftp.GetFileInfoAsync(_session, "/source", Arg.Any<CancellationToken>()).Returns(SourceInfo(3));
        var started = NewSignal();
        _sftp.DownloadStreamAsync(_session, "/source", Arg.Any<Stream>(), Arg.Any<IProgress<TransferProgress>>(), Arg.Any<CancellationToken>())
            .Returns(call => StopCooperatively(started, call.ArgAt<CancellationToken>(4)));
        using RemoteFsCapability api = DownloadApi();
        try
        {
            var start = await api.StartDownloadAsync(_session.ToString(), "/source", path,
                new() { OperationId = trigger, Timeout = TimeSpan.FromMinutes(10) });
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (trigger == "disconnect") _connections.SessionDisconnected += Raise.Event<Action<SshSession>>(_connections.GetSession(_session)!);
            else api.Dispose();
            await WaitIdle(api);
            RemoteTransferSnapshot done = await api.CancelTransferAsync(start.TransferId, 1);
            Assert.AreEqual(expected, done.State, done.ErrorMessage);
            Assert.AreEqual(code, done.ErrorCode);
            Assert.IsTrue(done.RemoteStopConfirmed);
            Assert.IsTrue(done.DestinationCommitConfirmedAbsent);
            Assert.IsEmpty(Directory.GetFiles(directory));
        }
        finally { await WaitIdle(api); Directory.Delete(directory, true); }
    }

    [TestMethod]
    public async Task DownloadFinalSpeedIsMeasuredFromRunningNotFromApproval()
    {
        string directory = NewDirectory("vela-download-speed-");
        string path = Path.Combine(directory, "target");
        byte[] content = new byte[64 * 1024];
        IPluginPermissionPrompt prompt = Substitute.For<IPluginPermissionPrompt>();
        prompt.RequestFileDownloadAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(async _ => { await Task.Delay(1500); return PluginPermissionDecision.AllowOnce; });
        _sftp.GetFileInfoAsync(_session, "/source", Arg.Any<CancellationToken>()).Returns(SourceInfo(content.Length));
        _sftp.DownloadStreamAsync(_session, "/source", Arg.Any<Stream>(), Arg.Any<IProgress<TransferProgress>>(), Arg.Any<CancellationToken>())
            .Returns(async call => await call.ArgAt<Stream>(2).WriteAsync(content, call.ArgAt<CancellationToken>(4)));
        ConfigureRemoteDigest("/source", content);
        using RemoteFsCapability api = DownloadApi(prompt);
        try
        {
            var start = await api.StartDownloadAsync(_session.ToString(), "/source", path, new() { OperationId = "speed" });
            RemoteTransferSnapshot done = await Terminal(api, start.TransferId);
            Assert.AreEqual(RemoteTransferState.Succeeded, done.State, done.ErrorMessage);
            Assert.IsGreaterThan(done.TotalBytes / 0.75, done.SpeedBytesPerSecond!.Value);
        }
        finally { await WaitIdle(api); Directory.Delete(directory, true); }
    }

    /// <summary>经测试落盘目标驱动的下载能力:worker 的收尾分类与平台无关,任何系统上都能验证(NTFS 目标另有 Windows 专用用例)。</summary>
    private RemoteFsCapability DownloadApi(IPluginPermissionPrompt? prompt = null, TimeSpan? stopGrace = null) => stopGrace is { } grace
        ? new(_sftp, _connections, pluginId: "test.transfer", uploadPermissions: new(null, prompt ?? AllowDownloads()))
        {
            DownloadTargetFactory = (path, overwrite) => new PathDownloadTarget(path, overwrite),
            StopGracePeriod = grace,
        }
        : new(_sftp, _connections, pluginId: "test.transfer", uploadPermissions: new(null, prompt ?? AllowDownloads()))
        {
            DownloadTargetFactory = (path, overwrite) => new PathDownloadTarget(path, overwrite),
        };

    /// <summary>
    /// 不理取消的后端永远停不下来:这类用例只验证「宽限期过了仍记 Unknown」,把宽限期调短,免得每条都干等生产默认值。
    /// </summary>
    private static readonly TimeSpan UncooperativeGrace = TimeSpan.FromMilliseconds(100);

    private static IPluginPermissionPrompt AllowDownloads()
    {
        IPluginPermissionPrompt prompt = Substitute.For<IPluginPermissionPrompt>();
        prompt.RequestFileDownloadAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(PluginPermissionDecision.AllowOnce);
        return prompt;
    }

    private static RemoteFileInfo SourceInfo(long size) => new() { Name = "source", FullPath = "/source", Size = size, LastModified = DateTime.UnixEpoch, Permissions = "-rw-------", IsDirectory = false, Owner = "user", Group = "user" };

    /// <summary>让替身给出远端摘要(sha256sum 那样的小写十六进制),下载的摘要比对才真的会跑。</summary>
    private void ConfigureRemoteDigest(string remotePath, byte[] content) =>
        _sftp.ComputeSha256Async(_session, Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyDictionary<string, string?>>(new Dictionary<string, string?> { [remotePath] = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant() }));

    private static string NewDirectory(string prefix)
    {
        string directory = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    /// <summary>测试用落盘目标:同目录独占创建临时文件、按路径提交。只用来驱动 worker 的收尾分类,不冒充 NTFS 的句柄语义。</summary>
    private sealed class PathDownloadTarget : RemoteFsCapability.IDownloadTarget
    {
        private readonly string _destination;
        private readonly string _temporary;
        private FileStream? _stream;
        private bool _committed;

        internal PathDownloadTarget(string destination, bool overwrite)
        {
            if (File.Exists(destination) && !overwrite) throw new IOException("The destination already exists.");
            _destination = destination;
            _temporary = Path.Combine(Path.GetDirectoryName(destination)!, ".vela-download-test-" + Guid.NewGuid().ToString("N"));
        }

        public FileStream CreateTemporary() =>
            _stream = new FileStream(_temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 4096, FileOptions.Asynchronous);

        public void Commit(bool overwrite)
        {
            _stream!.Dispose();
            File.Move(_temporary, _destination, overwrite);
            _committed = true;
        }

        public async ValueTask DisposeAsync()
        {
            if (_stream is not null) await _stream.DisposeAsync();
            if (!_committed) File.Delete(_temporary);
        }
    }

    /// <summary>宿主 DI 里唯一的 <see cref="ISftpService" /> 是路由服务;SSH 会话落到给定的 SFTP 后端。</summary>
    private static RoutingRemoteFileService Routed(ISftpService sftp) => new(sftp, Substitute.For<ISftpService>(),
        Substitute.For<IFtpSessionService>(), Substitute.For<ISftpService>(), Substitute.For<IPluginProtocolSessionService>());

    private static PluginPermissionGate AllowUploads()
    {
        IPluginPermissionPrompt prompt = Substitute.For<IPluginPermissionPrompt>();
        prompt.RequestFileUploadAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(PluginPermissionDecision.AllowOnce);
        return new(null, prompt);
    }

    private static string CreateFile(long length)
    {
        string path = Path.Combine(Path.GetTempPath(), "vela-transfer-test-" + Guid.NewGuid().ToString("N"));
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
        file.SetLength(length);
        return path;
    }
    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// 合作的后端:收到取消后先花一点时间收尾再停下 —— 真实库取消上传时要等在途写入落地、发 CLOSE,
    /// 不会在取消的同一瞬间结束。
    /// </summary>
    private static async Task StopCooperatively(TaskCompletionSource started, CancellationToken token)
    {
        started.TrySetResult();
        try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
        catch (OperationCanceledException)
        {
            await Task.Delay(200, CancellationToken.None);
            throw;
        }
    }

    /// <summary>收集宿主写进 <see cref="Trace" /> 的日志;本程序集不并行跑测试,全局监听器不会串到别的用例。</summary>
    private sealed class TraceCapture : TraceListener
    {
        private readonly StringBuilder _text = new();
        public TraceCapture() => Trace.Listeners.Add(this);
        public string Text { get { lock (_text) return _text.ToString(); } }
        public override void Write(string? message) { lock (_text) _text.Append(message); }
        public override void WriteLine(string? message) { lock (_text) _text.AppendLine(message); }
        protected override void Dispose(bool disposing)
        {
            Trace.Listeners.Remove(this);
            base.Dispose(disposing);
        }
    }
    private static async Task<RemoteTransferSnapshot> Terminal(RemoteFsCapability api, string id)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        RemoteTransferSnapshot result = await api.GetTransferAsync(id, timeout.Token);
        while (!result.IsTerminal) result = await api.WaitTransferAsync(id, result.Sequence, TimeSpan.FromSeconds(1), timeout.Token);
        return result;
    }
    private static async Task WaitIdle(RemoteFsCapability api)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (api.ActiveTransfers > 0) await Task.Delay(10, timeout.Token);
    }
    private sealed class Pair : IAsyncDisposable
    {
        internal required RpcConnection _host;
        internal required RpcConnection _plugin;
        internal static async Task<Pair> Open()
        {
            string name = PluginProcessClient.CreatePipeName();
            var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
            Task waiting = server.WaitForConnectionAsync();
            await client.ConnectAsync(5000);
            await waiting;
            var pair = new Pair { _host = new(server), _plugin = new(client) };
            pair._host.Start(); pair._plugin.Start();
            return pair;
        }
        public async ValueTask DisposeAsync() { await _plugin.DisposeAsync(); await _host.DisposeAsync(); }
    }
    private static PluginContext Context(IRemoteFsApi fs) => new()
    {
        PluginId = "test.transfer",
        PluginVersion = "1",
        DataDirectory = Path.GetTempPath(),
        Host = new TestHostInfo(),
        Log = new CollectingLogger(),
        Storage = new InMemoryStorage(),
        TimeSeries = new InMemoryTimeSeries(),
        Sessions = new FakeSessions(),
        RemoteFs = fs,
        RemoteExec = new FakeRemoteExec(),
        RemoteTunnel = new FakeRemoteTunnel(),
        TerminalView = new FakeTerminalViewApi(),
        Commands = new RecordingCommands(),
        Events = new PluginEventHub(new CollectingLogger(), null, null, null),
        Theme = new StaticHostTheme(),
        Ui = new FakeUi(),
        Secrets = new FakeSecrets(),
        Clipboard = new FakeClipboard(),
        Terminal = new FakeTerminal(),
        Protocols = new UnavailableProtocols(),
        Workspaces = new UnavailableWorkspaces(),
        Shutdown = CancellationToken.None,
    };
}
