using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using NSubstitute;
using VelaShell.Core.Credentials;
using VelaShell.Core.Data;
using VelaShell.Core.Models;
using VelaShell.Core.Resources;
using VelaShell.Core.Ssh;
using VelaShell.Services;
using VelaShell.Terminal.Rendering;
using VelaShell.Tests.TestSupport;
using VelaShell.ViewModels;
using VelaShell.Views;

namespace VelaShell.Tests.Services;

[TestClass]
[TestCategory("SharedPasswordFill")]
public sealed class SharedPasswordFillTests
{
    private static HeadlessUnitTestSession Session => HeadlessUnitTestSession.GetOrStartForAssembly(typeof(SharedPasswordFillTests).Assembly);

    [TestMethod]
    [DataRow("Password:", true)]
    [DataRow("Enter password: ", true)]
    [DataRow("[sudo] password for joe:", true)]
    [DataRow("joe@host's password:", true)]
    [DataRow("密码：", true)]
    [DataRow("请输入密码：", true)]
    [DataRow("Verification code:", false)]
    [DataRow("OTP:", false)]
    [DataRow("Enter passphrase for key 'x':", false)]
    [DataRow("log: password:", false)]
    [DataRow("$ echo password:", false)]
    [DataRow("Password: typed", false)]
    public void PromptDetection_IsConservative(string line, bool expected) =>
        Assert.AreEqual(expected, InteractivePromptDetector.IsPasswordFillPrompt(line));

    [TestMethod]
    public Task Fill_UsesRealBridgeAndSessionEncoding_WithoutTypedInputOrEnter() => Session.RunOnUiAsync(async () =>
    {
        using var control = new VelaTerminalControl();
        IShellStreamWrapper shell = Shell();
        await using var tab = new TerminalTabViewModel(control, shell) { ConnectionStatus = SessionStatus.Connected };
        control.SetEncoding(Encoding.Unicode);
        control.Feed("Password:"u8.ToArray());
        var repository = Substitute.For<ISharedCredentialRepository>();
        Guid id = Guid.NewGuid();
        repository.GetPasswordAsync(id).Returns("口令!42");
        int typed = 0;
        control.TypedInput += _ => typed++;
        using var peerControl = new VelaTerminalControl();
        IShellStreamWrapper peerShell = Shell();
        await using var peer = new TerminalTabViewModel(peerControl, peerShell) { ConnectionStatus = SessionStatus.Connected };
        var sync = new SyncInputCoordinator();
        sync.Attach(tab);
        sync.Attach(peer);
        tab.JoinSyncChannel(SyncInputChannel.A);
        peer.JoinSyncChannel(SyncInputChannel.A);
        var writes = new List<byte>();
        shell.WriteAsync(Arg.Any<byte[]>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                writes.AddRange(call.ArgAt<byte[]>(0).AsSpan(call.ArgAt<int>(1), call.ArgAt<int>(2)).ToArray());
                return Task.CompletedTask;
            });
        using var request = new SharedPasswordFillRequest(tab, () => true);
        Assert.AreEqual(PasswordFillResult.Queued, await request.FillAsync(repository, id));
        await DrainWritesAsync(tab);
        CollectionAssert.AreEqual(Encoding.Unicode.GetBytes("口令!42"), writes.ToArray());
        Assert.AreEqual(0, typed);
        await DrainWritesAsync(peer);
        await peerShell.DidNotReceive().WriteAsync(Arg.Any<byte[]>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
        sync.Detach(tab);
        sync.Detach(peer);
        Assert.AreEqual("", tab.InputTracker.CurrentInput);
        Assert.AreEqual(PasswordFillResult.Expired, await request.FillAsync(repository, id));
        await repository.Received(1).GetPasswordAsync(id);
    });

    [TestMethod]
    [DataRow("bad\r")]
    [DataRow("bad\n")]
    [DataRow("bad\e")]
    [DataRow("bad\t")]
    [DataRow("中文")]
    public Task InvalidPassword_DoesNotSend(string password) => Session.RunOnUiAsync(async () =>
    {
        using var control = new VelaTerminalControl();
        control.SetEncoding(Encoding.ASCII);
        IShellStreamWrapper shell = Shell();
        await using var tab = new TerminalTabViewModel(control, shell) { ConnectionStatus = SessionStatus.Connected };
        var repository = Substitute.For<ISharedCredentialRepository>();
        repository.GetPasswordAsync(Arg.Any<Guid>()).Returns(password);
        using var request = new SharedPasswordFillRequest(tab, () => true);
        Assert.AreEqual(PasswordFillResult.InvalidPassword, await request.FillAsync(repository, Guid.NewGuid()));
        await DrainWritesAsync(tab);
        await shell.DidNotReceive().WriteAsync(Arg.Any<byte[]>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    });

    [TestMethod]
    [DataRow("input")]
    [DataRow("prompt")]
    [DataRow("screen")]
    [DataRow("disconnect")]
    [DataRow("cancel")]
    [DataRow("inactive")]
    [DataRow("encoding")]
    [DataRow("raw")]
    public Task ContextChangeDuringRead_RejectsLatePassword(string change) => Session.RunOnUiAsync(async () =>
    {
        using var control = new VelaTerminalControl();
        IShellStreamWrapper shell = Shell();
        await using var tab = new TerminalTabViewModel(control, shell) { ConnectionStatus = SessionStatus.Connected };
        var repository = Substitute.For<ISharedCredentialRepository>();
        var pending = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        repository.GetPasswordAsync(Arg.Any<Guid>()).Returns(pending.Task);
        bool active = true;
        using var request = new SharedPasswordFillRequest(tab, () => active);
        Task<PasswordFillResult> fill = request.FillAsync(repository, Guid.NewGuid());
        switch (change)
        {
            case "input": control.WriteTextInput("x"); break;
            case "raw": tab.Bridge!.SendRaw("other-session-input"u8.ToArray()); break;
            case "prompt": control.Feed("changed"u8.ToArray()); break;
            case "screen": control.Feed("\e[?1049h\e[?1049l"u8.ToArray()); break;
            case "disconnect":
                tab.ConnectionStatus = SessionStatus.Disconnected;
                tab.ConnectionStatus = SessionStatus.Connected;
                break;
            case "cancel": request.Invalidate(); break;
            case "inactive": active = false; break;
            case "encoding": control.SetEncoding(Encoding.Unicode); break;
        }
        pending.SetResult("do-not-leak");
        Assert.AreEqual(PasswordFillResult.Expired, await fill);
        await DrainWritesAsync(tab);
        Assert.DoesNotContain(c => c.GetMethodInfo().Name == nameof(IShellStreamWrapper.WriteAsync)
            && ((byte[])c.GetArguments()[0]!).AsSpan().IndexOf("do-not-leak"u8) >= 0, shell.ReceivedCalls());
    });

    [TestMethod]
    public Task Popup_OpensOnlyOnRealPrompt_ListsUsablePasswords_AndPickingOneFillsWithoutEnter() =>
        Session.RunOnUiAsync(async () =>
    {
        using var control = new VelaTerminalControl();
        IShellStreamWrapper shell = Shell();
        await using var tab = new TerminalTabViewModel(control, shell) { ConnectionStatus = SessionStatus.Connected };
        var metadata = new SharedCredentialMetadata(Guid.NewGuid(), "production", "admin", AuthMethod.Password, true);
        var repository = Substitute.For<ISharedCredentialRepository>();
        repository.ListMetadataAsync().Returns(new List<SharedCredentialMetadata>
        {
            metadata,
            new(Guid.NewGuid(), "key", "root", AuthMethod.PrivateKey, true),
            new(Guid.NewGuid(), "missing", "", AuthMethod.Password, false),
            new(Guid.NewGuid(), "旧库无口令", "ops", AuthMethod.Password, true),
        });
        repository.GetPasswordAsync(metadata.Id).Returns("secret");
        var writes = new List<byte>();
        shell.WriteAsync(Arg.Any<byte[]>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                writes.AddRange(call.ArgAt<byte[]>(0).AsSpan(call.ArgAt<int>(1), call.ArgAt<int>(2)).ToArray());
                return Task.CompletedTask;
            });
        (Window window, TerminalTabView view, IDisposable restore) = ShowTerminal(tab, repository);
        try
        {
            control.Focus();
            control.Feed("Enter passphrase for key 'x':"u8.ToArray());
            Paint(window);
            Assert.IsFalse(view.PasswordPopupOpenForTest, "passphrase 不是密码提示,不该弹列表。");
            control.Feed("\r\e[2KPassword:"u8.ToArray());
            Paint(window);
            Assert.IsTrue(view.PasswordPopupOpenForTest);
            // 展开列表只读元数据;滤掉非密码认证与没有口令的条目。
            await repository.Received(1).ListMetadataAsync();
            await repository.DidNotReceive().GetPasswordAsync(Arg.Any<Guid>());
            CollectionAssert.AreEqual(
                new[] { "production", "旧库无口令" },
                view.PasswordItemsForTest.Select(item => item.Name).ToArray());
            // 锚在提示符的下一行:列表不遮住用户可能正在手敲的那一行,也不漂到别处。
            Rect anchor = view.PasswordPlacementRectForTest;
            Rect cursor = control.GetCursorRect();
            Assert.AreEqual(cursor.X, anchor.X, 0.5, "锚点应与光标同列。");
            Assert.AreEqual(cursor.Bottom, anchor.Y, 0.5, "列表应紧贴光标行下沿。");
            // 点一行即读取并填入:只有密码正文,不带回车、不带引号 —— 远端还停在提示符上等输入。
            view.PickPasswordItem(metadata);
            await view.PasswordFillForTest;
            await DrainWritesAsync(tab);
            Assert.AreEqual("secret", Encoding.UTF8.GetString(writes.ToArray()));
            await repository.Received(1).GetPasswordAsync(metadata.Id);
            // 填完即收口:留着只会让人再点一次。
            Assert.IsFalse(view.PasswordPopupOpenForTest);
        }
        finally
        {
            restore.Dispose();
            window.Close();
        }
    });

    [TestMethod]
    public Task Popup_StaysClosedWhenContextIsWrong_AndReopensOnlyForANewPrompt() =>
        Session.RunOnUiAsync(async () =>
    {
        using var control = new VelaTerminalControl();
        await using var tab = new TerminalTabViewModel(control, Shell()) { ConnectionStatus = SessionStatus.Connected };
        var metadata = new SharedCredentialMetadata(Guid.NewGuid(), "prod", "admin", AuthMethod.Password, true);
        var repository = Substitute.For<ISharedCredentialRepository>();
        repository.ListMetadataAsync().Returns(new List<SharedCredentialMetadata> { metadata });
        (Window window, TerminalTabView view, IDisposable restore) = ShowTerminal(tab, repository);
        try
        {
            Dispatcher.UIThread.RunJobs();
            using var frame = window.CaptureRenderedFrame();
            // 终端没焦点:按键会去别处,不能弹出只走鼠标的列表。
            control.Feed("Password:"u8.ToArray());
            Paint(window);
            Assert.IsFalse(view.PasswordPopupOpenForTest);
            control.Focus();
            control.Feed("\r\e[2KPassword:"u8.ToArray());
            Paint(window);
            Assert.IsTrue(view.PasswordPopupOpenForTest);
            Assert.IsTrue(control.IsFocused, "列表不得把焦点从终端抢走。");
            // 用户开始手敲同一提示:收口,且不再反复重开。
            control.WriteTextInput("x");
            Paint(window);
            Assert.IsFalse(view.PasswordPopupOpenForTest);
            control.Feed("\r\e[2KPassword:"u8.ToArray());
            Paint(window);
            Assert.IsFalse(view.PasswordPopupOpenForTest, "同一提示已开始手输时不能再次冒出入口。");
            // 备用屏(vim 等)里没有 shell 提示符这回事。
            control.Feed("\e[?1049h\r\e[2KPassword:"u8.ToArray());
            Paint(window);
            Assert.IsFalse(view.PasswordPopupOpenForTest);
            // 回主屏后又是另一条提示(另一个主机/用户):这是新的一次询问,该给入口。
            control.Feed("\e[?1049l\r\e[2Kjoe@host's password:"u8.ToArray());
            Paint(window);
            Assert.IsTrue(view.PasswordPopupOpenForTest, "换了一条提示应重新给出入口。");
            // 回滚到历史里时弹层失去锚点意义,收口。
            control.Feed(Encoding.UTF8.GetBytes(string.Join("\r\n", Enumerable.Repeat("x", control.Rows * 2))));
            Paint(window);
            control.ScrollOffset = control.MaxScrollOffset;
            Paint(window);
            Assert.IsFalse(view.PasswordPopupOpenForTest);
            // 切走活动标签页:列表不能留在别人的终端上。
            control.ScrollOffset = 0;
            control.Feed("\r\e[2KPassword:"u8.ToArray());
            Paint(window);
            Assert.IsTrue(view.PasswordPopupOpenForTest);
            window.Content = null;
            Dispatcher.UIThread.RunJobs();
            Assert.IsFalse(view.PasswordPopupOpenForTest);
        }
        finally
        {
            restore.Dispose();
            window.Close();
        }
    });

    [TestMethod]
    public Task LatePassword_KeepsPopupOpenAndSaysNothingWasSent() => Session.RunOnUiAsync(async () =>
    {
        using var control = new VelaTerminalControl();
        IShellStreamWrapper shell = Shell();
        await using var tab = new TerminalTabViewModel(control, shell) { ConnectionStatus = SessionStatus.Connected };
        var metadata = new SharedCredentialMetadata(Guid.NewGuid(), "prod", "admin", AuthMethod.Password, true);
        var repository = Substitute.For<ISharedCredentialRepository>();
        repository.ListMetadataAsync().Returns(new List<SharedCredentialMetadata> { metadata });
        var pending = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        repository.GetPasswordAsync(metadata.Id).Returns(pending.Task);
        var writes = new List<byte>();
        shell.WriteAsync(Arg.Any<byte[]>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                writes.AddRange(call.ArgAt<byte[]>(0).AsSpan(call.ArgAt<int>(1), call.ArgAt<int>(2)).ToArray());
                return Task.CompletedTask;
            });
        (Window window, TerminalTabView view, IDisposable restore) = ShowTerminal(tab, repository);
        try
        {
            control.Focus();
            Dispatcher.UIThread.RunJobs();
            control.Feed("\r\e[2KPassword:"u8.ToArray());
            Paint(window);
            Assert.IsTrue(view.PasswordPopupOpenForTest, $"光标行=[{control.GetBufferLine(control.CursorRow)}]");
            view.PickPasswordItem(metadata);
            await Task.Yield();
            // 读取期间别处有输入注入(同步输入的另一会话)→ 请求作废。这里不能用键入来造:
            // 键入本身就会先收口弹层,到不了"失败"分支。迟到的密码不发送,但必须**说明**
            // 没发出去 —— 悄悄收口会让用户以为已经填上了。
            tab.Bridge!.SendRaw("other-session-input"u8.ToArray());
            pending.SetResult("late-secret");
            // 等这次读取与填入整个跑完再断言。迟到结果的续体排在 UI 调度器上,让一次路轮不到它;
            // 原先靠后面 DrainWritesAsync 恰好多跨一跳才赶上,写入排空得快就读到空状态(macOS CI 偶发)。
            await view.PasswordFillForTest;
            await DrainWritesAsync(tab);
            // 唯一的写入是上面那次注入本身,密码没有跟出去。
            Assert.AreEqual("other-session-input", Encoding.UTF8.GetString(writes.ToArray()));
            Assert.IsTrue(view.PasswordPopupOpenForTest, "失败要留着弹层,否则状态文字随弹层一起消失。");
            Assert.AreEqual(Strings.Get("PasswordFill_Expired"), view.PasswordStatusForTest);
        }
        finally
        {
            restore.Dispose();
            window.Close();
        }
    });

    [TestMethod]
    public Task Picker_CancelWhileReading_DoesNotSendLateResult() => Session.RunOnUiAsync(async () =>
    {
        using var control = new VelaTerminalControl();
        IShellStreamWrapper shell = Shell();
        await using var tab = new TerminalTabViewModel(control, shell) { ConnectionStatus = SessionStatus.Connected };
        var metadata = new SharedCredentialMetadata(Guid.NewGuid(), "prod", "admin", AuthMethod.Password, true);
        var repository = Substitute.For<ISharedCredentialRepository>();
        repository.ListMetadataAsync().Returns(new List<SharedCredentialMetadata> { metadata });
        var pending = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        repository.GetPasswordAsync(metadata.Id).Returns(pending.Task);
        (Window window, TerminalTabView view, IDisposable restore) = ShowTerminal(tab, repository);
        try
        {
            control.Focus();
            control.Feed("Password:"u8.ToArray());
            Paint(window);
            view.PickPasswordItem(metadata);
            await Task.Yield();
            // 读取在途:此刻用户按 Esc 收口(或切走窗口),迟到的密码不得再发。
            view.ClosePasswordPopupForTest();
            Assert.IsFalse(view.PasswordPopupOpenForTest);
            pending.SetResult("late-secret");
            // 必须等迟到结果真的被处理过,下面的「没发」才有分量;只让一次路,断言时它可能还没跑。
            await view.PasswordFillForTest;
            await DrainWritesAsync(tab);
            await shell.DidNotReceive().WriteAsync(Arg.Any<byte[]>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
        }
        finally
        {
            restore.Dispose();
            window.Close();
        }
    });

    [TestMethod]
    public Task ReconnectDuringRead_DoesNotSendToOldOrNewTransport() => Session.RunOnUiAsync(async () =>
    {
        using var control = new VelaTerminalControl();
        IShellStreamWrapper oldShell = Shell();
        await using var tab = new TerminalTabViewModel(control, oldShell) { ConnectionStatus = SessionStatus.Connected };
        var repository = Substitute.For<ISharedCredentialRepository>();
        var pending = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        repository.GetPasswordAsync(Arg.Any<Guid>()).Returns(pending.Task);
        using var request = new SharedPasswordFillRequest(tab, () => true);
        Task<PasswordFillResult> result = request.FillAsync(repository, Guid.NewGuid());
        IShellStreamWrapper newShell = Shell();
        await tab.AttachTransportAsync(newShell);
        pending.SetResult("late-secret");
        Assert.AreEqual(PasswordFillResult.Expired, await result);
        await DrainWritesAsync(tab);
        await oldShell.DidNotReceive().WriteAsync(Arg.Any<byte[]>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
        await newShell.DidNotReceive().WriteAsync(Arg.Any<byte[]>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    });

    [TestMethod]
    public Task DeferredFocus_DoesNotStealFocusAfterTabSwitchOrDialogOpen() => Session.RunOnUiAsync(async () =>
    {
        using var control = new VelaTerminalControl();
        await using var tab = new TerminalTabViewModel(control, Shell());
        using var otherControl = new VelaTerminalControl();
        await using var otherTab = new TerminalTabViewModel(otherControl, Shell());
        var main = new MainWindowViewModel();
        var active = typeof(MainWindowViewModel).GetProperty(nameof(MainWindowViewModel.ActiveTerminalTab))!;
        active.SetValue(main, tab);
        var view = new TerminalTabView { DataContext = tab };
        var input = new TextBox();
        var panel = new StackPanel();
        panel.Children.Add(input);
        panel.Children.Add(view);
        var window = new Window { Width = 800, Height = 480, Content = panel, DataContext = main };
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            input.Focus();
            view.FocusTerminal();
            active.SetValue(main, otherTab);
            Dispatcher.UIThread.RunJobs();
            Assert.IsTrue(input.IsFocused, "排队后切换标签不得抢回焦点。");
            active.SetValue(main, tab);
            view.FocusTerminal();
            var dialogInput = new TextBox();
            var dialog = new Window { Content = dialogInput };
            Task result = dialog.ShowDialog(window);
            dialogInput.Focus();
            Dispatcher.UIThread.RunJobs();
            Assert.IsTrue(dialogInput.IsFocused, "排队后打开模态窗口不得抢回焦点。");
            dialog.Close();
            await result;
        }
        finally
        {
            window.Close();
        }
    });

    private static Task DrainWritesAsync(TerminalTabViewModel tab) =>
        (Task)typeof(VelaShell.Terminal.SshTerminalBridge)
            .GetMethod("DrainWritesAsync", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(tab.Bridge, null)!;

    private static IShellStreamWrapper Shell()
    {
        var shell = Substitute.For<IShellStreamWrapper>();
        shell.CanRead.Returns(false);
        shell.CanWrite.Returns(true);
        shell.WriteAsync(Arg.Any<byte[]>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        return shell;
    }

    /// <summary>
    /// 把标签页挂进一个真实窗口,并把凭据仓储接到 <see cref="TerminalTabView.RepositoryFactory" /> 上。
    /// headless 宿主(<c>VelaHeadlessApp</c>)不是 <c>App</c>,没有 DI 容器,所以那条取数口必须替换。
    /// 返回值里的 <see cref="IDisposable" /> 负责还原。
    /// </summary>
    private static (Window Window, TerminalTabView View, IDisposable Restore) ShowTerminal(
        TerminalTabViewModel tab,
        ISharedCredentialRepository repository)
    {
        var main = new MainWindowViewModel();
        typeof(MainWindowViewModel).GetProperty(nameof(MainWindowViewModel.ActiveTerminalTab))!.SetValue(main, tab);
        var view = new TerminalTabView { DataContext = tab };
        var window = new Window { Width = 800, Height = 480, DataContext = main, Content = view };
        window.Show();
        Func<ISharedCredentialRepository?> original = TerminalTabView.RepositoryFactory;
        TerminalTabView.RepositoryFactory = () => repository;
        return (window, view, new Restore(original));
    }

    /// <summary>跑一遍待处理作业并重绘一帧,让 OutputUpdated 之后的界面状态落定。</summary>
    private static void Paint(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        using var frame = window.CaptureRenderedFrame();
    }

    private sealed class Restore(Func<ISharedCredentialRepository?> original) : IDisposable
    {
        public void Dispose() => TerminalTabView.RepositoryFactory = original;
    }
}
