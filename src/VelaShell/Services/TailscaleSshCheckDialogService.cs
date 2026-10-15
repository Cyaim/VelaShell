using Avalonia.Controls;
using Avalonia.Threading;
using VelaShell.Core.Resources;
using VelaShell.Core.Ssh;
using VelaShell.ViewModels;
using VelaShell.Views;

namespace VelaShell.Services;

/// <summary>按实际认证跳管理非模态窗口;通知仅等待 UI 操作,不等待用户验证。</summary>
public sealed class TailscaleSshCheckDialogService(Func<Window?> ownerProvider)
{
    private readonly Dictionary<Guid, WaitingWindow> _windows = [];

    /// <summary>显示/更新立即返回;结束通知在窗口关闭、取消注册解除后返回。</summary>
    public async Task HandleAsync(TailscaleSshCheckNotification notification)
    {
        await Dispatcher.UIThread.InvokeAsync(() => HandleOnUiAsync(notification));
    }

    private async Task HandleOnUiAsync(TailscaleSshCheckNotification notification)
    {
        if (notification.VerificationUri is null)
        {
            if (_windows.TryGetValue(notification.AttemptId, out WaitingWindow? ending))
            {
                ending.Close();
                await ending.Closed.Task;
            }
            return;
        }
        if (notification.CancellationToken.IsCancellationRequested)
        {
            return;
        }
        if (_windows.TryGetValue(notification.AttemptId, out WaitingWindow? existing))
        {
            existing.ViewModel.UpdateVerificationUri(notification.VerificationUri);
            return;
        }

        WaitingWindow? waiting = null;
        try
        {
            if (!TailscaleSshCheckUri.IsAllowed(notification.VerificationUri)
                || notification.Cancel is not { } cancel
                || ownerProvider() is not { IsVisible: true } owner)
            {
                throw UiUnavailable();
            }
            var window = new TailscaleSshCheckView();
            var vm = new TailscaleSshCheckViewModel(notification.Target, notification.VerificationUri,
                window.OpenBrowserAsync, window.CopyLinkAsync, () =>
                {
                    try
                    {
                        cancel();
                    }
                    finally
                    {
                        window.PostClose();
                    }
                });
            waiting = new(window, vm);
            WaitingWindow entry = waiting;
            window.DataContext = vm;
            window.Closed += (_, _) =>
            {
                vm.Complete();
                entry.Registration.Unregister();
                if (_windows.TryGetValue(notification.AttemptId, out WaitingWindow? current)
                    && ReferenceEquals(current, entry))
                {
                    _windows.Remove(notification.AttemptId);
                }
                window.DataContext = null;
                entry.Closed.TrySetResult();
            };
            _windows.Add(notification.AttemptId, entry);
            window.Show(owner);
            // 回调只投递 UI 操作,不能等待 UI;UI 线程解除注册时也不等待回调。
            entry.Registration = notification.CancellationToken.Register(() =>
                Dispatcher.UIThread.Post(entry.Close));
            if (notification.CancellationToken.IsCancellationRequested)
            {
                entry.Close();
            }
        }
        catch (Exception)
        {
            if (waiting is not null)
            {
                waiting.ViewModel.Complete();
                waiting.Registration.Unregister();
                _windows.Remove(notification.AttemptId);
                waiting.Window.DataContext = null;
                try
                {
                    waiting.Window.Close();
                }
                catch (Exception)
                {
                    // 保留安全的创建失败原因,不传播平台异常。
                }
            }
            // 创建失败不是用户取消,且原平台异常可能含敏感链接。
            throw UiUnavailable();
        }
    }

    private static VelaSshTailscaleAuthenticationException UiUnavailable() =>
        new(Strings.Get("SshErr_TailscaleCheckUiUnavailable"));

    private sealed class WaitingWindow(TailscaleSshCheckView window, TailscaleSshCheckViewModel viewModel)
    {
        public TailscaleSshCheckView Window { get; } = window;
        public TailscaleSshCheckViewModel ViewModel { get; } = viewModel;
        public CancellationTokenRegistration Registration { get; set; }
        public TaskCompletionSource Closed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Close()
        {
            ViewModel.Complete();
            Registration.Unregister();
            if (!Closed.Task.IsCompleted)
            {
                Window.PostClose();
            }
        }
    }
}
