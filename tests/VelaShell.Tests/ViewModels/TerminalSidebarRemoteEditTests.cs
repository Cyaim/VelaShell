using NSubstitute;
using ReactiveUI.Builder;
using ReactiveUI.Primitives.Concurrency;
using VelaShell.Core.Models;
using VelaShell.Core.Sftp;
using VelaShell.Docking;
using VelaShell.Services;
using VelaShell.ViewModels;

namespace VelaShell.Tests.ViewModels;

/// <summary>
/// 终端侧栏的文件面板开出来的外部编辑会话,随面板拆掉(标签关闭)一起收掉:
/// 没传完的那一次先传完,再拆 SFTP 通道;别的会话名下的编辑会话不受影响。
/// </summary>
/// <remarks>
/// 以前只有 SFTP / 双栏文件标签关闭时才调 <see cref="RemoteEditSessionManager.CloseScopeAsync" />,
/// 侧栏这一路的 watcher 一直挂到应用退出,对着一个早就没了的会话。
/// </remarks>
[TestClass]
[TestCategory("ExternalEdit")]
[DoNotParallelize]
public sealed class TerminalSidebarRemoteEditTests
{
    static TerminalSidebarRemoteEditTests()
    {
        try
        {
            RxAppBuilder
                .CreateReactiveUIBuilder()
                .WithMainThreadScheduler(CurrentThreadSequencer.Instance)
                .WithCoreServices()
                .BuildApp();
        }
        catch (InvalidOperationException)
        {
            // 别的用例已经初始化过。
        }
    }

    [TestCleanup]
    public void CleanupRemoteEditSessions() => RemoteEditSessionManager.CleanupAll();

    [TestMethod]
    public async Task ClosingTheTerminalTab_FlushesTheLastSave_ThenEndsItsEditSessions_BeforeSftpCloses()
    {
        ISftpService sftp = Substitute.For<ISftpService>();
        var vm = new MainWindowViewModel(sftpService: sftp);
        TerminalDocument document = AddConnectedTab(vm);
        Guid sessionId = document.Terminal.SessionId;
        var events = new List<string>();
        sftp.CloseSessionAsync(sessionId, Arg.Any<CancellationToken>()).Returns(_ =>
        {
            Record(events, "sftp closed");
            return Task.CompletedTask;
        });

        RemoteEditSession session = await OpenAsync(sftp, sessionId, events);
        RemoteEditSession other = await OpenAsync(sftp, Guid.NewGuid(), events);
        await File.WriteAllTextAsync(session.LocalPath, "edited");
        await WaitUntilAsync(() => session.HasPendingChange || Snapshot(events).Count > 0);

        vm.Layout.CloseDocument(document);
        await vm.TerminalCloseTaskFor(document).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.DoesNotContain(session, RemoteEditSessionManager.ActiveSessions, "侧栏面板拆掉了,它名下的编辑会话还挂着");
        Assert.Contains(other, RemoteEditSessionManager.ActiveSessions, "别的会话名下的编辑会话不该被一起收掉");
        Assert.AreSequenceEqual(["uploaded /etc/app.conf", "sftp closed"], Snapshot(events),
            "改完立刻关标签:那一次保存要在拆 SFTP 通道之前传上去");
        Assert.IsFalse(Directory.Exists(Path.GetDirectoryName(session.LocalPath)), "全传上去了就不该留副本");
    }

    /// <summary>没有编辑会话时照旧:驱逐面板、拆 SFTP 通道,一步都不多等。</summary>
    [TestMethod]
    public async Task ClosingATabWithoutEditSessions_StillClosesSftp()
    {
        ISftpService sftp = Substitute.For<ISftpService>();
        var vm = new MainWindowViewModel(sftpService: sftp);
        TerminalDocument document = AddConnectedTab(vm);

        vm.Layout.CloseDocument(document);
        await vm.TerminalCloseTaskFor(document).WaitAsync(TimeSpan.FromSeconds(30));

        await sftp.Received(1).CloseSessionAsync(document.Terminal.SessionId, Arg.Any<CancellationToken>());
    }

    private static TerminalDocument AddConnectedTab(MainWindowViewModel vm)
    {
        var tab = new TerminalTabViewModel(FakeTerminal.Emulator())
        {
            Profile = new() { Name = "one", Host = "one.example" },
            SessionId = Guid.NewGuid(),
            ConnectionStatus = SessionStatus.Connected,
        };
        var document = new TerminalDocument(tab);
        vm.Layout.AddDocument(document);
        return document;
    }

    private static async Task<RemoteEditSession> OpenAsync(ISftpService sftp, Guid sessionId, List<string> events) =>
        await RemoteEditSessionManager.OpenAsync(new()
        {
            SftpService = sftp,
            SessionId = sessionId,
            RemotePath = "/etc/app.conf",
            FileName = "app.conf",
            OpenWith = RemoteEditOpenWith.Nothing,
            DownloadAsync = async (local, token) =>
            {
                await File.WriteAllTextAsync(local, "original", token);
                return true;
            },
            UploadAsync = (_, remote) =>
            {
                Record(events, "uploaded " + remote);
                return Task.CompletedTask;
            },
        }) ?? throw new AssertFailedException("编辑会话没开起来");

    private static void Record(List<string> events, string entry)
    {
        lock (events)
        {
            events.Add(entry);
        }
    }

    private static List<string> Snapshot(List<string> events)
    {
        lock (events)
        {
            return [.. events];
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                Assert.Fail("等了 10 秒,watcher 还没看到那次保存");
            }
            await Task.Delay(20);
        }
    }
}
