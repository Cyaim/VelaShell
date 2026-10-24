using NSubstitute;
using VelaShell.Core.Models;
using VelaShell.Core.Sftp;
using VelaShell.Core.Ssh;
using VelaShell.Infrastructure.XServer;

namespace VelaShell.Infrastructure.Tests.XServer;

/// <summary>
/// 本机文件拖进经 SSH 转发来的 X 程序(F16):按 X 程序的连接名(<c>user@host:22</c>)找到连着的会话,
/// 经它的 SFTP 传到远端一个只有自己看得到的临时目录,交回远端路径。
/// </summary>
[TestClass]
[TestCategory("XServer")]
public sealed class XServerDropUploaderTests
{
    private string _local = "";

    [TestInitialize]
    public void Setup() => _local = Directory.CreateTempSubdirectory("vx-drop-").FullName;

    [TestCleanup]
    public void Cleanup() => Directory.Delete(_local, recursive: true);

    private static SshSession Session(string user, string host, int port, SessionStatus status) => new()
    {
        ConnectionInfo = new ConnectionInfo { Host = host, Port = port, Username = user, AuthMethod = AuthMethod.Password },
        Status = status,
    };

    [TestMethod]
    public async Task Upload_FindsTheSessionByLabel_MakesAPrivateDirectory_AndReturnsRemotePathsInOrder()
    {
        SshSession other = Session("joe", "other", 22, SessionStatus.Connected);
        SshSession box = Session("joe", "box", 2222, SessionStatus.Connected);
        ISshConnectionService connections = Substitute.For<ISshConnectionService>();
        connections.Sessions.Returns([other, box]);
        ISftpService sftp = Substitute.For<ISftpService>();
        SftpXServerDropUploader uploader = new(connections, sftp);

        string a = Path.Combine(_local, "a.txt"), nested = Path.Combine(_local, "dir"), again = Path.Combine(nested, "a.txt");
        File.WriteAllText(a, "a");
        Directory.CreateDirectory(Path.Combine(nested, "sub"));
        File.WriteAllText(again, "b");
        File.WriteAllText(Path.Combine(nested, "sub", "c.txt"), "c");

        Assert.IsTrue(uploader.CanUpload("joe@box:2222"));
        Assert.IsFalse(uploader.CanUpload("joe@box:22"), "端口不同就不是同一个会话");
        IReadOnlyList<string>? remote = await uploader.UploadAsync("joe@box:2222", [a, nested, again, Path.Combine(_local, "gone.txt")]);

        Assert.IsNotNull(remote);
        Assert.HasCount(3, remote, "已经不存在的跳过");
        string directory = remote[0][..remote[0].LastIndexOf('/')];
        Assert.StartsWith("/tmp/velashell-drop-", directory);
        Assert.AreSequenceEqual([$"{directory}/a.txt", $"{directory}/dir", $"{directory}/a (2).txt"], [.. remote], "同名的加序号");
        await sftp.Received(1).CreateDirectoryAsync(box.SessionId, directory, Arg.Any<CancellationToken>());
        await sftp.Received(1).SetPermissionsAsync(box.SessionId, directory, 700, Arg.Any<CancellationToken>());
        await sftp.Received(1).UploadFileAsync(box.SessionId, a, $"{directory}/a.txt", null, 0, Arg.Any<CancellationToken>());
        await sftp.Received(1).UploadFileAsync(box.SessionId, again, $"{directory}/dir/a.txt", null, 0, Arg.Any<CancellationToken>());
        await sftp.Received(1).UploadFileAsync(box.SessionId, Path.Combine(nested, "sub", "c.txt"), $"{directory}/dir/sub/c.txt", null, 0, Arg.Any<CancellationToken>());
        await sftp.DidNotReceive().CreateDirectoryAsync(other.SessionId, Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [TestMethod]
    public async Task Upload_WithoutAConnectedSession_ReturnsNull()
    {
        ISshConnectionService connections = Substitute.For<ISshConnectionService>();
        connections.Sessions.Returns([Session("joe", "box", 22, SessionStatus.Disconnected)]);
        ISftpService sftp = Substitute.For<ISftpService>();
        SftpXServerDropUploader uploader = new(connections, sftp);

        Assert.IsFalse(uploader.CanUpload("joe@box:22"), "会话断开了");
        Assert.IsNull(await uploader.UploadAsync("joe@box:22", [Path.Combine(_local, "x")]));
        await sftp.DidNotReceive().CreateDirectoryAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // ------------------------------------------------------------------ 远端临时目录的清理

    private static readonly DateTime T0 = new(2026, 10, 24, 10, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Earlier = T0.AddHours(-1);

    /// <summary>本机时钟:用例自己拨(批次的时刻要与会话的连接时刻比先后)。</summary>
    private sealed class ManualClock : TimeProvider
    {
        public DateTime Now { get; set; } = T0;

        public override DateTimeOffset GetUtcNow() => new(Now);
    }

    private static SshSession Connected(string host, DateTime connectedAt) => new()
    {
        ConnectionInfo = new ConnectionInfo { Host = host, Port = 22, Username = "joe", AuthMethod = AuthMethod.Password },
        Status = SessionStatus.Connected,
        ConnectedAt = connectedAt,
    };

    /// <summary>会话表可以在用例里增删(连接服务每次给一份快照)。</summary>
    private static ISshConnectionService Connections(List<SshSession> sessions)
    {
        ISshConnectionService connections = Substitute.For<ISshConnectionService>();
        connections.Sessions.Returns(_ => [.. sessions]);
        return connections;
    }

    /// <summary>记下每次新建的远端目录。</summary>
    private static List<string> RecordCreated(ISftpService sftp)
    {
        List<string> created = [];
        sftp.CreateDirectoryAsync(Arg.Any<Guid>(), Arg.Do<string>(created.Add), Arg.Any<CancellationToken>());
        return created;
    }

    /// <summary>经这个会话删过这个目录几次。</summary>
    private static int Deletes(ISftpService sftp, Guid session, string directory) =>
        sftp.ReceivedCalls().Count(c => c.GetMethodInfo().Name == nameof(ISftpService.DeleteAsync)
            && (Guid)c.GetArguments()[0]! == session && (string)c.GetArguments()[1]! == directory);

    private string LocalFile(string name)
    {
        string path = Path.Combine(_local, name);
        File.WriteAllText(path, name);
        return path;
    }

    [TestMethod]
    public void OnlyTheExactDirectoryNameItMakes_CountsAsADropDirectory()
    {
        string hex = Guid.NewGuid().ToString("N");
        Assert.IsTrue(SftpXServerDropUploader.IsDropDirectory("/tmp/velashell-drop-" + hex));
        foreach (string? path in (string?[])[
            null, "", "/", "/tmp", "/tmp/", "/tmp/velashell-drop-", "/tmp/velashell-drop-*",
            "/tmp/velashell-drop-" + hex[..31], "/tmp/velashell-drop-" + hex + "0", "/tmp/velashell-drop-" + hex.ToUpperInvariant(),
            "/tmp/velashell-drop-" + hex + "/", "/tmp/velashell-drop-" + hex + "/a.txt", "/tmp/velashell-drop-" + hex[..30] + "/.",
            "/var/tmp/velashell-drop-" + hex[..28], " /tmp/velashell-drop-" + hex[..31], "/tmp/velashell-drop-" + hex[..31] + "g",
            "/tmp/velashell-drop-../../../home/joe/x",
        ])
        {
            Assert.IsFalse(SftpXServerDropUploader.IsDropDirectory(path), $"「{path}」不是这里建的目录,不能删");
        }
    }

    [TestMethod]
    public async Task FailedUpload_RemovesItsDirectoryInTheBackground_AndStillReportsTheFailure()
    {
        SshSession box = Connected("box", Earlier);
        ISftpService sftp = Substitute.For<ISftpService>();
        List<string> created = RecordCreated(sftp);
        sftp.UploadFileAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IProgress<TransferProgress>?>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new IOException("broken pipe")));
        // 删也删不掉(会话恰好断了):不报错,原来的异常照样交给用户。
        sftp.DeleteAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<IProgress<SftpDeleteProgress>?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new IOException("channel closed")));
        SftpXServerDropUploader uploader = new(Connections([box]), sftp, new ManualClock());

        IOException error = await Assert.ThrowsAsync<IOException>(() => uploader.UploadAsync("joe@box:22", [LocalFile("a.txt")]));
        Assert.AreEqual("broken pipe", error.Message);
        await uploader.Sweeping;
        Assert.AreEqual(1, Deletes(sftp, box.SessionId, created.Single()), "程序没拿到路径,这个目录当场不要了");
    }

    [TestMethod]
    public async Task NothingLeftToUpload_RemovesTheEmptyDirectory()
    {
        SshSession box = Connected("box", Earlier);
        ISftpService sftp = Substitute.For<ISftpService>();
        List<string> created = RecordCreated(sftp);
        SftpXServerDropUploader uploader = new(Connections([box]), sftp, new ManualClock());

        Assert.IsEmpty((await uploader.UploadAsync("joe@box:22", [Path.Combine(_local, "gone.txt")]))!);
        await uploader.Sweeping;
        Assert.AreEqual(1, Deletes(sftp, box.SessionId, created.Single()), "一个文件都没传上:空目录当场不要了");
    }

    /// <summary>
    /// 拖放那一刻连着这台主机的会话还有一个连着,那一批就留着(拿到路径的程序可能就在它上面);全都断了之后,
    /// 下一次往那台主机拖放、或者又连上同一个连接名时删掉。新连上的会话不算「那一刻连着的」。
    /// </summary>
    [TestMethod]
    public async Task Batches_StayWhileASessionFromThatMomentIsConnected_AndGoOnceTheyAllEnded()
    {
        SshSession first = Connected("box", Earlier), elsewhere = Connected("other", Earlier);
        List<SshSession> sessions = [first, elsewhere];
        ISshConnectionService connections = Connections(sessions);
        ISftpService sftp = Substitute.For<ISftpService>();
        List<string> created = RecordCreated(sftp);
        ManualClock clock = new();
        SftpXServerDropUploader uploader = new(connections, sftp, clock);

        await uploader.UploadAsync("joe@box:22", [LocalFile("a.txt")]);
        await uploader.Sweeping;
        await uploader.UploadAsync("joe@other:22", [LocalFile("b.txt")]);
        await uploader.Sweeping;
        await uploader.UploadAsync("joe@box:22", [LocalFile("c.txt")]);
        await uploader.Sweeping;
        (string a, string b, string c) = (created[0], created[1], created[2]);
        await sftp.DidNotReceive().DeleteAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<IProgress<SftpDeleteProgress>?>(), Arg.Any<CancellationToken>());

        // 第一个会话断了,又连上一个:那两批没人用了,借新会话删掉;别的主机上的那批不动。
        first.Status = SessionStatus.Disconnected;
        SshSession second = Connected("box", T0.AddMinutes(1));
        sessions.Add(second);
        connections.SessionConnected += Raise.Event<Action<SshSession>>(second);
        await uploader.Sweeping;
        Assert.AreEqual((1, 1), (Deletes(sftp, second.SessionId, a), Deletes(sftp, second.SessionId, c)));
        Assert.AreEqual(0, Deletes(sftp, second.SessionId, b) + Deletes(sftp, elsewhere.SessionId, b), "别的主机上的会话还连着");

        // 新会话上拖的一批:它自己连着,留着;它断了之后下一次往那台主机拖放(经第三个会话)时删,刚拖进去的那批不删。
        clock.Now = T0.AddMinutes(2);
        await uploader.UploadAsync("joe@box:22", [LocalFile("d.txt")]);
        await uploader.Sweeping;
        string d = created[3];
        Assert.AreEqual(0, Deletes(sftp, second.SessionId, d));
        second.Status = SessionStatus.Disconnected;
        SshSession third = Connected("box", T0.AddMinutes(3));
        sessions.Add(third);
        clock.Now = T0.AddMinutes(4);
        await uploader.UploadAsync("joe@box:22", [LocalFile("e.txt")]);
        await uploader.Sweeping;
        Assert.AreEqual((1, 0), (Deletes(sftp, third.SessionId, d), Deletes(sftp, third.SessionId, created[4])));
        Assert.AreEqual(0, Deletes(sftp, third.SessionId, a), "删过的不再记着");
    }

    /// <summary>删不掉的(会话恰好又断了)留到下一次机会再删;三次都不行就不再记着,免得每次连上都白费一趟。</summary>
    [TestMethod]
    public async Task FailedDeletes_AreRetriedOnTheNextChance_UpToThreeTimes()
    {
        SshSession first = Connected("box", Earlier);
        List<SshSession> sessions = [first];
        ISshConnectionService connections = Connections(sessions);
        ISftpService sftp = Substitute.For<ISftpService>();
        List<string> created = RecordCreated(sftp);
        sftp.DeleteAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<IProgress<SftpDeleteProgress>?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new IOException("channel closed")));
        SftpXServerDropUploader uploader = new(connections, sftp, new ManualClock());
        await uploader.UploadAsync("joe@box:22", [LocalFile("a.txt")]);
        await uploader.Sweeping;
        first.Status = SessionStatus.Disconnected;

        int total = 0;
        for (int round = 1; round <= 5; round++)
        {
            SshSession next = Connected("box", T0.AddMinutes(round));
            sessions.Add(next);
            connections.SessionConnected += Raise.Event<Action<SshSession>>(next);
            await uploader.Sweeping;
            total += Deletes(sftp, next.SessionId, created[0]);
            next.Status = SessionStatus.Disconnected;
        }
        Assert.AreEqual(3, total);
    }

    /// <summary>
    /// 本进程不知道的遗留目录(崩溃、上一次运行、别的电脑留下的):往那台主机拖放时一并清 —— 只删名字正好对、真目录、属主是自己、
    /// 按远端时钟一天以前就没再动过的。
    /// </summary>
    [TestMethod]
    public async Task Leftovers_OnlyOwnOldRealDirectoriesWithTheExactNameAreRemoved()
    {
        SshSession box = Connected("box", Earlier);
        ISftpService sftp = Substitute.For<ISftpService>();
        DateTime remoteNow = new(2026, 10, 24, 12, 0, 0, DateTimeKind.Utc);   // 远端的钟与本机对不上也没关系:只拿远端自己的时间比
        sftp.GetFileInfoAsync(box.SessionId, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call => Entry(((string)call[1]).Split('/')[^1], "joe", remoteNow));
        string old = "velashell-drop-" + Guid.NewGuid().ToString("N"), young = "velashell-drop-" + Guid.NewGuid().ToString("N");
        string bobs = "velashell-drop-" + Guid.NewGuid().ToString("N"), link = "velashell-drop-" + Guid.NewGuid().ToString("N");
        string file = "velashell-drop-" + Guid.NewGuid().ToString("N"), upper = "velashell-drop-" + Guid.NewGuid().ToString("N").ToUpperInvariant();
        DateTime twoDaysAgo = remoteNow.AddDays(-2);
        sftp.ListDirectoryAsync(box.SessionId, "/tmp", Arg.Any<CancellationToken>()).Returns(
        [
            Entry(old, "joe", twoDaysAgo),
            Entry(young, "joe", remoteNow.AddHours(-3)),
            Entry(bobs, "bob", twoDaysAgo),
            Entry(link, "joe", twoDaysAgo, symbolicLink: true),
            Entry(file, "joe", twoDaysAgo, directory: false),
            Entry(upper, "joe", twoDaysAgo),
            Entry("velashell-drop-notours", "joe", twoDaysAgo),
            Entry(".X11-unix", "root", twoDaysAgo),
        ]);
        SftpXServerDropUploader uploader = new(Connections([box]), sftp, new ManualClock());

        await uploader.UploadAsync("joe@box:22", [LocalFile("a.txt")]);
        await uploader.Sweeping;

        string[] deleted = [.. sftp.ReceivedCalls().Where(c => c.GetMethodInfo().Name == nameof(ISftpService.DeleteAsync)).Select(c => (string)c.GetArguments()[1]!)];
        Assert.AreSequenceEqual(["/tmp/" + old], deleted);

        static RemoteFileInfo Entry(string name, string owner, DateTime modified, bool directory = true, bool symbolicLink = false) => new()
        {
            Name = name,
            FullPath = "/tmp/" + name,
            Size = 4096,
            Permissions = "drwx------",
            IsDirectory = directory,
            IsSymbolicLink = symbolicLink,
            LastModified = modified,
            Owner = owner,
            Group = owner,
        };
    }
}
