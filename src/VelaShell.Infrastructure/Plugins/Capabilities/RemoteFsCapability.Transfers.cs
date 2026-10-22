using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;
using VelaShell.Core.Models;
using VelaShell.Core.Ssh;
using VelaShell.PluginSdk;
using VelaShell.PluginSdk.RemoteFs;
using VelaShell.Ssh.Diagnostics;
using RemoteUploadOptions = VelaShell.PluginSdk.RemoteFs.RemoteUploadOptions;

namespace VelaShell.Infrastructure.Plugins.Capabilities;

internal sealed partial class RemoteFsCapability
{
    public bool SupportsTransferOperations => true;
    internal bool UseDiskSnapshot { get; init; } = !OperatingSystem.IsWindows();

    /// <summary>
    /// 等待被取消、到期、断线或撤权打断后,给仍在跑的后端任务真正停下的有界宽限期:宽限期内停下了,才能确认停写、
    /// 清掉临时文件;仍没停才记 Unknown。测试可调短(与 <see cref="UseDiskSnapshot" /> 同一用意)。
    /// </summary>
    internal TimeSpan StopGracePeriod { get; init; } = TimeSpan.FromSeconds(5);

    private const int MaxRecords = 1024;
    private const int MaxActiveUploads = 32;
    private readonly Lock _transfersGate = new();
    private readonly Dictionary<string, UploadOperation> _transfers = [with(StringComparer.Ordinal)];
    private readonly Dictionary<string, StartReceipt> _receipts = [with(StringComparer.Ordinal)];
    private readonly SemaphoreSlim _uploadSlots = new(4);
    private bool _transfersDisposed;
    private bool _disconnectSubscribed;

    private sealed class SourceChangedException() : IOException("The approved source changed during upload.");
    private sealed record StartReceipt(string Session, string Local, string Remote, object Options, UploadOperation Operation);

    /// <summary>
    /// 任务被要求停下的原因,先到者为准。死线、断线、插件停用与用户取消取消的是同一个停止令牌,
    /// 终态必须按登记的原因分类,不能从「令牌被取消了」倒推成到期或用户取消。
    /// </summary>
    private enum StopReason { None, User, Timeout, Disconnected, Disposed }

    private sealed class UploadOperation(RemoteTransferSnapshot snapshot, FileStream? source, CancellationTokenSource stop)
    {
        internal readonly object _gate = new();
        internal RemoteTransferSnapshot _snapshot = snapshot;
        internal readonly FileStream _source = source!;
        internal readonly CancellationTokenSource _stop = stop;
        internal TaskCompletionSource _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Task _worker = Task.CompletedTask;
        internal readonly DateTime _sourceModified = source is null ? default : File.GetLastWriteTimeUtc(source.SafeFileHandle);
        internal readonly long _sourceLength = source?.Length ?? -1;
        internal readonly long _registeredTicks = Stopwatch.GetTimestamp();
        internal long _lastProgress;
        internal bool _stopDisposed;
        internal StopReason _stopReason;
        internal UploadPermissionLease? _permission;
        internal DownloadPermissionLease? _downloadPermission;
        internal bool PermissionRevoked => _permission?.IsRevoked == true || _downloadPermission?.IsRevoked == true;
    }

    internal int ActiveTransfers
    {
        get { lock (_transfersGate) return _transfers.Values.Count(op => !Read(op).IsTerminal || !op._worker.IsCompleted); }
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
        Guid session;
        lock (_transfersGate)
        {
            if (FindUploadReceipt(sessionId, local, remote, options) is { } previous) return Task.FromResult(previous);
            session = Resolve(sessionId);
            ThrowIfUploadQuotaReached();
        }
        // 解析、打开与核验源都是阻塞 I/O(没有写端的 FIFO 能让 open 一直等下去),一律放在账本锁外:
        // 锁里只做回执查重与登记,同插件的查询、取消、断线回调与停用不会陪着卡住。
        string resolved = ResolveUploadSource(local);
        RejectLinks(resolved);
        RejectSpecialSource(resolved);
        var source = new FileStream(resolved, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        try
        {
            RejectLinks(resolved);
            VerifyOpenedPath(source, resolved);
            // 打开前按路径查过类型,打开后再按句柄核一次:两次之间路径被换成设备也拦得住。
            if (!OperatingSystem.IsWindows() && source.SafeFileHandle.Type != FileHandleType.RegularFile)
                throw new NotSupportedException("Only regular files can be uploaded.");
            lock (_transfersGate)
            {
                // 锁外打开源的这段时间里,同一 OperationId 的并发启动可能已经先登记:沿用它的回执,本次打开的句柄作废。
                if (FindUploadReceipt(sessionId, local, remote, options) is { } raced)
                {
                    source.Dispose();
                    return Task.FromResult(raced);
                }
                ThrowIfUploadQuotaReached();
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
                    op._permission = uploadPermissions.CreateUploadLease(pluginId);
                if (!_disconnectSubscribed)
                {
                    connections.SessionDisconnected += OnTransferSessionDisconnected;
                    _disconnectSubscribed = true;
                }
                _transfers.Add(op._snapshot.TransferId, op);
                _receipts.Add(options.OperationId, new(sessionId, local, remote, options, op));
                // 请求令牌只控制回执等待,绝不链接到传输寿命。
                op._worker = Task.Run(() => RunUploadAsync(op, session, options), CancellationToken.None);
                _ = op._worker.ContinueWith(static task => _ = task.Exception, CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                return Task.FromResult(Read(op));
            }
        }
        catch { source.Dispose(); throw; }
    }

    /// <summary>
    /// 同一 OperationId 已登记过时按幂等规则重取回执(方向或参数不符一律拒绝,绝不重新打开源);没有回执时为 null。
    /// 调用方须持有 <see cref="_transfersGate" />。
    /// </summary>
    private RemoteTransferSnapshot? FindUploadReceipt(string sessionId, string local, string remote, RemoteUploadOptions options)
    {
        ObjectDisposedException.ThrowIf(_transfersDisposed || lifetime.IsCancellationRequested, this);
        if (!_receipts.TryGetValue(options.OperationId, out StartReceipt? previous)) return null;
        if (previous.Operation._snapshot.Direction != RemoteTransferDirection.Upload || previous.Session != sessionId || previous.Local != local || previous.Remote != remote || !Equals(previous.Options, options))
            throw new InvalidOperationException("Operation id was already used with different parameters.");
        return ReadAuthorized(previous.Operation);
    }

    /// <summary>回执与活动任务额度;调用方须持有 <see cref="_transfersGate" />。</summary>
    private void ThrowIfUploadQuotaReached()
    {
        if (_receipts.Count >= MaxRecords) throw new InvalidOperationException("Transfer operation receipt quota reached for this plugin lifetime.");
        if (_transfers.Values.Count(op => !op._worker.IsCompleted) >= MaxActiveUploads)
            throw new InvalidOperationException("Active upload queue quota reached for this plugin.");
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
        lock (op._gate)
        {
            if (op._snapshot.Sequence > afterSequence || op._snapshot.IsTerminal || waitTimeout == TimeSpan.Zero) return ReadAuthorized(op);
            changed = op._changed.Task;
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
        lock (op._gate)
        {
            if (expectedGeneration <= 0 || expectedGeneration != op._snapshot.Generation)
                throw new InvalidOperationException("Transfer generation does not match.");
            if ((!op._snapshot.IsTerminal || !op._snapshot.RemoteStopConfirmed && !op._worker.IsCompleted)
                && !op._snapshot.CancellationRequested)
            {
                Update(op, s => s with { State = s.IsTerminal ? s.State : RemoteTransferState.CancelRequested, CancellationRequested = true });
                RequestStop(op, StopReason.User);
            }
            // 撤权后只交还清理所需的状态:路径、会话与失败说明(其中可能带着路径)一律不给。
            return Task.FromResult(op.PermissionRevoked ? op._snapshot with { LocalPath = "", RemotePath = "", SessionId = "", ErrorMessage = null } : op._snapshot);
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
        lock (op._gate) { DemandSnapshotAccess(op); return op._snapshot; }
    }

    private static void DemandSnapshotAccess(UploadOperation op)
    {
        if (op.PermissionRevoked) throw new PluginPermissionDeniedException("Transfer authorization was revoked.");
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

    /// <summary>
    /// 只接受普通文件,而且在打开之前就拒:没有写端的 FIFO 会让 open 一直阻塞,设备与套接字读不出一份确定的内容,
    /// 有的设备一打开就有副作用 —— 何况这一步还在用户审批之前。Windows 没有这类会卡住 open 的对象,
    /// 设备与管道由打开后的路径核验拒绝。
    /// </summary>
    private static void RejectSpecialSource(string path)
    {
        const uint typeMask = 0xF000, regularFile = 0x8000;   // S_IFMT、S_IFREG
        if (ReadUnixFileMode(path) is { } mode && (mode & typeMask) != regularFile)
            throw new NotSupportedException("Only regular files can be uploaded.");
    }

    /// <summary>
    /// 不跟随链接地取路径的 st_mode。Linux 用 statx(结构各架构一致:stx_mask 在 0、stx_mode 在 28),
    /// macOS 用 64 位 inode 的 lstat(st_mode 在 4;x64 上符号名是 lstat$INODE64)。
    /// 查不了(不存在、没权限、内核或 seccomp 不给 statx、libc 太旧)时为 null:交给正式打开报出原本的错误,
    /// 打开之后还有按句柄的类型复核。
    /// </summary>
    private static uint? ReadUnixFileMode(string path)
    {
        byte[] buffer = new byte[512];   // struct statx 256 字节;macOS 的 struct stat 144 字节
        try
        {
            if (OperatingSystem.IsLinux())
            {
                const int atFdCwd = -100, atSymlinkNoFollow = 0x100;
                const uint statxType = 0x1;
                return LinuxStatX(atFdCwd, path, atSymlinkNoFollow, statxType, buffer) == 0 && (BitConverter.ToUInt32(buffer, 0) & statxType) != 0
                    ? BitConverter.ToUInt16(buffer, 28)
                    : null;
            }
            if (OperatingSystem.IsMacOS())
            {
                int result = RuntimeInformation.ProcessArchitecture == Architecture.X64 ? DarwinLStatInode64(path, buffer) : DarwinLStat(path, buffer);
                return result == 0 ? BitConverter.ToUInt16(buffer, 4) : null;
            }
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException) { }
        return null;
    }

    [LibraryImport("libc", EntryPoint = "statx", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int LinuxStatX(int directory, string path, int flags, uint mask, [Out] byte[] buffer);

    [LibraryImport("libc", EntryPoint = "lstat", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int DarwinLStat(string path, [Out] byte[] buffer);

    [LibraryImport("libc", EntryPoint = "lstat$INODE64", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int DarwinLStatInode64(string path, [Out] byte[] buffer);

    private static void RejectLinks(string path)
    {
        for (string? current = path; current is not null; current = Path.GetDirectoryName(current))
        {
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new NotSupportedException("Linked source paths are not supported for stable uploads.");
        }
    }

    private static RemoteTransferSnapshot Read(UploadOperation op) { lock (op._gate) return op._snapshot; }
    private static void Update(UploadOperation op, Func<RemoteTransferSnapshot, RemoteTransferSnapshot> update)
    {
        lock (op._gate)
        {
            op._snapshot = update(op._snapshot) with { Sequence = op._snapshot.Sequence + 1, UpdatedAt = DateTimeOffset.UtcNow };
            TaskCompletionSource changed = op._changed;
            op._changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
            changed.TrySetResult();
        }
    }

    private sealed class UploadProgressSink(UploadOperation op) : IProgress<TransferProgress>
    {
        public void Report(TransferProgress value)
        {
            long now = Environment.TickCount64;
            lock (op._gate)
            {
                if (op._snapshot.IsTerminal || now - op._lastProgress < 100) return;
                op._lastProgress = now;
                Update(op, s => ApplyTransferProgress(s, value));
            }
        }
    }

    internal static RemoteTransferSnapshot ApplyTransferProgress(RemoteTransferSnapshot snapshot, TransferProgress value) => snapshot with
    {
        TransferredBytes = snapshot.Direction == RemoteTransferDirection.Download || snapshot.TotalBytes < 0
            ? Math.Max(snapshot.TransferredBytes, value.BytesTransferred)
            : Math.Clamp(value.BytesTransferred, snapshot.TransferredBytes, snapshot.TotalBytes),
        SpeedBytesPerSecond = value.SpeedBytesPerSecond > 0 ? value.SpeedBytesPerSecond : null,
        EstimatedTimeRemaining = value.SpeedBytesPerSecond > 0 ? value.EstimatedTimeRemaining : null,
    };

    private async Task RunUploadAsync(UploadOperation op, Guid session, RemoteUploadOptions options)
    {
        string temporary = Read(op).RemotePath + ".vela-upload-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        bool slot = false, writing = false, committing = false;
        Task? remoteWrite = null;
        Task? commitTask = null;
        FileStream? diskSnapshot = null;
        string? snapshotDirectory = null;
        // 速率按真正开始传输之后的耗时算:审批对话框、排队与快照准备都不算进去。
        var elapsed = new Stopwatch();
        using var execution = CancellationTokenSource.CreateLinkedTokenSource(op._stop.Token, op._permission?.Revocation ?? CancellationToken.None);
        CancellationToken ct = execution.Token;
        using var heartbeatStop = new CancellationTokenSource();
        Task heartbeat = TransferHeartbeatAsync(op, heartbeatStop.Token);
        try
        {
            Update(op, s => s with { State = RemoteTransferState.Starting });
            if (uploadPermissions is null || op._permission is null || string.IsNullOrWhiteSpace(pluginId))
                throw new PluginPermissionDeniedException("A trusted upload approval is required.");
            SshSession target = connections.GetSession(session) ?? throw new PluginSessionNotFoundException(Read(op).SessionId);
            string label = $"{target.ConnectionInfo.Username}@{target.ConnectionInfo.Host}:{target.ConnectionInfo.Port}";
            bool approved = await uploadPermissions.CheckUploadAsync(op._permission, pluginId, label, Read(op).LocalPath,
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
            Stream uploadSource = op._source;
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
                while ((count = await op._source.ReadAsync(buffer, ct).ConfigureAwait(false)) != 0)
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
                op._source.Position = 0;
                byte[] snapshotHash = await SHA256.HashDataAsync(diskSnapshot, ct).ConfigureAwait(false);
                byte[] sourceHash = await SHA256.HashDataAsync(op._source, ct).ConfigureAwait(false);
                if (!CryptographicOperations.FixedTimeEquals(snapshotHash, sourceHash)) throw new SourceChangedException();
                diskSnapshot.Position = 0;
                uploadSource = diskSnapshot;
            }
            ValidateSource(op);
            byte[] hash = await SHA256.HashDataAsync(uploadSource, ct).ConfigureAwait(false);
            uploadSource.Position = 0;
            ct.ThrowIfCancellationRequested();
            Update(op, s => s with { State = RemoteTransferState.Running });
            elapsed.Start();
            writing = true;
            remoteWrite = sftp.UploadStreamAsync(session, uploadSource, temporary, op._sourceLength,
                progress: new UploadProgressSink(op), cancellationToken: ct);
            await remoteWrite.WaitAsync(ct).ConfigureAwait(false);
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
            Succeed();
        }
        catch (Exception ex)
        {
            // 取消、到期、断线、撤权只打断了等待,后端任务可能还在跑:先给它一段有界的宽限期真正停下来,再定终态。
            Task? backend = commitTask ?? remoteWrite;
            if (backend is { IsCompleted: false })
                await Task.WhenAny(backend, Task.Delay(StopGracePeriod)).ConfigureAwait(false);
            // 提交应答赶在宽限期内到了:成功就是成功,取消不宣称回滚。
            if (commitTask is { IsCompletedSuccessfully: true }) Succeed();
            else
            {
                // 后端自己的失败原因比「等待被打断」有用;同步抛出的提交失败就是 ex 本身。
                Exception cause = backend is { IsFaulted: true } ? backend.Exception!.InnerException ?? ex : ex;
                // 只有收不到应答的提交才是 Unknown;根本没发出去或服务端明确拒绝,目标就确认没被提交。
                bool commitRejected = committing && (commitTask is null or { IsFaulted: true }) && IsDefiniteCommitRejection(cause);
                FailUpload(op, ex, cause, writeStopped: remoteWrite is not { IsCompleted: false },
                    commitUnknown: committing && !commitRejected, commitRejected);
            }
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
                // 宽限期内没停下的后端:继续保留稳定源句柄,直到它真正结束;状态保持 Unknown,不冒充已停止。
                if (remoteWrite is not null) { try { await remoteWrite.ConfigureAwait(false); } catch { } }
                if (commitTask is not null) { try { await commitTask.ConfigureAwait(false); } catch { } }
                try
                {
                    if (diskSnapshot is not null) await diskSnapshot.DisposeAsync().ConfigureAwait(false);
                }
                finally
                {
                    if (snapshotDirectory is not null) { try { Directory.Delete(snapshotDirectory); } catch { } }
                    try { await op._source.DisposeAsync().ConfigureAwait(false); }
                    finally
                    {
                        try { if (slot) _uploadSlots.Release(); }
                        finally
                        {
                            op._permission?._active = false;
                            lock (op._gate)
                            {
                                op._stopDisposed = true;
                                op._stop.Dispose();
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

        void Succeed() => Update(op, s => s with
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

    /// <summary>
    /// 上传没能成功收尾时按事实定终态:写入任务已经结束(失败也好、取消也好,后端都已关了句柄)就是确认停写,
    /// 没提交过的临时文件随后清理;提交只有收不到应答才是 Unknown。停止原因按登记的那个分类,
    /// 失败原因清洗后留给插件,完整异常写进宿主日志。
    /// </summary>
    private void FailUpload(UploadOperation op, Exception ex, Exception cause, bool writeStopped, bool commitUnknown, bool commitRejected)
    {
        // 撤权优先于其它停止原因,与此前的口径一致:撤权后的到期不再记成到期。
        StopReason reason = op.PermissionRevoked ? StopReason.None : ReadStopReason(op);
        bool stopped = writeStopped && !commitUnknown;
        RemoteTransferState state = reason == StopReason.Timeout ? RemoteTransferState.TimedOut
            : !stopped ? RemoteTransferState.Unknown
            : op.PermissionRevoked || ex is PluginPermissionDeniedException ? RemoteTransferState.Failed
            : reason is StopReason.User or StopReason.Disposed ? RemoteTransferState.Cancelled
            : RemoteTransferState.Failed;
        string code = op.PermissionRevoked ? "permission-revoked"
            : ex is PluginPermissionDeniedException ? "permission-denied"
            : commitUnknown ? "commit-unknown"
            : StopCode(reason)
              ?? (commitRejected ? cause is NotSupportedException ? "not-supported" : "commit-rejected"
                  : ex is SourceChangedException ? "source-changed" : "upload-failed");
        string message = op.PermissionRevoked ? "Upload authorization was revoked."
            : ex is PluginPermissionDeniedException ? "A trusted host upload approval was not granted."
            : commitUnknown ? "The destination commit could not be confirmed; do not retry automatically."
            : !stopped ? "The remote write did not confirm it stopped; do not retry automatically."
            : StopText(reason) is { } stop ? stop + "; the destination was not committed."
            : commitRejected ? "The destination commit was refused; the destination was not committed: " + DescribeCause(cause)
            : ex is SourceChangedException ? "The approved source changed; the destination was not committed."
            : ex is InvalidDataException ? "Uploaded content verification failed."
            : "Upload failed: " + DescribeCause(cause);
        Update(op, s => s with
        {
            State = state,
            RemoteStopConfirmed = stopped,
            DestinationCommitConfirmedAbsent = !commitUnknown,
            CompletedAt = DateTimeOffset.UtcNow,
            Resumable = false,
            ErrorCode = code,
            ErrorMessage = message,
        });
        LogTransferEnd(op, state, code, cause);
    }

    /// <summary>
    /// 提交被明确拒绝:请求根本没发出去(后端不支持,如要覆盖而服务端没有 posix-rename),或服务端回了失败状态
    /// (如不覆盖时普通 RENAME 因目标已存在被拒)。取消、断线、超时都等不到应答,目标是否已被替换无从确认;
    /// SFTP 通道中途断开报的是传输中断,同样没有应答。
    /// </summary>
    private static bool IsDefiniteCommitRejection(Exception ex)
        => ex is NotSupportedException or (VelaSftpOperationException and not VelaSftpTransferInterruptedException);

    /// <summary>
    /// 停止原因:显式登记的(用户取消、断线、停用)优先;令牌被取消却没登记的,只能来自寿命令牌(插件停用、宿主退出)
    /// 或死线。须在停止令牌释放之前读取。
    /// </summary>
    private StopReason ReadStopReason(UploadOperation op)
    {
        lock (op._gate)
        {
            if (op._stopReason != StopReason.None || !op._stop.IsCancellationRequested) return op._stopReason;
        }
        return lifetime.IsCancellationRequested ? StopReason.Disposed : StopReason.Timeout;
    }

    /// <summary>确认停下、又不是授权问题时按停止原因给的错误码;没有停止原因(真失败)时为 null。</summary>
    private static string? StopCode(StopReason reason) => reason switch
    {
        StopReason.User => "cancelled",
        StopReason.Timeout => "timeout",
        StopReason.Disconnected => "session-disconnected",
        StopReason.Disposed => "plugin-stopped",
        _ => null,
    };

    private static string? StopText(StopReason reason) => reason switch
    {
        StopReason.User => "The transfer was cancelled",
        StopReason.Timeout => "The transfer deadline was reached",
        StopReason.Disconnected => "The session disconnected",
        StopReason.Disposed => "The plugin or host stopped",
        _ => null,
    };

    /// <summary>
    /// 失败原因进快照:只取异常消息,按不可信文本清洗(控制字符、双向控制符)并限长 —— 它会原样到达插件与模型;
    /// 完整异常只写进宿主日志。
    /// </summary>
    private static string DescribeCause(Exception ex) => PeerText.Sanitize(ex.Message);

    /// <summary>非成功收尾写进宿主日志:插件拿到的说明是清洗、截断过的,完整异常(含内层)只留在这里。</summary>
    private void LogTransferEnd(UploadOperation op, RemoteTransferState state, string code, Exception cause)
    {
        RemoteTransferSnapshot snapshot = Read(op);
        Trace.WriteLine($"[RemoteFs:{pluginId}] {snapshot.Direction} {snapshot.TransferId} ended {state} ({code}): {cause}");
    }

    private static async Task TransferHeartbeatAsync(UploadOperation op, CancellationToken ct)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                lock (op._gate)
                {
                    if (op._snapshot.IsTerminal) return;
                    if (op._snapshot.State == RemoteTransferState.Starting
                        || op._snapshot.State == RemoteTransferState.Running && op._snapshot.TransferredBytes == op._snapshot.TotalBytes)
                        Update(op, s => s);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    private static void RequestStop(UploadOperation op, StopReason reason)
    {
        lock (op._gate)
        {
            if (op._stopDisposed) return;
            // 先到者为准:死线已到或插件已停用时令牌早已取消,不改写成后来的原因。
            if (!op._stop.IsCancellationRequested && op._stopReason == StopReason.None) op._stopReason = reason;
            op._stop.Cancel();
        }
    }

    private static void ValidateSource(UploadOperation op)
    {
        if (op._source.Length != op._sourceLength || File.GetLastWriteTimeUtc(op._source.SafeFileHandle) != op._sourceModified)
            throw new SourceChangedException();
    }

    private void OnTransferSessionDisconnected(SshSession session)
    {
        lock (_transfersGate)
            foreach (UploadOperation op in _transfers.Values)
                if (!op._worker.IsCompleted && Read(op).SessionId == session.SessionId.ToString())
                    RequestStop(op, StopReason.Disconnected);
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
                if (!op._worker.IsCompleted)
                {
                    RequestStop(op, StopReason.Disposed);
                    _ = op._worker.ContinueWith(_ => { op._permission?.Dispose(); op._downloadPermission?.Dispose(); }, CancellationToken.None,
                        TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                }
                else { op._permission?.Dispose(); op._downloadPermission?.Dispose(); }
            }
        }
    }
}
