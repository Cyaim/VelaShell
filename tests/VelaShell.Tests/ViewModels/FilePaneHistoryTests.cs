using NSubstitute;
using NSubstitute.ExceptionExtensions;
using ReactiveUI.Primitives;
using VelaShell.Core.Models;
using VelaShell.Core.Sftp;
using VelaShell.ViewModels;

namespace VelaShell.Tests.ViewModels;

/// <summary>
/// 文件栏的后退 / 前进(维护者 2026-10-25 定:Alt+← / Alt+→ 加按钮,两栏各自的历史栈)。
/// 规则照浏览器:进了别的目录就把原目录压进后退栈、前进栈作废;刷新同一目录不记;首次加载之前的占位路径不记;
/// 目标进不去时照常报错、历史不动。断线重连后宿主建的是新面板,历史自然不留(见 MainWindowViewModel.RebindFileBrowser)。
/// </summary>
[TestClass]
[TestCategory("FileBrowser")]
public sealed class FilePaneHistoryTests
{
    private static readonly Guid Session = Guid.NewGuid();

    // ---- 远端栏 ----

    [TestMethod]
    public async Task Remote_BackAndForward_WalkTheVisitedDirectories()
    {
        FileBrowserViewModel vm = RemotePane(out _);
        await vm.NavigateToAsync("/a");
        Assert.IsFalse(vm.CanGoBack, "首次加载之前的 \"/\" 只是占位,用户没在那儿待过。");
        await vm.NavigateToAsync("/b");
        await vm.NavigateToAsync("/c");

        await vm.GoBackAsync();
        Assert.AreEqual("/b", vm.CurrentPath);
        Assert.IsTrue(vm.CanGoForward);
        await vm.GoBackAsync();
        Assert.AreEqual("/a", vm.CurrentPath);
        Assert.IsFalse(vm.CanGoBack);

        await vm.GoForwardAsync();
        Assert.AreEqual("/b", vm.CurrentPath);
        await vm.GoForwardAsync();
        Assert.AreEqual("/c", vm.CurrentPath);
        Assert.IsFalse(vm.CanGoForward);
        Assert.IsTrue(vm.CanGoBack);
    }

    [TestMethod]
    public async Task Remote_NavigatingSomewhereNew_AfterGoingBack_DropsTheForwardHistory()
    {
        FileBrowserViewModel vm = RemotePane(out _);
        await vm.NavigateToAsync("/a");
        await vm.NavigateToAsync("/b");
        await vm.GoBackAsync();
        Assert.IsTrue(vm.CanGoForward);

        await vm.NavigateToAsync("/c");

        Assert.IsFalse(vm.CanGoForward, "从历史中间岔出去,就没有「前进」了。");
        await vm.GoBackAsync();
        Assert.AreEqual("/a", vm.CurrentPath);
    }

    [TestMethod]
    public async Task Remote_RefreshingTheSameDirectory_IsNotRecorded()
    {
        FileBrowserViewModel vm = RemotePane(out _);
        await vm.NavigateToAsync("/a");
        await vm.NavigateToAsync("/b");

        await vm.NavigateToAsync("/b");
        await vm.GoBackAsync();

        Assert.AreEqual("/a", vm.CurrentPath, "刷新不该在历史里留一格「/b」。");
    }

    [TestMethod]
    public async Task Remote_GoingUp_IsRecordedLikeAnyOtherNavigation()
    {
        FileBrowserViewModel vm = RemotePane(out _);
        await vm.NavigateToAsync("/a/sub");

        await vm.GoUpCommand.Execute().FirstAsync();
        await vm.GoBackAsync();

        Assert.AreEqual("/a/sub", vm.CurrentPath);
    }

    [TestMethod]
    public async Task Remote_BackIntoAFolderThatIsGone_ReportsIt_AndKeepsTheHistory()
    {
        FileBrowserViewModel vm = RemotePane(out ISftpService sftp);
        await vm.NavigateToAsync("/a");
        await vm.NavigateToAsync("/b");
        sftp.ListDirectoryAsync(Session, "/a", Arg.Any<CancellationToken>()).ThrowsAsync(new IOException("No such file"));

        await vm.GoBackAsync();

        Assert.AreEqual("/b", vm.CurrentPath);
        Assert.IsNotNull(vm.ErrorMessage);
        Assert.IsTrue(vm.CanGoBack, "进不去就原样留着,不吞掉那一格。");
        Assert.IsFalse(vm.CanGoForward);
    }

    [TestMethod]
    public async Task Remote_History_IsCapped()
    {
        FileBrowserViewModel vm = RemotePane(out _);
        await vm.NavigateToAsync("/d0");
        for (int i = 1; i <= FileBrowserViewModel.MaxHistory + 5; i++)
        {
            await vm.NavigateToAsync($"/d{i}");
        }

        int steps = 0;
        while (vm.CanGoBack)
        {
            await vm.GoBackAsync();
            steps++;
        }

        Assert.AreEqual(FileBrowserViewModel.MaxHistory, steps);
        Assert.AreEqual("/d5", vm.CurrentPath, "最早的几格被挤掉了。");
    }

    [TestMethod]
    public async Task Remote_BackCommand_IsDisabledWithoutHistory()
    {
        FileBrowserViewModel vm = RemotePane(out _);
        await vm.NavigateToAsync("/a");
        var back = (System.Windows.Input.ICommand)vm.GoBackCommand;
        Assert.IsFalse(back.CanExecute(null));

        await vm.NavigateToAsync("/b");

        Assert.IsTrue(back.CanExecute(null));
    }

    // ---- 本地栏 ----

    [TestMethod]
    public async Task Local_BackAndForward_WalkTheVisitedDirectories()
    {
        string root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), $"vela-history-{Guid.NewGuid():N}"));
        string a = Directory.CreateDirectory(Path.Combine(root, "a")).FullName;
        string b = Directory.CreateDirectory(Path.Combine(root, "b")).FullName;
        try
        {
            using var vm = new LocalFilePaneViewModel(new TransferOptions());
            await vm.NavigateToAsync(root);
            Assert.IsFalse(vm.CanGoBack, "首次加载不记。");
            await vm.NavigateToAsync(a);
            await vm.NavigateToAsync(b);

            await vm.GoBackAsync();
            Assert.AreEqual(a, vm.CurrentPath);
            await vm.GoBackAsync();
            Assert.AreEqual(root, vm.CurrentPath);
            Assert.IsFalse(vm.CanGoBack);

            await vm.GoForwardAsync();
            Assert.AreEqual(a, vm.CurrentPath);
            Assert.IsTrue(vm.CanGoForward);

            Directory.Delete(b);
            await vm.GoForwardAsync();
            Assert.AreEqual(a, vm.CurrentPath, "目录没了:照常报错,停在原地。");
            Assert.IsNotNull(vm.ErrorMessage);
            Assert.IsTrue(vm.CanGoForward, "历史原样留着。");
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    // ---- 工具 ----

    /// <summary>远端栏:任何目录都列得出(空目录),只记调用。</summary>
    private static FileBrowserViewModel RemotePane(out ISftpService sftp)
    {
        sftp = Substitute.For<ISftpService>();
        sftp.ListDirectoryAsync(Session, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(new List<RemoteFileInfo>()));
        return new FileBrowserViewModel(sftp, Session) { TransferOptions = new() };
    }
}
