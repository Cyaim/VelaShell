using NSubstitute;
using ReactiveUI.Primitives;
using VelaShell.Core.Models;
using VelaShell.Core.Resources;
using VelaShell.Core.Sftp;
using VelaShell.ViewModels;

namespace VelaShell.Tests.ViewModels;

/// <summary>远端搜索窗口的视图模型,以及文件面板按结果定位(<see cref="FileBrowserViewModel.RevealAsync" />)。</summary>
[TestClass]
[TestCategory("FileBrowser")]
public sealed class RemoteSearchViewModelTests
{
    private readonly Guid _session = Guid.NewGuid();
    private readonly ISftpService _sftp = Substitute.For<ISftpService>();
    private readonly IRemoteSearchService _search = Substitute.For<IRemoteSearchService>();
    private readonly FileBrowserViewModel _pane;

    public RemoteSearchViewModelTests()
    {
        _pane = new(_sftp, _session);
        _sftp.ListDirectoryAsync(_session, "/srv/app", Arg.Any<CancellationToken>()).Returns(Task.FromResult(new List<RemoteFileInfo>
        {
            Entry("logs", isDirectory: true),
            Entry("nginx.conf", isDirectory: false),
            Entry(".env", isDirectory: false),
        }));
        _sftp.ListDirectoryAsync(_session, "/srv/app/logs", Arg.Any<CancellationToken>()).Returns(Task.FromResult(new List<RemoteFileInfo>()));
    }

    [TestMethod]
    public void CanSearchRemote_NeedsAServiceThatHasAnSshConnection()
    {
        bool raised = false;
        _pane.PropertyChanged += (_, e) => raised |= e.PropertyName == nameof(FileBrowserViewModel.CanSearchRemote);
        Assert.IsFalse(_pane.CanSearchRemote, "FTP / 插件协议的面板没有挂搜索服务");

        _search.IsAvailable(_session).Returns(false);
        _pane.RemoteSearch = _search;
        Assert.IsTrue(raised);
        Assert.IsFalse(_pane.CanSearchRemote);

        _search.IsAvailable(_session).Returns(true);
        Assert.IsTrue(_pane.CanSearchRemote);
    }

    [TestMethod]
    public async Task OpenRemoteSearchCommand_HandsTheRootToTheView()
    {
        _search.IsAvailable(_session).Returns(true);
        _pane.RemoteSearch = _search;
        var roots = new List<string?>();
        _pane.ShowRemoteSearch = root =>
        {
            roots.Add(root);
            return Task.CompletedTask;
        };

        await _pane.OpenRemoteSearchCommand.Execute(null).FirstAsync();
        await _pane.OpenRemoteSearchCommand.Execute("/srv/app/logs").FirstAsync();

        Assert.AreSequenceEqual([null, "/srv/app/logs"], roots);
    }

    [TestMethod]
    public async Task Reveal_File_OpensItsDirectoryAndSelectsIt()
    {
        RemoteFileInfoViewModel? revealed = null;
        _pane.RevealRequested += (_, row) => revealed = row;

        bool selected = await _pane.RevealAsync("/srv/app/nginx.conf", isDirectory: false);

        Assert.IsTrue(selected);
        Assert.AreEqual("/srv/app", _pane.CurrentPath);
        Assert.AreEqual("/srv/app/nginx.conf", _pane.SelectedFiles.Single().FullPath);
        Assert.AreSame(_pane.SelectedFiles.Single(), revealed);
    }

    /// <summary>点文件被隐藏着:只在这个面板上打开「显示隐藏文件」(不写回设置),否则定位过去也看不见。</summary>
    [TestMethod]
    public async Task Reveal_HiddenFile_ShowsHiddenFilesOnThisPaneOnly()
    {
        bool persisted = false;
        _pane.ShowHiddenFilesToggled = _ => persisted = true;
        Assert.IsFalse(_pane.ShowHiddenFiles);

        bool selected = await _pane.RevealAsync("/srv/app/.env", isDirectory: false);

        Assert.IsTrue(selected);
        Assert.IsTrue(_pane.ShowHiddenFiles);
        Assert.IsFalse(persisted, "为一次定位改掉全局设置太越权");
        Assert.AreEqual("/srv/app/.env", _pane.SelectedFiles.Single().FullPath);
    }

    [TestMethod]
    public async Task Reveal_Directory_GoesIntoIt()
    {
        bool selected = await _pane.RevealAsync("/srv/app/logs", isDirectory: true);

        Assert.IsFalse(selected);
        Assert.AreEqual("/srv/app/logs", _pane.CurrentPath);
    }

    [TestMethod]
    public async Task Reveal_FileThatIsGone_SelectsNothing()
    {
        bool selected = await _pane.RevealAsync("/srv/app/deleted.txt", isDirectory: false);

        Assert.IsFalse(selected);
        Assert.AreEqual("/srv/app", _pane.CurrentPath);
        Assert.IsEmpty(_pane.SelectedFiles);
    }

    [TestMethod]
    public async Task Search_StreamsResultsAndReportsTheCount()
    {
        _pane.CurrentPath = "/srv";
        RemoteSearchQuery? asked = null;
        _search.SearchAsync(_session, Arg.Any<RemoteSearchQuery>(), Arg.Any<Func<RemoteSearchHit, ValueTask>>(), Arg.Any<CancellationToken>())
               .Returns(async call =>
               {
                   asked = call.Arg<RemoteSearchQuery>();
                   Func<RemoteSearchHit, ValueTask> onHit = call.Arg<Func<RemoteSearchHit, ValueTask>>();
                   await onHit(new("/srv/app/logs", true));
                   await onHit(new("/srv/app/nginx.conf", false));
                   return new RemoteSearchOutcome(RemoteSearchStatus.Completed, 2);
               });
        using RemoteSearchViewModel vm = Create();
        Assert.AreEqual("/srv", vm.Root, "起点是面板当前的目录");
        vm.NamePattern = "nginx";
        vm.ContentText = "listen";
        vm.IgnoreCase = false;

        await vm.SearchCommand.Execute().FirstAsync();

        Assert.AreEqual(new RemoteSearchQuery("/srv", "nginx", "listen", IgnoreCase: false), asked);
        Assert.HasCount(2, vm.Results);
        Assert.IsTrue(vm.Results[0].IsDirectory);
        Assert.AreEqual("nginx.conf", vm.Results[1].Name);
        Assert.AreEqual("/srv/app", vm.Results[1].Directory);
        Assert.AreEqual(Strings.Format("Sftp_SearchDone", 2), vm.StatusText);
        Assert.IsFalse(vm.IsStatusWarning);
        Assert.IsFalse(vm.IsSearching);

        await vm.RevealCommand.Execute(vm.Results[1]).FirstAsync();
        Assert.AreEqual("/srv/app/nginx.conf", _pane.SelectedFiles.Single().FullPath);
    }

    [TestMethod]
    [DataRow(RemoteSearchStatus.Truncated, "Sftp_SearchTruncated")]
    [DataRow(RemoteSearchStatus.NotPosix, "Sftp_SearchNotPosix")]
    [DataRow(RemoteSearchStatus.ShellUnknown, "Sftp_SearchShellUnknown")]
    [DataRow(RemoteSearchStatus.ToolMissing, "Sftp_SearchToolMissing")]
    [DataRow(RemoteSearchStatus.NoSession, "Sftp_SearchNoSession")]
    public async Task Search_EachWayOfEndingSaysItsOwnThing(RemoteSearchStatus status, string key)
    {
        _search.SearchAsync(_session, Arg.Any<RemoteSearchQuery>(), Arg.Any<Func<RemoteSearchHit, ValueTask>>(), Arg.Any<CancellationToken>())
               .Returns(new RemoteSearchOutcome(status, 500));
        using RemoteSearchViewModel vm = Create();
        vm.Root = "/";
        vm.NamePattern = "*";

        await vm.SearchCommand.Execute().FirstAsync();

        string expected = status == RemoteSearchStatus.Truncated ? Strings.Format(key, 500) : Strings.Get(key);
        Assert.AreEqual(expected, vm.StatusText);
        Assert.IsTrue(vm.IsStatusWarning);
    }

    [TestMethod]
    [DataRow("srv", "x", null, "Sftp_SearchRootNotAbsolute")]
    [DataRow("/srv", " ", null, "Sftp_SearchNeedCriteria")]
    public async Task Search_InvalidInput_IsRejectedWithoutTouchingTheServer(string root, string? name, string? content, string key)
    {
        using RemoteSearchViewModel vm = Create();
        vm.Root = root;
        vm.NamePattern = name;
        vm.ContentText = content;

        await vm.SearchCommand.Execute().FirstAsync();

        Assert.AreEqual(Strings.Get(key), vm.StatusText);
        await _search.DidNotReceive().SearchAsync(
            Arg.Any<Guid>(), Arg.Any<RemoteSearchQuery>(), Arg.Any<Func<RemoteSearchHit, ValueTask>>(), Arg.Any<CancellationToken>());
    }

    /// <summary>停止 = 取消服务那一次调用;已经找到的留着,状态行说「已停止」。关窗(Dispose)同样会停。</summary>
    [TestMethod]
    public async Task Stop_CancelsTheRunningSearch_AndKeepsWhatWasFound()
    {
        var started = new TaskCompletionSource();
        _search.SearchAsync(_session, Arg.Any<RemoteSearchQuery>(), Arg.Any<Func<RemoteSearchHit, ValueTask>>(), Arg.Any<CancellationToken>())
               .Returns(async call =>
               {
                   await call.Arg<Func<RemoteSearchHit, ValueTask>>()(new("/srv/a.log", false));
                   started.SetResult();
                   try
                   {
                       await Task.Delay(Timeout.Infinite, call.Arg<CancellationToken>());
                   }
                   catch (OperationCanceledException)
                   {
                   }
                   return new RemoteSearchOutcome(RemoteSearchStatus.Cancelled, 1);
               });
        RemoteSearchViewModel vm = Create();
        vm.NamePattern = "*.log";

        Task search = vm.SearchAsync();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.IsTrue(vm.IsSearching);
        vm.Dispose();
        await search.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.IsFalse(vm.IsSearching);
        Assert.HasCount(1, vm.Results);
        Assert.AreEqual(Strings.Format("Sftp_SearchCancelled", 1), vm.StatusText);
    }

    private static RemoteFileInfo Entry(string name, bool isDirectory) => new()
    {
        Name = name,
        FullPath = "/srv/app/" + name,
        IsDirectory = isDirectory,
        Size = isDirectory ? 4096 : 10,
        Permissions = isDirectory ? "rwxr-xr-x" : "rw-r--r--",
        LastModified = new DateTime(2026, 10, 1, 8, 0, 0, DateTimeKind.Utc),
        Owner = "deploy",
        Group = "deploy",
    };

    private RemoteSearchViewModel Create() => new(_search, _pane, action =>
    {
        action();
        return Task.CompletedTask;
    });
}
