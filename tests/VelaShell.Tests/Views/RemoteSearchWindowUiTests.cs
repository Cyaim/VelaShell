using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
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

/// <summary>远端搜索窗口:回车即搜、结果进列表、定位按钮把面板带过去;关窗即停下还在跑的搜索。</summary>
[TestClass]
[TestCategory("FileBrowserUi")]
public sealed class RemoteSearchWindowUiTests
{
    private static HeadlessUnitTestSession _session = null!;

    [ClassInitialize]
    public static void Init(TestContext _) =>
        _session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(RemoteSearchWindowUiTests).Assembly);

    [TestMethod]
    public Task EnterSearches_AndTheRevealButtonLocatesTheFile() => _session.RunOnUiAsync(async () =>
    {
        (FileBrowserViewModel pane, IRemoteSearchService search) = NewPane();
        search.SearchAsync(Arg.Any<Guid>(), Arg.Any<RemoteSearchQuery>(), Arg.Any<Func<RemoteSearchHit, ValueTask>>(), Arg.Any<CancellationToken>())
              .Returns(async call =>
              {
                  await call.Arg<Func<RemoteSearchHit, ValueTask>>()(new("/srv/app/nginx.conf", false));
                  return new RemoteSearchOutcome(RemoteSearchStatus.Completed, 1);
              });
        using var vm = new RemoteSearchViewModel(search, pane);
        var window = new RemoteSearchWindow { DataContext = vm };
        window.Show();
        try
        {
            TextBox name = window.FindControl<TextBox>("NameBox")!;
            name.Text = "nginx";
            name.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter, Source = name });
            await WaitUntilAsync(() => !vm.IsSearching && vm.Results.Count == 1);

            Assert.AreEqual(Strings.Format("Sftp_SearchDone", 1), vm.StatusText);
            Button reveal = window.GetVisualDescendants().OfType<Button>()
                .Single(b => AutomationProperties.GetName(b) == Strings.Get("Sftp_SearchReveal"));
            reveal.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitUntilAsync(() => pane.SelectedFiles.Count == 1);

            Assert.AreEqual("/srv/app", pane.CurrentPath);
            Assert.AreEqual("/srv/app/nginx.conf", pane.SelectedFiles.Single().FullPath);
        }
        finally
        {
            window.Close();
        }
    });

    [TestMethod]
    public Task ClosingTheWindow_StopsTheSearch() => _session.RunOnUiAsync(async () =>
    {
        (FileBrowserViewModel pane, IRemoteSearchService search) = NewPane();
        CancellationToken seen = default;
        search.SearchAsync(Arg.Any<Guid>(), Arg.Any<RemoteSearchQuery>(), Arg.Any<Func<RemoteSearchHit, ValueTask>>(), Arg.Any<CancellationToken>())
              .Returns(async call =>
              {
                  seen = call.Arg<CancellationToken>();
                  await Task.Delay(Timeout.Infinite, seen).ContinueWith(static _ => { }, TaskScheduler.Default);
                  return new RemoteSearchOutcome(RemoteSearchStatus.Cancelled, 0);
              });
        var vm = new RemoteSearchViewModel(search, pane) { NamePattern = "*.log" };
        var window = new RemoteSearchWindow { DataContext = vm };
        window.Show();
        Task running = vm.SearchAsync();
        await WaitUntilAsync(() => seen.CanBeCanceled);

        window.Close();
        await running.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.IsTrue(seen.IsCancellationRequested, "关窗要停下远端的 find");
    });

    private static (FileBrowserViewModel Pane, IRemoteSearchService Search) NewPane()
    {
        var session = Guid.NewGuid();
        ISftpService sftp = Substitute.For<ISftpService>();
        sftp.ListDirectoryAsync(session, "/srv/app", Arg.Any<CancellationToken>()).Returns(Task.FromResult(new List<RemoteFileInfo>
        {
            new()
            {
                Name = "nginx.conf", FullPath = "/srv/app/nginx.conf", IsDirectory = false, Size = 10,
                Permissions = "rw-r--r--", LastModified = new DateTime(2026, 10, 1, 8, 0, 0, DateTimeKind.Utc),
                Owner = "deploy", Group = "deploy",
            },
        }));
        IRemoteSearchService search = Substitute.For<IRemoteSearchService>();
        search.IsAvailable(session).Returns(true);
        var pane = new FileBrowserViewModel(sftp, session) { CurrentPath = "/srv", RemoteSearch = search, ServerDisplayName = "web-01" };
        return (pane, search);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!condition())
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(10, timeout.Token);
        }
    }
}
