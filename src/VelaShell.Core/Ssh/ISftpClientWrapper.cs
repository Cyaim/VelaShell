namespace VelaShell.Core.Ssh;

/// <summary>一次上传在关闭句柄之前要做的事。</summary>
/// <param name="ResumeOffset">大于 0 时从这里续传。</param>
/// <param name="LastWriteTime">
/// 设了就在关闭之前用同一个句柄把远端的修改时间设成它 —— 一次往返;上传完再
/// <see cref="ISftpClientWrapper.SetLastWriteTimeAsync" /> 要多两次(先取回访问时间再设)。尽力而为:设不上不影响上传。
/// </param>
/// <param name="Fsync">
/// 关闭之前要求服务端落盘(SFTP 的 <c>fsync@openssh.com</c>,服务端不支持时跳过)。落盘失败算上传失败 ——
/// 要了「断电也不能丢」却没做到,不能报成功。
/// </param>
public sealed record RemoteUploadOptions(long ResumeOffset = 0, DateTimeOffset? LastWriteTime = null, bool Fsync = false);

/// <summary>
/// SFTP 客户端的库中立抽象(参见 <see cref="ISshClientWrapper" /> 的隔离说明):
/// 目录条目以 <see cref="SftpEntry" /> 返回,失败以 SshClientException 层级抛出,
/// 不暴露任何具体 SSH 库的类型。底层库(VelaShell.Ssh)原生全异步,本接口成员一律异步,
/// 不再提供同步阻塞变体。
/// </summary>
public interface ISftpClientWrapper : IAsyncDisposable
{
    /// <summary>
    /// 指示 SFTP 会话当前是否已连接。
    /// </summary>
    bool IsConnected { get; }

    /// <summary>
    /// 建立连接时使用的超时时长。
    /// </summary>
    TimeSpan ConnectionTimeout { get; set; }

    /// <summary>
    /// 当前远端工作目录。
    /// </summary>
    string WorkingDirectory { get; }

    /// <summary>
    /// 异步建立 SFTP 连接。
    /// </summary>
    Task ConnectAsync(CancellationToken cancellationToken);

    /// <summary>
    /// 异步列出指定远端目录下的条目。
    /// 符号链接条目的 <see cref="SftpEntry.IsSymbolicLink" /> 为 true,其余字段描述链接指向的对象
    /// (断链除外,见 <see cref="SftpEntry.IsSymbolicLink" />)。
    /// </summary>
    Task<IEnumerable<SftpEntry>> ListDirectoryAsync(string path, CancellationToken cancellationToken);

    /// <summary>
    /// 异步将输入流的内容上传到指定远端路径,并可通过回调报告上传进度。
    /// 回调报告从文件开头连续收到服务端 WRITE 成功确认的字节数,不是本地缓冲/流水线已入队的字节数。
    /// 全部字节确认后仍可能等待时间戳、fsync 或 CLOSE;调用成功返回才表示上传任务成功。
    /// </summary>
    Task UploadAsync(Stream input, string path, Action<ulong>? uploadCallback = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// 异步将指定远端路径的文件下载到输出流,并可通过回调报告下载进度。
    /// </summary>
    Task DownloadAsync(string path, Stream output, Action<ulong>? downloadCallback = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// 删除指定远端文件;文件不存在时抛出 SftpOperationException 由调用方甄别,
    /// 或由上层先行 <see cref="ExistsAsync" /> 检查。
    /// </summary>
    Task DeleteFileAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>
    /// 删除指定远端目录。
    /// </summary>
    Task DeleteDirectoryAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>
    /// 创建指定远端目录。
    /// </summary>
    Task CreateDirectoryAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>
    /// 重命名或移动远端条目。
    /// </summary>
    Task RenameFileAsync(string oldPath, string newPath, CancellationToken cancellationToken = default);

    /// <summary>
    /// 使用 <c>posix-rename@openssh.com</c> 扩展重命名/移动远端条目。
    /// 部分服务器会拒绝普通的 SSH_FXP_RENAME(SSH_FX_BAD_MESSAGE),尤其跨目录
    /// 移动时;但会接受 POSIX 变体。
    /// </summary>
    Task PosixRenameFileAsync(string oldPath, string newPath, CancellationToken cancellationToken = default);

    /// <summary>严格 POSIX 原子替换;服务器不支持扩展时必须拒绝,不能回退普通 rename。</summary>
    Task AtomicReplaceFileAsync(string oldPath, string newPath, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Atomic replacement is not supported by this backend.");

    /// <summary>
    /// 服务端有没有 <see cref="AtomicReplaceFileAsync" /> 要的严格 POSIX 原子替换(<c>posix-rename@openssh.com</c>)。
    /// 没有时替换必然被拒;上传据此在写入之前就拒绝,不白传一整份字节。
    /// </summary>
    bool SupportsAtomicReplace => false;

    /// <summary>
    /// 不跟随链接地取条目自身的模式位(含文件类型)与属主/属组;条目不存在时为 <see langword="null" />(不抛)。
    /// </summary>
    Task<SftpFileMode?> GetLinkModeAsync(string path, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Raw file modes are not available from this backend.");

    /// <summary>
    /// 按原始模式位改权限:<paramref name="mode" /> 是 <c>07777</c> 以内的位(含 setuid / setgid / 粘滞位),
    /// 不是 <see cref="ChangePermissionsAsync" /> 那种把八进制数字写成十进制的约定。
    /// </summary>
    Task SetModeAsync(string path, uint mode, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Raw file modes cannot be set on this backend.");

    /// <summary>
    /// 判断指定远端路径是否存在。
    /// </summary>
    Task<bool> ExistsAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>路径所在文件系统的用量;服务端不支持或查不到时为 <see langword="null" />(不抛)。</summary>
    Task<Sftp.RemoteSpaceInfo?> GetSpaceAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>请服务端展开 <c>~</c> / <c>~用户名</c>;展开不了时为 <see langword="null" />(不抛)。</summary>
    Task<string?> ExpandPathAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>
    /// 修改远端条目的权限。<paramref name="mode" /> 采用将三个八进制数字写作十进制数的约定
    /// (例如 755、644)。
    /// </summary>
    Task ChangePermissionsAsync(string path, short mode, CancellationToken cancellationToken = default);

    /// <summary>
    /// 修改远端条目的属主与属组(SFTP setstat 的 uid/gid)。为 null 的一项沿用当前值:
    /// SFTP v3 的 uid 与 gid 共用一个标志位,只改一项也得两个一起给,所以实现要先 stat 取回另一项。
    /// </summary>
    Task ChangeOwnerAsync(string path, int? userId, int? groupId, CancellationToken cancellationToken = default);

    /// <summary>
    /// 设置远端条目的最后修改时间(SFTP setstat)。用于"保留时间戳"的上传收尾
    /// (scp -p 语义):上传完成后把远端 mtime 设回本地源文件的 mtime。
    /// </summary>
    Task SetLastWriteTimeAsync(string path, DateTimeOffset lastWriteTime, CancellationToken cancellationToken = default);

    /// <summary>
    /// 以读或写方式打开远端文件。
    /// <para>
    /// **实现必须返回可 Seek 的流**(<see cref="Stream.CanSeek" /> 为 <c>true</c>):
    /// 续传起点校验与断点续传下载都要按绝对偏移定位。部分 SSH 库默认打开的是不可定位的流,
    /// 需要显式开启相应选项。断点续传上传请改用带 <c>resumeOffset</c> 的
    /// <see cref="UploadAsync(Stream, string, long, Action{ulong}, CancellationToken)" /> 重载,
    /// 不要用 <see cref="FileMode.Append" /> 自行拼接。
    /// </para>
    /// </summary>
    Task<Stream> OpenAsync(string path, FileMode mode, FileAccess access, CancellationToken cancellationToken = default);

    /// <summary>
    /// 获取远端文件的字节大小;若文件不存在则返回 -1。
    /// </summary>
    Task<long> GetFileSizeAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>
    /// 直接 stat 单个远端条目,不存在时返回 <c>null</c>。
    /// <para>
    /// 用于替代"列举父目录再从中挑一条"的做法 —— 后者在父目录条目很多时代价极高
    /// (批量传输会退化成每个文件一次全目录列举)。
    /// </para>
    /// <para>
    /// 符号链接与 <see cref="ListDirectoryAsync" /> 同一口径:<see cref="SftpEntry.IsSymbolicLink" /> 为 true,
    /// 其余字段描述链接指向的对象;断链返回 <c>IsDirectory == false</c> 的链接条目,而不是 <c>null</c>
    /// —— 链接本身是存在的,删除它不能先报"找不到"。
    /// </para>
    /// </summary>
    Task<SftpEntry?> GetEntryAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>
    /// 在 <paramref name="linkPath" /> 创建指向 <paramref name="targetPath" /> 的符号链接。
    /// <paramref name="targetPath" /> 原样写入链接(相对路径按链接所在目录解析,与 <c>ln -s</c> 一致)。
    /// </summary>
    Task CreateSymbolicLinkAsync(string linkPath, string targetPath, CancellationToken cancellationToken = default);

    /// <summary>服务端能不能把数字 uid / gid 翻成名字(SFTP 的 <c>users-groups-by-id@openssh.com</c>)。</summary>
    bool SupportsIdLookup { get; }

    /// <summary>
    /// 请服务端把数字 uid / gid 翻成名字,与传入的一一对应;服务端不认识的、查不了的为 <see langword="null" />(不抛)。
    /// </summary>
    /// <remarks>只开了 SFTP、没有 exec 的账号查不了 passwd 库,属主一栏靠它才显示得出名字。</remarks>
    Task<(IReadOnlyList<string?> Users, IReadOnlyList<string?> Groups)> LookupNamesAsync(
        IReadOnlyList<int> userIds, IReadOnlyList<int> groupIds, CancellationToken cancellationToken = default);

    /// <summary>服务端能不能在自己那边复制文件(SFTP 的 <c>copy-data</c>):能的话复制不必下载再上传。</summary>
    bool SupportsServerCopy { get; }

    /// <summary>
    /// 在服务端内把 <paramref name="sourcePath" /> 复制到 <paramref name="destPath" />(目标已存在就覆盖),数据不出服务器。
    /// </summary>
    /// <param name="sourcePath">源文件。</param>
    /// <param name="destPath">目标文件。</param>
    /// <param name="copyCallback">已复制的累计字节数(按段报)。</param>
    /// <param name="cancellationToken">在两段之间生效。</param>
    /// <exception cref="NotSupportedException">服务端不支持(先看 <see cref="SupportsServerCopy" />)。</exception>
    Task CopyOnServerAsync(string sourcePath, string destPath, Action<ulong>? copyCallback = null, CancellationToken cancellationToken = default);

    /// <summary>上传,带着关闭句柄之前要做的事(见 <see cref="RemoteUploadOptions" />)。</summary>
    Task UploadAsync(Stream input, string path, RemoteUploadOptions options,
        Action<ulong>? uploadCallback = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// 将流上传到远端路径,写入前先定位到 <paramref name="resumeOffset"/> 字节处。
    /// 用于断点续传上传。进度回调采用包含可信续传前缀的绝对累计坐标,确认与收尾语义同普通上传。
    /// </summary>
    Task UploadAsync(Stream input, string path, long resumeOffset, Action<ulong>? uploadCallback = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// 断点续传前必须从"当前文件长度"回退的字节数。
    /// <para>
    /// 底层库为了吞吐会同时投递多个写缓冲区,它们的完成顺序不保证与偏移顺序一致。
    /// 传输中途断开时,文件长度只代表"已确认的最高偏移",并不代表它之前的每个字节都写进去了 ——
    /// 中间可能留有读作 0 的空洞。因此不能直接从文件长度处续传:必须回退一整个在途写入窗口,
    /// 使续传起点之前的数据可信。
    /// </para>
    /// <para>返回 0 表示该实现保证写入是连续的,无需回退。</para>
    /// </summary>
    long ResumeSafetyMargin { get; }
}
