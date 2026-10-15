using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using ReactiveUI.Primitives;
using VelaShell.Core.Resources;
using VelaShell.Core.Ssh;
using VelaShell.Services;
using VelaShell.Tests.TestSupport;
using VelaShell.ViewModels;
using VelaShell.Views;

namespace VelaShell.Tests.Views;

[TestClass]
[TestCategory("Ssh")]
public class TailscaleSshCheckViewTests
{
    private static HeadlessUnitTestSession _session = null!;
    private static readonly Uri U1 = new("https://login.tailscale.com/a/VELA_UI_SECRET_ONE?next=one");
    private static readonly Uri U2 = new("https://login.tailscale.com/a/VELA_UI_SECRET_TWO?next=two");

    [ClassInitialize]
    public static void Init(TestContext _) =>
        _session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(TailscaleSshCheckViewTests).Assembly);

    [TestMethod]
    public async Task DisplayAndUpdateReturnWithoutClosing_AndEndWaitsForClosed() =>
        await _session.RunOnUiAsync(async () =>
        {
            var owner = new Window();
            owner.Show();
            using var lifetime = new CancellationTokenSource();
            try
            {
                var service = new TailscaleSshCheckDialogService(() => owner);
                Guid id = Guid.NewGuid();
                int cancelled = 0;
                TailscaleSshCheckNotification start = Start(id, U1, () => cancelled++, lifetime.Token);
                await service.HandleAsync(start).WaitAsync(TimeSpan.FromSeconds(5));
                var window = (TailscaleSshCheckView)owner.OwnedWindows.Single();
                var vm = (TailscaleSshCheckViewModel)window.DataContext!;
                Assert.IsTrue(window.IsVisible, "显示返回时窗口仍等待,不能等用户验证才完成回调。");
                Assert.AreEqual("user@node:22", vm.Target);
                Assert.AreEqual(U1, vm.VerificationUri);
                Assert.IsTrue(window.FindControl<TextBox>("VerificationLink")!.IsReadOnly);
                Assert.AreEqual(96d, window.FindControl<TextBox>("VerificationLink")!.MaxHeight);

                await service.HandleAsync(start);
                await service.HandleAsync(start with { VerificationUri = U2 });
                Assert.HasCount(1, owner.OwnedWindows);
                Assert.AreSame(window, owner.OwnedWindows.Single());
                Assert.AreEqual(U2, vm.VerificationUri);

                bool closed = false;
                window.Closed += (_, _) => closed = true;
                lifetime.Cancel();
                await service.HandleAsync(End(id)).WaitAsync(TimeSpan.FromSeconds(5));
                Assert.IsTrue(closed);
                Assert.IsTrue(vm.IsCompleted);
                Assert.IsNull(window.DataContext);
                Assert.IsEmpty(owner.OwnedWindows);
                Assert.AreEqual(0, cancelled, "服务端成功的 End 不能当成用户取消。");

                await service.HandleAsync(start);
                Assert.IsEmpty(owner.OwnedWindows, "携带已取消 UI token 的旧通知不能复活窗口。");
                vm.OpenBrowserCommand.Execute().Subscribe();
                vm.CopyLinkCommand.Execute().Subscribe();
                vm.CancelCommand.Execute().Subscribe();
                Assert.AreEqual(0, cancelled);
                AssertCommandsDisabled(vm);
            }
            finally
            {
                owner.Close();
                Dispatcher.UIThread.RunJobs();
            }
        });

    [TestMethod]
    public async Task SameTargetAttemptsRemainIndependent_WhenOneEndsAndOldActionsReplay() =>
        await _session.RunOnUiAsync(async () =>
        {
            var owner = new Window();
            owner.Show();
            using var aToken = new CancellationTokenSource();
            using var bToken = new CancellationTokenSource();
            try
            {
                var service = new TailscaleSshCheckDialogService(() => owner);
                Guid a = Guid.NewGuid(), b = Guid.NewGuid();
                int aCancelled = 0, bCancelled = 0;
                var aStart = Start(a, U1, () => aCancelled++, aToken.Token);
                await service.HandleAsync(aStart);
                var aWindow = (TailscaleSshCheckView)owner.OwnedWindows.Single();
                var aVm = (TailscaleSshCheckViewModel)aWindow.DataContext!;
                await service.HandleAsync(Start(b, U2, () => bCancelled++, bToken.Token));
                var bWindow = (TailscaleSshCheckView)owner.OwnedWindows.Single(w => w != aWindow);
                var bVm = (TailscaleSshCheckViewModel)bWindow.DataContext!;
                Assert.HasCount(2, owner.OwnedWindows);

                aVm.CancelCommand.Execute().Subscribe();
                aToken.Cancel();
                await service.HandleAsync(End(a));
                Assert.AreEqual(1, aCancelled);
                Assert.AreSame(bWindow, owner.OwnedWindows.Single());
                Assert.AreEqual(U2, bVm.VerificationUri);
                Assert.IsFalse(bVm.IsCompleted);

                aVm.CancelCommand.Execute().Subscribe();
                await service.HandleAsync(aStart with { VerificationUri = U2 });
                await service.HandleAsync(End(a));
                Assert.AreEqual(1, aCancelled);
                Assert.AreEqual(0, bCancelled);
                Assert.AreSame(bWindow, owner.OwnedWindows.Single());
                await service.HandleAsync(End(b));
                bToken.Cancel();
                Dispatcher.UIThread.RunJobs();
                Assert.IsEmpty(owner.OwnedWindows);
                Assert.AreEqual(0, bCancelled, "注销后的 token 不能误取消其它 attempt。");
            }
            finally
            {
                owner.Close();
                Dispatcher.UIThread.RunJobs();
            }
        });

    [TestMethod]
    public async Task CallerCancellationClosesWithoutInvokingUserCancel_AndCancelledStartIsIgnored() =>
        await _session.RunOnUiAsync(async () =>
        {
            var owner = new Window();
            owner.Show();
            using var lifetime = new CancellationTokenSource();
            try
            {
                int cancelled = 0;
                Guid id = Guid.NewGuid();
                var service = new TailscaleSshCheckDialogService(() => owner);
                var start = Start(id, U1, () => cancelled++, lifetime.Token);
                await service.HandleAsync(start);
                var vm = (TailscaleSshCheckViewModel)owner.OwnedWindows.Single().DataContext!;
                await Task.Run(lifetime.Cancel);
                Dispatcher.UIThread.RunJobs();
                await service.HandleAsync(End(id));
                Assert.IsEmpty(owner.OwnedWindows);
                Assert.IsTrue(vm.IsCompleted);
                Assert.AreEqual(0, cancelled);
                await service.HandleAsync(start);
                Assert.IsEmpty(owner.OwnedWindows);
            }
            finally
            {
                owner.Close();
                Dispatcher.UIThread.RunJobs();
            }
        });

    [TestMethod]
    [DataRow("button")]
    [DataRow("escape")]
    [DataRow("close")]
    public async Task UserCancellationClosesOnce_WithoutAffectingAnotherAttempt(string action) =>
        await _session.RunOnUiAsync(async () =>
        {
            var owner = new Window();
            owner.Show();
            using var lifetime = new CancellationTokenSource();
            try
            {
                var service = new TailscaleSshCheckDialogService(() => owner);
                Guid id = Guid.NewGuid();
                int cancelled = 0;
                await service.HandleAsync(Start(id, U1, () => cancelled++, lifetime.Token));
                var window = (TailscaleSshCheckView)owner.OwnedWindows.Single();
                var vm = (TailscaleSshCheckViewModel)window.DataContext!;
                Guid otherId = Guid.NewGuid();
                int otherCancelled = 0;
                await service.HandleAsync(Start(otherId, U2, () => otherCancelled++, CancellationToken.None));
                Window other = owner.OwnedWindows.Single(w => w != window);
                window.Activate();
                switch (action)
                {
                    case "button":
                        Click(window, "CancelButton");
                        break;
                    case "escape":
                        window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
                        // Headless 的 KeyPress 会泵送 PostClose；KeyUp 应交给仍存活的窗口。
                        owner.KeyRelease(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
                        break;
                    default:
                        window.Close();
                        break;
                }
                Dispatcher.UIThread.RunJobs();
                await service.HandleAsync(End(id));
                Assert.AreEqual(1, cancelled);
                Assert.IsTrue(vm.IsCompleted);
                Assert.IsNull(window.DataContext);
                Assert.AreSame(other, owner.OwnedWindows.Single());
                Assert.AreEqual(0, otherCancelled);
                vm.CancelCommand.Execute().Subscribe();
                Assert.AreEqual(1, cancelled);
                AssertCommandsDisabled(vm);
                await service.HandleAsync(End(otherId));
                Assert.IsEmpty(owner.OwnedWindows);
            }
            finally
            {
                owner.Close();
                Dispatcher.UIThread.RunJobs();
            }
        });

    [TestMethod]
    public async Task MissingOrFailingOwnerReportsOnlySafeAuthenticationFailure() =>
        await _session.RunOnUiAsync(async () =>
        {
            foreach (Func<Window?> owner in new Func<Window?>[]
                     { () => null, () => throw new InvalidOperationException(U1.AbsoluteUri) })
            {
                var service = new TailscaleSshCheckDialogService(owner);
                int cancelled = 0;
                try
                {
                    await service.HandleAsync(Start(Guid.NewGuid(), U1, () => cancelled++, CancellationToken.None));
                    Assert.Fail("无宿主时必须明确失败,不能静默等待。");
                }
                catch (VelaSshTailscaleAuthenticationException ex)
                {
                    Assert.AreEqual(Strings.Get("SshErr_TailscaleCheckUiUnavailable"), ex.Message);
                    Assert.IsNull(ex.InnerException);
                    Assert.DoesNotContain("VELA_UI_SECRET", ex.ToString());
                }
                Assert.AreEqual(0, cancelled);
            }
        });

    [TestMethod]
    public async Task UserActionsUseLatestLink_AndOperationFailuresStayInWindow() =>
        await _session.RunOnUiAsync(async () =>
        {
            var owner = new Window();
            owner.Show();
            var opened = new List<Uri>();
            var copied = new List<string>();
            int cancelled = 0;
            var vm = new TailscaleSshCheckViewModel("user@node:22", U1,
                uri => { opened.Add(uri); return Task.FromResult(false); },
                text => { copied.Add(text); return Task.FromException(new NotSupportedException(text)); },
                () => cancelled++);
            var window = new TailscaleSshCheckView { DataContext = vm };
            window.Show(owner);
            try
            {
                Assert.IsEmpty(opened);
                Assert.IsEmpty(copied);
                vm.UpdateVerificationUri(U2);
                Assert.IsEmpty(opened, "显示/更新链接绝不能自动打开浏览器。");
                await vm.OpenBrowserCommand.Execute().FirstAsync();
                Assert.AreSequenceEqual(new[] { U2 }, opened);
                Assert.AreEqual(Strings.Get("Ssh_TailscaleCheckOpenFailed"), vm.ErrorMessage);
                Assert.IsTrue(window.FindControl<TextBlock>("OperationError")!.IsVisible);
                Assert.IsTrue(window.IsVisible);
                Assert.IsFalse(vm.IsCompleted);
                await vm.CopyLinkCommand.Execute().FirstAsync();
                Assert.AreSequenceEqual(new[] { U2.AbsoluteUri }, copied);
                Assert.AreEqual(Strings.Get("Ssh_TailscaleCheckCopyFailed"), vm.ErrorMessage);
                Assert.DoesNotContain("VELA_UI_SECRET", vm.ErrorMessage);
                Assert.AreEqual(0, cancelled);
            }
            finally
            {
                vm.Complete();
                window.Close();
                owner.Close();
            }
        });

    [TestMethod]
    public async Task ChangedLinkOrCompletionDiscardsLateBrowserAndClipboardErrors() =>
        await _session.RunOnUiAsync(async () =>
        {
            var browser = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var clipboard = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var opened = new List<Uri>();
            var copied = new List<string>();
            int cancelled = 0;
            var vm = new TailscaleSshCheckViewModel("user@node:22", U1,
                uri => { opened.Add(uri); return browser.Task; },
                text => { copied.Add(text); return clipboard.Task; }, () => cancelled++);

            var firstOpen = vm.OpenBrowserCommand.Execute().FirstAsync();
            vm.UpdateVerificationUri(U2);
            browser.SetException(new InvalidOperationException(U1.AbsoluteUri));
            await firstOpen;
            Assert.AreEqual(string.Empty, vm.ErrorMessage, "U1 的迟到错误不能盖过 U2。");
            Assert.AreSequenceEqual(new[] { U1 }, opened);

            var copy = vm.CopyLinkCommand.Execute().FirstAsync();
            vm.UpdateVerificationUri(U1);
            clipboard.SetException(new InvalidOperationException(U2.AbsoluteUri));
            await copy;
            Assert.AreEqual(string.Empty, vm.ErrorMessage);
            Assert.AreSequenceEqual(new[] { U2.AbsoluteUri }, copied);

            browser = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var lastOpen = vm.OpenBrowserCommand.Execute().FirstAsync();
            vm.Complete();
            browser.SetResult(false);
            await lastOpen;
            Assert.AreEqual(string.Empty, vm.ErrorMessage, "完成后必须忽略所有迟到操作反馈。");
            vm.UpdateVerificationUri(U2);
            vm.OpenBrowserCommand.Execute().Subscribe();
            vm.CopyLinkCommand.Execute().Subscribe();
            vm.CancelCommand.Execute().Subscribe();
            Assert.HasCount(2, opened);
            Assert.HasCount(1, copied);
            Assert.AreEqual(0, cancelled);
            Assert.AreEqual(U1, vm.VerificationUri);
            AssertCommandsDisabled(vm);
        });

    [TestMethod]
    public async Task DisallowedLinksNeverReachPlatformActions() =>
        await _session.RunOnUiAsync(async () =>
        {
            int opened = 0, copied = 0;
            var vm = new TailscaleSshCheckViewModel("user@node:22", new Uri("http://login.tailscale.com/a/secret"),
                _ => { opened++; return Task.FromResult(true); },
                _ => { copied++; return Task.CompletedTask; }, () => { });
            await vm.OpenBrowserCommand.Execute().FirstAsync();
            Assert.AreEqual(Strings.Get("Ssh_TailscaleCheckOpenFailed"), vm.ErrorMessage);
            await vm.CopyLinkCommand.Execute().FirstAsync();
            Assert.AreEqual(Strings.Get("Ssh_TailscaleCheckCopyFailed"), vm.ErrorMessage);
            Assert.AreEqual(0, opened);
            Assert.AreEqual(0, copied);
            vm.Complete();
        });

    private static TailscaleSshCheckNotification Start(Guid id, Uri uri, Action cancel, CancellationToken token) =>
        new(id, "user@node:22", uri, cancel, token);

    private static TailscaleSshCheckNotification End(Guid id) =>
        new(id, "user@node:22", null, null, CancellationToken.None);

    private static void Click(Window window, string name) =>
        window.FindControl<Button>(name)!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private static void AssertCommandsDisabled(TailscaleSshCheckViewModel vm)
    {
        foreach (var command in new[] { vm.OpenBrowserCommand, vm.CopyLinkCommand, vm.CancelCommand })
        {
            bool canExecute = true;
            using IDisposable subscription = command.CanExecute.Subscribe(value => canExecute = value);
            Assert.IsFalse(canExecute);
        }
    }
}
