using Avalonia.Controls;
using Avalonia.Headless;
using NSubstitute;
using VelaShell.Core.Layouts;
using VelaShell.Core.Models;
using VelaShell.Core.Sftp;
using VelaShell.Docking;
using VelaShell.Docking.Model;
using VelaShell.PluginSdk.Protocols;
using VelaShell.PluginSdk.Workspaces;
using VelaShell.Services;
using VelaShell.Services.Layouts;
using VelaShell.Tests.TestSupport;
using VelaShell.ViewModels;

namespace VelaShell.Tests.ViewModels;

/// <summary>
/// 真实的停靠文档在模板里记成什么:同一条 SSH 配置的终端与独立 SFTP 要分得开,
/// 「连接中」占位按它连上之后的样子记,插件面板记不下来;以及退出时哪些算「在线」。
/// </summary>
[TestClass]
[TestCategory("Docking")]
public sealed class LayoutDocumentDescriberTests
{
    private static HeadlessUnitTestSession _session = null!;

    [ClassInitialize]
    public static void Initialize(TestContext _) =>
        _session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(LayoutDocumentDescriberTests).Assembly);

    private static SessionProfile Profile(string name, ConnectionType type = ConnectionType.SSH) =>
        new() { Name = name, Host = $"{name}.example", ConnectionType = type, Username = "root" };

    private static LayoutDocument? Describe(DockDocument document, bool workspace = false) =>
        LayoutDocumentDescriber.Describe(document, _ => workspace);

    [TestMethod]
    public void Terminals_RecordTheProfileOrTheShell()
    {
        SessionProfile ssh = Profile("web");
        var remote = new TerminalDocument(new TerminalTabViewModel(FakeTerminal.Emulator()) { Profile = ssh });
        var local = new TerminalDocument(new TerminalTabViewModel(FakeTerminal.Emulator())
        {
            LocalShell = new LocalShellInfo("wsl", "WSL", "wsl.exe"),
        });

        LayoutDocument terminal = Describe(remote)!;
        LayoutDocument shell = Describe(local)!;

        Assert.AreEqual(LayoutDocumentKind.Terminal, terminal.Kind);
        Assert.AreEqual(ssh.Id, terminal.ProfileId);
        Assert.AreEqual(LayoutDocumentKind.LocalShell, shell.Kind);
        Assert.AreEqual("wsl", shell.ShellId);
        Assert.IsNull(shell.ProfileId);
    }

    [TestMethod]
    public void ATerminalWithNeitherProfileNorShell_CannotBeReopened() =>
        Assert.IsNull(Describe(new TerminalDocument(new TerminalTabViewModel(FakeTerminal.Emulator()))));

    [TestMethod]
    public void ConnectingPlaceholders_AreRecordedAsWhatTheyWillBecome()
    {
        SessionProfile ssh = Profile("files-over-ssh");
        SessionProfile redis = Profile("cache", ConnectionType.Plugin);
        SessionProfile right = Profile("right", ConnectionType.FTP);

        Assert.AreEqual(LayoutDocumentKind.Files, Describe(new ConnectingDocument(ssh, "SFTP"))!.Kind,
            "终端标签不经过占位:SSH 配置的占位只会是独立 SFTP");
        Assert.AreEqual(LayoutDocumentKind.Workspace, Describe(new ConnectingDocument(redis, "Redis"), workspace: true)!.Kind);
        Assert.AreEqual(LayoutDocumentKind.Files, Describe(new ConnectingDocument(redis, "S3"), workspace: false)!.Kind);
        LayoutDocument dual = Describe(new ConnectingDocument(ssh, "SFTP", "a ⇄ b") { SecondaryProfile = right })!;
        Assert.AreEqual(LayoutDocumentKind.DualFiles, dual.Kind);
        Assert.AreEqual(ssh.Id, dual.ProfileId);
        Assert.AreEqual(right.Id, dual.SecondProfileId);
    }

    [TestMethod]
    public async Task FileDocumentsWorkspacesAndPanels() => await _session.RunOnUiAsync(() =>
    {
        ISftpService sftp = Substitute.For<ISftpService>();
        sftp.GetWorkingDirectoryAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult("/"));
        sftp.ListDirectoryAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new List<RemoteFileInfo>()));
        SessionProfile ssh = Profile("web");
        var session = new SshSession
        {
            SessionId = Guid.NewGuid(),
            ConnectionInfo = new() { Host = "web.example", Port = 22, Username = "root", AuthMethod = AuthMethod.Password },
            Status = SessionStatus.Connected,
        };
        var sftpDocument = new SftpDocument(new SftpDocumentViewModel(
            ssh, session, Substitute.For<VelaShell.Presentation.Services.IConnectionWorkflowService>(), sftp, new TransferOptions()));
        SessionProfile left = Profile("left", ConnectionType.SFTP), right = Profile("right", ConnectionType.FTP);
        var dual = new DualSftpDocument(new DualSftpDocumentViewModel(
            new(left, Guid.NewGuid(), null, (_, _) => Task.CompletedTask),
            new(right, Guid.NewGuid(), null, (_, _) => Task.CompletedTask),
            sftp,
            new TransferOptions()));
        SessionProfile redis = Profile("cache", ConnectionType.Plugin);
        IWorkspaceDocument offline = Substitute.For<IWorkspaceDocument>();
        offline.Status.Returns(new WorkspaceStatus(ProtocolSessionState.Closed));
        IWorkspaceDocument online = Substitute.For<IWorkspaceDocument>();
        online.Status.Returns(new WorkspaceStatus(ProtocolSessionState.Connected));
        var workspace = new PluginWorkspaceDocument(redis, Guid.NewGuid(), "Redis", offline);
        var connectedWorkspace = new PluginWorkspaceDocument(redis, Guid.NewGuid(), "Redis", online);
        var panel = new PluginDocument("ai", "AI", "velashell.ai", new TextBlock());

        LayoutDocument files = Describe(sftpDocument)!;
        Assert.AreEqual(LayoutDocumentKind.Files, files.Kind, "SSH 配置开的独立 SFTP 记成文件标签,不是终端");
        Assert.AreEqual(ssh.Id, files.ProfileId);
        LayoutDocument dualFiles = Describe(dual)!;
        Assert.AreEqual(LayoutDocumentKind.DualFiles, dualFiles.Kind);
        Assert.AreEqual((left.Id, right.Id), (dualFiles.ProfileId!.Value, dualFiles.SecondProfileId!.Value));
        Assert.AreEqual(LayoutDocumentKind.Workspace, Describe(workspace)!.Kind);
        Assert.IsNull(Describe(panel), "插件面板是插件自己建的控件,宿主没有重新打开它的契约");

        Assert.IsTrue(LayoutDocumentDescriber.IsLive(sftpDocument));
        Assert.IsTrue(LayoutDocumentDescriber.IsLive(dual));
        Assert.IsFalse(LayoutDocumentDescriber.IsLive(workspace), "插件没报已连接的工作台不算在线");
        Assert.IsTrue(LayoutDocumentDescriber.IsLive(connectedWorkspace));
        Assert.IsFalse(LayoutDocumentDescriber.IsLive(panel));
        return Task.CompletedTask;
    });

    [TestMethod]
    public void IsLive_FollowsTheTerminalConnectionAndSkipsPlaceholders()
    {
        var connected = new TerminalDocument(new TerminalTabViewModel(FakeTerminal.Emulator())
        {
            Profile = Profile("up"),
            ConnectionStatus = SessionStatus.Connected,
        });
        var dropped = new TerminalDocument(new TerminalTabViewModel(FakeTerminal.Emulator())
        {
            Profile = Profile("down"),
            ConnectionStatus = SessionStatus.Disconnected,
        });

        Assert.IsTrue(LayoutDocumentDescriber.IsLive(connected));
        Assert.IsFalse(LayoutDocumentDescriber.IsLive(dropped));
        Assert.IsFalse(LayoutDocumentDescriber.IsLive(new ConnectingDocument(Profile("x"), "SFTP")),
            "还在连 / 失败卡片:下次启动不替用户去连");
    }
}
