using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace VelaShell.Infrastructure.Plugins.Capabilities;

internal sealed partial class RemoteFsCapability
{
    /// <summary>
    /// 打开之后核验源句柄就是审批路径上的那个文件:比文件身份(Windows 卷序列号 + 文件 id,Linux / macOS dev + inode),
    /// 不比路径字符串。原先拿句柄的最终路径与审批路径逐字比,同一个文件会被误拒:映射盘查出来是 <c>\\server\share\…</c>、
    /// subst 盘查出来是底下的真实目录,macOS 大小写不敏感、Unicode 规范化不敏感的卷上查出来的是磁盘上存的那种写法
    /// (大小写不同、NFC 与 NFD 不同);同一个文件的另一个硬链接名也对不上。(8.3 短名不在此列:GetFullPath 碰到 <c>~</c> 已展开成长名。)
    /// </summary>
    /// <remarks>
    /// 拿来比的那一项必须是「不经任何链接」从审批路径走到的,否则核验本身也能被临时换进来的链接骗过
    /// (打开时经链接、核验时又经同一条链接,两边身份照样一致):
    /// Windows 逐级以遍历 + 读属性、共享读写不共享删除的方式钉住各级目录(钉着期间改不了名、挪不走,也就换不进链接),
    /// 每一级与末级都不许是名字代理类重解析点;Linux 从 "/" 起逐级 <c>openat(O_PATH | O_NOFOLLOW | O_DIRECTORY)</c>,
    /// 末级用 <c>statx(AT_SYMLINK_NOFOLLOW)</c>;macOS 用 <c>fstatat(AT_SYMLINK_NOFOLLOW_ANY)</c>,路径上任何一级是符号链接都失败。
    /// 查不出身份(文件系统不给文件 id)一律拒绝,不退回字符串比较。
    /// </remarks>
    internal static void VerifyOpenedSource(SafeFileHandle source, string expected)
    {
        if (OperatingSystem.IsWindows()) VerifyWindowsSource(source, expected);
        else if (OperatingSystem.IsLinux() && PosixDownloadTarget.IsSupported) VerifyLinuxSource(source, expected);
        else if (OperatingSystem.IsMacOS() && PosixDownloadTarget.IsSupported) VerifyDarwinSource(source, expected);
        else throw new PlatformNotSupportedException("Stable upload source verification is unavailable.");
    }

    private static void VerifyWindowsSource(SafeFileHandle source, string expected)
    {
        string root = Path.GetPathRoot(expected) ?? throw new IOException("Cannot verify the opened source object.");
        string parent = Path.GetDirectoryName(expected) ?? throw new IOException("Cannot verify the opened source object.");
        var pinned = new List<SafeFileHandle>();
        try
        {
            // 盘符根、UNC 共享根本身改不了名,不用钉;从它下面第一级开始。
            string current = root;
            foreach (string part in parent[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, part);
                // FILE_TRAVERSE | FILE_READ_ATTRIBUTES:只读属性的句柄不参与共享检查,带上遍历权限钉住才算数(与下载钉目录链同一做法);
                // OPEN_REPARSE_POINT:句柄描述这一级本身,不是它指向的地方。
                SafeFileHandle directory = OpenAttributesHandle(current, 0xA0, 3, 0, 3, 0x02200000, 0);
                int error = Marshal.GetLastPInvokeError();
                pinned.Add(directory);
                if (directory.IsInvalid) throw new IOException("Cannot verify the upload source directory.", new Win32Exception(error));
                if (IsLink(directory)) throw new NotSupportedException("Linked source paths are not supported for stable uploads.");
            }
            // 末级只读属性、全共享:不跟审批之后正常打开它的程序抢共享模式。
            using SafeFileHandle leaf = OpenAttributesHandle(expected, 0x80, 7, 0, 3, 0x02200000, 0);
            int leafError = Marshal.GetLastPInvokeError();
            if (leaf.IsInvalid) throw new IOException("Cannot verify the opened source object.", new Win32Exception(leafError));
            if (IsLink(leaf)) throw new NotSupportedException("Linked source paths are not supported for stable uploads.");
            if (!SameWindowsFile(source, leaf)) throw new IOException("The opened source does not match the approved path.");
        }
        finally
        {
            foreach (SafeFileHandle handle in pinned) handle.Dispose();
        }
    }

    /// <summary>
    /// 两个句柄是不是同一个文件。先取 FILE_ID_INFO(64 位卷序列号 + 128 位文件 id;ReFS 的 id 在 64 位里放不下),
    /// 文件系统或重定向器不给时两边一起退到 BY_HANDLE_FILE_INFORMATION(32 位卷序列号 + 64 位文件索引)。
    /// id 为 0 或全 1(无效 id)按查不出算,拒绝。
    /// </summary>
    private static bool SameWindowsFile(SafeFileHandle left, SafeFileHandle right)
    {
        if (TryReadFileId(left, out FileIdInformation a) && TryReadFileId(right, out FileIdInformation b))
            return a.VolumeSerialNumber == b.VolumeSerialNumber && a.FileIdLow == b.FileIdLow && a.FileIdHigh == b.FileIdHigh;
        if (!GetFileInformationByHandle(left, out ByHandleFileInformation c) || !GetFileInformationByHandle(right, out ByHandleFileInformation d)
            || !ValidFileIndex(c) || !ValidFileIndex(d))
        {
            throw new IOException("Cannot verify the opened source object.");
        }

        return c.VolumeSerialNumber == d.VolumeSerialNumber && c.FileIndexHigh == d.FileIndexHigh && c.FileIndexLow == d.FileIndexLow;

        static bool ValidFileIndex(ByHandleFileInformation info)
            => (info.FileIndexHigh | info.FileIndexLow) != 0 && (info.FileIndexHigh & info.FileIndexLow) != uint.MaxValue;
    }

    private static bool TryReadFileId(SafeFileHandle handle, out FileIdInformation information)
        => GetFileIdInformation(handle, 18, out information, (uint)Marshal.SizeOf<FileIdInformation>())   // FileIdInfo = 18
           && (information.FileIdLow | information.FileIdHigh) != 0
           && (information.FileIdLow & information.FileIdHigh) != ulong.MaxValue;

    /// <summary>FILE_ID_INFO:ULONGLONG VolumeSerialNumber + FILE_ID_128(16 字节)。</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct FileIdInformation
    {
        public ulong VolumeSerialNumber;
        public ulong FileIdLow;
        public ulong FileIdHigh;
    }

    /// <summary>BY_HANDLE_FILE_INFORMATION(52 字节,三个 FILETIME 按两个 DWORD 排)。</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public uint CreationLow, CreationHigh, AccessLow, AccessHigh, WriteLow, WriteHigh;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh, FileSizeLow, NumberOfLinks;
        public uint FileIndexHigh, FileIndexLow;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetFileIdInformation(SafeFileHandle file, int informationClass, out FileIdInformation information, uint size);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetFileInformationByHandle(SafeFileHandle file, out ByHandleFileInformation information);

    private const uint PosixTypeMask = 0xF000, PosixLinkFile = 0xA000;

    /// <summary>
    /// Linux:句柄与「从 / 起逐级 O_NOFOLLOW 走到的末级」各取一次 statx,比 (stx_dev_major, stx_dev_minor, stx_ino)。
    /// struct statx 各架构布局一致:stx_mask 在 0、stx_mode 在 28、stx_ino 在 32、stx_dev_major / minor 在 136 / 140。
    /// </summary>
    private static void VerifyLinuxSource(SafeFileHandle source, string expected)
    {
        const int atEmptyPath = 0x1000;
        LinuxIdentity opened = ReadLinuxIdentity(source, "", atEmptyPath)
            ?? throw new IOException("Cannot verify the opened source object.");
        string parent = Path.GetDirectoryName(expected) ?? throw new IOException("Cannot verify the opened source object.");
        using SafeFileHandle directory = OpenLinuxSourceChain(parent);
        LinuxIdentity named = ReadLinuxIdentity(directory, Path.GetFileName(expected), PosixDownloadTarget.AtSymlinkNoFollow)
            ?? throw new IOException("Cannot verify the opened source object.", new Win32Exception(Marshal.GetLastPInvokeError()));
        if ((named.Mode & PosixTypeMask) == PosixLinkFile)
            throw new NotSupportedException("Linked source paths are not supported for stable uploads.");
        if (named.DevMajor != opened.DevMajor || named.DevMinor != opened.DevMinor || named.Inode != opened.Inode)
            throw new IOException("The opened source does not match the approved path.");
    }

    private readonly record struct LinuxIdentity(uint DevMajor, uint DevMinor, ulong Inode, uint Mode);

    private static LinuxIdentity? ReadLinuxIdentity(SafeFileHandle directory, string name, int flags)
    {
        const uint statxType = 0x1, statxInode = 0x100;
        byte[] buffer = new byte[256];
        bool referenced = false;
        try
        {
            directory.DangerousAddRef(ref referenced);
            if (LinuxStatX((int)directory.DangerousGetHandle(), name, flags, statxType | statxInode, buffer) != 0) return null;
        }
        catch (EntryPointNotFoundException) { return null; }
        finally { if (referenced) directory.DangerousRelease(); }
        if ((BitConverter.ToUInt32(buffer, 0) & (statxType | statxInode)) != (statxType | statxInode)) return null;
        return new(BitConverter.ToUInt32(buffer, 136), BitConverter.ToUInt32(buffer, 140), BitConverter.ToUInt64(buffer, 32), BitConverter.ToUInt16(buffer, 28));
    }

    /// <summary>
    /// 从 "/" 起逐级 openat(O_PATH | O_NOFOLLOW | O_DIRECTORY) 走到父目录(与下载钉目录链同一套常量):只走不读,
    /// 只给了 x 权限的祖先也走得过去;任何一级是符号链接都打不开 —— 解析之后才换进来的链接按链接拒绝。
    /// </summary>
    private static SafeFileHandle OpenLinuxSourceChain(string parent)
    {
        int flags = PosixDownloadTarget.ODirectory | PosixDownloadTarget.ONoFollow | PosixDownloadTarget.OCloseOnExec | PosixDownloadTarget.OPath;
        int root = SourceOpenAt(PosixDownloadTarget.AtFdCwd, "/", flags);
        if (root < 0) throw new IOException("Cannot verify the upload source directory.", new Win32Exception(Marshal.GetLastPInvokeError()));
        var current = new SafeFileHandle(root, ownsHandle: true);
        try
        {
            foreach (string part in parent.Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                if (part is "." or "..") throw new IOException("Cannot verify the upload source directory.");
                int fd = SourceOpenAt((int)current.DangerousGetHandle(), part, flags);
                if (fd < 0)
                {
                    var error = new Win32Exception(Marshal.GetLastPInvokeError());
                    if (ReadLinuxIdentity(current, part, PosixDownloadTarget.AtSymlinkNoFollow) is { } entry && (entry.Mode & PosixTypeMask) == PosixLinkFile)
                        throw new NotSupportedException("Linked source paths are not supported for stable uploads.", error);
                    throw new IOException("Cannot verify the upload source directory.", error);
                }
                current.Dispose();
                current = new SafeFileHandle(fd, ownsHandle: true);
            }
            return current;
        }
        catch { current.Dispose(); throw; }
    }

    /// <summary>
    /// macOS:句柄取 fstat,审批路径取 fstatat(AT_SYMLINK_NOFOLLOW_ANY),比 (st_dev, st_ino)。
    /// AT_SYMLINK_NOFOLLOW_ANY(sys/fcntl.h 0x0800,macOS 11 起):路径上任何一级是符号链接都以 ELOOP 失败,
    /// 不要求对各级目录有读权限(与逐级 openat(O_RDONLY) 不同)。64 位 inode 的 struct stat:st_dev 在 0(int32)、
    /// st_mode 在 4、st_ino 在 8;x64 上符号名带 $INODE64。
    /// </summary>
    private static void VerifyDarwinSource(SafeFileHandle source, string expected)
    {
        const int symlinkNoFollowAny = 0x0800, loop = 62;   // ELOOP
        bool x64 = RuntimeInformation.ProcessArchitecture == Architecture.X64;
        byte[] opened = new byte[512], named = new byte[512];
        if ((x64 ? DarwinFStatInode64(source, opened) : DarwinFStat(source, opened)) != 0)
            throw new IOException("Cannot verify the opened source object.", new Win32Exception(Marshal.GetLastPInvokeError()));
        if ((x64 ? DarwinFStatAtPathInode64(PosixDownloadTarget.AtFdCwd, expected, named, symlinkNoFollowAny)
                 : DarwinFStatAtPath(PosixDownloadTarget.AtFdCwd, expected, named, symlinkNoFollowAny)) != 0)
        {
            int error = Marshal.GetLastPInvokeError();
            if (error == loop) throw new NotSupportedException("Linked source paths are not supported for stable uploads.", new Win32Exception(error));
            throw new IOException("Cannot verify the opened source object.", new Win32Exception(error));
        }
        if ((BitConverter.ToUInt16(named, 4) & PosixTypeMask) == PosixLinkFile)
            throw new NotSupportedException("Linked source paths are not supported for stable uploads.");
        if (BitConverter.ToInt32(named, 0) != BitConverter.ToInt32(opened, 0) || BitConverter.ToUInt64(named, 8) != BitConverter.ToUInt64(opened, 8))
            throw new IOException("The opened source does not match the approved path.");
    }

    /// <summary>openat 的三参形式:不带 O_CREAT,不传 mode(Apple arm64 的变参约定因此无关)。</summary>
    [LibraryImport("libc", EntryPoint = "openat", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int SourceOpenAt(int directory, string path, int flags);

    [LibraryImport("libc", EntryPoint = "fstat", SetLastError = true)]
    private static partial int DarwinFStat(SafeFileHandle file, [Out] byte[] buffer);

    [LibraryImport("libc", EntryPoint = "fstat$INODE64", SetLastError = true)]
    private static partial int DarwinFStatInode64(SafeFileHandle file, [Out] byte[] buffer);

    [LibraryImport("libc", EntryPoint = "fstatat", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int DarwinFStatAtPath(int directory, string path, [Out] byte[] buffer, int flags);

    [LibraryImport("libc", EntryPoint = "fstatat$INODE64", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int DarwinFStatAtPathInode64(int directory, string path, [Out] byte[] buffer, int flags);
}
