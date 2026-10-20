using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NSubstitute;
using VelaShell.Core.Models;
using VelaShell.Core.Resources;
using VelaShell.Core.Sftp;
using VelaShell.Tests.TestSupport;
using VelaShell.ViewModels;
using VelaShell.Views;

namespace VelaShell.Tests.Views;

/// <summary>目录收藏(#621)的界面:收藏管理窗口自己弹框、自己显示错误、跟着文档一起关;下拉菜单里的路径原样显示。</summary>
[TestClass]
[TestCategory("FileBrowserUi")]
public sealed class DirectoryBookmarksUiTests
{
    private static HeadlessUnitTestSession _session = null!;

    [ClassInitialize]
    public static void Init(TestContext _) =>
        _session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(DirectoryBookmarksUiTests).Assembly);

    /// <summary>
    /// 文档不是当前标签时文档视图已经离开窗口树:管理窗口借它的回调弹框,新增 / 编辑 / 删除就悄无声息地落空。
    /// 管理窗口得以自己为属主弹框,失败提示也得显示在它自己身上。
    /// </summary>
    [TestMethod]
    public Task ManagerWindow_OwnsItsDialogs_AndShowsBookmarkErrors() => _session.RunOnUiAsync(async () =>
    {
        SftpDocumentViewModel vm = NewDocument();
        var window = new DirectoryBookmarksWindow { DataContext = vm };
        window.Show();
        try
        {
            Assert.IsNull(vm.EditBookmarkDialog, "前提:文档视图不在(文档不是当前标签)");
            Button add = window.GetVisualDescendants().OfType<Button>()
                .Single(b => AutomationProperties.GetName(b) == Strings.Get("Bookmark_Add"));
            add.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            MessageDialog dialog = window.OwnedWindows.OfType<MessageDialog>().Single();

            // 名称清空再保存:校验不过的提示要出现在管理窗口里,不只写在(此刻看不见的)文档工具条上。
            dialog.GetVisualDescendants().OfType<TextBox>().First(t => t.Name != "InputBox").Text = "  ";
            dialog.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "ConfirmButton")
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            Dispatcher.UIThread.RunJobs();

            TextBlock error = window.FindControl<TextBlock>("ErrorText")!;
            Assert.AreEqual(Strings.Get("Bookmark_InvalidName"), vm.BookmarkErrorMessage);
            Assert.IsTrue(error.IsVisible);
            Assert.AreEqual(Strings.Get("Bookmark_InvalidName"), error.Text);
        }
        finally
        {
            window.Close();
            await vm.CloseAsync();
        }
    });

    /// <summary>文档关了,管理窗口跟着关:留着它只是一个什么也存不进去、还攥着视图模型的窗口。</summary>
    [TestMethod]
    public Task ManagerWindow_ClosesWithTheDocument() => _session.RunOnUiAsync(async () =>
    {
        SftpDocumentViewModel vm = NewDocument();
        var window = new DirectoryBookmarksWindow { DataContext = vm };
        bool closed = false;
        window.Closed += (_, _) => closed = true;
        window.Show();

        await vm.CloseAsync();
        Dispatcher.UIThread.RunJobs();

        Assert.IsTrue(closed);
    });

    /// <summary>
    /// 下拉菜单里的收藏名和路径原样显示:字符串菜单头按访问键解析,<c>/srv/my_app</c> 会显示成 <c>/srv/myapp</c>。
    /// </summary>
    [TestMethod]
    public Task BookmarkMenu_ShowsUnderscoresLiterally() => _session.RunOnUiAsync(async () =>
    {
        SftpDocumentViewModel vm = NewDocument(new RemoteDirectoryBookmark
        {
            Name = "my_app",
            LocalPath = "/tmp/my_app",
            RemotePath = "/srv/my_app",
        });
        try
        {
            MenuFlyout menu = SftpDocumentView.BuildBookmarkMenu(vm);

            string?[] headers = [.. menu.Items.OfType<MenuItem>().Select(static i => i.Header).OfType<TextBlock>().Select(static t => t.Text)];
            Assert.HasCount(3, headers, "应用、编辑、删除三项的标题都带用户数据,都不能交给访问键解析");
            StringAssert.Contains(headers[0], "/srv/my_app");
            Assert.IsTrue(headers.All(static h => h!.Contains("my_app", StringComparison.Ordinal)));
        }
        finally
        {
            await vm.CloseAsync();
        }
    });

    private static SftpDocumentViewModel NewDocument(params RemoteDirectoryBookmark[] bookmarks)
    {
        ISftpService sftp = Substitute.For<ISftpService>();
        sftp.GetWorkingDirectoryAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns("/home/joe");
        sftp.ListDirectoryAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new List<RemoteFileInfo>()));
        var profile = new SessionProfile
        {
            ConnectionType = ConnectionType.SFTP,
            Name = "files",
            Host = "files.example.com",
            Username = "joe",
            DirectoryBookmarks = [.. bookmarks],
        };
        return new SftpDocumentViewModel(profile, Guid.NewGuid(), static (_, _) => Task.CompletedTask, sftp, new TransferOptions());
    }
}
