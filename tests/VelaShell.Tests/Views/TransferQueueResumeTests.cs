using Avalonia.Headless;
using NSubstitute;
using ReactiveUI.Primitives;
using VelaShell.Core.Data;
using VelaShell.Core.Models;
using VelaShell.Core.Resources;
using VelaShell.Core.Sftp;
using VelaShell.Docking;
using VelaShell.Infrastructure.Ftp;
using VelaShell.Infrastructure.Tests.Ftp;
using VelaShell.Tests.TestSupport;
using VelaShell.ViewModels;

namespace VelaShell.Tests.Views;

/// <summary>
/// 主窗口一侧的「继续」:按批次记下的连接配置找连接,没有就照常打开一个文件标签再接着传;配置删了如实说。
/// </summary>
/// <remarks>用环回 FTP 服务器走真实的打开路径与上传,不靠替身模拟「连上了」。</remarks>
[TestClass]
[TestCategory("FileTransfer")]
public sealed class TransferQueueResumeTests
{
    private static HeadlessUnitTestSession _session = null!;

    [ClassInitialize]
    public static void Init(TestContext _) =>
        _session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(TransferQueueResumeTests).Assembly);

    /// <summary>
    /// 没有任何标签连着那台机器:点「继续」才打开文件标签去连(而不是启动时),连上之后剩下的文件传过去,暂停的那一行撤掉。
    /// </summary>
    [TestMethod]
    public async Task ResumingWithNoOpenConnection_OpensAFileTabAndTransfersTheRest()
    {
        string root = Path.Combine(Path.GetTempPath(), $"vela-tq-ftp-{Guid.NewGuid():N}");
        string local = Path.Combine(Path.GetTempPath(), $"vela-tq-local-{Guid.NewGuid():N}");
        Directory.CreateDirectory(local);
        string file = Path.Combine(local, "report.txt");
        await File.WriteAllTextAsync(file, "quarterly numbers");
        using var server = new LoopbackFtpServer(root);
        try
        {
            await _session.RunOnUiAsync(async () =>
            {
                var ftp = new FtpFileService();
                SessionProfile profile = FtpProfile(server.Port);
                ISessionRepository repository = Substitute.For<ISessionRepository>();
                repository.GetAllSessionsAsync().Returns(_ => Task.FromResult(new List<SessionProfile> { profile }));
                repository.GetAllGroupsAsync().Returns(_ => Task.FromResult(new List<ServerGroup>()));
                var vm = new MainWindowViewModel(sessionRepository: repository, sftpService: ftp, ftpSessionService: ftp);
                FileTransferViewModel sink = vm.FileTransfer;
                Guid batchId = sink.Journal.Begin(
                    new(profile.Id, profile.Name),
                    [new TransferQueueItem(TransferType.Upload, file, "/report.txt")]);
                sink.ShowPausedBatch(batchId, PausedBatchReason.Restored);
                Assert.IsEmpty(vm.Layout.AllDocuments().OfType<SftpDocument>(), "挂上暂停行时还没有任何连接");

                sink.ResumePausedBatchCommand.Execute(batchId).Subscribe();
                // 等那一行传完(看传输行的状态,而不是看文件在不在:服务器那边可能还开着写句柄)。
                for (int i = 0; i < 300 && !sink.Transfers.Any(t => t.Status is TransferStatus.Completed or TransferStatus.Failed); i++)
                {
                    await Task.Delay(20);
                }

                SftpDocument document = vm.Layout.AllDocuments().OfType<SftpDocument>().Single();
                Assert.AreEqual(profile.Id, document.ViewModel.Profile.Id);
                Assert.AreEqual(TransferStatus.Completed, sink.Transfers.Single().Status, document.ViewModel.RemoteFiles.ErrorMessage);
                Assert.AreEqual("quarterly numbers", await File.ReadAllTextAsync(Path.Combine(root, "report.txt")));
                Assert.IsEmpty(sink.PausedBatches);
                Assert.IsNull(sink.Journal.Snapshot(batchId));
                await document.ViewModel.CloseAsync();
            });
        }
        finally
        {
            TryDeleteDirectory(root);
            TryDeleteDirectory(local);
        }
    }

    /// <summary>配置已经删了:不去猜主机,行上如实说明,「继续」不可再点。</summary>
    [TestMethod]
    public Task ResumingABatchWhoseProfileWasDeleted_SaysSo() => _session.RunOnUiAsync(async () =>
    {
        ISessionRepository repository = Substitute.For<ISessionRepository>();
        repository.GetAllSessionsAsync().Returns(_ => Task.FromResult(new List<SessionProfile>()));
        repository.GetAllGroupsAsync().Returns(_ => Task.FromResult(new List<ServerGroup>()));
        var vm = new MainWindowViewModel(sessionRepository: repository);
        FileTransferViewModel sink = vm.FileTransfer;
        Guid batchId = sink.Journal.Begin(
            new(Guid.NewGuid(), "已删除的机器"),
            [new TransferQueueItem(TransferType.Download, "C:/tmp/a.bin", "/srv/a.bin")]);
        sink.ShowPausedBatch(batchId, PausedBatchReason.Restored);
        PausedTransferBatchViewModel row = sink.PausedBatches.Single();

        sink.ResumePausedBatchCommand.Execute(batchId).Subscribe();
        for (int i = 0; i < 100 && (row.IsResuming || row.CanResume); i++)
        {
            await Task.Delay(10);
        }

        Assert.IsFalse(row.CanResume);
        Assert.Contains(Strings.Get("Transfer_PausedProfileMissing"), row.Detail);
        Assert.IsNotNull(sink.Journal.Snapshot(batchId), "没放弃之前,账上的那一批原样留着");
    });

    private static SessionProfile FtpProfile(int port) =>
        new()
        {
            ConnectionType = ConnectionType.FTP,
            Name = "环回 FTP",
            Host = "127.0.0.1",
            Port = port,
            Username = "deploy",
            Password = "secret123",
            Ftp = new FtpSettings { EncryptionMode = FtpEncryptionMode.None, MaxConnections = 2 },
        };

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, true);
            }
        }
        catch (IOException)
        {
        }
    }
}
