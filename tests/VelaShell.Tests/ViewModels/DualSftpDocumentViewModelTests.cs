using NSubstitute;
using NSubstitute.ExceptionExtensions;
using ReactiveUI.Primitives;
using VelaShell.Core.Models;
using VelaShell.Core.Sftp;
using VelaShell.ViewModels;

namespace VelaShell.Tests.ViewModels;

/// <summary>
/// 「远程 + 远程」双栏文件标签:两栏之间的流式中转、连接的归属与收尾、目录比较。
/// </summary>
[TestClass]
[TestCategory("FileBrowser")]
public class DualSftpDocumentViewModelTests
{
    private static readonly Guid Left = Guid.NewGuid();
    private static readonly Guid Right = Guid.NewGuid();

    private static RemoteFileInfo Entry(string path, long size = 10, bool directory = false, DateTime? modified = null) => new()
    {
        Name = path[(path.LastIndexOf('/') + 1)..],
        FullPath = path,
        Size = size,
        Permissions = directory ? "drwxr-xr-x" : "-rw-r--r--",
        IsDirectory = directory,
        LastModified = modified ?? new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc),
        Owner = "u",
        Group = "g",
    };

    /// <summary>两栏共用一个服务(与真实的路由服务一样按会话分派),彼此互为另一栏。</summary>
    private static (ISftpService Sftp, FileBrowserViewModel LeftPane, FileBrowserViewModel RightPane) PanePair(TransferOptions? options = null)
    {
        ISftpService sftp = Substitute.For<ISftpService>();
        sftp.ListDirectoryAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(new List<RemoteFileInfo>()));
        options ??= new() { ConflictPolicy = "overwrite", ResumeEnabled = false };
        var left = new FileBrowserViewModel(sftp, Left) { ServerDisplayName = "alpha", TransferOptions = options };
        var right = new FileBrowserViewModel(sftp, Right) { ServerDisplayName = "beta", TransferOptions = options };
        left.DualPeer = right;
        right.DualPeer = left;
        return (sftp, left, right);
    }

    private static void Readable(ISftpService sftp, Guid session, RemoteFileInfo file)
    {
        sftp.GetFileInfoAsync(session, file.FullPath, Arg.Any<CancellationToken>()).Returns(file);
        sftp.OpenReadAsync(session, file.FullPath, Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult<Stream>(new MemoryStream(new byte[file.Size])));
    }

    [TestMethod]
    public async Task ReceiveFromPeer_StreamsFilesAndRecreatesFolders_OnThisSession()
    {
        (ISftpService sftp, _, FileBrowserViewModel right) = PanePair();
        RemoteFileInfo file = Entry("/src/a.txt", 5);
        RemoteFileInfo folder = Entry("/src/dir", directory: true);
        RemoteFileInfo nested = Entry("/src/dir/b.txt", 7);
        sftp.ListDirectoryAsync(Left, "/src/dir", Arg.Any<CancellationToken>()).Returns([nested]);
        Readable(sftp, Left, file);
        Readable(sftp, Left, nested);

        await right.ReceiveFromPeerAsync([new(file), new(folder)]);

        await sftp.Received(1).EnsureDirectoryAsync(Right, "/dir", Arg.Any<CancellationToken>());
        await sftp.Received(1).UploadStreamAsync(Right, Arg.Any<Stream>(), "/a.txt", 5, Arg.Any<DateTime?>(),
            Arg.Any<IProgress<TransferProgress>?>(), Arg.Any<long>(), Arg.Any<uint?>(), Arg.Any<CancellationToken>());
        await sftp.Received(1).UploadStreamAsync(Right, Arg.Any<Stream>(), "/dir/b.txt", 7, Arg.Any<DateTime?>(),
            Arg.Any<IProgress<TransferProgress>?>(), Arg.Any<long>(), Arg.Any<uint?>(), Arg.Any<CancellationToken>());
        // 中转不经本地文件:任何一次上传/下载本地文件的调用都说明字节落了盘。
        await sftp.DidNotReceiveWithAnyArgs().DownloadFileAsync(default, null!, null!);
        await sftp.DidNotReceiveWithAnyArgs().UploadFileAsync(default, null!, null!);
        Assert.IsNull(right.ErrorMessage);
    }

    [TestMethod]
    public async Task ReceiveFromPeer_WithoutAPeer_DoesNothing()
    {
        ISftpService sftp = Substitute.For<ISftpService>();
        var lonely = new FileBrowserViewModel(sftp, Right);

        await lonely.ReceiveFromPeerAsync([new(Entry("/src/a.txt"))]);

        Assert.IsEmpty(sftp.ReceivedCalls());
    }

    [TestMethod]
    public async Task ReceiveFromPeer_SkipPolicy_LeavesAnExistingTargetAlone()
    {
        (ISftpService sftp, _, FileBrowserViewModel right) = PanePair(new() { ConflictPolicy = "skip", ResumeEnabled = false });
        sftp.ListDirectoryAsync(Right, "/", Arg.Any<CancellationToken>()).Returns([Entry("/a.txt")]);
        RemoteFileInfo file = Entry("/src/a.txt");
        Readable(sftp, Left, file);

        await right.ReceiveFromPeerAsync([new(file)]);

        await sftp.DidNotReceiveWithAnyArgs().UploadStreamAsync(default, null!, null!, 0);
    }

    [TestMethod]
    public async Task ARelayThatFailsBeforeWriting_NeverDeletesTheExistingTarget()
    {
        // 源在规划之后被删掉:目标还没被碰过。若照「半截文件」清理,删掉的会是用户原有的同名文件。
        (ISftpService sftp, _, FileBrowserViewModel right) =
            PanePair(new() { ConflictPolicy = "overwrite", ResumeEnabled = false, AutoCleanTempFiles = true });
        RemoteFileInfo file = Entry("/src/a.txt");
        sftp.GetFileInfoAsync(Left, file.FullPath, Arg.Any<CancellationToken>()).ThrowsAsync(new FileNotFoundException("gone"));

        await right.ReceiveFromPeerAsync([new(file)]);

        await sftp.DidNotReceiveWithAnyArgs().DeleteAsync(default, null!);
    }

    [TestMethod]
    public async Task ARelayThatFailsWhileWriting_KeepsThePartialTarget_WhenResumeIsOn()
    {
        // 与上传同一口径:开着断点续传,写到一半的目标就是下次续传的素材,不能清。
        (ISftpService sftp, _, FileBrowserViewModel right) =
            PanePair(new() { ConflictPolicy = "overwrite", ResumeEnabled = true, AutoCleanTempFiles = true });
        RemoteFileInfo file = Entry("/src/a.txt");
        Readable(sftp, Left, file);
        FailWhileWriting(sftp, "/a.txt");

        await right.ReceiveFromPeerAsync([new(file)]);

        await sftp.DidNotReceiveWithAnyArgs().DeleteAsync(default, null!);
    }

    [TestMethod]
    public async Task ARelayThatFailsWhileWriting_CleansUpThePartialTarget_WhenResumeIsOff()
    {
        (ISftpService sftp, _, FileBrowserViewModel right) =
            PanePair(new() { ConflictPolicy = "overwrite", ResumeEnabled = false, AutoCleanTempFiles = true });
        RemoteFileInfo file = Entry("/src/a.txt");
        Readable(sftp, Left, file);
        FailWhileWriting(sftp, "/a.txt");

        await right.ReceiveFromPeerAsync([new(file)]);

        await sftp.Received(1).DeleteAsync(Right, "/a.txt", Arg.Any<IProgress<SftpDeleteProgress>?>(), Arg.Any<CancellationToken>());
    }

    [TestMethod]
    public async Task ReceiveFromPeer_WithAPartialTarget_ResumesFromIt()
    {
        // 上一次中转在目标上留下了 4 字节,源有 10 字节:从 4 接着传(真正的起点由目标端再核实),
        // 而不是按同名冲突去问覆盖还是跳过。
        (ISftpService sftp, _, FileBrowserViewModel right) =
            PanePair(new() { ConflictPolicy = "ask", ResumeEnabled = true });
        sftp.ListDirectoryAsync(Right, "/", Arg.Any<CancellationToken>()).Returns([Entry("/a.txt", 4)]);
        sftp.GetFileInfoAsync(Right, "/a.txt", Arg.Any<CancellationToken>()).Returns(Entry("/a.txt", 4));
        RemoteFileInfo file = Entry("/src/a.txt", 10);
        Readable(sftp, Left, file);
        right.ConfirmRemoteOverwrite = _ => throw new AssertFailedException("续传不该弹同名冲突框。");

        await right.ReceiveFromPeerAsync([new(file)]);

        await sftp.Received(1).UploadStreamAsync(Right, Arg.Any<Stream>(), "/a.txt", 10, Arg.Any<DateTime?>(),
            Arg.Any<IProgress<TransferProgress>?>(), 4, Arg.Any<uint?>(), Arg.Any<CancellationToken>());
    }

    [TestMethod]
    public async Task ReceiveFromPeer_WithAFullSizeTarget_IsAConflictNotAResume()
    {
        (ISftpService sftp, _, FileBrowserViewModel right) =
            PanePair(new() { ConflictPolicy = "skip", ResumeEnabled = true });
        sftp.ListDirectoryAsync(Right, "/", Arg.Any<CancellationToken>()).Returns([Entry("/a.txt", 10)]);
        sftp.GetFileInfoAsync(Right, "/a.txt", Arg.Any<CancellationToken>()).Returns(Entry("/a.txt", 10));
        RemoteFileInfo file = Entry("/src/a.txt", 10);
        Readable(sftp, Left, file);

        await right.ReceiveFromPeerAsync([new(file)]);

        await sftp.DidNotReceiveWithAnyArgs().UploadStreamAsync(default, null!, null!, 0);
    }

    [TestMethod]
    public async Task APaneThatCannotReceive_DisablesTheCopyTowardIt_AndRefusesTransfers()
    {
        // 右栏是一个没实现流式上传的插件协议:往它那边的按钮置灰,代码调用也被拒并说明原因;反方向照常。
        ISftpService sftp = Substitute.For<ISftpService>();
        var vm = new DualSftpDocumentViewModel(
            new(Profile("alpha", ConnectionType.SSH), Left, null, (_, _) => Task.CompletedTask),
            new(Profile("bucket", ConnectionType.Plugin), Right, null, (_, _) => Task.CompletedTask) { AcceptsStreamedUploads = false },
            sftp,
            new TransferOptions());

        Assert.IsFalse(await vm.CopyLeftToRightCommand.CanExecute.FirstAsync());
        Assert.IsTrue(await vm.CopyRightToLeftCommand.CanExecute.FirstAsync());
        await vm.RightFiles.ReceiveFromPeerAsync([new(Entry("/src/a.txt"))]);
        Assert.IsNotNull(vm.RightFiles.ErrorMessage);
        await sftp.DidNotReceiveWithAnyArgs().UploadStreamAsync(default, null!, null!, 0);
        await vm.CloseAsync();
    }

    [TestMethod]
    public async Task APluginPane_GetsItsProtocolsRightClickActions()
    {
        (string Action, string Path)? invoked = null;
        var protocol = new PluginSdk.Protocols.ProtocolDescriptor
        {
            Id = "acme.s3",
            DisplayName = "S3",
            Actions = [new("share", "Copy share link", PluginSdk.Protocols.ProtocolActionScope.File)],
        };
        var vm = new DualSftpDocumentViewModel(
            new(Profile("alpha", ConnectionType.SSH), Left, null, (_, _) => Task.CompletedTask),
            new(Profile("bucket", ConnectionType.Plugin), Right, null, (_, _) => Task.CompletedTask)
            {
                Protocol = protocol,
                InvokeProtocolAction = (action, path) =>
                {
                    invoked = (action, path);
                    return Task.CompletedTask;
                },
            },
            Substitute.For<ISftpService>(),
            new TransferOptions());

        Assert.IsNotNull(vm.RightFiles.InvokeProtocolAction, "插件栏要带上协议的右键动作。");
        Assert.IsNull(vm.LeftFiles.InvokeProtocolAction);
        await vm.RightFiles.InvokeProtocolAction("share", "/bucket/a.txt");
        Assert.AreEqual(("share", "/bucket/a.txt"), invoked);
        await vm.CloseAsync();
    }

    private static void FailWhileWriting(ISftpService sftp, string target) =>
        sftp.UploadStreamAsync(Right, Arg.Any<Stream>(), target, Arg.Any<long>(), Arg.Any<DateTime?>(),
                Arg.Any<IProgress<TransferProgress>?>(), Arg.Any<long>(), Arg.Any<uint?>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new IOException("connection reset"));

    [TestMethod]
    public async Task Close_DisconnectsBothSessions_EvenWhenTheFirstDisconnectThrows()
    {
        ISftpService sftp = Substitute.For<ISftpService>();
        var disconnected = new List<Guid>();
        var vm = new DualSftpDocumentViewModel(
            new(Profile("alpha", ConnectionType.SSH), Left, null, (id, _) =>
            {
                disconnected.Add(id);
                throw new IOException("already gone");
            }),
            new(Profile("beta", ConnectionType.FTP), Right, null, (id, _) =>
            {
                disconnected.Add(id);
                return Task.CompletedTask;
            }),
            sftp,
            new TransferOptions());

        await Assert.ThrowsExactlyAsync<IOException>(() => vm.CloseAsync());

        Assert.AreSequenceEqual([Left, Right], disconnected, SequenceOrder.InAnyOrder, "一条断开失败不能让另一条留着没人关。");
        Assert.AreSequenceEqual([Left, Right], [.. vm.SessionIds]);
        Assert.AreEqual("alpha ⇄ beta", vm.Title);
    }

    [TestMethod]
    public void TheSameProfileOnBothSides_IsRejected()
    {
        SessionProfile profile = Profile("alpha", ConnectionType.SSH);

        Assert.ThrowsExactly<ArgumentException>(() => new DualSftpDocumentViewModel(
            new(profile, Left, null, (_, _) => Task.CompletedTask),
            new(profile, Right, null, (_, _) => Task.CompletedTask),
            Substitute.For<ISftpService>(),
            new TransferOptions()));
    }

    [TestMethod]
    public void Status_IsTheWorseOfTheTwo()
    {
        Assert.AreEqual(SessionStatus.Error, DualSftpDocumentViewModel.Worse(SessionStatus.Connected, SessionStatus.Error));
        Assert.AreEqual(SessionStatus.Disconnected, DualSftpDocumentViewModel.Worse(SessionStatus.Disconnected, SessionStatus.Connecting));
        Assert.AreEqual(SessionStatus.Connected, DualSftpDocumentViewModel.Worse(SessionStatus.Connected, SessionStatus.Connected));
    }

    [TestMethod]
    public async Task CompareDirectories_SelectsWhatDiffersOnEachSide()
    {
        ISftpService sftp = Substitute.For<ISftpService>();
        DateTime t = new(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);
        sftp.GetWorkingDirectoryAsync(Left, Arg.Any<CancellationToken>()).Returns("/l");
        sftp.GetWorkingDirectoryAsync(Right, Arg.Any<CancellationToken>()).Returns("/r");
        sftp.ListDirectoryAsync(Left, "/l", Arg.Any<CancellationToken>()).Returns(
        [
            Entry("/l/same.txt", 3, modified: t),
            Entry("/l/only-left.txt", 3, modified: t),
            Entry("/l/newer.txt", 3, modified: t.AddHours(1)),
        ]);
        sftp.ListDirectoryAsync(Right, "/r", Arg.Any<CancellationToken>()).Returns(
        [
            Entry("/r/same.txt", 3, modified: t),
            Entry("/r/newer.txt", 3, modified: t),
            Entry("/r/only-right.txt", 3, modified: t),
        ]);
        var vm = new DualSftpDocumentViewModel(
            new(Profile("alpha", ConnectionType.SSH), Left, null, (_, _) => Task.CompletedTask),
            new(Profile("beta", ConnectionType.SFTP), Right, null, (_, _) => Task.CompletedTask),
            sftp,
            new TransferOptions());
        await vm.InitialLoadTask;

        await vm.CompareDirectoriesCommand.Execute().FirstAsync();

        Assert.AreSequenceEqual(["only-left.txt", "newer.txt"], [.. vm.LeftFiles.SelectedFiles.Select(f => f.Name)], SequenceOrder.InAnyOrder);
        Assert.AreSequenceEqual(["only-right.txt"], [.. vm.RightFiles.SelectedFiles.Select(f => f.Name)], SequenceOrder.InAnyOrder);
        Assert.IsNotNull(vm.CompareSummary);
        await vm.CloseAsync();
    }

    private static SessionProfile Profile(string name, ConnectionType type) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        Host = $"{name}.example.com",
        Username = "u",
        ConnectionType = type,
    };
}
