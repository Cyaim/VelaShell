using NSubstitute;
using ReactiveUI.Primitives;
using VelaShell.Core.Models;
using VelaShell.Core.Sftp;
using VelaShell.ViewModels;

namespace VelaShell.Tests.ViewModels;

/// <summary>
/// 文件栏头部的当前目录过滤(维护者 2026-10-25 定):可收起,只过滤显示,换目录时清空。
/// 两栏同一口径 —— 按「名字含有」不分大小写,".." 行留着;藏起来的行不在可见列表里,也就不在选区里。
/// </summary>
[TestClass]
[TestCategory("LocalFilePane")]
public sealed class FilePaneFilterTests
{
    private static readonly Guid Session = Guid.NewGuid();

    // ---- 远端栏 ----

    [TestMethod]
    public async Task Remote_Filter_KeepsOnlyMatchingNames_IgnoringCase_AndKeepsTheParentRow()
    {
        FileBrowserViewModel vm = await RemotePaneAsync();

        vm.FilterText = "LOG";

        Assert.AreSequenceEqual(["..", "app.log", "error.LOG"], [.. vm.Files.Select(f => f.Name)]);
    }

    [TestMethod]
    public async Task Remote_Filter_DropsHiddenRowsFromTheSelection()
    {
        FileBrowserViewModel vm = await RemotePaneAsync();
        vm.SelectedFiles.Add(vm.Files.Single(f => f.Name == "app.log"));
        vm.SelectedFiles.Add(vm.Files.Single(f => f.Name == "readme.md"));

        vm.FilterText = "log";

        Assert.AreSequenceEqual(["app.log"], [.. vm.SelectedFiles.Select(f => f.Name)],
                                "藏起来的 readme.md 不能留在选区里 —— 下载、删除都按选区来。");
    }

    [TestMethod]
    public async Task Remote_Filter_IsClearedWhenTheDirectoryChanges_ButTheBoxStaysOpen()
    {
        FileBrowserViewModel vm = await RemotePaneAsync();
        vm.IsFilterOpen = true;
        vm.FilterText = "log";

        await vm.NavigateToAsync("/srv/sub");

        Assert.AreEqual(string.Empty, vm.FilterText);
        Assert.IsTrue(vm.IsFilterOpen, "框照旧开着,等着输新的条件。");
        Assert.AreSequenceEqual(["..", "inner.txt"], [.. vm.Files.Select(f => f.Name)]);
    }

    [TestMethod]
    public async Task Remote_Filter_SurvivesARefreshOfTheSameDirectory()
    {
        FileBrowserViewModel vm = await RemotePaneAsync();
        vm.FilterText = "log";

        await vm.NavigateToAsync("/srv");

        Assert.AreEqual("log", vm.FilterText);
        Assert.AreSequenceEqual(["..", "app.log", "error.LOG"], [.. vm.Files.Select(f => f.Name)]);
    }

    [TestMethod]
    public async Task Remote_ClosingTheFilterBox_ClearsTheFilter()
    {
        FileBrowserViewModel vm = await RemotePaneAsync();
        vm.IsFilterOpen = true;
        vm.FilterText = "log";

        vm.IsFilterOpen = false;

        Assert.AreEqual(string.Empty, vm.FilterText, "收起来的框还在暗中藏行,只会让人以为文件丢了。");
        Assert.HasCount(1 + 4, vm.Files);
    }

    [TestMethod]
    public async Task Remote_RevealingARowTheFilterHides_ClearsTheFilter()
    {
        FileBrowserViewModel vm = await RemotePaneAsync();
        vm.FilterText = "log";

        bool selected = await vm.RevealAsync("/srv/readme.md", isDirectory: false);

        Assert.IsTrue(selected, "搜索结果点中的那一行被过滤挡着,等于没定位。");
        Assert.AreEqual(string.Empty, vm.FilterText);
    }

    [TestMethod]
    public async Task DualCompare_ClearsBothFiltersFirst()
    {
        ISftpService sftp = Substitute.For<ISftpService>();
        var left = Guid.NewGuid();
        var right = Guid.NewGuid();
        DateTime t = new(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);
        sftp.GetWorkingDirectoryAsync(left, Arg.Any<CancellationToken>()).Returns("/l");
        sftp.GetWorkingDirectoryAsync(right, Arg.Any<CancellationToken>()).Returns("/r");
        sftp.ListDirectoryAsync(left, "/l", Arg.Any<CancellationToken>()).Returns([Entry("/l/same.txt", t), Entry("/l/only-left.txt", t)]);
        sftp.ListDirectoryAsync(right, "/r", Arg.Any<CancellationToken>()).Returns([Entry("/r/same.txt", t)]);
        var vm = new DualSftpDocumentViewModel(
            new(Profile("alpha"), left, null, (_, _) => Task.CompletedTask),
            new(Profile("beta"), right, null, (_, _) => Task.CompletedTask),
            sftp,
            new TransferOptions());
        await vm.InitialLoadTask;
        vm.LeftFiles.FilterText = "same";

        await vm.CompareDirectoriesCommand.Execute().FirstAsync();

        Assert.AreEqual(string.Empty, vm.LeftFiles.FilterText, "比较的是整个目录,过滤挡着的行也得算进去。");
        Assert.AreSequenceEqual(["only-left.txt"], [.. vm.LeftFiles.SelectedFiles.Select(f => f.Name)]);
        await vm.CloseAsync();
    }

    // ---- 本地栏 ----

    [TestMethod]
    public async Task Local_Filter_KeepsOnlyMatchingNames_AndSortingWhileFilteredLosesNothing()
    {
        using TempTree temp = new("app.log", "error.LOG", "readme.md", "notes.txt");
        using var vm = new LocalFilePaneViewModel(new TransferOptions());
        await vm.NavigateToAsync(temp.Path);

        vm.FilterText = "log";
        Assert.AreSequenceEqual(["app.log", "error.LOG"], Names(vm));

        // 排序要从完整列举重排:从眼前的行重排,藏起来的那两个就再也回不来了。
        vm.SortCommand.Execute("size").Subscribe();
        vm.FilterText = string.Empty;

        Assert.AreSequenceEqual(["app.log", "error.LOG", "notes.txt", "readme.md"], Names(vm), SequenceOrder.InAnyOrder);
    }

    [TestMethod]
    public async Task Local_Filter_IsClearedWhenTheDirectoryChanges()
    {
        using TempTree temp = new("app.log", "sub/inner.txt");
        using var vm = new LocalFilePaneViewModel(new TransferOptions());
        await vm.NavigateToAsync(temp.Path);
        vm.IsFilterOpen = true;
        vm.FilterText = "log";

        await vm.NavigateToAsync(Path.Combine(temp.Path, "sub"));

        Assert.AreEqual(string.Empty, vm.FilterText);
        Assert.IsTrue(vm.IsFilterOpen);
        Assert.AreSequenceEqual(["inner.txt"], Names(vm));
    }

    [TestMethod]
    public async Task Local_Filter_DropsHiddenRowsFromTheSelection_AndClosingClearsIt()
    {
        using TempTree temp = new("app.log", "readme.md");
        using var vm = new LocalFilePaneViewModel(new TransferOptions());
        await vm.NavigateToAsync(temp.Path);
        vm.SelectedEntries.Add(vm.Entries.Single(e => e.Name == "app.log"));
        vm.SelectedEntries.Add(vm.Entries.Single(e => e.Name == "readme.md"));
        vm.IsFilterOpen = true;

        vm.FilterText = "log";
        Assert.AreSequenceEqual(["app.log"], [.. vm.SelectedEntries.Select(e => e.Name)], "上传、删除都按选区来。");

        vm.IsFilterOpen = false;
        Assert.AreEqual(string.Empty, vm.FilterText);
        Assert.AreSequenceEqual(["app.log", "readme.md"], Names(vm), SequenceOrder.InAnyOrder);
    }

    // ---- 工具 ----

    private static string[] Names(LocalFilePaneViewModel vm) => [.. vm.Entries.Where(e => !e.IsParentEntry).Select(e => e.Name)];

    private static async Task<FileBrowserViewModel> RemotePaneAsync()
    {
        ISftpService sftp = Substitute.For<ISftpService>();
        DateTime t = new(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);
        sftp.ListDirectoryAsync(Session, "/srv", Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult<List<RemoteFileInfo>>(
                [Entry("/srv/sub", t, directory: true), Entry("/srv/app.log", t), Entry("/srv/error.LOG", t), Entry("/srv/readme.md", t)]));
        sftp.ListDirectoryAsync(Session, "/srv/sub", Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult<List<RemoteFileInfo>>([Entry("/srv/sub/inner.txt", t)]));
        var vm = new FileBrowserViewModel(sftp, Session) { TransferOptions = new() };
        await vm.NavigateToAsync("/srv");
        return vm;
    }

    private static RemoteFileInfo Entry(string path, DateTime modified, bool directory = false) => new()
    {
        Name = path[(path.LastIndexOf('/') + 1)..],
        FullPath = path,
        Size = 3,
        IsDirectory = directory,
        Permissions = directory ? "drwxr-xr-x" : "-rw-r--r--",
        LastModified = modified,
        Owner = "u",
        Group = "u",
    };

    private static SessionProfile Profile(string name) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        Host = $"{name}.example.com",
        Username = "u",
        ConnectionType = ConnectionType.SSH,
    };

    /// <summary>临时目录,按给定的相对路径建空文件(带 / 的先建目录)。</summary>
    private sealed class TempTree : IDisposable
    {
        public TempTree(params string[] files)
        {
            Path = System.IO.Path.GetFullPath(System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"vela-filter-{Guid.NewGuid():N}"));
            foreach (string file in files)
            {
                string full = System.IO.Path.Combine(Path, file);
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
                File.WriteAllText(full, file);
            }
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
