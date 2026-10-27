using System.Security.Cryptography;
using NSubstitute;
using VelaShell.Core.Models;
using VelaShell.Core.Sftp;
using VelaShell.Infrastructure.Plugins.Capabilities;
using VelaShell.PluginSdk.RemoteFs;

namespace VelaShell.Infrastructure.Tests.Plugins;

/// <summary>
/// Linux / macOS 上传前的源快照:建在源文件所在的卷上(源目录里的隐藏文件),能 reflink / clonefile 就克隆,
/// 否则先查剩余空间、不够就在拷贝之前明确拒绝;源目录不可写时退到系统临时目录;建快照前清扫崩溃遗留。
/// Windows 生产上不做快照(持有拒写共享的源句柄),这里用测试开关在三个平台上都走这条路。
/// </summary>
public sealed partial class RemoteTransferTests
{
    [TestMethod]
    [DataRow(-1L, false)]
    [DataRow(0L, true)]
    public async Task SnapshotNeedsTheFilePlusHeadroomAndRefusesBeforeCopying(long margin, bool succeeds)
    {
        const long length = 300 * 1024 + 5;
        string directory = NewDirectory("vela-snapshot-space-");
        string file = Path.Combine(directory, "source");
        await File.WriteAllBytesAsync(file, RandomNumberGenerator.GetBytes((int)length));
        var probed = new List<string>();
        long needed = length + RemoteFsCapability.SnapshotHeadroom;
        ConfigureHashingUpload();
        using var api = new RemoteFsCapability(_sftp, _connections, pluginId: "test.transfer", uploadPermissions: AllowUploads())
        {
            UseDiskSnapshot = true,
            UseSnapshotClone = false,
            SnapshotFreeSpace = path => { lock (probed) probed.Add(path); return needed + margin; },
        };
        try
        {
            RemoteTransferSnapshot start = await api.StartUploadAsync(_session.ToString(), file, "/target", new() { OperationId = "space-" + margin });
            RemoteTransferSnapshot done = await Terminal(api, start.TransferId);
            await WaitIdle(api);
            Assert.AreSequenceEqual([directory], probed, "查的是源文件所在的卷");
            Assert.AreSequenceEqual([file], Directory.GetFileSystemEntries(directory), "快照不留在源目录里");
            if (succeeds)
            {
                Assert.IsTrue(done.IsSuccess, $"{done.State}: {done.ErrorMessage}");
                return;
            }
            Assert.AreEqual(RemoteTransferState.Failed, done.State, done.ErrorMessage);
            Assert.AreEqual("insufficient-space", done.ErrorCode);
            Assert.Contains($"{needed} bytes needed", done.ErrorMessage!);
            Assert.Contains($"{needed - 1} bytes free", done.ErrorMessage!);
            Assert.Contains("source file's volume", done.ErrorMessage!);
            Assert.IsTrue(done.RemoteStopConfirmed);
            Assert.IsTrue(done.DestinationCommitConfirmedAbsent);
            // 拷贝之前就拒:一个字节都没往远端写,也就没有要清的远端临时文件。
            await _sftp.DidNotReceive().UploadStreamAsync(Arg.Any<Guid>(), Arg.Any<Stream>(), Arg.Any<string>(), Arg.Any<long>(), Arg.Any<DateTime?>(), Arg.Any<IProgress<TransferProgress>>(), Arg.Any<long>(), Arg.Any<uint?>(), Arg.Any<CancellationToken>());
            await _sftp.DidNotReceive().DeleteAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<IProgress<SftpDeleteProgress>>(), Arg.Any<CancellationToken>());
        }
        finally { await WaitIdle(api); Directory.Delete(directory, true); }
    }

    /// <summary>
    /// 卷支持写时复制克隆(macOS 的 APFS;Linux 上 Btrfs、XFS 这类)时快照是克隆,不占额外空间,剩余空间再少也照常上传。
    /// 本机临时目录不支持克隆(Windows、ext4、tmpfs)时不确定;macOS 上必须走通。
    /// </summary>
    [TestMethod]
    public async Task SnapshotCloneNeedsNoFreeSpaceWhereTheVolumeSupportsIt()
    {
        string directory = NewDirectory("vela-snapshot-clone-");
        string file = Path.Combine(directory, "source");
        await File.WriteAllBytesAsync(file, RandomNumberGenerator.GetBytes(256 * 1024 + 9));
        ConfigureHashingUpload();
        using var api = new RemoteFsCapability(_sftp, _connections, pluginId: "test.transfer", uploadPermissions: AllowUploads())
        {
            UseDiskSnapshot = true,
            SnapshotFreeSpace = _ => 0,
        };
        try
        {
            RemoteTransferSnapshot start = await api.StartUploadAsync(_session.ToString(), file, "/target", new() { OperationId = "clone" });
            RemoteTransferSnapshot done = await Terminal(api, start.TransferId);
            await WaitIdle(api);
            if (!OperatingSystem.IsMacOS() && done.ErrorCode == "insufficient-space") Assert.Inconclusive("临时目录所在的文件系统不支持克隆。");
            Assert.IsTrue(done.IsSuccess, $"{done.State}: {done.ErrorMessage}");
            Assert.AreSequenceEqual([file], Directory.GetFileSystemEntries(directory));
        }
        finally { await WaitIdle(api); Directory.Delete(directory, true); }
    }

    /// <summary>
    /// 源目录不可写(只读目录、别人的目录)时快照退到系统临时目录,照样先查那边的空间;拒绝时说明查的是临时目录的卷。
    /// 以 root 跑(权限不拦)时不确定。
    /// </summary>
    [TestMethod]
    [OSCondition(ConditionMode.Exclude, OperatingSystems.Windows)]
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    public async Task SnapshotFallsBackToTheTemporaryDirectoryWhenTheSourceDirectoryIsNotWritable()
    {
        string directory = NewDirectory("vela-snapshot-readonly-");
        string file = Path.Combine(directory, "source");
        await File.WriteAllBytesAsync(file, [1, 2, 3, 4]);
        string temporary = Path.TrimEndingDirectorySeparator(Path.GetTempPath());
        var probed = new List<string>();
        string? snapshot = null;
        _sftp.UploadStreamAsync(_session, Arg.Any<Stream>(), Arg.Any<string>(), Arg.Any<long>(), Arg.Any<DateTime?>(),
            Arg.Any<IProgress<TransferProgress>>(), 0, Arg.Any<uint?>(), Arg.Any<CancellationToken>()).Returns(call =>
            {
                snapshot = ((FileStream)call.ArgAt<Stream>(1)).Name;
                return Task.FromException(new IOException("stop after the snapshot"));
            });
        File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            try
            {
                File.WriteAllBytes(Path.Combine(directory, "probe"), []);
                Assert.Inconclusive("以 root 运行,只读目录照样写得进去。");
            }
            catch (UnauthorizedAccessException) { }
            foreach (long free in new[] { long.MaxValue, 0L })
            {
                using var api = new RemoteFsCapability(_sftp, _connections, pluginId: "test.transfer", uploadPermissions: AllowUploads())
                {
                    UseDiskSnapshot = true,
                    UseSnapshotClone = false,
                    SnapshotFreeSpace = path => { lock (probed) probed.Add(path); return free; },
                };
                RemoteTransferSnapshot start = await api.StartUploadAsync(_session.ToString(), file, "/target", new() { OperationId = "readonly-" + free });
                RemoteTransferSnapshot done = await Terminal(api, start.TransferId);
                await WaitIdle(api);
                if (free == 0)
                {
                    Assert.AreEqual("insufficient-space", done.ErrorCode, done.ErrorMessage);
                    Assert.Contains("temporary directory's volume", done.ErrorMessage!);
                }
                else
                {
                    Assert.AreEqual("upload-failed", done.ErrorCode, done.ErrorMessage);
                    Assert.AreEqual(temporary, Path.GetDirectoryName(snapshot));
                }
            }
            Assert.AreSequenceEqual([Path.GetTempPath(), Path.GetTempPath()], probed);
        }
        finally
        {
            File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            Directory.Delete(directory, true);
        }
    }

    /// <summary>
    /// 建快照之前清扫源目录里崩溃遗留的快照,口径同下载临时文件:名字逐字对得上、没有上传在用(拿得到排他锁)的才删;
    /// 活着的(别的上传以不共享方式开着)、名字只是相像的都不碰;Unix 上同形的符号链接只删链接本身,不碰它指向的文件。
    /// </summary>
    [TestMethod]
    public async Task SnapshotSweepRemovesCrashLeftoversButNotLiveOnesOrLookalikes()
    {
        string directory = NewDirectory("vela-snapshot-sweep-");
        string file = Path.Combine(directory, "source");
        await File.WriteAllBytesAsync(file, [5, 6, 7]);
        string Name() => Path.Combine(directory, ".vela-snapshot-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(24)));
        string orphan = Name(), live = Name(), link = Name();
        string lookalike = Path.Combine(directory, ".vela-snapshot-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant());
        string victim = Path.Combine(directory, "victim");
        await File.WriteAllBytesAsync(orphan, [1]);
        await File.WriteAllBytesAsync(lookalike, [2]);
        await File.WriteAllBytesAsync(victim, [3]);
        bool linked = !OperatingSystem.IsWindows();
        if (linked) File.CreateSymbolicLink(link, victim);
        ConfigureHashingUpload();
        using var api = new RemoteFsCapability(_sftp, _connections, pluginId: "test.transfer", uploadPermissions: AllowUploads())
        {
            UseDiskSnapshot = true,
            UseSnapshotClone = false,
        };
        try
        {
            using (new FileStream(live, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            {
                RemoteTransferSnapshot start = await api.StartUploadAsync(_session.ToString(), file, "/target", new() { OperationId = "sweep" });
                RemoteTransferSnapshot done = await Terminal(api, start.TransferId);
                await WaitIdle(api);
                Assert.IsTrue(done.IsSuccess, $"{done.State}: {done.ErrorMessage}");
                Assert.IsFalse(File.Exists(orphan), "崩溃遗留的快照没清掉");
                Assert.IsTrue(File.Exists(live), "别的上传正在用的快照被删了");
            }
            Assert.IsTrue(File.Exists(lookalike), "名字只是相像的文件被删了");
            if (linked) Assert.IsFalse(File.Exists(link) || new FileInfo(link).LinkTarget is not null, "同形的符号链接没删");
            Assert.AreSequenceEqual(new byte[] { 3 }, await File.ReadAllBytesAsync(victim), "链接指向的文件被动了");
        }
        finally { await WaitIdle(api); Directory.Delete(directory, true); }
    }

    /// <summary>替身后端:把收到的流整份算摘要并据此回答远端摘要,上传就能一路走到提交成功。</summary>
    private void ConfigureHashingUpload()
    {
        string? hash = null;
        _sftp.UploadStreamAsync(_session, Arg.Any<Stream>(), Arg.Any<string>(), Arg.Any<long>(), Arg.Any<DateTime?>(),
            Arg.Any<IProgress<TransferProgress>>(), 0, Arg.Any<uint?>(), Arg.Any<CancellationToken>()).Returns(async call =>
            { hash = Convert.ToHexString(await SHA256.HashDataAsync(call.ArgAt<Stream>(1))); });
        _sftp.ComputeSha256Async(_session, Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult<IReadOnlyDictionary<string, string?>>(new Dictionary<string, string?> { [call.ArgAt<IReadOnlyList<string>>(1)[0]] = hash }));
    }
}
