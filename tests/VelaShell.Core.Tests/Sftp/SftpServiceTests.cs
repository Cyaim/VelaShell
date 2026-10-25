using NSubstitute;
using NSubstitute.ExceptionExtensions;
using VelaShell.Core.Data;
using VelaShell.Core.Models;
using VelaShell.Core.Sftp;
using VelaShell.Core.Ssh;

namespace VelaShell.Core.Tests.Sftp;

[TestClass]
[TestCategory("Sftp")]
public class SftpServiceTests
{
    private readonly ISshConnectionService _connectionService;
    private readonly Guid _sessionId;
    private readonly ISftpClientWrapper _sftpClient;
    private readonly SftpService _sftpService;
    private readonly SshSession _session;

    public SftpServiceTests()
    {
        _connectionService = Substitute.For<ISshConnectionService>();
        _sftpClient = Substitute.For<ISftpClientWrapper>();
        _sessionId = Guid.NewGuid();
        var session = new SshSession
        {
            SessionId = _sessionId,
            ConnectionInfo = new()
            {
                Host = "test.example.com",
                Port = 22,
                Username = "testuser",
                AuthMethod = AuthMethod.Password,
                Password = "testpass"
            },
            Status = SessionStatus.Connected
        };
        _session = session;
        _connectionService.GetSession(_sessionId).Returns(session);

        // 这一组用例测的是**纯 SFTP 路径**:没有 exec 通道,目录删除只能靠逐条递归。
        // 必须显式写成 null —— NSubstitute 的递归替身会给接口返回值自动造一个替身,
        // 于是 GetClient 交回一个凭空出现的 SSH 客户端,目录删除会拐进 rm -rf 快路径(#474)。
        _connectionService.GetClient(_sessionId).Returns((ISshClientWrapper?)null);
        _sftpClient.IsConnected.Returns(true);

        // 这一组多数用例验的是续传起点、字节与取消,上传走不带时间戳的两个重载;
        // 保留时间戳(关闭之前按同一个句柄设修改时间)另有专门的用例,自己建服务。
        ISettingsService settings = Substitute.For<ISettingsService>();
        settings.GetSettingsAsync().Returns(new AppSettings { Transfer = { PreserveTimestamps = false } });
        _sftpService = new SftpService(_connectionService, _ => _sftpClient, settings);
    }

    /// <summary>
    /// 会话没了(从没登记、已断开)用专门的类型报:传输失败重试据此不在原会话上空转,
    /// 而是把这一项留给换一条连接再接着传。仍是 InvalidOperationException,按基类接的调用方不受影响。
    /// </summary>
    [TestMethod]
    public async Task AMissingOrDisconnectedSession_IsReportedAsUnavailable()
    {
        Guid unknown = Guid.NewGuid();
        _connectionService.GetSession(unknown).Returns((SshSession?)null);

        await Assert.ThrowsExactlyAsync<RemoteSessionUnavailableException>(() => _sftpService.ListDirectoryAsync(unknown, "/"));

        _session.Status = SessionStatus.Disconnected;
        RemoteSessionUnavailableException ex = await Assert.ThrowsExactlyAsync<RemoteSessionUnavailableException>(
            () => _sftpService.UploadFileAsync(_sessionId, "C:/nowhere.bin", "/nowhere.bin"));
        Assert.IsInstanceOfType<InvalidOperationException>(ex);
    }

    [TestMethod]
    public async Task AtomicUploadCommit_NoOverwriteNeverFallsBackToPosixOrDeletesTarget()
    {
        _sftpClient.RenameFileAsync("/temp", "/target", Arg.Any<CancellationToken>())
            .ThrowsAsync(new NotSupportedException("normal rename rejected"));
        await Assert.ThrowsExactlyAsync<NotSupportedException>(() => _sftpService.CommitUploadAsync(_sessionId, "/temp", "/target", false));
        await _sftpClient.DidNotReceive().PosixRenameFileAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _sftpClient.DidNotReceive().AtomicReplaceFileAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _sftpClient.DidNotReceive().DeleteFileAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [TestMethod]
    public async Task AtomicUploadCommit_OverwriteRequiresStrictReplacementAndNeverDeletesTarget()
    {
        _sftpClient.AtomicReplaceFileAsync("/temp", "/target", Arg.Any<CancellationToken>())
            .ThrowsAsync(new NotSupportedException("no POSIX extension"));
        await Assert.ThrowsExactlyAsync<NotSupportedException>(() => _sftpService.CommitUploadAsync(_sessionId, "/temp", "/target", true));
        await _sftpClient.DidNotReceive().RenameFileAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _sftpClient.DidNotReceive().PosixRenameFileAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _sftpClient.DidNotReceive().DeleteFileAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// 覆盖提交换进来的是临时文件那个 inode(按 0644 新建):被替换的普通文件原有的属主与权限位先带过去。
    /// 属主先改、权限后改(非 root 改属主时内核会清掉 setuid / setgid);属主没改成(不是 root)时 setuid / setgid 不带 ——
    /// 否则凭空造出一个以上传者身份运行的 setuid 程序。粘滞位照带。
    /// </summary>
    [TestMethod]
    [DataRow("100600", true, "600")]
    [DataRow("100755", true, "755")]
    [DataRow("106755", true, "6755")]
    [DataRow("106755", false, "755")]
    [DataRow("101777", false, "1777")]
    public async Task AtomicUploadCommit_OverwriteCarriesTheReplacedFilesOwnerThenModeOntoTheTemporary(string target, bool ownerAccepted, string expected)
    {
        _sftpClient.SupportsAtomicReplace.Returns(true);
        _sftpClient.GetLinkModeAsync("/target", Arg.Any<CancellationToken>()).Returns(new SftpFileMode(Octal(target), 1000, 1000));
        if (!ownerAccepted)
        {
            _sftpClient.ChangeOwnerAsync("/temp", 1000, 1000, Arg.Any<CancellationToken>())
                .ThrowsAsync(new VelaSftpPermissionDeniedException("Permission denied"));
        }

        await _sftpService.CommitUploadAsync(_sessionId, "/temp", "/target", true);

        Received.InOrder(() =>
        {
            _sftpClient.ChangeOwnerAsync("/temp", 1000, 1000, Arg.Any<CancellationToken>());
            _sftpClient.SetModeAsync("/temp", Octal(expected), Arg.Any<CancellationToken>());
            _sftpClient.AtomicReplaceFileAsync("/temp", "/target", Arg.Any<CancellationToken>());
        });
    }

    /// <summary>新文件(目标不存在)与非普通文件的目标(被替换的是链接本身)不动模式,与新建上传同一口径。</summary>
    [TestMethod]
    [DataRow(null)]
    [DataRow("120777")]
    public async Task AtomicUploadCommit_OverwriteLeavesNewFilesAndLinksWithTheirCreationMode(string? target)
    {
        _sftpClient.SupportsAtomicReplace.Returns(true);
        _sftpClient.GetLinkModeAsync("/target", Arg.Any<CancellationToken>())
            .Returns(target is null ? null : new SftpFileMode(Octal(target), 1000, 1000));

        await _sftpService.CommitUploadAsync(_sessionId, "/temp", "/target", true);

        await _sftpClient.DidNotReceive().SetModeAsync(Arg.Any<string>(), Arg.Any<uint>(), Arg.Any<CancellationToken>());
        await _sftpClient.DidNotReceive().ChangeOwnerAsync(Arg.Any<string>(), Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<CancellationToken>());
        await _sftpClient.Received(1).AtomicReplaceFileAsync("/temp", "/target", Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// 覆盖上传的临时文件按被替换文件的 0777 那几位新建(setuid / setgid / 粘滞位留给提交:属主还没对上);
    /// 新文件、链接按服务端默认;是个条目却读不到模式时按 0600(提交反正会拒绝)。
    /// </summary>
    [TestMethod]
    [DataRow("100600", "600")]
    [DataRow("100755", "755")]
    [DataRow("106755", "755")]
    [DataRow("101777", "777")]
    [DataRow("120777", null)]
    [DataRow(null, null)]
    public async Task UploadCreateMode_IsTheReplacedRegularFilesPermissionBits(string? target, string? expected)
    {
        _sftpClient.GetLinkModeAsync("/target", Arg.Any<CancellationToken>())
            .Returns(target is null ? null : new SftpFileMode(Octal(target), 1000, 1000));

        uint? mode = await _sftpService.GetUploadCreateModeAsync(_sessionId, "/target");

        Assert.AreEqual(expected is null ? null : Octal(expected), mode);
    }

    [TestMethod]
    public async Task UploadCreateMode_UnknownModeOfAnExistingEntry_IsOwnerOnly()
    {
        _sftpClient.GetLinkModeAsync("/target", Arg.Any<CancellationToken>()).Returns(new SftpFileMode(null, null, null));

        Assert.AreEqual(Octal("600"), await _sftpService.GetUploadCreateModeAsync(_sessionId, "/target"));
    }

    /// <summary>流式上传把新建权限位交给客户端:随 OPEN 一起发出,文件一建出来就是这个模式。</summary>
    [TestMethod]
    public async Task UploadStream_WithCreateMode_PassesItToTheClient()
    {
        using var source = new MemoryStream(new byte[4]);

        await _sftpService.UploadStreamAsync(_sessionId, source, "/dir/.vela-up-1", 4, createMode: Octal("600"));

        await _sftpClient.Received(1).UploadAsync(source, "/dir/.vela-up-1",
            Arg.Is<RemoteUploadOptions>(o => o.CreateMode == Octal("600") && o.ResumeOffset == 0),
            Arg.Any<Action<ulong>?>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// 带不过去就只许收窄、不许放宽:目标存在却读不到它的模式,或服务端不许改临时文件的模式而它比目标宽,就拒绝提交,目标原样不动;
    /// 临时文件本来就不比目标宽(只是少了执行位)时照样提交。属主没报时不改属主(当成 0 等于把文件交给 root)。
    /// </summary>
    [TestMethod]
    [DataRow("unreported", "100644", false)]
    [DataRow("100600", "100644", false)]
    [DataRow("100755", "100644", true)]
    public async Task AtomicUploadCommit_OverwriteNeverWidensTheReplacedFilesPermissions(string target, string temporary, bool commits)
    {
        _sftpClient.SupportsAtomicReplace.Returns(true);
        _sftpClient.GetLinkModeAsync("/target", Arg.Any<CancellationToken>())
            .Returns(new SftpFileMode(target == "unreported" ? null : Octal(target), null, null));
        _sftpClient.GetLinkModeAsync("/temp", Arg.Any<CancellationToken>()).Returns(new SftpFileMode(Octal(temporary), null, null));
        _sftpClient.SetModeAsync("/temp", Arg.Any<uint>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new VelaSftpPermissionDeniedException("setstat disabled"));

        Task commit = _sftpService.CommitUploadAsync(_sessionId, "/temp", "/target", true);

        if (commits)
        {
            await commit;
            await _sftpClient.Received(1).AtomicReplaceFileAsync("/temp", "/target", Arg.Any<CancellationToken>());
        }
        else
        {
            Exception refused = await Assert.ThrowsAsync<Exception>(() => commit);
            Assert.IsTrue(refused is NotSupportedException or VelaSftpOperationException, refused.GetType().Name);
            await _sftpClient.DidNotReceive().AtomicReplaceFileAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        }
        await _sftpClient.DidNotReceive().ChangeOwnerAsync(Arg.Any<string>(), Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<CancellationToken>());
        await _sftpClient.DidNotReceive().DeleteFileAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// 提交预检:要覆盖而服务端没有原子替换、不覆盖而目标已存在,都在写之前就拒;问不出目标在不在就不拦(提交仍是权威)。
    /// </summary>
    [TestMethod]
    public async Task UploadCommitPreflight_RefusesOnlyCommitsThatWouldCertainlyBeRefused()
    {
        _sftpClient.SupportsAtomicReplace.Returns(false);
        await Assert.ThrowsExactlyAsync<NotSupportedException>(() => _sftpService.CheckUploadCommitAsync(_sessionId, "/target", true));
        _sftpClient.SupportsAtomicReplace.Returns(true);
        await _sftpService.CheckUploadCommitAsync(_sessionId, "/target", true);

        _sftpClient.ExistsAsync("/target", Arg.Any<CancellationToken>()).Returns(true);
        await Assert.ThrowsExactlyAsync<VelaSftpOperationException>(() => _sftpService.CheckUploadCommitAsync(_sessionId, "/target", false));
        _sftpClient.ExistsAsync("/target", Arg.Any<CancellationToken>()).Returns(false);
        await _sftpService.CheckUploadCommitAsync(_sessionId, "/target", false);
        _sftpClient.ExistsAsync("/target", Arg.Any<CancellationToken>()).ThrowsAsync(new VelaSftpPermissionDeniedException("Permission denied"));
        await _sftpService.CheckUploadCommitAsync(_sessionId, "/target", false);
    }

    private static uint Octal(string digits) => Convert.ToUInt32(digits, 8);

    /// <summary>
    /// SFTP 通道死了(sftp-server 退出、服务端关掉闲置通道)而 SSH 连接还在:下一次操作换一个新客户端,
    /// 旧的那个释放掉 —— 而不是让文件面板一直坏到整条 SSH 连接重连。
    /// </summary>
    [TestMethod]
    public async Task DeadSftpChannel_IsReplacedAndDisposed()
    {
        ISftpClientWrapper first = Substitute.For<ISftpClientWrapper>();
        ISftpClientWrapper second = Substitute.For<ISftpClientWrapper>();
        first.IsConnected.Returns(true);
        second.IsConnected.Returns(true);
        Queue<ISftpClientWrapper> made = new([first, second]);
        var service = new SftpService(_connectionService, _ => made.Dequeue());

        await service.ListDirectoryAsync(_sessionId, "/");
        first.IsConnected.Returns(false);   // 通道死了
        await service.ListDirectoryAsync(_sessionId, "/");

        await second.Received(1).ListDirectoryAsync("/", Arg.Any<CancellationToken>());
        await first.Received(1).DisposeAsync();
        Assert.IsEmpty(made, "死了才换,换一次");
    }

    /// <summary>
    /// 传输与浏览各用一条 SFTP 通道:上传大文件时几十个 WRITE 在途,浏览的 STAT / READDIR 不该排在它们后面。
    /// </summary>
    [TestMethod]
    public async Task Transfers_UseTheirOwnSftpChannel()
    {
        ISftpClientWrapper browsing = Substitute.For<ISftpClientWrapper>();
        ISftpClientWrapper transfer = Substitute.For<ISftpClientWrapper>();
        browsing.IsConnected.Returns(true);
        transfer.IsConnected.Returns(true);
        Queue<ISftpClientWrapper> made = new([browsing, transfer]);
        var service = new SftpService(_connectionService, _ => made.Dequeue());

        await service.ListDirectoryAsync(_sessionId, "/");
        await using (await service.OpenReadAsync(_sessionId, "/big.iso")) { }
        await service.ListDirectoryAsync(_sessionId, "/");

        await transfer.Received(1).OpenAsync("/big.iso", FileMode.Open, FileAccess.Read, Arg.Any<CancellationToken>());
        await browsing.DidNotReceive().OpenAsync(Arg.Any<string>(), Arg.Any<FileMode>(), Arg.Any<FileAccess>(), Arg.Any<CancellationToken>());
        await browsing.Received(2).ListDirectoryAsync("/", Arg.Any<CancellationToken>());

        await service.CloseSessionAsync(_sessionId);
        await browsing.Received(1).DisposeAsync();
        await transfer.Received(1).DisposeAsync();
    }

    /// <summary>开不出第二条通道(服务端 MaxSessions 太小):传输退回浏览那一条,之后不再试。</summary>
    [TestMethod]
    public async Task Transfers_FallBackToTheBrowsingChannel_WhenASecondCannotOpen()
    {
        ISftpClientWrapper browsing = Substitute.For<ISftpClientWrapper>();
        ISftpClientWrapper refused = Substitute.For<ISftpClientWrapper>();
        browsing.IsConnected.Returns(true);
        refused.ConnectAsync(Arg.Any<CancellationToken>()).Returns(Task.FromException(new VelaSshClientException("administratively prohibited")));
        Queue<ISftpClientWrapper> made = new([browsing, refused]);
        var service = new SftpService(_connectionService, _ => made.Dequeue());

        await using (await service.OpenReadAsync(_sessionId, "/a")) { }
        await using (await service.OpenReadAsync(_sessionId, "/b")) { }

        await browsing.Received(1).OpenAsync("/a", FileMode.Open, FileAccess.Read, Arg.Any<CancellationToken>());
        await browsing.Received(1).OpenAsync("/b", FileMode.Open, FileAccess.Read, Arg.Any<CancellationToken>());
        await refused.Received(1).DisposeAsync();
        Assert.IsEmpty(made, "第二条开不出来就记住,不再每次都试");
    }

    /// <summary>SSH 一断,挂在它上面的 SFTP 通道跟着收掉。</summary>
    /// <remarks>
    /// SFTP 复用的就是主 SSH 连接(DI 处 <c>OpenSftpClientAsync</c> 刻意不另开连接),
    /// 所以连接一没,缓存里那个客户端就只是个还占着句柄的死物。清理挂在
    /// <c>SessionDisconnected</c> 上而不是各个断开入口里 —— 入口有好几个
    /// (标签断开、关标签、隧道面板、退出应用),靠约定迟早漏一个。
    /// </remarks>
    [TestMethod]
    public async Task ASshDisconnect_TakesTheSftpChannelWithIt()
    {
        // 先让服务真的建出并缓存一个客户端,否则没有东西可关。
        _sftpClient.WorkingDirectory.Returns("/home/testuser");
        await _sftpService.GetWorkingDirectoryAsync(_sessionId);

        var disposed = new TaskCompletionSource();
#pragma warning disable CA2012 // NSubstitute 的配桩写法:调 DisposeAsync() 只是指名要配的成员,替身交回的 ValueTask 没人等
        _sftpClient.When(c => c.DisposeAsync()).Do(_ => disposed.TrySetResult());
#pragma warning restore CA2012

        _connectionService.SessionDisconnected += Raise.Event<Action<SshSession>>(_session);

        // 清理是即发即忘的:断开路径不等它,所以这里得等。
        await disposed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await _sftpClient.Received(1).DisposeAsync();
    }

    [TestMethod]
    public async Task UploadFileAsync_PreserveTimestampsOn_SetsRemoteMtimeToLocalFileMtime()
    {
        // 默认(无设置服务)保留时间戳 = true:上传完成后必须把远端 mtime 设回本地源文件的 mtime(scp -p 语义)。
        string localPath = Path.Combine(Path.GetTempPath(), $"vela-mtime-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(localPath, "timestamp probe");
        var knownUtc = new DateTime(2026, 7, 20, 3, 4, 5, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(localPath, knownUtc);
        try
        {
            var service = new SftpService(_connectionService, _ => _sftpClient);

            await service.UploadFileAsync(_sessionId, localPath, "/home/user/up.txt");

            // 关闭之前按同一个句柄设(一次往返),不再上传完之后 STAT + SETSTAT 两次往返。
            await _sftpClient.Received(1).UploadAsync(
                Arg.Any<Stream>(),
                "/home/user/up.txt",
                Arg.Is<RemoteUploadOptions>(o => o.ResumeOffset == 0 && o.LastWriteTime!.Value.UtcDateTime == knownUtc && !o.Fsync),
                Arg.Any<Action<ulong>?>(),
                Arg.Any<CancellationToken>());
            await _sftpClient.DidNotReceiveWithAnyArgs().SetLastWriteTimeAsync(default!, default, default);
        }
        finally
        {
            File.Delete(localPath);
        }
    }

    [TestMethod]
    public async Task UploadFileAsync_PreserveTimestampsOff_DoesNotTouchRemoteMtime()
    {
        ISettingsService settings = Substitute.For<ISettingsService>();
        settings.GetSettingsAsync().Returns(new AppSettings { Transfer = { PreserveTimestamps = false } });
        var service = new SftpService(_connectionService, _ => _sftpClient, settings);
        string localPath = Path.Combine(Path.GetTempPath(), $"vela-mtime-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(localPath, "timestamp probe");
        try
        {
            _sftpClient.UploadAsync(Arg.Any<Stream>(), Arg.Any<string>(), Arg.Any<Action<ulong>>(), Arg.Any<CancellationToken>())
                       .Returns(Task.CompletedTask);

            await service.UploadFileAsync(_sessionId, localPath, "/home/user/up.txt");

            await _sftpClient.DidNotReceive().SetLastWriteTimeAsync(
                Arg.Any<string>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());
            await _sftpClient.DidNotReceive().UploadAsync(
                Arg.Any<Stream>(), Arg.Any<string>(), Arg.Any<RemoteUploadOptions>(), Arg.Any<Action<ulong>?>(), Arg.Any<CancellationToken>());
        }
        finally
        {
            File.Delete(localPath);
        }
    }

    /// <summary>「上传后落盘」打开:带着落盘要求上传(关闭之前做);默认关时不带。</summary>
    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task UploadFileAsync_FsyncAfterUpload_IsPassedToTheClient(bool fsync)
    {
        ISettingsService settings = Substitute.For<ISettingsService>();
        settings.GetSettingsAsync().Returns(new AppSettings { Transfer = { PreserveTimestamps = false, FsyncAfterUpload = fsync } });
        var service = new SftpService(_connectionService, _ => _sftpClient, settings);
        string localPath = Path.Combine(Path.GetTempPath(), $"vela-fsync-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(localPath, "durable");
        try
        {
            await service.UploadFileAsync(_sessionId, localPath, "/home/user/up.txt");

            if (fsync)
            {
                await _sftpClient.Received(1).UploadAsync(
                    Arg.Any<Stream>(), "/home/user/up.txt",
                    Arg.Is<RemoteUploadOptions>(o => o.Fsync && o.LastWriteTime == null),
                    Arg.Any<Action<ulong>?>(), Arg.Any<CancellationToken>());
            }
            else
            {
                await _sftpClient.DidNotReceive().UploadAsync(
                    Arg.Any<Stream>(), Arg.Any<string>(), Arg.Any<RemoteUploadOptions>(), Arg.Any<Action<ulong>?>(), Arg.Any<CancellationToken>());
            }
        }
        finally
        {
            File.Delete(localPath);
        }
    }

    [TestMethod]
    public async Task GetWorkingDirectoryAsync_ReturnsClientWorkingDirectory()
    {
        _sftpClient.WorkingDirectory.Returns("/home/testuser");
        string result = await _sftpService.GetWorkingDirectoryAsync(_sessionId);
        Assert.AreEqual("/home/testuser", result);
    }

    [TestMethod]
    public async Task CloseSessionAsync_DisposesClient()
    {
        // Open a channel so it gets cached, then close it.
        _sftpClient.ListDirectoryAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                   .Returns(Task.FromResult<IEnumerable<SftpEntry>>([]));
        await _sftpService.ListDirectoryAsync(_sessionId, "/");
        await _sftpService.CloseSessionAsync(_sessionId);
        await _sftpClient.Received(1).DisposeAsync();
    }

    [TestMethod]
    public async Task CloseSessionAsync_UnknownSession_IsNoOp()
    {
        await _sftpService.CloseSessionAsync(Guid.NewGuid());
        await _sftpClient.DidNotReceive().DisposeAsync();
    }

    [TestMethod]
    public async Task RenameAsync_CallsRenameFileOnClient()
    {
        await _sftpService.RenameAsync(_sessionId, "/home/user/old.txt", "/home/user/new.txt");
        await _sftpClient.Received(1).RenameFileAsync("/home/user/old.txt", "/home/user/new.txt", Arg.Any<CancellationToken>());
    }

    [TestMethod]
    public async Task RenameAsync_WhenPlainRenameRejected_FallsBackToPosixRename()
    {
        // Some servers answer the plain SSH_FXP_RENAME with SSH_FX_BAD_MESSAGE.
        _sftpClient
            .RenameFileAsync("/home/user/dir", "/tmp/dir", Arg.Any<CancellationToken>())
            .ThrowsAsync(new VelaSftpOperationException("bad message"));
        await _sftpService.RenameAsync(_sessionId, "/home/user/dir", "/tmp/dir");
        await _sftpClient.Received(1).PosixRenameFileAsync("/home/user/dir", "/tmp/dir", Arg.Any<CancellationToken>());
    }

    [TestMethod]
    public async Task RenameAsync_WhenTargetExists_DoesNotFallBackToOverwritingPosixRename()
    {
        // posix-rename overwrites atomically; falling back to it when the plain rename failed
        // because the target exists would silently destroy the user's file.
        _sftpClient
            .RenameFileAsync("/home/user/a.txt", "/home/user/b.txt", Arg.Any<CancellationToken>())
            .ThrowsAsync(new VelaSftpOperationException("failure"));
        _sftpClient.ExistsAsync("/home/user/b.txt", Arg.Any<CancellationToken>()).Returns(true);

        VelaSftpOperationException ex = await Assert.ThrowsExactlyAsync<VelaSftpOperationException>(
            () => _sftpService.RenameAsync(_sessionId, "/home/user/a.txt", "/home/user/b.txt"));

        Assert.AreEqual("failure", ex.Message);
        await _sftpClient.DidNotReceive().PosixRenameFileAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [TestMethod]
    public async Task RenameAsync_WhenBothRenamesFail_SurfacesOriginalError()
    {
        _sftpClient
            .RenameFileAsync("/home/user/dir", "/tmp/dir", Arg.Any<CancellationToken>())
            .ThrowsAsync(new VelaSftpOperationException("bad message"));
        _sftpClient
            .PosixRenameFileAsync("/home/user/dir", "/tmp/dir", Arg.Any<CancellationToken>())
            .ThrowsAsync(new NotSupportedException("posix-rename not supported"));
        VelaSftpOperationException ex = await Assert.ThrowsExactlyAsync<VelaSftpOperationException>(() => _sftpService.RenameAsync(_sessionId, "/home/user/dir", "/tmp/dir"));
        Assert.AreEqual("bad message", ex.Message);
    }

    [TestMethod]
    public async Task UploadFileAsync_WhenCancelledMidTransfer_SurfacesCleanOperationCanceled()
    {
        string localPath = Path.Combine(Path.GetTempPath(), $"vela-up-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(localPath, "payload");
        using var cts = new CancellationTokenSource();
        try
        {
            // 兜底路径:取消之后连接被撕掉,底层先冒出的是 IO 错误而不是取消异常。
            // 服务必须把它归一成干净的 OperationCanceledException,别让 IOException 漏到上层
            // 被当成"传输失败"而标红。
            _sftpClient.UploadAsync(Arg.Any<Stream>(), "/home/user/up.txt",
                           Arg.Any<Action<ulong>?>(), Arg.Any<CancellationToken>())
                       .Returns(_ =>
                       {
                           cts.Cancel();
                           throw new IOException("connection torn down by cancellation");
                       });
            await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => _sftpService.UploadFileAsync(_sessionId, localPath, "/home/user/up.txt", null, cancellationToken: cts.Token));
        }
        finally
        {
            File.Delete(localPath);
        }
    }

    /// <summary>
    /// 正常取消路径:底层库自己响应 CancellationToken 抛出 OperationCanceledException,
    /// 服务原样放行 —— 不额外包一层、也不产生任何多余的异常。
    /// <para>
    /// 这条锁定的是"取消不该制造噪音"。早先的实现会在取消时把本地流 Dispose 掉
    /// (SSH.NET 时代的绕行手法,那时传输不响应取消),导致库内部先抛
    /// ObjectDisposedException/IOException,再被翻译成取消 —— 一次取消白白多出两个异常,
    /// 还和正在读写该流的工作线程赛跑。
    /// </para>
    /// </summary>
    [TestMethod]
    public async Task UploadFileAsync_WhenLibraryHonoursTheToken_PropagatesCancellationWithoutExtraExceptions()
    {
        string localPath = Path.Combine(Path.GetTempPath(), $"vela-up-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(localPath, "payload");
        using var cts = new CancellationTokenSource();
        var thrown = new OperationCanceledException(cts.Token);
        try
        {
            _sftpClient.UploadAsync(Arg.Any<Stream>(), "/home/user/up.txt",
                           Arg.Any<Action<ulong>?>(), Arg.Any<CancellationToken>())
                       .Returns(_ =>
                       {
                           cts.Cancel();
                           throw thrown;
                       });

            OperationCanceledException actual = await Assert.ThrowsExactlyAsync<OperationCanceledException>(
                () => _sftpService.UploadFileAsync(_sessionId, localPath, "/home/user/up.txt", null, cancellationToken: cts.Token));

            // 同一个实例原样上抛,证明没有被重新包装(重新包装 = 又多一个异常)。
            Assert.AreSame(thrown, actual);
        }
        finally
        {
            File.Delete(localPath);
        }
    }

    [TestMethod]
    public async Task SetPermissionsAsync_CallsChangePermissionsOnClient()
    {
        await _sftpService.SetPermissionsAsync(_sessionId, "/home/user/run.sh", 755);
        await _sftpClient.Received(1).ChangePermissionsAsync("/home/user/run.sh", 755, Arg.Any<CancellationToken>());
    }

    [TestMethod]
    [DataRow((short)-1)]
    [DataRow((short)778)] // last digit > 7
    [DataRow((short)787)] // middle digit > 7
    [DataRow((short)800)] // leading digit > 7
    public async Task SetPermissionsAsync_RejectsNonOctalModes(short mode)
    {
        await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(() => _sftpService.SetPermissionsAsync(_sessionId, "/home/user/run.sh", mode));
        await _sftpClient.DidNotReceive().ChangePermissionsAsync(Arg.Any<string>(), Arg.Any<short>(), Arg.Any<CancellationToken>());
    }

    [TestMethod]
    public async Task CreateFileAsync_UploadsAnEmptyStream()
    {
        await _sftpService.CreateFileAsync(_sessionId, "/home/user/empty.txt");
        await _sftpClient.Received(1).UploadAsync(Arg.Any<Stream>(),
            "/home/user/empty.txt",
            Arg.Any<Action<ulong>?>(),
            Arg.Any<CancellationToken>());
    }

    [TestMethod]
    public async Task ListDirectoryAsync_ReturnsRemoteFileInfoArray()
    {
        // Arrange
        var mockFiles = new List<SftpEntry>
        {
            CreateMockSftpFile("file1.txt", "/home/user/file1.txt", 1024, false, "rw-r--r--"),
            CreateMockSftpFile("dir1", "/home/user/dir1", 0, true, "rwxr-xr-x")
        };
        _sftpClient.ListDirectoryAsync("/home/user", Arg.Any<CancellationToken>())
                   .Returns(Task.FromResult<IEnumerable<SftpEntry>>(mockFiles));

        // Act
        List<RemoteFileInfo> result = await _sftpService.ListDirectoryAsync(_sessionId, "/home/user");

        // Assert
        Assert.HasCount(2, result);
        Assert.AreEqual("file1.txt", result[0].Name);
        Assert.AreEqual("/home/user/file1.txt", result[0].FullPath);
        Assert.AreEqual(1024L, result[0].Size);
        Assert.IsFalse(result[0].IsDirectory);
        Assert.Contains("rw-r--r--", result[0].Permissions);
        Assert.AreEqual("dir1", result[1].Name);
        Assert.IsTrue(result[1].IsDirectory);
        Assert.Contains("rwxr-xr-x", result[1].Permissions);
    }

    [TestMethod]
    public async Task UploadFileAsync_VerifiesBytesWritten()
    {
        // Arrange
        string localPath = Path.GetTempFileName();
        byte[] testData = new byte[1024];
        new Random().NextBytes(testData);
        await File.WriteAllBytesAsync(localPath, testData);
        string remotePath = "/home/user/uploaded.bin";
        _sftpClient.UploadAsync(Arg.Any<Stream>(),
                       remotePath,
                       Arg.Do<Action<ulong>?>(callback =>
                       {
                           if (callback != null)
                           {
                               callback(512);
                               callback(1024);
                           }
                       }),
                       Arg.Any<CancellationToken>())
                   .Returns(Task.CompletedTask);
        var progressReports = new List<TransferProgress>();

        // Act
        await _sftpService.UploadFileAsync(_sessionId, localPath, remotePath,
            new SynchronousProgress<TransferProgress>(progressReports.Add));

        // Assert
        await _sftpClient.Received(1).UploadAsync(Arg.Any<Stream>(),
            remotePath,
            Arg.Any<Action<ulong>?>(),
            Arg.Any<CancellationToken>());
        Assert.IsNotEmpty(progressReports, "because progress should be reported during upload");
        File.Delete(localPath);
    }

    [TestMethod]
    public async Task DownloadFileAsync_VerifiesBytesRead()
    {
        // Arrange
        string localPath = Path.Combine(Path.GetTempPath(), $"download_{Guid.NewGuid()}.bin");
        string remotePath = "/home/user/remote.bin";
        SftpEntry mockFile = CreateMockSftpFile("remote.bin", remotePath, 2048, false, "rw-r--r--");
        _sftpClient.GetEntryAsync(remotePath, Arg.Any<CancellationToken>()).Returns(mockFile);
        _sftpClient.DownloadAsync(remotePath,
                       Arg.Any<Stream>(),
                       Arg.Do<Action<ulong>?>(callback =>
                       {
                           if (callback != null)
                           {
                               callback(512);
                               callback(2048);
                           }
                       }),
                       Arg.Any<CancellationToken>())
                   .Returns(async callInfo =>
                   {
                       Stream? stream = callInfo.Arg<Stream>();
                       byte[] testData = new byte[2048];
                       new Random().NextBytes(testData);
                       await stream.WriteAsync(testData);
                   });
        var progressReports = new List<TransferProgress>();

        // Act
        await _sftpService.DownloadFileAsync(_sessionId, remotePath, localPath,
            new SynchronousProgress<TransferProgress>(progressReports.Add));

        // Assert
        await _sftpClient.Received(1).DownloadAsync(remotePath,
            Arg.Any<Stream>(),
            Arg.Any<Action<ulong>?>(),
            Arg.Any<CancellationToken>());
        Assert.IsTrue(File.Exists(localPath));
        Assert.IsNotEmpty(progressReports);
        Assert.AreEqual(2048L, progressReports.Last().BytesTransferred);
        if (File.Exists(localPath))
        {
            File.Delete(localPath);
        }
    }

    [TestMethod]
    public async Task DeleteAsync_DeletesFile()
    {
        // Arrange
        string remotePath = "/home/user/todelete.txt";
        SftpEntry mockFile = CreateMockSftpFile("todelete.txt", remotePath, 1024, false, "rw-r--r--");
        _sftpClient.GetEntryAsync(remotePath, Arg.Any<CancellationToken>()).Returns(mockFile);

        // Act
        await _sftpService.DeleteAsync(_sessionId, remotePath);

        // Assert
        await _sftpClient.Received(1).DeleteFileAsync(remotePath, Arg.Any<CancellationToken>());
        await _sftpClient.DidNotReceive().DeleteDirectoryAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());

        // 单次 stat 就该回答"存在吗 + 是不是目录",不该再去列举父目录。
        await _sftpClient.DidNotReceive().ListDirectoryAsync("/home/user", Arg.Any<CancellationToken>());
    }

    [TestMethod]
    public async Task CreateDirectoryAsync_CreatesDirectory()
    {
        // Arrange
        string remotePath = "/home/user/newdir";

        // Act
        await _sftpService.CreateDirectoryAsync(_sessionId, remotePath);

        // Assert
        await _sftpClient.Received(1).CreateDirectoryAsync(remotePath, Arg.Any<CancellationToken>());
    }

    [TestMethod]
    public async Task EnsureDirectoryAsync_WhenAlreadyExists_SkipsCreateAndThrowsNothing()
    {
        // 重复上传同一文件夹树的常态:目录已存在。必须先探测后返回,绝不调用 CreateDirectory
        // ——否则底层库会对已存在目录抛 SftpException,上千文件的文件夹重传即刷出上百条异常。
        string remotePath = "/home/user/existing";
        _sftpClient.ExistsAsync(remotePath, Arg.Any<CancellationToken>()).Returns(true);

        await _sftpService.EnsureDirectoryAsync(_sessionId, remotePath);

        await _sftpClient.Received(1).ExistsAsync(remotePath, Arg.Any<CancellationToken>());
        await _sftpClient.DidNotReceive().CreateDirectoryAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [TestMethod]
    public async Task EnsureDirectoryAsync_WhenAbsent_CreatesWithoutProbingAgain()
    {
        // 目录缺失(默认 ExistsAsync 返回 false):探测一次后创建;创建必然成功,不产生异常。
        string remotePath = "/home/user/fresh";

        await _sftpService.EnsureDirectoryAsync(_sessionId, remotePath);

        await _sftpClient.Received(1).CreateDirectoryAsync(remotePath, Arg.Any<CancellationToken>());
    }

    [TestMethod]
    public async Task EnsureDirectoryAsync_CreateRacesButExistsAfter_TreatsAsSuccess()
    {
        // 探测=不存在,但创建时被并发抢先(抛 VelaSshClientException),随后目录已存在 → 幂等成功。
        string remotePath = "/home/user/raced";
        _sftpClient.ExistsAsync(remotePath, Arg.Any<CancellationToken>()).Returns(false, true);
        _sftpClient.CreateDirectoryAsync(remotePath, Arg.Any<CancellationToken>())
                   .ThrowsAsync(new VelaSshClientException("already exists"));

        // 不抛即为通过。
        await _sftpService.EnsureDirectoryAsync(_sessionId, remotePath);
    }

    [TestMethod]
    public async Task EnsureDirectoryAsync_CreateFailsAndStillAbsent_Rethrows()
    {
        // 创建失败(权限/父目录缺失),复查仍不存在 → 抛出原错,不吞掉真实失败。
        string remotePath = "/home/user/denied";
        _sftpClient.ExistsAsync(remotePath, Arg.Any<CancellationToken>()).Returns(false, false);
        _sftpClient.CreateDirectoryAsync(remotePath, Arg.Any<CancellationToken>())
                   .ThrowsAsync(new VelaSshClientException("permission denied"));

        await Assert.ThrowsExactlyAsync<VelaSshClientException>(
            () => _sftpService.EnsureDirectoryAsync(_sessionId, remotePath));
    }

    [TestMethod]
    public async Task DeleteAsync_WithDirectory_DeletesDirectory()
    {
        // Arrange
        string remotePath = "/home/user/mydir";
        SftpEntry mockDir = CreateMockSftpFile("mydir", remotePath, 0, true, "rwxr-xr-x");
        _sftpClient.GetEntryAsync(remotePath, Arg.Any<CancellationToken>()).Returns(mockDir);
        _sftpClient.ListDirectoryAsync(remotePath, Arg.Any<CancellationToken>())
                   .Returns(Task.FromResult<IEnumerable<SftpEntry>>([]));

        // Act
        await _sftpService.DeleteAsync(_sessionId, remotePath);

        // Assert
        await _sftpClient.Received(1).DeleteDirectoryAsync(remotePath, Arg.Any<CancellationToken>());
        await _sftpClient.DidNotReceive().DeleteFileAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [TestMethod]
    public async Task DeleteAsync_RecursivelyDeletesDirectoryContents_AndReportsProgress()
    {
        const string dir = "/home/user/proj";
        SftpEntry mockDir = CreateMockSftpFile("proj", dir, 0, true, "rwxr-xr-x");
        SftpEntry childFile = CreateMockSftpFile("a.txt", "/home/user/proj/a.txt", 10, false, "rw-r--r--");
        SftpEntry childSub = CreateMockSftpFile("sub", "/home/user/proj/sub", 0, true, "rwxr-xr-x");
        SftpEntry grandchild = CreateMockSftpFile("b.txt", "/home/user/proj/sub/b.txt", 20, false, "rw-r--r--");
        _sftpClient.GetEntryAsync(dir, Arg.Any<CancellationToken>()).Returns(mockDir); // stat → proj 是目录
        _sftpClient.GetEntryAsync(childSub.FullName, Arg.Any<CancellationToken>()).Returns(childSub); // 进子目录之前不跟随链接地再确认一次
        _sftpClient.ListDirectoryAsync(dir, Arg.Any<CancellationToken>())
                   .Returns(Task.FromResult<IEnumerable<SftpEntry>>([childFile, childSub]));
        _sftpClient.ListDirectoryAsync("/home/user/proj/sub", Arg.Any<CancellationToken>())
                   .Returns(Task.FromResult<IEnumerable<SftpEntry>>([grandchild]));
        var reports = new List<SftpDeleteProgress>();
        await _sftpService.DeleteAsync(_sessionId, dir,
            new SynchronousProgress<SftpDeleteProgress>(reports.Add));

        // Files removed, and each directory removed only after its contents.
        await _sftpClient.Received(1).DeleteFileAsync("/home/user/proj/a.txt", Arg.Any<CancellationToken>());
        await _sftpClient.Received(1).DeleteFileAsync("/home/user/proj/sub/b.txt", Arg.Any<CancellationToken>());
        await _sftpClient.Received(1).DeleteDirectoryAsync("/home/user/proj/sub", Arg.Any<CancellationToken>());
        await _sftpClient.Received(1).DeleteDirectoryAsync(dir, Arg.Any<CancellationToken>());
        Assert.HasCount(5, reports); // initial 0/total + 2 files + 2 directories
        Assert.AreEqual(0, reports[0].DeletedCount);
        Assert.AreEqual(4, reports[0].TotalCount);
        Assert.AreEqual(4, reports[^1].DeletedCount);
        Assert.AreEqual(4, reports[^1].TotalCount);
    }

    [TestMethod]
    public async Task DeleteAsync_WhenPathNotFound_ThrowsFileNotFoundException()
    {
        // Arrange
        string remotePath = "/home/user/nonexistent.txt";
        _sftpClient.ExistsAsync(remotePath, Arg.Any<CancellationToken>()).Returns(false);

        // Act & Assert
        await Assert.ThrowsExactlyAsync<FileNotFoundException>(() => _sftpService.DeleteAsync(_sessionId, remotePath));
    }

    [TestMethod]
    public async Task ListDirectoryAsync_OwnerAndGroup_AreNotBooleanStrings()
    {
        // Arrange
        SftpEntry mockFile = CreateMockSftpFile("file.txt", "/home/user/file.txt", 1024, false, "rw-r--r--")
            with
        { UserId = 1000, GroupId = 1000 };
        _sftpClient.ListDirectoryAsync("/home/user", Arg.Any<CancellationToken>())
                   .Returns(Task.FromResult<IEnumerable<SftpEntry>>([mockFile]));

        // Act
        List<RemoteFileInfo> result = await _sftpService.ListDirectoryAsync(_sessionId, "/home/user");

        // Assert
        Assert.AreEqual("1000", result[0].Owner);
        Assert.AreEqual("1000", result[0].Group);
        Assert.AreNotEqual("True", result[0].Owner);
        Assert.AreNotEqual("False", result[0].Owner);
        Assert.AreNotEqual("True", result[0].Group);
        Assert.AreNotEqual("False", result[0].Group);
    }

    [TestMethod]
    public async Task DisposeAsync_DisposesAllClients()
    {
        // Arrange — trigger client caching by calling any method
        SftpEntry mockFile = CreateMockSftpFile("file.txt", "/home/user/file.txt", 0, false, "rw-r--r--");
        _sftpClient.ListDirectoryAsync("/home/user", Arg.Any<CancellationToken>())
                   .Returns(Task.FromResult<IEnumerable<SftpEntry>>([mockFile]));
        await _sftpService.ListDirectoryAsync(_sessionId, "/home/user");

        // Act
        await _sftpService.DisposeAsync();

        // Assert
        await _sftpClient.Received(1).DisposeAsync();
    }

    [TestMethod]
    public async Task ListDirectoryAsync_WhenPermissionDenied_ThrowsException()
    {
        // Arrange
        _sftpClient.ListDirectoryAsync("/root/restricted", Arg.Any<CancellationToken>())
                   .Throws(new VelaSftpPermissionDeniedException("Permission denied"));

        // Act & Assert
        await Assert.ThrowsExactlyAsync<VelaSftpPermissionDeniedException>(() => _sftpService.ListDirectoryAsync(_sessionId, "/root/restricted"));
    }

    [TestMethod]
    public async Task UploadFileAsync_WithProgressCallback_FiresCorrectPercentages()
    {
        // Arrange
        string localPath = Path.GetTempFileName();
        byte[] testData = new byte[10000];
        await File.WriteAllBytesAsync(localPath, testData);
        string remotePath = "/home/user/upload.bin";
        var progressReports = new List<TransferProgress>();
        _sftpClient.UploadAsync(Arg.Any<Stream>(),
                       remotePath,
                       Arg.Do<Action<ulong>?>(callback =>
                       {
                           if (callback != null)
                           {
                               // Simulate progress at 25%, 50%, 75%, 100%
                               callback(2500);
                               callback(5000);
                               callback(7500);
                               callback(10000);
                           }
                       }),
                       Arg.Any<CancellationToken>())
                   .Returns(Task.CompletedTask);

        // Act
        await _sftpService.UploadFileAsync(_sessionId, localPath, remotePath,
            new SynchronousProgress<TransferProgress>(progressReports.Add));

        // Assert —— 进度上报按时间片节流(见 Core/Sftp/TransferProgressThrottle.cs),因此不保证
        // "每个底层回调都对应一次上报"。这里断言的是真正的契约:立刻有首帧、单调不回退、
        // 收尾必达 100%。
        Assert.IsGreaterThanOrEqualTo(1, progressReports.Count);
        Assert.AreEqual(100, progressReports[^1].Percentage);
        Assert.AreEqual(10000, progressReports[^1].BytesTransferred);
        Assert.AreSequenceEqual(
            [.. progressReports.Select(p => p.BytesTransferred).OrderBy(b => b)], [.. progressReports.Select(p => p.BytesTransferred)], "进度必须单调不回退");

        // Cleanup
        File.Delete(localPath);
    }

    /// <summary>
    /// 底层按分块回调,GB 级文件会产生几十万次;若 1:1 转成 IProgress 上报会灌爆 UI 调度器
    /// (表现为传输到 1GB 左右界面长时间卡死)。这里证明节流确实把洪流收敛掉了。
    /// </summary>
    [TestMethod]
    public async Task UploadFileAsync_FloodOfCallbacks_IsThrottledButStillReaches100()
    {
        string localPath = Path.GetTempFileName();
        const int total = 100_000;
        await File.WriteAllBytesAsync(localPath, new byte[total]);
        string remotePath = "/home/user/flood.bin";
        var progressReports = new List<TransferProgress>();
        _sftpClient.UploadAsync(Arg.Any<Stream>(),
                       remotePath,
                       Arg.Do<Action<ulong>?>(callback =>
                       {
                           for (ulong sent = 1; sent <= total; sent++)
                           {
                               callback?.Invoke(sent);
                           }
                       }),
                       Arg.Any<CancellationToken>())
                   .Returns(Task.CompletedTask);

        await _sftpService.UploadFileAsync(_sessionId, localPath, remotePath,
            new SynchronousProgress<TransferProgress>(progressReports.Add));

        // 十万次回调必须被压到极少数几次(首帧 + 每 100ms 一帧 + 收尾),绝不是十万次。
        Assert.IsLessThan(100, progressReports.Count,
            $"节流失效:{total} 次回调产生了 {progressReports.Count} 次上报");
        Assert.AreEqual(100, progressReports[^1].Percentage);

        File.Delete(localPath);
    }

    [TestMethod]
    [DataRow(false, "success")]
    [DataRow(true, "success")]
    [DataRow(false, "failure")]
    [DataRow(true, "failure")]
    [DataRow(false, "cancel")]
    [DataRow(true, "cancel")]
    public async Task Upload_LastAckIsVisibleBeforeClose_AndDoesNotHideCloseOutcome(bool streamed, string outcome)
    {
        string localPath = Path.GetTempFileName();
        await File.WriteAllBytesAsync(localPath, new byte[100]);
        await using var source = new MemoryStream(new byte[100]);
        using var cancellation = new CancellationTokenSource();
        var closing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reports = new List<TransferProgress>();
        const string remotePath = "/home/user/close.bin";
        _sftpClient.UploadAsync(Arg.Any<Stream>(), remotePath, Arg.Any<Action<ulong>?>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                Action<ulong> callback = call.Arg<Action<ulong>>();
                callback(99);
                callback(100); // 同一时间片内最后的 ACK,然后还要等待 fsync / CLOSE。
                return closing.Task;
            });
        try
        {
            var progress = new SynchronousProgress<TransferProgress>(reports.Add);
            Task upload = streamed
                ? _sftpService.UploadStreamAsync(_sessionId, source, remotePath, 100, progress: progress, cancellationToken: cancellation.Token)
                : _sftpService.UploadFileAsync(_sessionId, localPath, remotePath, progress, cancellationToken: cancellation.Token);

            Assert.IsFalse(upload.IsCompleted, "字节确认不能提前完成上传任务。");
            Assert.AreEqual(100L, reports[^1].BytesTransferred, "最后 ACK 不得被 100ms 节流吞掉。");
            Assert.AreEqual(100, reports[^1].Percentage);
            int beforeClose = reports.Count;

            switch (outcome)
            {
                case "failure":
                    closing.SetException(new VelaSshClientException("close failed"));
                    await Assert.ThrowsExactlyAsync<VelaSshClientException>(() => upload);
                    Assert.HasCount(beforeClose, reports, "失败不得追加任务成功的收尾帧。");
                    break;
                case "cancel":
                    cancellation.Cancel();
                    closing.SetCanceled(cancellation.Token);
                    await Assert.ThrowsExactlyAsync<TaskCanceledException>(() => upload);
                    Assert.HasCount(beforeClose, reports);
                    break;
                default:
                    closing.SetResult();
                    await upload;
                    Assert.AreEqual(100, reports[^1].Percentage);
                    break;
            }
            Assert.IsTrue(source.CanRead, "流上传不拥有调用方的源流。");
        }
        finally
        {
            closing.TrySetResult();
            File.Delete(localPath);
        }
    }

    /// <summary>
    /// 满格只报一次:确认阶段可能报两遍满格(最后一次写回调与 Flush 的定时采样),成功收尾也不再补一帧。
    /// 插件桥把满格当「完成」,重复的满格就是重复的完成。
    /// </summary>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Upload_FullProgressIsReportedOnce(bool streamed)
    {
        string localPath = Path.GetTempFileName();
        await File.WriteAllBytesAsync(localPath, new byte[100]);
        await using var source = new MemoryStream(new byte[100]);
        var reports = new List<TransferProgress>();
        const string remotePath = "/home/user/once.bin";
        _sftpClient.UploadAsync(Arg.Any<Stream>(), remotePath, Arg.Any<Action<ulong>?>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                Action<ulong> callback = call.Arg<Action<ulong>>();
                callback(60);
                callback(100);
                callback(100);
                return Task.CompletedTask;
            });
        try
        {
            var progress = new SynchronousProgress<TransferProgress>(reports.Add);
            await (streamed
                ? _sftpService.UploadStreamAsync(_sessionId, source, remotePath, 100, progress: progress)
                : _sftpService.UploadFileAsync(_sessionId, localPath, remotePath, progress));

            Assert.AreEqual(1, reports.Count(static p => p.BytesTransferred == 100));
            Assert.AreEqual(100, reports[^1].Percentage);
        }
        finally
        {
            File.Delete(localPath);
        }
    }

    [TestMethod]
    public async Task Upload_FailureBeforeLastAck_DoesNotInvent100Percent()
    {
        await using var source = new MemoryStream(new byte[100]);
        var reports = new List<TransferProgress>();
        _sftpClient.UploadAsync(Arg.Any<Stream>(), "/partial.bin", Arg.Any<Action<ulong>?>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                call.Arg<Action<ulong>>()(60);
                return Task.FromException(new VelaSftpTransferInterruptedException("interrupted", 60));
            });

        await Assert.ThrowsExactlyAsync<VelaSftpTransferInterruptedException>(() =>
            _sftpService.UploadStreamAsync(_sessionId, source, "/partial.bin", 100,
                progress: new SynchronousProgress<TransferProgress>(reports.Add)));

        Assert.AreEqual(60L, reports[^1].BytesTransferred);
        Assert.DoesNotContain(p => p.Percentage == 100, reports);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Upload_ResumeSpeedUsesTheVerifiedOffset(bool streamed)
    {
        byte[] content = CreatePattern(200_000);
        string localPath = Path.GetTempFileName();
        await File.WriteAllBytesAsync(localPath, content);
        await using var source = new MemoryStream(content, false);
        const long remoteLength = 120_000;
        const string remotePath = "/home/user/speed.bin";
        var reports = new List<TransferProgress>();
        _sftpClient.GetFileSizeAsync(remotePath, Arg.Any<CancellationToken>()).Returns(remoteLength);
        _sftpClient.OpenAsync(remotePath, FileMode.Open, FileAccess.Read, Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult<Stream>(new MemoryStream(content[..(int)remoteLength], false)));
        _sftpClient.UploadAsync(Arg.Any<Stream>(), remotePath, remoteLength, Arg.Any<Action<ulong>?>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                await Task.Delay(150);
                call.Arg<Action<ulong>>()(remoteLength);
            });
        try
        {
            var progress = new SynchronousProgress<TransferProgress>(reports.Add);
            if (streamed)
            {
                await _sftpService.UploadStreamAsync(_sessionId, source, remotePath, content.Length,
                    progress: progress, resumeOffset: 50_000);
            }
            else
            {
                await _sftpService.UploadFileAsync(_sessionId, localPath, remotePath, progress, resumeOffset: 50_000);
            }
            Assert.AreEqual(remoteLength, reports[0].BytesTransferred);
            Assert.AreEqual(60, reports[0].Percentage);
            Assert.AreEqual(0d, reports[0].SpeedBytesPerSecond, "必须扣除核实后的 120000,而非调用方过时的 50000。");
        }
        finally
        {
            File.Delete(localPath);
        }
    }

    [TestMethod]
    public async Task GetFileInfoAsync_ReturnsFileInformation()
    {
        // Arrange
        string remotePath = "/home/user/info.txt";
        SftpEntry mockFile = CreateMockSftpFile("info.txt", remotePath, 4096, false, "rw-r--r--");
        _sftpClient.GetEntryAsync(remotePath, Arg.Any<CancellationToken>()).Returns(mockFile);

        // Act
        RemoteFileInfo result = await _sftpService.GetFileInfoAsync(_sessionId, remotePath);

        // Assert
        Assert.IsNotNull(result);
        Assert.AreEqual("info.txt", result.Name);
        Assert.AreEqual(remotePath, result.FullPath);
        Assert.AreEqual(4096L, result.Size);
        Assert.IsFalse(result.IsDirectory);

        // 单个文件的 stat 绝不能退化成列举整个父目录 —— 父目录上万条时那是灾难性的。
        await _sftpClient.DidNotReceive().ListDirectoryAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    /// <summary>路径不存在时 stat 返回 null,应翻译成 FileNotFoundException(契约不变)。</summary>
    [TestMethod]
    public async Task GetFileInfoAsync_WhenEntryMissing_ThrowsFileNotFound()
    {
        _sftpClient.GetEntryAsync("/home/user/gone.txt", Arg.Any<CancellationToken>())
                   .Returns((SftpEntry?)null);

        await Assert.ThrowsExactlyAsync<FileNotFoundException>(
            () => _sftpService.GetFileInfoAsync(_sessionId, "/home/user/gone.txt"));
    }

    // —— 属主/属组的数字 id → 名称翻译(见 RemoteIdentityResolver)————————————

    /// <summary>查表命令的返回样本:passwd 段 + 分隔标记 + group 段。</summary>
    private const string IdentityLookupOutput = """
                                                root:x:0:0:root:/root:/bin/bash
                                                deploy:x:1000:1000:Deploy User:/home/deploy:/bin/bash
                                                ###VELA-GROUPS###
                                                root:x:0:
                                                www-data:x:33:
                                                """;

    [TestMethod]
    public async Task ListDirectoryAsync_TranslatesNumericIdsUsingRemotePasswdDatabase()
    {
        GivenIdentityLookupReturns(IdentityLookupOutput);
        _sftpClient.ListDirectoryAsync("/srv", Arg.Any<CancellationToken>())
                   .Returns(Task.FromResult<IEnumerable<SftpEntry>>(
                       [CreateMockSftpFile("app.log", "/srv/app.log", 10, false, "rw-r--r--") with { UserId = 1000, GroupId = 33 }]));

        List<RemoteFileInfo> result = await _sftpService.ListDirectoryAsync(_sessionId, "/srv");

        Assert.AreEqual("deploy", result[0].Owner);
        Assert.AreEqual("www-data", result[0].Group);
    }

    [TestMethod]
    public async Task ListDirectoryAsync_IdWithoutPasswdEntry_FallsBackToNumericId()
    {
        GivenIdentityLookupReturns(IdentityLookupOutput);
        _sftpClient.ListDirectoryAsync("/srv", Arg.Any<CancellationToken>())
                   .Returns(Task.FromResult<IEnumerable<SftpEntry>>(
                       [CreateMockSftpFile("orphan", "/srv/orphan", 10, false, "rw-r--r--") with { UserId = 4242, GroupId = 4242 }]));

        List<RemoteFileInfo> result = await _sftpService.ListDirectoryAsync(_sessionId, "/srv");

        Assert.AreEqual("4242", result[0].Owner);
        Assert.AreEqual("4242", result[0].Group);
    }

    /// <summary>仅 SFTP、没有 exec 通道的主机:查表整个失败,不该拖垮列目录。</summary>
    [TestMethod]
    public async Task ListDirectoryAsync_WhenIdentityLookupFails_StillListsWithNumericIds()
    {
        ISshClientWrapper sshClient = Substitute.For<ISshClientWrapper>();
        sshClient.RunCommandAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                 .ThrowsAsync(new InvalidOperationException("no exec channel"));
        _connectionService.GetClient(_sessionId).Returns(sshClient);
        _sftpClient.ListDirectoryAsync("/srv", Arg.Any<CancellationToken>())
                   .Returns(Task.FromResult<IEnumerable<SftpEntry>>(
                       [CreateMockSftpFile("app.log", "/srv/app.log", 10, false, "rw-r--r--") with { UserId = 1000, GroupId = 33 }]));

        List<RemoteFileInfo> result = await _sftpService.ListDirectoryAsync(_sessionId, "/srv");

        Assert.HasCount(1, result);
        Assert.AreEqual("1000", result[0].Owner);
        Assert.AreEqual("33", result[0].Group);
    }

    /// <summary>
    /// 只开了 SFTP 的账号(没有 exec,查不了 passwd):表里没有的 id 经 SFTP 扩展(users-groups-by-id)补上 ——
    /// 属主一栏原来只能显示数字。每个 id 每个会话只问一次,服务端也不认识的同样不再问。
    /// </summary>
    [TestMethod]
    public async Task ListDirectoryAsync_SftpOnlyAccount_ResolvesNamesThroughTheSftpExtension()
    {
        ISshClientWrapper sshClient = Substitute.For<ISshClientWrapper>();
        sshClient.RunCommandAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                 .ThrowsAsync(new InvalidOperationException("no exec channel"));
        _connectionService.GetClient(_sessionId).Returns(sshClient);
        _sftpClient.SupportsIdLookup.Returns(true);
        _sftpClient.LookupNamesAsync(Arg.Any<IReadOnlyList<int>>(), Arg.Any<IReadOnlyList<int>>(), Arg.Any<CancellationToken>())
                   .Returns(call =>
                   {
                       IReadOnlyList<int> uids = call.ArgAt<IReadOnlyList<int>>(0);
                       IReadOnlyList<int> gids = call.ArgAt<IReadOnlyList<int>>(1);
                       return Task.FromResult<(IReadOnlyList<string?>, IReadOnlyList<string?>)>((
                           [.. uids.Select(u => u == 1000 ? "deploy" : null)],
                           [.. gids.Select(g => g == 33 ? "www-data" : null)]));
                   });
        _sftpClient.ListDirectoryAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                   .Returns(Task.FromResult<IEnumerable<SftpEntry>>(
                   [
                       CreateMockSftpFile("app.log", "/srv/app.log", 10, false, "rw-r--r--") with { UserId = 1000, GroupId = 33 },
                       CreateMockSftpFile("orphan", "/srv/orphan", 10, false, "rw-r--r--") with { UserId = 4242, GroupId = 33 },
                   ]));

        List<RemoteFileInfo> first = await _sftpService.ListDirectoryAsync(_sessionId, "/srv");
        List<RemoteFileInfo> second = await _sftpService.ListDirectoryAsync(_sessionId, "/srv");

        Assert.AreEqual("deploy", first[0].Owner);
        Assert.AreEqual("www-data", first[0].Group);
        Assert.AreEqual("4242", first[1].Owner, "服务端也不认识的回退数字");
        Assert.AreEqual("deploy", second[0].Owner);
        await _sftpClient.Received(1).LookupNamesAsync(
            Arg.Any<IReadOnlyList<int>>(), Arg.Any<IReadOnlyList<int>>(), Arg.Any<CancellationToken>());
    }

    /// <summary>整表只查一次:切目录不该每次都往返一条 getent。</summary>
    [TestMethod]
    public async Task ListDirectoryAsync_QueriesIdentityDatabaseOncePerSession()
    {
        ISshClientWrapper sshClient = GivenIdentityLookupReturns(IdentityLookupOutput);
        _sftpClient.ListDirectoryAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                   .Returns(Task.FromResult<IEnumerable<SftpEntry>>([]));

        await _sftpService.ListDirectoryAsync(_sessionId, "/a");
        await _sftpService.ListDirectoryAsync(_sessionId, "/b");
        await _sftpService.ListDirectoryAsync(_sessionId, "/c");

        await sshClient.Received(1).RunCommandAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    /// <summary>会话关闭要丢弃查表缓存,重连后才会按新主机重新查。</summary>
    [TestMethod]
    public async Task CloseSessionAsync_DiscardsIdentityCache()
    {
        ISshClientWrapper sshClient = GivenIdentityLookupReturns(IdentityLookupOutput);
        _sftpClient.ListDirectoryAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                   .Returns(Task.FromResult<IEnumerable<SftpEntry>>([]));

        await _sftpService.ListDirectoryAsync(_sessionId, "/a");
        await _sftpService.CloseSessionAsync(_sessionId);
        await _sftpService.ListDirectoryAsync(_sessionId, "/a");

        await sshClient.Received(2).RunCommandAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// 分隔标记以 '#' 开头,在命令里必须带引号:裸写时 shell 会把它当注释,连同其后的
    /// group 查询一起吞掉,于是只有 passwd 段跑出来 —— 属主显示名称而属组回退数字。
    /// 其余查表测试都直接喂假输出,测不到命令本身,故在此守住。
    /// </summary>
    [TestMethod]
    public async Task ListDirectoryAsync_IdentityLookupCommand_QuotesSectionSeparator()
    {
        ISshClientWrapper sshClient = GivenIdentityLookupReturns(IdentityLookupOutput);
        _sftpClient.ListDirectoryAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                   .Returns(Task.FromResult<IEnumerable<SftpEntry>>([]));

        await _sftpService.ListDirectoryAsync(_sessionId, "/srv");

        string command = (string)sshClient.ReceivedCalls()
                                          .Single(c => c.GetMethodInfo().Name == nameof(ISshClientWrapper.RunCommandAsync))
                                          .GetArguments()[0]!;
        Assert.Contains("echo '###VELA-GROUPS###'", command);
        Assert.IsFalse(command.Contains("echo ###", StringComparison.Ordinal),
                       "分隔标记未加引号,group 段会被 shell 当注释吞掉。");
    }

    // —— chown:名称 → 数字 id(与上面的显示翻译共用同一张表)————————————————

    [TestMethod]
    public async Task SetOwnerAsync_TranslatesNamesUsingRemotePasswdDatabase()
    {
        GivenIdentityLookupReturns(IdentityLookupOutput);

        await _sftpService.SetOwnerAsync(_sessionId, "/srv/app.log", "deploy", "www-data");

        await _sftpClient.Received(1).ChangeOwnerAsync("/srv/app.log", 1000, 33, Arg.Any<CancellationToken>());
    }

    /// <summary>只改一项:另一项传 null,由客户端取回当前值一并写回(uid/gid 共用一个标志位)。</summary>
    [TestMethod]
    public async Task SetOwnerAsync_OnlyGroup_LeavesOwnerToTheClient()
    {
        GivenIdentityLookupReturns(IdentityLookupOutput);

        await _sftpService.SetOwnerAsync(_sessionId, "/srv/app.log", null, "www-data");

        await _sftpClient.Received(1).ChangeOwnerAsync("/srv/app.log", null, 33, Arg.Any<CancellationToken>());
    }

    /// <summary>纯 SFTP 账号查不到表:数字 id 照样能用。</summary>
    [TestMethod]
    public async Task SetOwnerAsync_WithoutIdentityDatabase_AcceptsNumericIds()
    {
        await _sftpService.SetOwnerAsync(_sessionId, "/srv/app.log", " 1001 ", "4294967294");

        await _sftpClient.Received(1).ChangeOwnerAsync("/srv/app.log", 1001, unchecked((int)4294967294u), Arg.Any<CancellationToken>());
    }

    /// <summary>与 chown 命令同一个顺序:名字就叫「42」的用户优先于 UID 42。</summary>
    [TestMethod]
    public async Task SetOwnerAsync_NameThatLooksNumeric_PrefersTheName()
    {
        GivenIdentityLookupReturns("""
                                   42:x:1234:1234::/home/42:/bin/sh
                                   ###VELA-GROUPS###
                                   """);

        await _sftpService.SetOwnerAsync(_sessionId, "/srv/app.log", "42", null);

        await _sftpClient.Received(1).ChangeOwnerAsync("/srv/app.log", 1234, null, Arg.Any<CancellationToken>());
    }

    /// <summary>同一个 UID 的别名(BSD 的 toor)显示时让位给首行,填进来时照样认。</summary>
    [TestMethod]
    public async Task SetOwnerAsync_AliasSharingAnId_Resolves()
    {
        GivenIdentityLookupReturns("""
                                   root:x:0:0:root:/root:/bin/sh
                                   toor:x:0:0:Bourne-again Superuser:/root:/bin/sh
                                   ###VELA-GROUPS###
                                   wheel:x:0:root
                                   """);

        await _sftpService.SetOwnerAsync(_sessionId, "/srv/app.log", "toor", "wheel");

        await _sftpClient.Received(1).ChangeOwnerAsync("/srv/app.log", 0, 0, Arg.Any<CancellationToken>());
    }

    /// <summary>表里没有、也不是数字:当场报错,不往服务端发任何东西。</summary>
    [TestMethod]
    [DataRow("ghost", null)]
    [DataRow(null, "ghosts")]
    [DataRow("-1", null)]
    [DataRow("", null)]
    public async Task SetOwnerAsync_UnknownName_ThrowsWithoutTouchingTheServer(string? owner, string? group)
    {
        GivenIdentityLookupReturns(IdentityLookupOutput);

        ArgumentException ex = await Assert.ThrowsExactlyAsync<ArgumentException>(() => _sftpService.SetOwnerAsync(_sessionId, "/srv/app.log", owner, group));

        // 消息原样进错误条:不能拖着「(Parameter 'owner')」这种尾巴。
        Assert.IsNull(ex.ParamName);
        await _sftpClient.DidNotReceive().ChangeOwnerAsync(Arg.Any<string>(), Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<CancellationToken>());
    }

    [TestMethod]
    public async Task SetOwnerAsync_NothingToChange_IsANoOp()
    {
        await _sftpService.SetOwnerAsync(_sessionId, "/srv/app.log", null, null);

        await _sftpClient.DidNotReceive().ChangeOwnerAsync(Arg.Any<string>(), Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<CancellationToken>());
    }

    [TestMethod]
    public async Task GetOwnerChoicesAsync_ListsNamesFromIdentityDatabaseInOrdinalOrder()
    {
        GivenIdentityLookupReturns(IdentityLookupOutput);

        RemoteOwnerChoices? choices = await _sftpService.GetOwnerChoicesAsync(_sessionId);

        Assert.IsNotNull(choices);
        Assert.AreSequenceEqual(["deploy", "root"], choices.Users);
        Assert.AreSequenceEqual(["root", "www-data"], choices.Groups);
    }

    /// <summary>查不到表不等于改不了属主:给空名单(弹窗照样可填数字),而不是 null(只读)。</summary>
    [TestMethod]
    public async Task GetOwnerChoicesAsync_WithoutIdentityDatabase_ReturnsEmptyListsNotNull()
    {
        RemoteOwnerChoices? choices = await _sftpService.GetOwnerChoicesAsync(_sessionId);

        Assert.IsNotNull(choices);
        Assert.IsEmpty(choices.Users);
        Assert.IsEmpty(choices.Groups);
    }

    private ISshClientWrapper GivenIdentityLookupReturns(string output)
    {
        ISshClientWrapper sshClient = Substitute.For<ISshClientWrapper>();
        sshClient.RunCommandAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult(output));
        _connectionService.GetClient(_sessionId).Returns(sshClient);
        return sshClient;
    }

    // --- 符号链接 ---

    /// <summary>
    /// 删一个指向目录的链接,只删链接本身。
    /// 回归:旧实现 stat 跟随链接把它判成目录,再列举"链接下的"子项逐个删掉 —— 删光的是链接目标里的东西。
    /// </summary>
    [TestMethod]
    public async Task DeleteAsync_OnSymlinkToDirectory_RemovesOnlyTheLink()
    {
        const string linkPath = "/srv/app/current";
        SftpEntry link = CreateMockSftpFile("current", linkPath, 0, true, "rwxrwxrwx") with
        {
            IsSymbolicLink = true,
            LinkTarget = "/srv/app/releases/42"
        };
        _sftpClient.GetEntryAsync(linkPath, Arg.Any<CancellationToken>()).Returns(link);

        await _sftpService.DeleteAsync(_sessionId, linkPath);

        await _sftpClient.Received(1).DeleteFileAsync(linkPath, Arg.Any<CancellationToken>());
        await _sftpClient.DidNotReceive().ListDirectoryAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _sftpClient.DidNotReceive().DeleteDirectoryAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    /// <summary>递归删除遇到子目录里的链接:当叶子删掉,不进去(否则指向 / 的链接就是一场事故)。</summary>
    [TestMethod]
    public async Task DeleteAsync_Recursive_DoesNotDescendIntoNestedDirectoryLinks()
    {
        const string dir = "/home/user/proj";
        SftpEntry mockDir = CreateMockSftpFile("proj", dir, 0, true, "rwxr-xr-x");
        SftpEntry nestedLink = CreateMockSftpFile("shared", "/home/user/proj/shared", 0, true, "rwxrwxrwx") with
        {
            IsSymbolicLink = true,
            LinkTarget = "/"
        };
        _sftpClient.GetEntryAsync(dir, Arg.Any<CancellationToken>()).Returns(mockDir);
        _sftpClient.ListDirectoryAsync(dir, Arg.Any<CancellationToken>())
                   .Returns(Task.FromResult<IEnumerable<SftpEntry>>([nestedLink]));
        var reports = new List<SftpDeleteProgress>();

        await _sftpService.DeleteAsync(_sessionId, dir, new SynchronousProgress<SftpDeleteProgress>(reports.Add));

        await _sftpClient.DidNotReceive().ListDirectoryAsync(nestedLink.FullName, Arg.Any<CancellationToken>());
        await _sftpClient.Received(1).DeleteFileAsync(nestedLink.FullName, Arg.Any<CancellationToken>());
        await _sftpClient.Received(1).DeleteDirectoryAsync(dir, Arg.Any<CancellationToken>());
        Assert.AreEqual(2, reports[^1].TotalCount, "链接计 1 条、目录自身计 1 条,链接目标里的条目不能算进来。");
    }

    /// <summary>
    /// 列表说是真目录、不跟随链接的 stat 却说是链接:不进去,只删链接本身。
    /// </summary>
    /// <remarks>
    /// READDIR 的属性按 lstat 还是 stat 给,SFTP 草案没有规定。服务端给的是跟随之后的属性时,
    /// 指向目录的链接在列表里就是一个「真目录」—— 照着列表进去删,删掉的是链接目标里的东西。
    /// </remarks>
    [TestMethod]
    public async Task DeleteAsync_Recursive_ConfirmsWithLstatBeforeDescending()
    {
        const string dir = "/home/user/proj";
        SftpEntry mockDir = CreateMockSftpFile("proj", dir, 0, true, "rwxr-xr-x");
        // 列表里看起来是真目录(服务端给的是跟随之后的属性)……
        SftpEntry listedAsDirectory = CreateMockSftpFile("data", "/home/user/proj/data", 0, true, "rwxr-xr-x");
        // ……不跟随链接地 stat 一下,其实是指向别处的链接。
        SftpEntry actuallyLink = listedAsDirectory with { IsSymbolicLink = true, LinkTarget = "/var/important" };
        _sftpClient.GetEntryAsync(dir, Arg.Any<CancellationToken>()).Returns(mockDir);
        _sftpClient.GetEntryAsync(listedAsDirectory.FullName, Arg.Any<CancellationToken>()).Returns(actuallyLink);
        _sftpClient.ListDirectoryAsync(dir, Arg.Any<CancellationToken>())
                   .Returns(Task.FromResult<IEnumerable<SftpEntry>>([listedAsDirectory]));

        await _sftpService.DeleteAsync(_sessionId, dir);

        await _sftpClient.DidNotReceive().ListDirectoryAsync(listedAsDirectory.FullName, Arg.Any<CancellationToken>());
        await _sftpClient.Received(1).DeleteFileAsync(listedAsDirectory.FullName, Arg.Any<CancellationToken>());
        await _sftpClient.DidNotReceive().DeleteDirectoryAsync(listedAsDirectory.FullName, Arg.Any<CancellationToken>());
    }

    /// <summary>复制链接得到链接(cp -P 口径),不去下载目标内容。</summary>
    [TestMethod]
    public async Task CopyAsync_OnSymlink_RecreatesTheLinkInsteadOfCopyingTarget()
    {
        SftpEntry link = CreateMockSftpFile("current", "/srv/app/current", 0, true, "rwxrwxrwx") with
        {
            IsSymbolicLink = true,
            LinkTarget = "releases/42"
        };
        _sftpClient.GetEntryAsync(link.FullName, Arg.Any<CancellationToken>()).Returns(link);

        await _sftpService.CopyAsync(_sessionId, link.FullName, "/srv/app/current-copy");

        await _sftpClient.Received(1).CreateSymbolicLinkAsync("/srv/app/current-copy", "releases/42", Arg.Any<CancellationToken>());
        await _sftpClient.DidNotReceive().ListDirectoryAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _sftpClient.DidNotReceive().DownloadAsync(Arg.Any<string>(), Arg.Any<Stream>(), Arg.Any<Action<ulong>?>(), Arg.Any<CancellationToken>());
    }

    /// <summary>服务端支持 copy-data:在服务端内复制,不下载也不上传;保留时间戳时目标的修改时间设成源的。</summary>
    [TestMethod]
    public async Task CopyAsync_UsesTheServerSideCopy_WhenSupported()
    {
        var mtime = new DateTime(2025, 1, 2, 3, 4, 5, DateTimeKind.Local);
        SftpEntry file = CreateMockSftpFile("big.iso", "/data/big.iso", 4_000_000_000, false, "rw-r--r--") with { LastWriteTime = mtime };
        _sftpClient.GetEntryAsync(file.FullName, Arg.Any<CancellationToken>()).Returns(file);
        _sftpClient.SupportsServerCopy.Returns(true);
        var service = new SftpService(_connectionService, _ => _sftpClient);   // 无设置服务 = 默认保留时间戳

        await service.CopyAsync(_sessionId, file.FullName, "/data/big-copy.iso");

        await _sftpClient.Received(1).CopyOnServerAsync("/data/big.iso", "/data/big-copy.iso", Arg.Any<Action<ulong>?>(), Arg.Any<CancellationToken>());
        await _sftpClient.DidNotReceive().DownloadAsync(Arg.Any<string>(), Arg.Any<Stream>(), Arg.Any<Action<ulong>?>(), Arg.Any<CancellationToken>());
        await _sftpClient.DidNotReceive().UploadAsync(Arg.Any<Stream>(), Arg.Any<string>(), Arg.Any<Action<ulong>?>(), Arg.Any<CancellationToken>());
        await _sftpClient.Received(1).SetLastWriteTimeAsync("/data/big-copy.iso", new DateTimeOffset(mtime), Arg.Any<CancellationToken>());
    }

    [TestMethod]
    public async Task ListDirectoryAsync_CarriesSymlinkFlagTargetAndLPermissionPrefix()
    {
        SftpEntry link = CreateMockSftpFile("current", "/srv/app/current", 4096, true, "rwxr-xr-x") with
        {
            IsSymbolicLink = true,
            LinkTarget = "releases/42"
        };
        _sftpClient.ListDirectoryAsync("/srv/app", Arg.Any<CancellationToken>())
                   .Returns(Task.FromResult<IEnumerable<SftpEntry>>([link]));

        List<RemoteFileInfo> result = await _sftpService.ListDirectoryAsync(_sessionId, "/srv/app");

        RemoteFileInfo entry = result.Single();
        Assert.IsTrue(entry.IsSymbolicLink);
        Assert.IsTrue(entry.IsDirectory, "指向目录的链接要能直接进入。");
        Assert.AreEqual("releases/42", entry.LinkTarget);
        Assert.AreEqual("lrwxr-xr-x", entry.Permissions);
    }

    [TestMethod]
    public async Task CreateSymbolicLinkAsync_PassesLinkThenTargetToTheClient()
    {
        await _sftpService.CreateSymbolicLinkAsync(_sessionId, "/srv/app/current", "releases/43");

        await _sftpClient.Received(1).CreateSymbolicLinkAsync("/srv/app/current", "releases/43", Arg.Any<CancellationToken>());
    }

    private static SftpEntry CreateMockSftpFile(string name, string fullName, long length, bool isDirectory, string permissions)
    {
        // SftpEntry 是 Core 的中立不可变记录(不再是 SSH 库的接口),直接构造即可。
        return new()
        {
            Name = name,
            FullName = fullName,
            Length = length,
            IsDirectory = isDirectory,
            LastWriteTime = DateTime.UtcNow,
            OwnerCanRead = permissions.Length > 0 && permissions[0] == 'r',
            OwnerCanWrite = permissions.Length > 1 && permissions[1] == 'w',
            OwnerCanExecute = permissions.Length > 2 && permissions[2] == 'x',
            GroupCanRead = permissions.Length > 3 && permissions[3] == 'r',
            GroupCanWrite = permissions.Length > 4 && permissions[4] == 'w',
            GroupCanExecute = permissions.Length > 5 && permissions[5] == 'x',
            OthersCanRead = permissions.Length > 6 && permissions[6] == 'r',
            OthersCanWrite = permissions.Length > 7 && permissions[7] == 'w',
            OthersCanExecute = permissions.Length > 8 && permissions[8] == 'x'
        };
    }

    private sealed class SynchronousProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }

    // ---- 断点续传的起点核实(SftpService.ResolveUploadResumeAsync / ResolveDownloadResumeAsync)----
    //
    // 调用方给出的 resumeOffset 是更早之前探测出来的,从探测到真正开始写之间隔着冲突对话框
    // 和传输队列。这组测试锁定的行为是:以"此刻的实际长度"为准,并比对尾部确认那半截确实是
    // 同一个文件的前缀 —— 否则宁可整份重传/直接失败,也不能静默产出损坏文件。

    /// <summary>远端那半截确实是本地文件的前缀 → 按远端实际长度续传。</summary>
    [TestMethod]
    public async Task UploadFileAsync_ResumeWithMatchingPrefix_ResumesAtRemoteLength()
    {
        string localPath = Path.GetTempFileName();
        byte[] content = CreatePattern(200_000);
        await File.WriteAllBytesAsync(localPath, content);
        const long remoteLength = 120_000;
        const string remotePath = "/home/user/resume.bin";

        _sftpClient.GetFileSizeAsync(remotePath, Arg.Any<CancellationToken>()).Returns(remoteLength);
        _sftpClient.OpenAsync(remotePath, FileMode.Open, FileAccess.Read, Arg.Any<CancellationToken>())
                   .Returns(_ => Task.FromResult<Stream>(new MemoryStream(content[..(int)remoteLength], false)));

        // 调用方声称从 50_000 续 —— 应被"此刻远端实际长度"覆盖为 120_000。
        await _sftpService.UploadFileAsync(_sessionId, localPath, remotePath, null, 50_000);

        await _sftpClient.Received(1).UploadAsync(Arg.Any<Stream>(), remotePath, remoteLength,
            Arg.Any<Action<ulong>?>(), Arg.Any<CancellationToken>());
        File.Delete(localPath);
    }

    /// <summary>
    /// 上一次被打断时库交出了精确的续传点(DurableLength):续传从那里接着传,不按远端长度盲退一个在途窗口。
    /// </summary>
    [TestMethod]
    public async Task UploadFileAsync_ResumeAfterInterruption_UsesTheExactDurableLength()
    {
        string localPath = Path.GetTempFileName();
        byte[] content = CreatePattern(500_000);
        await File.WriteAllBytesAsync(localPath, content);
        const long remoteLength = 300_000;   // 已确认的最高偏移
        const long durable = 280_000;        // 从开头起连续确认落盘的字节数
        const string remotePath = "/home/user/exact.bin";

        _sftpClient.ResumeSafetyMargin.Returns(64 * 1024);
        _sftpClient.GetFileSizeAsync(remotePath, Arg.Any<CancellationToken>()).Returns(remoteLength);
        _sftpClient.OpenAsync(remotePath, FileMode.Open, FileAccess.Read, Arg.Any<CancellationToken>())
                   .Returns(_ => Task.FromResult<Stream>(new MemoryStream(content[..(int)remoteLength], false)));

        // 第一次上传中途断了:库报出精确的续传点。
        _sftpClient.UploadAsync(Arg.Any<Stream>(), remotePath, Arg.Any<Action<ulong>?>(), Arg.Any<CancellationToken>())
                   .Returns(Task.FromException(new VelaSftpTransferInterruptedException("中断", durable)));
        await Assert.ThrowsExactlyAsync<VelaSftpTransferInterruptedException>(
            () => _sftpService.UploadFileAsync(_sessionId, localPath, remotePath));

        // 续传:从 280_000 接着传,而不是 300_000 - 64 KiB。
        await _sftpService.UploadFileAsync(_sessionId, localPath, remotePath, null, resumeOffset: 1);

        await _sftpClient.Received(1).UploadAsync(Arg.Any<Stream>(), remotePath, durable,
            Arg.Any<Action<ulong>?>(), Arg.Any<CancellationToken>());
        File.Delete(localPath);
    }

    /// <summary>
    /// 底层库并发写多个缓冲区,中断后文件尾部可能留有空洞:文件长度只是"已确认的最高偏移"。
    /// 续传起点必须从长度处回退一整个在途写入窗口,否则尾部比对会落在已写入的那段上顺利通过,
    /// 却从一个带洞的位置接着传 —— 产出静默损坏的文件。
    /// </summary>
    [TestMethod]
    public async Task UploadFileAsync_Resume_BacksOffBySafetyMarginBeforeResuming()
    {
        string localPath = Path.GetTempFileName();
        byte[] content = CreatePattern(500_000);
        await File.WriteAllBytesAsync(localPath, content);
        const long remoteLength = 300_000;
        const long margin = 64 * 1024;
        const long expected = remoteLength - margin;
        const string remotePath = "/home/user/holes.bin";

        _sftpClient.ResumeSafetyMargin.Returns(margin);
        _sftpClient.GetFileSizeAsync(remotePath, Arg.Any<CancellationToken>()).Returns(remoteLength);
        _sftpClient.OpenAsync(remotePath, FileMode.Open, FileAccess.Read, Arg.Any<CancellationToken>())
                   .Returns(_ => Task.FromResult<Stream>(new MemoryStream(content[..(int)remoteLength], false)));

        await _sftpService.UploadFileAsync(_sessionId, localPath, remotePath, null, remoteLength);

        await _sftpClient.Received(1).UploadAsync(Arg.Any<Stream>(), remotePath, expected,
            Arg.Any<Action<ulong>?>(), Arg.Any<CancellationToken>());
        File.Delete(localPath);
    }

    /// <summary>已传部分比安全回退窗口还短 → 没有可信的续传起点,整份重传。</summary>
    [TestMethod]
    public async Task UploadFileAsync_ResumeShorterThanSafetyMargin_FallsBackToFullUpload()
    {
        string localPath = Path.GetTempFileName();
        await File.WriteAllBytesAsync(localPath, CreatePattern(500_000));
        const string remotePath = "/home/user/tiny-partial.bin";

        _sftpClient.ResumeSafetyMargin.Returns(64 * 1024L);
        _sftpClient.GetFileSizeAsync(remotePath, Arg.Any<CancellationToken>()).Returns(50_000L);

        await _sftpService.UploadFileAsync(_sessionId, localPath, remotePath, null, 50_000);

        await _sftpClient.Received(1).UploadAsync(Arg.Any<Stream>(), remotePath,
            Arg.Any<Action<ulong>?>(), Arg.Any<CancellationToken>());
        await _sftpClient.DidNotReceive().UploadAsync(Arg.Any<Stream>(), remotePath, Arg.Any<long>(),
            Arg.Any<Action<ulong>?>(), Arg.Any<CancellationToken>());
        File.Delete(localPath);
    }

    /// <summary>远端是同名的另一个文件(内容不同) → 必须报错,而不是接着往后追加。</summary>
    [TestMethod]
    public async Task UploadFileAsync_ResumeWithDifferentContent_ThrowsInsteadOfCorrupting()
    {
        string localPath = Path.GetTempFileName();
        await File.WriteAllBytesAsync(localPath, CreatePattern(200_000));
        byte[] impostor = CreatePattern(120_000, seed: 77);
        const string remotePath = "/home/user/impostor.bin";

        _sftpClient.GetFileSizeAsync(remotePath, Arg.Any<CancellationToken>()).Returns(impostor.LongLength);
        _sftpClient.OpenAsync(remotePath, FileMode.Open, FileAccess.Read, Arg.Any<CancellationToken>())
                   .Returns(_ => Task.FromResult<Stream>(new MemoryStream(impostor, false)));

        await Assert.ThrowsExactlyAsync<VelaSftpResumeMismatchException>(
            () => _sftpService.UploadFileAsync(_sessionId, localPath, remotePath, null, 120_000));

        // 关键:一个字节都不能写出去。
        await _sftpClient.DidNotReceive().UploadAsync(Arg.Any<Stream>(), remotePath, Arg.Any<long>(),
            Arg.Any<Action<ulong>?>(), Arg.Any<CancellationToken>());
        File.Delete(localPath);
    }

    /// <summary>远端已不短于本地(已传完/是别的更大文件) → 没有可续的半截,退化为整份重传。</summary>
    [TestMethod]
    public async Task UploadFileAsync_ResumeWhenRemoteNotShorter_FallsBackToFullUpload()
    {
        string localPath = Path.GetTempFileName();
        await File.WriteAllBytesAsync(localPath, CreatePattern(100_000));
        const string remotePath = "/home/user/complete.bin";
        _sftpClient.GetFileSizeAsync(remotePath, Arg.Any<CancellationToken>()).Returns(100_000L);

        await _sftpService.UploadFileAsync(_sessionId, localPath, remotePath, null, 40_000);

        // 走的是不带偏移量的全量重传重载。
        await _sftpClient.Received(1).UploadAsync(Arg.Any<Stream>(), remotePath,
            Arg.Any<Action<ulong>?>(), Arg.Any<CancellationToken>());
        await _sftpClient.DidNotReceive().UploadAsync(Arg.Any<Stream>(), remotePath, Arg.Any<long>(),
            Arg.Any<Action<ulong>?>(), Arg.Any<CancellationToken>());
        File.Delete(localPath);
    }

    /// <summary>本地残留的那半截不是远端文件的前缀 → 报错,不能把错位内容追加进去。</summary>
    [TestMethod]
    public async Task DownloadFileAsync_ResumeWithDifferentContent_ThrowsInsteadOfCorrupting()
    {
        string localPath = Path.Combine(Path.GetTempPath(), $"vela-resume-{Guid.NewGuid():N}.bin");
        await File.WriteAllBytesAsync(localPath, CreatePattern(120_000, seed: 77));
        byte[] remote = CreatePattern(200_000);
        const string remotePath = "/home/user/download.bin";

        _sftpClient.GetEntryAsync(remotePath, Arg.Any<CancellationToken>())
                   .Returns(new SftpEntry { Name = "download.bin", FullName = remotePath, Length = remote.LongLength });
        _sftpClient.OpenAsync(remotePath, FileMode.Open, FileAccess.Read, Arg.Any<CancellationToken>())
                   .Returns(_ => Task.FromResult<Stream>(new MemoryStream(remote, false)));

        await Assert.ThrowsExactlyAsync<VelaSftpResumeMismatchException>(
            () => _sftpService.DownloadFileAsync(_sessionId, remotePath, localPath, null, 120_000));

        File.Delete(localPath);
    }

    /// <summary>
    /// 续传校验依赖按偏移定位。若 <see cref="ISftpClientWrapper.OpenAsync" /> 的实现返回了不可 Seek 的流,
    /// 必须给出指明契约的清晰错误,而不是让底层库抛一个看不出所以然的裸 NotSupportedException。
    /// <para>
    /// 这条测试针对的是一个真实踩过的坑:上一版底层库的 FileOpenOptions 默认 Seekable/CacheLength 均为 false,
    /// 打开的流一 Seek 就抛 NotSupportedException;而当时的测试全用 MemoryStream(可 Seek),
    /// 于是测试全绿、真机必炸。
    /// </para>
    /// </summary>
    [TestMethod]
    public async Task UploadFileAsync_ResumeWithNonSeekableRemoteStream_ReportsTheContractViolation()
    {
        string localPath = Path.GetTempFileName();
        byte[] content = CreatePattern(500_000);
        await File.WriteAllBytesAsync(localPath, content);
        const long remoteLength = 300_000;
        const string remotePath = "/home/user/nonseekable.bin";

        _sftpClient.GetFileSizeAsync(remotePath, Arg.Any<CancellationToken>()).Returns(remoteLength);
        _sftpClient.OpenAsync(remotePath, FileMode.Open, FileAccess.Read, Arg.Any<CancellationToken>())
                   .Returns(_ => Task.FromResult<Stream>(
                       new NonSeekableStream(content[..(int)remoteLength])));

        InvalidOperationException ex = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => _sftpService.UploadFileAsync(_sessionId, localPath, remotePath, null, remoteLength));
        Assert.Contains("CanSeek", ex.Message);

        File.Delete(localPath);
    }

    [TestMethod]
    public async Task DownloadStreamWritesActualBytesLeavesCallerOpenAndUsesTransferChannel()
    {
        const string path = "/source";
        byte[] content = CreatePattern(300_000);
        _sftpClient.GetEntryAsync(path, Arg.Any<CancellationToken>()).Returns(new SftpEntry { Name = "source", FullName = path, Length = content.Length + 1 });
        _sftpClient.OpenAsync(path, FileMode.Open, FileAccess.Read, Arg.Any<CancellationToken>()).Returns(_ => Task.FromResult<Stream>(new MemoryStream(content, false)));
        using var destination = new MemoryStream();
        var reports = new List<TransferProgress>();
        await _sftpService.DownloadStreamAsync(_sessionId, path, destination, new SynchronousProgress<TransferProgress>(reports.Add));
        Assert.IsTrue(destination.CanWrite);
        CollectionAssert.AreEqual(content, destination.ToArray());
        Assert.AreEqual(content.LongLength, reports[^1].BytesTransferred, "Final count is actual bytes, not the advertised source length.");
        await _sftpClient.Received(1).OpenAsync(path, FileMode.Open, FileAccess.Read, Arg.Any<CancellationToken>());
    }

    [TestMethod]
    public async Task Download_ResumeSpeedExcludesTheVerifiedLocalPrefix()
    {
        const int prefix = 190_000;
        byte[] content = CreatePattern(prefix + 1_000);
        string localPath = Path.GetTempFileName();
        await File.WriteAllBytesAsync(localPath, content[..prefix]);
        const string remotePath = "/home/user/download-speed.bin";
        var reports = new List<TransferProgress>();
        _sftpClient.GetEntryAsync(remotePath, Arg.Any<CancellationToken>())
            .Returns(new SftpEntry { Name = "download-speed.bin", FullName = remotePath, Length = content.Length });
        int opened = 0;
        _sftpClient.OpenAsync(remotePath, FileMode.Open, FileAccess.Read, Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult<Stream>(++opened == 1
                ? new MemoryStream(content, false)
                : new DelayedReadStream(content)));
        try
        {
            await _sftpService.DownloadFileAsync(_sessionId, remotePath, localPath,
                new SynchronousProgress<TransferProgress>(reports.Add), resumeOffset: 50_000);

            Assert.AreEqual(2, opened, "核实和续写必须都走到,不能回落整份下载。");
            Assert.AreEqual(content.LongLength, reports[^1].BytesTransferred);
            Assert.AreEqual(100, reports[^1].Percentage);
            Assert.IsGreaterThan(0d, reports[0].SpeedBytesPerSecond);
            Assert.IsLessThan(20_000d, reports[0].SpeedBytesPerSecond, "只下载 1000 字节,不应把已有的 190000 字节计入速度。");
            CollectionAssert.AreEqual(content, await File.ReadAllBytesAsync(localPath));
        }
        finally
        {
            File.Delete(localPath);
        }
    }

    /// <summary>
    /// 续传下载同样受「带宽限制」管。续传这条路自己从远端流搬字节,不经 <c>client.DownloadAsync</c>,
    /// 原先漏包限速流:开着 1 MB/s 的限制,一续传就按链路全速跑。
    /// </summary>
    /// <remarks>
    /// 只断言下界:续传部分比 1 MiB 多一点,按 1 MiB/s 整形至少要等将近一秒;不限速时内存流一下就拷完。
    /// 机器忙只会让它更慢,不会让它误绿。
    /// </remarks>
    [TestMethod]
    public async Task DownloadFileAsync_ResumeHonoursTheDownloadBandwidthLimit()
    {
        ISettingsService settings = Substitute.For<ISettingsService>();
        settings.GetSettingsAsync().Returns(new AppSettings
        {
            Transfer = { PreserveTimestamps = false, BandwidthLimitEnabled = true, DownloadLimitMBps = 1 },
        });
        var service = new SftpService(_connectionService, _ => _sftpClient, settings);
        const int prefix = 100_000;
        byte[] content = CreatePattern(prefix + (1024 * 1024) + (64 * 1024));
        string localPath = Path.Combine(Path.GetTempPath(), $"vela-resume-limit-{Guid.NewGuid():N}.bin");
        await File.WriteAllBytesAsync(localPath, content[..prefix]);
        const string remotePath = "/home/user/limited.bin";
        _sftpClient.GetEntryAsync(remotePath, Arg.Any<CancellationToken>())
            .Returns(new SftpEntry { Name = "limited.bin", FullName = remotePath, Length = content.Length });
        _sftpClient.OpenAsync(remotePath, FileMode.Open, FileAccess.Read, Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult<Stream>(new MemoryStream(content, false)));
        try
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            await service.DownloadFileAsync(_sessionId, remotePath, localPath, null, resumeOffset: prefix);
            watch.Stop();

            CollectionAssert.AreEqual(content, await File.ReadAllBytesAsync(localPath));
            await _sftpClient.DidNotReceive().DownloadAsync(
                Arg.Any<string>(), Arg.Any<Stream>(), Arg.Any<Action<ulong>?>(), Arg.Any<CancellationToken>());
            Assert.IsGreaterThanOrEqualTo(
                600L, watch.ElapsedMilliseconds,
                $"续传了 {content.Length - prefix} 字节、限速 1 MiB/s,却只用了 {watch.ElapsedMilliseconds} ms —— 续传分支没有限速。");
        }
        finally
        {
            File.Delete(localPath);
        }
    }

    private sealed class DelayedReadStream(byte[] content) : MemoryStream(content, false)
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(150, cancellationToken);
            return await base.ReadAsync(buffer, cancellationToken);
        }

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            await ReadAsync(buffer.AsMemory(offset, count), cancellationToken);
    }

    /// <summary>模拟"打开选项没开 Seekable"的远端流:能读,但不能定位。</summary>
    private sealed class NonSeekableStream(byte[] content) : Stream
    {
        private readonly MemoryStream _inner = new(content, false);

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() { }
    }

    /// <summary>生成可复现的伪随机内容,使"尾部比对"能真正区分开不同文件。</summary>
    private static byte[] CreatePattern(int length, int seed = 42)
    {
        byte[] buffer = new byte[length];
        new Random(seed).NextBytes(buffer);
        return buffer;
    }
}
