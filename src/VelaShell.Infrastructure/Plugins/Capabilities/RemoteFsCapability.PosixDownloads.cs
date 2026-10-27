using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace VelaShell.Infrastructure.Plugins.Capabilities;

internal sealed partial class RemoteFsCapability
{
    /// <summary>
    /// 先把父目录解析成真实路径(Silverblue 的 /home → /var/home、macOS 的 /var → /private/var 照常可用)并写进快照的
    /// LocalPath:审批框与之后的查询看到的就是实际落盘的位置,再按这个路径钉住目录。
    /// </summary>
    private static PosixDownloadTarget OpenPosixDownloadTarget(UploadOperation op, bool overwrite)
    {
        string resolved = PosixDownloadTarget.Resolve(Read(op).LocalPath);
        Update(op, s => s with { LocalPath = resolved });
        return PosixDownloadTarget.Open(resolved, overwrite);
    }

    /// <summary>
    /// Linux / macOS(x64、arm64)的下载落盘目标:从 "/" 起逐级 openat(O_DIRECTORY | O_NOFOLLOW) 走到父目录,只留父目录 fd,
    /// 链上任何一级是符号链接都拒绝(与 Windows 拒绝重解析点同理)。建临时文件、查目标、提交与清理全部相对这个 fd:
    /// 祖先目录事后被改名或换掉,文件照样落进审批时钉住的那个目录,不会按路径重新解析到别处。
    /// 不覆盖的提交用 renameat2(RENAME_NOREPLACE) / renameatx_np(RENAME_EXCL);文件系统不认这个标志时退到 linkat + unlinkat,
    /// 目标已存在时 linkat 原子地失败。两条都走不通的文件系统就按不支持拒绝提交,不退回「先查再改名」。
    /// 与 Windows 只收固定本地 NTFS 同理,父目录必须在本地、按 inode 寻址的文件系统上:按路径解析的文件系统(9p、SMB、FUSE)
    /// 上目录 fd 钉不住改名,网络文件系统一律不收。
    /// </summary>
    internal sealed partial class PosixDownloadTarget : IDownloadTarget
    {
        private const string TemporaryPrefix = ".vela-download-";

        // 常量按系统与架构取。Linux:asm-generic/fcntl.h、asm-generic/errno*.h、linux/fcntl.h、linux/fs.h;arm64 的 asm/fcntl.h
        // 只改了 O_DIRECTORY / O_NOFOLLOW / O_DIRECT / O_LARGEFILE —— x64 的 O_NOFOLLOW 在 arm64 上恰是 O_LARGEFILE、O_DIRECTORY
        // 恰是 O_DIRECT,取错了不报错,只是悄悄不拦链接。macOS:sys/fcntl.h、sys/errno.h、sys/stdio.h,两种架构相同。
        private static readonly bool Linux = OperatingSystem.IsLinux();
        private static readonly bool LinuxArm64 = Linux && RuntimeInformation.ProcessArchitecture == Architecture.Arm64;
        private static readonly bool DarwinArm64 = OperatingSystem.IsMacOS() && RuntimeInformation.ProcessArchitecture == Architecture.Arm64;
        internal static readonly int ODirectory = LinuxArm64 ? 0x4000 : Linux ? 0x10000 : 0x100000;
        internal static readonly int ONoFollow = LinuxArm64 ? 0x8000 : Linux ? 0x20000 : 0x100;
        private static readonly int OCreate = Linux ? 0x40 : 0x200;
        private static readonly int OExclusive = Linux ? 0x80 : 0x800;
        private static readonly int ONonBlock = Linux ? 0x800 : 0x4;
        internal static readonly int OCloseOnExec = Linux ? 0x80000 : 0x1000000;
        // Linux 用 O_PATH 打开目录链:只走不读,只给了 x 权限的祖先目录(0711 的 /home 之类)也走得过去;macOS 用 O_RDONLY。
        internal static readonly int OPath = Linux ? 0x200000 : 0;
        private const int OReadOnly = 0, OReadWrite = 2;
        internal static readonly int AtFdCwd = Linux ? -100 : -2;
        internal static readonly int AtSymlinkNoFollow = Linux ? 0x100 : 0x20;
        private const uint LinuxRenameNoReplace = 0x1, DarwinRenameExclusive = 0x4;
        private const int LockExclusive = 2, LockNonBlocking = 4;
        private const uint FileTypeMask = 0xF000, RegularFile = 0x8000, DirectoryFile = 0x4000, LinkFile = 0xA000, PermissionBits = 0x1FF;
        private const int EPerm = 1, ENoEnt = 2, EExist = 17, EInval = 22;
        private static readonly int EAgain = Linux ? 11 : 35;
        private static readonly int ENoSys = Linux ? 38 : 78;
        private static readonly int ENotSup = Linux ? 95 : 45;
        private static readonly int EOpNotSupp = Linux ? 95 : 102;

        // Linux 的 statfs.f_type(linux/magic.h;ZFS 的取自 OpenZFS):ext2/3/4、XFS、Btrfs、tmpfs、F2FS、bcachefs、ZFS、overlayfs、
        // eCryptfs、NILFS、ReiserFS、vfat、exFAT。没列的(9p、CIFS / SMB、FUSE、NFS……)一律不收:WSL 的 /mnt/c(9p)上实测,
        // 祖先改名之后提交没有落进钉住的目录。macOS 看 statfs.f_flags 的 MNT_LOCAL(sys/mount.h 0x1000)。
        private static readonly uint[] LocalFileSystems =
            [0xEF53, 0x58465342, 0x9123683E, 0x01021994, 0xF2F52010, 0xCA451A4E, 0x2FC12FC1, 0x794C7630, 0xF15F, 0x3434, 0x52654973, 0x4D44, 0x2011BAB0];
        private const uint DarwinMountLocal = 0x1000;

        private readonly SafeFileHandle _directory;
        private readonly string _name;
        private readonly bool _overwrite;
        // 还归这个目标清理的临时名:提交改名后、或删掉后置空;经 linkat 提交而临时名没删掉时仍留着,释放时再删。
        private string? _temporary;
        private SafeFileHandle? _file;
        private FileStream? _stream;

        private PosixDownloadTarget(SafeFileHandle directory, string name, bool overwrite)
        {
            _directory = directory;
            _name = name;
            _overwrite = overwrite;
        }

        /// <summary>只收发布的四个 RID 对应的组合:别的系统或架构没核对过常量,一律不给后台下载。</summary>
        internal static bool IsSupported => (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            && RuntimeInformation.ProcessArchitecture is Architecture.X64 or Architecture.Arm64;

        /// <summary>测试开关:不试 renameat2 / renameatx_np,直接走 linkat 兜底(与 <see cref="UseDiskSnapshot" /> 同一用意)。</summary>
        internal bool UseLinkCommit { get; set; }

        /// <summary>与上传源同一套解析:中间的符号链接逐级展开(链接里的 .. 在前面的链接展开后才算),末级名字原样保留。</summary>
        internal static string Resolve(string destination)
        {
            string full = Path.GetFullPath(destination);
            if (Path.GetFileName(full).Length == 0) throw new IOException("Invalid destination name.");
            try { return ResolveUploadSource(full); }
            catch (IOException ex) { throw new IOException("Cannot verify the destination directory.", ex); }
        }

        /// <summary>钉住父目录、查目标、清掉崩溃遗留的临时文件;不建任何内容。<paramref name="destination" /> 须已解析。</summary>
        internal static PosixDownloadTarget Open(string destination, bool overwrite)
        {
            if (!IsSupported) throw new PlatformNotSupportedException("Safe download requires Linux or macOS on x64 or arm64.");
            if (!destination.StartsWith('/')) throw new IOException("Destination must be absolute.");
            string name = Path.GetFileName(destination);
            // 与临时文件同形的名字不给用:下一次清扫会把它当成崩溃遗留删掉。
            if (name is "" or "." or ".." || IsTemporaryName(name)) throw new IOException("Invalid destination name.");
            string parent = Path.GetDirectoryName(destination) ?? throw new IOException("Destination must name a file.");
            var target = new PosixDownloadTarget(OpenDirectoryChain(parent), name, overwrite);
            try
            {
                RequireLocalFileSystem(target._directory);
                if (target.ReadMode(name) is { } mode)
                {
                    if ((mode & FileTypeMask) is DirectoryFile or LinkFile) throw new IOException("The destination must not be a directory or link.");
                    if ((mode & FileTypeMask) != RegularFile) throw new IOException("The destination must be a regular file.");
                    if (!overwrite) throw new IOException("The destination already exists.");
                }
                target.SweepOrphans(parent);
                return target;
            }
            catch { target._directory.Dispose(); throw; }
        }

        private static SafeFileHandle OpenDirectoryChain(string parent)
        {
            int flags = ODirectory | ONoFollow | OCloseOnExec | OPath;
            int root = OpenAt(AtFdCwd, "/", flags, 0);
            if (root < 0) throw new IOException("Cannot lock the destination directory chain.", LastError());
            var current = new SafeFileHandle(root, ownsHandle: true);
            try
            {
                foreach (string part in parent.Split('/', StringSplitOptions.RemoveEmptyEntries))
                {
                    if (part is "." or "..") throw new IOException("Ambiguous directory name.");
                    int fd = OpenAt(Fd(current), part, flags, 0);
                    if (fd < 0)
                    {
                        Win32Exception error = LastError();
                        // 链上这一级是符号链接(解析之后才换进来的):与 Windows 碰到重解析点同一说法。查不了类型就照原样报打开失败。
                        uint? mode = null;
                        try { mode = ReadMode(current, part); }
                        catch (Exception ex) when (ex is IOException or NotSupportedException) { }
                        if (mode is { } type && (type & FileTypeMask) == LinkFile)
                            throw new IOException("Destination directory identity cannot be verified.", error);
                        throw new IOException("Cannot lock the destination directory chain.", error);
                    }
                    current.Dispose();
                    current = new SafeFileHandle(fd, ownsHandle: true);
                }
                return current;
            }
            catch { current.Dispose(); throw; }
        }

        private static void RequireLocalFileSystem(SafeFileHandle directory)
        {
            byte[] buffer = new byte[4096];   // Linux 的 struct statfs 120 字节;macOS 64 位 inode 的 struct statfs 2168 字节
            bool local;
            try
            {
                if (Linux)
                {
                    if (LinuxFStatFs(Fd(directory), buffer) != 0) throw new IOException("Cannot inspect the destination file system.", LastError());
                    local = LocalFileSystems.Contains(BitConverter.ToUInt32(buffer, 0));   // f_type 在 0,x64 / arm64 上都是 long,魔数在低 32 位
                }
                else
                {
                    int result = RuntimeInformation.ProcessArchitecture == Architecture.X64
                        ? DarwinFStatFsInode64(Fd(directory), buffer)
                        : DarwinFStatFs(Fd(directory), buffer);
                    if (result != 0) throw new IOException("Cannot inspect the destination file system.", LastError());
                    local = (BitConverter.ToUInt32(buffer, 64) & DarwinMountLocal) != 0;   // f_flags 在 64
                }
            }
            catch (EntryPointNotFoundException ex) { throw new PlatformNotSupportedException("Safe download currently requires a local file system.", ex); }
            if (!local) throw new PlatformNotSupportedException("Safe download currently requires a local file system.");
        }

        /// <summary>
        /// 新建时按 0666 建,交内核按 umask(或目录的默认 ACL)收窄 —— 与别的程序新建文件得到的权限一样,也不必在多线程进程里
        /// 用 umask() 先改后还去读它;传输中的半截内容不会比最终文件更公开。覆盖已有文件时按旧文件的权限位建(同样受 umask 收窄),
        /// 传输期间不比旧文件更宽,提交前再精确改成旧文件的权限位。
        /// </summary>
        public FileStream CreateTemporary()
        {
            if (_stream is not null) throw new InvalidOperationException();
            string temporary = TemporaryPrefix + Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
            uint mode = 0x1B6;
            if (_overwrite && ReadMode(_name) is { } existing && (existing & FileTypeMask) == RegularFile) mode = existing & PermissionBits;
            int flags = OReadWrite | OCreate | OExclusive | ONoFollow | OCloseOnExec;
            // Apple arm64 的被调方一律从栈上取变参:用 x3–x7 五个占位参数填满寄存器,mode 才落在首个栈槽上。
            int fd = DarwinArm64
                ? DarwinArm64OpenAt(Fd(_directory), temporary, flags, 0, 0, 0, 0, 0, (nint)mode)
                : OpenAt(Fd(_directory), temporary, flags, (nint)mode);
            if (fd < 0) throw new IOException("Cannot exclusively create the download temporary file.", LastError());
            var file = new SafeFileHandle(fd, ownsHandle: true);
            _temporary = temporary;
            try
            {
                // 用着的临时文件一直持有 flock:别的下载清扫崩溃遗留时拿不到锁,就不会删它。拿锁失败只可能是清扫恰好在建与锁之间抢先,
                // 那就作废重来;文件系统不支持 flock 时别人同样锁不上、不会删,照常继续。
                if (FLock(fd, LockExclusive | LockNonBlocking) != 0 && Marshal.GetLastPInvokeError() == EAgain)
                    throw new IOException("Cannot exclusively create the download temporary file.");
                _stream = new FileStream(file, FileAccess.ReadWrite, 128 * 1024);
                _file = file;
                return _stream;
            }
            catch
            {
                if (UnlinkAt(Fd(_directory), temporary, 0) == 0 || Marshal.GetLastPInvokeError() == ENoEnt) _temporary = null;
                file.Dispose();
                throw;
            }
        }

        public void Commit(bool overwrite)
        {
            if (_stream is null || _file is null || _temporary is null) throw new InvalidOperationException();
            int directory = Fd(_directory);
            if (overwrite)
            {
                if (ReadMode(_name) is { } existing)
                {
                    if ((existing & FileTypeMask) is DirectoryFile or LinkFile) throw new IOException("The destination must not be a directory or link.");
                    // 换成被替换文件的权限位;setuid / setgid / sticky 不跟过来 —— 内容是新的,特权位不该随旧文件留下。
                    if ((existing & FileTypeMask) == RegularFile && FChmod(Fd(_file), existing & PermissionBits) != 0)
                        throw new IOException("Atomic download commit was rejected.", LastError());
                }
                // renameat 不跟随最终名上的链接,只换目录项;替换本身是原子的。
                if (RenameAt(directory, _temporary, directory, _name) != 0) throw new IOException("Atomic download commit was rejected.", LastError());
                _temporary = null;
                return;
            }
            int error = UseLinkCommit ? EInval : RenameNoReplace(directory, _temporary);
            if (error == 0)
            {
                _temporary = null;
                return;
            }
            if (!IsUnsupported(error)) throw new IOException("Atomic download commit was rejected.", new Win32Exception(error));
            if (LinkAt(directory, _temporary, directory, _name, 0) != 0)
            {
                // EEXIST 就是原子的拒绝;文件系统根本不给硬链接(EPERM / ENOTSUP 之类)时按不支持拒绝,与 Windows 不认非 NTFS 同一归类。
                Win32Exception failure = LastError();
                if (failure.NativeErrorCode != EExist && (failure.NativeErrorCode == EPerm || IsUnsupported(failure.NativeErrorCode)))
                    throw new PlatformNotSupportedException("Safe download commit is unsupported on this filesystem.", failure);
                throw new IOException("Atomic download commit was rejected.", failure);
            }
            // 链接成功即已提交;临时名在这里删,删不掉留给释放时再删一次,仍删不掉就按清理未确认上报。
            if (UnlinkAt(directory, _temporary, 0) == 0 || Marshal.GetLastPInvokeError() == ENoEnt) _temporary = null;
        }

        public async ValueTask DisposeAsync()
        {
            if (_directory.IsClosed)
            {
                if (_temporary is not null) throw new IOException("The task temporary cleanup could not be confirmed.");
                return;
            }
            Exception? cleanupFailure = null;
            try
            {
                // 删的时候流还开着、flock 还在,别的清扫不会同时动它;名字已不在(ENOENT)也算清掉了。
                if (_temporary is not null)
                {
                    if (UnlinkAt(Fd(_directory), _temporary, 0) == 0 || Marshal.GetLastPInvokeError() == ENoEnt) _temporary = null;
                    else cleanupFailure = new IOException("The task temporary cleanup could not be confirmed.", LastError());
                }
                if (_stream is not null) await _stream.DisposeAsync().ConfigureAwait(false);
                else _file?.Dispose();
            }
            finally { _directory.Dispose(); }
            if (cleanupFailure is not null) throw cleanupFailure;
        }

        /// <summary>
        /// 清掉崩溃留下的临时文件,口径与 Windows 的 SweepOrphans 一致:名字与 <see cref="CreateTemporary" /> 起的逐字对得上才碰;
        /// 普通文件只删拿得到 flock 的 —— 活着的临时文件锁在它的 worker 手里,拿不到就留着;同形的符号链接不跟随,删的是链接本身;
        /// 目录与别的特殊文件不碰。按路径列目录只为拿候选名字:父目录此刻若已被换掉,列到的名字在钉住的目录里查无此项、自然跳过,
        /// 查、锁、删一律相对固定目录 fd。尽力而为,任何失败都只是跳过。
        /// </summary>
        private void SweepOrphans(string parent)
        {
            string[] names;
            try { names = [.. Directory.EnumerateFileSystemEntries(parent, TemporaryPrefix + "*").Select(Path.GetFileName).OfType<string>().Where(IsTemporaryName)]; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return; }
            foreach (string name in names)
            {
                uint type;
                try
                {
                    if (ReadMode(name) is not { } mode) continue;
                    type = mode & FileTypeMask;
                }
                catch (Exception ex) when (ex is IOException or NotSupportedException) { continue; }
                if (type == LinkFile)
                {
                    UnlinkAt(Fd(_directory), name, 0);
                    continue;
                }
                if (type != RegularFile) continue;
                int fd = OpenAt(Fd(_directory), name, OReadOnly | ONoFollow | ONonBlock | OCloseOnExec, 0);
                if (fd < 0) continue;
                using var candidate = new SafeFileHandle(fd, ownsHandle: true);
                if (FLock(fd, LockExclusive | LockNonBlocking) == 0) UnlinkAt(Fd(_directory), name, 0);
            }
        }

        /// <summary>与 CreateTemporary 起的名字逐字对得上才算:前缀 + 48 位大写十六进制(与 Windows 目标同一判定)。</summary>
        private static bool IsTemporaryName(string name) => name.Length == TemporaryPrefix.Length + 48
            && name.StartsWith(TemporaryPrefix, StringComparison.Ordinal)
            && name[TemporaryPrefix.Length..].All(char.IsAsciiHexDigitUpper);

        private uint? ReadMode(string name) => ReadMode(_directory, name);

        /// <summary>
        /// 不跟随链接地取目录 fd 下某个名字的 st_mode(Linux 用 statx,macOS 用 64 位 inode 的 fstatat,st_mode 都在既有偏移);
        /// 不存在时为 null。查不了一律拒绝:目标是不是目录、链接,以及覆盖时的权限位都靠它,不能像上传预检那样放过。
        /// </summary>
        private static uint? ReadMode(SafeFileHandle directory, string name)
        {
            byte[] buffer = new byte[512];   // struct statx 256 字节;macOS 的 struct stat 144 字节
            try
            {
                if (Linux)
                {
                    const uint statxType = 0x1, statxMode = 0x2;
                    if (LinuxStatX(Fd(directory), name, AtSymlinkNoFollow, statxType | statxMode, buffer) == 0)
                    {
                        return (BitConverter.ToUInt32(buffer, 0) & (statxType | statxMode)) == (statxType | statxMode)
                            ? BitConverter.ToUInt16(buffer, 28)
                            : throw new IOException("Cannot inspect the destination.");
                    }
                }
                else if ((RuntimeInformation.ProcessArchitecture == Architecture.X64
                    ? DarwinFStatAtInode64(Fd(directory), name, buffer, AtSymlinkNoFollow)
                    : DarwinFStatAt(Fd(directory), name, buffer, AtSymlinkNoFollow)) == 0)
                {
                    return BitConverter.ToUInt16(buffer, 4);
                }
            }
            catch (EntryPointNotFoundException ex) { throw new PlatformNotSupportedException("Safe download requires statx / fstatat.", ex); }
            int error = Marshal.GetLastPInvokeError();
            if (error == ENoEnt) return null;
            if (error == ENoSys) throw new PlatformNotSupportedException("Safe download requires statx / fstatat.");
            throw new IOException("Cannot inspect the destination.", new Win32Exception(error));
        }

        /// <summary>不覆盖地改名;返回 0 或 errno。glibc 2.28 之前没有 renameat2,按「标志不支持」处理,交给 linkat 兜底。</summary>
        private int RenameNoReplace(int directory, string temporary)
        {
            try
            {
                int result = Linux
                    ? LinuxRenameAt2(directory, temporary, directory, _name, LinuxRenameNoReplace)
                    : DarwinRenameAtX(directory, temporary, directory, _name, DarwinRenameExclusive);
                return result == 0 ? 0 : Marshal.GetLastPInvokeError();
            }
            catch (EntryPointNotFoundException) { return ENoSys; }
        }

        /// <summary>文件系统或内核不认这个标志 / 操作的几种 errno(Linux 的 ENOTSUP 与 EOPNOTSUPP 同值)。</summary>
        private static bool IsUnsupported(int error) => error == EInval || error == ENoSys || error == ENotSup || error == EOpNotSupp;

        private static int Fd(SafeFileHandle handle)
        {
            ObjectDisposedException.ThrowIf(handle.IsClosed, handle);
            return (int)handle.DangerousGetHandle();
        }

        private static Win32Exception LastError() => new(Marshal.GetLastPInvokeError());

        [LibraryImport("libc", EntryPoint = "openat", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
        private static partial int OpenAt(int directory, string path, int flags, nint mode);

        [LibraryImport("libc", EntryPoint = "openat", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
        private static partial int DarwinArm64OpenAt(int directory, string path, int flags,
            nint unused3, nint unused4, nint unused5, nint unused6, nint unused7, nint mode);

        [LibraryImport("libc", EntryPoint = "flock", SetLastError = true)]
        private static partial int FLock(int file, int operation);

        [LibraryImport("libc", EntryPoint = "fchmod", SetLastError = true)]
        private static partial int FChmod(int file, uint mode);

        [LibraryImport("libc", EntryPoint = "renameat", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
        private static partial int RenameAt(int fromDirectory, string from, int toDirectory, string to);

        [LibraryImport("libc", EntryPoint = "renameat2", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
        private static partial int LinuxRenameAt2(int fromDirectory, string from, int toDirectory, string to, uint flags);

        [LibraryImport("libc", EntryPoint = "renameatx_np", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
        private static partial int DarwinRenameAtX(int fromDirectory, string from, int toDirectory, string to, uint flags);

        [LibraryImport("libc", EntryPoint = "linkat", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
        private static partial int LinkAt(int fromDirectory, string from, int toDirectory, string to, int flags);

        [LibraryImport("libc", EntryPoint = "unlinkat", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
        private static partial int UnlinkAt(int directory, string path, int flags);

        [LibraryImport("libc", EntryPoint = "fstatfs", SetLastError = true)]
        private static partial int LinuxFStatFs(int directory, [Out] byte[] buffer);

        [LibraryImport("libc", EntryPoint = "fstatfs", SetLastError = true)]
        private static partial int DarwinFStatFs(int directory, [Out] byte[] buffer);

        [LibraryImport("libc", EntryPoint = "fstatfs$INODE64", SetLastError = true)]
        private static partial int DarwinFStatFsInode64(int directory, [Out] byte[] buffer);

        [LibraryImport("libc", EntryPoint = "fstatat", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
        private static partial int DarwinFStatAt(int directory, string path, [Out] byte[] buffer, int flags);

        [LibraryImport("libc", EntryPoint = "fstatat$INODE64", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
        private static partial int DarwinFStatAtInode64(int directory, string path, [Out] byte[] buffer, int flags);
    }
}
