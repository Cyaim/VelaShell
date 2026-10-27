using Avalonia.Headless;
using NSubstitute;
using VelaShell.Core.Models;
using VelaShell.Core.Resources;
using VelaShell.Core.Ssh;
using VelaShell.Presentation.Services;
using VelaShell.Tests.TestSupport;
using VelaShell.ViewModels;

namespace VelaShell.Tests.ViewModels;

[TestClass]
public sealed class InteractiveAuthFlowTests
{
    private static SshSession CreateSession(SessionProfile profile) =>
        new()
        {
            SessionId = Guid.NewGuid(),
            ConnectionInfo = new()
            {
                Host = profile.Host,
                Port = profile.Port,
                Username = profile.Username,
                AuthMethod = profile.AuthMethod,
                Password = profile.Password
            },
            Status = SessionStatus.Connected
        };

    [TestMethod]
    public async Task MissingPassword_PromptsAuthenticator_BeforeConnecting()
    {
        IConnectionWorkflowService? workflow = Substitute.For<IConnectionWorkflowService>();
        ISshConnectionService? ssh = Substitute.For<ISshConnectionService>();
        ISshClientWrapper? client = Substitute.For<ISshClientWrapper>();
        IShellStreamWrapper? shell = Substitute.For<IShellStreamWrapper>();
        shell.CanRead.Returns(false);
        var profile = new SessionProfile { Name = "P", Host = "h", Port = 22, Username = "root", AuthMethod = AuthMethod.Password };
        workflow.ConnectProfileAsync(Arg.Any<SessionProfile>(), Arg.Any<CancellationToken>())
                .Returns(args => CreateSession((SessionProfile)args[0]));
        ssh.GetClient(Arg.Any<Guid>()).Returns(client);
        client.CreateShellStreamAsync("xterm-256color", 120, 32, 0, 0, 4096, Arg.Any<IReadOnlyDictionary<TerminalMode, uint>?>(), Arg.Any<CancellationToken>()).Returns(shell);
        var vm = new MainWindowViewModel(workflow, ssh, () => FakeTerminal.Emulator());
        int prompted = 0;
        vm.InteractiveAuthenticator = p =>
        {
            prompted++;
            p.Password = "prompted-pass";
            return Task.FromResult<SessionProfile?>(p);
        };
        TerminalTabViewModel? tab = await vm.TryConnectProfileAsync(profile);
        Assert.AreEqual(1, prompted);
        Assert.IsNotNull(tab);
        await workflow.Received(1).ConnectProfileAsync(Arg.Is<SessionProfile>(p => p.Password == "prompted-pass"), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// 存着的密码在这台设备上解不开(载入时仍是密文):直接弹登录框,不先拿密文去试一次 ——
    /// 白白一次失败认证,还可能撞上 fail2ban(第二轮第 20 条)。
    /// </summary>
    [TestMethod]
    public async Task UnreadableStoredPassword_PromptsBeforeAnyAttempt()
    {
        IConnectionWorkflowService? workflow = Substitute.For<IConnectionWorkflowService>();
        ISshConnectionService? ssh = Substitute.For<ISshConnectionService>();
        ISshClientWrapper? client = Substitute.For<ISshClientWrapper>();
        IShellStreamWrapper? shell = Substitute.For<IShellStreamWrapper>();
        shell.CanRead.Returns(false);
        workflow.ConnectProfileAsync(Arg.Any<SessionProfile>(), Arg.Any<CancellationToken>())
                .Returns(args => CreateSession((SessionProfile)args[0]));
        ssh.GetClient(Arg.Any<Guid>()).Returns(client);
        client.CreateShellStreamAsync("xterm-256color", 120, 32, 0, 0, 4096, Arg.Any<IReadOnlyDictionary<TerminalMode, uint>?>(), Arg.Any<CancellationToken>()).Returns(shell);
        var vm = new MainWindowViewModel(workflow, ssh, () => FakeTerminal.Emulator());
        int prompted = 0;
        vm.InteractiveAuthenticator = p =>
        {
            prompted++;
            SessionProfile typed = p.Clone();
            typed.Password = "typed-again";
            return Task.FromResult<SessionProfile?>(typed);
        };
        var profile = new SessionProfile
        {
            Name = "P",
            Host = "h",
            Port = 22,
            Username = "root",
            AuthMethod = AuthMethod.Password,
            Password = "enc1:AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"
        };

        TerminalTabViewModel? tab = await vm.TryConnectProfileAsync(profile);

        Assert.IsNotNull(tab);
        Assert.AreEqual(1, prompted, "解不开就先问,而不是先拿密文去试");
        await workflow.DidNotReceive().ConnectProfileAsync(
            Arg.Is<SessionProfile>(p => p.Password != null && p.Password.StartsWith("enc1:", StringComparison.Ordinal)),
            Arg.Any<CancellationToken>());
        await workflow.Received(1).ConnectProfileAsync(Arg.Is<SessionProfile>(p => p.Password == "typed-again"), Arg.Any<CancellationToken>());
    }

    [TestMethod]
    public async Task PromptCancelled_ReturnsNull_WithoutError()
    {
        IConnectionWorkflowService? workflow = Substitute.For<IConnectionWorkflowService>();
        ISshConnectionService? ssh = Substitute.For<ISshConnectionService>();
        var vm = new MainWindowViewModel(workflow, ssh, () => FakeTerminal.Emulator())
        {
            InteractiveAuthenticator = _ => Task.FromResult<SessionProfile?>(null)
        };
        var profile = new SessionProfile { Name = "P", Host = "h", Username = "root", AuthMethod = AuthMethod.Password };
        TerminalTabViewModel? tab = await vm.TryConnectProfileAsync(profile);
        Assert.IsNull(tab);
        Assert.IsNull(vm.LastConnectionError);
        await workflow.DidNotReceive().ConnectProfileAsync(Arg.Any<SessionProfile>(), Arg.Any<CancellationToken>());
    }

    [TestMethod]
    public async Task AuthFailure_RepromptsUpToThreeAttempts()
    {
        IConnectionWorkflowService? workflow = Substitute.For<IConnectionWorkflowService>();
        ISshConnectionService? ssh = Substitute.For<ISshConnectionService>();
        workflow.ConnectProfileAsync(Arg.Any<SessionProfile>(), Arg.Any<CancellationToken>())
                .Returns<Task<SshSession>>(_ => throw new VelaSshAuthenticationException("denied"));
        var vm = new MainWindowViewModel(workflow, ssh, () => FakeTerminal.Emulator());
        int prompted = 0;
        vm.InteractiveAuthenticator = p =>
        {
            prompted++;
            p.Password = $"try-{prompted}";
            return Task.FromResult<SessionProfile?>(p);
        };

        // 密码已填 → 第一次直接连接失败,随后重试两次均需弹窗。
        var profile = new SessionProfile { Name = "P", Host = "h", Username = "root", AuthMethod = AuthMethod.Password, Password = "wrong" };
        TerminalTabViewModel? tab = await vm.TryConnectProfileAsync(profile);

        // 重试用尽后保留失败标签,标签页内显示“连接失败”覆盖层(设计 yxjmg),不再销毁标签。
        Assert.IsNotNull(tab);
        Assert.HasCount(1, vm.TerminalTabs);
        Assert.AreEqual(SessionStatus.Disconnected, tab.ConnectionStatus);
        Assert.IsTrue(tab.ShowDisconnectedOverlay);
        Assert.IsTrue(tab.HasConnectionError);
        Assert.AreEqual(2, prompted);
        await workflow.Received(3).ConnectProfileAsync(Arg.Any<SessionProfile>(), Arg.Any<CancellationToken>());

        // 比对本地化资源而非中文字面量:该文案随 UI 语言变化,写死会让测试只在中文环境通过。
        // 提示后面还会换行附上底层库给出的具体原因(DescribeConnectionError 有意为之,便于用户诊断),
        // 所以断言的是「本地化文案 + \n + 原因」,不是裸文案。
        // 原先中间还有一句「本版不会两步验证」(F-11);动态码框接上之后那句不再成立,已撤掉。
        Assert.AreEqual(
            $"{Strings.Format("Msg_AuthFailed", "root@h:22")}\ndenied",
            vm.LastConnectionError);
    }

    [TestMethod]
    [DataRow("SshErr_TailscaleRejected", true)]
    [DataRow("SshErr_TailscaleCheckTimeout", true)]
    [DataRow("SshErr_TailscaleCheckRequired", true)]
    [DataRow("SshErr_TailscaleRejected", false)]
    public async Task TailscaleJumpFailure_PreservesFailedPasswordTargetWithoutCredentialRetry(string resource, bool hasAuthenticator)
    {
        await HeadlessUnitTestSession.GetOrStartForAssembly(typeof(InteractiveAuthFlowTests).Assembly).RunOnUiAsync(async () =>
        {
            IConnectionWorkflowService workflow = Substitute.For<IConnectionWorkflowService>();
            ISshConnectionService ssh = Substitute.For<ISshConnectionService>();
            var profile = new SessionProfile
            {
                Name = "target",
                Host = "target",
                Username = "root",
                Password = "target-password",
                AuthMethod = AuthMethod.Password,
                JumpHostProfileId = Guid.NewGuid()
            };
            string reason = $"ops@jump:22: {Strings.Get(resource)}";
            workflow.ConnectProfileAsync(profile, Arg.Any<CancellationToken>())
                .Returns(Task.FromException<SshSession>(new VelaSshTailscaleAuthenticationException(reason)));
            var vm = new MainWindowViewModel(workflow, ssh, () => FakeTerminal.Emulator());
            int prompts = 0;
            if (hasAuthenticator)
            {
                vm.InteractiveAuthenticator = candidate =>
                {
                    prompts++;
                    return Task.FromResult<SessionProfile?>(candidate);
                };
            }

            TerminalTabViewModel? tab = await vm.TryConnectProfileAsync(profile);

            Assert.IsNotNull(tab);
            Assert.HasCount(1, vm.TerminalTabs);
            Assert.AreEqual(SessionStatus.Disconnected, tab.ConnectionStatus);
            Assert.IsTrue(tab.AuthenticationRequiresUserAction);
            Assert.IsFalse(tab.UserRequestedDisconnect);
            Assert.IsFalse(tab.HostKeyRejected);
            Assert.IsTrue(tab.ShowDisconnectedOverlay);
            Assert.AreEqual(reason, tab.ConnectionError);
            Assert.AreEqual(reason, vm.LastConnectionError);
            Assert.AreEqual(0, prompts);
            await workflow.Received(1).ConnectProfileAsync(profile, Arg.Any<CancellationToken>());
            await tab.DisposeAsync();
        });
    }

    [TestMethod]
    public async Task TailscaleCheckCancelled_RemovesInitialTabWithoutPasswordRetryOrError()
    {
        await HeadlessUnitTestSession.GetOrStartForAssembly(typeof(InteractiveAuthFlowTests).Assembly).RunOnUiAsync(async () =>
        {
            IConnectionWorkflowService workflow = Substitute.For<IConnectionWorkflowService>();
            var profile = new SessionProfile { Host = "tail", Username = "ops", AuthMethod = AuthMethod.Tailscale };
            workflow.ConnectProfileAsync(profile, Arg.Any<CancellationToken>())
                .Returns(Task.FromException<SshSession>(new VelaSshAuthenticationCancelledException("cancelled")));
            var vm = new MainWindowViewModel(workflow, Substitute.For<ISshConnectionService>(), () => FakeTerminal.Emulator());
            int prompts = 0;
            vm.InteractiveAuthenticator = candidate =>
            {
                prompts++;
                return Task.FromResult<SessionProfile?>(candidate);
            };

            Assert.IsNull(await vm.TryConnectProfileAsync(profile));
            Assert.IsEmpty(vm.TerminalTabs);
            Assert.IsEmpty(vm.Layout.AllDocuments());
            Assert.IsNull(vm.LastConnectionError);
            Assert.AreEqual(0, prompts);
            await workflow.Received(1).ConnectProfileAsync(profile, Arg.Any<CancellationToken>());
        });
    }

    [TestMethod]
    public async Task TailscaleMissingUsername_PromptsOnlyOnceAndConnectsWithoutPassword()
    {
        await HeadlessUnitTestSession.GetOrStartForAssembly(typeof(InteractiveAuthFlowTests).Assembly).RunOnUiAsync(async () =>
        {
            IConnectionWorkflowService workflow = Substitute.For<IConnectionWorkflowService>();
            ISshConnectionService ssh = Substitute.For<ISshConnectionService>();
            ISshClientWrapper client = Substitute.For<ISshClientWrapper>();
            IShellStreamWrapper shell = Substitute.For<IShellStreamWrapper>();
            shell.CanRead.Returns(false);
            ssh.GetClient(Arg.Any<Guid>()).Returns(client);
            client.CreateShellStreamAsync("xterm-256color", 120, 32, 0, 0, 4096,
                Arg.Any<IReadOnlyDictionary<TerminalMode, uint>?>(), Arg.Any<CancellationToken>()).Returns(shell);
            workflow.ConnectProfileAsync(Arg.Any<SessionProfile>(), Arg.Any<CancellationToken>())
                .Returns(args => CreateSession((SessionProfile)args[0]));
            var profile = new SessionProfile { Host = "tail", AuthMethod = AuthMethod.Tailscale };
            var vm = new MainWindowViewModel(workflow, ssh, () => FakeTerminal.Emulator());
            int prompts = 0;
            vm.InteractiveAuthenticator = candidate =>
            {
                prompts++;
                Assert.AreEqual(AuthMethod.Tailscale, candidate.AuthMethod);
                candidate.Username = "ops";
                return Task.FromResult<SessionProfile?>(candidate);
            };

            TerminalTabViewModel? tab = await vm.TryConnectProfileAsync(profile);
            Assert.IsNotNull(tab);
            Assert.AreEqual(1, prompts);
            Assert.IsFalse(tab.AuthenticationRequiresUserAction);
            await workflow.Received(1).ConnectProfileAsync(
                Arg.Is<SessionProfile>(candidate => candidate.AuthMethod == AuthMethod.Tailscale
                    && candidate.Username == "ops" && candidate.Password == null && candidate.PrivateKeyPath == null),
                Arg.Any<CancellationToken>());
            await tab.DisposeAsync();
        });
    }
}
