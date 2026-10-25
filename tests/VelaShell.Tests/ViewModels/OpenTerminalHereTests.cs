using NSubstitute;
using ReactiveUI.Primitives;
using VelaShell.Core.Models;
using VelaShell.Core.Sftp;
using VelaShell.Core.Ssh;
using VelaShell.Docking;
using VelaShell.Presentation.Services;
using VelaShell.Terminal;
using VelaShell.ViewModels;

namespace VelaShell.Tests.ViewModels;

/// <summary>
/// 文件栏右键「在此处打开终端」(维护者 2026-10-25 定,差距分析 C3 的 ①):只对 SSH 会话提供,
/// 开一个连同一配置的终端标签,路径用单引号转义、作为认证后命令 <c>cd</c> 进去 —— 与配置里的「初始目录」、
/// 跟随终端目录的钩子共用同一套静默注入与防回显。
/// </summary>
[TestClass]
[TestCategory("FileBrowser")]
public sealed class OpenTerminalHereTests
{
    // ---- 面板 ----

    [TestMethod]
    public async Task Pane_OpensTheTerminalInTheCurrentFolder_OrInTheRightClickedFolder()
    {
        ISftpService sftp = Substitute.For<ISftpService>();
        var session = Guid.NewGuid();
        sftp.ListDirectoryAsync(session, Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(_ => Task.FromResult(new List<RemoteFileInfo>()));
        var pane = new FileBrowserViewModel(sftp, session) { TransferOptions = new() };
        await pane.NavigateToAsync("/srv/app");
        List<string> asked = [];
        bool raised = false;
        pane.PropertyChanged += (_, e) => raised |= e.PropertyName == nameof(FileBrowserViewModel.CanOpenTerminalHere);
        Assert.IsFalse(pane.CanOpenTerminalHere, "宿主没挂(FTP、插件协议、SFTP 专用配置):右键里没有这一项。");

        pane.OpenTerminalAt = directory =>
        {
            asked.Add(directory);
            return Task.CompletedTask;
        };
        await pane.OpenTerminalHereCommand.Execute(null).FirstAsync();
        await pane.OpenTerminalHereCommand.Execute("/srv/app/logs").FirstAsync();

        Assert.IsTrue(raised && pane.CanOpenTerminalHere);
        Assert.AreSequenceEqual(["/srv/app", "/srv/app/logs"], asked, "空白处右键 = 当前目录;目录行右键 = 那个目录。");
    }

    // ---- 宿主 ----

    [TestMethod]
    public async Task SftpDocument_OfAnSshProfile_OffersIt_ButAnSftpOnlyProfileDoesNot()
    {
        (MainWindowViewModel vm, IConnectionWorkflowService workflow) = Host();
        SessionProfile ssh = Profile("ssh-box", ConnectionType.SSH);
        SessionProfile sftpOnly = Profile("sftp-box", ConnectionType.SFTP);
        workflow.ConnectProfileAsync(ssh, Arg.Any<CancellationToken>()).Returns(Session(ssh));
        workflow.ConnectProfileAsync(sftpOnly, Arg.Any<CancellationToken>()).Returns(Session(sftpOnly));

        SftpDocument? sshDocument = await vm.OpenSftpDocumentForProfileAsync(ssh);
        SftpDocument? sftpDocument = await vm.OpenSftpDocumentForProfileAsync(sftpOnly);

        Assert.IsTrue(sshDocument!.ViewModel.RemoteFiles.CanOpenTerminalHere);
        Assert.IsFalse(sftpDocument!.ViewModel.RemoteFiles.CanOpenTerminalHere, "SFTP 专用配置多半就是没有 shell 的账号。");
        await sshDocument.ViewModel.CloseAsync();
        await sftpDocument.ViewModel.CloseAsync();
    }

    [TestMethod]
    public async Task DualDocument_DecidesPerPane()
    {
        (MainWindowViewModel vm, IConnectionWorkflowService workflow) = Host();
        SessionProfile left = Profile("left-box", ConnectionType.SSH);
        SessionProfile right = Profile("right-box", ConnectionType.SFTP);
        workflow.ConnectProfileAsync(left, Arg.Any<CancellationToken>()).Returns(Session(left));
        workflow.ConnectProfileAsync(right, Arg.Any<CancellationToken>()).Returns(Session(right));

        DualSftpDocument? document = await vm.OpenDualSftpDocumentAsync(left, right);

        Assert.IsTrue(document!.ViewModel.LeftFiles.CanOpenTerminalHere);
        Assert.IsFalse(document.ViewModel.RightFiles.CanOpenTerminalHere);
        await document.ViewModel.CloseAsync();
    }

    [TestMethod]
    public async Task OpenTerminalHere_ForAnSftpOnlyProfile_DoesNotConnect()
    {
        (MainWindowViewModel vm, IConnectionWorkflowService workflow) = Host();

        TerminalTabViewModel? tab = await vm.OpenTerminalHereAsync(Profile("sftp-box", ConnectionType.SFTP), "/srv");

        Assert.IsNull(tab);
        await workflow.DidNotReceive().ConnectProfileAsync(Arg.Any<SessionProfile>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// 新标签认证后静默 cd 进去:路径用单引号裹住、内部的单引号转义(带空格、带引号的目录名不会变成两个参数或一条注入),
    /// 压过配置里的初始目录;只挂在这个标签上,不写进配置。
    /// </summary>
    [TestMethod]
    public async Task OpenTerminalHere_CdsIntoTheFolder_QuotedAsAPostAuthCommand_InsteadOfTheProfilesStartupDirectory()
    {
        IConnectionWorkflowService workflow = Substitute.For<IConnectionWorkflowService>();
        ISshConnectionService sshConnectionService = Substitute.For<ISshConnectionService>();
        ISshClientWrapper sshClient = Substitute.For<ISshClientWrapper>();
        IShellStreamWrapper shellStream = Substitute.For<IShellStreamWrapper>();
        ITerminalEmulator terminal = FakeTerminal.Emulator();
        shellStream.CanRead.Returns(true);
        shellStream.CanWrite.Returns(true);
        shellStream.ReadAsync(Arg.Any<byte[]>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
                   .Returns(new TaskCompletionSource<int>().Task);
        List<string> writes = [];
        var injected = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        shellStream.WriteAsync(Arg.Any<byte[]>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
                   .Returns(call =>
                   {
                       string payload = System.Text.Encoding.UTF8.GetString((byte[])call[0], (int)call[1], (int)call[2]);
                       lock (writes)
                       {
                           writes.Add(payload);
                       }
                       if (payload.Contains("cd '", StringComparison.Ordinal))
                       {
                           injected.TrySetResult(payload);
                       }
                       return Task.CompletedTask;
                   });
        SessionProfile profile = Profile("dev", ConnectionType.SSH);
        profile.Terminal = new TerminalOverrides { StartupDirectory = "/srv/old" };
        SshSession session = Session(profile);
        workflow.ConnectProfileAsync(profile, Arg.Any<CancellationToken>()).Returns(session);
        sshConnectionService.GetClient(session.SessionId).Returns(sshClient);
        sshClient.CreateShellStreamAsync("xterm-256color", 120, 32, 0, 0, 4096,
                                         Arg.Any<IReadOnlyDictionary<TerminalMode, uint>?>(),
                                         Arg.Any<CancellationToken>())
                 .Returns(shellStream);
        var vm = new MainWindowViewModel(workflow, sshConnectionService, () => terminal);

        TerminalTabViewModel? tab = await vm.OpenTerminalHereAsync(profile, "/srv/it's here");

        Assert.IsNotNull(tab);
        Assert.AreEqual("/srv/it's here", tab.StartupDirectoryOverride);
        Assert.AreEqual("/srv/old", profile.Terminal.StartupDirectory, "只挂在标签上,不改配置。");
        // 注入推迟到对端安静下来之后(替身流永远不吐数据,一路等到 3 秒上限),余量给宽。
        string payload = await injected.Task.WaitAsync(TimeSpan.FromSeconds(10));
        StringAssert.Contains(payload, @"cd '/srv/it'\''s here'");
        StringAssert.EndsWith(payload, "\n");
        lock (writes)
        {
            Assert.IsFalse(writes.Any(write => write.Contains("/srv/old", StringComparison.Ordinal)), "配置里的初始目录被这一个压过去,不再 cd 两次。");
        }
    }

    // ---- 工具 ----

    private static (MainWindowViewModel Vm, IConnectionWorkflowService Workflow) Host()
    {
        IConnectionWorkflowService workflow = Substitute.For<IConnectionWorkflowService>();
        ISftpService sftp = Substitute.For<ISftpService>();
        sftp.GetWorkingDirectoryAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns("/home/u");
        sftp.ListDirectoryAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(new List<RemoteFileInfo>()));
        var vm = new MainWindowViewModel(workflow, Substitute.For<ISshConnectionService>(), () => FakeTerminal.Emulator(), sftpService: sftp);
        return (vm, workflow);
    }

    private static SessionProfile Profile(string name, ConnectionType type) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        Host = $"{name}.example.com",
        Port = 22,
        Username = "root",
        AuthMethod = AuthMethod.Password,
        Password = "secret",
        ConnectionType = type,
    };

    private static SshSession Session(SessionProfile profile) => new()
    {
        SessionId = Guid.NewGuid(),
        ConnectionInfo = new()
        {
            Host = profile.Host,
            Port = profile.Port,
            Username = profile.Username,
            AuthMethod = profile.AuthMethod,
            Password = profile.Password,
        },
        Status = SessionStatus.Connected,
    };
}
