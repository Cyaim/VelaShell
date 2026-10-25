using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NSubstitute;
using ReactiveUI.Primitives;
using VelaShell.Core.Localization;
using VelaShell.Core.Models;
using VelaShell.Core.Resources;
using VelaShell.Core.Sftp;
using VelaShell.Localization;
using VelaShell.ViewModels;
using VelaShell.Views;

namespace VelaShell.Tests.Views;

/// <summary>
/// 文件栏头部工具条上的「上一级」(差距分析 A5):远端栏原先只有一个没人绑的 <c>GoUpCommand</c>,
/// 回上级只能双击「..」行;本地栏有按钮却没有提示语。双栏里两颗挨在一起,提示语与读屏名要分得出是哪一栏。
/// </summary>
[TestClass]
[TestCategory("PaneKeyboardUI")]
public sealed class FilePaneToolbarUiTests
{
    private static HeadlessUnitTestSession _session = null!;

    [ClassInitialize]
    public static void Init(TestContext _)
    {
        _session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(FilePaneToolbarUiTests).Assembly);
        LocalizedStrings.Instance.Attach(new LocalizationService());
    }

    [TestMethod]
    public void RemotePane_GoUpButton_NavigatesToTheParentDirectory()
    {
        OnUi(() =>
        {
            ISftpService sftp = Substitute.For<ISftpService>();
            var sessionId = Guid.NewGuid();
            sftp.ListDirectoryAsync(sessionId, Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(new List<RemoteFileInfo>()));
            var vm = new FileBrowserViewModel(sftp, sessionId)
            {
                IsVisible = true,
                TransferOptions = new(),
                CurrentPath = "/home/user/sub",
            };
            var view = new FileBrowserView { DataContext = vm };
            var window = new Window { Width = 900, Height = 400, Content = view };
            try
            {
                window.Show();
                Pump(window);
                Button goUp = view.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "RemoteGoUpButton");
                Assert.IsTrue(goUp.IsEffectivelyVisible, "终端侧栏与文件标签的远端栏都该有这颗按钮。");

                Click(window, goUp);
                PumpWhile(window, () => vm.CurrentPath != "/home/user");

                Assert.AreEqual("/home/user", vm.CurrentPath);
            }
            finally
            {
                window.Close();
                Dispatcher.UIThread.RunJobs();
            }
        });
    }

    [TestMethod]
    public void GoUpButtons_OfTheTwoPanes_HaveDistinctTooltipsAndAccessibleNames()
    {
        OnUi(() =>
        {
            var local = new LocalFilePaneView { DataContext = new LocalFilePaneViewModel(new TransferOptions()) };
            var remote = new FileBrowserView { DataContext = new FileBrowserViewModel(null, Guid.Empty) { IsVisible = true } };
            var window = new Window { Width = 1200, Height = 400, Content = new StackPanel { Children = { local, remote } } };
            try
            {
                window.Show();
                Pump(window);
                Button localUp = local.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "LocalGoUpButton");
                Button remoteUp = remote.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "RemoteGoUpButton");

                Assert.AreEqual(Strings.Get("Sftp_GoUpLocalPane"), ToolTip.GetTip(localUp) as string);
                Assert.AreEqual(Strings.Get("Sftp_GoUpRemotePane"), ToolTip.GetTip(remoteUp) as string);
                Assert.AreEqual(ToolTip.GetTip(localUp), AutomationProperties.GetName(localUp));
                Assert.AreEqual(ToolTip.GetTip(remoteUp), AutomationProperties.GetName(remoteUp));
                Assert.AreNotEqual(AutomationProperties.GetName(localUp), AutomationProperties.GetName(remoteUp));
            }
            finally
            {
                window.Close();
                Dispatcher.UIThread.RunJobs();
                ((LocalFilePaneViewModel)local.DataContext!).Dispose();
            }
        });
    }

    private static void Click(Window window, Control target)
    {
        Point center = target.TranslatePoint(new Point(target.Bounds.Width / 2, target.Bounds.Height / 2), window)!.Value;
        window.MouseDown(center, MouseButton.Left);
        window.MouseUp(center, MouseButton.Left);
        Pump(window);
    }

    private static void Pump(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }

    private static void PumpWhile(Window window, Func<bool> condition)
    {
        for (int i = 0; i < 2000 && condition(); i++)
        {
            Pump(window);
            Thread.Sleep(1);
        }
        Pump(window);
    }

    private static void OnUi(Action body) =>
        _session.Dispatch(() =>
        {
            body();
            return Task.CompletedTask;
        }, CancellationToken.None).GetAwaiter().GetResult();
}
