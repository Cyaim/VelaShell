using System.Diagnostics;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using VelaShell.Core.Models;
using VelaShell.PluginSdk.RemoteFs;
using VelaShell.PluginSdk;
using RemoteUploadOptions = VelaShell.PluginSdk.RemoteFs.RemoteUploadOptions;

namespace VelaShell.Infrastructure.Plugins.Capabilities;

internal sealed partial class RemoteFsCapability
{
    public bool SupportsTransferOperations => true;
    internal bool UseDiskSnapshot { get; init; } = !OperatingSystem.IsWindows();
    private const int MaxRecords = 1024;
    private const int MaxActiveUploads = 32;
    private readonly Lock _transfersGate = new();
    private readonly Dictionary<string, UploadOperation> _transfers = [with(StringComparer.Ordinal)];
    private readonly Dictionary<string, StartReceipt> _receipts = [with(StringComparer.Ordinal)];
    private readonly SemaphoreSlim _uploadSlots = new(4);
    private bool _transfersDisposed;
    private bool _disconnectSubscribed;

    private sealed class SourceChangedException() : IOException("The approved source changed during upload.");
    private sealed record StartReceipt(string Session, string Local, string Remote, RemoteUploadOptions Options, UploadOperation Operation);
    private sealed class UploadOperation(RemoteTransferSnapshot snapshot, FileStream source, CancellationTokenSource stop)
    {
        internal readonly object Gate = new();
        internal RemoteTransferSnapshot Snapshot = snapshot;
        internal readonly FileStream Source = source;
        internal readonly CancellationTokenSource Stop = stop;
        internal TaskCompletionSource Changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Task Worker = Task.CompletedTask;
        internal readonly DateTime SourceModified = File.GetLastWriteTimeUtc(source.SafeFileHandle);
        internal readonly long SourceLength = source.Length;
        internal readonly long RegisteredTicks = Stopwatch.GetTimestamp();
        internal long LastProgress;
        internal bool StopDisposed;
        internal UploadPermissionLease? Permission;
        internal bool PermissionRevoked => Permission?.IsRevoked == true;
    }

    internal int ActiveTransfers
    {
        get { lock (_transfersGate) return _transfers.Values.Count(op => !Read(op).IsTerminal || !op.Worker.IsCompleted); }
    }

    public Task<RemoteTransferSnapshot> StartUploadAsync(string sessionId, string localPath, string remotePath,
        RemoteUploadOptions options, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.OperationId);
        if (options.OperationId.Length > 256) throw new ArgumentException("Operation id is too long.", nameof(options));
        if (options.Timeout is { } timeout && (timeout <= TimeSpan.Zero || timeout.TotalMilliseconds > uint.MaxValue - 1))
            throw new ArgumentOutOfRangeException(nameof(options), "Timeout must be positive and supported by the host timer.");
        string local = Path.GetFullPath(localPath);
        string remote = NormalizeDestination(remotePath);
        lock (_transfersGate)
        {
            ObjectDisposedException.ThrowIf(_transfersDisposed || lifetime.IsCancellationRequested, this);
            if (_receipts.TryGetValue(options.OperationId, out StartReceipt? previous))
            {
                if (previous.Session != sessionId || previous.Local != local || previous.Remote != remote || previous.Options != options)
                    throw new InvalidOperationException("Operation id was already used with different parameters.");
                return Task.FromResult(ReadAuthorized(previous.Operation));
            }
            Guid session = Resolve(sessionId);
            if (_receipts.Count >= MaxRecords) throw new InvalidOperationException("Transfer operation receipt quota reached for this plugin lifetime.");
            if (_transfers.Values.Count(op => !op.Worker.IsCompleted) >= MaxActiveUploads)
                throw new InvalidOperationException("Active upload queue quota reached for this plugin.");
            string resolved = ResolveUploadSource(local);
            RejectLinks(resolved);
            var source = new FileStream(resolved, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            try
            {
                RejectLinks(resolved);
                VerifyOpenedPath(source, resolved);
                var stop = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
                if (options.Timeout is { } budget) stop.CancelAfter(budget);
                DateTimeOffset now = DateTimeOffset.UtcNow;
                var op = new UploadOperation(new()
                {
                    TransferId = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)),
                    OperationId = options.OperationId,
                    SessionId = sessionId,
                    LocalPath = resolved,
                    RemotePath = remote,
                    State = RemoteTransferState.Queued,
                    Generation = 1,
                    Sequence = 1,
                    TotalBytes = source.Length,
                    CreatedAt = now,
                    UpdatedAt = now,
                    DestinationCommitConfirmedAbsent = true,
                }, source, stop);
                if (uploadPermissions is not null && !string.IsNullOrWhiteSpace(pluginId))
                    op.Permission = uploadPermissions.CreateUploadLease(pluginId);
                if (!_disconnectSubscribed)
                {
                    connections.SessionDisconnected += OnTransferSessionDisconnected;
                    _disconnectSubscribed = true;
                }
                _transfers.Add(op.Snapshot.TransferId, op);
                _receipts.Add(options.OperationId, new(sessionId, local, remote, options, op));
                // 请求令牌只控制回执等待,绝不链接到传输寿命。
                op.Worker = Task.Run(() => RunUploadAsync(op, session, options), CancellationToken.None);
                _ = op.Worker.ContinueWith(static task => _ = task.Exception, CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                return Task.FromResult(Read(op));
            }
            catch { source.Dispose(); throw; }
        }
    }

    public Task<RemoteTransferSnapshot> GetTransferAsync(string transferId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(ReadAuthorized(FindTransfer(transferId)));
    }

    public async Task<RemoteTransferSnapshot> WaitTransferAsync(string transferId, long afterSequence, TimeSpan waitTimeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(afterSequence);
        if (waitTimeout < TimeSpan.Zero || waitTimeout > TimeSpan.FromSeconds(IRemoteFsApi.MaxTransferWaitSeconds))
            throw new ArgumentOutOfRangeException(nameof(waitTimeout));
        cancellationToken.ThrowIfCancellationRequested();
        UploadOperation op = FindTransfer(transferId);
        Task changed;
        lock (op.Gate)
        {
            if (op.Snapshot.Sequence > afterSequence || op.Snapshot.IsTerminal || waitTimeout == TimeSpan.Zero) return ReadAuthorized(op);
            changed = op.Changed.Task;
        }
        try { await changed.WaitAsync(waitTimeout, cancellationToken).ConfigureAwait(false); }
        catch (TimeoutException) { }
        return ReadAuthorized(FindTransfer(transferId));
    }

    public Task<RemoteTransferSnapshot> CancelTransferAsync(string transferId, long expectedGeneration,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        UploadOperation op = FindTransfer(transferId, allowRevokedCleanup: true);
        lock (op.Gate)
        {
            if (expectedGeneration <= 0 || expectedGeneration != op.Snapshot.Generation)
                throw new InvalidOperationException("Transfer generation does not match.");
            if ((!op.Snapshot.IsTerminal || !op.Snapshot.RemoteStopConfirmed && !op.Worker.IsCompleted)
                && !op.Snapshot.CancellationRequested)
            {
                Update(op, s => s with { State = s.IsTerminal ? s.State : RemoteTransferState.CancelRequested, CancellationRequested = true });
                RequestStop(op);
            }
            return Task.FromResult(op.PermissionRevoked ? op.Snapshot with { LocalPath = "", RemotePath = "", SessionId = "" } : op.Snapshot);
        }
    }

    public Task<RemoteTransferSnapshot> ResumeTransferAsync(string transferId, RemoteTransferResumeOptions options,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.OperationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.TargetSessionId);
        UploadOperation op = FindTransfer(transferId);
        if (options.ExpectedGeneration <= 0 || options.ExpectedGeneration != Read(op).Generation)
            throw new InvalidOperationException("Transfer generation does not match.");
        lock (_transfersGate)
        {
            if (_receipts.ContainsKey(options.OperationId))
                throw new InvalidOperationException("Operation id was already used for an upload.");
        }
        // 连接接口尚未暴露认证后的服务器密钥、账户与命名空间身份。
        // 地址或会话 id 都不能证明重连后的恢复检查点仍属于同一远端。
        throw new NotSupportedException("This host cannot verify the server identity and remote namespace required for safe resume.");
    }

    private static RemoteTransferSnapshot ReadAuthorized(UploadOperation op)
    {
        lock (op.Gate) { DemandSnapshotAccess(op); return op.Snapshot; }
    }

    private static void DemandSnapshotAccess(UploadOperation op)
    {
        if (op.PermissionRevoked) throw new PluginPermissionDeniedException("Upload authorization was revoked.");
    }

    private UploadOperation FindTransfer(string transferId, bool allowRevokedCleanup = false)
    {
        UploadOperation? op;
        lock (_transfersGate)
        {
            if (transferId is null || !_transfers.TryGetValue(transferId, out op))
                throw new FileNotFoundException("Transfer not found.");
        }
        // 归属边界是每插件独立的能力实例;断线后仍可查保守终态,撤销后不能泄漏路径。
        if (!allowRevokedCleanup) DemandSnapshotAccess(op);
        return op;
    }

    private static string NormalizeDestination(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!path.StartsWith('/') || path.EndsWith('/') || path.Contains('\0'))
            throw new ArgumentException("Upload destination must be an absolute file path.", nameof(path));
        var parts = new List<string>();
        foreach (string part in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == ".") continue;
            if (part == "..") { if (parts.Count == 0) throw new ArgumentException("Destination escapes root."); parts.RemoveAt(parts.Count - 1); }
            else parts.Add(part);
        }
        if (parts.Count == 0) throw new ArgumentException("Destination must name a file.", nameof(path));
        return "/" + string.Join('/', parts);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle handle, StringBuilder path, uint length, uint flags);

    [LibraryImport("libc", EntryPoint = "fcntl", SetLastError = true)]
    private static partial int GetDarwinFilePath(SafeFileHandle handle, int command, [Out] byte[] path);

    [LibraryImport("libc", EntryPoint = "fcntl", SetLastError = true)]
    private static partial int GetDarwinArm64FilePath(SafeFileHandle handle, int command,
        nint unused2, nint unused3, nint unused4, nint unused5, nint unused6, nint unused7, [Out] byte[] path);

    private static void VerifyOpenedPath(FileStream source, string expected)
    {
        string actual;
        if (OperatingSystem.IsWindows())
        {
            var path = new StringBuilder(32768);
            uint length = GetFinalPathNameByHandle(source.SafeFileHandle, path, (uint)path.Capacity, 0);
            if (length == 0 || length >= path.Capacity) throw new IOException("Cannot verify the opened source object.");
            actual = path.ToString();
            if (actual.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) actual = @"\\" + actual[8..];
            else if (actual.StartsWith(@"\\?\", StringComparison.Ordinal)) actual = actual[4..];
        }
        else if (OperatingSystem.IsLinux())
        {
            actual = File.ResolveLinkTarget($"/proc/self/fd/{source.SafeFileHandle.DangerousGetHandle().ToInt64()}", false)?.FullName
                ?? throw new IOException("Cannot verify the opened source object.");
        }
        else if (OperatingSystem.IsMacOS())
        {
            // Darwin sys/fcntl.h: F_GETPATH = 50;缓冲大于 MAXPATHLEN,失败时不降级路径检查。
            byte[] path = new byte[4096];
            // Apple arm64 的变参在栈上传递，填满 x2–x7 后将缓冲指针放到首个栈槽。
            int result = RuntimeInformation.ProcessArchitecture == Architecture.Arm64
                ? GetDarwinArm64FilePath(source.SafeFileHandle, 50, 0, 0, 0, 0, 0, 0, path)
                : GetDarwinFilePath(source.SafeFileHandle, 50, path);
            if (result == -1)
                throw new IOException("Cannot verify the opened source object.");
            int end = Array.IndexOf(path, (byte)0);
            if (end <= 0) throw new IOException("Cannot verify the opened source object.");
            actual = new UTF8Encoding(false, true).GetString(path, 0, end);
        }
        else throw new PlatformNotSupportedException("Stable upload source verification is unavailable.");
        StringComparison comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!string.Equals(Path.GetFullPath(actual), expected, comparison))
            throw new IOException("The opened source does not match the approved path.");
    }

    internal static string ResolveUploadSource(string path)
    {
        path = Path.GetFullPath(path);
        string parent = Path.GetDirectoryName(path) ?? throw new IOException("The upload source must name a file.");
        int links = 0;
        return Path.Combine(ResolveUploadDirectory(parent, ref links), Path.GetFileName(path));
    }

    private static string ResolveUploadDirectory(string path, ref int links)
    {
        string root = Path.GetPathRoot(path) ?? throw new IOException("The upload source must be absolute.");
        string current = root;
        foreach (string part in path[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == ".") continue;
            if (part == "..") { current = Path.GetDirectoryName(current) ?? root; continue; }
            var directory = new DirectoryInfo(Path.Combine(current, part));
            if (directory.LinkTarget is { } target)
            {
                if (++links > 40) throw new IOException("Too many links in the upload source path.");
                // 链接内容中的 .. 必须在前面的链接已展开后处理，不能先 GetFullPath 折叠。
                current = ResolveUploadDirectory(Path.IsPathRooted(target) ? target : Path.Combine(current, target), ref links);
            }
            else
            {
                if (!directory.Exists || (directory.Attributes & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Cannot verify the upload source directory.");
                current = directory.FullName;
            }
        }
        return current;
    }

    private static void RejectLinks(string path)
    {
        for (string? current = path; current is not null; current = Path.GetDirectoryName(current))
        {
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new NotSupportedException("Linked source paths are not supported for stable uploads.");
        }
    }

    private static RemoteTransferSnapshot Read(UploadOperation op) { lock (op.Gate) return op.Snapshot; }
    private static void Update(UploadOperation op, Func<RemoteTransferSnapshot, RemoteTransferSnapshot> update)
    {
        lock (op.Gate)
        {
            op.Snapshot = update(op.Snapshot) with { Sequence = op.Snapshot.Sequence + 1, UpdatedAt = DateTimeOffset.UtcNow };
            TaskCompletionSource changed = op.Changed;
            op.Changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
            changed.TrySetResult();
        }
    }

    private sealed class UploadProgressSink(UploadOperation op) : IProgress<TransferProgress>
    {
        public void Report(TransferProgress value)
        {
            long now = Environment.TickCount64;
            lock (op.Gate)
            {
                if (op.Snapshot.IsTerminal || now - op.LastProgress < 100) return;
                op.LastProgress = now;
                Update(op, s => ApplyTransferProgress(s, value));
            }
        }
    }

    internal static RemoteTransferSnapshot ApplyTransferProgress(RemoteTransferSnapshot snapshot, TransferProgress value) => snapshot with
    {
        TransferredBytes = Math.Clamp(value.BytesTransferred, snapshot.TransferredBytes, snapshot.TotalBytes),
        SpeedBytesPerSecond = value.SpeedBytesPerSecond > 0 ? value.SpeedBytesPerSecond : null,
        EstimatedTimeRemaining = value.SpeedBytesPerSecond > 0 ? value.EstimatedTimeRemaining : null,
    };

    private async Task RunUploadAsync(UploadOperation op, Guid session, RemoteUploadOptions options)
    {
        string temporary = Read(op).RemotePath + ".vela-upload-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        bool slot = false, writing = false, closed = false, committing = false;
        Task? remoteWrite = null;
        Task? commitTask = null;
        FileStream? diskSnapshot = null;
        string? snapshotDirectory = null;
        var elapsed = Stopwatch.StartNew();
        using var execution = CancellationTokenSource.CreateLinkedTokenSource(op.Stop.Token, op.Permission?.Revocation ?? CancellationToken.None);
        CancellationToken ct = execution.Token;
        using var heartbeatStop = new CancellationTokenSource();
        Task heartbeat = TransferHeartbeatAsync(op, heartbeatStop.Token);
        try
        {
            Update(op, s => s with { State = RemoteTransferState.Starting });
            if (uploadPermissions is null || op.Permission is null || string.IsNullOrWhiteSpace(pluginId))
                throw new PluginPermissionDeniedException("A trusted upload approval is required.");
            SshSession target = connections.GetSession(session) ?? throw new PluginSessionNotFoundException(Read(op).SessionId);
            string label = $"{target.ConnectionInfo.Username}@{target.ConnectionInfo.Host}:{target.ConnectionInfo.Port}";
            bool approved = await uploadPermissions.CheckUploadAsync(op.Permission, pluginId, label, Read(op).LocalPath,
                Read(op).RemotePath, options.Overwrite, ct).ConfigureAwait(false);
            if (!approved) throw new PluginPermissionDeniedException("The user denied this upload.");
            ct.ThrowIfCancellationRequested();
            ValidateSource(op);
            Update(op, s => s with { State = RemoteTransferState.Queued });
            await _uploadSlots.WaitAsync(ct).ConfigureAwait(false);
            slot = true;
            ct.ThrowIfCancellationRequested();
            Resolve(Read(op).SessionId);
            Update(op, s => s with { State = RemoteTransferState.Starting });
            Stream uploadSource = op.Source;
            if (UseDiskSnapshot)
            {
                snapshotDirectory = Path.Combine(Path.GetTempPath(), "vela-upload-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(24)));
                if (OperatingSystem.IsWindows()) Directory.CreateDirectory(snapshotDirectory);
                else Directory.CreateDirectory(snapshotDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                var snapshotOptions = new FileStreamOptions
                {
                    Mode = FileMode.CreateNew,
                    Access = FileAccess.ReadWrite,
                    Share = FileShare.None,
                    BufferSize = 128 * 1024,
                    Options = FileOptions.Asynchronous | FileOptions.DeleteOnClose | FileOptions.SequentialScan,
                };
                if (!OperatingSystem.IsWindows()) snapshotOptions.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                diskSnapshot = new FileStream(Path.Combine(snapshotDirectory, "source"), snapshotOptions);
                byte[] buffer = new byte[128 * 1024];
                long last = Environment.TickCount64;
                int count;
                while ((count = await op.Source.ReadAsync(buffer, ct).ConfigureAwait(false)) != 0)
                {
                    await diskSnapshot.WriteAsync(buffer.AsMemory(0, count), ct).ConfigureAwait(false);
                    if (Environment.TickCount64 - last >= 100)
                    {
                        last = Environment.TickCount64;
                        Update(op, s => s); // 准备阶段心跳,字节数仍专指远端已确认写入。
                    }
                }
                await diskSnapshot.FlushAsync(ct).ConfigureAwait(false);
                diskSnapshot.Position = 0;
                op.Source.Position = 0;
                byte[] snapshotHash = await SHA256.HashDataAsync(diskSnapshot, ct).ConfigureAwait(false);
                byte[] sourceHash = await SHA256.HashDataAsync(op.Source, ct).ConfigureAwait(false);
                if (!CryptographicOperations.FixedTimeEquals(snapshotHash, sourceHash)) throw new SourceChangedException();
                diskSnapshot.Position = 0;
                uploadSource = diskSnapshot;
            }
            ValidateSource(op);
            byte[] hash = await SHA256.HashDataAsync(uploadSource, ct).ConfigureAwait(false);
            uploadSource.Position = 0;
            ct.ThrowIfCancellationRequested();
            Update(op, s => s with { State = RemoteTransferState.Running });
            writing = true;
            remoteWrite = sftp.UploadStreamAsync(session, uploadSource, temporary, op.SourceLength,
                progress: new UploadProgressSink(op), cancellationToken: ct);
            await remoteWrite.WaitAsync(ct).ConfigureAwait(false);
            closed = true;
            Update(op, s => s with { TransferredBytes = s.TotalBytes, SpeedBytesPerSecond = null, EstimatedTimeRemaining = null });
            // 优先服务端摘要;纯 SFTP 账号无 exec 时流式回读校验,不把满字节误报为已提交。
            byte[]? actual = null;
            try
            {
                IReadOnlyDictionary<string, string?> hashes = await sftp.ComputeSha256Async(session, [temporary], ct).ConfigureAwait(false);
                if (hashes is not null && hashes.TryGetValue(temporary, out string? text) && text is { Length: 64 })
                    actual = Convert.FromHexString(text);
            }
            catch (NotSupportedException) { }
            catch (FormatException) { }
            if (actual is null)
            {
                await using Stream remote = await sftp.OpenReadAsync(session, temporary, ct).ConfigureAwait(false);
                actual = await SHA256.HashDataAsync(remote, ct).ConfigureAwait(false);
            }
            if (!CryptographicOperations.FixedTimeEquals(hash, actual)) throw new InvalidDataException("Uploaded content verification failed.");
            ct.ThrowIfCancellationRequested();
            Resolve(Read(op).SessionId);
            ValidateSource(op);
            DemandSnapshotAccess(op);
            ct.ThrowIfCancellationRequested();
            committing = true;
            commitTask = sftp.CommitUploadAsync(session, temporary, Read(op).RemotePath, options.Overwrite, ct);
            await commitTask.WaitAsync(ct).ConfigureAwait(false);
            Update(op, s => s with
            {
                State = RemoteTransferState.Succeeded,
                TransferredBytes = s.TotalBytes,
                CompletedAt = DateTimeOffset.UtcNow,
                RemoteStopConfirmed = true,
                DestinationCommitConfirmedAbsent = false,
                SpeedBytesPerSecond = elapsed.Elapsed.TotalSeconds > 0 ? s.TotalBytes / elapsed.Elapsed.TotalSeconds : null,
                EstimatedTimeRemaining = TimeSpan.Zero,
            });
        }
        catch (Exception ex)
        {
            bool stopped = (!writing || closed) && !committing;
            bool timeout = options.Timeout is not null && op.Stop.IsCancellationRequested && !Read(op).CancellationRequested
                && !lifetime.IsCancellationRequested && !op.PermissionRevoked;
            Update(op, s => s with
            {
                State = timeout ? RemoteTransferState.TimedOut : committing || !stopped ? RemoteTransferState.Unknown
                    : ex is PluginPermissionDeniedException || op.PermissionRevoked ? RemoteTransferState.Failed
                    : ct.IsCancellationRequested ? RemoteTransferState.Cancelled : RemoteTransferState.Failed,
                RemoteStopConfirmed = stopped,
                DestinationCommitConfirmedAbsent = !committing,
                CompletedAt = DateTimeOffset.UtcNow,
                Resumable = false,
                ErrorCode = op.PermissionRevoked ? "permission-revoked" : ex is PluginPermissionDeniedException ? "permission-denied"
                    : committing ? "commit-unknown" : timeout ? "timeout" : ct.IsCancellationRequested ? "cancelled"
                    : ex is SourceChangedException ? "source-changed" : "upload-failed",
                ErrorMessage = op.PermissionRevoked ? "Upload authorization was revoked."
                    : ex is PluginPermissionDeniedException ? "A trusted host upload approval was not granted."
                    : committing ? "The destination commit could not be confirmed; do not retry automatically."
                    : ex is SourceChangedException ? "The approved source changed; the destination was not committed."
                    : ex is InvalidDataException ? "Uploaded content verification failed." : "Upload did not complete successfully.",
            });
        }
        finally
        {
            try
            {
                heartbeatStop.Cancel();
                await heartbeat.ConfigureAwait(false);
            }
            finally
            {
                // 取消只中止等待时继续保留稳定源句柄,直到后端真正结束;状态保持 Unknown,不冒充已停止。
                if (remoteWrite is not null) { try { await remoteWrite.ConfigureAwait(false); } catch { } }
                if (commitTask is not null) { try { await commitTask.ConfigureAwait(false); } catch { } }
                try
                {
                    if (diskSnapshot is not null) await diskSnapshot.DisposeAsync().ConfigureAwait(false);
                }
                finally
                {
                    if (snapshotDirectory is not null) { try { Directory.Delete(snapshotDirectory); } catch { } }
                    try { await op.Source.DisposeAsync().ConfigureAwait(false); }
                    finally
                    {
                        try { if (slot) _uploadSlots.Release(); }
                        finally
                        {
                            op.Permission?.Active = false;
                            lock (op.Gate)
                            {
                                op.StopDisposed = true;
                                op.Stop.Dispose();
                            }
                        }
                    }
                }
            }
            // 未确认写入/提交不删除、不开放恢复;确认无提交的残留才尽力清理。
            if (writing && Read(op) is { IsSuccess: false, RemoteStopConfirmed: true, DestinationCommitConfirmedAbsent: true })
            {
                Task? cleanupTask = null;
                try
                {
                    using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    cleanupTask = sftp.DeleteAsync(session, temporary, cancellationToken: cleanup.Token);
                    await cleanupTask.WaitAsync(cleanup.Token).ConfigureAwait(false);
                }
                catch
                {
                    if (cleanupTask is not null)
                        _ = cleanupTask.ContinueWith(static task => _ = task.Exception, CancellationToken.None,
                            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                }
            }
        }
    }

    private static async Task TransferHeartbeatAsync(UploadOperation op, CancellationToken ct)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                lock (op.Gate)
                {
                    if (op.Snapshot.IsTerminal) return;
                    if (op.Snapshot.State == RemoteTransferState.Starting
                        || op.Snapshot.State == RemoteTransferState.Running && op.Snapshot.TransferredBytes == op.Snapshot.TotalBytes)
                        Update(op, s => s);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    private static void RequestStop(UploadOperation op)
    {
        lock (op.Gate)
            if (!op.StopDisposed) op.Stop.Cancel();
    }

    private static void ValidateSource(UploadOperation op)
    {
        if (op.Source.Length != op.SourceLength || File.GetLastWriteTimeUtc(op.Source.SafeFileHandle) != op.SourceModified)
            throw new SourceChangedException();
    }

    private void OnTransferSessionDisconnected(SshSession session)
    {
        lock (_transfersGate)
            foreach (UploadOperation op in _transfers.Values)
                if (!op.Worker.IsCompleted && Read(op).SessionId == session.SessionId.ToString())
                    RequestStop(op);
    }

    public void Dispose()
    {
        lock (_transfersGate)
        {
            if (_transfersDisposed) return;
            _transfersDisposed = true;
            if (_disconnectSubscribed) connections.SessionDisconnected -= OnTransferSessionDisconnected;
            foreach (UploadOperation op in _transfers.Values)
            {
                if (!op.Worker.IsCompleted)
                {
                    RequestStop(op);
                    _ = op.Worker.ContinueWith(_ => op.Permission?.Dispose(), CancellationToken.None,
                        TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                }
                else op.Permission?.Dispose();
            }
        }
    }
}
