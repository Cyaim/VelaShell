using VelaShell.Core.Resources;
using VelaShell.Infrastructure.Plugins;
using VelaShell.ViewModels;

namespace VelaShell.Tests.ViewModels;

/// <summary>插件管理页「查看日志」窗口:读到什么就显示什么,只取了尾部时注明,还没有日志时说清这里会记些什么。</summary>
[TestClass]
public sealed class PluginLogViewModelTests
{
    private const string Path = "/home/u/.velashell/logs/plugins/velashell.redis.log";

    private static PluginLogViewModel Create(PluginLogTail tail, string? directory = "/home/u/.velashell/logs/plugins") =>
        new("velashell.redis", "Redis", () => tail, directory);

    [TestMethod]
    public async Task Refresh_ShowsTheTailAndThePath()
    {
        PluginLogViewModel vm = Create(new("2026-10-24 10:00:00.000 [Information] connected\n", Path, Truncated: false));

        await vm.RefreshAsync();

        Assert.AreEqual("2026-10-24 10:00:00.000 [Information] connected\n", vm.Text);
        Assert.IsFalse(vm.IsEmpty);
        Assert.AreEqual(Path, vm.Subtitle);
        Assert.AreEqual(Strings.Format("PluginLog_Title", "Redis"), vm.Title);
        Assert.IsTrue(vm.CanOpenFolder);
        Assert.IsFalse(vm.IsLoading);
    }

    [TestMethod]
    public async Task Refresh_TruncatedTailSaysHowMuchIsShown()
    {
        PluginLogViewModel vm = Create(new("tail\n", Path, Truncated: true));

        await vm.RefreshAsync();

        Assert.AreEqual(
            Strings.Format("PluginLog_TailOf", TransferItemViewModel.FormatBytes(PluginLogFiles.DefaultTailBytes), Path),
            vm.Subtitle);
    }

    [TestMethod]
    public async Task Refresh_NothingLoggedYetExplainsWhatWillShowUpHere()
    {
        PluginLogViewModel vm = Create(new(string.Empty, Path, Truncated: false));

        await vm.RefreshAsync();

        Assert.IsTrue(vm.IsEmpty);
        Assert.AreEqual(Strings.Get("PluginLog_Empty"), vm.EmptyText);
    }

    /// <summary>没有单独的日志文件(没配,或 id 当不了文件名):指到宿主日志里那几行,也没有文件夹可开。</summary>
    [TestMethod]
    public async Task Refresh_NoOwnFilePointsAtTheApplicationLog()
    {
        PluginLogViewModel vm = Create(new(string.Empty, null, Truncated: false), directory: null);

        await vm.RefreshAsync();

        Assert.IsTrue(vm.IsEmpty);
        Assert.AreEqual(Strings.Format("PluginLog_NotOnDisk", "velashell.redis"), vm.EmptyText);
        Assert.AreEqual(string.Empty, vm.Subtitle);
        Assert.IsFalse(vm.CanOpenFolder);
    }

    /// <summary>刷新读的是最新内容,不是第一次打开时的快照。</summary>
    [TestMethod]
    public async Task Refresh_ReadsAgain()
    {
        int reads = 0;
        PluginLogViewModel vm = new("p", "P", () => new($"read {++reads}\n", Path, false), null);

        await vm.RefreshAsync();
        await vm.RefreshAsync();

        Assert.AreEqual("read 2\n", vm.Text);
    }
}
