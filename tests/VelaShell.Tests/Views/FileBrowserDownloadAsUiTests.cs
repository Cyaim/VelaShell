using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NSubstitute;
using VelaShell.Core.Models;
using VelaShell.Core.Resources;
using VelaShell.Core.Sftp;
using VelaShell.ViewModels;
using VelaShell.Views;

namespace VelaShell.Tests.Views;

/// <summary>
/// 远端行右键的「下载为…」(差距分析 A9):命令与系统另存为对话框早就在,只是没有入口。
/// 只给单个普通文件 —— 目录、".." 与多选时不出现(多选时「下载」作用于整个选区,这一项却只能落一个名字)。
/// </summary>
[TestClass]
[TestCategory("FileBrowserUi")]
public sealed class FileBrowserDownloadAsUiTests
{
    private static HeadlessUnitTestSession _session = null!;

    [ClassInitialize]
    public static void Init(TestContext _) =>
        _session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(FileBrowserDownloadAsUiTests).Assembly);

    [TestMethod]
    public void DownloadAs_ShowsOnlyForASingleRegularFile_AndRunsTheSaveAsDownload()
    {
        OnUi(() =>
        {
            ISftpService sftp = Substitute.For<ISftpService>();
            var vm = new FileBrowserViewModel(sftp, Guid.NewGuid()) { IsVisible = true };
            vm.TransferOptions.TransferLogging = false;
            vm.TransferOptions.NotifyOnComplete = false;
            RemoteFileInfoViewModel log = Row(vm, "app.log", isDirectory: false);
            RemoteFileInfoViewModel other = Row(vm, "other.log", isDirectory: false);
            RemoteFileInfoViewModel folder = Row(vm, "logs", isDirectory: true);
            var window = new Window { Width = 1200, Height = 400, Content = new FileBrowserView { DataContext = vm } };
            try
            {
                window.Show();
                Pump(window);

                // 视图一挂上就把真正的另存为对话框接了进来;这里换成替身,记下建议的文件名。
                string? suggested = null;
                vm.PickSavePathForDownload = name =>
                {
                    suggested = name;
                    return Task.FromResult<string?>(Path.Combine(Path.GetTempPath(), "vela-download-as-ui", name));
                };

                Select(vm, log);
                MenuItem downloadAs = OpenMenuItem(window, log);
                Assert.IsTrue(downloadAs.IsVisible, "单个普通文件:有「下载为…」");
                Assert.AreEqual(Strings.Get("DownloadAs"), downloadAs.Header);
                Assert.AreSame(log, downloadAs.CommandParameter);
                Assert.IsTrue(downloadAs.Command?.CanExecute(downloadAs.CommandParameter));
                downloadAs.Command!.Execute(downloadAs.CommandParameter);
                for (int i = 0; i < 50 && DownloadCalls(sftp) == 0; i++)
                {
                    Pump(window);
                }
                Assert.AreEqual("app.log", suggested, "对话框里预填远端的文件名");
                sftp.Received(1).DownloadFileAsync(
                    vm.SessionId, "/srv/app.log", Path.Combine(Path.GetTempPath(), "vela-download-as-ui", "app.log"),
                    Arg.Any<IProgress<TransferProgress>?>(), 0, Arg.Any<CancellationToken>());
                CloseMenus(window, log);

                Select(vm, folder);
                Assert.IsFalse(OpenMenuItem(window, folder).IsVisible, "目录不给「下载为…」");
                CloseMenus(window, folder);

                Select(vm, log, other);
                Assert.IsFalse(OpenMenuItem(window, log).IsVisible, "多选时不给:「下载」作用于整个选区,这一项只能落一个名字");
                CloseMenus(window, log);
            }
            finally
            {
                window.Close();
                Dispatcher.UIThread.RunJobs();
            }
        });
    }

    private static RemoteFileInfoViewModel Row(FileBrowserViewModel vm, string name, bool isDirectory)
    {
        var row = new RemoteFileInfoViewModel(new RemoteFileInfo
        {
            Name = name,
            FullPath = "/srv/" + name,
            Size = isDirectory ? 4096 : 2048,
            Permissions = isDirectory ? "drwxr-xr-x" : "-rw-r--r--",
            IsDirectory = isDirectory,
            LastModified = DateTime.UtcNow,
            Owner = "deploy",
            Group = "www-data",
        });
        vm.Files.Add(row);
        return row;
    }

    private static void Select(FileBrowserViewModel vm, params RemoteFileInfoViewModel[] rows)
    {
        vm.SelectedFiles.Clear();
        foreach (RemoteFileInfoViewModel row in rows)
        {
            vm.SelectedFiles.Add(row);
        }
    }

    /// <summary>打开某一行的右键菜单(打开了绑定才求值),取出「下载为…」那一项。</summary>
    private static MenuItem OpenMenuItem(Window window, RemoteFileInfoViewModel row)
    {
        Border border = RowBorder(window, row);
        ContextMenu menu = border.ContextMenu!;
        menu.Open(border);
        Pump(window);
        return menu.Items.OfType<MenuItem>().Single(item => item.Name == "DownloadAsMenuItem");
    }

    private static void CloseMenus(Window window, RemoteFileInfoViewModel row)
    {
        RowBorder(window, row).ContextMenu!.Close();
        Pump(window);
    }

    private static Border RowBorder(Window window, RemoteFileInfoViewModel row) =>
        window.GetVisualDescendants().OfType<Border>()
            .Single(border => border.Classes.Contains("file-row") && ReferenceEquals(border.DataContext, row));

    private static int DownloadCalls(ISftpService sftp) =>
        sftp.ReceivedCalls().Count(call => call.GetMethodInfo().Name == nameof(ISftpService.DownloadFileAsync));

    private static void Pump(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
    }

    private static void OnUi(Action body) =>
        _session.Dispatch(() =>
        {
            body();
            return Task.CompletedTask;
        }, CancellationToken.None).GetAwaiter().GetResult();
}
