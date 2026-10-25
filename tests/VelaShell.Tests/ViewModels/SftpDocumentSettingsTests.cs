using NSubstitute;
using VelaShell.Core.Data;
using VelaShell.Core.Models;
using VelaShell.Core.Sftp;
using VelaShell.Docking;
using VelaShell.ViewModels;

namespace VelaShell.Tests.ViewModels;

/// <summary>
/// 独立文件标签(单栏「本地 + 远程」、双栏「远程 + 远程」)与设置之间的接线。
/// 终端侧栏的文件面板早就随设置保存刷新、工具栏切换也写回设置;文件标签是各自 new 出来的面板,
/// 宿主的广播与回调都没覆盖到它们(差距分析 A7)。
/// </summary>
[TestClass]
[TestCategory("Sftp")]
public sealed class SftpDocumentSettingsTests
{
    /// <summary>
    /// 改了冲突策略、并发、重试次数之后,开着的文件标签(两种都算,双栏两栏都算)要当场换上新选项,
    /// 不能还按打开那一刻的快照走、要重开标签才生效。
    /// </summary>
    [TestMethod]
    public async Task SettingsSaved_ReplacesTheTransferOptionsOfEveryOpenFileTabPane()
    {
        ISettingsService settingsService = Substitute.For<ISettingsService>();
        var vm = new MainWindowViewModel(settingsService: settingsService);
        using var temp = new TempDirectory();
        var opened = new TransferOptions { LocalDownloadDirectory = temp.Path, ConflictPolicy = "ask" };
        await using OpenFileTabs tabs = OpenFileTabs.Add(vm, opened);

        var saved = new AppSettings
        {
            Transfer = { ConflictPolicy = "skip", MaxConcurrentTransfers = 7, TransferMaxRetries = 0 },
        };
        settingsService.SettingsSaved += Raise.Event<Action<AppSettings>>(saved);

        // 广播经 RxApp.MainThreadScheduler 编组,别的测试类可能已把它换成异步调度器 —— 等它落地。
        SpinWait.SpinUntil(() => tabs.Panes.All(pane => ReferenceEquals(pane.TransferOptions, saved.Transfer)), TimeSpan.FromSeconds(5));
        foreach (FileBrowserViewModel pane in tabs.Panes)
        {
            Assert.AreSame(saved.Transfer, pane.TransferOptions, $"{pane.ServerDisplayName} 还拿着打开时的那份设置。");
        }
    }

    /// <summary>开着一个单栏文件标签与一个双栏远程标签,收尾时两个都关掉(先于临时目录删除,免得目录监视回调进来)。</summary>
    private sealed class OpenFileTabs : IAsyncDisposable
    {
        private OpenFileTabs(SftpDocumentViewModel single, DualSftpDocumentViewModel dual)
        {
            Single = single;
            Dual = dual;
        }

        public SftpDocumentViewModel Single { get; }

        public DualSftpDocumentViewModel Dual { get; }

        public IReadOnlyList<FileBrowserViewModel> Panes => [Single.RemoteFiles, Dual.LeftFiles, Dual.RightFiles];

        public static OpenFileTabs Add(MainWindowViewModel vm, TransferOptions opened)
        {
            ISftpService sftp = Substitute.For<ISftpService>();
            sftp.GetWorkingDirectoryAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult("/"));
            sftp.ListDirectoryAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(_ => Task.FromResult(new List<RemoteFileInfo>()));
            static Task Disconnect(Guid _, CancellationToken __) => Task.CompletedTask;

            var single = new SftpDocumentViewModel(Profile("single"), Guid.NewGuid(), Disconnect, sftp, opened);
            var dual = new DualSftpDocumentViewModel(
                new DualSftpEndpoint(Profile("left"), Guid.NewGuid(), null, Disconnect),
                new DualSftpEndpoint(Profile("right"), Guid.NewGuid(), null, Disconnect),
                sftp,
                opened);
            vm.Layout.AddDocument(new SftpDocument(single));
            vm.Layout.AddDocument(new DualSftpDocument(dual));
            return new(single, dual);
        }

        public async ValueTask DisposeAsync()
        {
            await Single.CloseAsync();
            await Dual.CloseAsync();
        }

        private static SessionProfile Profile(string name) => new()
        {
            ConnectionType = ConnectionType.FTP,
            Name = name,
            Host = $"{name}.example.com",
            Port = 21,
            Username = "u",
        };
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"velashell-doc-settings-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, true);
            }
        }
    }
}
