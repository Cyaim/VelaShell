using System.ComponentModel;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using NSubstitute;
using VelaShell.Core.Models;
using VelaShell.Infrastructure.Plugins;
using VelaShell.Infrastructure.Plugins.Capabilities;
using VelaShell.PluginSdk.RemoteFs;
using PosixTarget = VelaShell.Infrastructure.Plugins.Capabilities.RemoteFsCapability.PosixDownloadTarget;

namespace VelaShell.Infrastructure.Tests.Plugins;

/// <summary>
/// Linux / macOS 的固定目录 fd 下载目标:直接驱动目标本身,逐条验证不覆盖的竞争、覆盖保留权限位、链接、祖先改名、
/// 崩溃遗留的清扫与释放清理。worker 的收尾分类由 RemoteTransferTests.cs 里那组 Download… 用例在同一个真实目标上验证。
/// </summary>
public sealed partial class RemoteTransferTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    [OSCondition(ConditionMode.Exclude, OperatingSystems.Windows)]
    public async Task PosixNoReplaceCommitRejectsTargetThatAppearsAfterOpen(bool linkFallback)
    {
        string directory = NewDirectory("vela-posix-noreplace-");
        string path = Path.Combine(directory, "target");
        try
        {
            var target = PosixTarget.Open(path, overwrite: false);
            target.UseLinkCommit = linkFallback;
            FileStream sink = target.CreateTemporary();
            await sink.WriteAsync(new byte[] { 1, 2, 3 });
            // 审批与传输期间目标被别人建出来:提交必须原子地拒绝,不能把它盖掉。
            await File.WriteAllBytesAsync(path, [9]);
            IOException rejected = Assert.ThrowsExactly<IOException>(() => target.Commit(false));
            Assert.AreEqual("Atomic download commit was rejected.", rejected.Message);
            Assert.AreEqual(17, Assert.IsInstanceOfType<Win32Exception>(rejected.InnerException).NativeErrorCode);   // EEXIST,两边同值
            await target.DisposeAsync();
            Assert.AreSequenceEqual(new byte[] { 9 }, await File.ReadAllBytesAsync(path));
            Assert.AreSequenceEqual([path], Directory.GetFiles(directory));
        }
        finally { Directory.Delete(directory, true); }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    [OSCondition(ConditionMode.Exclude, OperatingSystems.Windows)]
    [UnsupportedOSPlatform("windows")]
    public async Task PosixNoReplaceCommitLandsWithTheUmaskModeAndLeavesNoTemporary(bool linkFallback)
    {
        string directory = NewDirectory("vela-posix-commit-");
        string path = Path.Combine(directory, "target");
        string reference = Path.Combine(directory, "reference");
        try
        {
            var target = PosixTarget.Open(path, overwrite: false);
            target.UseLinkCommit = linkFallback;
            FileStream sink = target.CreateTemporary();
            await sink.WriteAsync(new byte[] { 1, 2, 3 });
            target.Commit(false);
            await target.DisposeAsync();
            Assert.AreSequenceEqual(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(path));
            // 新建的下载与别的程序新建的文件一样,按 0666 交 umask 收窄。
            await File.WriteAllBytesAsync(reference, []);
            Assert.AreEqual(File.GetUnixFileMode(reference), File.GetUnixFileMode(path));
            Assert.AreSequenceEqual([path, reference], Directory.GetFiles(directory), SequenceOrder.InAnyOrder);
        }
        finally { Directory.Delete(directory, true); }
    }

    [TestMethod]
    [DataRow(0b110_100_000, 0b110_100_000)]       // 0640
    [DataRow(0b100_111_101_101, 0b111_101_101)]   // 04755:setuid 不随旧文件留给新内容
    [OSCondition(ConditionMode.Exclude, OperatingSystems.Windows)]
    [UnsupportedOSPlatform("windows")]
    public async Task PosixOverwriteReplacesAndKeepsTheOldMode(int oldMode, int expectedMode)
    {
        string directory = NewDirectory("vela-posix-overwrite-");
        string path = Path.Combine(directory, "target");
        await File.WriteAllBytesAsync(path, [9]);
        File.SetUnixFileMode(path, (UnixFileMode)oldMode);
        try
        {
            var target = PosixTarget.Open(path, overwrite: true);
            FileStream sink = target.CreateTemporary();
            string temporary = Directory.GetFiles(directory).Single(file => file != path);
            // 传输期间的临时文件不比被替换的文件更宽。
            Assert.AreEqual((UnixFileMode)0, File.GetUnixFileMode(temporary) & ~(UnixFileMode)oldMode);
            await sink.WriteAsync(new byte[] { 1, 2, 3 });
            target.Commit(true);
            await target.DisposeAsync();
            Assert.AreSequenceEqual(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(path));
            Assert.AreEqual((UnixFileMode)expectedMode, File.GetUnixFileMode(path));
            Assert.AreSequenceEqual([path], Directory.GetFiles(directory));
        }
        finally { Directory.Delete(directory, true); }
    }

    [TestMethod]
    [OSCondition(ConditionMode.Exclude, OperatingSystems.Windows)]
    public void PosixTargetRejectsLinksAnywhereInTheChainAndAtTheLeaf()
    {
        string root = NewDirectory("vela-posix-links-");
        Directory.CreateDirectory(Path.Combine(root, "real", "inner"));
        Directory.CreateSymbolicLink(Path.Combine(root, "alias"), "real");
        Directory.CreateSymbolicLink(Path.Combine(root, "real", "leaf"), "inner");
        File.CreateSymbolicLink(Path.Combine(root, "real", "inner", "file-link"), "absent");
        try
        {
            // Open 收的是已解析的路径:链上仍有链接,只能是解析之后才换进来的,一律拒绝(顶层、末级父目录都一样)。
            foreach (string path in new[] { Path.Combine(root, "alias", "inner", "target"), Path.Combine(root, "real", "leaf", "target") })
            {
                IOException ex = Assert.ThrowsExactly<IOException>(() => PosixTarget.Open(path, overwrite: true), path);
                Assert.AreEqual("Destination directory identity cannot be verified.", ex.Message);
            }
            // 目标本身是链接或目录:覆盖也不行。
            foreach (string path in new[] { Path.Combine(root, "real", "inner", "file-link"), Path.Combine(root, "real", "inner") })
            {
                IOException ex = Assert.ThrowsExactly<IOException>(() => PosixTarget.Open(path, overwrite: true), path);
                Assert.AreEqual("The destination must not be a directory or link.", ex.Message);
            }
            Assert.IsEmpty(Directory.GetFiles(root, ".vela-download-*", SearchOption.AllDirectories));
        }
        finally
        {
            File.Delete(Path.Combine(root, "real", "inner", "file-link"));
            Directory.Delete(Path.Combine(root, "real", "leaf"));
            Directory.Delete(Path.Combine(root, "alias"));
            Directory.Delete(root, true);
        }
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = "procfs 只在 Linux 上;macOS 的 MNT_LOCAL 判定没有现成的非本地卷可测。")]
    public async Task PosixTargetRefusesFileSystemsOutsideTheLocalWhitelist()
    {
        // procfs 不在白名单里:与 9p、SMB、FUSE、NFS 走同一条拒绝,在审批之前、不建任何内容就按不支持收尾。
        PlatformNotSupportedException ex = Assert.ThrowsExactly<PlatformNotSupportedException>(() => PosixTarget.Open("/proc/sys/vela-download-target", overwrite: false));
        Assert.AreEqual("Safe download currently requires a local file system.", ex.Message);
        IPluginPermissionPrompt prompt = AllowDownloads();
        using RemoteFsCapability api = DownloadApi(prompt);
        RemoteTransferSnapshot start = await api.StartDownloadAsync(_session.ToString(), "/source", "/proc/sys/vela-download-target", new() { OperationId = "procfs" });
        RemoteTransferSnapshot done = await Terminal(api, start.TransferId);
        await WaitIdle(api);
        Assert.AreEqual(RemoteTransferState.Failed, done.State, done.ErrorMessage);
        Assert.AreEqual("not-supported", done.ErrorCode);
        Assert.IsTrue(done.DestinationCommitConfirmedAbsent);
        await prompt.DidNotReceive().RequestFileDownloadAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<long>(), Arg.Any<CancellationToken>());
    }

    [TestMethod]
    [OSCondition(ConditionMode.Exclude, OperatingSystems.Windows)]
    public async Task PosixDownloadApprovesAndLandsInTheResolvedParent()
    {
        string root = NewDirectory("vela-posix-resolve-");
        Directory.CreateDirectory(Path.Combine(root, "real"));
        Directory.CreateSymbolicLink(Path.Combine(root, "alias"), "real");
        string requested = Path.Combine(root, "alias", "target");
        string resolved = Path.Combine(root, "real", "target");
        IPluginPermissionPrompt prompt = Substitute.For<IPluginPermissionPrompt>();
        prompt.RequestFileDownloadAsync(Arg.Any<string>(), Arg.Any<string>(), "/source", resolved, false, Arg.Any<long>(), Arg.Any<CancellationToken>()).Returns(PluginPermissionDecision.AllowOnce);
        _sftp.GetFileInfoAsync(_session, "/source", Arg.Any<CancellationToken>()).Returns(SourceInfo(3));
        _sftp.DownloadStreamAsync(_session, "/source", Arg.Any<Stream>(), Arg.Any<IProgress<TransferProgress>>(), Arg.Any<CancellationToken>())
            .Returns(async call => await call.ArgAt<Stream>(2).WriteAsync(new byte[] { 1, 2, 3 }, call.ArgAt<CancellationToken>(4)));
        ConfigureRemoteDigest("/source", [1, 2, 3]);
        using RemoteFsCapability api = DownloadApi(prompt);
        try
        {
            RemoteTransferSnapshot start = await api.StartDownloadAsync(_session.ToString(), "/source", requested, new() { OperationId = "resolved" });
            RemoteTransferSnapshot done = await Terminal(api, start.TransferId);
            await WaitIdle(api);
            // 审批框与快照给的都是实际落盘的位置,不是插件递来的别名。
            Assert.AreEqual(RemoteTransferState.Succeeded, done.State, done.ErrorMessage);
            Assert.AreEqual(resolved, done.LocalPath);
            await prompt.Received(1).RequestFileDownloadAsync(Arg.Any<string>(), Arg.Any<string>(), "/source", resolved, false, Arg.Any<long>(), Arg.Any<CancellationToken>());
            Assert.AreSequenceEqual(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(resolved));
        }
        finally { await WaitIdle(api); Directory.Delete(Path.Combine(root, "alias")); Directory.Delete(root, true); }
    }

    [TestMethod]
    [OSCondition(ConditionMode.Exclude, OperatingSystems.Windows)]
    public async Task PosixCommitStaysInThePinnedDirectoryWhenAnAncestorIsRenamed()
    {
        string root = NewDirectory("vela-posix-pinned-");
        Directory.CreateDirectory(Path.Combine(root, "a", "b"));
        try
        {
            var target = PosixTarget.Open(Path.Combine(root, "a", "b", "target"), overwrite: false);
            // 审批之后祖先被改名,原路径上又换进一个同名的新目录:提交仍相对钉住的 fd,落进原来那个目录。
            Directory.Move(Path.Combine(root, "a"), Path.Combine(root, "moved"));
            Directory.CreateDirectory(Path.Combine(root, "a", "b"));
            FileStream sink = target.CreateTemporary();
            await sink.WriteAsync(new byte[] { 1, 2, 3 });
            target.Commit(false);
            await target.DisposeAsync();
            Assert.AreSequenceEqual(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(Path.Combine(root, "moved", "b", "target")));
            Assert.IsEmpty(Directory.GetFileSystemEntries(Path.Combine(root, "a", "b")));
            Assert.AreSequenceEqual([Path.Combine(root, "moved", "b", "target")], Directory.GetFiles(Path.Combine(root, "moved", "b")));
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    [OSCondition(ConditionMode.Exclude, OperatingSystems.Windows)]
    public async Task PosixOpenRemovesOrphanedTemporariesButNotLiveOnes()
    {
        string directory = NewDirectory("vela-posix-orphans-");
        string elsewhere = NewDirectory("vela-posix-orphans-outside-");
        string stale = Path.Combine(directory, TemporaryName());
        string unrelated = Path.Combine(directory, ".vela-download-notours");
        string outside = Path.Combine(elsewhere, "keep");
        string linked = Path.Combine(directory, TemporaryName());
        await File.WriteAllBytesAsync(stale, [7]);
        await File.WriteAllBytesAsync(unrelated, [7]);
        await File.WriteAllBytesAsync(outside, [5]);
        File.CreateSymbolicLink(linked, outside);
        try
        {
            // 另一条下载正在写:它的临时文件锁在它手里。
            var live = PosixTarget.Open(Path.Combine(directory, "live"), overwrite: false);
            FileStream sink = live.CreateTemporary();
            await sink.WriteAsync(new byte[] { 1 });
            string busy = Directory.GetFiles(directory, ".vela-download-*").Single(file => file != stale && file != unrelated);
            // 第一次 Open 已经清过:遗留的普通文件删掉;同形的链接与 Windows 一样删的是链接本身,不跟随、不碰它指向的文件。
            Assert.IsFalse(File.Exists(stale));
            Assert.IsFalse(File.Exists(linked));
            Assert.AreSequenceEqual(new byte[] { 5 }, await File.ReadAllBytesAsync(outside));
            await File.WriteAllBytesAsync(stale, [7]);
            await PosixTarget.Open(Path.Combine(directory, "other"), overwrite: false).DisposeAsync();
            Assert.IsFalse(File.Exists(stale));
            Assert.IsTrue(File.Exists(busy), "被占着的临时文件不能删");
            Assert.IsTrue(File.Exists(unrelated));
            live.Commit(false);
            await live.DisposeAsync();
            Assert.AreSequenceEqual(new byte[] { 1 }, await File.ReadAllBytesAsync(Path.Combine(directory, "live")));
            // 与临时文件同形的名字不能当目标:否则下一次清扫会把它当遗留删掉。
            Assert.AreEqual("Invalid destination name.", Assert.ThrowsExactly<IOException>(() => PosixTarget.Open(stale, overwrite: true)).Message);
        }
        finally { Directory.Delete(directory, true); Directory.Delete(elsewhere, true); }
    }

    [TestMethod]
    [OSCondition(ConditionMode.Exclude, OperatingSystems.Windows)]
    public async Task PosixDisposeWithoutCommitRemovesTheTemporary()
    {
        string directory = NewDirectory("vela-posix-dispose-");
        string path = Path.Combine(directory, "target");
        try
        {
            var target = PosixTarget.Open(path, overwrite: false);
            Assert.IsEmpty(Directory.GetFiles(directory), "Open pins and checks without creating content.");
            FileStream sink = target.CreateTemporary();
            await sink.WriteAsync(new byte[] { 1, 2, 3 });
            Assert.HasCount(1, Directory.GetFiles(directory));
            await target.DisposeAsync();
            await target.DisposeAsync();
            Assert.IsEmpty(Directory.GetFiles(directory));
            Assert.ThrowsExactly<InvalidOperationException>(() => target.Commit(false));
        }
        finally { Directory.Delete(directory, true); }
    }

    [TestMethod]
    [OSCondition(ConditionMode.Exclude, OperatingSystems.Windows)]
    [UnsupportedOSPlatform("windows")]
    public async Task PosixCleanupFailureIsReportedAsCleanupUnknown()
    {
        if (Environment.IsPrivilegedProcess)
        {
            TestContext.WriteLine("[SKIP] root 无视目录权限,删不掉临时文件的情形造不出来");
            return;
        }
        string directory = NewDirectory("vela-posix-cleanup-");
        string folder = Path.Combine(directory, "locked");
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, "target");
        _sftp.GetFileInfoAsync(_session, "/source", Arg.Any<CancellationToken>()).Returns(SourceInfo(3));
        // 短读让下载失败;收尾删临时文件之前目录已改成只读,unlinkat 被拒。
        _sftp.DownloadStreamAsync(_session, "/source", Arg.Any<Stream>(), Arg.Any<IProgress<TransferProgress>>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                await call.ArgAt<Stream>(2).WriteAsync(new byte[] { 1 });
                File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserExecute);
            });
        using RemoteFsCapability api = DownloadApi();
        try
        {
            RemoteTransferSnapshot start = await api.StartDownloadAsync(_session.ToString(), "/source", path, new() { OperationId = "cleanup" });
            RemoteTransferSnapshot done = await Terminal(api, start.TransferId);
            await WaitIdle(api);
            Assert.AreEqual(RemoteTransferState.Unknown, done.State, done.ErrorMessage);
            Assert.AreEqual("cleanup-unknown", done.ErrorCode);
            Assert.IsFalse(done.RemoteStopConfirmed);
            Assert.IsTrue(done.DestinationCommitConfirmedAbsent);
            Assert.IsFalse(File.Exists(path));
        }
        finally
        {
            await WaitIdle(api);
            File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            Directory.Delete(directory, true);
        }
    }

    private static string TemporaryName() => ".vela-download-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
}
