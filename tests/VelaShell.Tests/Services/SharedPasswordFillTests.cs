using System.Text;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using NSubstitute;
using VelaShell.Core.Credentials;
using VelaShell.Core.Data;
using VelaShell.Core.Models;
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
        Assert.IsFalse(shell.ReceivedCalls().Any(c => c.GetMethodInfo().Name == nameof(IShellStreamWrapper.WriteAsync)
            && ((byte[])c.GetArguments()[0]!).AsSpan().IndexOf("do-not-leak"u8) >= 0));
    });

    [TestMethod]
    public Task Picker_ListsOnlyMetadataAndRequiresExplicitConfirmation() => Session.RunOnUiAsync(async () =>
    {
        using var control = new VelaTerminalControl();
        IShellStreamWrapper shell = Shell();
        await using var tab = new TerminalTabViewModel(control, shell) { ConnectionStatus = SessionStatus.Connected };
        var repository = Substitute.For<ISharedCredentialRepository>();
        var metadata = new SharedCredentialMetadata(Guid.NewGuid(), "production", "admin", AuthMethod.Password, true);
        repository.ListMetadataAsync().Returns(new List<SharedCredentialMetadata>
        {
            metadata,
            new(Guid.NewGuid(), "key", "root", AuthMethod.PrivateKey, true),
            new(Guid.NewGuid(), "missing", "", AuthMethod.Password, false),
        });
        repository.GetPasswordAsync(metadata.Id).Returns("secret");
        using var request = new SharedPasswordFillRequest(tab, () => true);
        var owner = new Window();
        owner.Show();
        var dialog = new SharedPasswordPickerDialog(repository, request, "admin@host");
        try
        {
            Task<bool> result = dialog.ShowDialog<bool>(owner);
            Dispatcher.UIThread.RunJobs();
            var vm = (SharedPasswordPickerViewModel)dialog.DataContext!;
            Assert.HasCount(1, vm.Items);
            Assert.IsTrue(dialog.FindControl<TextBox>("SearchInput")!.IsFocused);
            vm.SearchQuery = "ADMIN";
            Assert.HasCount(1, vm.Items);
            vm.SelectedItem = metadata;
            dialog.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
            Dispatcher.UIThread.RunJobs();
            await repository.DidNotReceive().GetPasswordAsync(Arg.Any<Guid>());
            dialog.FindControl<Button>("FillButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            Assert.IsTrue(await result);
            await repository.Received(1).GetPasswordAsync(metadata.Id);
        }
        finally
        {
            dialog.Close();
            owner.Close();
        }
    });

    [TestMethod]
    public Task AutomaticHint_DoesNotTakeFocus_AndDetachesCleanly() => Session.RunOnUiAsync(async () =>
    {
        using var control = new VelaTerminalControl();
        await using var tab = new TerminalTabViewModel(control, Shell()) { ConnectionStatus = SessionStatus.Connected };
        var main = new MainWindowViewModel();
        typeof(MainWindowViewModel).GetProperty(nameof(MainWindowViewModel.ActiveTerminalTab))!.SetValue(main, tab);
        var view = new TerminalTabView { DataContext = tab };
        var window = new Window { Width = 800, Height = 480, DataContext = main, Content = view };
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            using var frame = window.CaptureRenderedFrame();
            control.Focus();
            control.Feed("Enter passphrase for key 'x':"u8.ToArray());
            Assert.IsFalse(view.FindControl<Border>("PasswordHint")!.IsVisible);
            control.Feed("\r\e[2KPassword:"u8.ToArray());
            Assert.IsTrue(view.FindControl<Border>("PasswordHint")!.IsVisible);
            Assert.IsTrue(control.IsFocused);
            control.WriteTextInput("x");
            Assert.IsFalse(view.FindControl<Border>("PasswordHint")!.IsVisible);
            control.Feed("\r\e[2KPassword:"u8.ToArray());
            Assert.IsFalse(view.FindControl<Border>("PasswordHint")!.IsVisible, "同一提示已开始手输时不能再次冒出入口。");
            control.Feed("\e[?1049hPassword:"u8.ToArray());
            Assert.IsFalse(view.FindControl<Border>("PasswordHint")!.IsVisible);
            window.Content = null;
            control.Feed("\e[?1049l\r\e[2KPassword:"u8.ToArray());
            Assert.IsFalse(view.FindControl<Border>("PasswordHint")!.IsVisible);
        }
        finally
        {
            window.Close();
        }
    });

    [TestMethod]
    public Task CancelWhileReading_DoesNotSendLateResult() => Session.RunOnUiAsync(async () =>
    {
        using var control = new VelaTerminalControl();
        IShellStreamWrapper shell = Shell();
        await using var tab = new TerminalTabViewModel(control, shell) { ConnectionStatus = SessionStatus.Connected };
        var repository = Substitute.For<ISharedCredentialRepository>();
        var metadata = new SharedCredentialMetadata(Guid.NewGuid(), "prod", "admin", AuthMethod.Password, true);
        repository.ListMetadataAsync().Returns(new List<SharedCredentialMetadata> { metadata });
        var pending = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        repository.GetPasswordAsync(metadata.Id).Returns(pending.Task);
        using var request = new SharedPasswordFillRequest(tab, () => true);
        var owner = new Window();
        owner.Show();
        var dialog = new SharedPasswordPickerDialog(repository, request, "host");
        try
        {
            Task<bool> result = dialog.ShowDialog<bool>(owner);
            Dispatcher.UIThread.RunJobs();
            ((SharedPasswordPickerViewModel)dialog.DataContext!).SelectedItem = metadata;
            dialog.FindControl<Button>("FillButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.IsFalse(dialog.FindControl<TextBox>("SearchInput")!.IsEnabled);
            Assert.IsFalse(dialog.FindControl<ListBox>("CredentialList")!.IsEnabled);
            dialog.FindControl<Button>("CancelButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            pending.SetResult("late-secret");
            Dispatcher.UIThread.RunJobs();
            Assert.IsFalse(await result);
            await Task.Yield();
            await DrainWritesAsync(tab);
            await shell.DidNotReceive().WriteAsync(Arg.Any<byte[]>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
        }
        finally
        {
            dialog.Close();
            owner.Close();
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
}
