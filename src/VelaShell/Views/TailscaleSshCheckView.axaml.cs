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
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (DataContext is TailscaleSshCheckViewModel { IsCompleted: false })
        {
            e.Cancel = true;
            RequestCancel();
        }
        base.OnClosing(e);
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
