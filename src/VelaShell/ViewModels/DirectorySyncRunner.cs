using VelaShell.Core.DirectorySync;
using VelaShell.Core.Models;
using VelaShell.Core.Resources;
using VelaShell.Core.Sftp;

namespace VelaShell.ViewModels;

/// <summary>一次同步执行的进度。</summary>
/// <param name="Done">已落定的步数。</param>
/// <param name="Total">总步数。</param>
/// <param name="Current">最近处理的相对路径。</param>
public readonly record struct SyncRunProgress(int Done, int Total, string Current);

/// <summary>一次同步执行的结果。</summary>
/// <param name="Succeeded">成功的步数。</param>
/// <param name="Failed">失败的步数。</param>
/// <param name="Cancelled">是否被取消(取消后不再执行删除)。</param>
/// <param name="TimestampsUnsupported">后端是否表示过「设不了修改时间」。</param>
/// <param name="Errors">失败项的说明(相对路径 + 原因)。</param>
public sealed record DirectorySyncResult(
    int Succeeded,
    int Failed,
    bool Cancelled,
    bool TimestampsUnsupported,
    IReadOnlyList<string> Errors);

/// <summary>
/// 把一份 <see cref="SyncAction" /> 计划落到两边:建目录 → 改时间 → 传文件 → 删除。
/// </summary>
/// <remarks>
/// <list type="bullet">
///   <item><b>删除放在最后</b>,而且被取消后不再执行:传输中途停下时,目标上多出来的东西还在,
///   比「新文件没传完、旧文件先删了」安全。</item>
///   <item><b>传输复用文件面板的管道</b>(进度浮窗、全窗口并发上限、取消、传输日志),但跳过冲突询问与续传,
///   见 <see cref="FileBrowserViewModel.RunSyncTransfersAsync" />。</item>
///   <item><b>传完回写修改时间</b>,不看「保留时间戳」设置:同步靠时间判新旧,不回写的话下一次比较
///   会把刚上传的文件当成「远端较新」,双向同步还会再把它下载回来。FTP 走 MFMT,服务器不支持时如实上报。</item>
///   <item>从远端名字拼出的本地路径逐段过 <see cref="LocalPathSafety" />:远端 Linux 上合法的
///   <c>a:b</c>、<c>CON</c> 在 Windows 上要么写不了、要么指向设备。</item>
/// </list>
/// </remarks>
internal sealed class DirectorySyncRunner(ISftpService sftp, Guid sessionId, FileBrowserViewModel transfers)
{
    /// <summary>执行计划。</summary>
    /// <param name="localRoot">本地同步根。</param>
    /// <param name="remoteRoot">远端同步根。</param>
    /// <param name="actions">要执行的步骤。</param>
    /// <param name="progress">进度回报;可为 null。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task<DirectorySyncResult> ExecuteAsync(
        string localRoot,
        string remoteRoot,
        IReadOnlyList<SyncAction> actions,
        IProgress<SyncRunProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actions);
        var run = new Run(Path.GetFullPath(localRoot), remoteRoot, actions.Count, progress);
        try
        {
            await CreateDirectoriesAsync(run, actions, cancellationToken).ConfigureAwait(true);
            await SetTimesAsync(run, actions, cancellationToken).ConfigureAwait(true);
            if (!await TransferAsync(run, actions, cancellationToken).ConfigureAwait(true))
            {
                return run.Result(cancelled: true);
            }
            await DeleteAsync(run, actions, cancellationToken).ConfigureAwait(true);
            return run.Result(cancelled: false);
        }
        catch (OperationCanceledException)
        {
            return run.Result(cancelled: true);
        }
    }

    private async Task CreateDirectoriesAsync(Run run, IReadOnlyList<SyncAction> actions, CancellationToken ct)
    {
        foreach (SyncAction action in actions
                     .Where(static a => a.Kind is SyncActionKind.CreateRemoteDirectory or SyncActionKind.CreateLocalDirectory)
                     .OrderBy(static a => Depth(a.RelativePath)))
        {
            ct.ThrowIfCancellationRequested();
            await run.StepAsync(action, async () =>
            {
                if (action.Kind == SyncActionKind.CreateRemoteDirectory)
                {
                    await EnsureRemoteDirectoriesAsync(run, action.RelativePath, ct).ConfigureAwait(true);
                }
                else
                {
                    Directory.CreateDirectory(ResolveLocal(run, action.RelativePath));
                }
            }).ConfigureAwait(true);
        }
    }

    private async Task SetTimesAsync(Run run, IReadOnlyList<SyncAction> actions, CancellationToken ct)
    {
        foreach (SyncAction action in actions.Where(static a => a.Kind is SyncActionKind.SetRemoteTime or SyncActionKind.SetLocalTime))
        {
            ct.ThrowIfCancellationRequested();
            await run.StepAsync(action, async () =>
            {
                if (action.Kind == SyncActionKind.SetRemoteTime)
                {
                    try
                    {
                        await sftp.SetLastWriteTimeAsync(sessionId, RemotePathOf(run, action), action.Local!.LastWriteTimeUtc, ct)
                            .ConfigureAwait(true);
                    }
                    catch (NotSupportedException)
                    {
                        run.TimestampsUnsupported = true;
                        throw;
                    }
                }
                else
                {
                    File.SetLastWriteTimeUtc(action.Local!.FullPath, action.Remote!.LastWriteTimeUtc);
                }
            }).ConfigureAwait(true);
        }
    }

    /// <summary>传输文件;返回 false 表示被取消。</summary>
    private async Task<bool> TransferAsync(Run run, IReadOnlyList<SyncAction> actions, CancellationToken ct)
    {
        var requests = new List<SyncTransferRequest>();
        var owners = new Dictionary<SyncTransferRequest, SyncAction>();
        foreach (SyncAction action in actions.Where(static a => a.Kind is SyncActionKind.Upload or SyncActionKind.Download))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                bool upload = action.Kind == SyncActionKind.Upload;
                string local = action.Local?.FullPath ?? ResolveLocal(run, action.RelativePath);
                string remote = RemotePathOf(run, action);
                // 父目录由这里补齐,而不是指望计划里一定有对应的「建目录」步骤:
                // 用户完全可以在预览里取消勾选那一步、只留下它里面的文件。
                if (upload)
                {
                    await EnsureRemoteDirectoriesAsync(run, ParentOf(action.RelativePath), ct).ConfigureAwait(true);
                }
                else if (Path.GetDirectoryName(local) is { Length: > 0 } parent)
                {
                    Directory.CreateDirectory(parent);
                }
                var request = new SyncTransferRequest(upload ? TransferType.Upload : TransferType.Download, local, remote);
                if (owners.TryAdd(request, action))
                {
                    requests.Add(request);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                run.Fail(action, ex.Message);
            }
        }
        if (requests.Count == 0)
        {
            return true;
        }
        return await transfers.RunSyncTransfersAsync(requests, async (request, status) =>
        {
            SyncAction action = owners[request];
            if (status == TransferStatus.Cancelled)
            {
                // 传输浮窗那一行的 × 只停了这一项:其余照做,这一项记「已取消」——
                // 不拿 ErrorMessage 顶替,那是别的文件留下的(或者没有,就成了「传输失败」)。
                run.Fail(action, Strings.Get("Sync_Cancelled"));
                return;
            }
            if (status != TransferStatus.Completed)
            {
                run.Fail(action, transfers.ErrorMessage ?? Strings.Get("Sync_TransferFailed"));
                return;
            }
            await AlignTimestampAsync(run, action, request).ConfigureAwait(true);
            run.Succeed(action);
        }, ct).ConfigureAwait(true);
    }

    /// <summary>传完后把目标的修改时间对齐源;失败不算这一步失败(内容已经对了),但要记下「设不了」。</summary>
    private async Task AlignTimestampAsync(Run run, SyncAction action, SyncTransferRequest request)
    {
        try
        {
            if (request.Type == TransferType.Upload && action.Local is { } source)
            {
                await sftp.SetLastWriteTimeAsync(sessionId, request.RemotePath, source.LastWriteTimeUtc, CancellationToken.None)
                    .ConfigureAwait(true);
            }
            else if (request.Type == TransferType.Download
                     && action.Remote is { Precision: not SyncTimePrecision.Unknown } remote)
            {
                File.SetLastWriteTimeUtc(request.LocalPath, remote.LastWriteTimeUtc);
            }
        }
        catch (NotSupportedException)
        {
            run.TimestampsUnsupported = true;
        }
        catch
        {
            // 尽力而为:个别服务器禁 setstat,内容已经传对了,不能因此把这一项记成失败。
        }
    }

    private async Task DeleteAsync(Run run, IReadOnlyList<SyncAction> actions, CancellationToken ct)
    {
        foreach (SyncAction action in actions
                     .Where(static a => a.Kind is SyncActionKind.DeleteRemote or SyncActionKind.DeleteLocal)
                     .OrderByDescending(static a => Depth(a.RelativePath)))
        {
            ct.ThrowIfCancellationRequested();
            await run.StepAsync(action, async () =>
            {
                if (action.Kind == SyncActionKind.DeleteRemote)
                {
                    await sftp.DeleteAsync(sessionId, RemotePathOf(run, action), null, ct).ConfigureAwait(true);
                    return;
                }
                string path = action.Local!.FullPath;
                await Task.Run(() =>
                {
                    // Directory.Delete(recursive) 遇到目录联接/符号链接只删链接本身,不进目标 —— 与远端删除同一口径。
                    if (Directory.Exists(path))
                    {
                        Directory.Delete(path, recursive: true);
                    }
                    else
                    {
                        File.Delete(path);
                    }
                }, ct).ConfigureAwait(true);
            }).ConfigureAwait(true);
        }
    }

    /// <summary>从同步根往下逐级确保远端目录存在;同一次执行里建过的不再重复发请求。</summary>
    private async Task EnsureRemoteDirectoriesAsync(Run run, string relativeDirectory, CancellationToken ct)
    {
        string current = string.Empty;
        foreach (string segment in relativeDirectory.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            current = current.Length == 0 ? segment : current + "/" + segment;
            string remote = DirectoryTreeScanner.CombineRemote(run.RemoteRoot, current);
            if (run.EnsuredRemote.Add(remote))
            {
                await sftp.EnsureDirectoryAsync(sessionId, remote, ct).ConfigureAwait(true);
            }
        }
    }

    /// <summary>逐段校验后拼出本地路径;任何一段在本机不合法都拒绝。</summary>
    private static string ResolveLocal(Run run, string relativePath)
    {
        string current = run.LocalRoot;
        foreach (string segment in relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!LocalPathSafety.TryResolveDestination(current, segment, out string next))
            {
                throw new InvalidOperationException(Strings.Format("Sync_UnsafeLocalName", relativePath));
            }
            current = next;
        }
        return current;
    }

    private static string RemotePathOf(Run run, SyncAction action) =>
        action.Remote?.FullPath ?? DirectoryTreeScanner.CombineRemote(run.RemoteRoot, action.RelativePath);

    private static string ParentOf(string relativePath)
    {
        int slash = relativePath.LastIndexOf('/');
        return slash < 0 ? string.Empty : relativePath[..slash];
    }

    private static int Depth(string relativePath) => relativePath.Count(static c => c == '/');

    /// <summary>一次执行的计数与错误汇总。传输的落定回调可能来自并发的工作任务,计数一律原子更新。</summary>
    private sealed class Run(string localRoot, string remoteRoot, int total, IProgress<SyncRunProgress>? progress)
    {
        private readonly Lock _errorsLock = new();
        private readonly List<string> _errors = [];
        private int _done;
        private int _succeeded;
        private int _failed;

        public string LocalRoot { get; } = localRoot;

        public string RemoteRoot { get; } = remoteRoot;

        public HashSet<string> EnsuredRemote { get; } = [with(StringComparer.Ordinal)];

        public bool TimestampsUnsupported { get; set; }

        public async Task StepAsync(SyncAction action, Func<Task> body)
        {
            try
            {
                await body().ConfigureAwait(true);
                Succeed(action);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Fail(action, ex.Message);
            }
        }

        public void Succeed(SyncAction action)
        {
            Interlocked.Increment(ref _succeeded);
            Report(action);
        }

        public void Fail(SyncAction action, string reason)
        {
            Interlocked.Increment(ref _failed);
            lock (_errorsLock)
            {
                _errors.Add($"{action.RelativePath}: {reason}");
            }
            Report(action);
        }

        public DirectorySyncResult Result(bool cancelled)
        {
            lock (_errorsLock)
            {
                return new(_succeeded, _failed, cancelled, TimestampsUnsupported, [.. _errors]);
            }
        }

        private void Report(SyncAction action) =>
            progress?.Report(new(Interlocked.Increment(ref _done), total, action.RelativePath));
    }
}
