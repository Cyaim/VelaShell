using VelaShell.Core.Models;
using VelaShell.Core.Sftp;

namespace VelaShell.Core.DirectorySync;

/// <summary>
/// 把本地目录或远端目录扫成一组以相对路径为键的 <see cref="SyncItem" />,供 <see cref="DirectoryComparer" /> 配对。
/// </summary>
/// <remarks>
/// <b>两边都不跟随指向目录的链接</b>,与文件夹下载(<c>rsync -r</c> 不带 <c>-L</c>)同一口径:
/// 链接可以指回祖先形成无限展开,也可以指向 <c>/</c>;更要紧的是镜像 + 删除多余文件时,
/// 沿链接进去等于拿链接目标那棵树去和本地比,删的也是那棵树。跳过的个数如实报出来。
/// 指向文件的远端链接照常当文件处理(大小与时间来自目标),与下载的口径一致。
/// </remarks>
public static class DirectoryTreeScanner
{
    private static readonly EnumerationOptions LocalListing = new()
    {
        AttributesToSkip = 0,
        IgnoreInaccessible = true,
        MatchType = MatchType.Win32,
        RecurseSubdirectories = false,
    };

    /// <summary>扫描本地目录。</summary>
    /// <param name="root">要扫描的目录(同步根,或保持最新时的某个子目录)。</param>
    /// <param name="mask">文件掩码;被排除的目录不进入。</param>
    /// <param name="recursive">false 时只列一层(子目录本身仍作为条目返回)。</param>
    /// <param name="relativePrefix">给相对路径加的前缀:扫子目录时用它让路径仍相对于同步根。</param>
    /// <param name="progress">每扫完一个目录报一次累计目录数。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public static Task<SyncTree> ScanLocalAsync(
        string root,
        SyncFileMask mask,
        bool recursive = true,
        string relativePrefix = "",
        IProgress<int>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentNullException.ThrowIfNull(mask);
        return Task.Run(() => ScanLocal(root, mask, recursive, relativePrefix, progress, cancellationToken), cancellationToken);
    }

    private static SyncTree ScanLocal(
        string root,
        SyncFileMask mask,
        bool recursive,
        string relativePrefix,
        IProgress<int>? progress,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException(root);
        }
        var items = new List<SyncItem>();
        int skippedLinks = 0;
        int scanned = 0;
        var pending = new Stack<(string Path, string Relative)>();
        pending.Push((root, relativePrefix.Trim('/')));
        while (pending.TryPop(out (string Path, string Relative) directory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (FileSystemInfo info in new DirectoryInfo(directory.Path).EnumerateFileSystemInfos("*", LocalListing))
            {
                cancellationToken.ThrowIfCancellationRequested();
                bool isDirectory;
                long size;
                DateTime modified;
                try
                {
                    // 只认真正的链接(符号链接、目录联接),不认所有重解析点:OneDrive「按需下载」的占位文件
                    // 也带 ReparsePoint 属性,把它们当链接跳过会让整个同步盘里的文件都"消失"。
                    if (info.Attributes.HasFlag(FileAttributes.ReparsePoint) && info.LinkTarget is not null)
                    {
                        skippedLinks++;
                        continue;
                    }
                    isDirectory = info.Attributes.HasFlag(FileAttributes.Directory);
                    size = isDirectory ? 0 : ((FileInfo)info).Length;
                    modified = info.LastWriteTimeUtc;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // 列举途中被删掉、或读属性被拒:跳过这一条,不连累整棵树。
                    continue;
                }
                string relative = Combine(directory.Relative, info.Name);
                if (!mask.Includes(relative, isDirectory))
                {
                    continue;
                }
                items.Add(new(relative, info.FullName, isDirectory, size, modified));
                if (isDirectory && recursive)
                {
                    pending.Push((info.FullName, relative));
                }
            }
            progress?.Report(++scanned);
        }
        return new(items, skippedLinks);
    }

    /// <summary>扫描远端目录。</summary>
    /// <param name="sftp">远端文件服务(SFTP / FTP / 插件协议都走它)。</param>
    /// <param name="sessionId">会话标识。</param>
    /// <param name="root">远端目录的绝对路径。</param>
    /// <param name="mask">文件掩码;被排除的目录不进入。</param>
    /// <param name="inferPrecision">
    /// 是否按原始时间推断精度(见 <see cref="SyncTime.InferPrecision" />)。FTP 与插件协议传 true,SFTP 传 false。
    /// </param>
    /// <param name="recursive">false 时只列一层。</param>
    /// <param name="relativePrefix">相对路径前缀。</param>
    /// <param name="serverClockOffset">
    /// 服务器时钟的 UTC 偏移(FTP 连接配置里的「服务器时区」,见 <see cref="SyncTime.ServerClockOffset" />);
    /// null = 与本机同一时区。只换算不带时区的时间(LIST),MLSD 给的 UTC 原样用。
    /// </param>
    /// <param name="progress">每扫完一个目录报一次累计目录数。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public static async Task<SyncTree> ScanRemoteAsync(
        ISftpService sftp,
        Guid sessionId,
        string root,
        SyncFileMask mask,
        bool inferPrecision,
        bool recursive = true,
        string relativePrefix = "",
        TimeSpan? serverClockOffset = null,
        IProgress<int>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sftp);
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentNullException.ThrowIfNull(mask);
        var items = new List<SyncItem>();
        int skippedLinks = 0;
        int scanned = 0;
        var pending = new Stack<(string Path, string Relative)>();
        pending.Push((root, relativePrefix.Trim('/')));
        while (pending.TryPop(out (string Path, string Relative) directory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            List<RemoteFileInfo> listed = await sftp.ListDirectoryAsync(sessionId, directory.Path, cancellationToken).ConfigureAwait(false);
            foreach (RemoteFileInfo entry in listed)
            {
                if (entry.Name is "" or "." or "..")
                {
                    continue;
                }
                if (entry is { IsSymbolicLink: true, IsDirectory: true })
                {
                    skippedLinks++;
                    continue;
                }
                string relative = Combine(directory.Relative, entry.Name);
                if (!mask.Includes(relative, entry.IsDirectory))
                {
                    continue;
                }
                SyncTimePrecision precision = inferPrecision
                    ? SyncTime.InferPrecision(entry.LastModified)
                    : entry.LastModified.Year < 1971 ? SyncTimePrecision.Unknown : SyncTimePrecision.Second;
                string fullPath = string.IsNullOrEmpty(entry.FullPath) ? CombineRemote(directory.Path, entry.Name) : entry.FullPath;
                // 精度按原始时间推断(换算之前:LIST 的「只有日期」是 00:00,挪过偏移就认不出来了)。
                items.Add(new(
                    relative,
                    fullPath,
                    entry.IsDirectory,
                    entry.IsDirectory ? 0 : entry.Size,
                    precision == SyncTimePrecision.Unknown ? DateTime.MinValue : SyncTime.ToUtc(entry.LastModified, serverClockOffset),
                    precision)
                {
                    CalendarOffset = SyncTime.CalendarOffsetFor(entry.LastModified, serverClockOffset),
                });
                if (entry.IsDirectory && recursive)
                {
                    pending.Push((fullPath, relative));
                }
            }
            progress?.Report(++scanned);
        }
        return new(items, skippedLinks);
    }

    /// <summary>远端路径拼接:分隔符恒为 <c>/</c>,与本机是不是 Windows 无关。</summary>
    public static string CombineRemote(string directory, string relative)
    {
        string tail = relative.Trim('/');
        if (tail.Length == 0)
        {
            return directory;
        }
        return directory == "/" ? "/" + tail : directory.TrimEnd('/') + "/" + tail;
    }

    private static string Combine(string relativeDirectory, string name) =>
        relativeDirectory.Length == 0 ? name : relativeDirectory + "/" + name;
}
