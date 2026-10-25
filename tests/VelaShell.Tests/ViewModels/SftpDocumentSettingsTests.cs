using Avalonia.Headless;
using NSubstitute;
using ReactiveUI.Primitives;
using VelaShell.Core.Data;
using VelaShell.Core.Models;
using VelaShell.Core.Sftp;
using VelaShell.Core.Ssh;
using VelaShell.Docking;
using VelaShell.Presentation.Services;
using VelaShell.Tests.TestSupport;
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
    /// 不往 UI 线程上派发,但要有 headless 会话:<see cref="MainWindowViewModel" /> 途中会碰 <c>Dispatcher.UIThread</c>
    /// (理由同 FtpConnectionFlowTests)。
    /// </summary>
    [ClassInitialize]
    public static void Init(TestContext _) =>
        HeadlessUnitTestSession.GetOrStartForAssembly(typeof(SftpDocumentSettingsTests).Assembly);

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

    /// <summary>
    /// 文件标签的远端栏按设置铺「显示隐藏文件」与列显示(差距分析 A8):原先一律是出厂值,
    /// 设置里开着隐藏文件,文件标签照样不显示。
    /// </summary>
    [TestMethod]
    public async Task OpeningFileTabs_TakesHiddenFilesAndColumnsFromTheSettings()
    {
        var vm = new MainWindowViewModel();
        using var temp = new TempDirectory();
        var opened = new TransferOptions
        {
            LocalDownloadDirectory = temp.Path,
            ShowHiddenFiles = true,
            ShowOwnerColumn = false,
            ShowModifiedColumn = false,
        };
        await using OpenFileTabs tabs = OpenFileTabs.Add(vm, opened);

        foreach (FileBrowserViewModel pane in tabs.Panes)
        {
            Assert.IsTrue(pane.ShowHiddenFiles, $"{pane.ServerDisplayName}:设置里开着隐藏文件。");
            Assert.IsFalse(pane.ShowOwnerColumn, pane.ServerDisplayName);
            Assert.IsFalse(pane.ShowModifiedColumn, pane.ServerDisplayName);
            Assert.IsTrue(pane.ShowSizeColumn, pane.ServerDisplayName);
        }
    }

    /// <summary>在别处(设置页、终端侧栏的面板)改了隐藏文件与列显示,开着的文件标签跟着变。</summary>
    [TestMethod]
    public async Task SettingsSaved_ReappliesHiddenFilesAndColumnsToOpenFileTabs()
    {
        ISettingsService settingsService = Substitute.For<ISettingsService>();
        var vm = new MainWindowViewModel(settingsService: settingsService);
        using var temp = new TempDirectory();
        await using OpenFileTabs tabs = OpenFileTabs.Add(vm, new TransferOptions { LocalDownloadDirectory = temp.Path });

        var saved = new AppSettings { Transfer = { ShowHiddenFiles = true, ShowTypeColumn = false } };
        settingsService.SettingsSaved += Raise.Event<Action<AppSettings>>(saved);

        SpinWait.SpinUntil(() => tabs.Panes.All(pane => pane.ShowHiddenFiles && !pane.ShowTypeColumn), TimeSpan.FromSeconds(5));
        foreach (FileBrowserViewModel pane in tabs.Panes)
        {
            Assert.IsTrue(pane.ShowHiddenFiles, pane.ServerDisplayName);
            Assert.IsFalse(pane.ShowTypeColumn, pane.ServerDisplayName);
        }
    }

    /// <summary>
    /// 在文件标签里点工具栏的「显示隐藏文件」、表头右键关掉一列,与终端侧栏的面板一样写回设置(设置审计 C-04)。
    /// 走真实的打开路径(<see cref="MainWindowViewModel.OpenSftpForProfileAsync" />),回调是宿主在那里挂上的。
    /// </summary>
    [TestMethod]
    public async Task TogglingInAFileTab_WritesBackToTheSettings()
    {
        var current = new AppSettings { Transfer = { ShowHiddenFiles = true } };
        ISettingsService settingsService = Substitute.For<ISettingsService>();
        settingsService.GetSettingsAsync().Returns(_ => Task.FromResult(current));
        (MainWindowViewModel vm, SessionProfile profile) = ConnectedSshMainWindow(settingsService);

        await vm.OpenSftpForProfileAsync(profile);
        SftpDocument document = vm.Layout.AllDocuments().OfType<SftpDocument>().Single();
        try
        {
            FileBrowserViewModel pane = document.ViewModel.RemoteFiles;
            Assert.IsTrue(pane.ShowHiddenFiles, "打开时按设置铺好。");

            pane.ToggleHiddenFilesCommand.Execute().Subscribe();
            pane.ShowOwnerColumn = false;

            // 写回在线程池上做(读设置 → 改 → 存),等它落地。
            await WaitUntilAsync(() => !current.Transfer.ShowHiddenFiles && !current.Transfer.ShowOwnerColumn);
            Assert.IsFalse(current.Transfer.ShowHiddenFiles, "工具栏切换要写回 Transfer.ShowHiddenFiles。");
            Assert.IsFalse(current.Transfer.ShowOwnerColumn, "表头右键的列开关要写回设置。");
            await settingsService.Received().SaveSettingsAsync(current);
        }
        finally
        {
            await document.ViewModel.CloseAsync();
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (int i = 0; i < 500 && !condition(); i++)
        {
            await Task.Delay(10);
        }
    }

    /// <summary>一个「SSH 会话已连上、SFTP 可列目录」的主窗口视图模型(装配同 StandaloneSftpDocumentBehaviorTests)。</summary>
    private static (MainWindowViewModel ViewModel, SessionProfile Profile) ConnectedSshMainWindow(ISettingsService settingsService)
    {
        IConnectionWorkflowService workflow = Substitute.For<IConnectionWorkflowService>();
        ISshConnectionService ssh = Substitute.For<ISshConnectionService>();
        ISftpService sftp = Substitute.For<ISftpService>();
        var profile = new SessionProfile
        {
            Name = "Files",
            Host = "files.example.com",
            Port = 22,
            Username = "root",
            AuthMethod = AuthMethod.Password,
            Password = "secret",
        };
        var session = new SshSession
        {
            SessionId = Guid.NewGuid(),
            ConnectionInfo = new()
            {
                Host = profile.Host,
                Port = profile.Port,
                Username = profile.Username,
                AuthMethod = profile.AuthMethod,
            },
            Status = SessionStatus.Connected,
        };
        workflow.ConnectProfileAsync(profile, Arg.Any<CancellationToken>()).Returns(session);
        sftp.GetWorkingDirectoryAsync(session.SessionId, Arg.Any<CancellationToken>()).Returns(Task.FromResult("/home/user"));
        sftp.ListDirectoryAsync(session.SessionId, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(new List<RemoteFileInfo>()));
        return (new MainWindowViewModel(workflow, ssh, () => FakeTerminal.Emulator(), settingsService: settingsService, sftpService: sftp), profile);
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
