using System.Buffers;
using System.Globalization;
using VelaShell.Core.Ssh;
using VelaShell.Ssh.Sftp;

namespace VelaShell.Infrastructure.Ssh;

/// <summary>
/// <see cref="ISftpClientWrapper" /> 的 VelaShell.Ssh 实现。
/// </summary>
/// <param name="connect">在主连接上开 SFTP 的工厂。</param>
public sealed class VelaSftpClientWrapper(Func<CancellationToken, ValueTask<SftpFileSystem>> connect)
    : ISftpClientWrapper
{
    /// <summary>传输每次搬运的块大小。</summary>
    /// <remarks>
    /// 只是**本地**的搬运粒度,与 SFTP 报文大小无关 —— 后者由
    /// <see cref="SftpFileSystem.BlockSize" /> 按服务端能力协商。
    /// </remarks>
    private const int CopyChunkSize = 256 * 1024;

    private readonly Func<CancellationToken, ValueTask<SftpFileSystem>> _connect =
        connect ?? throw new ArgumentNullException(nameof(connect));

    private SftpFileSystem? _fs;
    private bool _disposed;

    /// <inheritdoc />
    /// <remarks>
    /// 看的是库那条 SFTP 会话自己还活着没有(<see cref="SftpFileSystem.IsConnected" />),不只是「对象还在」:
    /// sftp-server 退出、服务端按 ChannelTimeout 关掉闲置通道之后,上层据此丢掉这个客户端重建一个,
    /// 而不是让文件面板一直坏到整条 SSH 连接重连。
    /// </remarks>
    public bool IsConnected => !_disposed && _fs is { IsConnected: true };

    /// <inheritdoc />
    /// <remarks>SFTP 复用主连接的通道,这里没有自己的建链超时;保留只为满足契约。</remarks>
    public TimeSpan ConnectionTimeout
    {
        get { ObjectDisposedException.ThrowIf(_disposed, this); return field; }
        set { ObjectDisposedException.ThrowIf(_disposed, this); field = value; }
    } = TimeSpan.FromSeconds(10);

    /// <inheritdoc />
    public string WorkingDirectory
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return field;
        }
        private set;
    } = "/";

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// 流水线写入的应答顺序不保证与偏移顺序一致,所以中断时**文件长度只代表
    /// 「已确认的最高偏移」**,它之前可能留着读作 0 的空洞。续传前必须回退
    /// 一整个在途写入窗口,使起点之前的数据可信。
    /// </para>
    /// <para>
    /// 窗口大小由库给出(<c>SftpFileSystem.MaxUnconfirmedWriteBytes</c>:单个写入流最多能有多少字节在途),
    /// 不在这里自己推算 —— 曾经按「起始在途数 × 块大小」算,而库的流水线深度会自己长大
    /// (最多到 <c>MaxPipelineDepth</c>),那时回退就不够了,错的方向是续传出一个坏文件。
    /// </para>
    /// <para>
    /// 这只是兜底:上传被打断时库交出精确的续传点(<c>SftpTransferInterruptedException.DurableLength</c>,
    /// 经 <see cref="SshInterop" /> 翻成 <see cref="VelaSftpTransferInterruptedException" />),<c>SftpService</c> 记下它,
    /// 下一次续传同一个路径时直接从那里接着传、一个字节都不回退。只有没有记下的时候(进程重启过、续的是别的会话留下的半截)
    /// 才按这个窗口回退。
    /// </para>
    /// </remarks>
    public long ResumeSafetyMargin =>
        _fs is { } fs ? fs.MaxUnconfirmedWriteBytes : (long)SftpOptions.Default.MaxPipelineDepth * 256 * 1024;

    /// <inheritdoc />
    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_fs is not null)
        {
            return;
        }

        try
        {
            _fs = await _connect(cancellationToken).ConfigureAwait(false);

            // 来自 REALPATH ".",比拼 /home/{user} 靠谱 —— 用户的家目录未必在那儿。
            WorkingDirectory = _fs.WorkingDirectory;
        }
        catch (Exception ex) when (SshInterop.Translate(ex, cancellationToken) is { } translated)
        {
            throw translated;
        }
    }

    /// <inheritdoc />
    public Task<IEnumerable<SftpEntry>> ListDirectoryAsync(string path, CancellationToken ct) =>
        GuardedAsync(async () =>
        {
            List<SftpEntry> entries = [];

            // 库这一侧已经做完了「不跟随地列、再并发补上链接目标」:
            // 直接跟随会让「这是个链接」这件事彻底消失,于是删一个指向目录的链接
            // 会变成递归删除目标目录里的东西。
            await foreach (SftpDirectoryEntry entry in
                EnsureConnected().EnumerateDirectoryAsync(path, ct).ConfigureAwait(false))
            {
                entries.Add(MapEntry(entry));
            }
            return (IEnumerable<SftpEntry>)entries;
        }, ct);

    /// <inheritdoc />
    public Task UploadAsync(Stream input, string path, Action<ulong>? uploadCallback = null,
        CancellationToken ct = default) =>
        UploadAsync(input, path, 0, uploadCallback, ct);

    /// <inheritdoc />
    public Task UploadAsync(Stream input, string path, long resumeOffset,
        Action<ulong>? uploadCallback = null, CancellationToken ct = default) =>
        UploadAsync(input, path, new RemoteUploadOptions(resumeOffset), uploadCallback, ct);

    /// <inheritdoc />
    public Task UploadAsync(Stream input, string path, RemoteUploadOptions options,
        Action<ulong>? uploadCallback = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(options);
        long resumeOffset = options.ResumeOffset;

        return GuardedAsync(async () =>
        {
            SftpFileSystem fs = EnsureConnected();

            SftpFileStream remote = resumeOffset > 0
                ? await fs.OpenAppendAsync(path, resumeOffset, cancellationToken: ct).ConfigureAwait(false)
                : await fs.OpenWriteAsync(path, cancellationToken: ct).ConfigureAwait(false);
            try
            {
                if (resumeOffset > 0)
                {
                    input.Seek(resumeOffset, SeekOrigin.Begin);
                }

                // 报的是**对端已确认**的字节数(DurableLength),不是交给流的字节数:流水线写入的 WriteAsync
                // 只等一个写槽就返回,单个流最多能有 MaxUnconfirmedWriteBytes(默认自适应深度上限 256 × 255 KiB ≈ 64 MiB)
                // 还没确认。例如约 70 MiB 的文件,按交出去的算可能迅速显示 90% 多、几十上百 MB/s,
                // 然后在下面的 Flush 里等这 64 MiB 按真实带宽排空。写槽是确认腾出来的,每次写返回时采样确认水位。
                Action<ulong>? confirmed = uploadCallback is null ? null : _ => uploadCallback((ulong)remote.DurableLength);
                await CopyAsync(input, remote, resumeOffset, confirmed, ct).ConfigureAwait(false);

                // **必须显式冲一次再关。** 流水线写入在 Flush 之前还有在途请求,
                // 不等它们落地就关,表现是「上传显示完成,远端文件尾部却缺字节」。
                await FlushReportingAsync(remote, uploadCallback, ct).ConfigureAwait(false);

                // 关闭之前用同一个句柄设修改时间(FSETSTAT,一次往返)。访问时间取「现在」:
                // 新写的文件本来就是这个值,事后 STAT 取回来的也是它。尽力而为 —— 个别服务端禁 setstat。
                if (options.LastWriteTime is { } mtime)
                {
                    try
                    {
                        await remote.SetTimesAsync(DateTimeOffset.UtcNow, mtime, ct).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is SftpException or ArgumentOutOfRangeException)
                    {
                        // 时间戳只是尽力而为(ArgumentOutOfRange:时间装不进 v3 的 32 位秒)。
                    }
                }

                // 落盘放在设时间之后:数据与刚设的时间一起落。服务端没有 fsync@openssh.com 就跳过;
                // 落盘失败不吞 —— 要了「断电也不能丢」却没做到,不能报成功。
                if (options.Fsync && fs.Capabilities.HasFsync)
                {
                    await remote.FsyncAsync(ct).ConfigureAwait(false);
                }
            }
            catch (Exception) when (ct.IsCancellationRequested)
            {
                // 调用方取消了:如实报取消。
                // 已经入队的 WRITE 不带这个令牌(库让它们跟着流走,见 spec/06 §6.4),关流会等它们落地再发 CLOSE。
                // 关流若恰好也报了错(比如通道同时断了),照 await using 的写法让它往外冒会顶掉真正的原因 ——
                // 用户按的是取消,看到的却是一条中断、还说能续传(续不续由上层的设置决定,双栏远程之间的中转
                // 就根本不续)。关流照做(要发 CLOSE 还句柄),它的异常不再往外报。
                await CloseQuietlyAsync(remote).ConfigureAwait(false);
                throw new OperationCanceledException(ct);
            }
            catch (Exception)
            {
                // 真失败:与原先 await using 的行为一致 —— 关流报出的「中断 + 精确的已确认字节数」若有,就由它往外报。
                await remote.DisposeAsync().ConfigureAwait(false);
                throw;
            }
            await remote.DisposeAsync().ConfigureAwait(false);
        }, ct);
    }

    /// <inheritdoc />
    public bool SupportsIdLookup => _fs is { } fs && fs.Capabilities.HasUsersGroupsById;

    /// <inheritdoc />
    public async Task<(IReadOnlyList<string?> Users, IReadOnlyList<string?> Groups)> LookupNamesAsync(
        IReadOnlyList<int> userIds, IReadOnlyList<int> groupIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(userIds);
        ArgumentNullException.ThrowIfNull(groupIds);
        string?[] users = new string?[userIds.Count];
        string?[] groups = new string?[groupIds.Count];
        if (_fs is not { } fs || !fs.Capabilities.HasUsersGroupsById)
        {
            return (users, groups);
        }

        // 一次最多问 MaxIdsPerLookup 个:多了分批。id 在宿主里是 int(与 SftpEntry 一致),线上是 uint32。
        int batch = SftpFileSystem.MaxIdsPerLookup;
        try
        {
            for (int start = 0; start < Math.Max(userIds.Count, groupIds.Count); start += batch)
            {
                uint[] u = [.. userIds.Skip(start).Take(batch).Select(static id => unchecked((uint)id))];
                uint[] g = [.. groupIds.Skip(start).Take(batch).Select(static id => unchecked((uint)id))];
                SftpIdNames names = await fs.LookupUserAndGroupNamesAsync(u, g, cancellationToken).ConfigureAwait(false);
                names.UserNames.ToArray().CopyTo(users, start);
                names.GroupNames.ToArray().CopyTo(groups, start);
            }
        }
        catch (Exception ex) when (ex is SftpException or VelaShell.Ssh.Diagnostics.SshException)
        {
            // 查不到就显示数字,不该让列目录失败。
        }
        return (users, groups);
    }

    /// <inheritdoc />
    public bool SupportsServerCopy => _fs is { } fs && fs.Capabilities.HasCopyData;

    /// <inheritdoc />
    public Task CopyOnServerAsync(string sourcePath, string destPath, Action<ulong>? copyCallback = null,
        CancellationToken cancellationToken = default) =>
        GuardedAsync(async () =>
        {
            SftpFileSystem fs = EnsureConnected();
            if (!fs.Capabilities.HasCopyData)
            {
                throw new NotSupportedException("The server does not support copy-data.");
            }
            IProgress<long>? progress = copyCallback is null ? null : new CopyProgress(copyCallback);
            await fs.CopyFileAsync(sourcePath, destPath, overwrite: true, progress, cancellationToken).ConfigureAwait(false);
        }, cancellationToken);

    /// <summary>把库按段报的累计字节数原样转给回调(同步,不经同步上下文)。</summary>
    private sealed class CopyProgress(Action<ulong> callback) : IProgress<long>
    {
        public void Report(long value) => callback((ulong)value);
    }

    /// <summary>冲写排空在途写入期间多久报一次已确认的字节数。</summary>
    private static readonly TimeSpan FlushProgressInterval = TimeSpan.FromMilliseconds(200);

    /// <summary>
    /// 冲写(等全部在途写入确认),等的同时按 <see cref="FlushProgressInterval" /> 报已确认的字节数。
    /// </summary>
    /// <remarks>
    /// 读完源文件之后最多还有一整个在途窗口没确认(见 UploadAsync 里的说明),慢链路上要排十几秒到一分钟 ——
    /// 这段时间里没有写可等,不另外报的话进度条就停在那儿不动,直到 Flush 返回才一下跳到 100%。
    /// </remarks>
    private static async Task FlushReportingAsync(SftpFileStream remote, Action<ulong>? progress, CancellationToken ct)
    {
        if (progress is null)
        {
            await remote.FlushAsync(ct).ConfigureAwait(false);
            return;
        }

        using var waiting = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Task flush = remote.FlushAsync(waiting.Token);
        try
        {
            // 定时器不带 ct:取消时 FlushAsync 自己会以取消结束,这里不必另外醒。
            while (await Task.WhenAny(flush, Task.Delay(FlushProgressInterval, CancellationToken.None)).ConfigureAwait(false) != flush)
            {
                progress((ulong)remote.DurableLength);
            }
            await flush.ConfigureAwait(false);
            progress((ulong)remote.DurableLength);
        }
        catch
        {
            // 回调也可能抛错:停止这一层的等待并观察 flush,不能遗留一个无人观察的故障任务。
            // 取消等待不取消已上线的 WRITE;外层仍按流的关闭期限排空并归还句柄。
            await waiting.CancelAsync().ConfigureAwait(false);
            try
            {
                await flush.ConfigureAwait(false);
            }
            catch (Exception)
            {
                // 原因仍是上面的失败(或取消),这里只观察冲写任务的收尾。
            }
            throw;
        }
    }

    /// <summary>关一个写到一半、已经不打算要了的远端流:CLOSE 照发,关流报的错不再往外抛。</summary>
    private static async ValueTask CloseQuietlyAsync(SftpFileStream remote)
    {
        try
        {
            await remote.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
            // 已经在按取消收尾了,关流的失败(在途写入被取消)正是取消本身的回声。
        }
    }

    /// <inheritdoc />
    public Task DownloadAsync(string path, Stream output, Action<ulong>? downloadCallback = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(output);

        return GuardedAsync(async () =>
        {
            await using SftpFileStream remote =
                await EnsureConnected().OpenReadAsync(path, ct).ConfigureAwait(false);

            await CopyAsync(remote, output, 0, downloadCallback, ct).ConfigureAwait(false);
            await output.FlushAsync(ct).ConfigureAwait(false);
        }, ct);
    }

    /// <summary>按块搬运并报告**累计**字节数(与契约一致,不是增量)。</summary>
    private static async Task CopyAsync(
        Stream source, Stream destination, long startOffset, Action<ulong>? progress, CancellationToken ct)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(CopyChunkSize);
        try
        {
            long transferred = startOffset;
            while (true)
            {
                int read = await source.ReadAsync(buffer.AsMemory(0, CopyChunkSize), ct).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                await destination.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                transferred += read;
                progress?.Invoke((ulong)transferred);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <inheritdoc />
    public Task DeleteFileAsync(string path, CancellationToken ct = default) =>
        GuardedAsync(async () => await EnsureConnected().DeleteFileAsync(path, ct).ConfigureAwait(false), ct);

    /// <inheritdoc />
    /// <remarks>
    /// 删的是**空目录**。递归由 <c>SftpService</c> 自己做(它要逐条报进度、
    /// 还要判断「指向目录的链接」当叶子处理),所以这里不需要递归删。
    /// </remarks>
    public Task DeleteDirectoryAsync(string path, CancellationToken ct = default) =>
        GuardedAsync(async () => await EnsureConnected().DeleteDirectoryAsync(path, ct).ConfigureAwait(false), ct);

    /// <inheritdoc />
    public Task CreateDirectoryAsync(string path, CancellationToken ct = default) =>
        GuardedAsync(async () =>
            await EnsureConnected().CreateDirectoryAsync(path, cancellationToken: ct).ConfigureAwait(false), ct);

    /// <inheritdoc />
    public Task RenameFileAsync(string oldPath, string newPath, CancellationToken ct = default) =>
        GuardedAsync(async () =>
            await EnsureConnected().RenameAsync(oldPath, newPath, overwrite: false, ct).ConfigureAwait(false), ct);

    /// <inheritdoc />
    /// <remarks>
    /// 这一条以前是**假的** —— 上一版底层库没暴露 posix-rename,实现直接转调普通重命名,
    /// 于是「普通 RENAME 被拒、POSIX 变体能过」的那些服务端照样失败。
    /// 现在真的发 <c>posix-rename@openssh.com</c>(服务端支持时),它同时还是原子覆盖。
    /// </remarks>
    public Task PosixRenameFileAsync(string oldPath, string newPath, CancellationToken ct = default) =>
        GuardedAsync(async () =>
        {
            SftpFileSystem fs = EnsureConnected();

            // 能力要先问:不支持时退回普通重命名,而不是抛一个用户看不懂的"不支持"。
            await fs.RenameAsync(oldPath, newPath, fs.Capabilities.HasPosixRename, ct).ConfigureAwait(false);
        }, ct);

    /// <inheritdoc />
    public Task AtomicReplaceFileAsync(string oldPath, string newPath, CancellationToken ct = default) =>
        GuardedAsync(async () =>
        {
            SftpFileSystem fs = EnsureConnected();
            if (!fs.Capabilities.HasPosixRename)
                throw new NotSupportedException("The server does not support atomic POSIX replacement.");
            await fs.RenameAsync(oldPath, newPath, overwrite: true, ct).ConfigureAwait(false);
        }, ct);

    /// <inheritdoc />
    public bool SupportsAtomicReplace => _fs is { } fs && fs.Capabilities.HasPosixRename;

    /// <inheritdoc />
    /// <remarks>
    /// LSTAT 原样交出:类型位留着(调用方要分辨普通文件与链接),uid / gid 只在服务端报了才给 ——
    /// 没报的当成 0,等于把文件交给 root(同 <see cref="ChangeOwnerAsync" /> 的说明)。
    /// </remarks>
    public Task<SftpFileMode?> GetLinkModeAsync(string path, CancellationToken ct = default) =>
        GuardedAsync(async () =>
        {
            try
            {
                SftpFileAttributes attributes = await EnsureConnected().GetLinkAttributesAsync(path, ct).ConfigureAwait(false);
                return new SftpFileMode(
                    attributes.HasPermissions ? attributes.Permissions : null,
                    attributes.HasUidGid ? unchecked((int)attributes.UserId) : null,
                    attributes.HasUidGid ? unchecked((int)attributes.GroupId) : null);
            }
            catch (SftpException ex) when (ex.IsNotFound)
            {
                return (SftpFileMode?)null;
            }
        }, ct);

    /// <inheritdoc />
    public Task SetModeAsync(string path, uint mode, CancellationToken ct = default) =>
        GuardedAsync(async () =>
            await EnsureConnected().SetPermissionsAsync(path, mode & 0xFFF, ct).ConfigureAwait(false), ct);

    /// <inheritdoc />
    public Task<bool> ExistsAsync(string path, CancellationToken ct = default) =>
        GuardedAsync(async () => await EnsureConnected().ExistsAsync(path, ct).ConfigureAwait(false), ct);

    /// <inheritdoc />
    /// <remarks>服务端没有 <c>expand-path@openssh.com</c> 与 <c>home-directory</c>、用户或路径不存在时都只是展开不了。</remarks>
    public async Task<string?> ExpandPathAsync(string path, CancellationToken ct = default)
    {
        try
        {
            return await EnsureConnected().ExpandPathAsync(path, ct).ConfigureAwait(false);
        }
        catch (SftpException)
        {
            return null;
        }
    }

    /// <inheritdoc />
    /// <remarks>服务端没有 <c>statvfs@openssh.com</c> 时不发请求;请求失败(路径不在了之类)也只是查不到。</remarks>
    public async Task<Core.Sftp.RemoteSpaceInfo?> GetSpaceAsync(string path, CancellationToken ct = default)
    {
        SftpFileSystem fs = EnsureConnected();
        if (!fs.Capabilities.HasStatVfs)
        {
            return null;
        }

        try
        {
            SftpFileSystemInfo info = await fs.GetFileSystemInfoAsync(path, ct).ConfigureAwait(false);
            return new Core.Sftp.RemoteSpaceInfo(info.TotalBytes, info.AvailableBytes, info.IsReadOnly);
        }
        catch (SftpException)
        {
            return null;
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// <paramref name="mode" /> 按契约是「把三个八进制数字写成十进制」(755、644),
    /// 所以要按 8 进制解回去,不能直接当数值用。
    /// </remarks>
    public Task ChangePermissionsAsync(string path, short mode, CancellationToken ct = default) =>
        GuardedAsync(async () =>
        {
            uint permissions = Convert.ToUInt32(mode.ToString(CultureInfo.InvariantCulture), 8);
            await EnsureConnected().SetPermissionsAsync(path, permissions, ct).ConfigureAwait(false);
        }, ct);

    /// <inheritdoc />
    /// <remarks>
    /// 只改一项时先 stat 取回另一项再一并写回(uid 与 gid 共用一个标志位)。
    /// 服务端 stat 时没报 uid/gid 就不写:拿不到的那一项会被当成 0,等于把文件交给 root。
    /// </remarks>
    public Task ChangeOwnerAsync(string path, int? userId, int? groupId, CancellationToken ct = default) =>
        GuardedAsync(async () =>
        {
            if (userId is null && groupId is null)
            {
                return;
            }
            SftpFileSystem fs = EnsureConnected();
            uint uid;
            uint gid;
            if (userId is { } u && groupId is { } g)
            {
                (uid, gid) = (unchecked((uint)u), unchecked((uint)g));
            }
            else
            {
                SftpFileAttributes current = await fs.GetAttributesAsync(path, ct).ConfigureAwait(false);
                if (!current.HasUidGid)
                {
                    throw new NotSupportedException($"The server did not report the current owner of {path}; set owner and group together.");
                }
                uid = userId is { } newUid ? unchecked((uint)newUid) : current.UserId;
                gid = groupId is { } newGid ? unchecked((uint)newGid) : current.GroupId;
            }
            var attributes = new SftpFileAttributes
            {
                Flags = SftpAttributeFields.UidGid,
                UserId = uid,
                GroupId = gid,
            };
            await fs.SetAttributesAsync(path, attributes, ct).ConfigureAwait(false);
        }, ct);

    /// <inheritdoc />
    /// <remarks>
    /// 库那一侧会**先把当前的 atime 取回来**再一并写回 —— SFTP 的 atime 与 mtime
    /// 共用一个标志位,只给一个会把另一个抹成 1970 年。
    /// </remarks>
    public Task SetLastWriteTimeAsync(string path, DateTimeOffset lastWriteTime, CancellationToken ct = default) =>
        GuardedAsync(async () =>
            await EnsureConnected().SetLastWriteTimeAsync(path, lastWriteTime, ct).ConfigureAwait(false), ct);

    /// <inheritdoc />
    /// <remarks>
    /// 返回的 <see cref="SftpFileStream" /> <b>本来就可 Seek</b>,不需要像上一版那样
    /// 显式打开两个互相独立的开关(<c>Seekable</c> + <c>CacheLength</c>)——
    /// 那两个开关缺一个,症状就是「上传一半取消后再传同一个文件必报错」。
    /// <para>
    /// 交出去的是 <see cref="TranslatingSftpStream" />:调用方在自己的循环里读写(续传下载、双栏中转、插件的流式读取),
    /// 那些调用不经过这里的 <c>GuardedAsync</c>,断线时库的异常要在流上就翻成中立类型,失败重试才认得出是断线。
    /// </para>
    /// </remarks>
    public Task<Stream> OpenAsync(string path, FileMode mode, FileAccess access, CancellationToken ct = default) =>
        GuardedAsync<Stream>(async () =>
        {
            SftpFileSystem fs = EnsureConnected();
            bool canRead = access is FileAccess.Read or FileAccess.ReadWrite;
            bool canWrite = access is FileAccess.Write or FileAccess.ReadWrite;

            if (mode == FileMode.Append)
            {
                long end = await GetFileSizeAsync(path, ct).ConfigureAwait(false);
                return new TranslatingSftpStream(
                    await fs.OpenAppendAsync(path, Math.Max(0, end), cancellationToken: ct).ConfigureAwait(false));
            }

            SftpOpenModes open = (canRead ? SftpOpenModes.Read : SftpOpenModes.None)
                                | (canWrite ? SftpOpenModes.Write : SftpOpenModes.None);

            open |= mode switch
            {
                FileMode.CreateNew => SftpOpenModes.Create | SftpOpenModes.Exclusive,
                // Create 要求截断旧内容,否则新内容比旧文件短时会残留旧尾部。
                FileMode.Create => SftpOpenModes.Create | SftpOpenModes.Truncate,
                FileMode.OpenOrCreate => SftpOpenModes.Create,
                _ => SftpOpenModes.None,
            };

            SftpFileStream stream = await fs.OpenAsync(path, open, cancellationToken: ct).ConfigureAwait(false);

            // FileMode.Truncate 是「已有的文件截成 0、不存在就失败」。v3 的 TRUNC 必须配 CREAT(不存在就会建出来),
            // 所以照字面翻不出来:不带 CREAT 地打开(不存在照样失败),再截成 0。
            if (mode == FileMode.Truncate)
            {
                try
                {
                    await stream.SetLengthAsync(0, ct).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    await stream.DisposeAsync().ConfigureAwait(false);
                    throw;
                }
            }
            return new TranslatingSftpStream(stream);
        }, ct);

    /// <inheritdoc />
    public Task<long> GetFileSizeAsync(string path, CancellationToken ct = default) =>
        GuardedAsync(async () =>
        {
            try
            {
                SftpFileAttributes attributes =
                    await EnsureConnected().GetAttributesAsync(path, ct).ConfigureAwait(false);
                return attributes.HasSize ? (long)attributes.Size : -1L;
            }
            catch (SftpException ex) when (ex.IsNotFound)
            {
                // 契约:不存在返回 -1。
                return -1L;
            }
        }, ct);

    /// <inheritdoc />
    /// <remarks>
    /// 先 lstat(不跟随):只有这样才看得出路径本身是不是链接。是链接再补跟随的 stat
    /// 与 readlink,于是 <see cref="SftpEntry.IsDirectory" /> 仍描述链接指向的对象,
    /// 而删除/复制据 <see cref="SftpEntry.IsSymbolicLink" /> 不沿链接递归。
    /// 非链接仍是一次往返。
    /// </remarks>
    public Task<SftpEntry?> GetEntryAsync(string path, CancellationToken ct = default) =>
        GuardedAsync(async () =>
        {
            // 库给的完整条目:链接保留「是链接」这个事实、并发补上目标与跟随后的属性,断链保留链接自身的属性,
            // 名字按 SFTP 的「/」取(不用本机的 Path.GetFileName —— Windows 上它把远端名字里合法的「\」当分隔符)。
            SftpDirectoryEntry? entry = await EnsureConnected().GetEntryAsync(path, ct).ConfigureAwait(false);
            return entry is { } found ? MapEntry(found) : null;
        }, ct);

    /// <inheritdoc />
    /// <remarks>
    /// 参数顺序按人话来(先在哪建、再指向哪)。OpenSSH 服务端把 SSH_FXP_SYMLINK 的两个参数
    /// 实现反了(bugzilla #861),库那一侧已经按 OpenSSH 的顺序发包 —— 这里**不要**再对调一次。
    /// </remarks>
    public Task CreateSymbolicLinkAsync(string linkPath, string targetPath, CancellationToken ct = default) =>
        GuardedAsync(async () =>
            await EnsureConnected().CreateSymbolicLinkAsync(linkPath, targetPath, ct).ConfigureAwait(false), ct);

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;

        SftpFileSystem? target = Interlocked.Exchange(ref _fs, null);
        if (target is not null)
        {
            await DisposeQuietlyAsync(target).ConfigureAwait(false);
        }
        GC.SuppressFinalize(this);
    }

    // ------------------------------------------------------------ 映射

    internal static SftpEntry MapEntry(SftpDirectoryEntry entry) =>
        MapEntry(entry.FullPath, entry.Attributes, entry.IsSymbolicLink, entry.LinkTarget, entry.Name);

    internal static SftpEntry MapEntry(
        string fullPath, SftpFileAttributes attributes, bool isSymbolicLink, string? linkTarget, string name)
    {
        // PermissionBits 已经去掉了高位的文件类型（0xF000）——
        // 直接用 Permissions 在这九位上结果一样，但读起来像是在碰类型位。
        uint permissions = attributes.PermissionBits;

        return new SftpEntry
        {
            Name = name,
            FullName = fullPath,
            Length = (long)attributes.Size,
            IsDirectory = attributes.IsDirectory,
            IsSymbolicLink = isSymbolicLink,
            LinkTarget = linkTarget,

            // 必须 LocalDateTime 而非 .DateTime:后者把 DateTimeOffset 的偏移剥掉,留下
            // "UTC 墙钟数 + Kind=Unspecified" —— 文件浏览器显示成 +0 时区,下载保留时间戳
            // (File.SetLastWriteTime 按本地解读)还会再错一次时差。
            LastWriteTime = attributes.LastWriteTime.LocalDateTime,
            UserId = (int)attributes.UserId,
            GroupId = (int)attributes.GroupId,

            OwnerCanRead = (permissions & 0b100_000_000) != 0,
            OwnerCanWrite = (permissions & 0b010_000_000) != 0,
            OwnerCanExecute = (permissions & 0b001_000_000) != 0,
            GroupCanRead = (permissions & 0b000_100_000) != 0,
            GroupCanWrite = (permissions & 0b000_010_000) != 0,
            GroupCanExecute = (permissions & 0b000_001_000) != 0,
            OthersCanRead = (permissions & 0b000_000_100) != 0,
            OthersCanWrite = (permissions & 0b000_000_010) != 0,
            OthersCanExecute = (permissions & 0b000_000_001) != 0,
        };
    }

    // ------------------------------------------------------------ 管道

    private SftpFileSystem EnsureConnected()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _fs ?? throw new InvalidOperationException("Not connected.");
    }

    /// <summary>
    /// 统一异常翻译:库异常经 <see cref="SshInterop.Translate" /> 翻译为 Core 中立异常。
    /// </summary>
    /// <remarks>
    /// 曾经还把「释放竞态下的 NRE」归一成 <see cref="ObjectDisposedException" /> —— 那是换库前留下的。
    /// 现在的库在操作在途时被释放只以「已释放」或 SFTP / 通道的错误结束(库的 SftpTests 里有用例盯着),
    /// 真冒出一个 NRE 就是 bug,该原样露出来,而不是被说成「已释放」。
    /// </remarks>
    private static async Task<T> GuardedAsync<T>(Func<Task<T>> operation, CancellationToken ct = default)
    {
        try
        {
            return await operation().ConfigureAwait(false);
        }
        catch (Exception ex) when (SshInterop.Translate(ex, ct) is { } translated)
        {
            throw translated;
        }
    }

    private static async Task GuardedAsync(Func<Task> operation, CancellationToken ct = default) =>
        await GuardedAsync(async () =>
        {
            await operation().ConfigureAwait(false);
            return true;
        }, ct).ConfigureAwait(false);

    private static async ValueTask DisposeQuietlyAsync(SftpFileSystem fs)
    {
        try
        {
            await fs.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
            // 释放路径不抛:通道可能已经随连接一起没了。
        }
    }
}
