using System.Text;
using VelaShell.Core.DirectorySync;
using VelaShell.Core.Ftp;
using VelaShell.Core.Models;
using VelaShell.Core.Sftp;
using VelaShell.Infrastructure.Ftp;

namespace VelaShell.Infrastructure.Tests.Ftp;

/// <summary>
/// <see cref="FtpFileService" /> 打通真实 FTP 协议的端到端验证:对着
/// <see cref="LoopbackFtpServer" />(进程内、纯明文)跑登录、列目录、传输与目录操作。
/// <para>
/// 这里验证的是 Mock 验不到的那一段:PASV/EPSV 数据连接、Unix LIST 输出到
/// <see cref="RemoteFileInfo" /> 的解析、以及连接池在并发下真的开了多条控制连接。
/// FTPS 与 TLS 会话复用需要真实服务器(见 docs/FTP客户端可行性调研.md 第五节风险 1),不在此列。
/// </para>
/// </summary>
[TestClass]
[TestCategory("Ftp")]
public class FtpFileServiceIntegrationTests
{
    private string _root = string.Empty;
    private LoopbackFtpServer _server = null!;
    private FtpFileService _service = null!;

    [TestInitialize]
    public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), $"vela-ftp-root-{Guid.NewGuid():N}");
        _server = new LoopbackFtpServer(_root);
        _service = new FtpFileService();
    }

    [TestCleanup]
    public void Cleanup()
    {
        _service.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _server.Dispose();
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, true);
            }
        }
        catch (IOException)
        {
            // 临时目录删不掉不影响断言。
        }
    }

    [TestMethod]
    public async Task OpenSession_ThenList_ParsesEntriesFromUnixListing()
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "readme.txt"), "hello ftp");
        Directory.CreateDirectory(Path.Combine(_root, "logs"));

        Guid sessionId = await OpenAsync();

        Assert.IsTrue(_service.OwnsSession(sessionId), "打开后该会话应归 FTP 后端所有(路由据此分派)。");
        Assert.AreEqual("/", await _service.GetWorkingDirectoryAsync(sessionId));

        List<RemoteFileInfo> entries = await _service.ListDirectoryAsync(sessionId, "/");

        Assert.HasCount(2, entries);
        RemoteFileInfo file = entries.Single(e => e.Name == "readme.txt");
        Assert.IsFalse(file.IsDirectory);
        Assert.AreEqual(9, file.Size);
        Assert.AreEqual("rw-r--r--", file.Permissions);   // 首位类型字符已剥掉
        Assert.AreEqual("deploy", file.Owner);            // FTP 给的是名字,不是 UID
        Assert.AreEqual("staff", file.Group);
        Assert.IsTrue(entries.Single(e => e.Name == "logs").IsDirectory);
    }

    [TestMethod]
    public async Task UploadThenDownload_RoundTripsContent()
    {
        Guid sessionId = await OpenAsync();
        string localSource = Path.Combine(_root, "..", $"vela-ftp-src-{Guid.NewGuid():N}.bin");
        string localTarget = $"{localSource}.out";
        byte[] payload = Encoding.UTF8.GetBytes(new string('x', 40_000) + "-tail");
        await File.WriteAllBytesAsync(localSource, payload);
        try
        {
            var uploadReports = new List<TransferProgress>();
            await _service.UploadFileAsync(sessionId, localSource, "/uploaded.bin",
                new Progress<TransferProgress>(uploadReports.Add));

            string remoteOnDisk = Path.Combine(_root, "uploaded.bin");
            Assert.IsTrue(File.Exists(remoteOnDisk), "上传后服务器根目录下应出现该文件。");
            byte[] uploaded = await File.ReadAllBytesAsync(remoteOnDisk);
            Assert.AreSequenceEqual(payload, uploaded);
            Assert.IsTrue(await _service.ExistsAsync(sessionId, "/uploaded.bin"));

            RemoteFileInfo info = await _service.GetFileInfoAsync(sessionId, "/uploaded.bin");
            Assert.AreEqual(payload.Length, info.Size);

            await _service.DownloadFileAsync(sessionId, "/uploaded.bin", localTarget);
            byte[] downloaded = await File.ReadAllBytesAsync(localTarget);
            Assert.AreSequenceEqual(payload, downloaded);
        }
        finally
        {
            File.Delete(localSource);
            File.Delete(localTarget);
        }
    }

    [TestMethod]
    public async Task OpenRead_StreamsSequentially_AndReleasesConnectionOnDispose()
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "log.txt"), "line-1\nline-2\n");
        Guid sessionId = await OpenAsync();

        string content;
        await using (Stream stream = await _service.OpenReadAsync(sessionId, "/log.txt"))
        {
            Assert.IsFalse(stream.CanSeek, "FTP 数据流不可 Seek —— 这正是不能实现 ISftpClientWrapper 的原因。");
            using var reader = new StreamReader(stream);
            content = await reader.ReadToEndAsync();
        }

        Assert.AreEqual("line-1\nline-2\n", content);
        // 流释放后连接必须已归还池子,否则后续操作会一直等在信号量上。
        List<RemoteFileInfo> entries = await _service.ListDirectoryAsync(sessionId, "/");
        Assert.HasCount(1, entries);
    }

    [TestMethod]
    public async Task DirectoryOperations_CreateRenameDelete()
    {
        Guid sessionId = await OpenAsync();

        await _service.CreateDirectoryAsync(sessionId, "/data");
        Assert.IsTrue(Directory.Exists(Path.Combine(_root, "data")));

        await _service.CreateFileAsync(sessionId, "/data/note.txt");
        Assert.IsTrue(File.Exists(Path.Combine(_root, "data", "note.txt")));

        await _service.RenameAsync(sessionId, "/data/note.txt", "/data/renamed.txt");
        Assert.IsTrue(File.Exists(Path.Combine(_root, "data", "renamed.txt")));

        var deleteReports = new List<SftpDeleteProgress>();
        await _service.DeleteAsync(sessionId, "/data", new Progress<SftpDeleteProgress>(deleteReports.Add));
        Assert.IsFalse(Directory.Exists(Path.Combine(_root, "data")), "递归删除应连同目录本身一起删掉。");
    }

    [TestMethod]
    public async Task MissingPath_TranslatesToPathNotFound()
    {
        Guid sessionId = await OpenAsync();

        await Assert.ThrowsExactlyAsync<VelaFtpPathNotFoundException>(
            () => _service.GetFileInfoAsync(sessionId, "/nope.txt"));
        Assert.IsFalse(await _service.ExistsAsync(sessionId, "/nope.txt"));
    }

    [TestMethod]
    public async Task ConcurrentTransfers_UseSeparatePooledConnections()
    {
        // FTP 一条控制连接同时只能跑一条命令:并发传输必须落到不同连接上,否则会互相打架。
        for (int i = 0; i < 4; i++)
        {
            await File.WriteAllTextAsync(Path.Combine(_root, $"f{i}.txt"), new string((char)('a' + i), 2048));
        }
        Guid sessionId = await OpenAsync(maxConnections: 3);
        string outDir = Path.Combine(Path.GetTempPath(), $"vela-ftp-out-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outDir);
        try
        {
            await Task.WhenAll(Enumerable.Range(0, 4).Select(i =>
                _service.DownloadFileAsync(sessionId, $"/f{i}.txt", Path.Combine(outDir, $"f{i}.txt"))));

            for (int i = 0; i < 4; i++)
            {
                Assert.AreEqual(2048, new FileInfo(Path.Combine(outDir, $"f{i}.txt")).Length);
            }
            Assert.IsGreaterThan(1, _server.AcceptedConnections, "并发传输应开出不止一条控制连接。");
            Assert.IsLessThanOrEqualTo(3, _server.AcceptedConnections, "连接数不得超过 MaxConnections。");
        }
        finally
        {
            Directory.Delete(outDir, true);
        }
    }

    [TestMethod]
    public async Task AnonymousLogin_SendsAnonymousCredentials()
    {
        Guid sessionId = await OpenAsync(anonymous: true);

        Assert.IsTrue(_service.OwnsSession(sessionId));
        Assert.AreEqual(FtpConnectionInfo.AnonymousUser, _server.LastUser);
        Assert.IsNotEmpty(_server.LastPassword ?? string.Empty);
    }

    [TestMethod]
    public async Task CloseSession_ReleasesOwnership()
    {
        Guid sessionId = await OpenAsync();
        Assert.IsTrue(_service.OwnsSession(sessionId));

        await _service.CloseSessionAsync(sessionId);

        Assert.IsFalse(_service.OwnsSession(sessionId));
        await Assert.ThrowsExactlyAsync<VelaFtpConnectionException>(
            () => _service.ListDirectoryAsync(sessionId, "/"));
    }

    /// <summary>
    /// 服务器只肯给一条控制连接(用户报的"仅支持单线程上传"那类)时,批量上传必须**全部成功**。
    /// </summary>
    /// <remarks>
    /// 修之前:第一个文件占住那唯一的连接,后面每个文件都去开第二条,被 421 顶回来 ——
    /// 用户看到的是"批量上传只成功一个,其余全失败",而且那个 <c>421</c> 被翻译成连接级异常,
    /// 顺带把整条会话标记成离线(树上的圆点变灰)。
    /// 修之后:池发现"已有活连接却开不出新连接",就把上限收成 1,后来的传输排队复用那一条。
    /// </remarks>
    [TestMethod]
    public async Task ConcurrentUploads_WhenServerAllowsOneConnection_AllSucceed()
    {
        _server.MaxConcurrentSessions = 1;
        string outDir = Path.Combine(Path.GetTempPath(), $"vela-ftp-src-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outDir);
        try
        {
            bool faulted = false;
            _service.SessionStateChanged += (_, change) => faulted |= change.State == FtpSessionState.Faulted;

            byte[] payload = [.. Enumerable.Range(0, 4096).Select(static i => (byte)(i % 251))];
            var sources = new List<string>();
            for (int i = 0; i < 5; i++)
            {
                string path = Path.Combine(outDir, $"batch{i}.bin");
                await File.WriteAllBytesAsync(path, payload, TestContext.CancellationToken);
                sources.Add(path);
            }
            Guid sessionId = await OpenAsync(maxConnections: 4);

            // 界面上的"最大并发传输数"就是这么发起的:所有文件同时开跑。
            await Task.WhenAll(sources.Select(source => _service.UploadFileAsync(
                sessionId,
                source,
                "/" + Path.GetFileName(source),
                progress: null,
                resumeOffset: 0,
                TestContext.CancellationToken)));

            foreach (string source in sources)
            {
                string landed = Path.Combine(_root, Path.GetFileName(source));
                Assert.IsTrue(File.Exists(landed), $"{Path.GetFileName(source)} 应已上传。");
                byte[] landedBytes = await File.ReadAllBytesAsync(landed, TestContext.CancellationToken);
                Assert.AreSequenceEqual(payload, landedBytes);
            }
            Assert.IsFalse(faulted, "退化成单连接是正常降级,不该把整条会话标记为离线。");
        }
        finally
        {
            Directory.Delete(outDir, true);
        }
    }

    /// <summary>
    /// 另一类服务器:控制连接随便开,但**一次只让传一个文件**(第二个 STOR 直接 450)。
    /// 池那条自适应看不到这种拒绝(连接开得出来),得由传输层收紧后重试。
    /// </summary>
    [TestMethod]
    public async Task ConcurrentUploads_WhenServerAllowsOneTransfer_AllSucceed()
    {
        _server.MaxConcurrentTransfers = 1;
        string outDir = Path.Combine(Path.GetTempPath(), $"vela-ftp-src-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outDir);
        try
        {
            byte[] payload = [.. Enumerable.Range(0, 8192).Select(static i => (byte)(i % 253))];
            var sources = new List<string>();
            for (int i = 0; i < 4; i++)
            {
                string path = Path.Combine(outDir, $"busy{i}.bin");
                await File.WriteAllBytesAsync(path, payload, TestContext.CancellationToken);
                sources.Add(path);
            }
            Guid sessionId = await OpenAsync(maxConnections: 4);

            await Task.WhenAll(sources.Select(source => _service.UploadFileAsync(
                sessionId,
                source,
                "/" + Path.GetFileName(source),
                progress: null,
                resumeOffset: 0,
                TestContext.CancellationToken)));

            foreach (string source in sources)
            {
                string landed = Path.Combine(_root, Path.GetFileName(source));
                Assert.IsTrue(File.Exists(landed), $"{Path.GetFileName(source)} 应已上传。");
                byte[] landedBytes = await File.ReadAllBytesAsync(landed, TestContext.CancellationToken);
                Assert.AreSequenceEqual(payload, landedBytes);
            }
        }
        finally
        {
            Directory.Delete(outDir, true);
        }
    }

    /// <summary>
    /// 目录同步在 FTP 上成立的前提:上传后用 MFMT 把远端时间对齐本地,再经 LIST 读回来,
    /// 比较器必须判「相同」。这一路上有三处会让它判成「不同」—— MFMT 按 UTC 发、LIST 按服务器
    /// 当地时间给且只到分钟、FluentFTP 不做时区换算 —— 单测用替身一处都测不到。
    /// </summary>
    [TestMethod]
    public async Task SetLastWriteTime_SurvivesTheListingRoundTrip_SoASyncCompareSeesNoDifference()
    {
        string localDir = Path.Combine(Path.GetTempPath(), $"vela-ftp-sync-{Guid.NewGuid():N}");
        Directory.CreateDirectory(localDir);
        try
        {
            string localFile = Path.Combine(localDir, "a.txt");
            await File.WriteAllTextAsync(localFile, "sync me", TestContext.CancellationToken);
            // 几小时前、秒不为 0:LIST 只给到分钟,这样才验得到「按分钟截断后相同」。
            DateTime stamp = DateTime.UtcNow.AddHours(-3);
            stamp = new DateTime(stamp.Year, stamp.Month, stamp.Day, stamp.Hour, stamp.Minute, 37, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(localFile, stamp);
            Guid sessionId = await OpenAsync();

            await _service.UploadFileAsync(sessionId, localFile, "/a.txt", null, 0, TestContext.CancellationToken);
            Assert.AreNotEqual(stamp, File.GetLastWriteTimeUtc(Path.Combine(_root, "a.txt")), "刚上传的文件时间应当是现在,否则后面的断言验不出 MFMT");
            await _service.SetLastWriteTimeAsync(sessionId, "/a.txt", stamp, TestContext.CancellationToken);

            Assert.AreEqual(stamp, File.GetLastWriteTimeUtc(Path.Combine(_root, "a.txt")), "服务器收到的应当是 UTC 时间");
            SyncTree remote = await DirectoryTreeScanner.ScanRemoteAsync(
                _service, sessionId, "/", SyncFileMask.Empty, inferPrecision: true, cancellationToken: TestContext.CancellationToken);
            SyncTree local = await DirectoryTreeScanner.ScanLocalAsync(localDir, SyncFileMask.Empty, cancellationToken: TestContext.CancellationToken);
            SyncItem listed = remote.Items.Single();
            Assert.AreEqual(SyncTimePrecision.Minute, listed.Precision);
            Assert.AreEqual(SyncComparisonState.Same,
                DirectoryComparer.Compare(local.Items, remote.Items, new SyncOptions()).Single().State);

            // 反证:本地再晚一分钟,就必须判成本地较新。
            File.SetLastWriteTimeUtc(localFile, stamp.AddMinutes(1));
            SyncTree touched = await DirectoryTreeScanner.ScanLocalAsync(localDir, SyncFileMask.Empty, cancellationToken: TestContext.CancellationToken);
            Assert.AreEqual(SyncComparisonState.LocalNewer,
                DirectoryComparer.Compare(touched.Items, remote.Items, new SyncOptions()).Single().State);
        }
        finally
        {
            Directory.Delete(localDir, true);
        }
    }

    /// <summary>
    /// 服务器与本机不在同一时区:LIST 给的是服务器墙上时间,不配偏移时整体错位、判成不同;
    /// 配上服务器时区,同一份文件就是「相同」。MLSD 给的是 UTC,配着同样的偏移也照样「相同」(不能再挪一遍)。
    /// </summary>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ServerInAnotherTimeZone_SyncCompareNeedsTheOffsetForListButNotForMlsd(bool mlsd)
    {
        // 服务器时区 = 本机偏移 + 3 小时(夹在 UTC−12…UTC+14 里):不管测试机在哪个时区,两边都一定不同。
        TimeSpan local = TimeZoneInfo.Local.GetUtcOffset(DateTime.UtcNow);
        TimeSpan server = local + TimeSpan.FromHours(3) > TimeSpan.FromHours(14) ? local - TimeSpan.FromHours(3) : local + TimeSpan.FromHours(3);
        _server.ListClockOffset = server;
        _server.SupportsMlsd = mlsd;
        string localDir = Path.Combine(Path.GetTempPath(), $"vela-ftp-tz-{Guid.NewGuid():N}");
        Directory.CreateDirectory(localDir);
        try
        {
            // 同一个时刻落在两边(几小时前、秒为 0:LIST 只到分钟)。
            DateTime stamp = DateTime.UtcNow.AddHours(-5);
            stamp = new DateTime(stamp.Year, stamp.Month, stamp.Day, stamp.Hour, stamp.Minute, 0, DateTimeKind.Utc);
            string localFile = Path.Combine(localDir, "a.txt");
            await File.WriteAllTextAsync(localFile, "same", TestContext.CancellationToken);
            File.SetLastWriteTimeUtc(localFile, stamp);
            string remoteFile = Path.Combine(_root, "a.txt");
            await File.WriteAllTextAsync(remoteFile, "same", TestContext.CancellationToken);
            File.SetLastWriteTimeUtc(remoteFile, stamp);
            Guid sessionId = await OpenAsync();
            SyncTree localTree = await DirectoryTreeScanner.ScanLocalAsync(localDir, SyncFileMask.Empty, cancellationToken: TestContext.CancellationToken);

            SyncTree withOffset = await DirectoryTreeScanner.ScanRemoteAsync(
                _service, sessionId, "/", SyncFileMask.Empty, inferPrecision: true, serverClockOffset: server,
                cancellationToken: TestContext.CancellationToken);
            Assert.AreEqual(stamp, withOffset.Items.Single().LastWriteTimeUtc);
            Assert.AreEqual(SyncComparisonState.Same,
                DirectoryComparer.Compare(localTree.Items, withOffset.Items, new SyncOptions()).Single().State);

            SyncTree withoutOffset = await DirectoryTreeScanner.ScanRemoteAsync(
                _service, sessionId, "/", SyncFileMask.Empty, inferPrecision: true,
                cancellationToken: TestContext.CancellationToken);
            SyncComparisonState legacy = DirectoryComparer.Compare(localTree.Items, withoutOffset.Items, new SyncOptions()).Single().State;
            if (mlsd)
            {
                Assert.AreEqual(SyncComparisonState.Same, legacy, "MLSD 给的是 UTC,本来就对得上");
            }
            else
            {
                Assert.AreNotEqual(SyncComparisonState.Same, legacy, "不配偏移时 LIST 的时间整体错开 —— 这正是要修的问题");
            }
        }
        finally
        {
            Directory.Delete(localDir, true);
        }
    }

    [TestMethod]
    public async Task SetLastWriteTime_OnAServerWithoutMfmt_ReportsNotSupported()
    {
        _server.SupportsMfmt = false;
        await File.WriteAllTextAsync(Path.Combine(_root, "a.txt"), "x", TestContext.CancellationToken);
        Guid sessionId = await OpenAsync();

        await Assert.ThrowsExactlyAsync<NotSupportedException>(() =>
            _service.SetLastWriteTimeAsync(sessionId, "/a.txt", DateTime.UtcNow, TestContext.CancellationToken));
    }

    /// <summary>服务器端 SHA-256:FluentFTP 经 FEAT 发现 XSHA256,摘要与本地算的一致;不存在的文件只让它自己为 null。</summary>
    [TestMethod]
    public async Task ComputeSha256_UsesTheServerSideCommand_AndMatchesTheLocalDigest()
    {
        byte[] payload = Encoding.UTF8.GetBytes("checksum me");
        await File.WriteAllBytesAsync(Path.Combine(_root, "a.txt"), payload, TestContext.CancellationToken);
        Guid sessionId = await OpenAsync();

        IReadOnlyDictionary<string, string?> digests =
            await _service.ComputeSha256Async(sessionId, ["/a.txt", "/missing.txt"], TestContext.CancellationToken);

        Assert.AreEqual(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(payload)), digests["/a.txt"]);
        Assert.IsNull(digests["/missing.txt"]);
    }

    [TestMethod]
    public async Task ComputeSha256_OnAServerWithoutHashCommands_ReportsNotSupported()
    {
        _server.SupportsSha256 = false;
        await File.WriteAllTextAsync(Path.Combine(_root, "a.txt"), "x", TestContext.CancellationToken);
        Guid sessionId = await OpenAsync();

        await Assert.ThrowsExactlyAsync<NotSupportedException>(() =>
            _service.ComputeSha256Async(sessionId, ["/a.txt"], TestContext.CancellationToken));
    }

    /// <summary>MSTest 注入的测试上下文(取消令牌)。</summary>
    public TestContext TestContext { get; set; } = null!;

    private Task<Guid> OpenAsync(int maxConnections = 2, bool anonymous = false) =>
        _service.OpenSessionAsync(new FtpConnectionInfo
        {
            Host = "127.0.0.1",
            Port = _server.Port,
            Username = anonymous ? string.Empty : "deploy",
            Password = anonymous ? null : "secret123",
            Settings = new FtpSettings
            {
                EncryptionMode = FtpEncryptionMode.None,
                Anonymous = anonymous,
                MaxConnections = maxConnections,
            },
        });
}
