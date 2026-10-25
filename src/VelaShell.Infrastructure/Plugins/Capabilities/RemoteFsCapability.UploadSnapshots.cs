using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace VelaShell.Infrastructure.Plugins.Capabilities;

internal sealed partial class RemoteFsCapability
{
    /// <summary>测试开关:不试 reflink(Linux 的 FICLONE)/ clonefile(macOS),直接整份拷贝(与 <see cref="UseDiskSnapshot" /> 同一用意)。</summary>
    internal bool UseSnapshotClone { get; init; } = true;

    /// <summary>
    /// 测试开关:替换「快照所在目录的卷还剩多少字节可用」的查询,返回 null 表示查不出(不拦,交给写入本身报错)。
    /// 为 null 时查真实的卷(与 <see cref="UseDiskSnapshot" /> 同一用意)。
    /// </summary>
    internal Func<string, long?>? SnapshotFreeSpace { get; init; }

    /// <summary>整份拷贝的快照之外至少给卷留的余量:不为一次上传把用户的盘写到一个字节不剩。</summary>
    internal const long SnapshotHeadroom = 64L * 1024 * 1024;

    private const string SnapshotPrefix = ".vela-snapshot-";

    /// <summary>快照所在的卷剩余空间不够:在拷贝之前就明确拒绝,消息里写明需要多少、还剩多少。</summary>
    private sealed class InsufficientSnapshotSpaceException(string message) : IOException(message);

    /// <summary>
    /// 一份上传源快照。Unix 上建好就删掉名字,数据靠打开着的描述符活着,宿主崩溃由内核回收;
    /// 名字没能当场删掉(极少见)时记下,释放时再删一次。Windows(只在测试开关下走到)靠关闭即删。
    /// </summary>
    private sealed class UploadSnapshot(FileStream stream, string? pendingPath) : IAsyncDisposable
    {
        internal FileStream Stream { get; } = stream;

        public async ValueTask DisposeAsync()
        {
            await Stream.DisposeAsync().ConfigureAwait(false);
            if (pendingPath is null) return;
            try { File.Delete(pendingPath); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>
    /// Linux / macOS 上传前给源做一份私有快照:Unix 的 FileShare 拦不住别的写入者,上传、摘要与提交都读这份不会再变的副本。
    /// </summary>
    /// <remarks>
    /// 快照放在源文件所在的目录里(同一个卷),名字是隐藏的 <c>.vela-snapshot-</c> 加 48 位十六进制,建好就删掉名字。
    /// 原先一律放进系统临时目录:Linux 的 /tmp 常是 tmpfs,一份几个 G 的快照直接吃内存与交换区,去掉 32 MiB 上限之后
    /// 也没有别的保护。放在源文件旁边:
    /// <list type="bullet">
    /// <item>文件系统支持 reflink(Btrfs、XFS、bcachefs 的 FICLONE,macOS APFS 的 clonefile)时快照是写时复制的克隆,
    /// 不占额外空间、瞬间完成;</item>
    /// <item>否则先查这个卷的剩余空间,不够「文件大小 + <see cref="SnapshotHeadroom" />」就在拷贝之前明确拒绝
    /// (<c>insufficient-space</c>,消息里写明需要多少、还剩多少),不把盘写满、也不写到一半才失败。</item>
    /// </list>
    /// 源目录不可写(只读目录、别人的目录、只读挂载)时退到系统临时目录,同样先查空间。快照照旧与源句柄逐字节比对摘要,
    /// 期间源变了就是 <c>source-changed</c>。建快照之前清扫这个目录里崩溃遗留的快照(口径同下载临时文件的清扫)。
    /// </remarks>
    private async Task<UploadSnapshot> CreateUploadSnapshotAsync(UploadOperation op, CancellationToken ct)
    {
        string directory = Path.GetDirectoryName(Read(op).LocalPath) ?? throw new IOException("Cannot create the upload snapshot.");
        bool sameVolume = true;
        UploadSnapshot snapshot;
        bool cloned;
        try { (snapshot, cloned) = OpenUploadSnapshot(op, directory); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            directory = Path.GetTempPath();
            sameVolume = false;
            (snapshot, cloned) = OpenUploadSnapshot(op, directory);
        }
        try
        {
            if (!cloned)
            {
                long needed = op._sourceLength + SnapshotHeadroom;
                long? free = SnapshotFreeSpace is { } probe ? probe(directory) : AvailableBytes(directory);
                if (free is { } available && available < needed)
                    throw new InsufficientSnapshotSpaceException(
                        $"Not enough free space for the upload snapshot on the {(sameVolume ? "source file's" : "temporary directory's")} volume: "
                        + $"{needed} bytes needed (the {op._sourceLength}-byte file plus {SnapshotHeadroom} bytes headroom), {available} bytes free.");
                byte[] buffer = new byte[128 * 1024];
                long last = Environment.TickCount64;
                int count;
                op._source.Position = 0;
                while ((count = await op._source.ReadAsync(buffer, ct).ConfigureAwait(false)) != 0)
                {
                    await snapshot.Stream.WriteAsync(buffer.AsMemory(0, count), ct).ConfigureAwait(false);
                    if (Environment.TickCount64 - last >= 100)
                    {
                        last = Environment.TickCount64;
                        Update(op, s => s); // 准备阶段心跳,字节数仍专指远端已确认写入。
                    }
                }
                await snapshot.Stream.FlushAsync(ct).ConfigureAwait(false);
            }
            snapshot.Stream.Position = 0;
            op._source.Position = 0;
            byte[] snapshotHash = await SHA256.HashDataAsync(snapshot.Stream, ct).ConfigureAwait(false);
            byte[] sourceHash = await SHA256.HashDataAsync(op._source, ct).ConfigureAwait(false);
            if (!CryptographicOperations.FixedTimeEquals(snapshotHash, sourceHash)) throw new SourceChangedException();
            snapshot.Stream.Position = 0;
            return snapshot;
        }
        catch
        {
            await snapshot.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// 在 <paramref name="directory" /> 里建快照文件(先清扫遗留),能克隆就克隆;返回是否已是完整克隆。
    /// 建不出来(目录不可写)抛 <see cref="IOException" /> / <see cref="UnauthorizedAccessException" />。
    /// </summary>
    private (UploadSnapshot Snapshot, bool Cloned) OpenUploadSnapshot(UploadOperation op, string directory)
    {
        SweepSnapshotOrphans(directory);
        string path = Path.Combine(directory, SnapshotPrefix + Convert.ToHexString(RandomNumberGenerator.GetBytes(24)));
        FileStream? stream = null;
        bool cloned = false;
        if (UseSnapshotClone && OperatingSystem.IsMacOS() && DarwinClone(op._source.SafeFileHandle, path))
        {
            // clonefile 按名字新建、权限位随源文件;以不共享方式打开(.NET 在 Unix 上据此加 flock 排他锁,清扫认得出它还活着)。
            try
            {
                stream = new FileStream(path, new FileStreamOptions
                {
                    Mode = FileMode.Open,
                    Access = FileAccess.Read,
                    Share = FileShare.None,
                    BufferSize = 128 * 1024,
                    Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
                });
                cloned = true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                try { File.Delete(path); }
                catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException) { }
            }
        }
        if (stream is null)
        {
            var options = new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.ReadWrite,
                Share = FileShare.None,
                BufferSize = 128 * 1024,
                Options = FileOptions.Asynchronous | FileOptions.SequentialScan | (OperatingSystem.IsWindows() ? FileOptions.DeleteOnClose : 0),
            };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            stream = new FileStream(path, options);
            if (UseSnapshotClone && OperatingSystem.IsLinux()) cloned = LinuxClone(stream.SafeFileHandle, op._source.SafeFileHandle);
        }
        if (OperatingSystem.IsWindows()) return (new(stream, null), cloned);
        // 快照是源文件的一份明文副本:名字建好就删,宿主崩在中途也不留下它。删不掉就等释放时再删。
        try
        {
            File.Delete(path);
            return (new(stream, null), cloned);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return (new(stream, path), cloned); }
    }

    /// <summary>
    /// 清掉这个目录里崩溃遗留的快照,口径与下载临时文件的清扫一致:名字与 <see cref="OpenUploadSnapshot" /> 起的逐字对得上才碰;
    /// 普通文件只删拿得到排他锁的 —— 活着的快照被它的上传以不共享方式占着(Unix 上是 flock),拿不到就留着;
    /// 同形的符号链接只删链接本身;FIFO、设备这类不碰(打开就可能卡住)。尽力而为,任何失败都只是跳过。
    /// </summary>
    private static void SweepSnapshotOrphans(string directory)
    {
        string[] candidates;
        try { candidates = [.. Directory.EnumerateFileSystemEntries(directory, SnapshotPrefix + "*").Where(path => IsSnapshotName(Path.GetFileName(path)))]; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return; }
        foreach (string path in candidates)
        {
            try
            {
                if (OperatingSystem.IsWindows())
                {
                    // 活着的快照以不共享方式开着,打不开;打得开的就是遗留,关掉即删。
                    using var orphan = new FileStream(path, new FileStreamOptions
                    {
                        Mode = FileMode.Open,
                        Access = FileAccess.Read,
                        Share = FileShare.None,
                        Options = FileOptions.DeleteOnClose,
                    });
                    continue;
                }
                if (new FileInfo(path).LinkTarget is not null)
                {
                    File.Delete(path);
                    continue;
                }
                if (ReadUnixFileMode(path) is not { } mode || (mode & 0xF000) != 0x8000) continue;
                using var candidate = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
                File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>与 <see cref="OpenUploadSnapshot" /> 起的名字逐字对得上:前缀 + 48 位大写十六进制。</summary>
    private static bool IsSnapshotName(string name) => name.Length == SnapshotPrefix.Length + 48
        && name.StartsWith(SnapshotPrefix, StringComparison.Ordinal)
        && name[SnapshotPrefix.Length..].All(char.IsAsciiHexDigitUpper);

    /// <summary>目录所在的卷对当前用户还剩多少字节可用;查不出为 null。</summary>
    private static long? AvailableBytes(string directory)
    {
        try
        {
            if (OperatingSystem.IsWindows())
                return GetDiskFreeSpaceEx(directory, out ulong available, out _, out _) ? (long)Math.Min(available, long.MaxValue) : null;
            // Unix 上 DriveInfo 收任意路径,按它所在的文件系统 statfs(f_bavail:非特权用户可用的块)。
            return new DriveInfo(directory).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return null; }
    }

    /// <summary>
    /// Linux:ioctl(快照, FICLONE, 源) 把源整份克隆进刚建的快照(Btrfs、XFS、bcachefs 等支持 reflink 的文件系统;
    /// 内核对两边加锁,克隆是一个时间点上的完整副本)。不支持(EOPNOTSUPP、EXDEV、EINVAL、ENOTTY……)时返回 false,改整份拷贝。
    /// FICLONE = _IOW(0x94, 9, int),asm-generic 的编码,x64 与 arm64 相同。
    /// </summary>
    private static bool LinuxClone(SafeFileHandle snapshot, SafeFileHandle source)
    {
        const nuint ficlone = 0x40049409;
        try { return LinuxIoctl(snapshot, ficlone, source) == 0; }
        catch (EntryPointNotFoundException) { return false; }
    }

    /// <summary>
    /// macOS:fclonefileat(源 fd, AT_FDCWD, 快照路径, CLONE_NOFOLLOW | CLONE_NOOWNERCOPY) 按源句柄克隆出快照
    /// (APFS 写时复制;属主是当前用户)。不支持(非 APFS、跨卷)时返回 false,改整份拷贝。
    /// </summary>
    private static bool DarwinClone(SafeFileHandle source, string path)
    {
        const uint cloneNoFollow = 0x1, cloneNoOwnerCopy = 0x2;
        try { return DarwinFCloneFileAt(source, PosixDownloadTarget.AtFdCwd, path, cloneNoFollow | cloneNoOwnerCopy) == 0; }
        catch (EntryPointNotFoundException) { return false; }
    }

    [LibraryImport("libc", EntryPoint = "ioctl", SetLastError = true)]
    private static partial int LinuxIoctl(SafeFileHandle file, nuint request, SafeFileHandle argument);

    [LibraryImport("libc", EntryPoint = "fclonefileat", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int DarwinFCloneFileAt(SafeFileHandle source, int directory, string destination, uint flags);

    [LibraryImport("kernel32.dll", EntryPoint = "GetDiskFreeSpaceExW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetDiskFreeSpaceEx(string directory, out ulong freeBytesAvailable, out ulong totalBytes, out ulong totalFreeBytes);
}
