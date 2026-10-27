using System.IO.Compression;
using System.Security.Cryptography;
using VelaShell.Services;
using VelaShell.Services.Update;

namespace VelaShell.Tests.Services;

[TestClass]
public class UpdateServiceTests : IDisposable
{
    private readonly string _appDir;
    private readonly FakeUpdateSource _source = new();

    public UpdateServiceTests()
    {
        _appDir = Path.Combine(Path.GetTempPath(), $"velashell_update_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_appDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_appDir))
        {
            Directory.Delete(_appDir, true);
        }
        GC.SuppressFinalize(this);
    }

    private UpdateService CreateService(
        string currentVersion = "1.0.0",
        Func<Task<string>>? channelProvider = null,
        Action? shutdown = null,
        TimeSpan? stallTimeout = null) =>
        new(_source,
            channelProvider,
            applicationDirectory: _appDir,
            currentVersionOverride: currentVersion,
            shutdownForRestart: shutdown ?? (static () => { }))
        {
            DownloadStallTimeout = stallTimeout ?? UpdateService.DefaultDownloadStallTimeout
        };

    /// <summary>为当前平台构造一个指向 <paramref name="archiveBytes" /> 的清单。</summary>
    private static UpdateManifest CreateManifest(
        string version, byte[] archiveBytes, string? tag = null,
        string? assetName = null, string? sha256 = null, long? size = null)
    {
        string rid = UpdateManifest.CurrentRid()
            ?? throw new InvalidOperationException("Tests must run on a supported platform.");
        string sha = sha256 ?? Convert.ToHexStringLower(SHA256.HashData(archiveBytes));
        string name = assetName ?? $"VelaShell-{version}-{rid}.zip";
        return UpdateManifest.Parse($$"""
            {
              "version": "{{version}}",
              "tag": "{{tag ?? $"v{version}"}}",
              "assets": {
                "{{rid}}": { "name": {{System.Text.Json.JsonSerializer.Serialize(name)}}, "sha256": "{{sha}}", "size": {{size ?? archiveBytes.Length}} }
              }
            }
            """);
    }

    private static byte[] CreateZipBytes(params (string Path, string Content)[] entries)
    {
        using MemoryStream stream = new();
        using (ZipArchive zip = new(stream, ZipArchiveMode.Create, true))
        {
            foreach ((string path, string content) in entries)
            {
                using StreamWriter writer = new(zip.CreateEntry(path).Open());
                writer.Write(content);
            }
        }
        return stream.ToArray();
    }

    [TestMethod]
    [TestCategory("Update")]
    public void CurrentVersion_WithoutOverride_ReturnsAssemblyVersion()
    {
        var service = new UpdateService(_source, applicationDirectory: _appDir);
        Assert.IsFalse(string.IsNullOrEmpty(service.CurrentVersion));
    }

    [TestMethod]
    [TestCategory("Update")]
    public void AvailableVersion_Initially_IsNull() => Assert.IsNull(CreateService().AvailableVersion);

    [TestMethod]
    [TestCategory("Update")]
    public void CanSelfUpdate_WritableTempDir_IsFalseOnLinux() =>
        Assert.AreEqual(!OperatingSystem.IsLinux(), CreateService().CanSelfUpdate);

    [TestMethod]
    [TestCategory("Update")]
    public async Task CheckForUpdateAsync_NewerVersion_ReturnsTrueAndExposesVersion()
    {
        byte[] zip = CreateZipBytes(("app.txt", "new"));
        _source.Manifest = CreateManifest("2.0.0", zip);
        UpdateService service = CreateService("1.0.0");

        Assert.IsTrue(await service.CheckForUpdateAsync());
        Assert.AreEqual("2.0.0", service.AvailableVersion);
    }

    [TestMethod]
    [TestCategory("Update")]
    [DataRow("1.0.0")]
    [DataRow("0.9.0")]
    [DataRow("1.0.0-beta")]
    public async Task CheckForUpdateAsync_SameOrOlderVersion_ReturnsFalse(string remote)
    {
        _source.Manifest = CreateManifest(remote, CreateZipBytes(("a", "b")));
        UpdateService service = CreateService("1.0.0");

        Assert.IsFalse(await service.CheckForUpdateAsync());
        Assert.IsNull(service.AvailableVersion);
    }

    [TestMethod]
    [TestCategory("Update")]
    public async Task CheckForUpdateAsync_NoManifestOnSource_ReturnsFalse()
    {
        _source.Manifest = null;
        Assert.IsFalse(await CreateService().CheckForUpdateAsync());
    }

    [TestMethod]
    [TestCategory("Update")]
    public async Task CheckForUpdateAsync_NoAssetForPlatform_ReturnsFalse()
    {
        _source.Manifest = UpdateManifest.Parse("""
            { "version": "9.9.9", "tag": "v9.9.9",
              "assets": { "solaris-sparc": { "name": "n", "sha256": "s", "size": 1 } } }
            """);
        Assert.IsFalse(await CreateService().CheckForUpdateAsync());
    }

    [TestMethod]
    [TestCategory("Update")]
    public async Task CheckForUpdateAsync_SourceThrows_ReturnsFalse()
    {
        _source.ThrowOnManifest = true;
        Assert.IsFalse(await CreateService().CheckForUpdateAsync());
    }

    [TestMethod]
    [TestCategory("Update")]
    public async Task CheckForUpdateAsync_PreviewChannel_RequestsPreReleases()
    {
        _source.Manifest = null;
        await CreateService(channelProvider: static () => Task.FromResult("preview")).CheckForUpdateAsync();
        Assert.IsTrue(_source.LastIncludePreRelease);

        await CreateService(channelProvider: static () => Task.FromResult("stable")).CheckForUpdateAsync();
        Assert.IsFalse(_source.LastIncludePreRelease);
    }

    [TestMethod]
    [TestCategory("Update")]
    public async Task DownloadUpdateAsync_WithoutCheck_ThrowsInsteadOfReportingSuccess()
    {
        UpdateService service = CreateService();

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => service.DownloadUpdateAsync());

        Assert.IsNull(service.ReadyUpdate);
        Assert.AreEqual(0, _source.DownloadCalls);
    }

    [TestMethod]
    [TestCategory("Update")]
    public async Task DownloadUpdateAsync_TargetClearedByConcurrentCheck_ThrowsWithoutReady()
    {
        // 关于页检查完、还没开始下载,启动时的通知检查插进来清空了检查结果:无参数下载不能拿着
        // 空目标「成功」返回,否则关于页报「已就绪」,手里却没有包。
        byte[] zip = CreateZipBytes(("app.txt", "new"));
        UpdateManifest manifest = CreateManifest("2.0.0", zip);
        _source.Manifest = manifest;
        _source.AssetBytes = zip;
        UpdateService service = CreateService();
        Assert.IsTrue(await service.CheckForUpdateAsync());
        TaskCompletionSource<UpdateManifest?> pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _source.OnManifest = () => pending.Task;
        Task<bool> notificationCheck = service.CheckForUpdateAsync();
        try
        {
            Assert.IsNull(service.AvailableVersion, "并发检查一开始就清掉了上一次的结果");
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => service.DownloadUpdateAsync());
            Assert.IsNull(service.ReadyUpdate);
            Assert.AreEqual(0, _source.DownloadCalls);
            Assert.ThrowsExactly<InvalidOperationException>(service.ApplyUpdateAndRestart);
        }
        finally
        {
            pending.TrySetResult(manifest);
        }
        Assert.IsTrue(await notificationCheck.WaitAsync(TimeSpan.FromSeconds(10)));
        await service.DownloadUpdateAsync();
        Assert.AreSame(manifest, service.ReadyUpdate, "并发检查落定后再下载照常就绪");
    }

    [TestMethod]
    [TestCategory("Update")]
    public async Task DownloadUpdateAsync_ValidChecksum_Succeeds()
    {
        byte[] zip = CreateZipBytes(("app.txt", "new"));
        _source.Manifest = CreateManifest("2.0.0", zip);
        _source.AssetBytes = zip;
        UpdateService service = CreateService("1.0.0");
        Assert.IsTrue(await service.CheckForUpdateAsync());

        List<int> reported = [];
        await service.DownloadUpdateAsync(new SynchronousProgress(reported.Add));

        string archive = Path.Combine(_appDir, UpdateApplier.StagingDirectoryName, "VelaShell-2.0.0-" + UpdateManifest.CurrentRid() + ".zip");
        Assert.IsTrue(File.Exists(archive));
        Assert.Contains(100, reported);
    }

    [TestMethod]
    [TestCategory("Update")]
    public async Task DownloadUpdateAsync_ChecksumMismatch_ThrowsAndDeletesArchive()
    {
        byte[] zip = CreateZipBytes(("app.txt", "new"));
        _source.Manifest = CreateManifest("2.0.0", zip);
        _source.AssetBytes = [1, 2, 3]; // 与清单声明的哈希不符
        UpdateService service = CreateService("1.0.0");
        Assert.IsTrue(await service.CheckForUpdateAsync());

        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => service.DownloadUpdateAsync());

        string staging = Path.Combine(_appDir, UpdateApplier.StagingDirectoryName);
        Assert.IsEmpty(Directory.GetFiles(staging), "校验失败的下载必须删除");
        // 校验失败后不允许进入换版。
        Assert.ThrowsExactly<InvalidOperationException>(service.ApplyUpdateAndRestart);
    }

    [TestMethod]
    [TestCategory("Update")]
    public async Task DownloadUpdateAsync_ExistingValidArchive_ReusedWithoutRedownload()
    {
        byte[] zip = CreateZipBytes(("app.txt", "new"));
        _source.Manifest = CreateManifest("2.0.0", zip);
        _source.AssetBytes = zip;
        UpdateService service = CreateService("1.0.0");
        Assert.IsTrue(await service.CheckForUpdateAsync());
        await service.DownloadUpdateAsync();
        Assert.AreEqual(1, _source.DownloadCalls);

        // 再次下载(例如重复点“检查更新”):完整包校验通过,直接复用不再请求网络。
        List<int> reported = [];
        await service.DownloadUpdateAsync(new SynchronousProgress(reported.Add));

        Assert.AreEqual(1, _source.DownloadCalls);
        Assert.Contains(100, reported);
    }

    [TestMethod]
    [TestCategory("Update")]
    public async Task DownloadUpdateAsync_StaleLeftovers_AreCleaned()
    {
        byte[] zip = CreateZipBytes(("app.txt", "new"));
        _source.Manifest = CreateManifest("2.0.0", zip);
        _source.AssetBytes = zip;
        UpdateService service = CreateService("1.0.0");
        Assert.IsTrue(await service.CheckForUpdateAsync());
        string staging = Path.Combine(_appDir, UpdateApplier.StagingDirectoryName);
        Directory.CreateDirectory(staging);
        // 旧版本的包与半成品(文件名不同)必须被清掉,不参与本次续传。
        File.WriteAllText(Path.Combine(staging, "VelaShell-1.5.0-old.zip"), "stale");
        File.WriteAllText(Path.Combine(staging, "VelaShell-1.5.0-old.zip.partial"), "stale");

        await service.DownloadUpdateAsync();

        string[] files = Directory.GetFiles(staging);
        Assert.HasCount(1, files);
        Assert.AreEqual("VelaShell-2.0.0-" + UpdateManifest.CurrentRid() + ".zip", Path.GetFileName(files[0]));
    }

    [TestMethod]
    [TestCategory("Update")]
    public async Task DownloadUpdateAsync_PendingSwapBookkeeping_IsLeftAlone()
    {
        // 老实现会把暂存目录里除下载产物外的一切删光,包括换版日志 —— 上一轮没收尾的备份
        // 就此失去归属,再没有代码知道它们的存在,残留永久堆在应用目录里。
        byte[] zip = CreateZipBytes(("app.txt", "new"));
        _source.Manifest = CreateManifest("2.0.0", zip);
        _source.AssetBytes = zip;
        UpdateService service = CreateService("1.0.0");
        Assert.IsTrue(await service.CheckForUpdateAsync());
        UpdateApplier applier = new(_appDir);
        Directory.CreateDirectory(applier.BackupDirectory);
        File.WriteAllText(Path.Combine(applier.BackupDirectory, "app.txt"), "previous version");
        string journal = Path.Combine(applier.StagingDirectory, UpdateApplier.JournalFileName);
        File.WriteAllText(journal, """{ "Phase": "applying", "Files": [ { "Path": "app.txt", "Existed": true } ] }""");

        await service.DownloadUpdateAsync();

        Assert.IsTrue(File.Exists(journal), "换版日志归 UpdateApplier 管,下载流程不得删除");
        Assert.AreEqual("previous version", File.ReadAllText(Path.Combine(applier.BackupDirectory, "app.txt")));
    }

    [TestMethod]
    [TestCategory("Update")]
    [DataRow(false)]
    [DataRow(true)]
    public async Task DownloadUpdateAsync_InFlight_PinsSnapshotAndRejectsOtherOperations(bool explicitTarget)
    {
        byte[] zip = CreateZipBytes(("app.txt", "v3"));
        UpdateManifest v3 = CreateManifest("3.0.0", zip);
        _source.Manifest = v3;
        _source.AssetBytes = zip;
        int shutdowns = 0;
        UpdateService service = CreateService(shutdown: () => shutdowns++);
        if (explicitTarget && !service.CanSelfUpdate)
        {
            Assert.Inconclusive("Explicit downloads require a self-updating platform.");
        }
        Assert.IsTrue(await service.CheckForUpdateAsync());
        _source.Manifest = CreateManifest("4.0.0", zip);
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _source.OnDownload = async (_, ct) =>
        {
            started.TrySetResult();
            await release.Task.WaitAsync(ct);
        };

        Task download = explicitTarget ? service.DownloadUpdateAsync(v3) : service.DownloadUpdateAsync();
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.IsTrue(await service.CheckForUpdateAsync());
            Assert.AreEqual(1, _source.ManifestCalls, "下载期间不查询 v4");
            Assert.AreEqual("3.0.0", service.AvailableVersion);
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => service.DownloadUpdateAsync());
            if (service.CanSelfUpdate)
            {
                await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => service.DownloadUpdateAsync(v3));
            }
            Assert.IsFalse(service.RepairUpdateState(), "下载期间修复不能清除下载锁");
            Assert.ThrowsExactly<InvalidOperationException>(service.ApplyUpdateAndRestart);
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => service.DownloadUpdateAsync());
            Assert.AreEqual(1, _source.DownloadCalls, "被拒操作的 finally 不能解除正在下载的操作锁");
            Assert.IsNull(service.ReadyUpdate);
            Assert.AreEqual(0, shutdowns);
        }
        finally
        {
            release.TrySetResult();
            await download.WaitAsync(TimeSpan.FromSeconds(10));
        }

        Assert.AreSame(v3, _source.DownloadedManifest);
        Assert.AreEqual(v3.AssetForCurrentPlatform(), _source.DownloadedAsset);
        Assert.AreSame(v3, service.ReadyUpdate);
        Assert.IsTrue(await service.CheckForUpdateAsync());
        Assert.AreEqual(1, _source.ManifestCalls, "Ready 期间同样不查询 v4");
        Assert.AreEqual("3.0.0", service.AvailableVersion);
    }

    [TestMethod]
    [TestCategory("Update")]
    [DataRow(false)]
    [DataRow(true)]
    public async Task CheckForUpdateAsync_LateResult_CannotReplaceExplicitDownload(bool completeDownloadFirst)
    {
        byte[] zip = CreateZipBytes(("app.txt", "v3"));
        UpdateManifest v3 = CreateManifest("3.0.0", zip);
        UpdateManifest v4 = CreateManifest("4.0.0", zip);
        _source.AssetBytes = zip;
        UpdateService service = CreateService();
        if (!service.CanSelfUpdate)
        {
            Assert.Inconclusive("Explicit downloads require a self-updating platform.");
        }
        TaskCompletionSource<UpdateManifest?> checkedManifest = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _source.OnManifest = () => checkedManifest.Task;
        _source.OnDownload = async (_, ct) =>
        {
            started.TrySetResult();
            await release.Task.WaitAsync(ct);
        };
        Task<bool> check = service.CheckForUpdateAsync();
        Task download = service.DownloadUpdateAsync(v3);
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            if (completeDownloadFirst)
            {
                release.TrySetResult();
                await download.WaitAsync(TimeSpan.FromSeconds(10));
            }
            checkedManifest.TrySetResult(v4);
            Assert.IsFalse(await check.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.AreEqual("3.0.0", service.AvailableVersion);
            Assert.AreSame(v3, _source.DownloadedManifest);
        }
        finally
        {
            checkedManifest.TrySetResult(v4);
            release.TrySetResult();
            await download.WaitAsync(TimeSpan.FromSeconds(10));
            await check.WaitAsync(TimeSpan.FromSeconds(10));
        }
        Assert.AreSame(v3, service.ReadyUpdate);
    }

    [TestMethod]
    [TestCategory("Update")]
    public async Task CheckForUpdateAsync_LateResultAfterFailedDownload_IsDiscardedByGeneration()
    {
        byte[] zip = CreateZipBytes(("app.txt", "v3"));
        UpdateManifest v3 = CreateManifest("3.0.0", zip);
        UpdateService service = CreateService();
        if (!service.CanSelfUpdate)
        {
            Assert.Inconclusive("Explicit downloads require a self-updating platform.");
        }
        TaskCompletionSource<UpdateManifest?> result = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _source.OnManifest = () => result.Task;
        _source.OnDownload = static (_, _) => Task.FromException(new HttpRequestException("offline"));
        Task<bool> check = service.CheckForUpdateAsync();
        try
        {
            await Assert.ThrowsExactlyAsync<HttpRequestException>(() => service.DownloadUpdateAsync(v3));
        }
        finally
        {
            result.TrySetResult(CreateManifest("4.0.0", zip));
        }
        Assert.IsFalse(await check.WaitAsync(TimeSpan.FromSeconds(10)),
            "下载已失败且没有 Ready,仍须由 generation 拦下旧检查");
        Assert.AreEqual("3.0.0", service.AvailableVersion);
        Assert.IsNull(service.ReadyUpdate);
        _source.OnDownload = null;
        _source.AssetBytes = zip;
        await service.DownloadUpdateAsync();
        Assert.AreSame(v3, service.ReadyUpdate);
    }

    [TestMethod]
    [TestCategory("Update")]
    public async Task CheckForUpdateAsync_LateResultAfterRepair_IsDiscarded()
    {
        TaskCompletionSource<UpdateManifest?> result = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _source.OnManifest = () => result.Task;
        UpdateService service = CreateService();
        Task<bool> check = service.CheckForUpdateAsync();
        Assert.IsTrue(service.RepairUpdateState());
        result.SetResult(CreateManifest("4.0.0", CreateZipBytes(("app.txt", "v4"))));
        Assert.IsFalse(await check.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.IsNull(service.AvailableVersion);
        Assert.IsNull(service.ReadyUpdate);
    }

    [TestMethod]
    [TestCategory("Update")]
    public async Task CheckForUpdateAsync_OlderCheckCompletesLast_KeepsLatestGeneration()
    {
        byte[] zip = CreateZipBytes(("app.txt", "new"));
        TaskCompletionSource<UpdateManifest?> result = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _source.OnManifest = () => result.Task;
        UpdateService service = CreateService();
        Task<bool> first = service.CheckForUpdateAsync();
        _source.OnManifest = null;
        _source.Manifest = CreateManifest("3.0.0", zip);
        Assert.IsTrue(await service.CheckForUpdateAsync());
        result.SetResult(CreateManifest("4.0.0", zip));
        Assert.IsFalse(await first.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.AreEqual("3.0.0", service.AvailableVersion);
    }

    [TestMethod]
    [TestCategory("Update")]
    public async Task DownloadUpdateAsync_ExplicitSnapshot_NoLatestLookupAndReadyIdentityIsEnforced()
    {
        byte[] zip = CreateZipBytes(("app.txt", "v3"));
        UpdateManifest v3 = CreateManifest("3.0.0", zip);
        _source.Manifest = CreateManifest("4.0.0", zip);
        _source.AssetBytes = zip;
        UpdateService service = CreateService();
        if (!service.CanSelfUpdate)
        {
            Assert.Inconclusive("Explicit downloads require a self-updating platform.");
        }

        await service.DownloadUpdateAsync(v3);
        Assert.AreEqual(0, _source.ManifestCalls);
        Assert.AreSame(v3, service.ReadyUpdate);
        Assert.AreEqual("3.0.0", service.AvailableVersion);
        // 同值但非同一对象的快照也应复用,不能只比较引用。
        await service.DownloadUpdateAsync(CreateManifest("3.0.0", zip));
        Assert.AreEqual(1, _source.DownloadCalls);
        UpdateManifest ready = service.ReadyUpdate!;
        UpdateManifest[] differentTargets =
        [
            _source.Manifest!,
            CreateManifest("3.0.0", zip, tag: "3.0.0"),
            CreateManifest("3.0.0", zip, assetName: "other.zip"),
            CreateManifest("3.0.0", zip, sha256: new string('a', 64)),
            CreateManifest("3.0.0", zip, size: zip.Length + 1)
        ];
        foreach (UpdateManifest different in differentTargets)
        {
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => service.DownloadUpdateAsync(different));
            Assert.AreSame(ready, service.ReadyUpdate);
            Assert.AreEqual("3.0.0", service.AvailableVersion);
        }
        Assert.AreEqual(1, _source.DownloadCalls);
        Assert.IsTrue(service.RepairUpdateState());
        Assert.IsNull(service.ReadyUpdate);
        await service.DownloadUpdateAsync(_source.Manifest!);
        Assert.AreEqual("4.0.0", service.ReadyUpdate!.Version);
    }

    [TestMethod]
    [TestCategory("Update")]
    [DataRow(false)]
    [DataRow(true)]
    public async Task DownloadUpdateAsync_MissingOrCorruptReadyArchive_FailedRetryClearsReady(bool missing)
    {
        byte[] zip = CreateZipBytes(("app.txt", "new"));
        _source.Manifest = CreateManifest("3.0.0", zip);
        _source.AssetBytes = zip;
        int shutdowns = 0;
        UpdateService service = CreateService(shutdown: () => shutdowns++);
        Assert.IsTrue(await service.CheckForUpdateAsync());
        await service.DownloadUpdateAsync();
        string archive = Path.Combine(_appDir, UpdateApplier.StagingDirectoryName,
            _source.Manifest.AssetForCurrentPlatform()!.Name);
        if (missing)
        {
            File.Delete(archive);
        }
        else
        {
            await File.WriteAllTextAsync(archive, "corrupt");
        }
        _source.OnDownload = static (_, _) => Task.FromException(new HttpRequestException("offline"));

        await Assert.ThrowsExactlyAsync<HttpRequestException>(() => service.DownloadUpdateAsync());
        Assert.IsNull(service.ReadyUpdate, "重下失败不能仍报告旧包 Ready");
        Assert.ThrowsExactly<InvalidOperationException>(service.ApplyUpdateAndRestart);
        Assert.AreEqual(0, shutdowns);
        _source.OnDownload = null;
        await service.DownloadUpdateAsync();
        Assert.AreSame(_source.Manifest, service.ReadyUpdate, "失败后操作锁必须释放,允许再次下载");
    }

    [TestMethod]
    [TestCategory("Update")]
    [DataRow("v3.0.0")]
    [DataRow("3.0.0")]
    public async Task DownloadUpdateAsync_ExplicitManifest_AcceptsMatchingTagWithOrWithoutV(string tag)
    {
        byte[] zip = CreateZipBytes(("app.txt", "new"));
        _source.AssetBytes = zip;
        UpdateService service = CreateService();
        if (!service.CanSelfUpdate)
        {
            Assert.Inconclusive("Explicit downloads require a self-updating platform.");
        }
        UpdateManifest manifest = CreateManifest("3.0.0", zip, tag: tag);
        await service.DownloadUpdateAsync(manifest);
        Assert.AreSame(manifest, service.ReadyUpdate);
        Assert.AreEqual(0, _source.ManifestCalls);
    }

    [TestMethod]
    [TestCategory("Update")]
    [DataRow("1.0.0", "v1.0.0")]
    [DataRow("0.9.0", "0.9.0")]
    [DataRow("3.0.0", "v4.0.0")]
    [DataRow("3.0.0", "not-a-version")]
    public async Task DownloadUpdateAsync_ExplicitManifest_RejectsOldOrMismatchedVersion(string version, string tag)
    {
        UpdateService service = CreateService();
        if (!service.CanSelfUpdate)
        {
            Assert.Inconclusive("Explicit downloads require a self-updating platform.");
        }
        UpdateManifest manifest = CreateManifest(version, CreateZipBytes(("a", "b")), tag: tag);
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => service.DownloadUpdateAsync(manifest));
        Assert.AreEqual(0, _source.DownloadCalls);
        Assert.IsNull(service.ReadyUpdate);
        Assert.IsNull(service.AvailableVersion);
    }

    [TestMethod]
    [TestCategory("Update")]
    public async Task DownloadUpdateAsync_ExplicitManifest_WithoutCurrentPlatformAssetIsRejected()
    {
        UpdateService service = CreateService();
        if (!service.CanSelfUpdate)
        {
            Assert.Inconclusive("Explicit downloads require a self-updating platform.");
        }
        var manifest = UpdateManifest.Parse("""
            { "version": "3.0.0", "tag": "3.0.0",
              "assets": { "solaris-sparc": { "name": "archive.zip", "sha256": "abc", "size": 1 } } }
            """);
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => service.DownloadUpdateAsync(manifest));
        Assert.AreEqual(0, _source.DownloadCalls);
        Assert.IsNull(service.ReadyUpdate);
    }

    [TestMethod]
    [TestCategory("Update")]
    [DataRow("../escape.zip", 1L, false)]
    [DataRow("..\\escape.zip", 1L, false)]
    [DataRow("C:\\escape.zip", 1L, false)]
    [DataRow("/escape.zip", 1L, false)]
    [DataRow("archive.zip:stream", 1L, false)]
    [DataRow("archive.zip.", 1L, false)]
    [DataRow("archive.zip", 0L, false)]
    [DataRow("archive.zip", 8589934593L, false)]
    [DataRow("archive.zip", 1L, true)]
    public async Task DownloadUpdateAsync_UnsafeAsset_IsRejectedBeforeDownload(string name, long size, bool badHash)
    {
        UpdateManifest manifest = CreateManifest("3.0.0", [1], assetName: name,
            sha256: badHash ? new string('z', 64) : null, size: size);
        UpdateService service = CreateService();
        if (service.CanSelfUpdate)
        {
            await Assert.ThrowsExactlyAsync<ArgumentException>(() => service.DownloadUpdateAsync(manifest));
        }
        // 旧入口仍用于跨平台底层下载,但也不能让不安全文件名越过暂存目录边界。
        _source.Manifest = manifest;
        Assert.IsTrue(await service.CheckForUpdateAsync());
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => service.DownloadUpdateAsync());
        Assert.AreEqual(0, _source.DownloadCalls);
        Assert.IsNull(service.ReadyUpdate);
        Assert.IsFalse(Directory.Exists(Path.Combine(_appDir, UpdateApplier.StagingDirectoryName)));
    }

    [TestMethod]
    [TestCategory("Update")]
    public async Task StoreManaged_RejectsBothDownloadEntrypointsAndApply()
    {
        UpdateManifest manifest = CreateManifest("3.0.0", CreateZipBytes(("a", "b")));
        int shutdowns = 0;
        UpdateService service = new(_source, applicationDirectory: _appDir,
            currentVersionOverride: "1.0.0", shutdownForRestart: () => shutdowns++, storeManagedOverride: true);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => service.DownloadUpdateAsync());
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => service.DownloadUpdateAsync(manifest));
        Assert.ThrowsExactly<InvalidOperationException>(service.ApplyUpdateAndRestart);
        Assert.AreEqual(0, _source.DownloadCalls);
        Assert.AreEqual(0, _source.ManifestCalls);
        Assert.AreEqual(0, shutdowns);
        Assert.IsNull(service.ReadyUpdate);
    }

    [TestMethod]
    [TestCategory("Update")]
    public async Task DownloadUpdateAsync_ExplicitManifest_UnsupportedInstallationIsRejected()
    {
        UpdateManifest manifest = CreateManifest("3.0.0", CreateZipBytes(("a", "b")));
        // 不存在的安装目录不能写探针,与 Linux 的平台防御都走 CanSelfUpdate。
        UpdateService service = new(_source, applicationDirectory: Path.Combine(_appDir, "missing"),
            currentVersionOverride: "1.0.0", shutdownForRestart: static () => { }, storeManagedOverride: false);
        Assert.IsFalse(service.CanSelfUpdate);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => service.DownloadUpdateAsync(manifest));
        Assert.AreEqual(0, _source.DownloadCalls);
        Assert.IsNull(service.AvailableVersion);
    }

    // ———— 下载停滞看门狗 ————

    [TestMethod]
    [TestCategory("Update")]
    public async Task DownloadUpdateAsync_SilentlyDroppedConnection_ReconnectsAndResumesFromPartial()
    {
        // NAT/VPN 静默断开:响应体读到一半就再也没有数据,TCP 也不报错。老实现会一直挂着,
        // 界面上「已 1h 20m 没有收到数据」永远涨下去;现在看门狗掐断这次尝试,带 Range 续传剩下的部分。
        byte[] zip = CreateZipBytes(("app.txt", new string('x', 300_000)));
        string rid = UpdateManifest.CurrentRid()!;
        string assetName = $"VelaShell-2.0.0-{rid}.zip";
        int half = zip.Length / 2;
        List<long?> ranges = [];
        GitHubReleaseSource source = new("https://github.com/owner/repo", new StubHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/latest.json", StringComparison.Ordinal))
            {
                return new(System.Net.HttpStatusCode.OK)
                {
                    Content = new StringContent($$"""
                        { "version": "2.0.0", "tag": "v2.0.0",
                          "assets": { "{{rid}}": { "name": "{{assetName}}",
                            "sha256": "{{Convert.ToHexStringLower(SHA256.HashData(zip))}}", "size": {{zip.Length}} } } }
                        """)
                };
            }
            long from = request.Headers.Range?.Ranges.First().From ?? 0;
            lock (ranges)
            {
                ranges.Add(request.Headers.Range?.Ranges.First().From);
            }
            // 第一次连接只给前一半就"断"(读操作挂住直到被取消);重连后正常给完剩下的。
            HttpResponseMessage response = new(from == 0 ? System.Net.HttpStatusCode.OK : System.Net.HttpStatusCode.PartialContent)
            {
                Content = new StreamContent(new TrickleStream(zip[(int)from..], hangAfter: from == 0 ? half : null))
            };
            response.Content.Headers.ContentLength = zip.Length - from;
            return response;
        }));
        UpdateService service = new(
            source,
            applicationDirectory: _appDir,
            currentVersionOverride: "1.0.0",
            shutdownForRestart: static () => { })
        {
            DownloadStallTimeout = TimeSpan.FromSeconds(1)
        };
        Assert.IsTrue(await service.CheckForUpdateAsync());

        await service.DownloadUpdateAsync().WaitAsync(TimeSpan.FromSeconds(30));

        Assert.HasCount(2, ranges, "停滞后应当恰好重连一次");
        Assert.IsNull(ranges[0]);
        Assert.AreEqual(half, ranges[1], "重连要从半成品的断点续传,而不是整包重下");
        string archive = Path.Combine(_appDir, UpdateApplier.StagingDirectoryName, assetName);
        // 先 await 再比:Span 不能跨 await 存活。
        byte[] downloaded = await File.ReadAllBytesAsync(archive);
        Assert.IsTrue(zip.AsSpan().SequenceEqual(downloaded), "续传拼起来的包必须逐字节一致");
    }

    [TestMethod]
    [TestCategory("Update")]
    public async Task DownloadUpdateAsync_StallsEveryTime_GivesUpAfterBoundedReconnects()
    {
        byte[] zip = CreateZipBytes(("app.txt", "new"));
        _source.Manifest = CreateManifest("2.0.0", zip);
        _source.AssetBytes = zip;
        _source.OnDownload = static (_, ct) => Task.Delay(Timeout.InfiniteTimeSpan, ct);
        UpdateService service = CreateService("1.0.0", stallTimeout: TimeSpan.FromMilliseconds(50));
        Assert.IsTrue(await service.CheckForUpdateAsync());

        // 次数用尽必须抛出,调用方(关于页)据此报下载失败、恢复按钮,而不是永远挂着。
        await Assert.ThrowsExactlyAsync<TimeoutException>(
            () => service.DownloadUpdateAsync().WaitAsync(TimeSpan.FromSeconds(30)));

        Assert.AreEqual(1 + UpdateService.MaxStallReconnects, _source.DownloadCalls);
    }

    [TestMethod]
    [TestCategory("Update")]
    public async Task DownloadUpdateAsync_CallerCancels_DoesNotReconnect()
    {
        byte[] zip = CreateZipBytes(("app.txt", "new"));
        _source.Manifest = CreateManifest("2.0.0", zip);
        _source.AssetBytes = zip;
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _source.OnDownload = (_, ct) =>
        {
            started.TrySetResult();
            return Task.Delay(Timeout.InfiniteTimeSpan, ct);
        };
        UpdateService service = CreateService("1.0.0");
        Assert.IsTrue(await service.CheckForUpdateAsync());
        using CancellationTokenSource cancellation = new();

        Task download = service.DownloadUpdateAsync(cancellationToken: cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await cancellation.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(() => download.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.AreEqual(1, _source.DownloadCalls, "调用方主动取消不是停滞,不该重连");
    }

    [TestMethod]
    [TestCategory("Update")]
    public async Task RepairUpdateState_ClearsStagingAndForcesFreshDownload()
    {
        byte[] zip = CreateZipBytes(("app.txt", "new"));
        _source.Manifest = CreateManifest("2.0.0", zip);
        _source.AssetBytes = zip;
        UpdateService service = CreateService("1.0.0");
        Assert.IsTrue(await service.CheckForUpdateAsync());
        await service.DownloadUpdateAsync();
        Assert.AreEqual(1, _source.DownloadCalls);

        Assert.IsTrue(service.RepairUpdateState());

        Assert.IsFalse(Directory.Exists(Path.Combine(_appDir, UpdateApplier.StagingDirectoryName)));
        // 修复会连检查结果一起丢掉,必须重新走完整流程(而不是拿着失效状态继续换版)。
        Assert.ThrowsExactly<InvalidOperationException>(service.ApplyUpdateAndRestart);
        Assert.IsTrue(await service.CheckForUpdateAsync());
        await service.DownloadUpdateAsync();
        Assert.AreEqual(2, _source.DownloadCalls);
    }

    [TestMethod]
    [TestCategory("Update")]
    public async Task ApplyUpdateAndRestart_StagesWithoutTouchingApplicationDirectory_OnFailure()
    {
        // 包与清单哈希一致却解不开(发布时就坏了):解包阶段失败,而解包不动应用目录,
        // 因此应用完好如初,重试也不需要任何人工清理。下载后才损坏的包换版前就会被拦下,见下一条。
        byte[] broken = "not an archive, but its hash matches the manifest"u8.ToArray();
        _source.Manifest = CreateManifest("2.0.0", broken);
        _source.AssetBytes = broken;
        UpdateService service = CreateService("1.0.0");
        File.WriteAllText(Path.Combine(_appDir, "app.txt"), "current");
        Assert.IsTrue(await service.CheckForUpdateAsync());
        await service.DownloadUpdateAsync();

        Assert.ThrowsExactly<InvalidDataException>(service.ApplyUpdateAndRestart);

        Assert.AreEqual("current", File.ReadAllText(Path.Combine(_appDir, "app.txt")));
        Assert.IsEmpty(Directory.GetFiles(_appDir, "*.old", SearchOption.AllDirectories));
        Assert.AreSame(_source.Manifest, service.ReadyUpdate, "包与快照一致,解包失败不丢就绪状态");
        Assert.IsTrue(service.RepairUpdateState(), "解包异常后必须释放操作锁,允许修复");
        Assert.IsNull(service.ReadyUpdate);
    }

    [TestMethod]
    [TestCategory("Update")]
    [DataRow(true)]
    [DataRow(false)]
    public async Task ApplyUpdateAndRestart_MissingOrCorruptReadyArchive_DropsReadyAndRedownloads(bool missing)
    {
        // 就绪后包被删或被改写:换版必须失败且不碰应用目录,同时丢掉名存实亡的就绪状态 ——
        // 否则检查更新一直短路成「已就绪」,重启又失败,只能靠修复更新状态脱身。
        byte[] zip = CreateZipBytes(("app.txt", "new"));
        _source.Manifest = CreateManifest("2.0.0", zip);
        _source.AssetBytes = zip;
        int shutdowns = 0;
        UpdateService service = CreateService(shutdown: () => shutdowns++);
        File.WriteAllText(Path.Combine(_appDir, "app.txt"), "current");
        Assert.IsTrue(await service.CheckForUpdateAsync());
        await service.DownloadUpdateAsync();
        Assert.AreSame(_source.Manifest, service.ReadyUpdate);
        string archive = Path.Combine(_appDir, UpdateApplier.StagingDirectoryName,
            _source.Manifest.AssetForCurrentPlatform()!.Name);
        if (missing)
        {
            File.Delete(archive);
        }
        else
        {
            File.WriteAllText(archive, "corrupted after checksum passed");
        }

        Assert.ThrowsExactly<InvalidDataException>(service.ApplyUpdateAndRestart);

        Assert.IsNull(service.ReadyUpdate, "包已不可用,不能再报告就绪");
        Assert.IsFalse(File.Exists(archive), "坏包当场删掉");
        Assert.AreEqual("current", File.ReadAllText(Path.Combine(_appDir, "app.txt")));
        Assert.AreEqual(0, shutdowns);
        Assert.ThrowsExactly<InvalidOperationException>(service.ApplyUpdateAndRestart);
        Assert.IsTrue(await service.CheckForUpdateAsync());
        Assert.AreEqual(2, _source.ManifestCalls, "就绪状态已丢,检查更新要真的去查");
        await service.DownloadUpdateAsync();
        Assert.AreEqual(2, _source.DownloadCalls);
        Assert.AreSame(_source.Manifest, service.ReadyUpdate);
    }

    [TestMethod]
    [TestCategory("Update")]
    public async Task StoreManaged_SkipsUpdateCheckEntirely()
    {
        // 商店版(MSIX)装在只读的 WindowsApps 下,更新归 Microsoft Store 管:连请求都不该发,
        // 查到了也无从安装,只会给用户一条装不上的"有新版本"提示。
        byte[] zip = CreateZipBytes(("app.txt", "new"));
        _source.Manifest = CreateManifest("2.0.0", zip);
        _source.AssetBytes = zip;
        UpdateService service = new(
            _source,
            applicationDirectory: _appDir,
            currentVersionOverride: "1.0.0",
            shutdownForRestart: static () => { },
            storeManagedOverride: true);

        Assert.IsTrue(service.IsStoreManaged);
        Assert.IsFalse(service.CanSelfUpdate, "商店版不得声称能自更新");
        Assert.IsFalse(await service.CheckForUpdateAsync(), "有新版本也要返回 false");
        Assert.AreEqual(0, _source.ManifestCalls, "商店版不该访问更新源");
    }

    [TestMethod]
    [TestCategory("Update")]
    public async Task PortableBuild_StillChecksForUpdates()
    {
        // 同一份二进制在便携形态下行为不变:商店判定只关掉商店版,不影响 GitHub 分发的那份。
        byte[] zip = CreateZipBytes(("app.txt", "new"));
        _source.Manifest = CreateManifest("2.0.0", zip);
        _source.AssetBytes = zip;
        UpdateService service = new(
            _source,
            applicationDirectory: _appDir,
            currentVersionOverride: "1.0.0",
            shutdownForRestart: static () => { },
            storeManagedOverride: false);

        Assert.IsFalse(service.IsStoreManaged);
        Assert.IsTrue(await service.CheckForUpdateAsync());
        Assert.AreEqual(!OperatingSystem.IsLinux(), service.CanSelfUpdate);
    }

    [TestMethod]
    [TestCategory("Update")]
    public void ApplyUpdateAndRestart_WithoutDownload_ThrowsInvalidOperation() => Assert.ThrowsExactly<InvalidOperationException>(() => CreateService().ApplyUpdateAndRestart());

    [TestMethod]
    [TestCategory("Update")]
    public void Constructor_WithNullSource_ThrowsArgumentNullException() => Assert.ThrowsExactly<ArgumentNullException>(() => _ = new UpdateService((IUpdateSource)null!));

    [TestMethod]
    [TestCategory("Update")]
    public void Constructor_WithInvalidRepositoryUrl_Throws() => Assert.ThrowsExactly<ArgumentException>(() => _ = new UpdateService("https://github.com/only-owner"));

    [TestMethod]
    [TestCategory("Update")]
    public void ImplementsIUpdateService() => Assert.IsInstanceOfType<IUpdateService>(CreateService());

    /// <summary>同步回调的进度器(Progress&lt;T&gt; 经同步上下文投递,测试里改用直呼)。</summary>
    private sealed class SynchronousProgress(Action<int> handler) : IProgress<UpdateDownloadProgress>
    {
        public void Report(UpdateDownloadProgress value) => handler(value.Percent);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(responder(request));
    }

    /// <summary>
    /// 不可寻址的响应体:按块吐出 <paramref name="data" />;给了 <paramref name="hangAfter" /> 时,
    /// 吐完这么多字节后读操作一直挂着直到被取消 —— 即连接静默断开的样子。
    /// </summary>
    private sealed class TrickleStream(byte[] data, int? hangAfter) : Stream
    {
        private int _position;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _position; set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            int end = hangAfter ?? data.Length;
            if (_position >= end && end < data.Length)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            int count = Math.Min(buffer.Length, end - _position);
            data.AsSpan(_position, count).CopyTo(buffer.Span);
            _position += count;
            return count;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class FakeUpdateSource : IUpdateSource
    {
        public UpdateManifest? Manifest { get; set; }
        public byte[]? AssetBytes { get; set; }
        public bool ThrowOnManifest { get; set; }
        public bool LastIncludePreRelease { get; private set; }
        public int DownloadCalls { get; private set; }
        public int ManifestCalls { get; private set; }
        public UpdateManifest? DownloadedManifest { get; private set; }
        public UpdateAsset? DownloadedAsset { get; private set; }
        public Func<Task<UpdateManifest?>>? OnManifest { get; set; }

        /// <summary>每次下载落盘前先跑一下(参数:第几次调用、取消令牌);用来模拟连接卡住。</summary>
        public Func<int, CancellationToken, Task>? OnDownload { get; set; }

        public Task<UpdateManifest?> GetLatestManifestAsync(bool includePreRelease, CancellationToken cancellationToken = default)
        {
            ManifestCalls++;
            LastIncludePreRelease = includePreRelease;
            return ThrowOnManifest
                ? Task.FromException<UpdateManifest?>(new HttpRequestException("offline"))
                : OnManifest?.Invoke() ?? Task.FromResult(Manifest);
        }

        public async Task<string?> DownloadAssetAsync(
            UpdateManifest manifest,
            UpdateAsset asset,
            string destinationPath,
            IProgress<UpdateDownloadProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            DownloadCalls++;
            DownloadedManifest = manifest;
            DownloadedAsset = asset;
            if (OnDownload != null)
            {
                await OnDownload(DownloadCalls, cancellationToken);
            }
            byte[] bytes = AssetBytes ?? [];
            await File.WriteAllBytesAsync(destinationPath, bytes, cancellationToken);
            progress?.Report(UpdateDownloadProgress.Completed(bytes.LongLength));
            // 不返回流式哈希,覆盖 UpdateService 读文件校验的兜底路径。
            return null;
        }
    }
}
