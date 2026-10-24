using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using ReactiveUI.Primitives;
using VelaShell.ViewModels;
using FireAndForget = VelaShell.Services.FireAndForget;

namespace VelaShell.Views;

/// <summary>文件面板的远端搜索窗口(非模态,一个面板一扇)。</summary>
/// <remarks>
/// 关窗即停:还在跑的 <c>find</c> 没人看了,继续占着远端的 CPU 与磁盘没有意义。
/// 面板被拆掉(标签关闭、会话断开)时由打开它的视图负责关窗(见 <c>FileBrowserView</c>)。
/// </remarks>
public partial class RemoteSearchWindow : Window
{
    /// <summary>初始化窗口。</summary>
    public RemoteSearchWindow()
    {
        InitializeComponent();
        WindowChrome.Apply(this, WindowChromeKind.Tool);
        Opened += (_, _) => NameBox.Focus();
        Closed += (_, _) => (DataContext as RemoteSearchViewModel)?.Dispose();
    }

    private void OnCriteriaKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && DataContext is RemoteSearchViewModel { IsSearching: false } vm)
        {
            vm.SearchCommand.Execute().Subscribe(_ => { }, _ => { });
            e.Handled = true;
        }
    }

    private void OnResultDoubleTapped(object? sender, TappedEventArgs e) => RevealSelected();

    private void OnResultKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            RevealSelected();
            e.Handled = true;
        }
    }

    private void OnRevealClick(object? sender, RoutedEventArgs e) => FireAndForget.Run(async () =>
    {
        if (DataContext is RemoteSearchViewModel vm && sender is Control { DataContext: RemoteSearchResultViewModel result })
        {
            await vm.RevealAsync(result);
        }
    });

    private void RevealSelected() => FireAndForget.Run(async () =>
    {
        if (DataContext is RemoteSearchViewModel vm && ResultList.SelectedItem is RemoteSearchResultViewModel result)
        {
            await vm.RevealAsync(result);
        }
    });

    private void OnCloseClick(object? sender, RoutedEventArgs e) => this.PostClose();

    private void OnTitleBarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            this.BeginWindowMoveDrag(e);
        }
    }

    /// <inheritdoc />
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            // 先停,再关:Esc 在搜着的时候首先是「停下」。
            if (DataContext is RemoteSearchViewModel { IsSearching: true } vm)
            {
                vm.Stop();
            }
            else
            {
                this.PostClose();
            }
            e.Handled = true;
            return;
        }
        base.OnKeyDown(e);
    }
}
