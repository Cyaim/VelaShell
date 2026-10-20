using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using ReactiveUI.Primitives;
using VelaShell.ViewModels;

namespace VelaShell.Views;

/// <summary>本次认证跳的非模态等待窗;浏览器与剪贴板能力来自自身窗口。</summary>
public partial class TailscaleSshCheckView : Window
{
    /// <summary>初始化窗口,不启动任何浏览器操作。</summary>
    public TailscaleSshCheckView()
    {
        InitializeComponent();
        WindowChrome.Apply(this, WindowChromeKind.Tool);
        Closed += (_, _) =>
        {
            (DataContext as TailscaleSshCheckViewModel)?.Complete();
            DataContext = null;
        };
    }

    internal Task<bool> OpenBrowserAsync(Uri uri) => Launcher.LaunchUriAsync(uri);

    internal Task CopyLinkAsync(string text) => Clipboard is { } clipboard
        ? clipboard.SetTextAsync(text)
        : Task.FromException(new NotSupportedException());

    private void Cancel_Click(object? sender, RoutedEventArgs e) => RequestCancel();

    private void RequestCancel()
    {
        if (DataContext is TailscaleSshCheckViewModel vm && !vm.IsCompleted)
        {
            vm.CancelCommand.Execute().Subscribe();
        }
        this.PostClose();
    }

    /// <inheritdoc />
    /// <remarks>
    /// 只拦用户关本窗口(标题栏关闭键 / Alt+F4):改走取消。主窗口关闭时 Avalonia 先拿 OwnerWindowClosing 问各个子窗口,
    /// 这里一拦主窗口就关不掉、它自己的 Closing 也不会来 —— 曾经一律拦下,表现为点一次主窗口的关闭键只取消了验证,
    /// 还得再点一次。所以别的原因一律放行:主窗口真关了,本窗口随之关闭,取消在 <see cref="OnClosed" /> 里做;
    /// 主窗口自己拦下了关闭(最小化到托盘、关闭前确认)则什么都不动,验证照常等。
    /// </remarks>
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (e.CloseReason == WindowCloseReason.WindowClosing
            && DataContext is TailscaleSshCheckViewModel { IsCompleted: false })
        {
            e.Cancel = true;
            RequestCancel();
        }
        base.OnClosing(e);
    }

    /// <inheritdoc />
    protected override void OnClosed(EventArgs e)
    {
        // 窗口还在等就没了(随主窗口关闭、应用退出):与点取消同一句话,不留一条没人能操作的认证挂着。
        if (DataContext is TailscaleSshCheckViewModel { IsCompleted: false } vm)
        {
            vm.CancelCommand.Execute().Subscribe();
        }
        base.OnClosed(e);
    }

    /// <inheritdoc />
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            RequestCancel();
            e.Handled = true;
            return;
        }
        base.OnKeyDown(e);
    }

    private void Header_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            this.BeginWindowMoveDrag(e);
        }
    }
}
