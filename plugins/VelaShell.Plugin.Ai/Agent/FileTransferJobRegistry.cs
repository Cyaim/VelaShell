using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using VelaShell.PluginSdk;
using VelaShell.PluginSdk.RemoteFs;

namespace VelaShell.Plugin.Ai.Agent;

/// <summary>后台线程发出的、只属于一个对话轮次的上传进度；撤权后不再包含路径或快照。</summary>
/// <param name="OwnerId">入口持有的稳定对话所有者。</param>
/// <param name="RunId">入口生成的运行代际。</param>
/// <param name="TransferId">不透明的宿主传输标识。</param>
/// <param name="LocalPath">已批准的本地源路径；撤权时为空。</param>
/// <param name="RemotePath">最终远端目标路径；撤权时为空。</param>
/// <param name="Snapshot">宿主权威快照；撤权时为空。</param>
/// <param name="Withheld">授权已撤销，必须隐藏旧元数据。</param>
/// <param name="MonitoringUnavailable">本地进度观察暂不可确认，不修改宿主状态或序号。</param>
public sealed record FileTransferJobUpdate(string OwnerId, string RunId, string TransferId,
    string LocalPath, string RemotePath, RemoteTransferSnapshot? Snapshot, bool Withheld = false,
    bool MonitoringUnavailable = false);

/// <summary>
/// 插件生命周期内共享的 AI 上传登记与本地进度监听。请求、模型与 run 取消不取消宿主任务；
/// 用户必须显式取消传输。插件停用由宿主及 context.Shutdown 收尾。
/// </summary>
public sealed class FileTransferJobRegistry
{
    private static readonly ConditionalWeakTable<IPluginContext, FileTransferJobRegistry> Shared = new();
    private readonly IRemoteFsApi _remoteFs;
    private readonly CancellationToken _lifetime;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Job> _jobs = new(StringComparer.Ordinal);
    private sealed record StartReceipt(FileTransferRunScope Scope, string Signature, Task<RemoteTransferSnapshot> Result);
    private readonly Dictionary<string, StartReceipt> _operations = new(StringComparer.Ordinal);

    private FileTransferJobRegistry(IPluginContext context)
    {
        _remoteFs = context.RemoteFs;
        _lifetime = context.Shutdown;
    }

    /// <summary>Bridge 每轮释放工具箱不会销毁这里的监听或取消已批准上传。</summary>
    public static FileTransferJobRegistry For(IPluginContext context)
        => Shared.GetValue(context, static key => new FileTransferJobRegistry(key));

    /// <summary>传输身份能力由入口生成，不接受模型指定 owner/run。</summary>
    public FileTransferRunScope BeginRun(string ownerId, string runId,
        Func<string, CancellationToken, Task<bool>>? authorize = null)
        => new(this, ownerId, runId, authorize);

    internal async Task<RemoteTransferSnapshot> StartAsync(FileTransferRunScope scope, string sessionId,
        string localPath, string remotePath, string operationId, bool overwrite, CancellationToken token)
    {
        if (!_remoteFs.SupportsTransferOperations)
            throw new NotSupportedException("This host does not support independent transfers; synchronous upload fallback is disabled.");
        await AuthorizeSessionAsync(scope, sessionId, token).ConfigureAwait(false);
        string signature = JsonSerializer.Serialize(new { sessionId, localPath, remotePath, overwrite });
        TaskCompletionSource<RemoteTransferSnapshot>? starter = null;
        Task<RemoteTransferSnapshot> receipt;
        lock (_gate)
        {
            if (_operations.TryGetValue(operationId, out StartReceipt? prior))
            {
                if (!ReferenceEquals(prior.Scope, scope) || prior.Signature != signature)
                    throw new InvalidOperationException("Upload operation identity conflict; do not replay this operation.");
                receipt = prior.Result;
            }
            else
            {
                // 先登记在途回执，调用者取消等待也不能把宿主持有的上传变成无主任务。
                if (_operations.Count >= 1024) throw new InvalidOperationException("The upload task budget is exhausted.");
                starter = new(TaskCreationOptions.RunContinuationsAsynchronously);
                receipt = starter.Task;
                _operations.Add(operationId, new(scope, signature, receipt));
                scope.PendingStarts++;
            }
        }
        if (starter is not null) _ = CompleteStartAsync(scope, sessionId, localPath, remotePath, operationId, overwrite, starter);
        RemoteTransferSnapshot result = await receipt.WaitAsync(token).ConfigureAwait(false);
        return Deliver(Find(scope, result.TransferId));
    }

    private async Task CompleteStartAsync(FileTransferRunScope scope, string sessionId, string localPath,
        string remotePath, string operationId, bool overwrite, TaskCompletionSource<RemoteTransferSnapshot> completion)
    {
        RemoteTransferSnapshot? result = null;
        Exception? failure = null;
        try
        {
            result = await ReceiveStartAsync(scope, sessionId, localPath, remotePath, operationId, overwrite).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            failure = ex;
            if (ex is not (ArgumentException or NotSupportedException or UnauthorizedAccessException or PluginPermissionDeniedException))
                scope.UncertainStart = true;
        }
        finally { lock (_gate) scope.PendingStarts--; }
        if (failure is null) completion.TrySetResult(result!);
        else
        {
            completion.TrySetException(failure);
            // 没有等待者的迟到错误也必须被观察；保留账本身份，禁止自动新建上传。
            _ = completion.Task.Exception;
        }
    }

    private async Task<RemoteTransferSnapshot> ReceiveStartAsync(FileTransferRunScope scope, string sessionId,
        string localPath, string remotePath, string operationId, bool overwrite)
    {
        RemoteTransferSnapshot snapshot;
        try
        {
            snapshot = await _remoteFs.StartUploadAsync(sessionId, localPath, remotePath,
                new RemoteUploadOptions { OperationId = operationId, Overwrite = overwrite }, _lifetime).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or TimeoutException)
        {
            // 短请求回执丢失时只用原 OperationId 重取一次；从不另造 id 重传。
            snapshot = await _remoteFs.StartUploadAsync(sessionId, localPath, remotePath,
                new RemoteUploadOptions { OperationId = operationId, Overwrite = overwrite }, _lifetime).ConfigureAwait(false);
        }
        Job job;
        lock (_gate)
        {
            if (!_jobs.TryGetValue(snapshot.TransferId, out job!))
            {
                job = new Job(scope, snapshot);
                _jobs.Add(snapshot.TransferId, job);
            }
            else if (!ReferenceEquals(job.Scope, scope))
                throw new InvalidOperationException("Transfer identity conflict.");
        }
        await PublishAsync(job, snapshot).ConfigureAwait(false);
        bool cancelLate;
        lock (_gate) cancelLate = scope.CancelAllRequested;
        if (cancelLate && NeedsCleanup(snapshot))
        {
            RemoteTransferSnapshot cancelled = await _remoteFs.CancelTransferAsync(snapshot.TransferId,
                snapshot.Generation, _lifetime).ConfigureAwait(false);
            await PublishAsync(job, cancelled).ConfigureAwait(false);
        }
        StartWatcher(job);
        // 后台收据登记不是模型读取；迟到终态仍必须由协调器消费一次。
        return snapshot;
    }

    internal async Task<RemoteTransferSnapshot> ReadAsync(FileTransferRunScope scope, string id, CancellationToken token,
        bool consume = true)
    {
        Job job = Find(scope, id);
        await AuthorizeAsync(job, token).ConfigureAwait(false);
        RemoteTransferSnapshot snapshot;
        try { snapshot = await _remoteFs.GetTransferAsync(id, token).ConfigureAwait(false); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or PluginPermissionDeniedException)
        {
            await WithholdAsync(job).ConfigureAwait(false);
            throw new UnauthorizedAccessException("Transfer metadata is withheld.");
        }
        bool recovered;
        lock (_gate)
        {
            recovered = job.MonitoringFailed;
            if (recovered)
            {
                job.MonitoringFailed = false;
                if (!snapshot.IsTerminal) job.Completion = Signal();
            }
        }
        await PublishAsync(job, snapshot, forceObservationUpdate: recovered).ConfigureAwait(false);
        await AuthorizeAsync(job, token).ConfigureAwait(false);
        StartWatcher(job);
        if (consume) return Deliver(job);
        lock (_gate) return job.Snapshot;
    }

    internal async Task<RemoteTransferSnapshot> WaitAsync(FileTransferRunScope scope, string id, TimeSpan wait,
        CancellationToken token)
    {
        if (wait < TimeSpan.Zero || wait > TimeSpan.FromSeconds(IRemoteFsApi.MaxTransferWaitSeconds))
            throw new ArgumentOutOfRangeException(nameof(wait));
        Job job = Find(scope, id);
        await AuthorizeAsync(job, token).ConfigureAwait(false);
        try { await job.Completion.Task.WaitAsync(wait, token).ConfigureAwait(false); }
        catch (TimeoutException) { /* 只是本地等待窗口，不是传输执行死线。 */ }
        await AuthorizeAsync(job, token).ConfigureAwait(false);
        return Deliver(job);
    }

    internal async Task CancelAsync(FileTransferRunScope scope, string id, long generation, CancellationToken token)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(generation);
        Job job = Find(scope, id);
        // 清理不交出返回的快照，且不受读取授权撤销阻挡。
        RemoteTransferSnapshot snapshot = await _remoteFs.CancelTransferAsync(id, generation, token).ConfigureAwait(false);
        await PublishAsync(job, snapshot).ConfigureAwait(false);
    }

    internal async Task<RemoteTransferSnapshot> ResumeAsync(FileTransferRunScope scope, string id,
        RemoteTransferResumeOptions options, CancellationToken token)
    {
        Job job = Find(scope, id);
        await AuthorizeAsync(job, token).ConfigureAwait(false);
        await AuthorizeSessionAsync(scope, options.TargetSessionId, token).ConfigureAwait(false);
        RemoteTransferSnapshot snapshot = await _remoteFs.ResumeTransferAsync(id, options, token).ConfigureAwait(false);
        lock (_gate)
        {
            if (snapshot.Generation > job.Snapshot.Generation)
            {
                job.Completion = Signal();
                job.Delivered = false;
            }
        }
        await PublishAsync(job, snapshot).ConfigureAwait(false);
        StartWatcher(job);
        return Deliver(job);
    }

    private Job Find(FileTransferRunScope scope, string id)
    {
        lock (_gate)
        {
            if (!_jobs.TryGetValue(id, out Job? job) || !ReferenceEquals(job.Scope, scope))
                throw new FileNotFoundException("No upload task with this id belongs to this run.");
            return job;
        }
    }

    private static async Task AuthorizeSessionAsync(FileTransferRunScope scope, string sessionId, CancellationToken token)
    {
        if (scope.Authorize is { } authorize && !await authorize(sessionId, token).ConfigureAwait(false))
            throw new UnauthorizedAccessException("Authorization for this transfer was revoked; metadata is withheld.");
    }

    private async Task AuthorizeAsync(Job job, CancellationToken token)
    {
        try { await AuthorizeSessionAsync(job.Scope, job.Snapshot.SessionId, token).ConfigureAwait(false); }
        catch (UnauthorizedAccessException)
        {
            await WithholdAsync(job).ConfigureAwait(false);
            throw;
        }
        lock (_gate)
            if (job.Withheld) throw new UnauthorizedAccessException("Transfer metadata is withheld.");
    }

    private async Task WithholdAsync(Job job)
    {
        bool first;
        lock (_gate) { first = !job.Withheld; job.Withheld = true; job.Completion.TrySetResult(); }
        if (!first) return;
        job.Scope.Publish(new(job.Scope.OwnerId, job.Scope.RunId, job.Snapshot.TransferId, "", "", null, true));
        try
        {
            await _remoteFs.CancelTransferAsync(job.Snapshot.TransferId, job.Snapshot.Generation, _lifetime).ConfigureAwait(false);
        }
        catch { /* 尽力清理；撤权后的元数据始终隐藏。 */ }
    }

    private async Task PublishAsync(Job job, RemoteTransferSnapshot snapshot, bool forceObservationUpdate = false)
    {
        try
        {
            await AuthorizeAsync(job, _lifetime).ConfigureAwait(false);
            await AuthorizeSessionAsync(job.Scope, snapshot.SessionId, _lifetime).ConfigureAwait(false);
        }
        catch (UnauthorizedAccessException) { await WithholdAsync(job).ConfigureAwait(false); return; }
        lock (_gate)
        {
            if (snapshot.Sequence < job.Snapshot.Sequence || job.Withheld) return;
            if (job.PublishedSequence >= snapshot.Sequence && !forceObservationUpdate) return;
            job.PublishedSequence = snapshot.Sequence;
            job.Snapshot = snapshot;
            if (snapshot.IsTerminal) job.Completion.TrySetResult();
        }
        job.Scope.Publish(new(job.Scope.OwnerId, job.Scope.RunId, snapshot.TransferId,
            snapshot.LocalPath, snapshot.RemotePath, snapshot));
    }

    private void StartWatcher(Job job)
    {
        lock (_gate)
        {
            if (job.Watching || job.Withheld || job.Snapshot.IsTerminal) return;
            job.Watching = true;
        }
        _ = WatchAsync(job);
    }

    private async Task WatchAsync(Job job)
    {
        int failures = 0;
        try
        {
            while (!_lifetime.IsCancellationRequested)
            {
                RemoteTransferSnapshot current;
                lock (_gate)
                {
                    current = job.Snapshot;
                    if (current.IsTerminal || job.Withheld) return;
                }
                try
                {
                    await AuthorizeAsync(job, _lifetime).ConfigureAwait(false);
                    RemoteTransferSnapshot next;
                    try
                    {
                        next = await _remoteFs.WaitTransferAsync(current.TransferId, current.Sequence,
                            TimeSpan.FromSeconds(IRemoteFsApi.MaxTransferWaitSeconds), _lifetime).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is not (OperationCanceledException or UnauthorizedAccessException or PluginPermissionDeniedException))
                    {
                        // 丢失进度等待不代表任务失败；回读成功后继续监听，不能悄悄停止并让完成等待永远挂起。
                        next = await _remoteFs.GetTransferAsync(current.TransferId, _lifetime).ConfigureAwait(false);
                        await Task.Delay(TimeSpan.FromMilliseconds(250), _lifetime).ConfigureAwait(false);
                    }
                    await PublishAsync(job, next).ConfigureAwait(false);
                    failures = 0;
                    if (next.Sequence <= current.Sequence && !next.IsTerminal)
                        await Task.Delay(TimeSpan.FromMilliseconds(100), _lifetime).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { return; }
                catch (Exception ex) when (ex is UnauthorizedAccessException or PluginPermissionDeniedException)
                { await WithholdAsync(job).ConfigureAwait(false); return; }
                catch (Exception)
                {
                    if (++failures < 3)
                    {
                        await Task.Delay(TimeSpan.FromMilliseconds(250 * failures), _lifetime).ConfigureAwait(false);
                        continue;
                    }
                    // 只结束本地观察，不取消宿主任务，也不把网络故障伪装成撤权。
                    lock (_gate)
                    {
                        // 本地观察失败独立于宿主 Sequence/终态，不能伪造版本或让清理漏掉仍运行的上传。
                        job.MonitoringFailed = true;
                        job.Scope.UncertainStart = true;
                        job.Completion.TrySetResult();
                    }
                    job.Scope.Publish(new(job.Scope.OwnerId, job.Scope.RunId, current.TransferId,
                        current.LocalPath, current.RemotePath, current, MonitoringUnavailable: true));
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        finally
        {
            bool restart;
            lock (_gate)
            {
                job.Watching = false;
                restart = !_lifetime.IsCancellationRequested && !job.MonitoringFailed
                    && !job.Withheld && !job.Snapshot.IsTerminal;
            }
            // Resume/显式查询可能与旧观察者收尾竞态；不能因旧代次仍持 Watching 而丢失新代次监听。
            if (restart) StartWatcher(job);
        }
    }

    private RemoteTransferSnapshot Deliver(Job job)
    {
        lock (_gate)
        {
            if (job.Withheld) throw new UnauthorizedAccessException("Transfer metadata is withheld.");
            if (job.Snapshot.IsTerminal || job.MonitoringFailed) job.Delivered = true;
            return job.MonitoringFailed ? MonitoringUnknown(job) : job.Snapshot;
        }
    }

    private static RemoteTransferSnapshot MonitoringUnknown(Job job) => job.Snapshot with
    {
        State = RemoteTransferState.Unknown, RemoteStopConfirmed = false, ErrorCode = "ProgressUnavailable",
        ErrorMessage = "Transfer progress cannot be confirmed. Do not automatically replay this operation."
    };

    private static bool NeedsCleanup(RemoteTransferSnapshot snapshot) => !snapshot.IsTerminal
        || (snapshot.State is RemoteTransferState.Unknown or RemoteTransferState.TimedOut && !snapshot.RemoteStopConfirmed);

    internal bool HasRunningTransfers(FileTransferRunScope scope)
    {
        lock (_gate) return scope.PendingStarts > 0
            || _jobs.Values.Any(j => ReferenceEquals(j.Scope, scope) && NeedsCleanup(j.Snapshot));
    }

    internal bool HasActiveJobs(FileTransferRunScope scope)
    {
        lock (_gate) return _jobs.Values.Any(j => ReferenceEquals(j.Scope, scope) && !j.Delivered);
    }

    internal async Task CancelAllAsync(FileTransferRunScope scope)
    {
        Job[] jobs;
        lock (_gate)
        {
            scope.CancelAllRequested = true;
            jobs = [.. _jobs.Values.Where(j => ReferenceEquals(j.Scope, scope) && NeedsCleanup(j.Snapshot))];
        }
        foreach (Job job in jobs)
        {
            try { await CancelAsync(scope, job.Snapshot.TransferId, job.Snapshot.Generation, _lifetime).ConfigureAwait(false); }
            catch { /* 宿主关闭或撤权可能已经收尾了任务。 */ }
        }
    }

    internal bool HasDeniedExecution(FileTransferRunScope scope)
    {
        lock (_gate) return _jobs.Values.Any(j => ReferenceEquals(j.Scope, scope)
            && j.Snapshot.State == RemoteTransferState.Failed && j.Snapshot.ErrorCode == "permission-denied");
    }

    internal bool HasUnknownOutcome(FileTransferRunScope scope)
    {
        if (scope.UncertainStart) return true;
        lock (_gate) return _jobs.Values.Any(j => ReferenceEquals(j.Scope, scope)
            && (j.Withheld || j.Snapshot.State == RemoteTransferState.Unknown
                || (j.Snapshot.State == RemoteTransferState.TimedOut && !j.Snapshot.RemoteStopConfirmed)));
    }

    internal async Task<IReadOnlyList<RemoteTransferSnapshot>> WaitForCompletionAsync(FileTransferRunScope scope,
        CancellationToken token)
    {
        while (true)
        {
            Job[] pending;
            lock (_gate) pending = [.. _jobs.Values.Where(j => ReferenceEquals(j.Scope, scope) && !j.Delivered)];
            if (pending.Length == 0) return [];
            await Task.WhenAny(pending.Select(j => j.Completion.Task)).WaitAsync(token).ConfigureAwait(false);
            List<RemoteTransferSnapshot> results = [];
            foreach (Job job in pending.Where(j => j.Completion.Task.IsCompleted))
            {
                try { await AuthorizeAsync(job, token).ConfigureAwait(false); }
                catch (UnauthorizedAccessException) { }
                lock (_gate)
                {
                    if (job.Delivered) continue;
                    job.Delivered = true;
                    results.Add(job.Withheld ? new RemoteTransferSnapshot
                    {
                        TransferId = job.Snapshot.TransferId, OperationId = "", SessionId = "", LocalPath = "", RemotePath = "",
                        State = RemoteTransferState.Unknown, ErrorCode = "ScopeRevoked",
                        ErrorMessage = "Transfer result is withheld. Do not automatically replay this operation."
                    } : job.MonitoringFailed ? MonitoringUnknown(job) : job.Snapshot);
                }
            }
            if (results.Count > 0) return results;
        }
    }

    internal static string StableOperationId(string ownerId, string runId, string callIdentity)
        => "ai-transfer-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(new { ownerId, runId, callIdentity })))).ToLowerInvariant();

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class Job(FileTransferRunScope scope, RemoteTransferSnapshot snapshot)
    {
        internal readonly FileTransferRunScope Scope = scope;
        internal RemoteTransferSnapshot Snapshot = snapshot;
        internal TaskCompletionSource Completion = Signal();
        internal bool Delivered;
        internal bool Withheld;
        internal bool Watching;
        internal bool MonitoringFailed;
        internal long PublishedSequence;
    }
}

/// <summary>所有者隔离的上传能力。模型 Stop/Dispose 不暗中取消上传；界面订阅必须显式释放。</summary>
public sealed class FileTransferRunScope : IAsyncDisposable
{
    private readonly FileTransferJobRegistry _registry;
    private readonly Lock _gate = new();
    private readonly List<Action<FileTransferJobUpdate>> _subscribers = [];
    internal readonly Func<string, CancellationToken, Task<bool>>? Authorize;
    internal volatile bool UncertainStart;
    internal int PendingStarts;
    internal bool CancelAllRequested;
    /// <summary>入口持有的稳定对话所有者。</summary>
    public string OwnerId { get; }
    /// <summary>入口生成的运行代际。</summary>
    public string RunId { get; }
    /// <summary>仍有尚未投递终态的上传。</summary>
    public bool HasActiveJobs => _registry.HasActiveJobs(this);
    /// <summary>宿主尚未报告终态的真实任务，不受模型通知是否消费影响。</summary>
    public bool HasRunningTransfers => _registry.HasRunningTransfers(this);
    /// <summary>宿主逐任务可信上传审批被拒绝；不是普通传输失败。</summary>
    public bool HasDeniedExecution => _registry.HasDeniedExecution(this);
    /// <summary>存在不可自动重放的不确定结果。</summary>
    public bool HasUnknownOutcome => _registry.HasUnknownOutcome(this);

    internal FileTransferRunScope(FileTransferJobRegistry registry, string ownerId, string runId,
        Func<string, CancellationToken, Task<bool>>? authorize)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        _registry = registry; OwnerId = ownerId; RunId = runId; Authorize = authorize;
    }

    /// <summary>订阅后台进度；调用者切到 UI 线程并显式释放返回的订阅。</summary>
    public IDisposable Subscribe(Action<FileTransferJobUpdate> callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        lock (_gate) _subscribers.Add(callback);
        return new Subscription(() => { lock (_gate) _subscribers.Remove(callback); });
    }

    internal void Publish(FileTransferJobUpdate update)
    {
        Action<FileTransferJobUpdate>[] callbacks;
        lock (_gate) callbacks = [.. _subscribers];
        foreach (Action<FileTransferJobUpdate> callback in callbacks)
            try { callback(update); } catch { /* 界面观察者不能破坏进度监听。 */ }
    }

    internal Task<RemoteTransferSnapshot> StartAsync(string sessionId, string localPath, string remotePath,
        string operationId, bool overwrite, CancellationToken token)
        => _registry.StartAsync(this, sessionId, localPath, remotePath, operationId, overwrite, token);
    /// <summary>重验当前授权后读取本轮任务；消费终态以避免重复模型唤醒。</summary>
    public Task<RemoteTransferSnapshot> ReadAsync(string transferId, CancellationToken cancellationToken = default)
        => _registry.ReadAsync(this, transferId, cancellationToken);
    /// <summary>供界面同步快照，不消费模型终态通知。</summary>
    public Task<RemoteTransferSnapshot> PeekAsync(string transferId, CancellationToken cancellationToken = default)
        => _registry.ReadAsync(this, transferId, cancellationToken, consume: false);
    /// <summary>只等本地完成信号，超时或取消本次等待不会取消上传。</summary>
    public Task<RemoteTransferSnapshot> WaitAsync(string transferId, TimeSpan wait,
        CancellationToken cancellationToken = default) => _registry.WaitAsync(this, transferId, wait, cancellationToken);
    /// <summary>显式取消指定执行代次，不返回撤权后的元数据。</summary>
    public Task CancelAsync(string transferId, long expectedGeneration, CancellationToken cancellationToken = default)
        => _registry.CancelAsync(this, transferId, expectedGeneration, cancellationToken);
    internal Task<RemoteTransferSnapshot> ResumeAsync(string transferId, RemoteTransferResumeOptions options,
        CancellationToken token) => _registry.ResumeAsync(this, transferId, options, token);
    /// <summary>原子消费一次终态通知；本地等待不调用模型。</summary>
    public Task<IReadOnlyList<RemoteTransferSnapshot>> WaitForCompletionAsync(CancellationToken cancellationToken = default)
        => _registry.WaitForCompletionAsync(this, cancellationToken);

    /// <summary>入口显式销毁/撤销授权时取消该轮次的未完成上传；模型 Stop 不调用它。</summary>
    public Task CancelAllAsync() => _registry.CancelAllAsync(this);

    /// <summary>释放模型观察者不取消宿主任务；UI 订阅由返回的 IDisposable 独立管理。</summary>
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private sealed class Subscription(Action unsubscribe) : IDisposable
    {
        private Action? _unsubscribe = unsubscribe;
        public void Dispose() => Interlocked.Exchange(ref _unsubscribe, null)?.Invoke();
    }
}
