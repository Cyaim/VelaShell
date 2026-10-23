using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;
using VelaShell.Core.Models;
using VelaShell.PluginSdk;
using VelaShell.PluginSdk.RemoteFs;

namespace VelaShell.Infrastructure.Plugins.Capabilities;

internal sealed partial class RemoteFsCapability
{
    // Other platforms deliberately fail closed until a directory-relative implementation exists.
    public bool SupportsDownloadOperations => OperatingSystem.IsWindows() || DownloadTargetFactory is not null;

    /// <summary>
    /// 测试开关:注入落盘目标,在非 Windows 上也能验证下载 worker 的收尾分类(与 <see cref="UseDiskSnapshot" /> 同一用意)。
    /// 生产代码从不设置;为 null 时只有 Windows 固定本地 NTFS 目标可用。
    /// </summary>
    internal Func<string, bool, IDownloadTarget>? DownloadTargetFactory { get; init; }

    /// <summary>下载落盘目标:独占创建临时文件,校验后原子提交;未提交的临时文件随释放清除,清不掉时释放抛出。</summary>
    internal interface IDownloadTarget : IAsyncDisposable
    {
        /// <summary>独占创建临时文件并交出写入流;流归目标所有。</summary>
        FileStream CreateTemporary();

        /// <summary>把临时文件原子提交为最终目标;不覆盖时目标已存在必须拒绝。</summary>
        void Commit(bool overwrite);
    }

    public Task<RemoteTransferSnapshot> StartDownloadAsync(string sessionId, string remotePath, string localPath,
        RemoteDownloadOptions options, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.OperationId);
        if (options.OperationId.Length > 256) throw new ArgumentException("Operation id is too long.", nameof(options));
        if (options.Timeout is { } timeout && (timeout <= TimeSpan.Zero || timeout.TotalMilliseconds > uint.MaxValue - 1))
            throw new ArgumentOutOfRangeException(nameof(options));
        string local = Path.GetFullPath(localPath);
        string remote = NormalizeDestination(remotePath);
        lock (_transfersGate)
        {
            ObjectDisposedException.ThrowIf(_transfersDisposed || lifetime.IsCancellationRequested, this);
            if (_receipts.TryGetValue(options.OperationId, out StartReceipt? previous))
            {
                if (previous.Operation._snapshot.Direction != RemoteTransferDirection.Download || previous.Session != sessionId || previous.Local != local || previous.Remote != remote || !Equals(previous.Options, options))
                    throw new InvalidOperationException("Operation id was already used with different parameters or direction.");
                return Task.FromResult(ReadAuthorized(previous.Operation));
            }
            if (!SupportsDownloadOperations) throw new PlatformNotSupportedException("Safe background download commit is unavailable on this platform.");
            Guid session = Resolve(sessionId);
            if (_receipts.Count >= MaxRecords) throw new InvalidOperationException("Transfer operation receipt quota reached for this plugin lifetime.");
            if (_transfers.Values.Count(op => !op._worker.IsCompleted) >= MaxActiveUploads)
                throw new InvalidOperationException("Active transfer queue quota reached for this plugin.");
            var stop = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
            if (options.Timeout is { } budget) stop.CancelAfter(budget);
            DateTimeOffset now = DateTimeOffset.UtcNow;
            var op = new UploadOperation(new()
            {
                TransferId = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)),
                OperationId = options.OperationId,
                SessionId = sessionId,
                LocalPath = local,
                RemotePath = remote,
                Direction = RemoteTransferDirection.Download,
                Phase = RemoteTransferPhase.Approval,
                State = RemoteTransferState.Queued,
                Generation = 1,
                Sequence = 1,
                TotalBytes = -1,
                CreatedAt = now,
                UpdatedAt = now,
                DestinationCommitConfirmedAbsent = true,
            }, null, stop);
            if (uploadPermissions is not null && !string.IsNullOrWhiteSpace(pluginId))
                op._downloadPermission = uploadPermissions.CreateDownloadLease(pluginId);
            if (!_disconnectSubscribed)
            {
                connections.SessionDisconnected += OnTransferSessionDisconnected;
                _disconnectSubscribed = true;
            }
            _transfers.Add(op._snapshot.TransferId, op);
            _receipts.Add(options.OperationId, new(sessionId, local, remote, options, op));
            op._worker = Task.Run(() => RunDownloadAsync(op, session, options), CancellationToken.None);
            _ = op._worker.ContinueWith(static task => _ = task.Exception, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            return Task.FromResult(Read(op));
        }
    }

    private async Task RunDownloadAsync(UploadOperation op, Guid session, RemoteDownloadOptions options)
    {
        bool slot = false, committed = false;
        Task? downloadWrite = null;
        IDownloadTarget? target = null;
        // 速率按真正开始传输之后的耗时算:审批对话框与排队等待不算进去。
        var elapsed = new Stopwatch();
        using var execution = CancellationTokenSource.CreateLinkedTokenSource(op._stop.Token, op._downloadPermission?.Revocation ?? CancellationToken.None);
        CancellationToken ct = execution.Token;
        using var heartbeatStop = new CancellationTokenSource();
        Task heartbeat = TransferHeartbeatAsync(op, heartbeatStop.Token);
        try
        {
            Update(op, s => s with { State = RemoteTransferState.Starting, Phase = RemoteTransferPhase.Approval });
            if (uploadPermissions is null || op._downloadPermission is null || string.IsNullOrWhiteSpace(pluginId))
                throw new PluginPermissionDeniedException("A trusted download approval is required.");
            // Pin and verify the whole local directory chain before approval, without creating content.
            target = DownloadTargetFactory?.Invoke(Read(op).LocalPath, options.Overwrite)
                ?? WindowsDownloadTarget.Open(Read(op).LocalPath, options.Overwrite);
            RemoteFileInfo source = await sftp.GetFileInfoAsync(session, Read(op).RemotePath, ct).ConfigureAwait(false);
            ValidateDownloadSource(source);
            Update(op, s => s with { TotalBytes = source.Size });
            SshSession connection = connections.GetSession(session) ?? throw new PluginSessionNotFoundException(Read(op).SessionId);
            string label = $"{connection.ConnectionInfo.Username}@{connection.ConnectionInfo.Host}:{connection.ConnectionInfo.Port}";
            if (!await uploadPermissions.CheckDownloadAsync(op._downloadPermission, pluginId, label, Read(op).RemotePath,
                Read(op).LocalPath, options.Overwrite, ct).ConfigureAwait(false))
                throw new PluginPermissionDeniedException("The user denied this download.");
            Update(op, s => s with { State = RemoteTransferState.Queued });
            await _uploadSlots.WaitAsync(ct).ConfigureAwait(false);
            slot = true;
            ct.ThrowIfCancellationRequested();
            DemandSnapshotAccess(op);
            Resolve(Read(op).SessionId);
            RemoteFileInfo before = await sftp.GetFileInfoAsync(session, Read(op).RemotePath, ct).ConfigureAwait(false);
            if (!SameDownloadSource(source, before)) throw new SourceChangedException();
            FileStream sink = target.CreateTemporary();
            Update(op, s => s with { State = RemoteTransferState.Running, Phase = RemoteTransferPhase.Transfer });
            elapsed.Start();
            downloadWrite = sftp.DownloadStreamAsync(session, Read(op).RemotePath, sink, new UploadProgressSink(op), ct);
            await downloadWrite.WaitAsync(ct).ConfigureAwait(false);
            Update(op, s => s with { Phase = RemoteTransferPhase.Verification, TransferredBytes = sink.Length, SpeedBytesPerSecond = null, EstimatedTimeRemaining = null });
            if (sink.Length != source.Size) throw new InvalidDataException("Downloaded length does not match the remote source.");
            RemoteFileInfo after = await sftp.GetFileInfoAsync(session, Read(op).RemotePath, ct).ConfigureAwait(false);
            if (!SameDownloadSource(source, after)) throw new SourceChangedException();
            // Use the existing digest capability when available. Pure SFTP accounts need no exec;
            // there is no mandatory second full-file download and this is not a remote snapshot.
            IReadOnlyDictionary<string, string?>? hashes = null;
            try { hashes = await sftp.ComputeSha256Async(session, [Read(op).RemotePath], ct).ConfigureAwait(false); }
            catch (NotSupportedException) { }
            if (hashes is not null && hashes.TryGetValue(Read(op).RemotePath, out string? digest) && digest is { Length: 64 })
            {
                byte[]? expected = null;
                try { expected = Convert.FromHexString(digest); } catch (FormatException) { }
                if (expected is not null)
                {
                    sink.Position = 0;
                    byte[] actual = await SHA256.HashDataAsync(sink, ct).ConfigureAwait(false);
                    if (!CryptographicOperations.FixedTimeEquals(expected, actual)) throw new InvalidDataException("Downloaded content verification failed.");
                }
            }
            await sink.FlushAsync(ct).ConfigureAwait(false);
            sink.Flush(flushToDisk: true);
            ct.ThrowIfCancellationRequested();
            Resolve(Read(op).SessionId);
            DemandSnapshotAccess(op);
            Update(op, s => s with { Phase = RemoteTransferPhase.Commit });
            ct.ThrowIfCancellationRequested();
            DemandSnapshotAccess(op);
            // The synchronous NTFS rename has a definite native return. A cancellation racing it
            // may lose to commit; success then remains success, not a promise to roll it back.
            target.Commit(options.Overwrite);
            committed = true;
            await target.DisposeAsync().ConfigureAwait(false);
            target = null;
            Update(op, s => s with
            {
                State = RemoteTransferState.Succeeded,
                CompletedAt = DateTimeOffset.UtcNow,
                RemoteStopConfirmed = true,
                DestinationCommitConfirmedAbsent = false,
                TransferredBytes = s.TotalBytes,
                SpeedBytesPerSecond = elapsed.Elapsed.TotalSeconds > 0 ? s.TotalBytes / elapsed.Elapsed.TotalSeconds : null,
                EstimatedTimeRemaining = TimeSpan.Zero,
            });
        }
        catch (Exception ex)
        {
            // 与上传同理:取消、到期、断线、撤权只打断了等待,后端可能还在往临时文件里写。先给它一段有界的宽限期停下来,
            // 停下了才关掉并删掉临时文件、确认停写;宽限期内仍没停(后端不理取消),照旧尽快发布未确认的结果,
            // 流、目录钉与共享槽保留到它真正退出。
            if (downloadWrite is { IsCompleted: false })
                await Task.WhenAny(downloadWrite, Task.Delay(StopGracePeriod)).ConfigureAwait(false);
            bool stopped = downloadWrite is null || downloadWrite.IsCompleted;
            bool cleaned = stopped;
            if (stopped && target is not null)
            {
                try { await target.DisposeAsync().ConfigureAwait(false); }
                catch { cleaned = false; }
                target = null;
            }
            // 后端自己的失败原因比「等待被打断」有用。
            Exception cause = downloadWrite is { IsFaulted: true } ? downloadWrite.Exception!.InnerException ?? ex : ex;
            // 撤权优先于其它停止原因;断线、停用不再被推断成到期或用户取消。
            StopReason reason = op.PermissionRevoked ? StopReason.None : ReadStopReason(op);
            RemoteTransferState state = committed ? RemoteTransferState.Unknown : reason == StopReason.Timeout ? RemoteTransferState.TimedOut
                : !cleaned ? RemoteTransferState.Unknown : op.PermissionRevoked || ex is PluginPermissionDeniedException ? RemoteTransferState.Failed
                : reason is StopReason.User or StopReason.Disposed ? RemoteTransferState.Cancelled : RemoteTransferState.Failed;
            string code = op.PermissionRevoked ? "permission-revoked" : ex is PluginPermissionDeniedException ? "permission-denied"
                : committed ? "commit-unknown" : !cleaned ? "cleanup-unknown"
                : StopCode(reason) ?? (ex is SourceChangedException ? "source-changed" : ex is NotSupportedException ? "not-supported" : "download-failed");
            Update(op, s => s with
            {
                State = state,
                CompletedAt = DateTimeOffset.UtcNow,
                RemoteStopConfirmed = cleaned,
                DestinationCommitConfirmedAbsent = !committed,
                Resumable = false,
                SpeedBytesPerSecond = null,
                EstimatedTimeRemaining = null,
                ErrorCode = code,
                ErrorMessage = committed ? "The destination was committed but final completion could not be confirmed; do not retry automatically."
                    : op.PermissionRevoked ? "Download authorization was revoked."
                    : ex is PluginPermissionDeniedException ? "A trusted host download approval was not granted."
                    : !cleaned ? "The download did not confirm it stopped and cleaned up; the destination was not committed; do not retry automatically."
                    : StopText(reason) is { } stop ? stop + "; the destination was not committed."
                    : ex is SourceChangedException ? "The remote source changed; the destination was not committed."
                    : "Download did not complete; the destination was not committed: " + DescribeCause(cause),
            });
            LogTransferEnd(op, state, code, cause);
        }
        finally
        {
            heartbeatStop.Cancel();
            await heartbeat.ConfigureAwait(false);
            if (downloadWrite is not null) { try { await downloadWrite.ConfigureAwait(false); } catch { } }
            try
            {
                if (target is not null) await target.DisposeAsync().ConfigureAwait(false);
            }
            catch
            {
                Update(op, s => s with
                {
                    State = RemoteTransferState.Unknown,
                    RemoteStopConfirmed = false,
                    ErrorCode = "cleanup-unknown",
                    ErrorMessage = "The task temporary cleanup could not be confirmed."
                });
            }
            finally
            {
                if (slot) _uploadSlots.Release();
                op._downloadPermission?._active = false;
                lock (op._gate) { op._stopDisposed = true; op._stop.Dispose(); }
            }
        }
    }

    private static void ValidateDownloadSource(RemoteFileInfo source)
    {
        if (source.IsDirectory || source.IsSymbolicLink || source.Size < 0)
            throw new NotSupportedException("Download source must be a regular file with a known length.");
    }

    private static bool SameDownloadSource(RemoteFileInfo left, RemoteFileInfo right)
        => !right.IsDirectory && !right.IsSymbolicLink && left.Size == right.Size && left.LastModified == right.LastModified;

    /// <summary>
    /// Windows local NTFS only: every ancestor is held without write/delete sharing, preventing rename/reparse retargeting.
    /// The exclusively-created temporary is renamed/deleted by its handle; final paths are never opened for writing.
    /// Unsupported filesystems/platforms fail closed, rather than substituting path-based preflight checks.
    /// </summary>
    private sealed partial class WindowsDownloadTarget : IDownloadTarget
    {
        private readonly List<SafeFileHandle> _directories = [];
        private readonly string _destination;
        private FileStream? _stream;
        private bool _committed;
        private WindowsDownloadTarget(string destination) => _destination = destination;

        internal static WindowsDownloadTarget Open(string destination, bool overwrite)
        {
            if (!OperatingSystem.IsWindows() || destination.StartsWith(@"\\", StringComparison.Ordinal) || destination.IndexOf(':', 2) >= 0)
                throw new PlatformNotSupportedException("Safe download requires an ordinary local NTFS path.");
            string root = Path.GetPathRoot(destination) ?? throw new IOException("Destination must be absolute.");
            var drive = new DriveInfo(root);
            if (drive.DriveType != DriveType.Fixed || !string.Equals(drive.DriveFormat, "NTFS", StringComparison.OrdinalIgnoreCase))
                throw new PlatformNotSupportedException("Safe download currently requires a fixed local NTFS volume.");
            string name = Path.GetFileName(destination);
            if (string.IsNullOrWhiteSpace(name) || name.EndsWith('.') || name.EndsWith(' ')) throw new IOException("Invalid destination name.");
            var target = new WindowsDownloadTarget(destination);
            try
            {
                string parent = Path.GetDirectoryName(destination) ?? throw new IOException("Destination must name a file.");
                string current = root;
                target.PinDirectory(current);
                foreach (string part in parent[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
                {
                    if (part.EndsWith('.') || part.EndsWith(' ')) throw new IOException("Ambiguous directory name.");
                    current = Path.Combine(current, part);
                    target.PinDirectory(current);
                }
                if (File.Exists(destination) || Directory.Exists(destination))
                {
                    if ((File.GetAttributes(destination) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                        throw new IOException("The destination must not be a directory or link.");
                    if (!overwrite) throw new IOException("The destination already exists.");
                }
                return target;
            }
            catch { foreach (SafeFileHandle handle in target._directories) handle.Dispose(); throw; }
        }

        private void PinDirectory(string path)
        {
            SafeFileHandle handle = CreateFileW(path, 0x80, 1, 0, 3, 0x02200000, 0);
            if (handle.IsInvalid) { handle.Dispose(); throw new IOException("Cannot lock the destination directory chain.", new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error())); }
            try
            {
                var buffer = new StringBuilder(32768);
                uint length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Capacity, 0);
                string actual = buffer.ToString();
                if (actual.StartsWith(@"\\?\", StringComparison.Ordinal)) actual = actual[4..];
                if (length == 0 || length >= buffer.Capacity || !string.Equals(Path.TrimEndingDirectorySeparator(actual), Path.TrimEndingDirectorySeparator(path), StringComparison.OrdinalIgnoreCase)
                    || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Destination directory identity cannot be verified.");
                _directories.Add(handle);
            }
            catch { handle.Dispose(); throw; }
        }

        public FileStream CreateTemporary()
        {
            string temporary = Path.Combine(Path.GetDirectoryName(_destination)!, ".vela-download-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(24)));
            const uint fileFlagOverlapped = 0x40000000;
            SafeFileHandle handle = CreateFileW(temporary, 0xC0010000, 0, 0, 1, fileFlagOverlapped, 0);
            if (handle.IsInvalid) { handle.Dispose(); throw new IOException("Cannot exclusively create the download temporary file."); }
            _stream = new FileStream(handle, FileAccess.ReadWrite, 128 * 1024, isAsync: true);
            return _stream;
        }

        public void Commit(bool overwrite)
        {
            if (_stream is null) throw new InvalidOperationException();
            // Win32 needs a terminated name; FileNameLength excludes the UTF-16 terminator.
            byte[] name = Encoding.Unicode.GetBytes(_destination + '\0');
            int offset = IntPtr.Size == 8 ? 20 : 12;
            nint info = Marshal.AllocHGlobal(offset + name.Length);
            try
            {
                for (int i = 0; i < offset; i++) Marshal.WriteByte(info, i, 0);
                Marshal.WriteByte(info, 0, overwrite ? (byte)1 : (byte)0);
                Marshal.WriteInt32(info, offset - 4, name.Length - sizeof(char));
                Marshal.Copy(name, 0, info + offset, name.Length);
                if (!SetFileInformationByHandle(_stream.SafeFileHandle, 3, info, (uint)(offset + name.Length)))
                    throw new IOException("Atomic download commit was rejected.", new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()));
                _committed = true;
            }
            finally { Marshal.FreeHGlobal(info); }
        }

        public async ValueTask DisposeAsync()
        {
            Exception? cleanupFailure = null;
            try
            {
                if (_stream is not null && !_stream.SafeFileHandle.IsClosed)
                {
                    if (!_committed)
                    {
                        nint disposition = Marshal.AllocHGlobal(4);
                        try
                        {
                            Marshal.WriteInt32(disposition, 1);
                            if (!SetFileInformationByHandle(_stream.SafeFileHandle, 4, disposition, 4))
                                cleanupFailure = new IOException("The task temporary cleanup could not be confirmed.");
                        }
                        finally { Marshal.FreeHGlobal(disposition); }
                    }
                    await _stream.DisposeAsync().ConfigureAwait(false);
                }
            }
            finally { foreach (SafeFileHandle handle in _directories) handle.Dispose(); }
            if (cleanupFailure is not null) throw cleanupFailure;
        }

        [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
        private static partial SafeFileHandle CreateFileW(string path, uint access, uint share, nint security, uint creation, uint flags, nint template);
        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool SetFileInformationByHandle(SafeFileHandle file, int informationClass, nint information, uint size);
    }
}
